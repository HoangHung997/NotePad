using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using H2AgentLab.OfficeProtocol;

namespace H2AgentLab.OfficeHost;

public sealed class OfficeHostServer
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _pipeName;
    private readonly IOfficeBackend _backend;
    private readonly bool _fixtureMode;

    public OfficeHostServer(string pipeName, IOfficeBackend backend, bool fixtureMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (pipeName.Length > 180 || pipeName.Any(char.IsControl))
            throw new ArgumentException("OfficeHost pipe name is invalid.", nameof(pipeName));
        _pipeName = pipeName;
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _fixtureMode = fixtureMode;
    }

    /// <summary>
    /// Intentionally synchronous: this method stays on Program.Main's STA thread so every COM
    /// discovery/read/mutation is executed on the same STA, not on a thread-pool continuation.
    /// </summary>
    public void Run(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.CurrentUserOnly);

            try
            {
                server.WaitForConnection();
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            HandleConnection(server, cancellationToken);
        }
    }

    private void HandleConnection(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 16 * 1024,
            leaveOpen: true);
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(false),
            bufferSize: 16 * 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        string? line;
        try
        {
            line = reader.ReadLine();
        }
        catch (DecoderFallbackException)
        {
            Write(writer, Error("", "invalid_json", "OfficeHost request is not valid UTF-8."));
            return;
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            Write(writer, Error("", "invalid_request", "OfficeHost request is empty."));
            return;
        }
        if (Encoding.UTF8.GetByteCount(line) > OfficeProtocolConstants.MaxMessageBytes)
        {
            Write(writer, Error("", "request_too_large", "OfficeHost request exceeds the protocol size limit."));
            return;
        }

        OfficeRpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<OfficeRpcRequest>(line, Json);
        }
        catch (JsonException)
        {
            Write(writer, Error("", "invalid_json", "OfficeHost request JSON is invalid."));
            return;
        }

        if (request is null
            || string.IsNullOrWhiteSpace(request.Id)
            || string.IsNullOrWhiteSpace(request.Method))
        {
            Write(writer, Error(request?.Id ?? "", "invalid_request", "OfficeHost request requires id and method."));
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        OfficeRpcResponse response;
        try
        {
            var result = Dispatch(request, cancellationToken);
            response = new OfficeRpcResponse(
                request.Id,
                true,
                JsonSerializer.SerializeToElement(result, result.GetType(), Json),
                null);
        }
        catch (OfficeHostFaultException ex)
        {
            response = Error(request.Id, ex.Code, ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult is unchecked((int)0x80010001) or unchecked((int)0x8001010A))
        {
            response = Error(request.Id, "application_busy", "Word/Excel đang mở hộp thoại hoặc đang bận. Hãy đóng thông báo trong ứng dụng rồi thử lại; không lặp lại thao tác ghi khi ứng dụng chưa sẵn sàng.");
        }
        catch (Exception ex)
        {
            response = Error(request.Id, "host_error", Sanitize(ex.Message));
        }

        Write(writer, response);
    }

    private object Dispatch(
        OfficeRpcRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return request.Method switch
        {
            "ping" => new OfficePingResult(
                Environment.ProcessId,
                OfficeProtocolConstants.Version,
                _fixtureMode,
                Thread.CurrentThread.GetApartmentState() == ApartmentState.STA),

            "excel.discover" => _backend.DiscoverExcel(),
            "excel.snapshot" => _backend.SnapshotExcel(Parameters<ExcelSnapshotRequest>(request).SessionId),
            "excel.patch" => PatchExcel(Parameters<ExcelPatchRequest>(request)),
            "excel.recalculate" => _backend.RecalculateExcel(Parameters<ExcelRecalculateRequest>(request)),
            "excel.saveCopy" => _backend.SaveExcelCopy(Parameters<OfficeSaveCopyRequest>(request)),

            "word.discover" => _backend.DiscoverWord(),
            "word.snapshot" => _backend.SnapshotWord(Parameters<WordSnapshotRequest>(request).SessionId),
            "word.patch" => _backend.PatchWord(Parameters<WordPatchRequest>(request)),
            "word.languageEvidence" => _backend.InspectWordLanguage(Parameters<WordLanguageEvidenceRequest>(request)),
            "word.saveCopy" => _backend.SaveWordCopy(Parameters<OfficeSaveCopyRequest>(request)),

            "fixture.delay" when _fixtureMode => FixtureDelay(request, cancellationToken),
            "fixture.crash" when _fixtureMode => CrashFixture(),
            _ => throw new OfficeHostFaultException("unknown_method", $"OfficeHost method '{request.Method}' is not supported.")
        };
    }

    private ExcelPatchResult PatchExcel(ExcelPatchRequest request)
    {
        OfficeHostSafety.RequirePermission(request.PermissionGranted);
        if (ExcelPatchLimits.ValidationError(request.Cells?.Count ?? -1) is { } problem)
            throw new OfficeHostFaultException(ExcelPatchLimits.ErrorCode, problem);
        return _backend.PatchExcel(request);
    }

    private static object FixtureDelay(
        OfficeRpcRequest request,
        CancellationToken cancellationToken)
    {
        var delay = request.Parameters.ValueKind == JsonValueKind.Object
            && request.Parameters.TryGetProperty("milliseconds", out var milliseconds)
            && milliseconds.TryGetInt32(out var value)
            ? value
            : 1_000;
        if (delay is < 0 or > 30_000)
            throw new OfficeHostFaultException("invalid_request", "Fixture delay must be 0..30000 ms.");

        var remaining = delay;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slice = Math.Min(remaining, 50);
            Thread.Sleep(slice);
            remaining -= slice;
        }
        return new { delayedMilliseconds = delay };
    }

    private static object CrashFixture()
    {
        Environment.Exit(42);
        throw new InvalidOperationException("unreachable");
    }

    private static T Parameters<T>(OfficeRpcRequest request)
        where T : class
    {
        try
        {
            return request.Parameters.Deserialize<T>(Json)
                ?? throw new OfficeHostFaultException("invalid_request", $"Missing parameters for '{request.Method}'.");
        }
        catch (JsonException ex)
        {
            throw new OfficeHostFaultException("invalid_request", "OfficeHost parameters are invalid: " + Sanitize(ex.Message));
        }
    }

    private static OfficeRpcResponse Error(string id, string code, string message)
        => new(id, false, null, new OfficeRpcError(code, Sanitize(message)));

    private static void Write(StreamWriter writer, OfficeRpcResponse response)
    {
        var json = JsonSerializer.Serialize(response, Json);
        if (Encoding.UTF8.GetByteCount(json) > OfficeProtocolConstants.MaxMessageBytes)
            json = JsonSerializer.Serialize(
                Error(response.Id, "response_too_large", "OfficeHost response exceeds the protocol size limit."),
                Json);
        writer.WriteLine(json);
    }

    private static string Sanitize(string value)
    {
        value ??= "";
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 1_000 ? value : value[..1_000];
    }
}

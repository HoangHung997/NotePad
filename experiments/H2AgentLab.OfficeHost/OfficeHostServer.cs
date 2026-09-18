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

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            await HandleConnectionAsync(server, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleConnectionAsync(Stream stream, CancellationToken cancellationToken)
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
            NewLine = "
"
        };

        string? line;
        try
        {
            line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DecoderFallbackException)
        {
            await WriteAsync(writer, Error("", "invalid_json", "OfficeHost request is not valid UTF-8."), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            await WriteAsync(writer, Error("", "invalid_request", "OfficeHost request is empty."), cancellationToken).ConfigureAwait(false);
            return;
        }
        if (Encoding.UTF8.GetByteCount(line) > OfficeProtocolConstants.MaxMessageBytes)
        {
            await WriteAsync(writer, Error("", "request_too_large", "OfficeHost request exceeds the protocol size limit."), cancellationToken).ConfigureAwait(false);
            return;
        }

        OfficeRpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<OfficeRpcRequest>(line, Json);
        }
        catch (JsonException)
        {
            await WriteAsync(writer, Error("", "invalid_json", "OfficeHost request JSON is invalid."), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request is null
            || string.IsNullOrWhiteSpace(request.Id)
            || string.IsNullOrWhiteSpace(request.Method))
        {
            await WriteAsync(writer, Error(request?.Id ?? "", "invalid_request", "OfficeHost request requires id and method."), cancellationToken).ConfigureAwait(false);
            return;
        }

        OfficeRpcResponse response;
        try
        {
            var result = await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
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
        catch (Exception ex)
        {
            response = Error(request.Id, "host_error", Sanitize(ex.Message));
        }

        await WriteAsync(writer, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> DispatchAsync(
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
            "excel.patch" => _backend.PatchExcel(Parameters<ExcelPatchRequest>(request)),
            "excel.recalculate" => _backend.RecalculateExcel(Parameters<ExcelRecalculateRequest>(request)),
            "excel.saveCopy" => _backend.SaveExcelCopy(Parameters<OfficeSaveCopyRequest>(request)),

            "word.discover" => _backend.DiscoverWord(),
            "word.snapshot" => _backend.SnapshotWord(Parameters<WordSnapshotRequest>(request).SessionId),
            "word.patch" => _backend.PatchWord(Parameters<WordPatchRequest>(request)),
            "word.saveCopy" => _backend.SaveWordCopy(Parameters<OfficeSaveCopyRequest>(request)),

            "fixture.delay" when _fixtureMode => await FixtureDelayAsync(request, cancellationToken).ConfigureAwait(false),
            "fixture.crash" when _fixtureMode => CrashFixture(),
            _ => throw new OfficeHostFaultException("unknown_method", $"OfficeHost method '{request.Method}' is not supported.")
        };
    }

    private static async Task<object> FixtureDelayAsync(
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
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
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

    private static async Task WriteAsync(
        StreamWriter writer,
        OfficeRpcResponse response,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(response, Json);
        if (Encoding.UTF8.GetByteCount(json) > OfficeProtocolConstants.MaxMessageBytes)
            json = JsonSerializer.Serialize(
                Error(response.Id, "response_too_large", "OfficeHost response exceeds the protocol size limit."),
                Json);
        await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static string Sanitize(string value)
    {
        value ??= "";
        value = value.Replace('', ' ').Replace('
', ' ').Trim();
        return value.Length <= 1_000 ? value : value[..1_000];
    }
}

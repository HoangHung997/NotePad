using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using H2AgentLab.OfficeProtocol;

namespace H2AgentLab.Office;

public sealed class OfficeHostClientException : IOException
{
    public OfficeHostClientException(string code, string message, bool noEffect = false) : base(message)
    {
        Code = code; NoEffect = noEffect;
    }

    public string Code { get; }
    public bool NoEffect { get; }
}

public sealed class OfficeHostClient : IOfficeSessionClient, IOfficeCaptureClient, IExcelRangeReadClient, IWordPagedReadClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _hostExecutable;
    private readonly bool _fixtureMode;
    private readonly TimeSpan _defaultTimeout;
    private Process? _process;
    private string? _pipeName;
    private bool _disposed;
    private int _starts;
    private readonly Guid _connectionIdentity = Guid.NewGuid();
    public string InstanceIdentity => "office-connection:" + _connectionIdentity.ToString("N") + ":" + _starts;

    public OfficeHostClient(
        string hostExecutable,
        bool fixtureMode = false,
        TimeSpan? defaultTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostExecutable);
        _hostExecutable = Path.GetFullPath(hostExecutable);
        if (!File.Exists(_hostExecutable))
            throw new FileNotFoundException("OfficeHost executable was not found.", _hostExecutable);
        _fixtureMode = fixtureMode;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(10);
        if (_defaultTimeout <= TimeSpan.Zero || _defaultTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(defaultTimeout));
    }

    public int StartCount => _starts;
    public int? ProcessId => _process is { HasExited: false } ? _process.Id : null;

    public Task<OfficeCaptureResult> CaptureAsync(OfficeCaptureRequest request, CancellationToken cancellationToken = default)
        => CallAsync<OfficeCaptureResult>("office.capture", request,
            TimeSpan.FromMilliseconds(OfficeDiscoveryLimits.CaptureDeadlineMilliseconds), cancellationToken);

    public Task<OfficePingResult> PingAsync(CancellationToken cancellationToken = default)
        => CallAsync<OfficePingResult>("ping", new { }, null, cancellationToken);

    public Task<ExcelDiscovery> DiscoverExcelAsync(CancellationToken cancellationToken = default)
        => CallAsync<ExcelDiscovery>("excel.discover", new { }, TimeSpan.FromMilliseconds(OfficeDiscoveryLimits.DiscoveryDeadlineMilliseconds), cancellationToken);

    public Task<ExcelLiveSnapshot> SnapshotExcelAsync(string sessionId, CancellationToken cancellationToken = default)
        => CallAsync<ExcelLiveSnapshot>(
            "excel.snapshot",
            new ExcelSnapshotRequest(sessionId),
            null,
            cancellationToken);

    public Task<ExcelRangeReadPage> ReadExcelRangeAsync(
        ExcelReadRangeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return CallAsync<ExcelRangeReadPage>("excel.readRange", request, null, cancellationToken);
    }

    public Task<ExcelPatchResult> PatchExcelAsync(ExcelPatchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (ExcelPatchLimits.ValidationError(request.Cells?.Count ?? -1) is { } problem)
            return Task.FromException<ExcelPatchResult>(new OfficeHostClientException(ExcelPatchLimits.ErrorCode, problem));
        return CallAsync<ExcelPatchResult>("excel.patch", request, null, cancellationToken);
    }

    public Task<ExcelLiveSnapshot> RecalculateExcelAsync(ExcelRecalculateRequest request, CancellationToken cancellationToken = default)
        => CallAsync<ExcelLiveSnapshot>("excel.recalculate", request, null, cancellationToken);

    public Task<OfficeSaveCopyResult> SaveExcelCopyAsync(OfficeSaveCopyRequest request, CancellationToken cancellationToken = default)
        => CallAsync<OfficeSaveCopyResult>("excel.saveCopy", request, null, cancellationToken);

    public Task<WordDiscovery> DiscoverWordAsync(CancellationToken cancellationToken = default)
        => CallAsync<WordDiscovery>("word.discover", new { }, TimeSpan.FromMilliseconds(OfficeDiscoveryLimits.DiscoveryDeadlineMilliseconds), cancellationToken);

    public Task<WordLiveSnapshot> SnapshotWordAsync(string sessionId, CancellationToken cancellationToken = default)
        => CallAsync<WordLiveSnapshot>(
            "word.snapshot",
            new WordSnapshotRequest(sessionId),
            null,
            cancellationToken);

    public Task<WordParagraphReadPage> ReadWordParagraphsAsync(WordParagraphReadRequest request, CancellationToken cancellationToken = default)
        => CallAsync<WordParagraphReadPage>("word.readParagraphs", request, null, cancellationToken);

    public Task<WordRangeReadPage> ReadWordRangeAsync(WordRangeReadRequest request, CancellationToken cancellationToken = default)
        => CallAsync<WordRangeReadPage>("word.readRange", request, null, cancellationToken);

    public Task<WordTableReadPage> ReadWordTablesAsync(WordTableReadRequest request, CancellationToken cancellationToken = default)
        => CallAsync<WordTableReadPage>("word.readTables", request, null, cancellationToken);

    public Task<WordPatchResult> PatchWordAsync(WordPatchRequest request, CancellationToken cancellationToken = default)
        => CallAsync<WordPatchResult>("word.patch", request, null, cancellationToken);

    public Task<WordLanguageEvidenceResult> InspectWordLanguageAsync(
        WordLanguageEvidenceRequest request,
        CancellationToken cancellationToken = default)
        => CallAsync<WordLanguageEvidenceResult>(
            "word.languageEvidence",
            request,
            null,
            cancellationToken);

    public Task<OfficeSaveCopyResult> SaveWordCopyAsync(OfficeSaveCopyRequest request, CancellationToken cancellationToken = default)
        => CallAsync<OfficeSaveCopyResult>("word.saveCopy", request, null, cancellationToken);

    public Task<JsonElement> FixtureDelayAsync(int milliseconds, TimeSpan timeout, CancellationToken cancellationToken = default)
        => CallAsync<JsonElement>("fixture.delay", new { milliseconds }, timeout, cancellationToken);

    public Task<JsonElement> FixtureCrashAsync(CancellationToken cancellationToken = default)
        => CallAsync<JsonElement>("fixture.crash", new { }, TimeSpan.FromSeconds(3), cancellationToken);

    public async Task<T> CallAsync<T>(
        string method,
        object parameters,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(parameters);

        cancellationToken.ThrowIfCancellationRequested();
        EnsureStarted();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? _defaultTimeout);

        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName!,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);

            using var writer = new StreamWriter(
                pipe,
                new UTF8Encoding(false),
                16 * 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            using var reader = new StreamReader(
                pipe,
                new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 16 * 1024,
                leaveOpen: true);

            var request = new OfficeRpcRequest(
                Guid.NewGuid().ToString("N"),
                method.Trim(),
                JsonSerializer.SerializeToElement(parameters, parameters.GetType(), Json));
            var requestJson = JsonSerializer.Serialize(request, Json);
            if (Encoding.UTF8.GetByteCount(requestJson) > OfficeProtocolConstants.MaxMessageBytes)
                throw new InvalidOperationException("OfficeHost request exceeds protocol size limit.");

            await writer.WriteLineAsync(requestJson.AsMemory(), deadline.Token).ConfigureAwait(false);
            var responseJson = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(responseJson))
                throw new IOException("OfficeHost closed the pipe without a response.");
            if (Encoding.UTF8.GetByteCount(responseJson) > OfficeProtocolConstants.MaxMessageBytes)
                throw new IOException("OfficeHost response exceeds protocol size limit.");

            var response = JsonSerializer.Deserialize<OfficeRpcResponse>(responseJson, Json)
                ?? throw new IOException("OfficeHost returned invalid response JSON.");
            if (response.Id != request.Id) throw new IOException("OfficeHost response belongs to another invocation.");
            if (!response.Ok)
                throw new OfficeHostClientException(
                    response.Error?.Code ?? "host_error",
                    response.Error?.Message ?? "OfficeHost request failed.", response.Error?.NoEffect == true);
            if (response.Result is not JsonElement result)
                throw new IOException("OfficeHost response is missing result.");
            return result.Deserialize<T>(Json)
                ?? throw new IOException("OfficeHost result cannot be deserialized.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            StopHost();
            throw new TimeoutException($"OfficeHost call '{method}' timed out.");
        }
        catch (OperationCanceledException)
        {
            StopHost();
            throw;
        }
        catch (OfficeHostClientException)
        {
            throw;
        }
        catch
        {
            if (_process is null || _process.HasExited)
                StopHost();
            throw;
        }
    }

    private void EnsureStarted()
    {
        if (_process is { HasExited: false } && !string.IsNullOrWhiteSpace(_pipeName))
            return;

        StopHost();
        _pipeName = "H2AgentLab.OfficeHost." + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(_hostExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_hostExecutable)!
        };
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(_pipeName);
        if (_fixtureMode)
            start.ArgumentList.Add("--fixture");

        _process = Process.Start(start)
            ?? throw new IOException("Failed to start H2AgentLab.OfficeHost.");
        _starts++;
    }

    private void StopHost()
    {
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2_000);
            }
            catch
            {
            }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }
        _pipeName = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopHost();
    }
}

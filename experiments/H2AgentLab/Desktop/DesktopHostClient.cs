using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using H2AgentLab.DesktopProtocol;

namespace H2AgentLab.Desktop;

public sealed class DesktopHostClientException : IOException
{
    public DesktopHostClientException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class DesktopHostClient : IDisposable
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
    private int? _validatedProtocolProcessId;

    public DesktopHostClient(
        string hostExecutable,
        bool fixtureMode = false,
        TimeSpan? defaultTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostExecutable);
        _hostExecutable = Path.GetFullPath(hostExecutable);
        if (!File.Exists(_hostExecutable))
            throw new FileNotFoundException("DesktopHost executable was not found.", _hostExecutable);
        _fixtureMode = fixtureMode;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(10);
        if (_defaultTimeout <= TimeSpan.Zero || _defaultTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(defaultTimeout));
    }

    public int StartCount => _starts;
    public int? ProcessId => _process is { HasExited: false } ? _process.Id : null;

    public Task<DesktopPingResult> PingAsync(CancellationToken cancellationToken = default)
        => CallAsync<DesktopPingResult>("ping", new { }, null, cancellationToken);

    public Task<IReadOnlyList<DesktopWindowInfo>> ListWindowsAsync(CancellationToken cancellationToken = default)
        => CallAsync<IReadOnlyList<DesktopWindowInfo>>("desktop.list", new { }, null, cancellationToken);

    public async Task<DesktopApplicationLaunchResult> LaunchApplicationAsync(
        DesktopApplicationLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureCurrentProtocolAsync(cancellationToken).ConfigureAwait(false);
        var validatedProcessId = RequireValidatedProcessId();
        return await CallAsync<DesktopApplicationLaunchResult>(
            "desktop.launch_app",
            request,
            ApplicationTimeout(request.WaitMilliseconds),
            cancellationToken,
            requiredProcessId: validatedProcessId).ConfigureAwait(false);
    }

    public async Task<DesktopWindowInfo> WaitForApplicationWindowAsync(
        DesktopApplicationWaitRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureCurrentProtocolAsync(cancellationToken).ConfigureAwait(false);
        var validatedProcessId = RequireValidatedProcessId();
        return await CallAsync<DesktopWindowInfo>(
            "desktop.wait_app",
            request,
            ApplicationTimeout(request.WaitMilliseconds),
            cancellationToken,
            requiredProcessId: validatedProcessId).ConfigureAwait(false);
    }

    public async Task<DesktopWindowInfo> ActivateWindowAsync(
        DesktopApplicationActivateRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureCurrentProtocolAsync(cancellationToken).ConfigureAwait(false);
        var validatedProcessId = RequireValidatedProcessId();
        return await CallAsync<DesktopWindowInfo>(
            "desktop.activate_window",
            request,
            null,
            cancellationToken,
            requiredProcessId: validatedProcessId).ConfigureAwait(false);
    }

    public Task<DesktopObservation> ObserveAsync(string sessionId, CancellationToken cancellationToken = default)
        => CallAsync<DesktopObservation>(
            "desktop.observe",
            new DesktopObserveRequest(sessionId),
            null,
            cancellationToken);

    public Task<DesktopActionResult> ActAsync(DesktopActionRequest request, CancellationToken cancellationToken = default)
        => CallAsync<DesktopActionResult>("desktop.act", request, null, cancellationToken);

    public async Task<T> CallAsync<T>(
        string method,
        object parameters,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        int? requiredProcessId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(parameters);

        if (requiredProcessId is { } expectedProcessId)
        {
            try
            {
                ValidateLiveProcessIdentity(expectedProcessId, ProcessId);
            }
            catch (DesktopHostClientException)
            {
                StopHost();
                throw;
            }
        }
        else
        {
            EnsureStarted();
        }

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
                16 * 1024,
                leaveOpen: true);

            var request = new DesktopRpcRequest(
                Guid.NewGuid().ToString("N"),
                method.Trim(),
                JsonSerializer.SerializeToElement(parameters, parameters.GetType(), Json));
            var requestJson = JsonSerializer.Serialize(request, Json);
            if (Encoding.UTF8.GetByteCount(requestJson) > DesktopProtocolConstants.MaxMessageBytes)
                throw new InvalidOperationException("DesktopHost request exceeds protocol size limit.");

            await writer.WriteLineAsync(requestJson.AsMemory(), deadline.Token).ConfigureAwait(false);
            var responseJson = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(responseJson))
                throw new IOException("DesktopHost closed the pipe without a response.");

            var response = JsonSerializer.Deserialize<DesktopRpcResponse>(responseJson, Json)
                ?? throw new IOException("DesktopHost returned invalid response JSON.");
            if (!response.Ok)
                throw new DesktopHostClientException(
                    response.Error?.Code ?? "host_error",
                    response.Error?.Message ?? "DesktopHost request failed.");
            if (response.Result is not JsonElement result)
                throw new IOException("DesktopHost response is missing result.");
            return result.Deserialize<T>(Json)
                ?? throw new IOException("DesktopHost result cannot be deserialized.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            StopHost();
            throw new TimeoutException($"DesktopHost call '{method}' timed out.");
        }
        catch (OperationCanceledException)
        {
            StopHost();
            throw;
        }
        catch (DesktopHostClientException)
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

    private async Task EnsureCurrentProtocolAsync(CancellationToken cancellationToken)
    {
        int expectedProcessId;
        DesktopPingResult ping;
        try
        {
            // Application lifecycle is the first path that requires the protocol handshake.
            // Start the helper before capturing its expected PID; otherwise a fresh client would
            // compare the successful ping against a null pre-start ProcessId and reject itself.
            EnsureStarted();
            expectedProcessId = ProcessId
                ?? throw new IOException("DesktopHost did not expose a live process after start.");
            if (_validatedProtocolProcessId == expectedProcessId)
                return;

            ping = await PingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            StopHost();
            throw new DesktopHostClientException(
                "preflight_unavailable",
                "DesktopHost preflight failed before any application lifecycle mutation.");
        }

        try
        {
            ValidatePreflightIdentity(expectedProcessId, ping);
        }
        catch (DesktopHostClientException)
        {
            StopHost();
            throw;
        }

        _validatedProtocolProcessId = ping.ProcessId;
    }

    internal static void ValidatePreflightIdentity(int? expectedProcessId, DesktopPingResult ping)
    {
        if (expectedProcessId is null
            || expectedProcessId <= 0
            || ping.ProcessId <= 0
            || ping.ProcessId != expectedProcessId.Value)
            throw new DesktopHostClientException(
                "preflight_unavailable",
                "DesktopHost preflight process identity does not match the helper started by H2 Notes.");

        if (!string.Equals(
                ping.ProtocolVersion,
                DesktopProtocolConstants.Version,
                StringComparison.Ordinal))
            throw new DesktopHostClientException(
                "protocol_mismatch",
                "DesktopHost protocol version does not match this H2 Notes build.");
    }

    private int RequireValidatedProcessId()
    {
        if (_validatedProtocolProcessId is not { } expectedProcessId)
            throw new DesktopHostClientException(
                "preflight_unavailable",
                "DesktopHost application lifecycle call has no validated helper identity.");
        ValidateLiveProcessIdentity(expectedProcessId, ProcessId);
        return expectedProcessId;
    }

    internal static void ValidateLiveProcessIdentity(int expectedProcessId, int? actualProcessId)
    {
        if (expectedProcessId <= 0
            || actualProcessId is null
            || actualProcessId <= 0
            || actualProcessId.Value != expectedProcessId)
            throw new DesktopHostClientException(
                "preflight_unavailable",
                "DesktopHost process changed after preflight; application lifecycle call was not sent.");
    }

    private static TimeSpan ApplicationTimeout(int waitMilliseconds)
        => TimeSpan.FromMilliseconds(
            Math.Clamp(
                waitMilliseconds,
                0,
                DesktopProtocolConstants.MaxApplicationWaitMilliseconds)
            + 5_000);

    private void EnsureStarted()
    {
        if (_process is { HasExited: false } && !string.IsNullOrWhiteSpace(_pipeName))
            return;

        StopHost();
        _pipeName = "H2AgentLab.DesktopHost." + Guid.NewGuid().ToString("N");
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
            ?? throw new IOException("Failed to start H2AgentLab.DesktopHost.");
        _starts++;
    }

    private void StopHost()
    {
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: false);
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
        _validatedProtocolProcessId = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopHost();
    }
}

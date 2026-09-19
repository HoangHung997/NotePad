using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace H2AgentLab.Providers;

public sealed record McpServerDefinition(
    string ProviderId,
    string ProviderVersion,
    string ServerId,
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout)
{
    public object SafeMetadata()
        => new
        {
            ProviderId,
            ProviderVersion,
            ServerId,
            Command = Path.GetFileName(Command),
            ArgumentCount = Arguments.Count,
            EnvironmentKeys = Environment.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            TimeoutMilliseconds = (int)Math.Clamp(Timeout.TotalMilliseconds, 1, int.MaxValue)
        };
}

public interface IMcpRpcTransport : IAsyncDisposable
{
    bool IsRunning { get; }
    Task StartAsync(McpServerDefinition definition, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task<JsonElement> CallAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken);
    Task NotifyAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken);
}

public sealed class McpServerConnection : IAsyncDisposable
{
    private readonly McpServerDefinition _definition;
    private readonly IMcpRpcTransport _transport;
    private readonly int _maxReconnectAttempts;
    private ProviderHealthState _health = new(
        ProviderHealthStatus.Disconnected,
        DateTime.UtcNow);

    public McpServerConnection(
        McpServerDefinition definition,
        IMcpRpcTransport transport,
        int maxReconnectAttempts = 2)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (maxReconnectAttempts is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(maxReconnectAttempts));
        _maxReconnectAttempts = maxReconnectAttempts;
    }

    public ProviderHealthState Health => _health;
    public McpServerDefinition Definition => _definition;
    public string? NegotiatedProtocolVersion { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_health.Status == ProviderHealthStatus.Ready && _transport.IsRunning)
            return;

        Transition(ProviderHealthStatus.Connecting);
        try
        {
            await _transport.StartAsync(_definition, cancellationToken).ConfigureAwait(false);
            var initialize = await _transport.CallAsync(
                "initialize",
                new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new
                    {
                        name = "H2AgentLab",
                        version = "2.0"
                    }
                },
                _definition.Timeout,
                cancellationToken).ConfigureAwait(false);

            if (!initialize.TryGetProperty("protocolVersion", out var protocol)
                || protocol.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(protocol.GetString()))
                throw new IOException("MCP initialize response did not include protocolVersion.");

            NegotiatedProtocolVersion = Bound(protocol.GetString()!, 64);
            await _transport.NotifyAsync(
                "notifications/initialized",
                new { },
                cancellationToken).ConfigureAwait(false);
            Transition(ProviderHealthStatus.Ready);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await StopQuietly().ConfigureAwait(false);
            Transition(ProviderHealthStatus.Failed, Bound(ex.Message, 800), _health.ConsecutiveFailures + 1);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _transport.StopAsync(cancellationToken).ConfigureAwait(false);
        NegotiatedProtocolVersion = null;
        Transition(ProviderHealthStatus.Disconnected);
    }

    public async Task<JsonElement> CallAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_health.Status != ProviderHealthStatus.Ready || !_transport.IsRunning)
                await ConnectAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return await _transport.CallAsync(
                    method.Trim(),
                    parameters,
                    _definition.Timeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var failures = _health.ConsecutiveFailures + 1;
                await StopQuietly().ConfigureAwait(false);
                if (attempt >= _maxReconnectAttempts)
                {
                    Transition(
                        ProviderHealthStatus.Failed,
                        Bound(ex.Message, 800),
                        failures);
                    throw;
                }

                attempt++;
                Transition(
                    ProviderHealthStatus.Degraded,
                    Bound(ex.Message, 800),
                    failures);
            }
        }
    }

    private async Task StopQuietly()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _transport.StopAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void Transition(
        ProviderHealthStatus status,
        string? boundedError = null,
        int? failures = null)
    {
        _health = new ProviderHealthState(
            status,
            DateTime.UtcNow,
            string.IsNullOrWhiteSpace(boundedError) ? null : Bound(boundedError, 800),
            failures ?? (status == ProviderHealthStatus.Ready ? 0 : _health.ConsecutiveFailures));
    }

    private static string Bound(string value, int max)
    {
        value ??= "";
        value = value.Replace((char)13, ' ').Replace((char)10, ' ').Trim();
        return value.Length <= max ? value : value[..max];
    }

    public async ValueTask DisposeAsync()
    {
        await StopQuietly().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        Transition(ProviderHealthStatus.Disconnected);
    }
}

/// <summary>
/// Minimal stdio JSON-RPC transport for MCP servers. Requests are serialized because this generic
/// implementation intentionally avoids background reader complexity; providers may replace it
/// with a richer transport without changing McpServerConnection or the orchestrator.
/// </summary>
public sealed class StdioMcpRpcTransport : IMcpRpcTransport
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private long _nextId;

    public bool IsRunning => _process is { HasExited: false };

    public Task StartAsync(
        McpServerDefinition definition,
        CancellationToken cancellationToken)
    {
        if (IsRunning) return Task.CompletedTask;

        var start = new ProcessStartInfo(definition.Command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in definition.Arguments)
            start.ArgumentList.Add(argument);
        foreach (var pair in definition.Environment)
            start.Environment[pair.Key] = pair.Value;

        _process = Process.Start(start)
            ?? throw new IOException("Failed to start MCP server process.");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                try { process.StandardInput.Close(); } catch { }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    public async Task<JsonElement> CallAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var process = _process is { HasExited: false } p
                ? p
                : throw new IOException("MCP server process is not running.");

            var id = Interlocked.Increment(ref _nextId);
            var payload = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters
            });

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);

            await process.StandardInput.WriteLineAsync(payload.AsMemory(), deadline.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);

            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false);
                if (line is null)
                {
                    var error = await ReadBoundedErrorAsync(process, deadline.Token).ConfigureAwait(false);
                    throw new IOException("MCP server closed stdout. " + error);
                }

                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("id", out var responseId)
                        || responseId.ValueKind != JsonValueKind.Number
                        || !responseId.TryGetInt64(out var responseNumber)
                        || responseNumber != id)
                        continue;

                    if (root.TryGetProperty("error", out var errorNode))
                    {
                        var message = errorNode.TryGetProperty("message", out var messageNode)
                            ? messageNode.GetString() ?? "MCP error"
                            : "MCP error";
                        throw new IOException(BoundError(message));
                    }

                    if (!root.TryGetProperty("result", out var result))
                        throw new IOException("MCP response is missing result.");
                    return result.Clone();
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"MCP call '{method}' timed out.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task NotifyAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        var process = _process is { HasExited: false } p
            ? p
            : throw new IOException("MCP server process is not running.");
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters
        });
        await process.StandardInput.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadBoundedErrorAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        try
        {
            var task = process.StandardError.ReadToEndAsync(cancellationToken);
            var text = await task.ConfigureAwait(false);
            return BoundError(text);
        }
        catch
        {
            return "";
        }
    }

    private static string BoundError(string value)
    {
        value ??= "";
        value = value.Replace((char)13, ' ').Replace((char)10, ' ').Trim();
        return value.Length <= 800 ? value : value[..800];
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Dispose();
    }
}

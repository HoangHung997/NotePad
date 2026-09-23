using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using H2AgentLab.Metrics;
using H2Notes.Core;

namespace H2AgentLab.Transport;

internal interface IResponsesWebSocketConnection : IAsyncDisposable
{
    bool IsOpen { get; }
    Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken);
    Task SendTextAsync(string text, CancellationToken cancellationToken);
    IAsyncEnumerable<string> ReceiveTextAsync(CancellationToken cancellationToken);
    Task CloseAsync(CancellationToken cancellationToken);
}

internal sealed class ClientResponsesWebSocketConnection : IResponsesWebSocketConnection
{
    private const int MaxMessageBytes = 3_000_000;
    private readonly ClientWebSocket _socket = new();
    private bool _disposed;

    public bool IsOpen => !_disposed && _socket.State == WebSocketState.Open;

    public async Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ClientResponsesWebSocketConnection));
        foreach (var pair in headers) _socket.Options.SetRequestHeader(pair.Key, pair.Value);
        await _socket.ConnectAsync(uri, cancellationToken);
    }

    public async Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        if (!IsOpen) throw new IOException("Responses WebSocket chưa mở.");
        var bytes = Encoding.UTF8.GetBytes(text);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    public async IAsyncEnumerable<string> ReceiveTextAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!IsOpen) throw new IOException("Responses WebSocket chưa mở.");
        var buffer = new byte[16 * 1024];
        while (IsOpen)
        {
            await using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) yield break;
                if (result.MessageType != WebSocketMessageType.Text)
                    throw new IOException("Responses WebSocket trả frame không phải text.");
                if (message.Length + result.Count > MaxMessageBytes)
                    throw new IOException("Responses WebSocket message vượt giới hạn an toàn 3 MB.");
                await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            }
            while (!result.EndOfMessage);

            yield return Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (_disposed) return;
        if (_socket.State == WebSocketState.Open)
        {
            try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "turn complete", cancellationToken); }
            catch (WebSocketException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "dispose", CancellationToken.None);
        }
        catch (WebSocketException) { }
        _socket.Dispose();
    }
}

/// <summary>
/// Turn-scoped Responses WebSocket transport for the official OpenAI endpoint. A single socket is
/// reused for Start + all tool continuations in the same user turn. The first request sends full
/// canonical input; continuations send only function_call_output items plus previous_response_id.
/// Public construction enables a best-effort generate=false prewarm. Prewarm has no model/tool side
/// effects; if it fails, the real request is still sent with full input. If the socket cannot connect
/// before a real request is written, the transport may safely fall back to HTTP/SSE. After a real
/// request write succeeds, disconnects are ambiguous and are never auto-replayed through HTTP.
/// </summary>
public sealed class OpenAiResponsesWebSocketTransport : IAgentTransport, IAgentRequestBudgetSource
{
    private readonly AgentRequestBudgetGuard _requestBudget;
    public AgentRequestBudgetReceipt? LastRequestBudget => (_fallback as IAgentRequestBudgetSource)?.LastRequestBudget ?? _requestBudget.LastReceipt;
    public event Action<AgentRequestBudgetReceipt>? RequestBudgetEvaluated
    {
        add { _requestBudget.Evaluated += value; _fallbackBudgetObserver += value; }
        remove { _requestBudget.Evaluated -= value; _fallbackBudgetObserver -= value; }
    }
    private readonly AiProfile _profile;
    private readonly string _apiKey;
    private readonly Func<IResponsesWebSocketConnection> _connectionFactory;
    private readonly Func<IAgentTransport>? _httpFallbackFactory;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, AgentToolDefinition> _tools = new(StringComparer.Ordinal);
    private readonly bool _enablePrewarm;
    private readonly AgentTrace? _trace;
    private IResponsesWebSocketConnection? _connection;
    private IAgentTransport? _fallback;
    private List<AgentTransportToolCall> _pendingCalls = [];
    private Guid _taskId;
    private Guid _turnId;
    private bool _started;
    private bool _disposed;
    private bool _allowParallelToolCalls;
    private string? _promptCacheKey;
    private string? _previousResponseId;
    private JsonArray _initialInput = [];

    public OpenAiResponsesWebSocketTransport(AiProfile profile, string apiKey, AgentTrace? trace = null)
        : this(profile, apiKey, () => new ClientResponsesWebSocketConnection(), null, enablePrewarm: true, trace)
    {
    }

    internal OpenAiResponsesWebSocketTransport(
        AiProfile profile,
        string apiKey,
        Func<IResponsesWebSocketConnection> connectionFactory,
        Func<IAgentTransport>? httpFallbackFactory,
        bool enablePrewarm = false,
        AgentTrace? trace = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        if (profile.Protocol != AiProtocol.OpenAiResponses)
            throw new ArgumentException("Responses WebSocket chỉ nhận hồ sơ OpenAiResponses.", nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.Model))
            throw new ArgumentException("Chưa chọn model OpenAI Responses.", nameof(profile));

        _profile = profile.Copy();
        _requestBudget = new AgentRequestBudgetGuard(_profile);
        _profile.Protocol = AiProtocol.OpenAiResponses;
        _apiKey = apiKey ?? "";
        _connectionFactory = connectionFactory;
        _httpFallbackFactory = httpFallbackFactory ?? (() => new OpenAiResponsesTransport(
            _profile, _apiKey, stateMode: OpenAiResponsesStateMode.Stateless));
        _enablePrewarm = enablePrewarm;
        _trace = trace;

        _ = WebSocketEndpoint(_profile); // fail before any data can be sent
    }

    public AgentTransportCapabilities Capabilities
        => _fallback?.Capabilities ?? AgentTransportCapabilities.OpenAiResponsesWebSocket;

    public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
        AgentTransportStartRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureUsable();
        if (_started) throw new InvalidOperationException("Responses WebSocket turn đã bắt đầu.");
        if (request.TaskId == Guid.Empty || request.TurnId == Guid.Empty)
            throw new ArgumentException("TaskId và TurnId phải là GUID hợp lệ.", nameof(request));
        if (request.AllowParallelToolCalls && !Capabilities.ParallelToolCalls)
            throw new InvalidOperationException("Transport không hỗ trợ parallel tool calls.");

        _taskId = request.TaskId;
        _turnId = request.TurnId;
        _allowParallelToolCalls = request.AllowParallelToolCalls;
        _promptCacheKey = NormalizeCacheKey(request.PromptCacheKey);
        foreach (var tool in SnapshotTools(request.Tools)) _tools[tool.Name] = tool;
        foreach (var message in request.Messages) _initialInput.Add(ToResponsesMessage(message));
        if (_initialInput.Count == 0) throw new ArgumentException("Turn cần ít nhất một message.", nameof(request));
        _started = true;

        if (!await TryConnect(cancellationToken))
        {
            await foreach (var item in StartFallback(request, cancellationToken).WithCancellation(cancellationToken))
                yield return item;
            yield break;
        }

        string? warmupResponseId = null;
        if (_enablePrewarm) warmupResponseId = await TryPrewarm(cancellationToken);

        // Prewarm may have observed a closed/broken socket. Because generate=false cannot execute
        // model output or tools, reconnecting before the real request is safe.
        if (_connection?.IsOpen != true && !await TryConnect(cancellationToken))
        {
            await foreach (var item in StartFallback(request, cancellationToken).WithCancellation(cancellationToken))
                yield return item;
            yield break;
        }

        var input = warmupResponseId is null ? _initialInput : new JsonArray();
        var payload = BuildPayload(input, warmupResponseId);
        await foreach (var item in SendAndReceive(payload, cancellationToken).WithCancellation(cancellationToken))
            yield return item;
    }

    public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
        AgentTransportContinuationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureUsable();
        if (!_started) throw new InvalidOperationException("Responses WebSocket turn chưa bắt đầu.");
        if (request.TaskId != _taskId || request.TurnId != _turnId)
            throw new InvalidOperationException("Continuation không thuộc task/turn đang chạy.");

        if (_fallback is not null)
        {
            await foreach (var item in _fallback.ContinueAsync(request, cancellationToken).WithCancellation(cancellationToken))
                yield return item;
            yield break;
        }

        if (_pendingCalls.Count == 0 && request.SupplementalUserMessages is not { Count: > 0 })
            throw new InvalidOperationException("Không có Responses function call đang chờ kết quả.");
        if (string.IsNullOrWhiteSpace(_previousResponseId))
            throw new InvalidOperationException("Responses WebSocket continuation thiếu previous_response_id.");

        var byId = request.ToolResults
            .GroupBy(x => x.ToolCallId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        if (byId.Count != _pendingCalls.Count || byId.Values.Any(v => v.Length != 1)
            || _pendingCalls.Any(call => !byId.ContainsKey(call.Id)))
            throw new InvalidOperationException("Tool results phải khớp đúng một lần với toàn bộ Responses function call đang chờ.");
        foreach (var call in _pendingCalls)
        {
            var result = byId[call.Id][0];
            if (!string.Equals(call.Name, result.ToolName, StringComparison.Ordinal))
                throw new InvalidOperationException("Tool result không khớp tên function call đang chờ: " + call.Name);
        }

        if (request.NewlyLoadedTools is { Count: > 0 })
            foreach (var tool in SnapshotTools(request.NewlyLoadedTools)) _tools[tool.Name] = tool;

        var input = new JsonArray();
        foreach (var call in _pendingCalls)
        {
            var result = byId[call.Id][0];
            input.Add(new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = call.Id,
                ["output"] = result.Content
            });
        }
        foreach (var message in request.SupplementalUserMessages ?? [])
            input.Add(new JsonObject { ["role"] = "user", ["content"] = message });
        _pendingCalls = [];

        var payload = BuildPayload(input, _previousResponseId);
        await foreach (var item in SendAndReceive(payload, cancellationToken).WithCancellation(cancellationToken))
            yield return item;
    }

    public void Cancel()
    {
        if (!_disposed && !_lifetime.IsCancellationRequested) _lifetime.Cancel();
        _fallback?.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_lifetime.IsCancellationRequested) _lifetime.Cancel();
        if (_fallback is not null) await _fallback.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        _lifetime.Dispose();
    }

    private async Task<bool> TryConnect(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureConnected(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException)
        {
            return false;
        }
    }

    private async IAsyncEnumerable<AgentTransportEvent> StartFallback(
        AgentTransportStartRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_httpFallbackFactory is null)
            throw new IOException("Responses WebSocket không kết nối được và không có HTTP fallback.");
        _fallback = _httpFallbackFactory();
        if (_fallback is IAgentRequestBudgetSource budgeted)
            budgeted.RequestBudgetEvaluated += ForwardFallbackBudget;
        await foreach (var item in _fallback.StartAsync(request, cancellationToken).WithCancellation(cancellationToken))
            yield return item;
    }

    private void ForwardFallbackBudget(AgentRequestBudgetReceipt receipt)
        => _fallbackBudgetObserver?.Invoke(receipt);
    private event Action<AgentRequestBudgetReceipt>? _fallbackBudgetObserver;

    private async Task EnsureConnected(CancellationToken cancellationToken)
    {
        if (_connection?.IsOpen == true) return;
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
        _connection = _connectionFactory();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(_apiKey)) headers["Authorization"] = "Bearer " + _apiKey;
        await _connection.ConnectAsync(WebSocketEndpoint(_profile), headers, cancellationToken);
    }

    private async Task<string?> TryPrewarm(CancellationToken cancellationToken)
    {
        if (_connection?.IsOpen != true) return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        _trace?.Mark(AgentTraceKind.PrewarmStart, "responses-websocket");
        try
        {
            var payload = BuildPayload(_initialInput, previousResponseId: null);
            payload["generate"] = false;
            var serialized = _requestBudget.Prepare(payload, _taskId, _turnId, "ResponsesWebSocket", token, prewarm: true);
            await _connection.SendTextAsync(serialized, token);

            string? responseId = null;
            var completed = false;
            var characters = 0;
            await foreach (var raw in _connection.ReceiveTextAsync(token).WithCancellation(token))
            {
                characters += raw.Length;
                if (characters > 3_000_000) throw new IOException("Responses prewarm vượt giới hạn an toàn 3 MB.");
                using var json = JsonDocument.Parse(raw);
                var root = json.RootElement;
                var type = String(root, "type") ?? "";
                if (type is "error" or "response.failed" or "response.incomplete")
                    throw new IOException("Responses prewarm không hoàn tất; bỏ prewarm và chạy request thật.");
                if (type == "response.output_text.delta" || type == "response.output_item.done")
                    throw new InvalidDataException("Responses prewarm generate=false trả output; không dùng response này làm continuation.");
                if (type is "response.created" or "response.in_progress")
                {
                    if (root.TryGetProperty("response", out var created) && created.ValueKind == JsonValueKind.Object)
                        responseId ??= String(created, "id");
                }
                if (type == "response.completed")
                {
                    if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("Responses prewarm completion thiếu response object.");
                    responseId = String(response, "id") ?? responseId;
                    completed = true;
                    break;
                }
            }
            if (!completed || string.IsNullOrWhiteSpace(responseId))
                throw new IOException("Responses prewarm đóng trước completion hoặc thiếu response id.");

            _requestBudget.ObserveCompleted(null, responseId, prewarm: true);
            _trace?.Mark(AgentTraceKind.PrewarmFinish, "responses-websocket", "success");
            return responseId;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is WebSocketException or IOException or JsonException or InvalidDataException)
        {
            _trace?.Mark(AgentTraceKind.PrewarmFinish, "responses-websocket", "failed:" + ex.GetType().Name);
            if (_connection?.IsOpen != true && _connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }
            return null;
        }
    }

    private async IAsyncEnumerable<AgentTransportEvent> SendAndReceive(
        JsonObject payload,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureUsable();
        if (_connection?.IsOpen != true) throw new IOException("Responses WebSocket đã đóng; không tự phát lại request không rõ trạng thái.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;

        // From this point onward a network failure is ambiguous: the provider may have accepted
        // the request. Do not auto-fallback/retry, which could duplicate tool execution or output.
        var serialized = _requestBudget.Prepare(payload, _taskId, _turnId, "ResponsesWebSocket", token);
        await _connection.SendTextAsync(serialized, token);

        var startedEmitted = false;
        var completed = false;
        string? responseId = null;
        var outputItems = new List<string>();
        AgentTransportUsage? usage = null;
        var textCharacters = 0;
        var messageCharacters = 0;

        await foreach (var raw in _connection.ReceiveTextAsync(token).WithCancellation(token))
        {
            token.ThrowIfCancellationRequested();
            messageCharacters += raw.Length;
            if (messageCharacters > 3_000_000)
                throw new IOException("Responses WebSocket response vượt giới hạn an toàn 3 MB.");

            using var json = JsonDocument.Parse(raw);
            var root = json.RootElement;
            var type = String(root, "type") ?? "";

            if (type == "error")
                throw new IOException("OpenAI Responses WebSocket báo lỗi. Không tự phát lại request.");
            if (type == "response.failed")
                throw new IOException("OpenAI Responses WebSocket kết thúc ở trạng thái failed.");
            if (type == "response.incomplete")
            {
                var reason = ReadIncompleteReason(root);
                throw new IOException("OpenAI Responses WebSocket kết thúc chưa hoàn tất" + (reason.Length > 0 ? ": " + reason : "."));
            }

            if (type is "response.created" or "response.in_progress")
            {
                if (root.TryGetProperty("response", out var responseObject) && responseObject.ValueKind == JsonValueKind.Object)
                    responseId ??= String(responseObject, "id");
                if (!startedEmitted)
                {
                    startedEmitted = true;
                    yield return AgentTransportEvent.Started(responseId);
                }
                continue;
            }

            if (!startedEmitted)
            {
                startedEmitted = true;
                yield return AgentTransportEvent.Started(responseId);
            }

            if (type == "response.output_text.delta" && String(root, "delta") is { Length: > 0 } text)
            {
                textCharacters += text.Length;
                if (textCharacters > 120_000) throw new IOException("Responses answer vượt giới hạn 120.000 ký tự.");
                yield return AgentTransportEvent.TextDeltaEvent(text);
                continue;
            }
            if (type == "response.reasoning_summary_text.delta" && String(root, "delta") is { Length: > 0 } summary)
            {
                yield return AgentTransportEvent.ReasoningSummary(summary);
                continue;
            }
            if (type == "response.output_item.done" && root.TryGetProperty("item", out var item)
                && item.ValueKind == JsonValueKind.Object)
            {
                outputItems.Add(item.GetRawText());
                continue;
            }
            if (type == "response.completed")
            {
                if (!root.TryGetProperty("response", out var responseObject) || responseObject.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("response.completed thiếu response object.");
                responseId = String(responseObject, "id") ?? responseId;
                usage = ReadUsage(responseObject);
                if (outputItems.Count == 0 && responseObject.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                    outputItems.AddRange(output.EnumerateArray().Select(x => x.GetRawText()));
                completed = true;
                break;
            }
        }

        token.ThrowIfCancellationRequested();
        if (!completed)
            throw new IOException("Responses WebSocket đóng trước response.completed; không tự phát lại request không rõ trạng thái.");
        if (string.IsNullOrWhiteSpace(responseId))
            throw new IOException("Responses WebSocket completion thiếu response id; không thể continuation an toàn.");

        var calls = ResolveFunctionCalls(outputItems);
        var hasMessage = outputItems.Any(IsAssistantMessage);
        if (!hasMessage && calls.Count == 0)
            throw new IOException("Responses WebSocket hoàn tất nhưng không trả assistant message hoặc function call.");

        _previousResponseId = responseId;
        _pendingCalls = calls;
        _requestBudget.ObserveCompleted(usage, responseId);
        if (usage is not null) yield return AgentTransportEvent.Meter(usage);
        foreach (var call in calls) yield return AgentTransportEvent.Tool(call);
        yield return AgentTransportEvent.Complete(responseId, calls.Count > 0 ? "tool_calls" : "stop");
    }

    private JsonObject BuildPayload(JsonArray input, string? previousResponseId)
    {
        var payload = new JsonObject
        {
            ["type"] = "response.create",
            ["model"] = _profile.Model,
            ["input"] = input.DeepClone(),
            ["store"] = false
        };
        if (!string.IsNullOrWhiteSpace(previousResponseId)) payload["previous_response_id"] = previousResponseId;
        if (_tools.Count > 0)
        {
            payload["tools"] = BuildTools();
            payload["parallel_tool_calls"] = _allowParallelToolCalls;
        }
        if (_promptCacheKey is { Length: > 0 }) payload["prompt_cache_key"] = _promptCacheKey;

        var reasoning = new JsonObject();
        if (_profile.RequestReasoningSummary) reasoning["summary"] = "auto";
        if (AiModelCapabilities.ResolveReasoningEffort(_profile) is { Length: > 0 } effort) reasoning["effort"] = effort;
        if (reasoning.Count > 0) payload["reasoning"] = reasoning;
        return payload;
    }

    private JsonObject ToResponsesMessage(AgentTransportMessage message)
    {
        var role = message.Role switch
        {
            AgentTransportMessageRole.System => "system",
            AgentTransportMessageRole.User => "user",
            AgentTransportMessageRole.Assistant => "assistant",
            _ => throw new InvalidOperationException("Role không được Responses transport hỗ trợ.")
        };
        if (message.Images is not { Count: > 0 } && message.Files is not { Count: > 0 })
            return new JsonObject { ["role"] = role, ["content"] = message.Content ?? "" };

        var parts = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = message.Content ?? "" });
        foreach (var image in message.Images ?? [])
            parts.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = "data:" + image.MimeType + ";base64," + Convert.ToBase64String(image.Data) });
        foreach (var file in message.Files ?? [])
            parts.Add(new JsonObject
            {
                ["type"] = "input_file",
                ["filename"] = file.Name,
                ["file_data"] = "data:" + file.MimeType + ";base64," + Convert.ToBase64String(file.Data)
            });
        return new JsonObject { ["role"] = role, ["content"] = parts };
    }

    private JsonArray BuildTools()
    {
        var result = new JsonArray();
        foreach (var tool in _tools.Values)
            result.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = JsonNode.Parse(tool.Parameters.GetRawText())
            });
        return result;
    }

    private static IReadOnlyList<AgentToolDefinition> SnapshotTools(IEnumerable<AgentToolDefinition> tools)
    {
        var result = new List<AgentToolDefinition>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name) || !names.Add(tool.Name))
                throw new ArgumentException("Tên tool trống hoặc trùng trong Responses WebSocket request.");
            result.Add(tool.Snapshot());
        }
        return result;
    }

    private static List<AgentTransportToolCall> ResolveFunctionCalls(IEnumerable<string> outputItems)
    {
        var result = new List<AgentTransportToolCall>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in outputItems)
        {
            using var json = JsonDocument.Parse(raw);
            var item = json.RootElement;
            if (!string.Equals(String(item, "type"), "function_call", StringComparison.Ordinal)) continue;
            var id = String(item, "call_id") ?? "";
            var name = String(item, "name") ?? "";
            var arguments = String(item, "arguments") ?? "";
            if (id.Length == 0 || name.Length == 0 || !ids.Add(id))
                throw new InvalidDataException("Responses function_call thiếu/trùng call_id hoặc thiếu name.");
            using var args = JsonDocument.Parse(arguments);
            if (args.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Responses function_call arguments phải là JSON object.");
            result.Add(new AgentTransportToolCall(id, name, arguments));
        }
        return result;
    }

    private static AgentTransportUsage? ReadUsage(JsonElement response)
    {
        if (!response.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        var input = Long(usage, "input_tokens");
        var output = Long(usage, "output_tokens");
        var total = Long(usage, "total_tokens");
        long? cached = null;
        if (usage.TryGetProperty("input_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
            cached = Long(details, "cached_tokens");
        return new AgentTransportUsage(input, cached, null, output, total);
    }

    private static bool IsAssistantMessage(string raw)
    {
        try
        {
            using var json = JsonDocument.Parse(raw);
            return string.Equals(String(json.RootElement, "type"), "message", StringComparison.Ordinal)
                && string.Equals(String(json.RootElement, "role"), "assistant", StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }

    private static string ReadIncompleteReason(JsonElement root)
    {
        if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("incomplete_details", out var details) && details.ValueKind == JsonValueKind.Object)
            return String(details, "reason") ?? "";
        return "";
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Long(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) && number >= 0 ? number : null;

    private static string? NormalizeCacheKey(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > 128 || value.Any(char.IsControl))
            throw new ArgumentException("Prompt cache key không hợp lệ.");
        return value;
    }

    private static Uri WebSocketEndpoint(AiProfile profile)
    {
        if (!AiModelCapabilities.IsOfficialOpenAi(profile))
            throw new InvalidOperationException("Responses WebSocket v2 hiện chỉ bật cho endpoint OpenAI chính thức https://api.openai.com/v1.");
        var http = AiClient.Endpoint(profile, "responses");
        var builder = new UriBuilder(http) { Scheme = "wss", Port = -1 };
        return builder.Uri;
    }

    private void EnsureUsable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OpenAiResponsesWebSocketTransport));
    }
}

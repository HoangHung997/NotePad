using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using H2Notes.Core;

namespace H2AgentLab.Transport;

/// <summary>
/// Turn-scoped Ollama transport extracted from the proven v1 wire behavior. It deliberately owns
/// Ollama JSON/history/thinking replay so the future orchestrator never needs provider-specific
/// message shapes. Tool outputs are supplied through ContinueAsync; the full message replay is an
/// Ollama limitation and is advertised by IncrementalContinuation=false.
/// </summary>
public sealed partial class OllamaTransport : IAgentTransport, IAgentRequestBudgetSource, IAgentContextRebaseTransport
{
    private sealed class ToolAccumulator(string id)
    {
        public string Id { get; set; } = id;
        public string Name { get; set; } = "";
        public StringBuilder ArgumentFragments { get; } = new();
        public string? ObjectArguments { get; set; }
    }

    private readonly AgentRequestBudgetGuard _requestBudget;
    public AgentRequestBudgetReceipt? LastRequestBudget => _requestBudget.LastReceipt;
    public event Action<AgentRequestBudgetReceipt>? RequestBudgetEvaluated
    {
        add => _requestBudget.Evaluated += value;
        remove => _requestBudget.Evaluated -= value;
    }
    private readonly AiProfile _profile;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _lifetime = new();
    private JsonArray _messages = [];
    private readonly Dictionary<string, AgentToolDefinition> _tools = new(StringComparer.Ordinal);
    private List<AgentTransportToolCall> _pendingCalls = [];
    private Guid _taskId;
    private Guid _turnId;
    private int _responseSequence;
    private bool _started;
    private bool _disposed;

    public OllamaTransport(AiProfile profile, HttpMessageHandler? handler = null)
    {
        if (profile.Protocol != AiProtocol.Ollama)
            throw new ArgumentException("OllamaTransport chỉ nhận hồ sơ Ollama.", nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.Model))
            throw new ArgumentException("Chưa chọn model Ollama.", nameof(profile));

        // Shared Core owns the snapshot contract so future AiProfile fields cannot be silently
        // omitted by this transport while a turn is running.
        _profile = profile.Copy();
        _requestBudget = new AgentRequestBudgetGuard(_profile);
        _profile.Protocol = AiProtocol.Ollama;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.OllamaNative;

    public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
        AgentTransportStartRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureUsable();
        if (_started) throw new InvalidOperationException("Ollama transport turn đã bắt đầu.");
        if (request.TaskId == Guid.Empty || request.TurnId == Guid.Empty)
            throw new ArgumentException("TaskId và TurnId phải là GUID hợp lệ.", nameof(request));

        var messages = new JsonArray();
        foreach (var message in request.Messages)
            messages.Add(ToOllamaMessage(message));
        if (messages.Count == 0) throw new ArgumentException("Turn cần ít nhất một message.", nameof(request));

        var tools = SnapshotTools(request.Tools);
        _messages = messages;
        foreach (var tool in tools) _tools[tool.Name] = tool;
        _taskId = request.TaskId;
        _turnId = request.TurnId;
        _started = true;

        await foreach (var item in StreamOnce(cancellationToken).WithCancellation(cancellationToken))
            yield return item;
    }

    public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
        AgentTransportContinuationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureUsable();
        if (!_started) throw new InvalidOperationException("Ollama transport turn chưa bắt đầu.");
        if (request.TaskId != _taskId || request.TurnId != _turnId)
            throw new InvalidOperationException("Continuation không thuộc task/turn đang chạy.");
        if (_pendingCalls.Count == 0 && request.SupplementalUserMessages is not { Count: > 0 })
            throw new InvalidOperationException("Không có tool call đang chờ kết quả.");

        var byId = request.ToolResults
            .GroupBy(x => x.ToolCallId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        if (byId.Count != _pendingCalls.Count || byId.Values.Any(v => v.Length != 1)
            || _pendingCalls.Any(call => !byId.ContainsKey(call.Id)))
            throw new InvalidOperationException("Tool results phải khớp đúng một lần với toàn bộ tool call đang chờ.");

        var additions = request.NewlyLoadedTools is null ? [] : SnapshotTools(request.NewlyLoadedTools);
        foreach (var call in _pendingCalls)
        {
            var result = byId[call.Id][0];
            if (!string.Equals(result.ToolName, call.Name, StringComparison.Ordinal))
                throw new InvalidOperationException("Tool result không khớp tên tool call đang chờ: " + call.Name);
        }

        foreach (var tool in additions) _tools[tool.Name] = tool;
        foreach (var call in _pendingCalls)
        {
            var result = byId[call.Id][0];
            _messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_name"] = call.Name,
                ["content"] = result.Content
            });
        }
        foreach (var input in request.SupplementalUserMessages ?? [])
            _messages.Add(new JsonObject { ["role"] = "user", ["content"] = input });
        _pendingCalls = [];

        await foreach (var item in StreamOnce(cancellationToken).WithCancellation(cancellationToken))
            yield return item;
    }

    public void Cancel()
    {
        if (!_disposed && !_lifetime.IsCancellationRequested) _lifetime.Cancel();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        if (!_lifetime.IsCancellationRequested) _lifetime.Cancel();
        _http.Dispose();
        _lifetime.Dispose();
        return ValueTask.CompletedTask;
    }

    private async IAsyncEnumerable<AgentTransportEvent> StreamOnce(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureUsable();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        var responseNumber = ++_responseSequence;

        var payload = BuildContextPayload(_messages, _tools.Values.ToArray());

        var serialized = _requestBudget.Prepare(payload, _taskId, _turnId, "OllamaTransport", token);
        using var request = new HttpRequestMessage(HttpMethod.Post, AiClient.Endpoint(_profile, "api/chat"))
        {
            Content = new StringContent(serialized, Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw AiFailure.FromStatus(response.StatusCode, "");

        yield return AgentTransportEvent.Started();

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream);
        var content = new StringBuilder();
        var thinkingText = new StringBuilder();
        var calls = new SortedDictionary<int, ToolAccumulator>();
        var done = false;
        string? doneReason = null;
        AgentTransportUsage? usage = null;
        long receivedCharacters = 0;

        while (await reader.ReadLineAsync(token) is { } line)
        {
            token.ThrowIfCancellationRequested();
            receivedCharacters += line.Length;
            if (receivedCharacters > 2_000_000)
                throw new IOException("Phản hồi Ollama vượt giới hạn an toàn 2 MB.");
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.TryGetProperty("error", out _))
                throw new IOException("Ollama báo lỗi trong luồng phản hồi. Không tự gửi lại.");

            if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
            {
                if (String(message, "thinking") is { Length: > 0 } thought)
                {
                    thinkingText.Append(thought);
                    yield return AgentTransportEvent.Reasoning(thought);
                }
                if (String(message, "content") is { Length: > 0 } text)
                {
                    content.Append(text);
                    if (content.Length > 120_000) throw new IOException("Câu trả lời Ollama vượt giới hạn 120.000 ký tự.");
                    yield return AgentTransportEvent.TextDeltaEvent(text);
                }
                if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                    AccumulateToolCalls(toolCalls, calls, responseNumber);
            }

            if (String(root, "done_reason") is { Length: > 0 } reason) doneReason = reason;
            var measured = Usage(root);
            if (measured is not null) usage = measured;
            if (root.TryGetProperty("done", out var completed) && completed.ValueKind == JsonValueKind.True)
            {
                done = true;
                break;
            }
        }

        token.ThrowIfCancellationRequested();
        if (!done) throw new IOException("Kết nối Ollama kết thúc trước khi model hoàn tất; không chạy tool call dở dang.");
        if (doneReason is "length" or "unload")
            throw new IOException("Ollama kết thúc chưa hoàn tất: " + doneReason + ". Không chạy tool call bị cắt.");

        var resolvedCalls = ResolveToolCalls(calls);
        if (content.Length == 0 && resolvedCalls.Count == 0)
            throw new IOException("Ollama hoàn tất nhưng không trả nội dung hoặc tool call.");

        var assistant = new JsonObject { ["role"] = "assistant", ["content"] = content.ToString() };
        if (resolvedCalls.Count > 0)
        {
            var nativeCalls = new JsonArray();
            foreach (var call in resolvedCalls)
            {
                nativeCalls.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = JsonNode.Parse(call.ArgumentsJson)
                    }
                });
            }
            assistant["tool_calls"] = nativeCalls;
            // Ollama requires the provider-supplied thinking field to accompany an active assistant
            // tool-call turn. It remains private to this transport and is never persisted as chat.
            if (thinkingText.Length > 0) assistant["thinking"] = thinkingText.ToString();
        }
        _messages.Add(assistant);
        _pendingCalls = resolvedCalls;

        foreach (var call in resolvedCalls) yield return AgentTransportEvent.Tool(call);
        _requestBudget.ObserveCompleted(usage);
        if (usage is not null) yield return AgentTransportEvent.Meter(usage);
        yield return AgentTransportEvent.Complete(finishReason: resolvedCalls.Count > 0 ? "tool_calls" : doneReason ?? "stop");
    }

    private JsonObject BuildContextPayload(JsonArray input, IReadOnlyList<AgentToolDefinition> tools)
    {
        var payload = new JsonObject
        {
            ["model"] = _profile.Model,
            ["messages"] = input.DeepClone(),
            ["stream"] = true
        };
        if (tools.Count > 0) payload["tools"] = BuildTools(tools);
        if (AiModelCapabilities.ResolveReasoningEffort(_profile) is { Length: > 0 } effort)
            payload["think"] = effort;
        else if (_profile.OllamaThinking is bool thinking)
            payload["think"] = thinking;

        return payload;
    }

    private JsonObject ToOllamaMessage(AgentTransportMessage message)
    {
        if (message.Files is { Count: > 0 })
            throw new InvalidOperationException("Ollama native chat không hỗ trợ file attachment trong transport này; hãy tiền xử lý thành text/image trước.");
        var role = message.Role switch
        {
            AgentTransportMessageRole.System => "system",
            AgentTransportMessageRole.User => "user",
            AgentTransportMessageRole.Assistant => "assistant",
            _ => throw new InvalidOperationException("Role không được Ollama transport hỗ trợ.")
        };
        var result = new JsonObject { ["role"] = role, ["content"] = message.Content ?? "" };
        if (message.Images is { Count: > 0 })
        {
            var images = new JsonArray();
            foreach (var image in message.Images) images.Add(Convert.ToBase64String(image.Data));
            result["images"] = images;
        }
        return result;
    }

    private static List<AgentToolDefinition> SnapshotTools(IReadOnlyList<AgentToolDefinition> tools)
    {
        var result = new List<AgentToolDefinition>(tools.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name) || tool.Parameters.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Tool name/schema không hợp lệ.", nameof(tools));
            if (!names.Add(tool.Name)) throw new ArgumentException("Tool bị trùng tên: " + tool.Name, nameof(tools));
            result.Add(tool.Snapshot());
        }
        return result;
    }

    private JsonArray BuildTools(IEnumerable<AgentToolDefinition>? candidates = null)
    {
        var result = new JsonArray();
        foreach (var tool in candidates ?? _tools.Values)
        {
            result.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(tool.Parameters.GetRawText())
                }
            });
        }
        return result;
    }

    private void AccumulateToolCalls(JsonElement toolCalls, SortedDictionary<int, ToolAccumulator> calls, int responseNumber)
    {
        var position = 0;
        foreach (var toolCall in toolCalls.EnumerateArray())
        {
            if (toolCall.ValueKind != JsonValueKind.Object || !toolCall.TryGetProperty("function", out var function)
                || function.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Ollama tool call không đúng cấu trúc function.");
            var index = toolCall.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var explicitIndex)
                ? explicitIndex
                : function.TryGetProperty("index", out ix) && ix.TryGetInt32(out explicitIndex)
                    ? explicitIndex : position;
            if (index is < 0 or > 63) throw new InvalidDataException("Ollama trả quá nhiều tool call trong một response.");
            if (!calls.TryGetValue(index, out var accumulator))
            {
                var id = String(toolCall, "id");
                if (string.IsNullOrWhiteSpace(id)) id = $"ollama_{_turnId:N}_{responseNumber}_{index}";
                accumulator = new ToolAccumulator(id);
                calls[index] = accumulator;
            }
            else if (String(toolCall, "id") is { Length: > 0 } id)
            {
                accumulator.Id = id;
            }

            if (String(function, "name") is { Length: > 0 } name) accumulator.Name = name;
            if (function.TryGetProperty("arguments", out var arguments))
            {
                if (arguments.ValueKind == JsonValueKind.Object)
                    accumulator.ObjectArguments = arguments.GetRawText();
                else if (arguments.ValueKind == JsonValueKind.String && arguments.GetString() is { } fragment)
                    accumulator.ArgumentFragments.Append(fragment);
                else if (arguments.ValueKind != JsonValueKind.Null && arguments.ValueKind != JsonValueKind.Undefined)
                    throw new InvalidDataException("Ollama tool arguments phải là object hoặc JSON string.");
            }
            position++;
        }
    }

    private static List<AgentTransportToolCall> ResolveToolCalls(SortedDictionary<int, ToolAccumulator> calls)
    {
        var result = new List<AgentTransportToolCall>(calls.Count);
        foreach (var accumulator in calls.Values)
        {
            if (string.IsNullOrWhiteSpace(accumulator.Name)) throw new InvalidDataException("Ollama tool call thiếu function name.");
            var raw = accumulator.ObjectArguments ?? (accumulator.ArgumentFragments.Length > 0 ? accumulator.ArgumentFragments.ToString() : "{}");
            using var json = JsonDocument.Parse(raw);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Ollama tool arguments phải giải mã thành JSON object.");
            result.Add(new(accumulator.Id, accumulator.Name, json.RootElement.GetRawText()));
        }
        return result;
    }

    private static AgentTransportUsage? Usage(JsonElement root)
    {
        var input = Long(root, "prompt_eval_count");
        var cached = Long(root, "prompt_eval_cached_count");
        var output = Long(root, "eval_count");
        if (input is null && cached is null && output is null) return null;
        long? total = input is null && output is null ? null : (input ?? 0) + (output ?? 0);
        return new(input, cached, null, output, total);
    }

    private static long? Long(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) && number >= 0 ? number : null;

    private static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private void EnsureUsable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OllamaTransport));
        _lifetime.Token.ThrowIfCancellationRequested();
    }
}

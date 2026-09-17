using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using H2Notes.Core;

namespace H2AgentLab.Transport;

/// <summary>
/// Turn-scoped OpenAI-compatible Chat Completions fallback transport. It preserves the proven v1
/// tool-call wire behavior while moving provider JSON, fragmented SSE assembly and transcript replay
/// out of the future orchestrator. Chat Completions has no provider-side incremental continuation in
/// this harness, so each continuation replays the bounded active turn owned by this transport.
/// </summary>
public sealed class ChatCompletionsTransport : IAgentTransport
{
    private sealed class ToolAccumulator(string fallbackId)
    {
        public string Id { get; set; } = fallbackId;
        public StringBuilder Name { get; } = new();
        public StringBuilder Arguments { get; } = new();
    }

    private readonly AiProfile _profile;
    private readonly string _apiKey;
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

    public ChatCompletionsTransport(AiProfile profile, string apiKey, HttpMessageHandler? handler = null)
    {
        if (profile.Protocol != AiProtocol.OpenAiChat)
            throw new ArgumentException("ChatCompletionsTransport chỉ nhận hồ sơ OpenAiChat.", nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.Model))
            throw new ArgumentException("Chưa chọn model Chat Completions.", nameof(profile));
        if (profile.RequestReasoningSummary)
            throw new ArgumentException("Chat Completions fallback không hỗ trợ yêu cầu reasoning summary công khai; dùng Responses nếu cần.", nameof(profile));

        _profile = profile.Copy();
        _profile.Protocol = AiProtocol.OpenAiChat;
        _profile.RequestReasoningSummary = false;
        _profile.OllamaThinking = null;
        _apiKey = apiKey ?? "";
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.ChatCompletionsFallback;

    public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
        AgentTransportStartRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureUsable();
        if (_started) throw new InvalidOperationException("Chat Completions transport turn đã bắt đầu.");
        if (request.TaskId == Guid.Empty || request.TurnId == Guid.Empty)
            throw new ArgumentException("TaskId và TurnId phải là GUID hợp lệ.", nameof(request));
        if (request.AllowParallelToolCalls && !Capabilities.ParallelToolCalls)
            throw new InvalidOperationException("Chat Completions fallback hiện không quảng bá parallel tool calls.");

        var messages = new JsonArray();
        foreach (var message in request.Messages)
            messages.Add(ToChatMessage(message));
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
        if (!_started) throw new InvalidOperationException("Chat Completions transport turn chưa bắt đầu.");
        if (request.TaskId != _taskId || request.TurnId != _turnId)
            throw new InvalidOperationException("Continuation không thuộc task/turn đang chạy.");
        if (_pendingCalls.Count == 0)
            throw new InvalidOperationException("Không có tool call đang chờ kết quả.");

        var byId = request.ToolResults
            .GroupBy(x => x.ToolCallId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        if (byId.Count != _pendingCalls.Count || byId.Values.Any(v => v.Length != 1)
            || _pendingCalls.Any(call => !byId.ContainsKey(call.Id)))
            throw new InvalidOperationException("Tool results phải khớp đúng một lần với toàn bộ tool call đang chờ.");

        foreach (var call in _pendingCalls)
        {
            var result = byId[call.Id][0];
            if (!string.Equals(result.ToolName, call.Name, StringComparison.Ordinal))
                throw new InvalidOperationException("Tool result không khớp tên tool call đang chờ: " + call.Name);
        }

        if (request.NewlyLoadedTools is { Count: > 0 })
            foreach (var tool in SnapshotTools(request.NewlyLoadedTools)) _tools[tool.Name] = tool;

        foreach (var call in _pendingCalls)
        {
            var result = byId[call.Id][0];
            _messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = call.Id,
                ["content"] = result.Content
            });
        }
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

        var payload = new JsonObject
        {
            ["model"] = _profile.Model,
            ["stream"] = true,
            ["messages"] = _messages.DeepClone()
        };
        if (_tools.Count > 0) payload["tools"] = BuildTools();
        if (AiModelCapabilities.ResolveReasoningEffort(_profile) is { Length: > 0 } effort)
            payload["reasoning_effort"] = effort;

        using var request = new HttpRequestMessage(HttpMethod.Post, AiClient.Endpoint(_profile, "chat/completions"))
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw AiFailure.FromStatus(response.StatusCode, "");

        yield return AgentTransportEvent.Started();

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream);
        var content = new StringBuilder();
        var calls = new SortedDictionary<int, ToolAccumulator>();
        var finished = false;
        string? finishReason = null;
        long receivedCharacters = 0;

        while (await reader.ReadLineAsync(token) is { } line)
        {
            token.ThrowIfCancellationRequested();
            receivedCharacters += line.Length;
            if (receivedCharacters > 2_000_000)
                throw new IOException("Phản hồi Chat Completions vượt giới hạn an toàn 2 MB.");
            if (line.Length == 0 || line.StartsWith(':')) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            line = line[5..].Trim();
            if (line == "[DONE]")
            {
                finished = true;
                break;
            }

            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.TryGetProperty("error", out _))
                throw new IOException("Máy chủ Chat Completions báo lỗi trong luồng phản hồi. Không tự gửi lại.");
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                continue;

            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                var reasoning = String(delta, "reasoning_content");
                if (string.IsNullOrEmpty(reasoning)) reasoning = String(delta, "reasoning");
                if (reasoning is { Length: > 0 }) yield return AgentTransportEvent.Reasoning(reasoning);

                if (String(delta, "content") is { Length: > 0 } text)
                {
                    content.Append(text);
                    if (content.Length > 120_000)
                        throw new IOException("Câu trả lời Chat Completions vượt giới hạn 120.000 ký tự.");
                    yield return AgentTransportEvent.TextDeltaEvent(text);
                }

                if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                    AccumulateToolCalls(toolCalls, calls, responseNumber);
            }

            if (String(choice, "finish_reason") is { Length: > 0 } reason)
            {
                finishReason = reason;
                finished = true;
                break;
            }
        }

        token.ThrowIfCancellationRequested();
        if (!finished)
            throw new IOException("Kết nối Chat Completions kết thúc trước khi model hoàn tất; không chạy tool call dở dang.");
        if (finishReason is not null && finishReason is not ("stop" or "tool_calls"))
            throw new IOException("Chat Completions kết thúc chưa hoàn tất: " + finishReason + ". Không chạy tool call bị cắt.");

        var resolvedCalls = ResolveToolCalls(calls);
        if (content.Length == 0 && resolvedCalls.Count == 0)
            throw new IOException("Chat Completions hoàn tất nhưng không trả nội dung hoặc tool call.");
        if (finishReason == "tool_calls" && resolvedCalls.Count == 0)
            throw new IOException("Chat Completions báo tool_calls nhưng không có tool call hợp lệ.");

        var assistant = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = content.ToString()
        };
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
                        ["arguments"] = call.ArgumentsJson
                    }
                });
            }
            assistant["tool_calls"] = nativeCalls;
        }
        _messages.Add(assistant);
        _pendingCalls = resolvedCalls;

        foreach (var call in resolvedCalls) yield return AgentTransportEvent.Tool(call);
        yield return AgentTransportEvent.Complete(finishReason: resolvedCalls.Count > 0 ? "tool_calls" : finishReason ?? "stop");
    }

    private JsonObject ToChatMessage(AgentTransportMessage message)
    {
        var role = message.Role switch
        {
            AgentTransportMessageRole.System => "system",
            AgentTransportMessageRole.User => "user",
            AgentTransportMessageRole.Assistant => "assistant",
            _ => throw new InvalidOperationException("Role không được Chat Completions transport hỗ trợ.")
        };

        var result = new JsonObject { ["role"] = role };
        if (message.Images is not { Count: > 0 } && message.Files is not { Count: > 0 })
        {
            result["content"] = message.Content ?? "";
            return result;
        }

        var parts = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = message.Content ?? "" }
        };
        foreach (var image in message.Images ?? [])
        {
            var dataUrl = "data:" + image.MimeType + ";base64," + Convert.ToBase64String(image.Data);
            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = dataUrl }
            });
        }
        foreach (var file in message.Files ?? [])
        {
            var dataUrl = "data:" + file.MimeType + ";base64," + Convert.ToBase64String(file.Data);
            parts.Add(new JsonObject
            {
                ["type"] = "file",
                ["file"] = new JsonObject
                {
                    ["filename"] = file.Name,
                    ["file_data"] = dataUrl
                }
            });
        }
        result["content"] = parts;
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

    private JsonArray BuildTools()
    {
        var result = new JsonArray();
        foreach (var tool in _tools.Values)
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
                throw new InvalidDataException("Chat Completions tool call không đúng cấu trúc function.");

            var index = toolCall.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var explicitIndex)
                ? explicitIndex : position;
            if (index is < 0 or > 63)
                throw new InvalidDataException("Chat Completions trả quá nhiều tool call trong một response.");

            if (!calls.TryGetValue(index, out var accumulator))
            {
                accumulator = new ToolAccumulator($"call_{_turnId:N}_{responseNumber}_{index}");
                calls[index] = accumulator;
            }
            if (String(toolCall, "id") is { Length: > 0 } id) accumulator.Id = id;
            if (String(function, "name") is { Length: > 0 } name) accumulator.Name.Append(name);
            if (function.TryGetProperty("arguments", out var arguments))
            {
                if (arguments.ValueKind == JsonValueKind.String && arguments.GetString() is { } fragment)
                    accumulator.Arguments.Append(fragment);
                else if (arguments.ValueKind == JsonValueKind.Object)
                    accumulator.Arguments.Append(arguments.GetRawText());
                else if (arguments.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    throw new InvalidDataException("Chat Completions tool arguments phải là JSON string/object.");
            }
            position++;
        }
    }

    private static List<AgentTransportToolCall> ResolveToolCalls(SortedDictionary<int, ToolAccumulator> calls)
    {
        var result = new List<AgentTransportToolCall>(calls.Count);
        foreach (var accumulator in calls.Values)
        {
            if (accumulator.Name.Length == 0)
                throw new InvalidDataException("Chat Completions tool call thiếu function name.");
            var raw = accumulator.Arguments.Length == 0 ? "{}" : accumulator.Arguments.ToString();
            using var json = JsonDocument.Parse(raw);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Chat Completions tool arguments phải giải mã thành JSON object.");
            result.Add(new(accumulator.Id, accumulator.Name.ToString(), json.RootElement.GetRawText()));
        }
        return result;
    }

    private static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private void EnsureUsable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ChatCompletionsTransport));
        _lifetime.Token.ThrowIfCancellationRequested();
    }
}

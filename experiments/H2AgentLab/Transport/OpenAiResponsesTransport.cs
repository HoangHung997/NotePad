using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using H2Notes.Core;

namespace H2AgentLab.Transport;

public enum OpenAiResponsesStateMode
{
    /// <summary>Privacy-preserving default. Keep store=false and replay returned output items locally.</summary>
    Stateless,

    /// <summary>
    /// Opt-in official OpenAI server state. Uses store=true and previous_response_id so tool
    /// continuations send only new function outputs rather than the growing prior transcript.
    /// </summary>
    StoredContinuation
}

/// <summary>
/// Public OpenAI Responses HTTP/SSE transport. Stateless mode is the default. StoredContinuation
/// is an explicit opt-in because OpenAI documents that store=true retains response data for later
/// retrieval; it is never enabled silently and is restricted to the official OpenAI /v1 endpoint.
/// </summary>
public sealed class OpenAiResponsesTransport : IAgentTransport
{
    private readonly AiProfile _profile;
    private readonly string _apiKey;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _lifetime = new();
    private JsonArray _requestInput = [];
    private readonly Dictionary<string, AgentToolDefinition> _tools = new(StringComparer.Ordinal);
    private readonly List<string> _completedOutputItems = [];
    private readonly OpenAiResponsesStateMode _stateMode;
    private List<AgentTransportToolCall> _pendingCalls = [];
    private Guid _taskId;
    private Guid _turnId;
    private bool _started;
    private bool _disposed;
    private bool _allowParallelToolCalls;
    private string? _promptCacheKey;
    private string? _previousResponseId;

    public OpenAiResponsesTransport(
        AiProfile profile,
        string apiKey,
        HttpMessageHandler? handler = null,
        OpenAiResponsesStateMode stateMode = OpenAiResponsesStateMode.Stateless)
    {
        if (profile.Protocol != AiProtocol.OpenAiResponses)
            throw new ArgumentException("OpenAiResponsesTransport chỉ nhận hồ sơ OpenAiResponses.", nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.Model))
            throw new ArgumentException("Chưa chọn model OpenAI Responses.", nameof(profile));
        ValidateSummaryRequest(profile);
        ValidateStateMode(profile, stateMode);

        _profile = profile.Copy();
        _profile.Protocol = AiProtocol.OpenAiResponses;
        _stateMode = stateMode;
        _apiKey = apiKey ?? "";
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public AgentTransportCapabilities Capabilities
        => _stateMode == OpenAiResponsesStateMode.StoredContinuation
            ? AgentTransportCapabilities.OpenAiResponsesHttp with { IncrementalContinuation = true }
            : AgentTransportCapabilities.OpenAiResponsesHttp;

    public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
        AgentTransportStartRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureUsable();
        if (_started) throw new InvalidOperationException("OpenAI Responses transport turn đã bắt đầu.");
        if (request.TaskId == Guid.Empty || request.TurnId == Guid.Empty)
            throw new ArgumentException("TaskId và TurnId phải là GUID hợp lệ.", nameof(request));
        if (request.AllowParallelToolCalls && !Capabilities.ParallelToolCalls)
            throw new InvalidOperationException("Transport không hỗ trợ parallel tool calls.");

        var input = new JsonArray();
        foreach (var message in request.Messages) input.Add(ToResponsesMessage(message));
        if (input.Count == 0) throw new ArgumentException("Turn cần ít nhất một message.", nameof(request));
        _requestInput = input;
        foreach (var tool in SnapshotTools(request.Tools)) _tools[tool.Name] = tool;

        _taskId = request.TaskId;
        _turnId = request.TurnId;
        _allowParallelToolCalls = request.AllowParallelToolCalls;
        _promptCacheKey = NormalizeCacheKey(request.PromptCacheKey);
        _started = true;

        await foreach (var item in StreamOnce(cancellationToken).WithCancellation(cancellationToken))
            yield return item;
    }

    public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
        AgentTransportContinuationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureUsable();
        if (!_started) throw new InvalidOperationException("OpenAI Responses transport turn chưa bắt đầu.");
        if (request.TaskId != _taskId || request.TurnId != _turnId)
            throw new InvalidOperationException("Continuation không thuộc task/turn đang chạy.");
        if (_pendingCalls.Count == 0 && request.SupplementalUserMessages is not { Count: > 0 })
            throw new InvalidOperationException("Không có Responses function call đang chờ kết quả.");

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

        var continuationInput = new JsonArray();
        if (_stateMode == OpenAiResponsesStateMode.Stateless)
        {
            foreach (var raw in _completedOutputItems)
                _requestInput.Add(JsonNode.Parse(raw) ?? throw new InvalidDataException("Responses output item không giải mã được."));
        }
        else if (string.IsNullOrWhiteSpace(_previousResponseId))
        {
            throw new InvalidOperationException("Provider-state continuation thiếu previous response id; không tự hạ cấp hoặc gửi lại toàn bộ transcript.");
        }

        foreach (var call in _pendingCalls)
        {
            var result = byId[call.Id][0];
            var output = new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = call.Id,
                ["output"] = result.Content
            };
            if (_stateMode == OpenAiResponsesStateMode.Stateless) _requestInput.Add(output);
            else continuationInput.Add(output);
        }

        if (_stateMode == OpenAiResponsesStateMode.StoredContinuation)
            _requestInput = continuationInput;

        foreach (var input in request.SupplementalUserMessages ?? [])
            _requestInput.Add(new JsonObject { ["role"] = "user", ["content"] = input });
        _completedOutputItems.Clear();
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
        var payload = BuildPayload();

        using var request = new HttpRequestMessage(HttpMethod.Post, AiClient.Endpoint(_profile, "responses"))
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw AiFailure.FromStatus(response.StatusCode, "");

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream);
        var startedEmitted = false;
        var completed = false;
        string? responseId = null;
        long receivedCharacters = 0;
        var textCharacters = 0;
        var outputItems = new List<string>();
        AgentTransportUsage? usage = null;
        string? incompleteReason = null;

        while (await reader.ReadLineAsync(token) is { } line)
        {
            token.ThrowIfCancellationRequested();
            receivedCharacters += line.Length;
            if (receivedCharacters > 3_000_000)
                throw new IOException("Responses SSE vượt giới hạn an toàn 3 MB.");
            if (line.Length == 0 || line.StartsWith(':') || line.StartsWith("event:", StringComparison.Ordinal)) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            line = line[5..].Trim();
            if (line.Length == 0 || line == "[DONE]") continue;

            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            var type = String(root, "type") ?? "";

            if (type == "error")
                throw new IOException("OpenAI Responses báo lỗi trong luồng phản hồi. Không tự gửi lại.");
            if (type == "response.failed")
                throw new IOException("OpenAI Responses kết thúc ở trạng thái failed. Không chạy function call từ phản hồi lỗi.");
            if (type == "response.incomplete")
            {
                incompleteReason = ReadIncompleteReason(root);
                throw new IOException("OpenAI Responses kết thúc chưa hoàn tất" + (incompleteReason is { Length: > 0 } ? ": " + incompleteReason : "."));
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
                if (textCharacters > 120_000)
                    throw new IOException("Responses answer vượt giới hạn 120.000 ký tự.");
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
            throw new IOException("Kết nối Responses đóng trước response.completed; không chạy function call từ phản hồi dở dang.");
        if (_stateMode == OpenAiResponsesStateMode.StoredContinuation && string.IsNullOrWhiteSpace(responseId))
            throw new IOException("Stored Responses completion không trả response id; không thể continuation an toàn.");

        var calls = ResolveFunctionCalls(outputItems);
        var hasMessage = outputItems.Any(IsAssistantMessage);
        if (!hasMessage && calls.Count == 0)
            throw new IOException("Responses hoàn tất nhưng không trả assistant message hoặc function call.");

        _completedOutputItems.Clear();
        if (_stateMode == OpenAiResponsesStateMode.Stateless)
            _completedOutputItems.AddRange(outputItems);
        else
            _previousResponseId = responseId;
        _pendingCalls = calls;

        if (usage is not null) yield return AgentTransportEvent.Meter(usage);
        foreach (var call in calls) yield return AgentTransportEvent.Tool(call);
        yield return AgentTransportEvent.Complete(responseId, calls.Count > 0 ? "tool_calls" : "stop");
    }

    private JsonObject BuildPayload()
    {
        var stored = _stateMode == OpenAiResponsesStateMode.StoredContinuation;
        var payload = new JsonObject
        {
            ["model"] = _profile.Model,
            ["input"] = _requestInput.DeepClone(),
            ["stream"] = true,
            ["store"] = stored
        };
        if (stored && _previousResponseId is { Length: > 0 })
            payload["previous_response_id"] = _previousResponseId;

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

        if (!stored)
        {
            // Stateless reasoning workflows must return encrypted reasoning items so the next request
            // can replay them without server-side response storage.
            payload["include"] = new JsonArray("reasoning.encrypted_content");
        }
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
        {
            var url = "data:" + image.MimeType + ";base64," + Convert.ToBase64String(image.Data);
            parts.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = url });
        }
        foreach (var file in message.Files ?? [])
        {
            var url = "data:" + file.MimeType + ";base64," + Convert.ToBase64String(file.Data);
            parts.Add(new JsonObject
            {
                ["type"] = "input_file",
                ["filename"] = file.Name,
                ["file_data"] = url
            });
        }
        return new JsonObject { ["role"] = role, ["content"] = parts };
    }

    private JsonArray BuildTools()
    {
        var result = new JsonArray();
        foreach (var tool in _tools.Values)
        {
            result.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = JsonNode.Parse(tool.Parameters.GetRawText())
            });
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

    private static List<AgentTransportToolCall> ResolveFunctionCalls(IEnumerable<string> outputItems)
    {
        var calls = new List<AgentTransportToolCall>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in outputItems)
        {
            using var json = JsonDocument.Parse(raw);
            var item = json.RootElement;
            if (String(item, "type") != "function_call") continue;
            var callId = String(item, "call_id");
            var name = String(item, "name");
            var arguments = String(item, "arguments");
            if (string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(name) || arguments is null)
                throw new InvalidDataException("Responses function_call thiếu call_id/name/arguments.");
            using var parsed = JsonDocument.Parse(arguments);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Responses function arguments phải giải mã thành JSON object.");
            if (!ids.Add(callId)) throw new InvalidDataException("Responses trả trùng call_id trong cùng response.");
            calls.Add(new(callId, name, parsed.RootElement.GetRawText()));
        }
        return calls;
    }

    private static bool IsAssistantMessage(string raw)
    {
        try
        {
            using var json = JsonDocument.Parse(raw);
            var root = json.RootElement;
            return String(root, "type") == "message" && String(root, "role") == "assistant";
        }
        catch (JsonException) { return false; }
    }

    private static AgentTransportUsage? ReadUsage(JsonElement response)
    {
        if (!response.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        long? cached = null;
        long? cacheWrite = null;
        if (usage.TryGetProperty("input_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
        {
            cached = Long(details, "cached_tokens");
            cacheWrite = Long(details, "cache_write_tokens");
        }
        return new(
            InputTokens: Long(usage, "input_tokens"),
            CachedInputTokens: cached,
            CacheWriteInputTokens: cacheWrite,
            OutputTokens: Long(usage, "output_tokens"),
            TotalTokens: Long(usage, "total_tokens"));
    }

    private static long? Long(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) && number >= 0 ? number : null;

    private static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ReadIncompleteReason(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("incomplete_details", out var details) || details.ValueKind != JsonValueKind.Object)
            return null;
        return String(details, "reason");
    }

    private static string? NormalizeCacheKey(string? key)
    {
        key = key?.Trim();
        if (string.IsNullOrEmpty(key)) return null;
        if (key.Length > 64) throw new ArgumentException("Prompt cache key vượt 64 ký tự.", nameof(key));
        return key;
    }

    private static void ValidateSummaryRequest(AiProfile profile)
    {
        if (!profile.RequestReasoningSummary) return;
        if (!AiModelCapabilities.IsOfficialOpenAi(profile))
            throw new InvalidOperationException("Reasoning summary chỉ được bật cho OpenAI Responses chính thức đã xác nhận.");
    }

    private static void ValidateStateMode(AiProfile profile, OpenAiResponsesStateMode stateMode)
    {
        if (!Enum.IsDefined(stateMode)) throw new ArgumentOutOfRangeException(nameof(stateMode));
        if (stateMode == OpenAiResponsesStateMode.StoredContinuation && !AiModelCapabilities.IsOfficialOpenAi(profile))
            throw new InvalidOperationException("Provider-state continuation hiện chỉ được bật rõ ràng cho OpenAI Responses chính thức; endpoint tương thích khác giữ chế độ stateless.");
    }

    private void EnsureUsable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OpenAiResponsesTransport));
        _lifetime.Token.ThrowIfCancellationRequested();
    }
}

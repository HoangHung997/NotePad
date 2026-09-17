using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace H2Notes.Core;

public enum AiProtocol { Ollama, OpenAiResponses, OpenAiChat, Gemini }

public enum AiStreamEventKind { Text, Reasoning, ReasoningSummary }

// Only Text belongs in assistant history. Reasoning events are transient UI data,
// never replayed as AiTurn content, persisted, or interpreted as final answers.
public sealed record AiStreamEvent(AiStreamEventKind Kind, string Text);

public sealed class AiProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Ollama trên máy";
    public AiProtocol Protocol { get; set; }
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 180;
    public bool WaitForCompletion { get; set; } = true;
    // Independent from effort; only requests the provider's public summary.
    public bool RequestReasoningSummary { get; set; }
    public string ReasoningEffort { get; set; } = "";
    // Null preserves the model default; explicit booleans require a model that supports them.
    public bool? OllamaThinking { get; set; }
    internal AiProfile Copy() => (AiProfile)MemberwiseClone();
    [System.Text.Json.Serialization.JsonIgnore] public bool IsOllamaCloud => Protocol == AiProtocol.Ollama &&
        (Model.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase) || Model.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase)
         || Uri.TryCreate(BaseUrl, UriKind.Absolute, out var endpoint) && endpoint.Host.Equals("ollama.com", StringComparison.OrdinalIgnoreCase));
    [System.Text.Json.Serialization.JsonIgnore] public string ProcessingLocation => Protocol != AiProtocol.Ollama ? "API online"
        : IsOllamaCloud ? "Ollama Cloud · xử lý online"
        : !Uri.TryCreate(BaseUrl, UriKind.Absolute, out var endpoint) ? "Ollama · địa chỉ chưa hợp lệ"
        : AiClient.IsLocalMachine(endpoint) ? "Ollama · chạy trên máy"
        : AiClient.IsPrivateLan(endpoint) ? "Ollama · máy chủ LAN (không trên máy này)"
        : "Ollama · máy chủ từ xa";
    public override string ToString() => Name;
}

public sealed class AiConnectionSettings
{
    public List<AiProfile> Profiles { get; set; } = [new()];
    public Guid? SelectedId { get; set; }
    public AiPdfSettings Pdf { get; set; } = new();
    public List<Guid> ProjectAccessConversationIds { get; set; } = [];
}

public sealed record AiImage(string MimeType, byte[] Data);
public sealed record AiFile(string Name, string MimeType, byte[] Data);
public sealed record AiTurn(string Role, string Content, IReadOnlyList<AiImage>? Images = null,
    IReadOnlyList<AiFile>? Files = null);

// Keys are supplied for a request, never retained in profiles or project documents.
public sealed class AiClient : IDisposable
{
    private readonly HttpClient _http;
    public AiClient(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static Uri Endpoint(AiProfile profile, string path)
    {
        if (!Uri.TryCreate(profile.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var root)
            || root.UserInfo.Length > 0 || root.Query.Length > 0 || root.Fragment.Length > 0
            || (root.Scheme != "https" && !(profile.Protocol == AiProtocol.Ollama && root.Scheme == "http" && (IsLocalMachine(root) || IsPrivateLan(root)))))
            throw new InvalidOperationException("Địa chỉ cần dùng HTTPS. Ollama được dùng HTTP với localhost hoặc IP LAN riêng do bạn cấu hình; không dùng HTTP với máy chủ công cộng.");
        var endpoint = new Uri(root, path);
        if (endpoint.Scheme != root.Scheme || endpoint.IdnHost != root.IdnHost || endpoint.Port != root.Port
            || endpoint.UserInfo.Length > 0 || endpoint.Fragment.Length > 0)
            throw new InvalidOperationException("Đường dẫn API không được đổi máy chủ nhận dữ liệu hoặc chứa thông tin đăng nhập.");
        return endpoint;
    }

    internal static bool IsLocalMachine(Uri endpoint) => endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || Address(endpoint) is { } address && IPAddress.IsLoopback(address);

    internal static bool IsPrivateLan(Uri endpoint)
    {
        // Literal addresses only: never resolve arbitrary hostnames to authorize HTTP.
        if (Address(endpoint) is not { } address) return false;
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
            : address.AddressFamily == AddressFamily.InterNetworkV6 && ((bytes[0] & 0xfe) == 0xfc || address.IsIPv6LinkLocal);
    }

    private static IPAddress? Address(Uri endpoint)
    {
        if (!IPAddress.TryParse(endpoint.DnsSafeHost, out var address)) return null;
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    private static void ValidateSummaryRequest(AiProfile profile)
    {
        if (!profile.RequestReasoningSummary) return;
        var endpoint = Endpoint(profile, "");
        var supported = endpoint.Scheme == "https" && endpoint.IsDefaultPort && (profile.Protocol switch
        {
            AiProtocol.OpenAiResponses => endpoint.IdnHost == "api.openai.com" && endpoint.AbsolutePath == "/v1/",
            AiProtocol.Gemini => endpoint.IdnHost == "generativelanguage.googleapis.com" && endpoint.AbsolutePath is "/v1/" or "/v1beta/",
            _ => false
        });
        if (!supported)
            throw new InvalidOperationException("Chỉ yêu cầu tóm tắt suy luận với model hỗ trợ trên API OpenAI Responses hoặc Gemini chính thức. Tắt tùy chọn này cho endpoint khác; nội dung suy luận máy chủ tự gửi vẫn được hiển thị.");
    }

    private static HttpRequestMessage Request(AiProfile profile, string key, HttpMethod method, string path, object? payload = null)
    {
        var request = new HttpRequestMessage(method, Endpoint(profile, path));
        if (!string.IsNullOrWhiteSpace(key) && profile.Protocol != AiProtocol.Ollama)
        {
            if (profile.Protocol == AiProtocol.Gemini) request.Headers.Add("x-goog-api-key", key);
            else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        if (payload is not null) request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return request;
    }

    public async Task<IReadOnlyList<string>> ListModels(AiProfile profile, string key, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var path = profile.Protocol == AiProtocol.Ollama ? "api/tags" : "models";
        using var request = Request(profile, key, HttpMethod.Get, path);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); await AiFailure.CheckResponse(response, timeout.Token);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var array = profile.Protocol == AiProtocol.OpenAiChat || profile.Protocol == AiProtocol.OpenAiResponses ? "data" : "models";
        var property = profile.Protocol == AiProtocol.Ollama || profile.Protocol == AiProtocol.Gemini ? "name" : "id";
        if (!json.RootElement.TryGetProperty(array, out var models)) throw new InvalidDataException("Máy chủ không trả danh sách model đúng giao thức.");
        return models.EnumerateArray().Select(m => m.GetProperty(property).GetString() ?? "").Select(n => n.StartsWith("models/") ? n[7..] : n).Where(n => n.Length > 0).Order().ToArray();
    }

    public async Task SetOllamaLoaded(AiProfile profile, bool load, CancellationToken token = default)
    {
        if (profile.Protocol != AiProtocol.Ollama || string.IsNullOrWhiteSpace(profile.Model)) throw new InvalidOperationException("Chọn model Ollama đã cài trước.");
        if (profile.IsOllamaCloud) throw new InvalidOperationException("Model cloud không được nạp vào RAM máy này. Chỉ nạp/giải phóng model local.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var request = Request(profile, "", HttpMethod.Post, "api/generate", new { model = profile.Model, stream = false, keep_alive = load ? "10m" : "0" });
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); await AiFailure.CheckResponse(response, timeout.Token);
    }

    public async Task<IReadOnlyList<string>> LoadedOllamaModels(AiProfile profile, CancellationToken token = default)
    {
        if (profile.Protocol != AiProtocol.Ollama) throw new InvalidOperationException("Chỉ áp dụng cho Ollama.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = Request(profile, "", HttpMethod.Get, "api/ps");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); await AiFailure.CheckResponse(response, timeout.Token);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        return json.RootElement.GetProperty("models").EnumerateArray().Select(m => String(m, "name") ?? "").Where(n => n.Length > 0).ToArray();
    }

    public async IAsyncEnumerable<string> Stream(AiProfile profile, string key, IReadOnlyList<AiTurn> turns,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        await foreach (var item in StreamEvents(profile, key, turns, token))
            if (item.Kind == AiStreamEventKind.Text) yield return item.Text;
    }

    public async IAsyncEnumerable<AiStreamEvent> StreamEvents(AiProfile profile, string key, IReadOnlyList<AiTurn> turns,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(profile.Model)) throw new InvalidOperationException("Chưa chọn model. Mở Thiết lập AI trước.");
        token.ThrowIfCancellationRequested();
        ValidateSummaryRequest(profile);
        AiPdf.ValidateRequest(profile, turns);
        var effort = AiModelCapabilities.ResolveReasoningEffort(profile);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        // A slow model may spend minutes loading or thinking without sending a token.
        // Default to explicit user cancellation, not an absolute generation deadline.
        var idleLimit = profile.WaitForCompletion ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromSeconds(Math.Clamp(profile.TimeoutSeconds, 10, 1800));
        timeout.CancelAfter(idleLimit);
        object payload; string path;
        object Parts(AiTurn turn, AiProtocol protocol)
        {
            var parts = new List<object>();
            parts.Add(protocol == AiProtocol.Gemini ? new { text = turn.Content } : (object)new { type = protocol == AiProtocol.OpenAiResponses ? "input_text" : "text", text = turn.Content });
            foreach (var image in turn.Images ?? [])
            {
                var data = Convert.ToBase64String(image.Data); var url = "data:" + image.MimeType + ";base64," + data;
                parts.Add(protocol switch
                {
                    AiProtocol.Gemini => new { inline_data = new { mime_type = image.MimeType, data } },
                    AiProtocol.OpenAiResponses => (object)new { type = "input_image", image_url = url },
                    _ => new { type = "image_url", image_url = new { url } }
                });
            }
            foreach (var file in turn.Files ?? [])
            {
                var data = Convert.ToBase64String(file.Data);
                var url = "data:" + file.MimeType + ";base64," + data;
                parts.Add(protocol switch
                {
                    AiProtocol.Gemini => new { inlineData = new { mimeType = file.MimeType, data } },
                    AiProtocol.OpenAiResponses => (object)new { type = "input_file", filename = file.Name, file_data = url },
                    _ => new { type = "file", file = new { filename = file.Name, file_data = url } }
                });
            }
            return parts;
        }
        var messages = turns.Select(t => new { role = t.Role, content = t.Images is { Count: > 0 } || t.Files is { Count: > 0 } ? Parts(t, profile.Protocol) : t.Content }).ToArray();
        switch (profile.Protocol)
        {
            case AiProtocol.Ollama:
                path = "api/chat";
                var ollamaPayload = new Dictionary<string, object>
                {
                    ["model"] = profile.Model,
                    ["messages"] = turns.Select(t => new { role = t.Role, content = t.Content, images = t.Images?.Select(i => Convert.ToBase64String(i.Data)).ToArray() ?? [] }),
                    ["stream"] = true
                };
                if (effort is not null) ollamaPayload["think"] = effort;
                else if (profile.OllamaThinking is { } think) ollamaPayload["think"] = think;
                payload = ollamaPayload;
                break;
            case AiProtocol.OpenAiResponses:
                path = "responses";
                var responsesPayload = new Dictionary<string, object> { ["model"] = profile.Model, ["input"] = messages, ["stream"] = true, ["store"] = false };
                var reasoning = new Dictionary<string, object>();
                if (profile.RequestReasoningSummary) reasoning["summary"] = "auto";
                if (effort is not null) reasoning["effort"] = effort;
                if (reasoning.Count > 0) responsesPayload["reasoning"] = reasoning;
                payload = responsesPayload;
                break;
            case AiProtocol.Gemini:
                path = "models/" + Uri.EscapeDataString(profile.Model) + ":streamGenerateContent?alt=sse";
                var geminiPayload = new Dictionary<string, object>
                {
                    ["system_instruction"] = new { parts = new[] { new { text = string.Join("\n", turns.Where(t => t.Role == "system").Select(t => t.Content)) } } },
                    ["contents"] = turns.Where(t => t.Role != "system").Select(t => new { role = t.Role == "assistant" ? "model" : "user", parts = Parts(t, AiProtocol.Gemini) }).ToArray()
                };
                var thinkingConfig = new Dictionary<string, object>();
                if (profile.RequestReasoningSummary) thinkingConfig["includeThoughts"] = true;
                if (effort is not null) thinkingConfig["thinkingLevel"] = effort;
                if (thinkingConfig.Count > 0) geminiPayload["generationConfig"] = new { thinkingConfig };
                payload = geminiPayload;
                break;
            default:
                path = "chat/completions";
                var chatPayload = new Dictionary<string, object> { ["model"] = profile.Model, ["messages"] = messages, ["stream"] = true };
                if (effort is not null) chatPayload["reasoning_effort"] = effort;
                payload = chatPayload;
                break;
        }
        using var request = Request(profile, key, HttpMethod.Post, path, payload);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); await AiFailure.CheckResponse(response, timeout.Token);
        timeout.CancelAfter(idleLimit);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);
        var finished = false;
        var receivedText = false;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            timeout.Token.ThrowIfCancellationRequested();
            // Reasoning, answer tokens and SSE heartbeats all prove the connection is alive.
            timeout.CancelAfter(idleLimit);
            if (line.Length == 0 || line.StartsWith(':')) continue;
            if (profile.Protocol != AiProtocol.Ollama)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                line = line[5..].Trim();
                if (line == "[DONE]") { finished = true; break; }
            }
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.TryGetProperty("error", out _) || String(root, "type") is "error" or "response.failed" or "response.incomplete")
                throw new HttpRequestException("Model báo lỗi hoặc phản hồi chưa hoàn tất. Phần đã nhận được giữ lại; không tự gửi lại.");
            switch (profile.Protocol)
            {
                case AiProtocol.Ollama:
                    if (root.TryGetProperty("message", out var message))
                    {
                        if (String(message, "thinking") is { Length: > 0 } thinking) yield return new(AiStreamEventKind.Reasoning, thinking);
                        timeout.Token.ThrowIfCancellationRequested();
                        if (String(message, "content") is { Length: > 0 } local) { receivedText = true; yield return new(AiStreamEventKind.Text, local); }
                    }
                    if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True) finished = true;
                    break;
                case AiProtocol.OpenAiResponses:
                    if (String(root, "type") == "response.reasoning_summary_text.delta" && String(root, "delta") is { Length: > 0 } summary)
                        yield return new(AiStreamEventKind.ReasoningSummary, summary);
                    if (String(root, "type") == "response.output_text.delta" && String(root, "delta") is { Length: > 0 } delta) { receivedText = true; yield return new(AiStreamEventKind.Text, delta); }
                    if (String(root, "type") == "response.completed") finished = true;
                    break;
                case AiProtocol.Gemini:
                    if (root.TryGetProperty("candidates", out var candidates))
                        foreach (var candidate in candidates.EnumerateArray().Take(1))
                        {
                            if (candidate.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
                                foreach (var part in parts.EnumerateArray())
                                {
                                    timeout.Token.ThrowIfCancellationRequested();
                                    if (String(part, "text") is not { Length: > 0 } text) continue;
                                    var thought = part.TryGetProperty("thought", out var flag) && flag.ValueKind == JsonValueKind.True;
                                    if (!thought) receivedText = true;
                                    yield return new(thought ? AiStreamEventKind.ReasoningSummary : AiStreamEventKind.Text, text);
                                }
                            if (String(candidate, "finishReason") is { Length: > 0 }) finished = true;
                        }
                    break;
                default:
                    if (root.TryGetProperty("choices", out var choices))
                        foreach (var choice in choices.EnumerateArray().Take(1))
                        {
                            if (choice.TryGetProperty("delta", out var part))
                            {
                                // Some compatible providers expose both aliases; prefer
                                // reasoning_content rather than duplicating the same trace.
                                var reasoning = String(part, "reasoning_content");
                                if (string.IsNullOrEmpty(reasoning)) reasoning = String(part, "reasoning");
                                if (reasoning is { Length: > 0 }) yield return new(AiStreamEventKind.Reasoning, reasoning);
                                timeout.Token.ThrowIfCancellationRequested();
                                if (String(part, "content") is { Length: > 0 } text) { receivedText = true; yield return new(AiStreamEventKind.Text, text); }
                            }
                            if (String(choice, "finish_reason") is { Length: > 0 }) finished = true;
                        }
                    break;
            }
            if (finished) break;
        }
        timeout.Token.ThrowIfCancellationRequested();
        if (!finished) throw new IOException("Kết nối kết thúc trước khi model hoàn tất. Đã giữ lại phần trả lời nhận được.");
        if (!receivedText) throw new AiServiceException("empty_reply", "Máy chủ kết thúc nhưng không trả nội dung văn bản. Kiểm tra model có hỗ trợ chat văn bản hoặc yêu cầu bị chặn; app không tự gửi lại.");
    }

    private static string? String(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    public void Dispose() => _http.Dispose();
}

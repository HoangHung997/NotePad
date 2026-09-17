using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using H2Notes.Core;

namespace H2AgentLab.Metrics;

/// <summary>
/// Deliberately minimal no-tool baseline used to measure provider/model latency outside the Lab
/// agent harness. This is measurement code, not the v2 production transport. Phase 02 replaces
/// duplicated wire handling with IAgentTransport.
/// </summary>
public sealed class RawModelBaselineProbe : IDisposable
{
    private readonly HttpClient _http;
    public RawModelBaselineProbe(HttpMessageHandler? handler = null)
        => _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<string> Run(AiProfile profile, string apiKey, string prompt, AgentRunTelemetry telemetry, CancellationToken ct)
    {
        if (profile.Protocol is not (AiProtocol.Ollama or AiProtocol.OpenAiChat))
            throw new InvalidOperationException("Raw v1 baseline currently measures Ollama or Chat Completions only; Responses is added in Phase 02.");
        if (string.IsNullOrWhiteSpace(profile.Model)) throw new InvalidOperationException("Model is required.");
        ArgumentNullException.ThrowIfNull(telemetry);
        var trace = telemetry.Trace;
        var metrics = telemetry.Metrics;
        trace.Mark(AgentTraceKind.Send, "raw-baseline");
        trace.Mark(AgentTraceKind.ContextReady, "raw-user-only");
        var local = profile.Protocol == AiProtocol.Ollama;
        var payload = new Dictionary<string, object?>
        {
            ["model"] = profile.Model,
            ["messages"] = new[] { new { role = "user", content = prompt } },
            ["stream"] = true
        };
        if (local)
        {
            if (profile.OllamaThinking is { } think) payload["think"] = think;
        }
        else
        {
            var effort = AiModelCapabilities.ResolveReasoningEffort(profile);
            if (effort is not null) payload["reasoning_effort"] = effort;
            var root = AiClient.Endpoint(profile, "");
            if (root.Scheme == "https" && root.IdnHost == "api.openai.com" && root.AbsolutePath == "/v1/")
                payload["stream_options"] = new { include_usage = true };
        }
        var payloadText = JsonSerializer.Serialize(payload);
        metrics.IncrementModelCalls();
        metrics.AddTraffic(bytesSent: Encoding.UTF8.GetByteCount(payloadText));
        trace.Mark(AgentTraceKind.RequestStart, "raw-request");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, AiClient.Endpoint(profile, local ? "api/chat" : "chat/completions"));
            if (!local && !string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(payloadText, Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            trace.Mark(AgentTraceKind.ConnectionReady, "raw-response-headers");
            if (!response.IsSuccessStatusCode) throw new IOException("Raw baseline provider returned HTTP " + (int)response.StatusCode + ".");
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
            var answer = new StringBuilder();
            var first = true;
            var finished = false;
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                metrics.AddTraffic(bytesReceived: Encoding.UTF8.GetByteCount(line));
                if (line.Length == 0 || line.StartsWith(':')) continue;
                if (!local)
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    line = line[5..].Trim();
                    if (line == "[DONE]") { finished = true; break; }
                }
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (first)
                {
                    first = false;
                    trace.Mark(AgentTraceKind.FirstModelEvent, "raw-first-event");
                }
                if (root.TryGetProperty("error", out _)) throw new IOException("Raw baseline provider reported an error.");
                if (local)
                {
                    if (root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                        answer.Append(content.GetString());
                    if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
                    {
                        finished = true;
                        metrics.AddUsage(
                            inputTokens: NonNegativeLong(root, "prompt_eval_count"),
                            cachedInputTokens: NonNegativeLong(root, "prompt_eval_cached_count"),
                            outputTokens: NonNegativeLong(root, "eval_count"));
                    }
                }
                else
                {
                    if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        var input = NonNegativeLong(usage, "prompt_tokens");
                        var output = NonNegativeLong(usage, "completion_tokens");
                        var cached = usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object
                            ? NonNegativeLong(details, "cached_tokens") : 0;
                        metrics.AddUsage(inputTokens: input, cachedInputTokens: cached, outputTokens: output);
                    }
                    if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) continue;
                    var choice = choices[0];
                    if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                        answer.Append(content.GetString());
                    if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(reason.GetString()))
                        finished = true;
                }
                if (local && finished) break;
            }
            if (!finished) throw new IOException("Raw baseline connection ended before completion.");
            trace.Mark(AgentTraceKind.Final, "raw-final");
            return answer.ToString();
        }
        catch (OperationCanceledException)
        {
            trace.Mark(AgentTraceKind.Cancel, "raw-cancel");
            throw;
        }
        catch (Exception ex)
        {
            trace.Mark(AgentTraceKind.Error, ex.GetType().Name);
            throw;
        }
    }

    private static long NonNegativeLong(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) && number >= 0 ? number : 0;

    public void Dispose() => _http.Dispose();
}

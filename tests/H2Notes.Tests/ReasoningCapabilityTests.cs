using System.Net;
using System.Text.Json;
using H2Notes.Core;

internal static class ReasoningCapabilityTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Reasoning common verified models expose exact supported effort values", () =>
        {
            foreach (var model in new[] { "gpt-5", "gpt-5-mini", "gpt-5-nano" })
                Check(AiModelCapabilities.GetReasoningOptions(Profile(AiProtocol.OpenAiResponses, model)).SequenceEqual(["minimal", "low", "medium", "high"]));
            Check(AiModelCapabilities.GetReasoningOptions(Profile(AiProtocol.OpenAiResponses, "gpt-5.1")).SequenceEqual(["none", "low", "medium", "high"]));
            foreach (var model in new[] { "gpt-5.2", "gpt-5.4" })
                Check(AiModelCapabilities.GetReasoningOptions(Profile(AiProtocol.OpenAiResponses, model)).SequenceEqual(["none", "low", "medium", "high", "xhigh"]));
            Check(AiModelCapabilities.GetReasoningOptions(Profile(AiProtocol.Gemini, "gemini-3.1-pro-preview")).SequenceEqual(["low", "medium", "high"]));
            Check(AiModelCapabilities.GetReasoningOptions(Profile(AiProtocol.Gemini, "gemini-3-pro-preview")).SequenceEqual(["low", "high"]));
            Check(AiModelCapabilities.GetReasoningOptions(Profile(AiProtocol.OpenAiResponses, "gemini-3-pro-preview")).Count == 0);
            Check(AiModelCapabilities.GetReasoningOptions(Profile(AiProtocol.Gemini, "gemini-3-flash-preview")).SequenceEqual(["minimal", "low", "medium", "high"]));
            Check(AiModelCapabilities.GetReasoningOptions(Profile(AiProtocol.Ollama, "gpt-oss:20b")).SequenceEqual(["low", "medium", "high"]));
        });
        test("Reasoning allowlist rejects model suffixes and impersonated official endpoints", () =>
        {
            foreach (var model in new[] { "gpt-5-custom", "gpt-5.2-pro", "gpt-4.1", "new-model", "gpt-5-2099-01-01" })
                Check(AiModelCapabilities.GetReasoningOptions(Profile(AiProtocol.OpenAiResponses, model)).Count == 0);
            foreach (var url in new[] { "https://api.openai.com.evil.test/v1", "https://api.openai.com:444/v1", "https://api.openai.com/other", "https://example.test/v1" })
            {
                var profile = Profile(AiProtocol.OpenAiResponses, "gpt-5"); profile.BaseUrl = url;
                Check(AiModelCapabilities.GetReasoningOptions(profile).Count == 0);
            }
        });
        test("Reasoning maximum maps to highest known wire value without mutating profile", () =>
        {
            var profile = Profile(AiProtocol.OpenAiResponses, "gpt-5.2"); profile.ReasoningEffort = "low";
            profile.RequestReasoningSummary = true; profile.OllamaThinking = false;
            var copy = AiModelCapabilities.WithReasoning(profile, "max");
            Check(!ReferenceEquals(copy, profile) && copy.Id == profile.Id && copy.BaseUrl == profile.BaseUrl);
            Check(copy.RequestReasoningSummary && copy.OllamaThinking == false && profile.ReasoningEffort == "low");
            Check(AiModelCapabilities.ResolveReasoningEffort(copy) == "xhigh");
            Check(AiModelCapabilities.ResolveReasoningEffort(AiModelCapabilities.WithReasoning(profile, null)) == "low");
            profile.Model = "unknown"; profile.ReasoningEffort = "max";
            Check(AiModelCapabilities.ResolveReasoningEffort(profile) is null);
            profile.Model = "gpt-6-astra";
            Check(AiModelCapabilities.ResolveReasoningEffort(profile) == "max");
        });
        foreach (var protocol in Enum.GetValues<AiProtocol>())
        {
            test("Reasoning emits only its verified protocol parameter: " + protocol, () =>
            {
                var profile = Profile(protocol, protocol == AiProtocol.Ollama ? "gpt-oss:20b" : protocol == AiProtocol.Gemini ? "gemini-3.1-pro-preview" : "gpt-5");
                profile.ReasoningEffort = "max";
                profile.RequestReasoningSummary = protocol is AiProtocol.Gemini or AiProtocol.OpenAiResponses;
                using var handler = new CapabilityStub(protocol); using var client = new AiClient(handler);
                Drain(client.StreamEvents(profile, "synthetic-key", [new("user", "test")])).GetAwaiter().GetResult();
                using var json = JsonDocument.Parse(handler.Body!);
                var root = json.RootElement;
                var wire = protocol switch
                {
                    AiProtocol.Ollama => root.GetProperty("think").GetString(),
                    AiProtocol.OpenAiResponses => root.GetProperty("reasoning").GetProperty("effort").GetString(),
                    AiProtocol.Gemini => root.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString(),
                    _ => root.GetProperty("reasoning_effort").GetString()
                };
                Check(wire == "high" && !handler.Body!.Contains("synthetic-key") && !handler.Body.Contains("encrypted"));
                if (protocol == AiProtocol.OpenAiResponses) Check(root.GetProperty("reasoning").GetProperty("summary").GetString() == "auto");
                if (protocol == AiProtocol.Gemini) Check(root.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("includeThoughts").GetBoolean());
            });
            test("Reasoning unknown maximum is display-only, no invented API parameters: " + protocol, () =>
            {
                var profile = Profile(protocol, "unknown"); profile.ReasoningEffort = "max";
                using var handler = new CapabilityStub(protocol); using var client = new AiClient(handler);
                Drain(client.StreamEvents(profile, "", [])).GetAwaiter().GetResult();
                using var json = JsonDocument.Parse(handler.Body!);
                foreach (var field in new[] { "reasoning", "reasoning_effort", "think", "generationConfig" }) Check(!json.RootElement.TryGetProperty(field, out _));
            });
        }
        test("Reasoning invalid explicit effort fails before transmitting", () =>
        {
            var profile = Profile(AiProtocol.OpenAiResponses, "gpt-5"); profile.ReasoningEffort = "ultra";
            using var handler = new CapabilityStub(profile.Protocol); using var client = new AiClient(handler);
            Throws<InvalidOperationException>(() => Drain(client.StreamEvents(profile, "", [])).GetAwaiter().GetResult());
            Check(handler.Calls == 0);
        });
        test("Reasoning and action audit metadata round trip with safe legacy defaults", () =>
        {
            var empty = JsonSerializer.Deserialize<AiConversation>("{}")!;
            Check(empty.PermissionMode == AiPermissionMode.ConfirmChanges && empty.ReasoningEffort is null);
            var conversation = new AiConversation { ReasoningEffort = "high", Messages = [new() { Content = "proposal", ProjectActionsAudit = "actual-only-audit" }] };
            Check(!AiHistory.RequestTurns(conversation).Single().Content.Contains("actual-only-audit"));
            conversation.Messages[0].ProjectActionsApplied = true;
            var copy = JsonSerializer.Deserialize<AiConversation>(JsonSerializer.Serialize(conversation))!;
            Check(copy.ReasoningEffort == "high" && AiHistory.RequestTurns(copy).Single().Content.Contains("actual-only-audit"));
        });
    }

    internal static AiProfile Profile(AiProtocol protocol, string model) => new()
    {
        Protocol = protocol, Model = model,
        BaseUrl = protocol == AiProtocol.Ollama ? "http://localhost:11434" : protocol == AiProtocol.Gemini ? "https://generativelanguage.googleapis.com/v1beta" : "https://api.openai.com/v1"
    };

    internal static async Task Drain(IAsyncEnumerable<AiStreamEvent> source) { await foreach (var _ in source) { } }
    internal static void Check(bool condition) { if (!condition) throw new Exception("Capability assertion failed."); }
    internal static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }

    internal sealed class CapabilityStub(AiProtocol protocol) : HttpMessageHandler
    {
        public string? Body;
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            Body = await request.Content!.ReadAsStringAsync(token);
            var response = protocol switch
            {
                AiProtocol.Ollama => "{\"message\":{\"content\":\"ok\"},\"done\":true}\n",
                AiProtocol.OpenAiResponses => "data: {\"type\":\"response.output_text.delta\",\"delta\":\"ok\"}\n\ndata: {\"type\":\"response.completed\"}\n\n",
                AiProtocol.Gemini => "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}\n\n",
                _ => "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n"
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }
}

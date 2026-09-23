using System.Net;
using System.Text;
using System.Text.Json;
using H2AgentLab.Transport;
using H2Notes.Core;

internal static class H2AgentSteeringWireTests
{
    public static void Run(Action<string, Action> test)
    {
        H2AgentOpenAiWireTests.Run(test);
        foreach (var kind in new[] { "ollama", "chat", "responses", "responses-stored" })
            test("Agent steering wire preserves user role and turn identity: " + kind, () => Task.Run(async () =>
            {
                var handler = new Handler(kind);
                var profile = new AiProfile { Model = "fixture", Protocol = kind == "ollama" ? AiProtocol.Ollama : kind == "chat" ? AiProtocol.OpenAiChat : AiProtocol.OpenAiResponses,
                    BaseUrl = kind == "ollama" ? "http://localhost:11434"
                    : kind == "responses-stored" ? "https://api.openai.com/v1" : "https://example.test/v1" };
                await using IAgentTransport transport = kind switch
                {
                    "ollama" => new OllamaTransport(profile, handler),
                    "chat" => new ChatCompletionsTransport(profile, "", handler),
                    _ => new OpenAiResponsesTransport(profile, "", handler, kind == "responses-stored"
                        ? OpenAiResponsesStateMode.StoredContinuation : OpenAiResponsesStateMode.Stateless)
                };
                var task = Guid.NewGuid(); var turn = Guid.NewGuid();
                await foreach (var _ in transport.StartAsync(new(task, turn, [new(AgentTransportMessageRole.User, "First")], []))) { }
                await foreach (var _ in transport.ContinueAsync(new(task, turn, [], SupplementalUserMessages: ["Follow-up"]))) { }
                using var wire = JsonDocument.Parse(handler.Bodies.Last());
                var items = wire.RootElement.GetProperty(kind.StartsWith("responses") ? "input" : "messages");
                var user = items.EnumerateArray().Last();
                if (user.GetProperty("role").GetString() != "user" || user.GetProperty("content").GetString() != "Follow-up")
                    throw new Exception("Supplement was not a user message");
                try
                {
                    await foreach (var _ in transport.ContinueAsync(new(task, Guid.NewGuid(), [], SupplementalUserMessages: ["Wrong turn"]))) { }
                    throw new Exception("Unrelated turn accepted");
                }
                catch (InvalidOperationException) { }
                if (handler.Bodies.Count != 2) throw new Exception("Rejected continuation reached provider");
            }).GetAwaiter().GetResult());
    }

    private sealed class Handler(string kind) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            var body = kind == "ollama" ? "{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"},\"done\":true}\n"
                : kind == "chat" ? "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
                : "data: {\"type\":\"response.output_text.delta\",\"delta\":\"OK\"}\n\ndata: " +
                    """{"type":"response.completed","response":{"id":"resp_fixture","status":"completed","output":[{"id":"msg_fixture","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"OK","annotations":[]}]}]}}""" + "\n\n";
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, kind == "ollama" ? "application/x-ndjson" : "text/event-stream") };
        }
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using H2Notes.Core;

namespace H2AgentLab.Transport;

public static class V2ChatCompletionsTransportTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var lines = new List<string>();
        var failed = 0;

        async Task Test(string name, Func<Task> action)
        {
            try
            {
                await action();
                lines.Add("PASS " + name);
            }
            catch (Exception ex)
            {
                failed++;
                lines.Add("FAIL " + name + ": " + ex.Message);
            }
        }

        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        static AiProfile Profile(string baseUrl = "https://example.invalid/v1") => new()
        {
            Protocol = AiProtocol.OpenAiChat,
            BaseUrl = baseUrl,
            Model = "fixture"
        };

        static AgentTransportStartRequest StartRequest(params AgentTransportMessage[] messages)
            => new(Guid.NewGuid(), Guid.NewGuid(), messages, Array.Empty<AgentToolDefinition>());

        await Test("Chat direct stream preserves reasoning and text with bearer auth", async () =>
        {
            var handler = new QueueHandler(
                Sse(
                    """{"choices":[{"delta":{"reasoning_content":"trace ","content":"Xin "},"finish_reason":null}]}""",
                    """{"choices":[{"delta":{"content":"chào"},"finish_reason":"stop"}]}"""));
            await using var transport = new ChatCompletionsTransport(Profile(), "test-key", handler);
            var events = await Collect(transport.StartAsync(StartRequest(new AgentTransportMessage(AgentTransportMessageRole.User, "hello"))));

            Check(string.Concat(events.Where(e => e.Kind == AgentTransportEventKind.ReasoningDelta).Select(e => e.Text)) == "trace ", "Reasoning alias was not surfaced separately.");
            Check(string.Concat(events.Where(e => e.Kind == AgentTransportEventKind.TextDelta).Select(e => e.Text)) == "Xin chào", "Text deltas were not preserved.");
            Check(events.Last().Kind == AgentTransportEventKind.Completed && events.Last().FinishReason == "stop", "Direct response did not complete with stop.");
            Check(handler.Requests.Count == 1 && handler.AuthorizationParameters.Single() == "test-key", "Bearer API key was not applied exactly once.");
            using var payload = JsonDocument.Parse(handler.Requests[0]);
            Check(payload.RootElement.GetProperty("model").GetString() == "fixture", "Model did not reach request.");
            Check(payload.RootElement.GetProperty("stream").GetBoolean(), "Streaming was not enabled.");
        });

        await Test("Fragmented tool call preserves provider id arguments and continuation result", async () =>
        {
            var handler = new QueueHandler(
                Sse(
                    """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_abc","type":"function","function":{"name":"read_","arguments":"{\"path\":"}}]},"finish_reason":null}]}""",
                    """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"file","arguments":"\"a.txt\"}"}}]},"finish_reason":"tool_calls"}]}"""),
                Sse("""{"choices":[{"delta":{"content":"Verified answer"},"finish_reason":"stop"}]}"""));

            var schema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { path = new { type = "string" } },
                required = new[] { "path" },
                additionalProperties = false
            });
            var taskId = Guid.NewGuid();
            var turnId = Guid.NewGuid();
            var start = new AgentTransportStartRequest(taskId, turnId,
                [new(AgentTransportMessageRole.User, "Read file")],
                [new("read_file", "Read a text file", schema)]);

            await using var transport = new ChatCompletionsTransport(Profile(), "k", handler);
            var first = await Collect(transport.StartAsync(start));
            var call = first.Single(e => e.Kind == AgentTransportEventKind.ToolCall).ToolCall!;
            Check(call.Id == "call_abc", "Provider tool call id changed.");
            Check(call.Name == "read_file", "Fragmented function name was not assembled.");
            using (var args = JsonDocument.Parse(call.ArgumentsJson))
                Check(args.RootElement.GetProperty("path").GetString() == "a.txt", "Fragmented tool arguments were not assembled.");

            var second = await Collect(transport.ContinueAsync(new(taskId, turnId,
                [new(call.Id, call.Name, "{\"content\":\"hello\"}")])));
            Check(string.Concat(second.Where(e => e.Kind == AgentTransportEventKind.TextDelta).Select(e => e.Text)) == "Verified answer", "Continuation final answer mismatch.");
            Check(handler.Requests.Count == 2, "Expected exactly two model requests.");

            using var continuation = JsonDocument.Parse(handler.Requests[1]);
            var messages = continuation.RootElement.GetProperty("messages");
            Check(messages.GetArrayLength() == 3, "Continuation transcript should contain user, assistant tool call and tool result.");
            var assistant = messages[1];
            var nativeCall = assistant.GetProperty("tool_calls")[0];
            Check(nativeCall.GetProperty("id").GetString() == "call_abc", "Assistant tool call id was not replayed.");
            Check(nativeCall.GetProperty("function").GetProperty("name").GetString() == "read_file", "Assistant tool name was not replayed.");
            Check(nativeCall.GetProperty("function").GetProperty("arguments").GetString()!.Contains("a.txt", StringComparison.Ordinal), "Assistant tool arguments were not replayed as Chat Completions JSON string.");
            var toolResult = messages[2];
            Check(toolResult.GetProperty("role").GetString() == "tool" && toolResult.GetProperty("tool_call_id").GetString() == "call_abc", "Tool result did not preserve call id.");
            Check(toolResult.GetProperty("content").GetString()!.Contains("hello", StringComparison.Ordinal), "Tool result content missing from continuation.");
        });

        await Test("Chat multimodal request preserves H2 image and file wire shapes", async () =>
        {
            var handler = new QueueHandler(Sse("""{"choices":[{"delta":{"content":"ok"},"finish_reason":"stop"}]}"""));
            await using var transport = new ChatCompletionsTransport(Profile(), "", handler);
            var request = StartRequest(new AgentTransportMessage(AgentTransportMessageRole.User, "inspect",
                [new AiImage("image/png", [1, 2, 3])],
                [new AiFile("brief.pdf", "application/pdf", [4, 5, 6])]));
            _ = await Collect(transport.StartAsync(request));

            using var payload = JsonDocument.Parse(handler.Requests.Single());
            var parts = payload.RootElement.GetProperty("messages")[0].GetProperty("content");
            Check(parts.GetArrayLength() == 3, "Expected text + image + file parts.");
            Check(parts[1].GetProperty("type").GetString() == "image_url", "Image part type mismatch.");
            Check(parts[1].GetProperty("image_url").GetProperty("url").GetString()!.StartsWith("data:image/png;base64,", StringComparison.Ordinal), "Image data URL missing.");
            Check(parts[2].GetProperty("type").GetString() == "file", "File part type mismatch.");
            Check(parts[2].GetProperty("file").GetProperty("filename").GetString() == "brief.pdf", "File name missing.");
            Check(parts[2].GetProperty("file").GetProperty("file_data").GetString()!.StartsWith("data:application/pdf;base64,", StringComparison.Ordinal), "File data URL missing.");
        });

        await Test("Chat transport rejects truncated completion before exposing tool call", async () =>
        {
            var handler = new QueueHandler(Sse(
                """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_cut","function":{"name":"read_file","arguments":"{}"}}]},"finish_reason":"length"}]}"""));
            var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });
            await using var transport = new ChatCompletionsTransport(Profile(), "", handler);
            var request = new AgentTransportStartRequest(Guid.NewGuid(), Guid.NewGuid(),
                [new(AgentTransportMessageRole.User, "read")], [new("read_file", "read", schema)]);
            var seenTools = 0;
            try
            {
                await foreach (var item in transport.StartAsync(request))
                    if (item.Kind == AgentTransportEventKind.ToolCall) seenTools++;
                throw new InvalidOperationException("Truncated response was accepted.");
            }
            catch (IOException ex)
            {
                Check(ex.Message.Contains("length", StringComparison.Ordinal), "Expected length failure.");
            }
            Check(seenTools == 0, "Truncated tool proposal must not be exposed for execution.");
        });

        await Test("Chat transport reuses H2 endpoint safety and explicit capability profile", async () =>
        {
            var handler = new QueueHandler(Sse("""{"choices":[{"delta":{"content":"unused"},"finish_reason":"stop"}]}"""));
            await using var transport = new ChatCompletionsTransport(Profile("http://8.8.8.8/v1"), "", handler);
            Check(transport.Capabilities == AgentTransportCapabilities.ChatCompletionsFallback, "Capability profile changed.");
            try
            {
                _ = await Collect(transport.StartAsync(StartRequest(new AgentTransportMessage(AgentTransportMessageRole.User, "x"))));
                throw new InvalidOperationException("Unsafe HTTP endpoint was accepted.");
            }
            catch (InvalidOperationException ex)
            {
                Check(ex.Message.Contains("HTTPS", StringComparison.OrdinalIgnoreCase), "Expected H2 endpoint safety rejection.");
            }
            Check(handler.Requests.Count == 0, "Unsafe endpoint reached HTTP handler.");
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(outputDirectory, "v2-chat-completions-transport-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join("\n", lines));
        return failed == 0 ? 0 : 1;
    }

    private static async Task<List<AgentTransportEvent>> Collect(IAsyncEnumerable<AgentTransportEvent> stream)
    {
        var result = new List<AgentTransportEvent>();
        await foreach (var item in stream) result.Add(item);
        return result;
    }

    private static string Sse(params string[] chunks)
        => string.Join("\n", chunks.Select(chunk => "data: " + chunk)) + "\ndata: [DONE]\n";

    private sealed class QueueHandler(params string[] responses) : HttpMessageHandler
    {
        private int _next;
        public List<string> Requests { get; } = [];
        public List<string?> AuthorizationParameters { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            AuthorizationParameters.Add(request.Headers.Authorization?.Parameter);
            if (_next >= responses.Length) throw new InvalidOperationException("Unexpected extra HTTP request.");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses[_next++], Encoding.UTF8, "text/event-stream")
            };
        }
    }
}

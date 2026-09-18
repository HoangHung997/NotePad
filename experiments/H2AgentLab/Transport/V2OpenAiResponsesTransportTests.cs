using System.Net;
using System.Text;
using System.Text.Json;
using H2Notes.Core;

namespace H2AgentLab.Transport;

public static class V2OpenAiResponsesTransportTests
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
            Protocol = AiProtocol.OpenAiResponses,
            BaseUrl = baseUrl,
            Model = "fixture"
        };

        await Test("Responses HTTP streams summary text usage and public request controls", async () =>
        {
            var handler = new QueueHandler(Sse(
                """{"type":"response.created","response":{"id":"resp_1","status":"in_progress"}}""",
                """{"type":"response.reasoning_summary_text.delta","delta":"Tóm tắt "}""",
                """{"type":"response.output_text.delta","delta":"Xin chào"}""",
                """{"type":"response.output_item.done","output_index":0,"item":{"id":"msg_1","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"Xin chào","annotations":[]}]}}""",
                """{"type":"response.completed","response":{"id":"resp_1","status":"completed","output":[{"id":"msg_1","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"Xin chào","annotations":[]}]}],"usage":{"input_tokens":100,"input_tokens_details":{"cached_tokens":60,"cache_write_tokens":4},"output_tokens":12,"total_tokens":112}}}"""));
            await using var transport = new OpenAiResponsesTransport(Profile(), "test-key", handler);
            var request = new AgentTransportStartRequest(Guid.NewGuid(), Guid.NewGuid(),
                [new(AgentTransportMessageRole.User, "hello")], [], PromptCacheKey: "stable-prefix", AllowParallelToolCalls: true);
            var events = await Collect(transport.StartAsync(request));

            Check(events.First().Kind == AgentTransportEventKind.ResponseStarted && events.First().ResponseId == "resp_1", "Response id was not surfaced from response.created.");
            Check(events.Single(e => e.Kind == AgentTransportEventKind.ReasoningSummaryDelta).Text == "Tóm tắt ", "Reasoning summary delta missing.");
            Check(events.Single(e => e.Kind == AgentTransportEventKind.TextDelta).Text == "Xin chào", "Output text delta missing.");
            var usage = events.Single(e => e.Kind == AgentTransportEventKind.Usage).Usage!;
            Check(usage.InputTokens == 100 && usage.CachedInputTokens == 60 && usage.CacheWriteInputTokens == 4 && usage.OutputTokens == 12 && usage.TotalTokens == 112, "Responses usage counters mismatch.");
            Check(events.Last().Kind == AgentTransportEventKind.Completed && events.Last().ResponseId == "resp_1", "Responses completion missing.");
            Check(handler.AuthorizationParameters.Single() == "test-key", "Bearer API key missing.");

            using var payload = JsonDocument.Parse(handler.Requests.Single());
            var root = payload.RootElement;
            Check(root.GetProperty("model").GetString() == "fixture" && root.GetProperty("stream").GetBoolean(), "Responses model/stream fields mismatch.");
            Check(!root.GetProperty("store").GetBoolean(), "Default Agent Lab Responses mode must keep store=false.");
            Check(root.GetProperty("prompt_cache_key").GetString() == "stable-prefix", "Prompt cache key missing.");
            Check(root.GetProperty("include")[0].GetString() == "reasoning.encrypted_content", "Stateless encrypted reasoning include missing.");
            Check(!transport.Capabilities.IncrementalContinuation, "Stateless mode must not advertise provider continuation.");
        });

        await Test("Responses function call continuation replays exact output items without previous response state", async () =>
        {
            var handler = new QueueHandler(
                Sse(
                    """{"type":"response.created","response":{"id":"resp_tools","status":"in_progress"}}""",
                    """{"type":"response.output_item.done","output_index":0,"item":{"id":"fc_1","type":"function_call","status":"completed","call_id":"call_abc","name":"read_file","arguments":"{\"path\":\"a.txt\"}"}}""",
                    """{"type":"response.completed","response":{"id":"resp_tools","status":"completed","output":[{"id":"fc_1","type":"function_call","status":"completed","call_id":"call_abc","name":"read_file","arguments":"{\"path\":\"a.txt\"}"}],"usage":{"input_tokens":20,"output_tokens":5,"total_tokens":25}}}"""),
                Sse(
                    """{"type":"response.created","response":{"id":"resp_final","status":"in_progress"}}""",
                    """{"type":"response.output_text.delta","delta":"Verified answer"}""",
                    """{"type":"response.output_item.done","output_index":0,"item":{"id":"msg_2","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"Verified answer","annotations":[]}]}}""",
                    """{"type":"response.completed","response":{"id":"resp_final","status":"completed","output":[{"id":"msg_2","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"Verified answer","annotations":[]}]}],"usage":{"input_tokens":30,"output_tokens":4,"total_tokens":34}}}"""));

            var schema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { path = new { type = "string" } },
                required = new[] { "path" },
                additionalProperties = false
            });
            var task = Guid.NewGuid();
            var turn = Guid.NewGuid();
            var start = new AgentTransportStartRequest(task, turn,
                [new(AgentTransportMessageRole.User, "Read a.txt")],
                [new("read_file", "Read text", schema)]);

            await using var transport = new OpenAiResponsesTransport(Profile(), "", handler);
            var first = await Collect(transport.StartAsync(start));
            var call = first.Single(e => e.Kind == AgentTransportEventKind.ToolCall).ToolCall!;
            Check(call.Id == "call_abc" && call.Name == "read_file", "Responses call identity mismatch.");
            using (var args = JsonDocument.Parse(call.ArgumentsJson))
                Check(args.RootElement.GetProperty("path").GetString() == "a.txt", "Responses function arguments mismatch.");

            var second = await Collect(transport.ContinueAsync(new(task, turn,
                [new(call.Id, call.Name, "{\"content\":\"hello\"}")])));
            Check(string.Concat(second.Where(e => e.Kind == AgentTransportEventKind.TextDelta).Select(e => e.Text)) == "Verified answer", "Responses continuation final text mismatch.");
            Check(handler.Requests.Count == 2, "Expected two Responses HTTP requests.");

            using var payload = JsonDocument.Parse(handler.Requests[1]);
            var root = payload.RootElement;
            Check(!root.TryGetProperty("previous_response_id", out _), "Default stateless mode must not invent previous_response_id.");
            var input = root.GetProperty("input");
            Check(input.GetArrayLength() == 3, "Stateless continuation should replay user + function_call + function_call_output.");
            Check(input[1].GetProperty("type").GetString() == "function_call" && input[1].GetProperty("call_id").GetString() == "call_abc", "Completed provider function_call was not replayed exactly.");
            Check(input[2].GetProperty("type").GetString() == "function_call_output" && input[2].GetProperty("call_id").GetString() == "call_abc", "Function output item missing.");
            Check(input[2].GetProperty("output").GetString()!.Contains("hello", StringComparison.Ordinal), "Function output content missing.");
        });

        await Test("Official stored continuation sends only tool output plus previous response id", async () =>
        {
            var handler = new QueueHandler(
                Sse(
                    """{"type":"response.created","response":{"id":"resp_state_1","status":"in_progress"}}""",
                    """{"type":"response.output_item.done","output_index":0,"item":{"id":"fc_state","type":"function_call","status":"completed","call_id":"call_state","name":"read_file","arguments":"{\"path\":\"state.txt\"}"}}""",
                    """{"type":"response.completed","response":{"id":"resp_state_1","status":"completed","output":[{"id":"fc_state","type":"function_call","status":"completed","call_id":"call_state","name":"read_file","arguments":"{\"path\":\"state.txt\"}"}],"usage":{"input_tokens":50,"output_tokens":6,"total_tokens":56}}}"""),
                Sse(
                    """{"type":"response.created","response":{"id":"resp_state_2","status":"in_progress"}}""",
                    """{"type":"response.output_text.delta","delta":"state verified"}""",
                    """{"type":"response.output_item.done","output_index":0,"item":{"id":"msg_state","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"state verified","annotations":[]}]}}""",
                    """{"type":"response.completed","response":{"id":"resp_state_2","status":"completed","output":[{"id":"msg_state","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"state verified","annotations":[]}]}],"usage":{"input_tokens":8,"output_tokens":3,"total_tokens":11}}}"""));

            var profile = Profile("https://api.openai.com/v1");
            var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { path = new { type = "string" } } });
            var task = Guid.NewGuid();
            var turn = Guid.NewGuid();
            await using var transport = new OpenAiResponsesTransport(profile, "test", handler, OpenAiResponsesStateMode.StoredContinuation);
            Check(transport.Capabilities.IncrementalContinuation, "Stored mode must advertise incremental continuation.");
            var first = await Collect(transport.StartAsync(new(task, turn,
                [new(AgentTransportMessageRole.System, "stable policy"), new(AgentTransportMessageRole.User, "Read state.txt")],
                [new("read_file", "read", schema)])));
            var call = first.Single(e => e.Kind == AgentTransportEventKind.ToolCall).ToolCall!;
            _ = await Collect(transport.ContinueAsync(new(task, turn, [new(call.Id, call.Name, "{\"content\":\"ok\"}")])));

            using var firstRequest = JsonDocument.Parse(handler.Requests[0]);
            Check(firstRequest.RootElement.GetProperty("store").GetBoolean(), "Stored mode must explicitly send store=true.");
            Check(!firstRequest.RootElement.TryGetProperty("previous_response_id", out _), "First stored request must not have previous_response_id.");
            Check(!firstRequest.RootElement.TryGetProperty("include", out _), "Stored mode does not need encrypted stateless reasoning output.");

            using var continuation = JsonDocument.Parse(handler.Requests[1]);
            var root = continuation.RootElement;
            Check(root.GetProperty("store").GetBoolean(), "Stored continuation must keep store=true.");
            Check(root.GetProperty("previous_response_id").GetString() == "resp_state_1", "Stored continuation did not link previous response id.");
            var input = root.GetProperty("input");
            Check(input.GetArrayLength() == 1, "Stored continuation must not resend system/user/function-call transcript.");
            Check(input[0].GetProperty("type").GetString() == "function_call_output" && input[0].GetProperty("call_id").GetString() == "call_state", "Stored continuation should send only the new function output.");
        });

        await Test("Stored continuation is explicit and restricted to official OpenAI endpoint", () =>
        {
            try
            {
                _ = new OpenAiResponsesTransport(Profile("https://example.invalid/v1"), "", stateMode: OpenAiResponsesStateMode.StoredContinuation);
                throw new InvalidOperationException("Compatible endpoint was allowed to claim official stored continuation.");
            }
            catch (InvalidOperationException ex)
            {
                Check(ex.Message.Contains("OpenAI Responses chính thức", StringComparison.Ordinal), "Expected explicit official-endpoint rejection.");
            }
            return Task.CompletedTask;
        });

        await Test("Responses request preserves H2 native image file and function tool shapes", async () =>
        {
            var handler = new QueueHandler(Sse(
                """{"type":"response.output_text.delta","delta":"ok"}""",
                """{"type":"response.output_item.done","output_index":0,"item":{"id":"msg_3","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"ok","annotations":[]}]}}""",
                """{"type":"response.completed","response":{"id":"resp_multi","status":"completed","output":[{"id":"msg_3","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"ok","annotations":[]}]}],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}}"""));
            var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });
            await using var transport = new OpenAiResponsesTransport(Profile(), "", handler);
            var request = new AgentTransportStartRequest(Guid.NewGuid(), Guid.NewGuid(),
                [new(AgentTransportMessageRole.User, "inspect", [new AiImage("image/png", [1, 2])], [new AiFile("brief.pdf", "application/pdf", [3, 4])])],
                [new("inspect_doc", "Inspect document", schema)], AllowParallelToolCalls: true);
            _ = await Collect(transport.StartAsync(request));

            using var payload = JsonDocument.Parse(handler.Requests.Single());
            var root = payload.RootElement;
            var parts = root.GetProperty("input")[0].GetProperty("content");
            Check(parts[0].GetProperty("type").GetString() == "input_text", "Responses text part mismatch.");
            Check(parts[1].GetProperty("type").GetString() == "input_image" && parts[1].GetProperty("image_url").GetString()!.StartsWith("data:image/png;base64,", StringComparison.Ordinal), "Responses image shape mismatch.");
            Check(parts[2].GetProperty("type").GetString() == "input_file" && parts[2].GetProperty("filename").GetString() == "brief.pdf", "Responses file shape mismatch.");
            var tool = root.GetProperty("tools")[0];
            Check(tool.GetProperty("type").GetString() == "function" && tool.GetProperty("name").GetString() == "inspect_doc", "Responses flat function tool schema mismatch.");
            Check(root.GetProperty("parallel_tool_calls").GetBoolean(), "Responses parallel tool request missing.");
        });

        await Test("Responses incomplete stream cannot expose a function call", async () =>
        {
            var handler = new QueueHandler(Sse(
                """{"type":"response.output_item.done","output_index":0,"item":{"id":"fc_cut","type":"function_call","status":"completed","call_id":"cut","name":"read_file","arguments":"{}"}}""",
                """{"type":"response.incomplete","response":{"id":"resp_cut","status":"incomplete","incomplete_details":{"reason":"max_output_tokens"}}}"""));
            var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });
            await using var transport = new OpenAiResponsesTransport(Profile(), "", handler);
            var request = new AgentTransportStartRequest(Guid.NewGuid(), Guid.NewGuid(),
                [new(AgentTransportMessageRole.User, "read")], [new("read_file", "read", schema)]);
            var seenCalls = 0;
            try
            {
                await foreach (var item in transport.StartAsync(request))
                    if (item.Kind == AgentTransportEventKind.ToolCall) seenCalls++;
                throw new InvalidOperationException("Incomplete Responses result was accepted.");
            }
            catch (IOException ex)
            {
                Check(ex.Message.Contains("max_output_tokens", StringComparison.Ordinal), "Incomplete reason was not preserved safely.");
            }
            Check(seenCalls == 0, "Incomplete function call must not be exposed for execution.");
        });

        await Test("Responses transport reuses H2 endpoint safety and explicit baseline capabilities", async () =>
        {
            var handler = new QueueHandler(Sse());
            await using var transport = new OpenAiResponsesTransport(Profile("http://8.8.8.8/v1"), "", handler);
            Check(transport.Capabilities == AgentTransportCapabilities.OpenAiResponsesHttp, "Responses baseline capability profile changed.");
            try
            {
                _ = await Collect(transport.StartAsync(new(Guid.NewGuid(), Guid.NewGuid(), [new(AgentTransportMessageRole.User, "x")], [])));
                throw new InvalidOperationException("Unsafe Responses endpoint was accepted.");
            }
            catch (InvalidOperationException ex)
            {
                Check(ex.Message.Contains("HTTPS", StringComparison.OrdinalIgnoreCase), "Expected H2 HTTPS safety rejection.");
            }
            Check(handler.Requests.Count == 0, "Unsafe Responses endpoint reached HTTP handler.");
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(outputDirectory, "v2-openai-responses-transport-tests.txt");
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

    private static string Sse(params string[] events)
        => string.Join("\n", events.Select(e => "data: " + e)) + "\n";

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

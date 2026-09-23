using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using H2AgentLab.Transport;
using H2Notes.Core;

internal static class H2AgentOpenAiWireTests
{
    private static void Check(bool condition, string message = "AR-065 assertion failed") { if (!condition) throw new Exception(message); }
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static AgentToolDefinition Tool(string name) => new(name, "Dedicated synthetic fixture tool",
        Json("""{"type":"object","properties":{"q":{"type":"string"},"optional":{"type":"integer"}},"required":["q"]}"""));
    private static AiProfile Profile() => new() { Protocol = AiProtocol.OpenAiResponses, Model = "gpt-5.6-luna", BaseUrl = "https://api.openai.com/v1" };
    private static AgentTransportStartRequest Start(params AgentToolDefinition[] tools) => new(Guid.NewGuid(), Guid.NewGuid(),
        [new(AgentTransportMessageRole.System, "host policy"), new(AgentTransportMessageRole.Assistant, "prior answer"), new(AgentTransportMessageRole.User, "synthetic task")], tools);
    private static async Task<List<AgentTransportEvent>> Collect(IAsyncEnumerable<AgentTransportEvent> source)
    { var items = new List<AgentTransportEvent>(); await foreach (var item in source) items.Add(item); return items; }
    private static async Task<T> Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T ex) { return ex; } throw new Exception("Expected " + typeof(T).Name); }
    private static string Wire(JsonElement payload, int index = 0) => payload.GetProperty("tools")[index].GetProperty("name").GetString()!;
    private static object Call(string name, string id = "call_1", string arguments = "{\"q\":\"sample\"}")
        => new { type = "function_call", id = "fc_" + id, call_id = id, name, arguments, status = "completed" };
    private static object Message(string phase = "final_answer") => new { type = "message", id = "msg_fixture", role = "assistant", phase,
        content = new[] { new { type = "output_text", text = "fixture complete", annotations = Array.Empty<object>() } } };
    private static string[] Completed(string id, params object[] output) =>
    [JsonSerializer.Serialize(new { type = "response.completed", response = new { id, status = "completed", output, usage = new { input_tokens = 10, output_tokens = 5, total_tokens = 15 } } })];
    private static HttpResponseMessage Sse(IEnumerable<string> events) => new(HttpStatusCode.OK)
    { Content = new StringContent(string.Join("", events.Select(x => "data: " + x + "\n\n")), Encoding.UTF8, "text/event-stream") };
    private static HttpResponseMessage Bad(string body) => new(HttpStatusCode.BadRequest) { Content = new StringContent(body) };

    public static void Run(Action<string, Action> test)
    {
        void Async(string name, Func<Task> body) => test("AR-065 " + name, () => Task.Run(body).GetAwaiter().GetResult());
        foreach (var name in new[] { "tool_search", "excel.read_range", "word.replace_range", "autocad.inspect_file", "Đọc.bảng", new string('x', 100) })
            test("AR-065 deterministic bounded wire identity " + name[..Math.Min(name.Length, 24)], () =>
            {
                var wire = OpenAiResponsesWireContract.WireName(name);
                Check(Regex.IsMatch(wire, "\\A[A-Za-z0-9_-]{1,64}\\z") && wire == OpenAiResponsesWireContract.WireName(name));
                Check(OpenAiResponsesWireContract.InternalName(wire, [Tool(name)]) == name);
            });
        test("AR-065 aliases distinguish dots underscores case and reserved literals", () =>
        {
            var alias = OpenAiResponsesWireContract.WireName("excel.read_range");
            var names = new[] { "excel.read_range", "excel_read_range", "Excel.read_range", alias };
            var wire = names.Select(OpenAiResponsesWireContract.WireName).ToArray();
            Check(wire.Distinct(StringComparer.Ordinal).Count() == names.Length && wire[3] != alias);
            var reverseOrder = OpenAiResponsesWireContract.Tools(names.Reverse().Select(Tool));
            Check(reverseOrder.Select(x => x!["name"]!.GetValue<string>()).Reverse().SequenceEqual(wire));
        });
        Async("projection refuses collisions rather than choosing an arbitrary callable", async () =>
        {
            // Distinct malformed UTF-16 inputs encode to the same replacement bytes; even this
            // adversarial alias collision must fail closed, not silently overwrite the inverse map.
            await Reject<ArgumentException>(() => Task.FromResult(OpenAiResponsesWireContract.Tools([Tool("\ud800"), Tool("\ud801")])));
        });
        foreach (var schema in new[] { "null", "[]", "\"object\"" })
            Async("non-object schema refused before wire " + schema, async () =>
                await Reject<ArgumentException>(() => Task.FromResult(OpenAiResponsesWireContract.Tools([new("tool", "fixture", Json(schema))]))));
        Async("duplicate advertised identities refused", async () =>
            await Reject<ArgumentException>(() => Task.FromResult(OpenAiResponsesWireContract.Tools([Tool("same"), Tool("same")]))));
        test("AR-065 optional schema preserved and strict false explicit without mutating source", () =>
        {
            var tool = Tool("excel.read_range"); var before = tool.Parameters.GetRawText();
            var wire = OpenAiResponsesWireContract.Tools([tool])[0]!;
            Check(!wire["strict"]!.GetValue<bool>() && JsonElement.DeepEquals(Json(wire["parameters"]!.ToJsonString()), tool.Parameters));
            Check(tool.Parameters.GetRawText() == before && tool.Name == "excel.read_range");
        });
        Async("deferred contract replacement is atomic and idempotent", async () =>
        {
            var current = new Dictionary<string, AgentToolDefinition>(StringComparer.Ordinal) { ["a.read"] = Tool("a.read") };
            OpenAiResponsesWireContract.Admit(current, [Tool("a.read"), Tool("b.read")]);
            var original = current["a.read"];
            await Reject<InvalidOperationException>(() => { OpenAiResponsesWireContract.Admit(current,
                [Tool("unpublished.read"), Tool("a.read") with { Description = "changed" }]); return Task.CompletedTask; });
            Check(current.Count == 2 && ReferenceEquals(current["a.read"], original) && !current.ContainsKey("unpublished.read"));
        });

        foreach (var kind in new[] { "http", "ws" })
        {
            Async("WIRE CONTROL dotted callable roundtrip " + kind, async () =>
            {
                using var fixture = new Fixture(kind, (payload, _) =>
                {
                    var name = Wire(payload);
                    Check(Regex.IsMatch(name, "\\A[A-Za-z0-9_-]{1,64}\\z"), "Legacy dotted wire name reproduced.");
                    return Completed("r1", Call(name));
                });
                await using var transport = fixture.Transport();
                var events = await Collect(transport.StartAsync(Start(Tool("excel.read_range"))));
                Check(events.Single(x => x.ToolCall is not null).ToolCall!.Name == "excel.read_range");
            });
            Async("WIRE CONTROL explicit non-strict schema " + kind, async () =>
            {
                using var fixture = new Fixture(kind, (payload, _) =>
                {
                    Check(payload.GetProperty("tools")[0].TryGetProperty("strict", out var strict) && strict.ValueKind == JsonValueKind.False,
                        "Legacy implicit strict normalization reproduced.");
                    return Completed("r1", Message());
                });
                await using var transport = fixture.Transport();
                await Collect(transport.StartAsync(Start(Tool("read_file"))));
            });
        }
        foreach (var kind in new[] { "http", "stored", "ws" })
            Async("search deferred dot-tool output final and stable registry " + kind, async () =>
            {
                string? discovered = null;
                using var fixture = new Fixture(kind, (payload, step) =>
                {
                    if (step == 0) return Completed("r_search", Call(Wire(payload), "search_call"));
                    Check(payload.GetProperty("tools").GetArrayLength() == 2 && Wire(payload) == "tool_search");
                    var outputs = payload.GetProperty("input").EnumerateArray().Where(x => x.TryGetProperty("type", out var t) && t.GetString() == "function_call_output").ToArray();
                    Check(outputs.Last().GetProperty("call_id").GetString() == (step == 1 ? "search_call" : "read_call"));
                    if (kind != "http") Check(payload.GetProperty("previous_response_id").GetString() == (step == 1 ? "r_search" : "r_read"));
                    if (step == 1) { discovered = Wire(payload, 1); return Completed("r_read", Call(discovered, "read_call")); }
                    Check(discovered == Wire(payload, 1)); return Completed("r_final", Message());
                });
                await using var transport = fixture.Transport(); var request = Start(Tool("tool_search"));
                var first = (await Collect(transport.StartAsync(request))).Single(x => x.ToolCall is not null).ToolCall!;
                var second = (await Collect(transport.ContinueAsync(new(request.TaskId, request.TurnId,
                    [new(first.Id, first.Name, "{\"loaded\":\"excel.read_range\"}")], [Tool("excel.read_range")])))).Single(x => x.ToolCall is not null).ToolCall!;
                Check(second.Name == "excel.read_range" && second.Id == "read_call");
                var final = await Collect(transport.ContinueAsync(new(request.TaskId, request.TurnId, [new(second.Id, second.Name, "{\"value\":42}")])));
                Check(final.Last().Kind == AgentTransportEventKind.Completed && fixture.Requests.Count == 3 && fixture.Connections <= 1);
            });
        Async("stateless complete output repairs partial done events preserving reasoning phase roles and IDs", async () =>
        {
            object reasoning = new { id = "rs", type = "reasoning", encrypted_content = "SYNTHETIC_ENCRYPTED_FIXTURE", summary = Array.Empty<object>() };
            using var fixture = new Fixture("http", (payload, step) =>
            {
                if (step == 0)
                {
                    var call = Call(Wire(payload));
                    return [JsonSerializer.Serialize(new { type = "response.output_item.done", item = call }), .. Completed("r1", reasoning, Message("commentary"), call)];
                }
                var input = payload.GetProperty("input").EnumerateArray().ToArray();
                Check(input[0].GetProperty("role").GetString() == "system" && input[1].GetProperty("role").GetString() == "assistant" && input[2].GetProperty("role").GetString() == "user");
                Check(input[3].GetProperty("encrypted_content").GetString() == "SYNTHETIC_ENCRYPTED_FIXTURE");
                Check(input[4].GetProperty("phase").GetString() == "commentary");
                Check(input[5].GetProperty("name").GetString() == Wire(payload) && input[6].GetProperty("call_id").GetString() == "call_1");
                Check(!payload.TryGetProperty("previous_response_id", out _) && !payload.GetProperty("store").GetBoolean());
                return Completed("r2", Message());
            });
            await using var transport = fixture.Transport(); var request = Start(Tool("word.read"));
            var call = (await Collect(transport.StartAsync(request))).Single(x => x.ToolCall is not null).ToolCall!;
            await Collect(transport.ContinueAsync(new(request.TaskId, request.TurnId, [new(call.Id, call.Name, "synthetic output")])));
        });
        foreach (var fault in new[] { "wrong-id", "wrong-name", "duplicate", "missing" })
            Async("continuation rejects unmatched output without another send " + fault, async () =>
            {
                using var fixture = new Fixture("http", (payload, _) => Completed("r1", Call(Wire(payload))));
                await using var transport = fixture.Transport(); var request = Start(Tool("a.read"));
                var call = (await Collect(transport.StartAsync(request))).Single(x => x.ToolCall is not null).ToolCall!;
                var valid = new AgentToolResult(call.Id, call.Name, "done");
                AgentToolResult[] results = fault switch { "wrong-id" => [valid with { ToolCallId = "foreign" }], "wrong-name" => [valid with { ToolName = "other" }], "duplicate" => [valid, valid], _ => [] };
                await Reject<InvalidOperationException>(() => Collect(transport.ContinueAsync(new(request.TaskId, request.TurnId, results))));
                Check(fixture.Requests.Count == 1);
            });
        foreach (var fault in new[] { "unknown-name", "duplicate-id", "array-arguments" })
            Async("malformed provider batch has zero dispatch " + fault, async () =>
            {
                using var fixture = new Fixture("http", (payload, _) => fault switch
                {
                    "unknown-name" => Completed("r1", Call("unadvertised")),
                    "duplicate-id" => Completed("r1", Call(Wire(payload)), Call(Wire(payload))),
                    _ => Completed("r1", Call(Wire(payload), arguments: "[]"))
                });
                await using var transport = fixture.Transport(); var calls = 0;
                await Reject<InvalidDataException>(async () => { await foreach (var item in transport.StartAsync(Start(Tool("a.read")))) if (item.ToolCall is not null) calls++; });
                Check(calls == 0 && fixture.Requests.Count == 1);
            });
        foreach (var kind in new[] { "http", "ws" })
            Async("rebase preview is pure and matches exact mapped request budget " + kind, async () =>
            {
                using var fixture = new Fixture(kind, (payload, step) => step == 0 ? Completed("r1", Call(Wire(payload))) : Completed("r2", Message()));
                await using var transport = fixture.Transport(); var original = Start(Tool("a.read"));
                var call = (await Collect(transport.StartAsync(original))).Single(x => x.ToolCall is not null).ToolCall!;
                var ack = new AgentTransportContinuationRequest(original.TaskId, original.TurnId, [new(call.Id, call.Name, "observed")]);
                var compact = original with { Messages = [new(AgentTransportMessageRole.System, "host policy"), new(AgentTransportMessageRole.User, "retained work state")], Tools = [Tool("a.read"), Tool("b.read")] };
                var rebase = (IAgentContextRebaseTransport)transport;
                var first = rebase.PreviewContextRebase(compact, ack, default); var second = rebase.PreviewContextRebase(compact, ack, default);
                Check(first.PayloadSha256 == second.PayloadSha256 && fixture.Requests.Count == 1);
                await Reject<InvalidOperationException>(() => Collect(rebase.RebaseContextAsync(compact, ack, new string('0', 64))));
                Check(fixture.Requests.Count == 1);
                await Collect(rebase.RebaseContextAsync(compact, ack, first.PayloadSha256));
                var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.Requests[1]))).ToLowerInvariant();
                Check(actual == first.PayloadSha256 && ((IAgentRequestBudgetSource)transport).LastRequestBudget!.PayloadSha256 == actual);
            });
        foreach (var status in new[] { 400, 401, 403, 429, 500 })
            Async("safe status diagnostic and no retry " + status, async () =>
            {
                var handler = new Handler((_, _) => new HttpResponseMessage((HttpStatusCode)status)
                { Content = new StringContent("""{"error":{"code":"invalid_function_parameters","type":"invalid_request_error","param":"tools[0].parameters","message":"Bearer sk-PRIVATE user document password"}}""") });
                await using var transport = new OpenAiResponsesTransport(Profile(), "SYNTHETIC_KEY", handler);
                var error = await Reject<AiServiceException>(() => Collect(transport.StartAsync(Start(Tool("a.read")))));
                Check(error.StatusCode == (HttpStatusCode)status && error.Message.Contains("phase=initial") && error.Message.Contains("param=tools[0].parameters"));
                Check(!error.ToString().Contains("PRIVATE") && !error.ToString().Contains("SYNTHETIC_KEY") && !error.ToString().Contains("password"));
                Check(handler.Requests.Count == 1 && AiFailure.Describe(error) == error.Message);
            });
        Async("continuation HTTP400 retains phase and does not replay earlier tool effects", async () =>
        {
            var count = 0;
            var handler = new Handler((payload, step) => step == 0 ? Sse(Completed("r1", Call(Wire(payload))))
                : Bad("""{"error":{"type":"invalid_request_error","code":"invalid_tool_call","param":"input[0].call_id","message":"private output"}}"""));
            await using var transport = new OpenAiResponsesTransport(Profile(), "", handler); var request = Start(Tool("fixture.write"));
            var call = (await Collect(transport.StartAsync(request))).Single(x => x.ToolCall is not null).ToolCall!;
            count++; // Declared synthetic executor counter, not a real Office mutation.
            var error = await Reject<AiServiceException>(() => Collect(transport.ContinueAsync(new(request.TaskId, request.TurnId, [new(call.Id, call.Name, "observed write receipt")]))));
            Check(error.Message.Contains("phase=tool_continuation") && count == 1 && handler.Requests.Count == 2 && !error.ToString().Contains("private output"));
        });
        foreach (var body in new[] { "not-json SECRET", "{\"error\":\"SECRET\"}", new string('x', 20000), "{\"error\":{\"code\":\"SECRET\",\"type\":\"SECRET\",\"param\":\"tools[0].parameters.SECRET\",\"message\":\"SECRET\"}}" })
            Async("malformed oversized or malicious body stays bounded " + body.Length, async () =>
            {
                var handler = new Handler((_, _) => Bad(body));
                await using var transport = new OpenAiResponsesTransport(Profile(), "", handler);
                var error = await Reject<AiServiceException>(() => Collect(transport.StartAsync(Start())));
                Check(error.Message.Length < 600 && !error.ToString().Contains("SECRET") && handler.Requests.Count == 1);
            });
        Async("error body reads at most 16KiB and caller cancellation propagates", async () =>
        {
            using var stream = new CountingStream(100_000);
            using var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StreamContent(stream) };
            var error = await OpenAiResponsesDiagnostics.HttpError(response, "initial", default);
            Check(stream.BytesRead <= 16384 && error.StatusCode == HttpStatusCode.BadRequest);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            using var response2 = Bad("{}");
            await Reject<OperationCanceledException>(async () => { await OpenAiResponsesDiagnostics.HttpError(response2, "initial", cancelled.Token); });
        });
        foreach (var kind in new[] { "http", "ws" })
            Async("stream error exposes safe diagnostic without dispatch " + kind, async () =>
            {
                using var fixture = new Fixture(kind, (_, _) => ["""{"type":"error","error":{"type":"invalid_request_error","code":"invalid_function_name","param":"tools[0].name","message":"SECRET"}}"""]);
                await using var transport = fixture.Transport();
                var error = await Reject<AiServiceException>(() => Collect(transport.StartAsync(Start(Tool("a.read")))));
                Check(error.Message.Contains("code=invalid_function_name") && !error.ToString().Contains("SECRET") && fixture.Requests.Count == 1);
            });
        test("AR-065 Luna reasoning exact official metadata and unknown provider isolation", () =>
        {
            var official = Profile(); Check(AiModelCapabilities.GetReasoningOptions(official).SequenceEqual(new[] { "none", "low", "medium", "high", "xhigh", "max" }));
            official.ReasoningEffort = "max"; Check(AiModelCapabilities.ResolveReasoningEffort(official) == "max");
            var custom = Profile(); custom.BaseUrl = "https://example.invalid/v1";
            Check(AiModelCapabilities.GetReasoningOptions(custom).Count == 0);
            var unknown = Profile(); unknown.Model = "gpt-5.6-luna-guessed-snapshot"; Check(AiModelCapabilities.GetReasoningOptions(unknown).Count == 0);
        });
    }

    private sealed class Handler(Func<JsonElement, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = await request.Content!.ReadAsStringAsync(token); var step = Requests.Count; Requests.Add(body);
            using var json = JsonDocument.Parse(body); return respond(json.RootElement, step);
        }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _kind; private readonly Func<JsonElement, int, string[]> _respond;
        private readonly Handler _handler; private readonly Socket _socket;
        internal List<string> Requests => _kind == "ws" ? _socket.Requests : _handler.Requests;
        internal int Connections => _socket.Connections;
        internal Fixture(string kind, Func<JsonElement, int, string[]> respond)
        { _kind = kind; _respond = respond; _handler = new Handler((p, n) => Sse(respond(p, n))); _socket = new Socket(respond); }
        internal IAgentTransport Transport() => _kind == "ws"
            ? new OpenAiResponsesWebSocketTransport(Profile(), "", () => _socket, () => throw new Exception("Unexpected fallback"))
            : new OpenAiResponsesTransport(Profile(), "", _handler, _kind == "stored" ? OpenAiResponsesStateMode.StoredContinuation : OpenAiResponsesStateMode.Stateless);
        public void Dispose() => _handler.Dispose();
    }
    private sealed class Socket(Func<JsonElement, int, string[]> respond) : IResponsesWebSocketConnection
    {
        internal List<string> Requests { get; } = []; internal int Connections { get; private set; }
        private string[] _events = []; public bool IsOpen { get; private set; }
        public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken token) { IsOpen = true; Connections++; return Task.CompletedTask; }
        public Task SendTextAsync(string text, CancellationToken token)
        { using var json = JsonDocument.Parse(text); _events = respond(json.RootElement, Requests.Count); Requests.Add(text); return Task.CompletedTask; }
        public async IAsyncEnumerable<string> ReceiveTextAsync([EnumeratorCancellation] CancellationToken token)
        { foreach (var e in _events) { token.ThrowIfCancellationRequested(); yield return e; } await Task.CompletedTask; }
        public Task CloseAsync(CancellationToken token) { IsOpen = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
    }
    private sealed class CountingStream(int length) : MemoryStream(Enumerable.Repeat((byte)'x', length).ToArray())
    {
        internal int BytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { var count = await base.ReadAsync(buffer, token); BytesRead += count; return count; }
    }
}

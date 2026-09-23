using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Transport;
using H2Notes.Core;

/// <summary>E1 settings/policy; E2 actual concrete serializers, intercepted HTTP/socket frames,
/// production adapter and disposable archive. No model/API call, credentials, native media
/// processing or E3/E4/E5 acceptance. Synthetic limits are explicitly sourced test settings.</summary>
internal static class H2AgentRequestBudgetTests
{
    private static readonly string[] Kinds = ["ollama", "chat", "responses", "stored", "ws"];
    private const string Marker = "SOURCE-ĐÚNG-😀-\\\"";
    public static void Run(Action<string, Action> test)
    {
        test("AR-050 policy local fallback is labelled and never invents an Ollama context allocation", () =>
        {
            var guard = new AgentRequestBudgetGuard(new() { Model = "unknown-name" });
            var raw = guard.Prepare(new() { ["messages"] = new JsonArray() }, Guid.NewGuid(), Guid.NewGuid(), "fixture");
            var value = JsonNode.Parse(raw)!;
            Check(value["options"]!["num_ctx"] is null, "Fallback changed native context allocation.");
            Check(value["options"]!["num_predict"]!.GetValue<int>() == 4096, "Missing finite generation reserve.");
            var receipt = guard.LastReceipt!;
            Check(!receipt.ProviderLimitConfigured && receipt.LimitSource.Contains("not-provider-limit")
                && receipt.TokenAccounting.StartsWith("Estimated") && receipt.ByteAccounting.StartsWith("Exact"), "Mislabelled budget.");
        });
        test("AR-050 policy sourced settings roundtrip and transport snapshot cannot follow mutable profile changes", () =>
        {
            var profile = Profile("chat", 2048);
            var guard = new AgentRequestBudgetGuard(profile);
            var roundtrip = JsonSerializer.Deserialize<AiProfile>(JsonSerializer.Serialize(profile))!;
            Check(roundtrip.RequestBudget == profile.RequestBudget, "Local profile dropped settings.");
            profile.RequestBudget = Settings(100000);
            Expect<AgentRequestBudgetException>(() => guard.Prepare(new() { ["messages"] = new string('x', 3000) }, Guid.NewGuid(), Guid.NewGuid(), "fixture"));
            Check(guard.LastReceipt!.ContextLimitTokens == 2048, "Profile mutation changed admitted policy.");
        });
        foreach (var entry in new[] { "source", "reserve", "margin", "wire", "image", "field" })
            test("AR-050 invalid configuration fails before transport allocation " + entry, () =>
            {
                var settings = Settings(2048);
                settings = entry switch {
                    "source" => settings with { ContextLimitSource = "" },
                    "reserve" => settings with { ReservedOutputTokens = 2048 },
                    "margin" => settings with { SafetyMarginTokens = -1 },
                    "wire" => settings with { MaxSerializedBytes = 0 },
                    "image" => settings with { NativeImageTokenEstimate = 0 },
                    _ => settings with { ChatOutputLimitParameter = "unexpected" } };
                Expect<AgentRequestBudgetException>(() => new AgentRequestBudgetGuard(Profile("chat").withBudget(settings)));
            });
        test("AR-050 exact serialized bytes include escapes schemas media output reserve and safety margin", () =>
        {
            var profile = Profile("responses");
            var task = Guid.NewGuid(); var turn = Guid.NewGuid();
            var payload = new JsonObject { ["input"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "input_text", ["text"] = Marker },
                    new JsonObject { ["type"] = "input_image", ["image_url"] = "data:image/png;base64,AQID" },
                    new JsonObject { ["type"] = "input_file", ["file_data"] = "data:application/pdf;base64,BAUG" }) }),
                ["tools"] = new JsonArray(new JsonObject { ["type"] = "function", ["name"] = "tool", ["description"] = new string('S', 1000) }) };
            var guard = new AgentRequestBudgetGuard(profile);
            var raw = guard.Prepare(payload, task, turn, "Responses"); var r = guard.LastReceipt!;
            AssertWire(raw, r);
            Check(r.Images == 1 && r.Files == 1 && r.SerializedInputEstimate == r.SerializedBytes + 8192 + 32768, "Media/schema overhead missing.");
            Check(!JsonSerializer.Serialize(r).Contains(Marker) && !JsonSerializer.Serialize(r).Contains("data:image"), "Receipt leaked input.");
            var total = checked((int)(r.EstimatedInputTokens + r.ReservedOutputTokens + r.SafetyMarginTokens));
            var equal = new AgentRequestBudgetGuard(Profile("responses", total));
            equal.Prepare(payload, task, turn, "Responses");
            var below = new AgentRequestBudgetGuard(Profile("responses", total - 1));
            var error = Expect<AgentRequestBudgetException>(() => below.Prepare(payload, task, turn, "Responses"));
            Check(error.Receipt is { Allowed: false } && error.Code == "request_budget_exceeded", "Boundary not inclusive/exclusive.");
        });
        test("AR-050 cache hits never erase retained context and unknown response lineage is rejected", () =>
        {
            var guard = new AgentRequestBudgetGuard(Profile("stored", 4096)); var task = Guid.NewGuid(); var turn = Guid.NewGuid();
            guard.Prepare(new() { ["input"] = "hello" }, task, turn, "stored");
            guard.ObserveCompleted(new(3990, 3990, null, 64, 4054), "resp-owned");
            var ex = Expect<AgentRequestBudgetException>(() => guard.Prepare(new() { ["input"] = "small delta", ["previous_response_id"] = "resp-owned" }, task, turn, "stored"));
            Check(ex.Receipt!.RetainedContextEstimate >= 4054 && ex.Code == "request_budget_exceeded", "Cached server history was discounted.");
            var other = new AgentRequestBudgetGuard(Profile("stored"));
            Check(Expect<AgentRequestBudgetException>(() => other.Prepare(new() { ["previous_response_id"] = "foreign" }, task, turn, "stored")).Code == "request_budget_context_unknown", "Unknown state adopted.");
        });
        test("AR-050 observed underestimates persist on replay while invalid provider usage fails closed", () =>
        {
            var task = Guid.NewGuid(); var turn = Guid.NewGuid(); var guard = new AgentRequestBudgetGuard(Profile("chat", 10000));
            guard.Prepare(new() { ["messages"] = "x" }, task, turn, "chat");
            guard.ObserveCompleted(new(4000, 3999, null, 64, 4064));
            guard.Prepare(new() { ["messages"] = "y" }, task, turn, "chat");
            Check(guard.LastReceipt!.UsageCorrectionTokens > 3000, "Usage correction lost.");
            guard.ObserveCompleted(new(-1));
            Check(Expect<AgentRequestBudgetException>(() => guard.Prepare(new() { ["messages"] = "z" }, task, turn, "chat")).Code == "request_budget_accounting_invalid", "Negative usage accepted.");
        });
        test("AR-050 output limit dialect is explicit per provider and Responses disables truncation", () =>
        {
            foreach (var kind in new[] { "ollama", "chat", "responses", "official-chat" })
            {
                var profile = Profile(kind);
                var raw = new AgentRequestBudgetGuard(profile).Prepare(new(), Guid.NewGuid(), Guid.NewGuid(), kind);
                var obj = JsonNode.Parse(raw)!;
                var value = kind == "ollama" ? obj["options"]!["num_predict"] : obj[kind == "chat" ? "max_tokens" : kind == "official-chat" ? "max_completion_tokens" : "max_output_tokens"];
                Check(value!.GetValue<int>() == 64, "Wrong output reserve dialect.");
                if (kind == "responses") Check(obj["truncation"]!.GetValue<string>() == "disabled", "Implicit context trimming allowed.");
            }
        });
        foreach (var kind in Kinds)
        {
            test("AR-050 RC-17 actual serializer accounts for 205 tool continuations without dropping role or IDs " + kind,
                () => Run(() => LongCorpus(kind)));
            test("AR-050 known oversized start never reaches provider send " + kind,
                () => Run(async () =>
                {
                    using var fixture = new WireFixture(kind, Profile(kind, 2048));
                    await using var transport = fixture.Create();
                    var seen = new List<AgentRequestBudgetReceipt>(); Subscribe(transport, seen);
                    var ex = await ExpectAsync<AgentRequestBudgetException>(() => Collect(transport.StartAsync(Start(new string('x', 5000)))));
                    Check(fixture.Bodies.Count == 0 && seen.Count == 1 && !seen[0].Allowed && !ex.Message.Contains(new string('x', 40)), "Oversized request was sent or leaked.");
                    Save(kind + "-start-rejected", seen);
                }));
            test("AR-050 newly loaded schemas and repair steering are counted before every continuation " + kind,
                () => Run(async () =>
                {
                    using var fixture = new WireFixture(kind, Profile(kind, 4096)) { EmitTools = false };
                    await using var transport = fixture.Create(); var seen = new List<AgentRequestBudgetReceipt>(); Subscribe(transport, seen);
                    var start = Start("first"); await Collect(transport.StartAsync(start));
                    await Collect(transport.ContinueAsync(new(start.TaskId, start.TurnId, [], SupplementalUserMessages: ["repair with source, not a new turn"])));
                    var schema = new AgentToolDefinition("huge", new string('S', 5000), Parameters());
                    await ExpectAsync<AgentRequestBudgetException>(() => Collect(transport.ContinueAsync(new(start.TaskId, start.TurnId, [], [schema], ["steer"]))));
                    Check(fixture.Bodies.Count == 2 && seen.Count == 3 && !seen[^1].Allowed, "Continuation bypassed guard.");
                    Check(JsonNode.Parse(fixture.Bodies[1])![kind is "ollama" or "chat" ? "messages" : "input"]!.AsArray().Last()!["role"]!.GetValue<string>() == "user", "Steering role changed.");
                    Save(kind + "-repair-schema", seen);
                }));
            test("AR-050 actual serialized images are measured rather than omitted " + kind,
                () => Run(async () =>
                {
                    using var fixture = new WireFixture(kind, Profile(kind)) { EmitTools = false };
                    await using var transport = fixture.Create(); var seen = new List<AgentRequestBudgetReceipt>(); Subscribe(transport, seen);
                    var request = Start("image fixture") with { Messages = [new(AgentTransportMessageRole.User, "image fixture", [new("image/png", [137, 80, 78, 71, 13, 10, 26, 10])])] };
                    await Collect(transport.StartAsync(request));
                    Check(seen.Single().Images == 1 && seen.Single().SerializedInputEstimate > seen.Single().SerializedBytes, "Image accounting missing.");
                    AssertWire(fixture.Bodies.Single(), seen.Single());
                }));
        }
        foreach (var kind in new[] { "chat", "responses", "stored", "ws" })
            test("AR-050 serialized native file metadata and bytes have an explicit estimated charge " + kind, () => Run(async () =>
            {
                using var fixture = new WireFixture(kind, Profile(kind)) { EmitTools = false };
                await using var transport = fixture.Create(); var seen = new List<AgentRequestBudgetReceipt>(); Subscribe(transport, seen);
                // Wire fixture bytes, not a claim to render/parse a PDF or know its expanded token cost.
                var start = Start("file fixture") with { Messages = [new(AgentTransportMessageRole.User, "file fixture", Files: [new("sample.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.7 wire-only"))])] };
                await Collect(transport.StartAsync(start));
                Check(seen.Single().Files == 1 && seen.Single().SerializedInputEstimate == seen.Single().SerializedBytes + 32768, "File charge missing.");
                AssertWire(fixture.Bodies.Single(), seen.Single());
            }));
        test("AR-050 RC-18 smaller sourced model budget remeasures full summary and mandatory anchors", () => Run(async () =>
        {
            var canonical = Start("summary + mandatory source anchors " + new string('C', 2500));
            using var big = new WireFixture("chat", Profile("chat", 8192)) { EmitTools = false };
            await using var first = big.Create(); await Collect(first.StartAsync(canonical));
            using var small = new WireFixture("chat", Profile("chat", 2048)) { EmitTools = false };
            await using var second = small.Create();
            await ExpectAsync<AgentRequestBudgetException>(() => Collect(second.StartAsync(canonical)));
            Check(big.Bodies.Count == 1 && small.Bodies.Count == 0 && canonical.Messages[0].Content.Length > 2500, "Rebase trimmed source or reused old cap.");
        }));
        test("AR-050 exact wire bound applies independently of a large context allowance", () =>
        {
            var guard = new AgentRequestBudgetGuard(Profile("chat").withBudget(Settings(100000) with { MaxSerializedBytes = 256 }));
            Check(Expect<AgentRequestBudgetException>(() => guard.Prepare(new() { ["messages"] = new string('x', 300) }, Guid.NewGuid(), Guid.NewGuid(), "chat")).Code == "request_wire_limit", "Wire cap bypassed.");
        });
        foreach (var kind in new[] { "stored", "ws" })
            test("AR-050 small native continuation cannot bypass large provider-held context " + kind, () => Run(async () =>
            {
                using var fixture = new WireFixture(kind, Profile(kind, 4096)) { ReportedInput = 3900 };
                await using var transport = fixture.Create(); var seen = new List<AgentRequestBudgetReceipt>(); Subscribe(transport, seen);
                var start = Start("first"); var events = await Collect(transport.StartAsync(start));
                var call = events.Single(e => e.Kind == AgentTransportEventKind.ToolCall).ToolCall!;
                await ExpectAsync<AgentRequestBudgetException>(() => Collect(transport.ContinueAsync(new(start.TaskId, start.TurnId, [new(call.Id, call.Name, "tiny")]))));
                Check(fixture.Bodies.Count == 1 && seen[^1].RetainedContextEstimate >= 3964 && !seen[^1].Allowed, "Server-held lineage missing.");
                Save(kind + "-retained-rejected", seen);
            }));
        test("AR-050 WebSocket prewarm and native start both have exact decisions with retained context", () => Run(async () =>
        {
            using var fixture = new WireFixture("ws", Profile("ws")) { EmitTools = false, Prewarm = true };
            await using var transport = fixture.Create(); var seen = new List<AgentRequestBudgetReceipt>(); Subscribe(transport, seen);
            await Collect(transport.StartAsync(Start("prewarm marker")));
            Check(fixture.Bodies.Count == 2 && seen.Count == 2 && seen[0].Kind == "Prewarm"
                && seen[1].RetainedContextEstimate >= seen[0].EstimatedInputTokens, "Prewarm lineage absent.");
            for (var i = 0; i < seen.Count; i++) AssertWire(fixture.Bodies[i], seen[i]);
            Save("prewarm", seen);
        }));
        test("AR-050 rejected WebSocket prewarm cannot be swallowed or trigger fallback", () => Run(async () =>
        {
            using var fixture = new WireFixture("ws", Profile("ws", 2048)) { Prewarm = true };
            await using var transport = fixture.Create(); var seen = new List<AgentRequestBudgetReceipt>(); Subscribe(transport, seen);
            await ExpectAsync<AgentRequestBudgetException>(() => Collect(transport.StartAsync(Start(new string('x', 3000)))));
            Check(fixture.Bodies.Count == 0 && seen.Single().Kind == "Prewarm", "Prewarm budget failure swallowed.");
        }));
        test("AR-050 concrete HTTP fallback retains serialization guard and forwards its receipt", () => Run(async () =>
        {
            var profile = Profile("ws", 2048);
            using var fixture = new WireFixture("responses", profile);
            await using var transport = new OpenAiResponsesWebSocketTransport(profile, "", () => new WireSocket(fixture, true),
                () => new OpenAiResponsesTransport(profile, "", fixture.Handler));
            var seen = new List<AgentRequestBudgetReceipt>(); Subscribe(transport, seen);
            await ExpectAsync<AgentRequestBudgetException>(() => Collect(transport.StartAsync(Start(new string('x', 4000)))));
            Check(fixture.Bodies.Count == 0 && seen.Count == 1 && transport.LastRequestBudget == seen[0], "Fallback bypass or metrics loss.");
        }));
        test("AR-050 cancelled or failed pre-send observer dispatches no body", () => Run(async () =>
        {
            using var fixture = new WireFixture("chat", Profile("chat")); await using var transport = fixture.Create();
            ((IAgentRequestBudgetSource)transport).RequestBudgetEvaluated += _ => throw new IOException("fixture-observer-failure");
            await ExpectAsync<IOException>(() => Collect(transport.StartAsync(Start("first"))));
            Check(fixture.Bodies.Count == 0, "Observer failure sent request.");
            using var other = new WireFixture("chat", Profile("chat")); await using var cancelled = other.Create();
            using var token = new CancellationTokenSource(); token.Cancel();
            await ExpectAsync<OperationCanceledException>(() => Collect(cancelled.StartAsync(Start("first"), token.Token)));
            Check(other.Bodies.Count == 0, "Cancelled request sent.");
        }));
        test("AR-050 factory records real pre-send budget in existing telemetry without network", () => Run(async () =>
        {
            var telemetry = new AgentRunTelemetry();
            await using var transport = new AgentTransportFactory().Create(Profile("ollama", 1024), "", telemetry);
            await ExpectAsync<AgentRequestBudgetException>(() => Collect(transport.StartAsync(Start(new string('x', 2000)))));
            var trace = JsonSerializer.Serialize(telemetry.Trace.Snapshot());
            Check(trace.Contains("outgoing-request-budget") && trace.Contains("request_budget_exceeded") && !trace.Contains(new string('x', 50)), "No safe budget telemetry.");
        }));
        foreach (var project in new[] { false, true })
            test("AR-050 production budget exhaustion is durable Blocked not completion " + (project ? "Project" : "Global"), () =>
            {
                var root = Path.Combine(Path.GetTempPath(), "h2-ar050-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
                var profile = Profile("ollama", 1024); var factory = new CountingFactory(); var state = Path.Combine(root, "state");
                Guid id;
                try
                {
                    using (var adapter = new H2ProductionAgentAdapter(state, () => new(profile, ""), factory))
                    {
                        id = adapter.StartTaskAsync(project ? Guid.NewGuid() : null, new string('G', 5000), new(root, "fixture only"), true).GetAwaiter().GetResult();
                        var done = Wait(adapter, id);
                        Check(done.Status == H2AgentTaskStatus.Blocked && done.Error?.Contains("request_budget_exceeded") == true, "Budget not a truthful blocker: " + done.Status + " " + done.Error);
                        Check(factory.Sends == 0, "Production overflow reached HTTP.");
                        adapter.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                    }
                    using var restored = new H2ProductionAgentAdapter(state, () => new(profile, ""), factory);
                    Check(restored.GetTaskSummary(id)?.Status == H2AgentTaskStatus.Blocked && factory.Sends == 0, "Archive reload replayed or erased budget failure.");
                    Save(project ? "production-project" : "production-global", new { id, status = "Blocked", sends = factory.Sends });
                }
                finally { try { Directory.Delete(root, true); } catch (IOException) { } }
            });
    }

    private static AiProfile withBudget(this AiProfile profile, AiRequestBudgetSettings settings) { profile.RequestBudget = settings; return profile; }
    private static AiRequestBudgetSettings Settings(int context) => new() { ContextLimitTokens = context, ContextLimitSource = "ar050-synthetic-fixture-v1",
        ReservedOutputTokens = 64, SafetyMarginTokens = 32, MaxSerializedBytes = 16 * 1024 * 1024 };
    private static AiProfile Profile(string kind, int context = 8_000_000) => new() { Model = "fixture-only-no-network",
        Protocol = kind == "ollama" ? AiProtocol.Ollama : kind is "chat" or "official-chat" ? AiProtocol.OpenAiChat : AiProtocol.OpenAiResponses,
        BaseUrl = kind == "ollama" ? "http://localhost:11434" : kind == "chat" ? "https://example.test/v1" : "https://api.openai.com/v1",
        RequestBudget = Settings(context) };
    private static AgentTransportStartRequest Start(string text) => new(Guid.NewGuid(), Guid.NewGuid(), [new(AgentTransportMessageRole.User, text)], []);
    private static JsonElement Parameters() => JsonSerializer.SerializeToElement(new { type = "object", properties = new { key = new { type = "string" } } });
    private static void Subscribe(IAgentTransport transport, List<AgentRequestBudgetReceipt> seen)
        => ((IAgentRequestBudgetSource)transport).RequestBudgetEvaluated += seen.Add;
    private static async Task LongCorpus(string kind)
    {
        using var fixture = new WireFixture(kind, Profile(kind)); await using var transport = fixture.Create();
        var seen = new List<AgentRequestBudgetReceipt>(); Subscribe(transport, seen);
        var start = Start(Marker) with { Tools = Enumerable.Range(0, 32).Select(i => new AgentToolDefinition("schema_" + i, new string('D', 120), Parameters())).Append(new("lookup", "lookup fixture", Parameters())).ToArray() };
        var events = await Collect(transport.StartAsync(start));
        string? lastId = null;
        for (var round = 1; round <= 204; round++)
        {
            var call = events.Single(e => e.Kind == AgentTransportEventKind.ToolCall).ToolCall!;
            Check(call.Id != lastId, "Response reused call identity."); lastId = call.Id;
            var output = "result-" + round + Marker + new string('r', 128);
            var extra = round % 20 == 0 ? new[] { new AgentToolDefinition("extra_" + round, new string('S', 180), Parameters()) } : null;
            events = await Collect(transport.ContinueAsync(new(start.TaskId, start.TurnId, [new(call.Id, call.Name, output)], extra, ["correction-" + round])));
            var body = JsonNode.Parse(fixture.Bodies[^1])!;
            var items = body[kind is "ollama" or "chat" ? "messages" : "input"]!.AsArray();
            Check(items.Any(x => x?["role"]?.GetValue<string>() == "user" && x?["content"]?.ToString() == "correction-" + round), "Supplement lost/changed role.");
            if (kind is "ollama" or "chat")
                Check(items.Any(x => x?["role"]?.GetValue<string>() == "tool" && x?["content"]?.ToString() == output
                    && (kind == "ollama" || x?["tool_call_id"]?.GetValue<string>() == call.Id)), "Tool result pair lost.");
            else Check(items.Any(x => x?["type"]?.GetValue<string>() == "function_call_output" && x?["call_id"]?.GetValue<string>() == call.Id && x?["output"]?.ToString() == output), "Native call output lost.");
        }
        Check(fixture.Bodies.Count == 205 && seen.Count == 205 && seen.All(x => x.Allowed && x.TaskId == start.TaskId && x.TurnId == start.TurnId), "200+ actual sends not observed.");
        for (var i = 0; i < seen.Count; i++) { AssertWire(fixture.Bodies[i], seen[i]); Check(seen[i].Sequence == i + 1, "Nonmonotonic budget sequence."); }
        if (kind is "ws" or "stored") Check(seen[^1].RetainedContextEstimate > seen[0].SerializedBytes, "Native lineage ignored.");
        Save(kind + "-205-serialized", seen);
        var pending = events.Single(e => e.Kind == AgentTransportEventKind.ToolCall).ToolCall!;
        await ExpectAsync<AgentRequestBudgetException>(() => Collect(transport.ContinueAsync(new(start.TaskId, start.TurnId, [new(pending.Id, pending.Name, new string('X', 8_000_001))]))));
        Check(fixture.Bodies.Count == 205 && !seen[^1].Allowed, "Large final observation bypassed guard.");
        Save(kind + "-large-output-rejected", seen[^1]);
    }
    private static void AssertWire(string body, AgentRequestBudgetReceipt receipt)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        Check(receipt.SerializedBytes == bytes.Length && receipt.PayloadSha256 == Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "Measured object differs from actual serialized send.");
        Check(receipt.EstimatedInputTokens + receipt.ReservedOutputTokens + receipt.SafetyMarginTokens <= receipt.ContextLimitTokens, "Known-over-limit send.");
    }
    private static async Task<List<AgentTransportEvent>> Collect(IAsyncEnumerable<AgentTransportEvent> source)
    { var result = new List<AgentTransportEvent>(); await foreach (var item in source) result.Add(item); return result; }
    private static void Run(Func<Task> body) => Task.Run(body).WaitAsync(TimeSpan.FromSeconds(90)).GetAwaiter().GetResult();
    private static T Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T ex) { return ex; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task<T> ExpectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T ex) { return ex; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Save(string name, object value)
    {
        var directory = Environment.GetEnvironmentVariable("H2_AR050_EVIDENCE_DIR"); if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }
    private static H2AgentTaskSummary Wait(H2ProductionAgentAdapter adapter, Guid id)
    {
        var watch = Stopwatch.StartNew(); while (watch.Elapsed < TimeSpan.FromSeconds(30))
        { var value = adapter.GetTaskSummary(id); if (value is not null && value.Status is H2AgentTaskStatus.Completed or H2AgentTaskStatus.Failed or H2AgentTaskStatus.Blocked or H2AgentTaskStatus.Cancelled) return value; Thread.Sleep(10); }
        adapter.CancelTask(id); throw new TimeoutException("Production fixture did not drain.");
    }
    private sealed class CountingFactory : IAgentTransportFactory
    {
        public int Sends;
        public IAgentTransport Create(AiProfile profile, string key, AgentRunTelemetry telemetry) => new OllamaTransport(profile, new Counter(this));
        private sealed class Counter(CountingFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            { owner.Sends++; throw new InvalidOperationException("Oversized fixture must not reach HTTP."); }
        }
    }
    private sealed class WireFixture : IDisposable
    {
        internal readonly string Kind; private readonly AiProfile _profile;
        public List<string> Bodies { get; } = []; public bool EmitTools = true; public bool Prewarm; public long ReportedInput = 20;
        public HttpMessageHandler Handler { get; }
        public WireFixture(string kind, AiProfile profile) { Kind = kind; _profile = profile; Handler = new WireHandler(this); }
        public IAgentTransport Create() => Kind switch {
            "ollama" => new OllamaTransport(_profile, Handler), "chat" => new ChatCompletionsTransport(_profile, "", Handler),
            "ws" => new OpenAiResponsesWebSocketTransport(_profile, "", () => new WireSocket(this), () => throw new InvalidOperationException("Unexpected fallback"), Prewarm),
            _ => new OpenAiResponsesTransport(_profile, "", Handler, Kind == "stored" ? OpenAiResponsesStateMode.StoredContinuation : OpenAiResponsesStateMode.Stateless) };
        internal string[] Reply()
        {
            var n = Bodies.Count; var warm = JsonNode.Parse(Bodies[^1])?["generate"]?.GetValue<bool>() == false;
            var calls = EmitTools && !warm;
            if (Kind == "ollama") return [JsonSerializer.Serialize(new { message = new { role = "assistant", content = calls ? "" : "OK",
                tool_calls = calls ? new[] { new { function = new { name = "lookup", arguments = new { } } } } : [] }, done = true, prompt_eval_count = ReportedInput, eval_count = 1 })];
            if (Kind == "chat") return [JsonSerializer.Serialize(new { choices = new[] { new { index = 0,
                delta = calls ? JsonSerializer.SerializeToElement(new { tool_calls = new[] { new { index = 0, id = "call_" + n, type = "function", function = new { name = "lookup", arguments = "{}" } } } })
                    : JsonSerializer.SerializeToElement(new { content = "OK" }), finish_reason = calls ? "tool_calls" : "stop" } } }), "[DONE]"];
            var output = warm ? new JsonArray() : calls ? new JsonArray(new JsonObject { ["type"] = "function_call", ["call_id"] = "call_" + n, ["name"] = "lookup", ["arguments"] = "{}" })
                : new JsonArray(new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "OK" }) });
            return [new JsonObject { ["type"] = "response.completed", ["response"] = new JsonObject { ["id"] = "resp_" + n, ["status"] = "completed", ["output"] = output,
                ["usage"] = new JsonObject { ["input_tokens"] = ReportedInput, ["output_tokens"] = 1, ["total_tokens"] = ReportedInput + 1,
                    ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = ReportedInput } } } }.ToJsonString()];
        }
        public void Dispose() => Handler.Dispose();
        private sealed class WireHandler(WireFixture owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                owner.Bodies.Add(await request.Content!.ReadAsStringAsync(token));
                var frames = owner.Reply(); var raw = owner.Kind == "ollama" ? string.Join("\n", frames) + "\n" : string.Join("", frames.Select(x => "data: " + x + "\n\n"));
                return new(HttpStatusCode.OK) { Content = new StringContent(raw, Encoding.UTF8, owner.Kind == "ollama" ? "application/x-ndjson" : "text/event-stream") };
            }
        }
    }
    private sealed class WireSocket(WireFixture owner, bool failConnect = false) : IResponsesWebSocketConnection
    {
        public bool IsOpen { get; private set; }
        public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); if (failConnect) throw new WebSocketException("fixture-connect-failed"); IsOpen = true; return Task.CompletedTask; }
        public Task SendTextAsync(string text, CancellationToken ct) { ct.ThrowIfCancellationRequested(); owner.Bodies.Add(text); return Task.CompletedTask; }
        public async IAsyncEnumerable<string> ReceiveTextAsync([EnumeratorCancellation] CancellationToken ct)
        { await Task.Yield(); foreach (var frame in owner.Reply()) { ct.ThrowIfCancellationRequested(); yield return frame; } }
        public Task CloseAsync(CancellationToken ct) { IsOpen = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
    }
}

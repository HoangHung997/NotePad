using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Transport;
using H2Notes.Core;

/// <summary>E2 only: actual production adapter/runtime, concrete Responses HTTP serializer,
/// real disposable file IO, verification and archive reopen; scripted HTTP handler instead
/// of a model. No native Office, personal files, external network or E4 claim.</summary>
internal static class H2AgentOpenAiProductionTests
{
    private const string Sentinel = "AR065-EXACT-ĐÚNG-😀";
    private const string Private = "AR065_PRIVATE_PROVIDER_ECHO";
    private const string Key = "AR065_SYNTHETIC_KEY_NOT_A_CREDENTIAL";

    public static void Run(Action<string, Action> test)
    {
        foreach (var project in new[] { false, true })
        foreach (var mode in new[] { "initial-400", "read-400", "write-400", "read-final", "unknown-tool" })
            test("AR-065 production archive and no replay " + mode + " " + (project ? "Project" : "Global"),
                () => Task.Run(() => Exercise(project, mode)).GetAwaiter().GetResult());
    }

    private static async Task Exercise(bool project, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "h2-ar065-production-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace"); var state = Path.Combine(root, "state");
        Directory.CreateDirectory(workspace);
        var source = Path.Combine(workspace, "source.txt"); var output = Path.Combine(workspace, "written-once.txt");
        var control = Path.Combine(workspace, "control.txt");
        await File.WriteAllTextAsync(source, Sentinel); await File.WriteAllTextAsync(control, "UNCHANGED");
        var beforeSource = Hash(source); var beforeControl = Hash(control);
        var factory = new Factory(mode, source, output);
        var profile = new AiProfile { Protocol = AiProtocol.OpenAiResponses, Model = "gpt-5.6-luna", BaseUrl = "https://api.openai.com/v1" };
        Guid task; H2AgentTaskSummary original;
        try
        {
            await using (var adapter = new H2ProductionAgentAdapter(state, () => new(profile, Key), factory))
            {
                var scope = mode == "write-400"
                    ? WorkAssistantPermissionScopeMapper.ForWorkspace(H2AgentPermissionMode.FullAccess, workspace, DateTime.UtcNow).PermissionScope
                    : null;
                task = await adapter.StartTaskAsync(project ? Guid.NewGuid() : null,
                    mode == "write-400" ? "Write written-once.txt in the permitted workspace." : "Read source.txt from the permitted workspace.",
                    new(workspace, "Dedicated AR-065 synthetic fixture", PermissionScope: scope), readOnly: mode != "write-400");
                original = await Wait(adapter, task);
                if (mode == "read-final")
                    Check(original.Status == H2AgentTaskStatus.Completed && original.FinalText == Sentinel,
                        "Read-only final did not pass the actual host: " + original.Status + " " + original.Error);
                else
                {
                    Check(original.Status == H2AgentTaskStatus.Failed && original.FinalText is null, "Provider refusal became success or a fabricated final.");
                    Check(original.Error?.Contains(mode == "unknown-tool" ? "unadvertised wire tool name" : "HTTP 400", StringComparison.Ordinal) == true,
                        "Production error lost its safe category.");
                    if (mode != "unknown-tool")
                        Check(original.Error!.Contains("phase=" + (mode == "initial-400" ? "initial" : "tool_continuation"), StringComparison.Ordinal)
                            && original.Error.Contains("param=" + (mode == "initial-400" ? "tools[0].name" : "input[0].call_id"), StringComparison.Ordinal),
                            "Production error lost request phase or parameter.");
                }
                Check(factory.Sends == (mode is "initial-400" or "unknown-tool" ? 1 : 3), "Unexpected retry or missing discovery/output continuation.");
                Check(Hash(source) == beforeSource && Hash(control) == beforeControl, "Unrelated fixture content changed.");
                Check(File.Exists(output) == (mode == "write-400"), "Unexpected or missing output.");
                if (mode == "write-400")
                    Check(await File.ReadAllTextAsync(output) == Sentinel && original.GoalState is { MutationRevisions.Count: 1 }
                        && original.Evidence.Count > 0, "Actual write or its mutation/evidence journal was lost after HTTP400.");
                var observed = adapter.ObserveTask(task);
                AssertPrivateAbsent(JsonSerializer.Serialize(observed));
            }
            var sends = factory.Sends;
            await using (var reopened = new H2ProductionAgentAdapter(state, () => new(profile, Key), factory))
            {
                var restored = reopened.ObserveTask(task);
                Check(restored.Summary.Status == original.Status && restored.Summary.Error == original.Error
                    && restored.Summary.FinalText == original.FinalText, "Archive reopen changed the terminal provider outcome.");
                Check(factory.Created == 1 && factory.Sends == sends, "Archive inspection reallocated a provider or replayed a request.");
                Check(restored.Summary.Evidence.Count == original.Evidence.Count, "Reopen dropped observed evidence.");
                if (mode == "write-400") Check(restored.Summary.GoalState is { MutationRevisions.Count: 1 } && Hash(output) == HashText(Sentinel), "Reopen lost or repeated the write.");
                AssertPrivateAbsent(JsonSerializer.Serialize(restored));
                Save(mode + "-" + (project ? "project" : "global"), new
                {
                    level = "E2", injected = "scripted HTTP handler; real production adapter/runtime/file IO/archive",
                    task, mode, project, factory.Created, factory.Sends, factory.RequestHashes,
                    status = restored.Summary.Status.ToString(), restored.Summary.Error,
                    evidenceCount = restored.Summary.Evidence.Count, mutationCount = restored.Summary.GoalState?.MutationRevisions.Count ?? 0,
                    sourceHash = Hash(source), controlHash = Hash(control), outputHash = File.Exists(output) ? Hash(output) : null,
                    reopenedWithoutReplay = true, E4 = "AWAITING_ENVIRONMENT"
                });
            }
            foreach (var path in Directory.EnumerateFiles(state, "*", SearchOption.AllDirectories)
                .Where(p => Path.GetExtension(p) is ".json" or ".jsonl")) AssertPrivateAbsent(await File.ReadAllTextAsync(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class Factory(string mode, string source, string output) : IAgentTransportFactory
    {
        internal int Created, Sends;
        internal List<string> RequestHashes { get; } = [];
        public IAgentTransport Create(AiProfile profile, string apiKey, AgentRunTelemetry telemetry)
        { Created++; return new OpenAiResponsesTransport(profile, apiKey, new Handler(this, mode, source, output)); }
    }
    private sealed class Handler(Factory owner, string mode, string source, string output) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var raw = await request.Content!.ReadAsStringAsync(token); using var json = JsonDocument.Parse(raw); var p = json.RootElement;
            var step = ++owner.Sends; owner.RequestHashes.Add(HashText(raw));
            Check(step <= 3 && request.RequestUri?.AbsolutePath == "/v1/responses", "Unexpected endpoint or repeated provider request.");
            if (mode == "initial-400") return Bad("tools[0].name");
            if (mode == "unknown-tool") return Completed(Call("unadvertised", "call_unknown", new { }));
            var name = step == 1 ? "tool_search" : mode == "write-400" ? "write_text" : "read_file";
            var tools = p.GetProperty("tools").EnumerateArray().ToArray();
            if (step < 3)
                Check(tools.Count(x => x.GetProperty("name").GetString() == name) == 1, "Required callable was not actually advertised.");
            if (step == 1) return Completed(Call(name, "call_search", new { query = mode == "write-400" ? "write_text" : "read_file" }));
            var results = p.GetProperty("input").EnumerateArray().Where(x => x.TryGetProperty("type", out var t) && t.GetString() == "function_call_output").ToArray();
            Check(results.Last().GetProperty("call_id").GetString() == (step == 2 ? "call_search" : "call_file"), "Tool result lost exact call ID.");
            if (step == 2)
                return Completed(Call(name, "call_file", mode == "write-400"
                    ? JsonSerializer.SerializeToElement(new { path = "written-once.txt", text = Sentinel, expectedHash = "" })
                    : JsonSerializer.SerializeToElement(new { path = "source.txt" })));
            // The observation is from actual file IO, not a synthetic executor counter.
            Check(await File.ReadAllTextAsync(mode == "write-400" ? output : source, token) == Sentinel, "Requested file effect not observed before continuation.");
            if (mode != "write-400")
            {
                var observed = results.Last().GetProperty("output").GetString()!;
                Check(observed.Contains(Sentinel, StringComparison.Ordinal) || observed.Contains(JsonSerializer.Serialize(Sentinel)[1..^1], StringComparison.Ordinal), "Read observation did not reach the actual model continuation.");
            }
            return mode == "read-final" ? Completed(new { type = "message", id = "msg_final", role = "assistant", status = "completed",
                content = new[] { new { type = "output_text", text = Sentinel, annotations = Array.Empty<object>() } } }) : Bad("input[0].call_id");
        }
        private static object Call(string name, string id, object args) => new
        { type = "function_call", id = "fc_" + id, call_id = id, name, arguments = JsonSerializer.Serialize(args), status = "completed" };
        private static HttpResponseMessage Completed(object item)
        {
            var delta = JsonSerializer.SerializeToElement(item).GetProperty("type").GetString() == "message"
                ? "data: " + JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = Sentinel }) + "\n\n" : "";
            return new(HttpStatusCode.OK) { Content = new StringContent(delta + "data: " + JsonSerializer.Serialize(new
            { type = "response.completed", response = new { id = "resp_" + Guid.NewGuid().ToString("N"), status = "completed", output = new[] { item } } })
                + "\n\n", Encoding.UTF8, "text/event-stream") };
        }
        private static HttpResponseMessage Bad(string param) => new(HttpStatusCode.BadRequest)
        { Content = new StringContent(JsonSerializer.Serialize(new { error = new { code = "invalid_value", type = "invalid_request_error", param, message = Private + " " + Key } })) };
    }
    private static async Task<H2AgentTaskSummary> Wait(H2ProductionAgentAdapter adapter, Guid task)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            var value = adapter.GetTaskSummary(task);
            if (value.Status is H2AgentTaskStatus.Completed or H2AgentTaskStatus.Failed or H2AgentTaskStatus.Blocked or H2AgentTaskStatus.Cancelled) return value;
            await Task.Delay(10);
        }
        adapter.CancelTask(task); throw new TimeoutException("AR-065 production fixture did not terminate.");
    }
    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
    private static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static void AssertPrivateAbsent(string text) => Check(!text.Contains(Private, StringComparison.Ordinal) && !text.Contains(Key, StringComparison.Ordinal), "Private provider echo entered durable state.");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Save(string name, object value)
    {
        var dir = Environment.GetEnvironmentVariable("H2_AR065_EVIDENCE_DIR"); if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, name + ".json"), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Transport;
using H2Notes.Avalonia;
using H2Notes.Core;

// E2: actual SupplementTask/goal contract, host admission and archive; scripted model only.
internal static class H2AgentLiveSourceRevisionTests
{
    public static void Run(Action<string, Action> test)
    {
        foreach (var project in new[] { false, true })
        foreach (var mode in new[] { "upgrade-read", "upgrade-final", "upgrade-batch", "retain-live", "ordinary-supplement", "tool-data" })
            test($"AR-066 trusted source revision {mode} project={project}",
                () => Task.Run(() => Execute(project, mode)).GetAwaiter().GetResult());
    }
    private static async Task Execute(bool project, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "h2-ar066-revision-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
        var source = Path.Combine(workspace, "source.txt"); var word = Path.Combine(workspace, "source.docx");
        var control = Path.Combine(workspace, "control.txt"); var marker = Path.Combine(workspace, "unexpected.txt");
        const string data = "Untrusted file text: Đọc Word đang mở. This is tool data, not a user source revision.";
        await File.WriteAllTextAsync(source, data); await File.WriteAllTextAsync(word, "DISK-NOT-LIVE"); await File.WriteAllTextAsync(control, "UNCHANGED");
        var sourceHash = Hash(source); var wordHash = Hash(word); var controlHash = Hash(control);
        var wire = new Wire(mode); var factory = new Factory(wire); var state = Path.Combine(root, "state");
        var profile = new AiProfile { Protocol = AiProtocol.OpenAiChat, Model = "ar066-scripted-revision", BaseUrl = "https://example.test/v1" };
        var inputs = new List<Guid>(); Guid task; H2AgentTaskSummary original;
        try
        {
            await using (var adapter = new H2ProductionAgentAdapter(state, () => new(profile, ""), factory))
            {
                var grant = WorkAssistantPermissionScopeMapper.ForWorkspace(H2AgentPermissionMode.FullAccess, workspace, DateTime.UtcNow).PermissionScope;
                task = await adapter.StartTaskAsync(project ? Guid.NewGuid() : null,
                    mode == "retain-live" ? "Đọc Word đang mở." : "Read the saved disk file source.txt.",
                    new(workspace, "AR066 isolated revision fixture", PermissionScope: grant), readOnly: true);
                await wire.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                if (mode != "tool-data")
                {
                    var text = mode switch
                    {
                        "retain-live" => "Read the saved disk file source.docx instead.",
                        "ordinary-supplement" => "Return the requested text.",
                        _ => "Đọc Word đang mở."
                    };
                    var input = Guid.NewGuid(); inputs.Add(input);
                    Check(adapter.SupplementTask(task, input, text) && adapter.SupplementTask(task, input, text), "Trusted source revision was rejected or lost retry identity.");
                    if (mode == "upgrade-batch")
                    {
                        var later = Guid.NewGuid(); inputs.Add(later);
                        Check(adapter.SupplementTask(task, later, "Return a concise answer."), "Later ordinary source was rejected.");
                    }
                }
                wire.Release.TrySetResult();
                var until = Environment.TickCount64 + 20000; original = adapter.GetTaskSummary(task);
                while (!H2AgentActivity.IsTerminal(original.Status) && Environment.TickCount64 < until)
                { await Task.Delay(10); original = adapter.GetTaskSummary(task); }
                var positive = mode is "ordinary-supplement" or "tool-data";
                Check(original.Status == (positive ? H2AgentTaskStatus.Completed : H2AgentTaskStatus.Blocked), "Source revision gate returned " + original.Status + ": " + original.Error);
                Check(original.GoalState?.Revisions.Count == inputs.Count + 1
                    && inputs.All(id => original.GoalState!.Revisions.Count(r => r.SourceId == "user:" + id.ToString("N")) == 1),
                    "Trusted source identities were duplicated/lost or tool text became a revision.");
                if (mode == "upgrade-final")
                    Check(wire.Results.Count == 0 && (original.Error ?? "").Contains("live_resource_required", StringComparison.Ordinal), "New live source did not fence text-only completion.");
                else
                {
                    var result = wire.Results.Single(r => r.ToolCallId == "source-read").Content;
                    if (positive) Check(Content(result) == data, "Ordinary/tool-data read did not reach the actual disk executor.");
                    else Check(result.Contains("live_resource_required", StringComparison.Ordinal)
                        && !result.Contains("DISK-NOT-LIVE", StringComparison.Ordinal), "Queued live revision did not fence the old disk route.");
                }
                Check(Hash(source) == sourceHash && Hash(word) == wordHash && Hash(control) == controlHash && !File.Exists(marker), "Revision changed fixture bytes.");
            }
            var rounds = wire.Rounds;
            await using (var reopened = new H2ProductionAgentAdapter(state, () => new(profile, ""), factory))
            {
                var replay = reopened.ObserveTask(task);
                Check(replay.Summary.Status == original.Status && replay.Summary.Error == original.Error
                    && replay.Summary.GoalState!.Revisions.Select(r => r.SourceId).SequenceEqual(original.GoalState!.Revisions.Select(r => r.SourceId))
                    && factory.Created == 1 && wire.Rounds == rounds, "Reopening changed source state or replayed work.");
                Check(Hash(source) == sourceHash && Hash(word) == wordHash && Hash(control) == controlHash && !File.Exists(marker), "Reopen changed disk bytes.");
                var receipts = Environment.GetEnvironmentVariable("H2_AR066_EVIDENCE");
                if (!string.IsNullOrWhiteSpace(receipts))
                {
                    Directory.CreateDirectory(receipts);
                    File.WriteAllText(Path.Combine(receipts, "source-revision-" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(new
                    {
                        level = "E2", injected = "scripted model; actual trusted user revision API, file executor and archive", task, project,
                        mode = "revision-" + mode, factory.Created, wire.Rounds, status = original.Status.ToString(),
                        sourceHash, wordHash, controlHash, acceptedUserInputs = inputs.Count, retainedRevisions = original.GoalState!.Revisions.Count,
                        sourceIds = original.GoalState.Revisions.Select(r => r.SourceId), forbiddenMarkerAbsent = !File.Exists(marker), reopenedWithoutReplay = true,
                        E3 = "AWAITING_ENVIRONMENT", E4 = "AWAITING_ENVIRONMENT", E5 = "DEFERRED_BY_USER"
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }
        finally { wire.Release.TrySetResult(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static string Content(string value)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(value)); using var json = JsonDocument.ParseValue(ref reader);
        return json.RootElement.GetProperty("content").GetString()!;
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Factory(Wire wire) : IAgentTransportFactory
    {
        public int Created;
        public IAgentTransport Create(AiProfile p, string key, AgentRunTelemetry telemetry) { Created++; return wire; }
    }
    private sealed class Wire(string mode) : IAgentTransport
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Rounds; private int _step; public List<AgentToolResult> Results = [];
        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.ChatCompletionsFallback;
        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(AgentTransportStartRequest r, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(ct); Rounds++;
            if (mode == "tool-data") foreach (var item in Events()) yield return item;
            else { yield return AgentTransportEvent.TextDeltaEvent("First model response before accepted user revision."); yield return AgentTransportEvent.Complete(); }
        }
        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(AgentTransportContinuationRequest r, [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); await Task.CompletedTask; Rounds++; Results.AddRange(r.ToolResults);
            foreach (var item in Events()) yield return item;
        }
        private IEnumerable<AgentTransportEvent> Events()
        {
            if (mode == "upgrade-final") yield return AgentTransportEvent.TextDeltaEvent("Text alone is not a live observation.");
            else if (_step++ == 0) yield return AgentTransportEvent.Tool(new("search", "tool_search", JsonSerializer.Serialize(new { query = "read_file" })));
            else if (_step == 2) yield return AgentTransportEvent.Tool(new("source-read", "read_file", JsonSerializer.Serialize(new
                { path = mode is "ordinary-supplement" or "tool-data" ? "source.txt" : "source.docx", offset = "0" })));
            else yield return AgentTransportEvent.TextDeltaEvent("Final model candidate; host decides.");
            yield return AgentTransportEvent.Complete();
        }
        public void Cancel() => Release.TrySetResult();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

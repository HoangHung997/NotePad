using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using H2AgentLab;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Session;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2Notes.Avalonia;
using H2Notes.Core;

/// <summary>AR-031 E1 + concrete local archive/runtime/process fixtures. No Office, model or NAS.
/// Crash probes terminate only this runner's dedicated child. All paths are newly allocated temp roots.</summary>
internal static partial class H2AgentArchiveJournalTests
{
    internal static void Run(Action<string, Action> test)
    {
        RunBoundaryTests(test);
        test("AR-031 source journal preserves revisions progress and exact evidence after restart", () => Fixture(root =>
        {
            var task = Summary(); var thread = new H2AgentThread(task.ThreadId!.Value, null, "history", task.CreatedUtc, task.CreatedUtc, "draft");
            using (var archive = new AgentIntegrationTaskArchive(root))
            {
                archive.Upsert(task); archive.SaveThread(thread);
                archive.AppendProgress(task.TaskId, new(0, DateTime.UtcNow, "user", "supplement-received", "Keep formula =A1+$B$2"));
                archive.Upsert(task with { GoalState = Goals(), FinalText = new string('X', 9000) + "EXACT-TAIL" });
            }
            using var restored = new AgentIntegrationTaskArchive(root);
            Check(restored.Status.CanWrite && restored.Get(task.TaskId)!.FinalText!.EndsWith("EXACT-TAIL"), "Full final output was trimmed.");
            Check(restored.Get(task.TaskId)!.GoalState!.Outcomes[0].Requirement == "Keep =A1+$B$2", "Requirement was approximated.");
            Check(restored.ReadProgress(task.TaskId, -1).Single().Message.Contains("=A1+$B$2"), "Progress lost source text.");
            Check(restored.Threads().Single().Draft == "draft", "Thread draft was lost.");
            var journal = restored.ReadJournal(task.TaskId);
            Check(journal.Any(e => e.Kind == "revision") && journal.Select(e => e.EventId).Distinct().Count() == journal.Count, "Revision source/IDs missing.");
        }));
        test("AR-031 migration validates legacy tasks threads and progress without rewriting originals", () => Fixture(root =>
        {
            var t = Summary(); var originals = Legacy(root, t);
            using (var archive = new AgentIntegrationTaskArchive(root))
            {
                Check(archive.Status.CanWrite && archive.Get(t.TaskId)!.FinalText == "legacy-output", "Migration lost a task.");
                Check(archive.Threads().Single().Draft == "UNSENT" && archive.ReadProgress(t.TaskId, -1).Count == 1, "Legacy thread/progress lost.");
                archive.Upsert(t with { FinalText = "new-v2-output" });
            }
            foreach (var item in originals) Check(File.ReadAllBytes(item.Key).SequenceEqual(item.Value), "Legacy rollback source changed.");
            using var next = new AgentIntegrationTaskArchive(root);
            Check(next.Get(t.TaskId)!.FinalText == "new-v2-output", "Reopen imported stale legacy data again.");
            Check(JsonSerializer.Deserialize<H2AgentTaskSummary>(originals.Single(p => p.Key.EndsWith(t.TaskId.ToString("N") + ".json")).Value)!.FinalText == "legacy-output", "Pre-migration copy not independently readable.");
        }));
        foreach (var mode in new[] { "missing", "torn", "wrong-index" })
            test("AR-031 RC-34 rebuilds " + mode + " checkpoint from verified source", () => Fixture(root =>
            {
                var t = Summary(); using (var a = new AgentIntegrationTaskArchive(root)) a.Upsert(t);
                var checkpoint = Path.Combine(root, "journal-v2", "checkpoint.json");
                if (mode == "missing") File.Delete(checkpoint);
                else if (mode == "torn") File.WriteAllText(checkpoint, "{broken");
                else File.WriteAllText(checkpoint, File.ReadAllText(checkpoint).Replace(t.TaskId.ToString(), Guid.NewGuid().ToString()));
                using var a2 = new AgentIntegrationTaskArchive(root);
                Check(a2.Status.CanWrite && a2.Status.State == "RecoveredWithDiagnostics" && a2.Get(t.TaskId)!.FinalText == t.FinalText, "Index corruption erased authoritative work.");
                Check(File.Exists(checkpoint), "Index was not rebuilt.");
            }));
        foreach (var mode in new[] { "first-record", "middle-record", "missing-tail", "future-schema" })
            test("AR-031 RC-34 " + mode + " source is not healthy empty or auto-replayed", () => Fixture(root =>
            {
                var t = Summary(H2AgentTaskStatus.Running);
                using (var a = new AgentIntegrationTaskArchive(root)) { a.Upsert(t); a.Upsert(t with { FinalText = "later" }); }
                var entries = Events(root); var path = mode == "first-record" ? entries[0] : entries[^1];
                if (mode == "missing-tail") File.Delete(path);
                else if (mode == "future-schema") File.WriteAllText(path, File.ReadAllText(path).Replace("\"Schema\":2", "\"Schema\":3"));
                else File.WriteAllText(path, "{torn");
                var bad = File.Exists(path) ? File.ReadAllBytes(path) : null;
                using var a2 = new AgentIntegrationTaskArchive(root);
                Check(!a2.Status.CanWrite && a2.Status.State == "RecoveryRequired" && a2.Status.Diagnostics.Count > 0, "Corrupt source was accepted as healthy.");
                Reject<IOException>(() => a2.Upsert(t));
                if (bad is not null) Check(File.ReadAllBytes(path).SequenceEqual(bad), "Corrupt original was destroyed.");
                var recovered = a2.Get(t.TaskId);
                if (recovered is not null) Check(recovered.Status == H2AgentTaskStatus.Blocked && recovered.Recovery!.ReconcileRequired, "Recovered prefix pretended to be current live work.");
            }));
        test("AR-031 valid JSON with altered payload hash is rejected without rewriting source", () => Fixture(root =>
        {
            var task = Summary(); using (var a = new AgentIntegrationTaskArchive(root)) a.Upsert(task);
            var path = Events(root)[0]; var old = File.ReadAllText(path);
            File.WriteAllText(path, old.Replace("Archive fixture", "Changed fixture"));
            using var restored = new AgentIntegrationTaskArchive(root);
            Check(!restored.Status.CanWrite && restored.Status.State == "RecoveryRequired", "Self-consistent JSON bypassed journal payload hash validation.");
        }));
        test("AR-031 malformed legacy task and future legacy schema block migration without empty success", () => Fixture(root =>
        {
            var t = Summary(); var files = Legacy(root, t); var path = files.Keys.Single(p => p.EndsWith(t.TaskId.ToString("N") + ".json"));
            File.WriteAllText(path, "{broken");
            using (var a = new AgentIntegrationTaskArchive(root)) Check(!a.Status.CanWrite, "Bad durable legacy task was silently dropped.");
            File.WriteAllBytes(path, files[path]);
            File.WriteAllText(Path.Combine(root, "recent-tasks-v1.json"), "{\"Schema\":99,\"Tasks\":[]}");
            using var a2 = new AgentIntegrationTaskArchive(root);
            Check(!a2.Status.CanWrite && !Directory.Exists(Path.Combine(root, "journal-v2")), "Future schema was replaced by v2.");
        }));
        test("AR-031 corrupt legacy recent index is rebuilt only when durable task sources exist", () => Fixture(root =>
        {
            var t = Summary(); Legacy(root, t); var path = Path.Combine(root, "recent-tasks-v1.json"); File.WriteAllText(path, "{bad-index");
            using var a = new AgentIntegrationTaskArchive(root);
            Check(a.Status.CanWrite && a.Get(t.TaskId) is not null && a.Status.Diagnostics.Any(x => x.Contains("legacy-index-rebuilt")), "Durable legacy task was ignored with broken cache.");
            Check(File.ReadAllText(path) == "{bad-index", "Old cache was overwritten.");
        }));
        test("AR-031 torn legacy progress preserves source and requires recovery", () => Fixture(root =>
        {
            var t = Summary(); Legacy(root, t); var path = Path.Combine(root, "task-records", t.TaskId.ToString("N") + ".jsonl");
            File.AppendAllText(path, "{unfinished"); var bytes = File.ReadAllBytes(path);
            using var a = new AgentIntegrationTaskArchive(root);
            Check(!a.Status.CanWrite && File.ReadAllBytes(path).SequenceEqual(bytes), "Torn progress was silently skipped.");
        }));
        test("AR-031 retention keeps old active task evidence beyond the recent index", () => Fixture(root =>
        {
            var artifact = new ArtifactStore(root).StoreText(AgentArtifactKind.ToolOutput, "source:retention", "fixture", "REFERENCE-CONTENT", "reference", 1);
            var task = Summary(H2AgentTaskStatus.Running) with { Evidence = [new(artifact.Handle.Id, "ArtifactHash", artifact.Handle.Sha256, "fixture-reference")] };
            using (var a = new AgentIntegrationTaskArchive(root, new(RecentLimit: 2)))
            {
                a.Upsert(task);
                for (var i = 0; i < 230; i++) a.Upsert(Summary() with { UpdatedUtc = DateTime.UtcNow.AddMinutes(i) });
                Check(a.Recent(null, 5000).Count == 231 && a.GetEvidence(artifact.Handle.Id) is not null, "Recent trimming erased source references.");
            }
            using var a2 = new AgentIntegrationTaskArchive(root, new(RecentLimit: 2));
            Check(a2.Get(task.TaskId)!.Recovery!.Interrupted && a2.GetEvidence(artifact.Handle.Id) is not null, "Active work was lost after retention/restart.");
            Check(Directory.EnumerateFiles(Path.Combine(root, "artifacts"), "*.txt", SearchOption.AllDirectories).Any(p => File.ReadAllText(p) == "REFERENCE-CONTENT"), "Referenced bytes were deleted.");
            var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "journal-v2", "checkpoint.json")));
            Check(index.RootElement.GetProperty("Index").GetProperty("Recent").GetArrayLength() == 2, "Hot index quota ignored.");
        }));
        test("AR-031 journal quota rejects before writing and never truncates existing history", () => Fixture(root =>
        {
            using var a = new AgentIntegrationTaskArchive(root, new(MaxEvents: 2)); var t = Summary();
            a.Upsert(t); a.Upsert(t with { FinalText = "second" }); var files = Events(root).Select(File.ReadAllBytes).ToArray();
            Reject<IOException>(() => a.Upsert(t with { FinalText = "overflow" }));
            Check(a.Get(t.TaskId)!.FinalText == "second" && Events(root).Length == 2 && Events(root).Select(File.ReadAllBytes).Zip(files).All(p => p.First.SequenceEqual(p.Second)), "Quota dropped accepted data.");
        }));
        test("AR-031 duplicate progress ack is idempotent and conflicting sequence is rejected", () => Fixture(root =>
        {
            using var a = new AgentIntegrationTaskArchive(root); var t = Summary(); a.Upsert(t);
            var p = new H2AgentProgress(0, DateTime.UtcNow, "user", "source", "literal"); a.AppendProgress(t.TaskId, p); var sequence = a.Status.LastSequence;
            a.AppendProgress(t.TaskId, p); Check(a.Status.LastSequence == sequence, "Same progress appended twice.");
            Reject<InvalidDataException>(() => a.AppendProgress(t.TaskId, p with { Message = "changed" }));
        }));
        test("AR-031 checkpoint activation failure retains committed journal and last valid checkpoint", () => Fixture(root =>
        {
            var armed = false; var t = Summary();
            using (var a = new AgentIntegrationTaskArchive(root, new(CheckpointEvery: 1), step => { if (armed && step == "checkpoint-before-activate") throw new IOException("injected"); }))
            {
                a.Upsert(t); var old = File.ReadAllBytes(Path.Combine(root, "journal-v2", "checkpoint.json")); armed = true;
                a.Upsert(t with { FinalText = "committed-before-checkpoint" });
                Check(a.Status.CanWrite && File.ReadAllBytes(Path.Combine(root, "journal-v2", "checkpoint.json")).SequenceEqual(old), "Invalid candidate replaced valid checkpoint.");
            }
            using var restored = new AgentIntegrationTaskArchive(root);
            Check(restored.Status.CanWrite && restored.Get(t.TaskId)!.FinalText == "committed-before-checkpoint", "Checkpoint failure rolled back a committed result.");
        }));
        test("AR-031 journal activation followed by lost acknowledgement fences writer until validated reload", () => Fixture(root =>
        {
            var armed = false; var t = Summary();
            using (var a = new AgentIntegrationTaskArchive(root, fault: step => { if (armed && step == "journal-activated") throw new IOException("injected"); }))
            {
                a.Upsert(t); armed = true; Reject<IOException>(() => a.Upsert(t with { FinalText = "durable-unacknowledged" }));
                Check(!a.Status.CanWrite, "Writer continued after uncertain journal acknowledgement.");
            }
            using var next = new AgentIntegrationTaskArchive(root);
            Check(next.Get(t.TaskId)!.FinalText == "durable-unacknowledged" && next.Status.CanWrite, "Durable unacknowledged source was lost.");
        }));
        test("AR-031 two local processes cannot own the same archive writer", () => Fixture(root =>
        {
            using var archive = new AgentIntegrationTaskArchive(root);
            Check(Child(root, "lock") == 89 && archive.Status.CanWrite, "Second physical process acquired the archive.");
        }));
        foreach (var mode in new[] { "before-intent", "after-intent", "after-dispatch", "after-effect", "after-result", "checkpoint-temp" })
            test("AR-031 RC-22 real child crash " + mode + " preserves observed boundary without replay", () => Fixture(root =>
            {
                Check(Child(root, mode) == 86, "Controlled child did not terminate at its requested boundary.");
                var taskId = Guid.Parse(File.ReadAllText(Path.Combine(root, "fixture-task-id")));
                using var restored = new AgentIntegrationTaskArchive(root);
                var task = restored.Get(taskId)!; Check(restored.Status.CanWrite && task.Recovery!.Interrupted && task.Status == H2AgentTaskStatus.Blocked, "Crash recovery lost interrupted work.");
                var operation = task.Recovery.Operations.SingleOrDefault();
                if (mode == "before-intent") Check(operation is null, "Undispatched operation was invented.");
                else if (mode == "after-intent") Check(operation!.State == "Prepared" && !task.Recovery.ReconcileRequired, "Prepared intent became an applied write.");
                else if (mode is "after-dispatch" or "after-effect") Check(operation!.State == "Dispatched" && task.Recovery.ReconcileRequired, "Unresolved dispatch was reported as known success.");
                else Check(operation!.State == "Result" && operation.Effect == "Applied", "Recorded effect was lost at restart.");
                var effect = Path.Combine(root, "effect.txt"); var expected = mode is "after-effect" or "after-result" or "checkpoint-temp" ? "ONE" : "";
                Check((File.Exists(effect) ? File.ReadAllText(effect) : "") == expected, "Recovery replayed or erased a side effect.");
                Save(mode, new { State = restored.Status, task, ExpectedEffect = expected, ObservedEffect = File.Exists(effect) ? File.ReadAllText(effect) : "" });
            }));
        test("AR-031 RC-34 killed migration preserves rollback bytes and reimports exactly once", () => Fixture(root =>
        {
            var t = Summary(); var files = Legacy(root, t); Check(Child(root, "migration") == 86, "Migration control did not stop.");
            Check(!Directory.Exists(Path.Combine(root, "journal-v2")), "Unactivated migration became authoritative.");
            using var restored = new AgentIntegrationTaskArchive(root);
            Check(restored.Status.CanWrite && restored.Recent(null, 500).Count == 1, "Interrupted import duplicated or lost history.");
            foreach (var file in files) Check(File.ReadAllBytes(file.Key).SequenceEqual(file.Value), "Migration changed rollback source.");
        }));
        foreach (var project in new[] { false, true })
            test("AR-031 concrete " + (project ? "Project" : "Global") + " runtime journals dispatch result verification and reloads", () => Fixture(root =>
            {
                var wire = new Wire(); var state = Path.Combine(root, "state"); Guid taskId;
                using (var a = Adapter(state, wire))
                {
                    var context = new H2AgentTaskContext(root, "Dedicated fixture", PermissionScope: WorkAssistantPermissionScopeMapper.ForWorkspace(H2AgentPermissionMode.FullAccess, root, DateTime.UtcNow).PermissionScope);
                    taskId = a.StartTaskAsync(project ? Guid.NewGuid() : null, "Write result.txt with literal content", context, false).Result;
                    var done = Wait(a, taskId); Check(done.Status == H2AgentTaskStatus.Completed, done.Error ?? "Task did not finish.");
                    a.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
                }
                using var reopened = Adapter(state, new Wire()); var task = reopened.GetTaskSummary(taskId);
                Check(task.Recovery!.Operations.Count == 1 && task.Recovery.Operations[0].Effect == "Applied", "Production bridge omitted operation receipt.");
                Check(File.ReadAllText(Path.Combine(root, "result.txt")) == "LITERAL =A1+$B$2", "Concrete file output differs.");
                Check(reopened.ObserveTask(taskId).Progress.Any(p => p.ToolOutcome is not null), "Journal progress lost typed outcomes.");
                var source = Directory.EnumerateFiles(Path.Combine(state, "integration", "journal-v2"), "event-*.json").Select(File.ReadAllText).ToArray();
                Check(source.Any(s => s.Contains("operation-intent")) && source.Any(s => s.Contains("operation-dispatched")) && source.Any(s => s.Contains("operation-result")) && source.Any(s => s.Contains("\"Kind\":\"verification\"")), "Not all runtime journal boundaries executed.");
                Check(!source.Any(s => s.Contains("TEST-CREDENTIAL-NEVER-PERSIST")), "Credential leaked to journal.");
                Save("production-" + project, new { task, State = reopened.GetArchiveStatus(), wire.StartCount });
            }));
        test("AR-031 corrupt archive prevents provider allocation instead of appearing healthy", () => Fixture(root =>
        {
            var state = Path.Combine(root, "state"); var integration = Path.Combine(state, "integration");
            using (var archive = new AgentIntegrationTaskArchive(integration)) archive.Upsert(Summary());
            File.WriteAllText(Events(integration)[0], "{corrupt"); var calls = 0;
            using var adapter = new H2ProductionAgentAdapter(state, () => { calls++; throw new Exception("must not resolve model"); });
            Check(!adapter.GetArchiveStatus().CanWrite, "Corruption hidden from product diagnostics.");
            Reject<IOException>(() => adapter.StartTaskAsync(null, "Hello", new(root, "fixture")).GetAwaiter().GetResult());
            Check(calls == 0, "A model/provider was accessed before storage recovery.");
        }));
    }

    internal static int Probe(string root, string mode)
    {
        root = Path.GetFullPath(root);
        if (!root.StartsWith(Path.Combine(Path.GetTempPath(), "h2-ar031-"), StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(root, "dedicated-fixture.marker"))) return 91;
        if (mode == "lock") { try { using var a = new AgentIntegrationTaskArchive(root); return 90; } catch (IOException) { return 89; } }
        if (mode == "migration") { using var a = new AgentIntegrationTaskArchive(root, fault: step => { if (step == "migration-before-activate") Environment.Exit(86); }); return 90; }
        var armed = false; var notifications = 0;
        using var archive = new AgentIntegrationTaskArchive(root, fault: step =>
        {
            if (!armed) return;
            if (step == "journal-activated")
            {
                notifications++;
                if (mode == "after-intent" && notifications == 1 || mode == "after-result" && notifications == 3) Environment.Exit(86);
            }
            if (mode == "checkpoint-temp" && step == "checkpoint-before-activate") Environment.Exit(86);
        });
        var t = Summary(H2AgentTaskStatus.Running); archive.Upsert(t); File.WriteAllText(Path.Combine(root, "fixture-task-id"), t.TaskId.ToString()); armed = true;
        if (mode == "before-intent") Environment.Exit(86);
        var operation = new H2AgentOperationRecord(Guid.NewGuid(), "op-fixture", Guid.NewGuid(), "revision-fixture", "call-fixture", "fixture.write", "Dispatched", "NotKnown", "Unknown", new string('a', 64), new string('b', 64), null, null);
        archive.RecordOperation(t.TaskId, operation);
        if (mode == "after-dispatch") Environment.Exit(86);
        File.AppendAllText(Path.Combine(root, "effect.txt"), "ONE");
        if (mode == "after-effect") Environment.Exit(86);
        archive.RecordOperation(t.TaskId, operation with { State = "Result", Status = "Succeeded", Effect = "Applied", OutputSha256 = new string('c', 64) });
        if (mode == "checkpoint-temp") archive.Dispose();
        return 90;
    }
    private static int Child(string root, string mode)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--ar031-crash-probe"); start.ArgumentList.Add(root); start.ArgumentList.Add(mode);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); throw new TimeoutException("Owned archive test child timed out."); }
        Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        if (process.ExitCode is not (86 or 89)) throw new InvalidOperationException("Probe failed: " + stderr.Result + stdout.Result);
        return process.ExitCode;
    }
    private static Dictionary<string, byte[]> Legacy(string root, H2AgentTaskSummary t)
    {
        var dir = Path.Combine(root, "task-records"); Directory.CreateDirectory(dir); Directory.CreateDirectory(Path.Combine(root, "threads"));
        var entries = new Dictionary<string, byte[]>
        {
            [Path.Combine(root, "recent-tasks-v1.json")] = JsonSerializer.SerializeToUtf8Bytes(new { Schema = 1, Tasks = new[] { t } }),
            [Path.Combine(dir, t.TaskId.ToString("N") + ".json")] = JsonSerializer.SerializeToUtf8Bytes(t with { FinalText = "legacy-output" }),
            [Path.Combine(root, "threads", t.ThreadId!.Value.ToString("N") + ".json")] = JsonSerializer.SerializeToUtf8Bytes(new H2AgentThread(t.ThreadId.Value, null, "history", t.CreatedUtc, t.UpdatedUtc, "UNSENT", [t.TaskId])),
            [Path.Combine(dir, t.TaskId.ToString("N") + ".jsonl")] = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new H2AgentProgress(0, DateTime.UtcNow, "tool", "observed", "legacy-progress")) + "\n")
        };
        foreach (var e in entries) File.WriteAllBytes(e.Key, e.Value); return entries;
    }
    private static H2AgentTaskSummary Summary(H2AgentTaskStatus status = H2AgentTaskStatus.Completed)
        => new(Guid.NewGuid(), null, "Archive fixture", status, null, [new("source-proof", "fixture", new string('d', 64), "reference")], "output", null, DateTime.UtcNow, DateTime.UtcNow, Guid.NewGuid(), Guid.NewGuid());
    private static H2AgentGoalSnapshot Goals() => new("revision-1", [new("revision-1", null, 1, "user:1", "Keep =A1+$B$2", ["outcome-1"], [])], [new("outcome-1", "Keep =A1+$B$2", "user:1", "revision-1", "fixture", "Verified", null, ["source-proof"])], []);
    private static string[] Events(string root) => Directory.GetFiles(Path.Combine(root, "journal-v2"), "event-*.json").Order(StringComparer.Ordinal).ToArray();
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static void Reject<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Fixture(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "h2-ar031-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "dedicated-fixture.marker"), "synthetic-data-only");
        try { action(root); } finally { Directory.Delete(root, true); }
    }
    private static void Save(string name, object value)
    {
        var root = Environment.GetEnvironmentVariable("H2_AR031_EVIDENCE_DIR"); if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, name + ".json"), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static H2ProductionAgentAdapter Adapter(string root, Wire wire) => new(root,
        () => new(new AiProfile { Model = "scripted-no-network", BaseUrl = "https://example.test/v1", Protocol = AiProtocol.OpenAiChat }, "TEST-CREDENTIAL-NEVER-PERSIST"), transportFactory: wire);
    private static H2AgentTaskSummary Wait(H2ProductionAgentAdapter a, Guid task)
    {
        var until = Environment.TickCount64 + 20_000;
        while (Environment.TickCount64 < until) { var s = a.GetTaskSummary(task); if (H2AgentActivity.IsTerminal(s.Status)) return s; Thread.Sleep(10); }
        throw new TimeoutException("Concrete AR-031 runtime timed out.");
    }
    private sealed class Wire : IAgentTransport, IAgentTransportFactory
    {
        public int StartCount; private int _step;
        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.ChatCompletionsFallback;
        public IAgentTransport Create(AiProfile p, string key, AgentRunTelemetry telemetry) => this;
        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(AgentTransportStartRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        { StartCount++; foreach (var e in Next()) yield return e; await Task.CompletedTask; }
        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(AgentTransportContinuationRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); foreach (var e in Next()) yield return e; await Task.CompletedTask; }
        private IEnumerable<AgentTransportEvent> Next()
        {
            if (_step++ == 0) yield return AgentTransportEvent.Tool(new("search", "tool_search", "{\"query\":\"write_text\"}"));
            else if (_step == 2) yield return AgentTransportEvent.Tool(new("write", "write_text", "{\"path\":\"result.txt\",\"text\":\"LITERAL =A1+$B$2\",\"expectedHash\":\"\"}"));
            else yield return AgentTransportEvent.TextDeltaEvent("File result observed.");
            yield return AgentTransportEvent.Complete();
        }
        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

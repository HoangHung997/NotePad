using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using H2AgentLab;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Runtime;
using H2AgentLab.Session;
using H2AgentLab.Tasking;
using H2AgentLab.Transport;
using H2Notes.Core;
using H2Notes.Avalonia;

/// <summary>AR-010 / RC-01: concrete production adapter/runtime and Lab facade, real file
/// executors, scripted model only. Native Office, real models and H2 UI are NOT certified.</summary>
internal static class H2AgentRuntimeHookTests
{
    private const string Goal = "Read marker.txt and report the observed marker.";
    private static readonly AgentTransportToolCall[] ReadCalls = [
        new("discover", "tool_search", "{\"query\":\"read_file\"}"),
        new("read", "read_file", "{\"path\":\"marker.txt\",\"offset\":\"0\"}")];

    public static void Run(Action<string, Action> test)
    {
        test("AR-010 RC-01 Global Project and Lab execute the same four concrete hooks", () => InWorkspace(root =>
        {
            var traces = new ConcurrentQueue<AgentRuntimeHookEvent>();
            var transport = new ScriptFactory(ReadCalls, traces);
            var instances = new List<IAgentRuntimeHooks>();
            var factory = new AgentRuntimeFactory(transport, telemetry =>
            {
                var hooks = new AgentRuntimeHooks(telemetry, traces.Enqueue);
                instances.Add(hooks);
                return hooks;
            });
            var projectId = Guid.NewGuid();
            var tasks = new List<Guid>();
            using var adapter = Adapter(root, factory);
            foreach (var entry in new[] { AgentRuntimeEntryPoint.Global, AgentRuntimeEntryPoint.Project })
            {
                var workspace = Path.Combine(root, entry.ToString()); Directory.CreateDirectory(workspace);
                File.WriteAllText(Path.Combine(workspace, "marker.txt"), "MARKER-" + entry);
                var id = adapter.StartTaskAsync(entry == AgentRuntimeEntryPoint.Project ? projectId : null,
                    Goal, new(workspace, "Synthetic marker fixture"), true).Result;
                tasks.Add(id);
                var done = Wait(adapter, id);
                Check(done.Status == H2AgentTaskStatus.Completed, done.Error ?? done.Status.ToString());
                Check(done.FinalText?.Contains("MARKER-" + entry) == true, "Executor read the wrong workspace.");
                Check(done.ProjectId == (entry == AgentRuntimeEntryPoint.Project ? projectId : null), "Project identity drifted.");
                Check(done.Evidence.Count > 0, "Production file observation lost evidence.");
                AssertTrace(traces.Where(e => e.Scope.TaskId == id).ToArray(), entry);
            }
            Drain(adapter);
            var labRoot = Path.Combine(root, "Lab"); Directory.CreateDirectory(labRoot);
            File.WriteAllText(Path.Combine(labRoot, "marker.txt"), "MARKER-Lab");
            var labSession = new LabSession { Workspace = labRoot };
            var state = Path.Combine(root, "lab-state");
            using var tools = new AgentTools(new SafeWorkspace(labRoot), state,
                (_, _) => Task.FromResult(false), (_, _) => { }) { ReadOnly = true };
            var telemetry = new AgentRunTelemetry();
            var inspection = new AgentOrchestratedRun(new AgentOrchestrator(runtimeFactory: factory)).RunAsync(
                Profile(), "", labSession, tools, Goal, (_, _) => { }, () => labSession.Save(state), telemetry,
                true, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            Check(inspection.State == AgentTaskState.Completed, "Lab facade did not complete.");
            tasks.Add(inspection.TaskId);
            AssertTrace(traces.Where(e => e.Scope.TaskId == inspection.TaskId).ToArray(), AgentRuntimeEntryPoint.Lab);
            Check(instances.Count == 3 && instances.All(h => h.GetType() == typeof(AgentRuntimeHooks)), "Composition did not use the shared concrete hook implementation.");
            Check(instances.Distinct().Count() == 3, "Task-local hook instances were shared.");
            Check(tasks.Distinct().Count() == 3 && traces.Select(e => e.Scope.TurnId).Distinct().Count() == 3, "Task or turn trace identities were reused.");
            Check(transport.Sessions.Count == 3 && transport.Sessions.All(s => s.Sends == 3), "Unexpected transport call count.");
            Check(transport.Sessions.All(s => s.Results.Any(r => r.ToolName == "read_file" && !r.IsError)), "Fixture skipped concrete file executor.");
            Check(transport.Sessions.All(s => s.NewTools.Contains("read_file")), "Deferred file schema was never actually registered/exposed.");
            Check(telemetry.Trace.Snapshot().Count(e => e.Kind == AgentTraceKind.RuntimeHook) == 10, "Shared hooks did not reach the existing telemetry channel.");
            var safeTrace = JsonSerializer.Serialize(traces.ToArray());
            Check(!safeTrace.Contains("MARKER-") && !safeTrace.Contains(Goal) && !safeTrace.Contains(root), "Hook telemetry leaked prompt/document/path content.");
            SaveTrace("rc01", traces);
        }));

        test("AR-010 Lab real compaction checkpoint is distinct from production in-memory boundaries", () => InWorkspace(root =>
        {
            var traces = new ConcurrentQueue<AgentRuntimeHookEvent>();
            var transport = new ScriptFactory([], traces);
            var factory = new AgentRuntimeFactory(transport, telemetry => new AgentRuntimeHooks(telemetry, traces.Enqueue));
            var session = new LabSession { Workspace = root };
            for (var i = 0; i < 150; i++) session.Add(i % 2 == 0 ? "user" : "assistant", "OLD-" + i + new string('x', 600));
            var state = Path.Combine(root, "lab-state"); session.Save(state);
            var old = session.Events.ToArray();
            using var tools = new AgentTools(new SafeWorkspace(root), state, (_, _) => Task.FromResult(false), (_, _) => { }) { ReadOnly = true };
            _ = new AgentOrchestratedRun(new AgentOrchestrator(runtimeFactory: factory)).RunAsync(
                Profile(), "", session, tools, Goal, (_, _) => { }, () => session.Save(state), new(), true,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            var prepared = traces.Single(t => t.CheckpointKind == AgentRuntimeCheckpointKind.ContextPrepared);
            Check(!string.IsNullOrWhiteSpace(prepared.DurableCheckpointId), "Actual Lab checkpoint was not surfaced.");
            var checkpoint = new CompactionManager(state).Load(prepared.DurableCheckpointId!);
            Check(checkpoint.Sources.Count > 0 && checkpoint.CoveredThroughSequence >= 0, "Checkpoint hook referenced no durable sources.");
            Check(session.Events.Take(old.Length).SequenceEqual(old), "Hook integration rewrote raw session history.");
            Check(traces.Where(t => t.CheckpointKind != AgentRuntimeCheckpointKind.ContextPrepared).All(t => t.DurableCheckpointId is null), "An in-memory boundary fabricated durable resume evidence.");
            SaveTrace("lab-checkpoint", traces);
        }));

        test("AR-010 supplemental no-tool continuation crosses the same pre-request hook", () => InWorkspace(root =>
        {
            var traces = new ConcurrentQueue<AgentRuntimeHookEvent>();
            var transport = new ScriptFactory([], traces, holdFirst: true);
            using var adapter = Adapter(root, new AgentRuntimeFactory(transport, t => new AgentRuntimeHooks(t, traces.Enqueue)));
            try
            {
                var id = adapter.StartTaskAsync(null, "Synthetic streaming request", new(root, ""), true).Result;
                var wire = transport.Sessions.Single();
                Await(wire.Entered.Task);
                Check(adapter.SupplementTask(id, Guid.NewGuid(), "Apply this user correction."), "Supplement was not accepted.");
                Check(adapter.GetTaskSummary(id).Status is not H2AgentTaskStatus.Completed, "Paused model was reported completed.");
                wire.Release.TrySetResult();
                var done = Wait(adapter, id);
                Check(done.Status == H2AgentTaskStatus.Completed, done.Error ?? "Supplement failed.");
                Check(wire.Continuations.Single().SupplementalUserMessages!.Contains("Apply this user correction."), "User role/content did not reach continuation.");
                Check(traces.Count(e => e.Kind == AgentRuntimeHookKind.BeforeModelRequest) == 2 && wire.Sends == 2, "Supplement bypassed or duplicated a request hook.");
                Check(traces.Count(e => e.Kind == AgentRuntimeHookKind.BeforeCompletion) == 1, "Supplement produced duplicate final candidates.");
            }
            finally { transport.Sessions.Single().Release.TrySetResult(); Drain(adapter); }
        }));

        test("AR-010 recovery-state continuation is hooked and cannot erase an unresolved failure", () => InWorkspace(root =>
        {
            var traces = new ConcurrentQueue<AgentRuntimeHookEvent>();
            var calls = new[] { new AgentTransportToolCall("search", "tool_search", "{\"query\":\"exec_command\"}"),
                new AgentTransportToolCall("fail", "exec_command", "{\"command\":\"exit 7\",\"timeout_seconds\":5}") };
            var transport = new ScriptFactory(calls, traces);
            using var adapter = Adapter(root, new AgentRuntimeFactory(transport, t => new AgentRuntimeHooks(t, traces.Enqueue)));
            try
            {
                var grant = WorkAssistantPermissionScopeMapper.ForWorkspace(H2AgentPermissionMode.FullAccess, root, DateTime.UtcNow).PermissionScope;
                var id = adapter.StartTaskAsync(null, "Run the synthetic failing command", new(root, "", PermissionScope: grant), false).Result;
                var done = Wait(adapter, id);
                Check(done.Status is H2AgentTaskStatus.Blocked or H2AgentTaskStatus.Failed, "Hook accepted an unresolved failure.");
                var wire = transport.Sessions.Single();
                Check(wire.Continuations.Any(c => c.ToolResults.Count == 0
                    && c.SupplementalUserMessages?.Any(s => s.Contains("[HOST RECOVERY STATE]", StringComparison.Ordinal)
                        && s.Contains("\"UnresolvedAttempts\":1", StringComparison.Ordinal)) == true),
                    "Structured recovery-state continuation was not exercised.");
                Check(traces.Count(e => e.Kind == AgentRuntimeHookKind.BeforeModelRequest) == wire.Sends,
                    "Recovery-state continuation bypassed pre-request hook.");
                Check(!traces.Any(e => e.CheckpointKind == AgentRuntimeCheckpointKind.CompletionValidated), "Failed completion was recorded as validated.");
            }
            finally { Drain(adapter); }
        }));

        foreach (var returnNull in new[] { false, true })
        {
            var nullHook = returnNull;
            test("AR-010 invalid hook factory allocates no transport: " + (nullHook ? "null" : "exception"), () => InWorkspace(root =>
            {
                var traces = new ConcurrentQueue<AgentRuntimeHookEvent>();
                var transport = new ScriptFactory([], traces);
                var factory = new AgentRuntimeFactory(transport, _ => nullHook ? null! : throw new InvalidOperationException("hook-factory-failure"));
                using var tools = new AgentTools(new SafeWorkspace(root), Path.Combine(root, "lab-state"),
                    (_, _) => Task.FromResult(false), (_, _) => { }) { ReadOnly = true };
                try
                {
                    _ = factory.Create(Profile(), "", tools, new H2AgentLab.Context.AgentContextManager(), new());
                    throw new Exception("Invalid hook factory was accepted.");
                }
                catch (InvalidOperationException error)
                {
                    Check(error.Message.Contains(nullHook ? "returned null" : "hook-factory-failure"), "The original hook factory error was hidden.");
                }
                Check(transport.Sessions.Count == 0, "Hook factory failure leaked a newly allocated transport.");
            }));
        }

        foreach (var kind in Enum.GetValues<AgentRuntimeHookKind>())
        {
            var target = kind;
            test("AR-010 cancellation at awaited " + target + " stops further model requests", () => InWorkspace(root =>
            {
                var traces = new ConcurrentQueue<AgentRuntimeHookEvent>();
                var transport = new ScriptFactory(ReadCalls, traces);
                var hook = new HoldingHooks(target, traces);
                using var adapter = Adapter(root, new AgentRuntimeFactory(transport, _ => hook));
                File.WriteAllText(Path.Combine(root, "marker.txt"), "CANCEL-MARKER");
                try
                {
                    var id = adapter.StartTaskAsync(null, Goal, new(root, ""), true).Result;
                    Await(hook.Entered.Task);
                    var count = transport.Sessions.Single().Sends;
                    adapter.CancelTask(id);
                    var done = Wait(adapter, id);
                    Check(done.Status == H2AgentTaskStatus.Cancelled, done.Error ?? "Cancellation did not reach the hook.");
                    Drain(adapter);
                    Check(transport.Sessions.Single().Sends == count, "Transport was invoked after hook cancellation.");
                    Check(transport.Sessions.Single().Cancelled, "Transport cancellation was not propagated.");
                }
                finally { hook.Release.TrySetResult(); Drain(adapter); }
            }));
        }

        test("AR-010 hook failure before provider send fails closed without a model call", () => InWorkspace(root =>
        {
            var traces = new ConcurrentQueue<AgentRuntimeHookEvent>();
            var transport = new ScriptFactory([], traces);
            using var adapter = Adapter(root, new AgentRuntimeFactory(transport, _ => new RejectingHooks()));
            try
            {
                var id = adapter.StartTaskAsync(null, Goal, new(root, ""), true).Result;
                var done = Wait(adapter, id);
                Check(done.Status == H2AgentTaskStatus.Failed && done.Error?.Contains("synthetic-hook-rejection") == true, "Hook exception was hidden or converted into success.");
                Check(transport.Sessions.Single().Sends == 0, "Rejected pre-request hook still contacted the provider.");
            }
            finally { Drain(adapter); }
        }));
    }

    private static void AssertTrace(AgentRuntimeHookEvent[] trace, AgentRuntimeEntryPoint entry)
    {
        var expected = new[] { AgentRuntimeHookKind.Checkpoint, AgentRuntimeHookKind.BeforeModelRequest,
            AgentRuntimeHookKind.AfterToolObservation, AgentRuntimeHookKind.Checkpoint, AgentRuntimeHookKind.BeforeModelRequest,
            AgentRuntimeHookKind.AfterToolObservation, AgentRuntimeHookKind.Checkpoint, AgentRuntimeHookKind.BeforeModelRequest,
            AgentRuntimeHookKind.BeforeCompletion, AgentRuntimeHookKind.Checkpoint };
        Check(trace.Select(t => t.Kind).SequenceEqual(expected), "Runtime hook order differs: " + string.Join(",", trace.Select(t => t.Kind)));
        Check(trace.Select(t => t.Sequence).SequenceEqual(Enumerable.Range(1, 10).Select(i => (long)i)), "Hook sequence is not task-local and ordered.");
        Check(trace.All(t => t.Scope.Invocation.EntryPoint == entry), "Entry point identity drifted.");
        Check(trace.All(t => t.DurableCheckpointId is null), "Simple task invented a persisted checkpoint.");
        Check(trace.Where(t => t.Kind == AgentRuntimeHookKind.BeforeModelRequest).Select(t => t.RequestIndex).SequenceEqual(new[] { 1, 2, 3 }), "Request hook count drifted.");
        Check(trace.Where(t => t.Kind == AgentRuntimeHookKind.BeforeModelRequest).All(t => t.RegisteredToolCount >= 20 && t.ContextCharacters > 0), "Hook did not observe the concrete registry/bounded context.");
        Check(trace.Where(t => t.Kind == AgentRuntimeHookKind.AfterToolObservation).All(t => t.ObservationCount == 1), "Observation counts differ from executed tools.");
    }

    private static void SaveTrace(string name, ConcurrentQueue<AgentRuntimeHookEvent> trace)
    {
        var output = Environment.GetEnvironmentVariable("H2_AR010_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, name + ".json"), JsonSerializer.Serialize(trace.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
    }
    private static AiProfile Profile() => new() { Model = "scripted-no-network", Protocol = AiProtocol.OpenAiChat, BaseUrl = "https://example.test/v1" };
    private static H2ProductionAgentAdapter Adapter(string root, AgentRuntimeFactory factory) => new(Path.Combine(root, "agent-state"), () => new(Profile(), ""), runtimeFactory: factory);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
    private static void Drain(H2ProductionAgentAdapter adapter) => Await(adapter.DisposeAsync().AsTask());
    private static H2AgentTaskSummary Wait(IH2AgentAdapter adapter, Guid id)
    {
        var end = Environment.TickCount64 + 20_000;
        while (Environment.TickCount64 < end)
        {
            var summary = adapter.GetTaskSummary(id);
            if (summary.Status is H2AgentTaskStatus.Completed or H2AgentTaskStatus.Cancelled or H2AgentTaskStatus.Blocked or H2AgentTaskStatus.Failed) return summary;
            Thread.Sleep(10);
        }
        throw new TimeoutException("AR-010 concrete runtime fixture timed out.");
    }
    private static void InWorkspace(Action<string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "h2-ar010-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Exception? primary = null;
        try { body(root); } catch (Exception ex) { primary = ex; throw; }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (Exception cleanup) when (primary is not null) { primary.Data["cleanup"] = cleanup.GetType().Name; }
        }
    }

    private sealed class ScriptFactory(AgentTransportToolCall[] calls, ConcurrentQueue<AgentRuntimeHookEvent> traces, bool holdFirst = false) : IAgentTransportFactory
    {
        public List<Wire> Sessions { get; } = [];
        public IAgentTransport Create(AiProfile profile, string key, AgentRunTelemetry telemetry)
        { var wire = new Wire(calls, traces, holdFirst); Sessions.Add(wire); return wire; }
    }
    private sealed class Wire(AgentTransportToolCall[] calls, ConcurrentQueue<AgentRuntimeHookEvent> traces, bool holdFirst) : IAgentTransport
    {
        private int _next;
        private Guid _taskId;
        public int Sends { get; private set; }
        public bool Cancelled { get; private set; }
        public List<AgentToolResult> Results { get; } = [];
        public List<string> NewTools { get; } = [];
        public List<AgentTransportContinuationRequest> Continuations { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.ChatCompletionsFallback;
        public IAsyncEnumerable<AgentTransportEvent> StartAsync(AgentTransportStartRequest request, CancellationToken cancellationToken = default)
        { _taskId = request.TaskId; BeforeSend(); return Round(cancellationToken); }
        public IAsyncEnumerable<AgentTransportEvent> ContinueAsync(AgentTransportContinuationRequest request, CancellationToken cancellationToken = default)
        {
            Check(request.TaskId == _taskId, "Transport continuation crossed task."); BeforeSend();
            Continuations.Add(request); Results.AddRange(request.ToolResults); NewTools.AddRange(request.NewlyLoadedTools?.Select(t => t.Name) ?? []);
            return Round(cancellationToken);
        }
        private void BeforeSend()
        {
            Sends++;
            Check(traces.Any(e => e.Scope.TaskId == _taskId && e.Kind == AgentRuntimeHookKind.BeforeModelRequest && e.RequestIndex == Sends), "Transport invoked before its awaited runtime hook.");
        }
        private async IAsyncEnumerable<AgentTransportEvent> Round([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (holdFirst && Sends == 1)
            {
                yield return AgentTransportEvent.TextDeltaEvent("Synthetic streamed prefix");
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            if (_next < calls.Length) yield return AgentTransportEvent.Tool(calls[_next++]);
            else yield return AgentTransportEvent.TextDeltaEvent("Fixture readback: " + (Results.LastOrDefault(r => r.ToolName == "read_file")?.Content ?? "No mutation claimed."));
            yield return AgentTransportEvent.Complete();
        }
        public void Cancel() => Cancelled = true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class HoldingHooks(AgentRuntimeHookKind target, ConcurrentQueue<AgentRuntimeHookEvent> traces) : AgentRuntimeHooks(observer: traces.Enqueue)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async ValueTask Hold(AgentRuntimeHookKind kind, CancellationToken ct)
        { if (kind == target) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct).ConfigureAwait(false); } }
        public override async ValueTask BeforeModelRequestAsync(AgentRuntimeModelRequestBoundary b, CancellationToken ct)
        { await base.BeforeModelRequestAsync(b, ct); await Hold(AgentRuntimeHookKind.BeforeModelRequest, ct).ConfigureAwait(false); }
        public override async ValueTask AfterToolObservationAsync(AgentRuntimeObservationBoundary b, CancellationToken ct)
        { await base.AfterToolObservationAsync(b, ct); await Hold(AgentRuntimeHookKind.AfterToolObservation, ct).ConfigureAwait(false); }
        public override async ValueTask BeforeCompletionAsync(AgentRuntimeCompletionBoundary b, CancellationToken ct)
        { await base.BeforeCompletionAsync(b, ct); await Hold(AgentRuntimeHookKind.BeforeCompletion, ct).ConfigureAwait(false); }
        public override async ValueTask OnCheckpointAsync(AgentRuntimeCheckpointBoundary b, CancellationToken ct)
        { await base.OnCheckpointAsync(b, ct); await Hold(AgentRuntimeHookKind.Checkpoint, ct).ConfigureAwait(false); }
    }
    private sealed class RejectingHooks : AgentRuntimeHooks
    {
        public override ValueTask BeforeModelRequestAsync(AgentRuntimeModelRequestBoundary boundary, CancellationToken cancellationToken)
            => throw new InvalidOperationException("synthetic-hook-rejection");
    }
}

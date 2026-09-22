using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using H2AgentLab;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Runtime;
using H2AgentLab.Transport;
using H2Notes.Core;
using H2Notes.Avalonia;

/// <summary>AR-012 draft corpus. Deterministic binding tests and concrete production file-tool
/// tests with a scripted transport. These are NOT evidence until actually executed.</summary>
internal static class H2AgentResourceBindingTests
{
    private static readonly DateTime Now = new(2026, 9, 22, 9, 0, 0, DateTimeKind.Utc);

    public static void Run(Action<string, Action> test)
    {
        test("AR-012 RC-04 quoted Unicode external file does not grant sibling or parent", () => InWorkspace(root =>
        {
            var path = Path.Combine(root, "Dữ liệu đúng.xlsx"); File.WriteAllText(path, "fixture");
            var targets = H2AgentTargetScope.FromUserRequest("Read exactly \"" + path + "\"");
            Check(targets.Count == 1 && targets[0].Source == "user-path" && !targets[0].IncludeChildren, "Exact source lost.");
            Check(H2AgentTargetScope.Contains(targets, path), "Named file denied.");
            Check(!H2AgentTargetScope.Contains(targets, root)
                && !H2AgentTargetScope.Contains(targets, Path.Combine(root, "sibling.xlsx")), "File expanded to parent/sibling.");
        }));
        test("AR-012 RC-04 project prefix collision is not containment", () => InWorkspace(root =>
        {
            var a = Directory.CreateDirectory(Path.Combine(root, "Project")).FullName;
            var b = Directory.CreateDirectory(Path.Combine(root, "ProjectOther")).FullName;
            var policy = new H2AgentTargetBindingPolicy(Guid.NewGuid(), a);
            Check(policy.AllowsPath(Path.Combine(a, "result.txt")), "Project child rejected.");
            Check(!policy.AllowsPath(Path.Combine(b, "result.txt")), "Prefix collision escaped project.");
        }));
        test("AR-012 ordinary path syntax rejects aliases devices streams and ambiguous trailing names", () => InWorkspace(root =>
        {
            foreach (var bad in new[] { "report.txt.", "report.txt ", "NUL.txt", "COM¹.txt", "LPT².xlsx", "a.txt:stream", "a*", "a?", "a\0b" })
                Check(!H2AgentTargetScope.TryNormalize(Path.Combine(root, bad), out _), "Unsafe syntax accepted: " + bad);
            Check(!H2AgentTargetScope.TryNormalize(@"\\.\C:\result.txt", out _), "Device path accepted.");
            Check(!H2AgentTargetScope.TryNormalize(@"\\?\C:\result.txt", out _), "Extended alias accepted without proof.");
            Check(H2AgentTargetScope.TryNormalize("sub/../result.txt", out var actual, root)
                && H2AgentTargetScope.PathComparer.Equals(actual, Path.Combine(root, "result.txt")), "Safe lexical normalization failed.");
        }));
        test("AR-012 target list is an immutable request snapshot", () => InWorkspace(root =>
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
            var targets = new List<H2AgentTargetPath> { new(Path.Combine(root, "one.txt"), false, "project-link") };
            var policy = new H2AgentTargetBindingPolicy(Guid.NewGuid(), workspace, targets);
            targets.Add(new(root, true, "project-link"));
            Check(policy.AllowsPath(Path.Combine(root, "one.txt")) && !policy.AllowsPath(Path.Combine(root, "two.txt")), "Caller changed a running task's target set.");
        }));
        test("AR-012 RC-03 near-name workbooks remain ambiguous without exact host target", () => InWorkspace(root =>
        {
            var a = Live("a", Path.Combine(root, "BaoCao.xlsx"));
            var b = Live("b", Path.Combine(root, "BaoCao (1).xlsx"));
            var policy = new H2AgentTargetBindingPolicy(Guid.NewGuid(), root);
            Check(policy.ResolveOpen(H2ApplicationKind.Excel, [a, b], H2AgentTargetIntent.OpenDocument, Now).Code == "ambiguous_target", "First/fuzzy candidate selected.");
            Check(policy.ResolveOpen(H2ApplicationKind.Excel, [a, b], H2AgentTargetIntent.OpenDocument, Now, "b").Code == "ambiguous_target", "Model session resolved ambiguity without a host choice.");
        }));
        test("AR-012 RC-04 project open request ignores outside active but active request never substitutes", () => InWorkspace(root =>
        {
            var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            var inside = Live("inside", Path.Combine(project, "inside.xlsx"));
            var outside = Live("outside", Path.Combine(root, "outside.xlsx"));
            var policy = new H2AgentTargetBindingPolicy(Guid.NewGuid(), project, captured: Capture(outside));
            Check(policy.ResolveOpen(H2ApplicationKind.Excel, [outside, inside], H2AgentTargetIntent.OpenDocument, Now).Binding == inside, "Open project request used outside active.");
            var active = policy.ResolveOpen(H2ApplicationKind.Excel, [outside, inside], H2AgentTargetIntent.CapturedActive, Now);
            Check(!active.Resolved && active.Code == "outside_resource_scope", "Explicit active request silently substituted a project file.");
        }));
        test("AR-012 RC-04 external link has a distinct narrow target label", () => InWorkspace(root =>
        {
            var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            var external = Live("external", Path.Combine(root, "external.xlsx"));
            var policy = new H2AgentTargetBindingPolicy(Guid.NewGuid(), project,
                [new(external.CanonicalPath!, false, "project-link")]);
            var resolved = policy.ResolveOpen(H2ApplicationKind.Excel, [external], H2AgentTargetIntent.OpenDocument, Now);
            Check(resolved.Resolved && resolved.IsExternal && resolved.ScopeLabel.StartsWith("External: "), "External projection not distinguishable.");
            Check(!policy.AllowsPath(Path.Combine(root, "sibling.xlsx")), "Linked file broadened to sibling.");
        }));
        test("AR-012 Global captured active is stable when provider enumeration order changes", () => InWorkspace(root =>
        {
            var a = Live("a", Path.Combine(root, "A.xlsx")); var b = Live("b", Path.Combine(root, "B.xlsx"));
            var policy = new H2AgentTargetBindingPolicy(null, root, captured: Capture(a));
            foreach (var candidates in new[] { new[] { a, b }, new[] { b, a } })
                Check(policy.ResolveOpen(H2ApplicationKind.Excel, candidates, H2AgentTargetIntent.CapturedActive, Now).Binding == a, "Foreground/enumeration change redirected task.");
        }));
        test("AR-012 stale captured active does not fall back to a different open file", () => InWorkspace(root =>
        {
            var a = Live("a", Path.Combine(root, "A.xlsx")); var b = Live("b", Path.Combine(root, "B.xlsx"));
            var policy = new H2AgentTargetBindingPolicy(null, root, captured: Capture(a) with { CapturedUtc = Now.AddMinutes(-11) });
            Check(policy.ResolveOpen(H2ApplicationKind.Excel, [b], H2AgentTargetIntent.OpenDocument, Now).Code == "stale_resource", "Stale capture silently retargeted.");
        }));
        test("AR-012 RC-05 UNSAVED-ONLY live identity cannot be replaced by a disk snapshot", () => InWorkspace(root =>
        {
            var unsaved = Live("UNSAVED-ONLY", null);
            var policy = new H2AgentTargetBindingPolicy(null, root, captured: Capture(unsaved));
            var resolved = policy.ResolveOpen(H2ApplicationKind.Excel, [unsaved], H2AgentTargetIntent.CapturedActive, Now);
            Check(resolved.Binding == unsaved && resolved.Binding.Provenance == "LiveDocument", "Unsaved live target lost.");
            var disk = H2AgentResourceBinding.FromDisk(Path.Combine(root, "Book1.xlsx"), "disk-v1", Now);
            Check(!policy.ResolveOpen(H2ApplicationKind.Excel, [disk], H2AgentTargetIntent.CapturedActive, Now).Resolved, "Disk file substituted for live request.");
        }));
        test("AR-012 RC-03 two views sharing a session require exact captured window", () => InWorkspace(root =>
        {
            var a = Live("same-session", Path.Combine(root, "same.xlsx"), window: "win-a");
            var b = Live("same-session", Path.Combine(root, "same.xlsx"), window: "win-b");
            var policy = new H2AgentTargetBindingPolicy(null, root, captured: Capture(b));
            Check(policy.ResolveOpen(H2ApplicationKind.Excel, [a, b], H2AgentTargetIntent.CapturedActive, Now).Binding == b, "Wrong workbook view selected.");
            Check(policy.ResolveOpen(H2ApplicationKind.Excel, [a, b], H2AgentTargetIntent.OpenDocument, Now, "same-session").Binding == b, "Host-captured view was lost.");
            var noCapture = new H2AgentTargetBindingPolicy(null, root);
            Check(noCapture.ResolveOpen(H2ApplicationKind.Excel, [a, b], H2AgentTargetIntent.OpenDocument, Now, "same-session").Code == "ambiguous_target", "Session alone resolved two views.");
        }));
        test("AR-012 RC-07 UI-only state does not change content identity or redirect selection", () => InWorkspace(root =>
        {
            var a = Live("a", Path.Combine(root, "A.xlsx"));
            var clicked = a with { UiStateToken = "Sheet1!Z99" };
            Check(a.MatchesObservation(clicked), "UI movement invalidated unchanged content.");
            Check(!a.MatchesObservation(clicked, requireUiState: true), "Changed selection accepted for original selection operation.");
            var policy = new H2AgentTargetBindingPolicy(null, root, captured: Capture(a));
            Check(policy.ResolveOpen(H2ApplicationKind.Excel, [clicked], H2AgentTargetIntent.CapturedSelection, Now).Code == "stale_resource", "Selection change redirected mutation.");
            Check(!a.MatchesObservation(a with { ContentVersion = "v2" }), "Changed content passed old precondition.");
            var noVersion = a with { ContentVersion = null };
            Check(!noVersion.MatchesObservation(noVersion), "Missing content version was accepted as a proven match.");
            Check(noVersion.MatchesObservation(noVersion, requireContentVersion: false), "Explicit identity-only comparison was lost.");
        }));
        test("AR-012 RC-24 process reuse provider switch and Save As require rebinding", () => InWorkspace(root =>
        {
            var a = Live("a", Path.Combine(root, "A.xlsx"));
            foreach (var changed in new[] { a with { ProcessStartUtcTicks = 456 }, a with { ProviderInstanceId = "other" },
                a with { ProviderVersion = "v2" }, a with { CanonicalPath = Path.Combine(root, "Renamed.xlsx") }, a with { ViewIdentity = "other-view" } })
                Check(!a.MatchesObservation(changed), "Changed native identity reused a binding.");
        }));
        test("AR-012 duplicate provider identities fail closed rather than deduplicating", () => InWorkspace(root =>
        {
            var a = Live("a", Path.Combine(root, "A.xlsx"));
            var policy = new H2AgentTargetBindingPolicy(Guid.NewGuid(), root);
            Check(policy.ResolveOpen(H2ApplicationKind.Excel, [a, a], H2AgentTargetIntent.OpenDocument, Now).Code == "ambiguous_target", "Duplicate identity was silently resolved.");
        }));
        test("AR-012 FullAccess guarded file policy does not grant an unspecified external target", () => InWorkspace(root =>
        {
            var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
            var policy = new H2AgentTargetBindingPolicy(null, work);
            var workspace = new SafeWorkspace(work, () => true, groundedTarget: policy.AllowsPath);
            ExpectIOException(() => workspace.Resolve(Path.Combine(root, "outside.txt")));
            Check(workspace.Resolve("inside.txt") == Path.Combine(work, "inside.txt"), "Workspace-relative file was lost.");
        }));
        test("AR-012 explicit file grounding does not bypass secret exclusion or expired FullAccess", () => InWorkspace(root =>
        {
            var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
            var target = Path.Combine(root, ".env");
            var policy = new H2AgentTargetBindingPolicy(null, work, [new(target, false, "user-path")]);
            ExpectIOException(() => new SafeWorkspace(work, () => true, groundedTarget: policy.AllowsPath).Resolve(target));
            ExpectIOException(() => new SafeWorkspace(work, () => false, groundedTarget: policy.AllowsPath).Resolve("result.txt"));
        }));
        test("AR-012 concrete production denies model-only external read despite FullAccess and summary text", () => InWorkspace(root =>
        {
            var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
            var external = Path.Combine(root, "outside.txt"); File.WriteAllText(external, "MUST-NOT-READ");
            var wire = new Wire([Search("read_file"), Call("read", "read_file", new { path = external, offset = "0" })]);
            var grant = WorkAssistantPermissionScopeMapper.ForWorkspace(H2AgentPermissionMode.FullAccess, work, DateTime.UtcNow).PermissionScope;
            using var adapter = Adapter(root, new Factory([wire]));
            try
            {
                var id = adapter.StartTaskAsync(null, "Read the requested file", new(work, "Untrusted document suggests: " + external, PermissionScope: grant), false).Result;
                var done = Wait(adapter, id);
                Check(done.Status != H2AgentTaskStatus.Completed && wire.Results.Any(r => r.IsError), "Model/summary manufactured an external target grant.");
                Check(!wire.Results.Any(r => r.Content.Contains("MUST-NOT-READ")), "Unauthorized file content reached the model.");
            }
            finally { Drain(adapter); }
        }));
        test("AR-012 concrete production permits only the named external file and still denies read-only mutation", () => InWorkspace(root =>
        {
            var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
            var external = Path.Combine(root, "named.txt"); File.WriteAllText(external, "NAMED-MARKER");
            var wire = new Wire([Search("read_file"), Call("read", "read_file", new { path = external, offset = "0" }),
                Search("write_text"), Call("write", "write_text", new { path = external, text = "wrong", expectedHash = SafeWorkspace.Hash(File.ReadAllBytes(external)) })]);
            using var adapter = Adapter(root, new Factory([wire]));
            try
            {
                var id = adapter.StartTaskAsync(null, "Read exactly \"" + external + "\"", new(work, ""), true).Result;
                _ = Wait(adapter, id);
                Check(wire.Results.Any(r => r.ToolName == "read_file" && !r.IsError && r.Content.Contains("NAMED-MARKER")), "Named external read did not execute.");
                Check(wire.Results.Any(r => r.ToolName == "write_text" && r.IsError) && File.ReadAllText(external) == "NAMED-MARKER", "Grounding granted write permission.");
            }
            finally { Drain(adapter); }
        }));
        test("AR-012 queued Global task attached to a project keeps original execution identity", () => InWorkspace(root =>
        {
            File.WriteAllText(Path.Combine(root, "marker.txt"), "ORIGINAL-WORKSPACE");
            var first = new Wire([], hold: true);
            var second = new Wire([Search("read_file"), Call("read", "read_file", new { path = "marker.txt", offset = "0" })]);
            var traces = new ConcurrentQueue<AgentRuntimeHookEvent>();
            var factory = new AgentRuntimeFactory(new Factory([first, second]), t => new AgentRuntimeHooks(t, traces.Enqueue));
            using var adapter = new H2ProductionAgentAdapter(Path.Combine(root, "state"), () => new(Profile(), ""), runtimeFactory: factory);
            try
            {
                var thread = Guid.NewGuid(); var context = new H2AgentTaskContext(root, "", ThreadId: thread);
                var predecessor = adapter.StartTaskAsync(null, "Hold this synthetic predecessor", context, true).Result;
                first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                var id = adapter.StartTaskAsync(null, "Read marker.txt", context with { AfterTaskId = predecessor }, true).Result;
                var project = Guid.NewGuid(); Check(adapter.AttachProject(id, project), "History attachment failed.");
                first.Release.TrySetResult();
                var done = Wait(adapter, id);
                Check(done.ProjectId == project && done.Status == H2AgentTaskStatus.Completed, "Task did not retain history attachment/read result.");
                Check(traces.Where(e => e.Scope.TaskId == id).Any()
                    && traces.Where(e => e.Scope.TaskId == id).All(e => e.Scope.Invocation.EntryPoint == AgentRuntimeEntryPoint.Global && e.Scope.Invocation.ProjectId is null),
                    "Attaching history silently changed execution scope before queued task started.");
            }
            finally { first.Release.TrySetResult(); Drain(adapter); }
        }));
    }

    private static H2AgentResourceBinding Live(string id, string? path, string window = "window-a")
        => H2AgentResourceBinding.FromLiveObservation(H2ApplicationKind.Excel, "fixture-office", id, path, Now,
            "fixture-process", "v1", 123, 123, window, window, "content-v1", "Sheet1!D51", false);
    private static H2ActiveWorkContext Capture(H2AgentResourceBinding live)
        => new(123, 123, "EXCEL", H2ApplicationKind.Excel, 101, live.WindowIdentity!, "Synthetic workbook",
            live.DocumentSessionId, live.CanonicalPath, live.UiStateToken, live.ProviderId, Now);
    private static AgentTransportToolCall Search(string name) => Call("load-" + name, "tool_search", new { query = name });
    private static AgentTransportToolCall Call(string id, string name, object arguments) => new(id, name, JsonSerializer.Serialize(arguments));
    private static AiProfile Profile() => new() { Model = "fixture-no-network", Protocol = AiProtocol.OpenAiChat, BaseUrl = "https://example.test/v1" };
    private static H2ProductionAgentAdapter Adapter(string root, Factory factory)
        => new(Path.Combine(root, "state"), () => new(Profile(), ""), transportFactory: factory);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void ExpectIOException(Action action)
    { try { action(); } catch (IOException) { return; } throw new InvalidOperationException("Expected a fail-closed filesystem rejection."); }
    private static void Drain(H2ProductionAgentAdapter adapter)
        => adapter.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
    private static H2AgentTaskSummary Wait(IH2AgentAdapter adapter, Guid id)
    {
        var deadline = Environment.TickCount64 + 20_000;
        while (Environment.TickCount64 < deadline)
        {
            var task = adapter.GetTaskSummary(id);
            if (H2AgentActivity.IsTerminal(task.Status)) return task;
            Thread.Sleep(10);
        }
        throw new TimeoutException("AR-012 fixture did not finish within its finite budget.");
    }
    private static void InWorkspace(Action<string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "h2-ar012-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Exception? primary = null;
        try { body(root); } catch (Exception ex) { primary = ex; throw; }
        finally { try { Directory.Delete(root, true); } catch (Exception ex) when (primary is not null) { primary.Data["cleanup"] = ex.GetType().Name; } }
    }
    private sealed class Factory(Wire[] wires) : IAgentTransportFactory
    {
        private int _next;
        public IAgentTransport Create(AiProfile profile, string key, AgentRunTelemetry telemetry) => wires[Interlocked.Increment(ref _next) - 1];
    }
    private sealed class Wire(AgentTransportToolCall[] calls, bool hold = false) : IAgentTransport
    {
        private int _next;
        private bool _started;
        public List<AgentToolResult> Results { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.ChatCompletionsFallback;
        public IAsyncEnumerable<AgentTransportEvent> StartAsync(AgentTransportStartRequest request, CancellationToken ct = default) => Round(ct);
        public IAsyncEnumerable<AgentTransportEvent> ContinueAsync(AgentTransportContinuationRequest request, CancellationToken ct = default)
        { Results.AddRange(request.ToolResults); return Round(ct); }
        private async IAsyncEnumerable<AgentTransportEvent> Round([EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!_started && hold) { _started = true; Entered.TrySetResult(); await Release.Task.WaitAsync(ct).ConfigureAwait(false); }
            if (_next < calls.Length) yield return AgentTransportEvent.Tool(calls[_next++]);
            else yield return AgentTransportEvent.TextDeltaEvent("Synthetic final candidate; host completion gates remain authoritative.");
            yield return AgentTransportEvent.Complete();
        }
        public void Cancel() => Release.TrySetCanceled();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

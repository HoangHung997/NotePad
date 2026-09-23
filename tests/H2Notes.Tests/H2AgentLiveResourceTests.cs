using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Office;
using H2AgentLab.OfficeHost;
using H2AgentLab.OfficeProtocol;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2Notes.Avalonia;
using H2Notes.Core;

/// <summary>AR-066 E1/E2 only. Production runtime/archive, disposable files and scripted
/// transport/native probe. This is not real Office/browser/AutoCAD or real-model acceptance.</summary>
internal static class H2AgentLiveResourceTests
{
    public static void Run(Action<string, Action> test)
    {
        foreach (var (goal, kind, path) in new[]
        {
            ("Sửa file Word đang mở", H2ApplicationKind.Word, "A.docx"),
            ("Sua file Word dang mo", H2ApplicationKind.Word, "A.DOCM"),
            ("Đọc Excel đang active", H2ApplicationKind.Excel, "A.xlsx"),
            ("Đọc ô đang chọn", H2ApplicationKind.Unknown, "A.csv"),
            ("Sửa bản vẽ đang chọn", H2ApplicationKind.AutoCAD, "A.dwg"),
            ("Đọc trang web đang mở", H2ApplicationKind.Browser, "A.mhtml"),
            ("Read the currently open Word document", H2ApplicationKind.Word, "A.rtf"),
            ("Inspect current drawing", H2ApplicationKind.AutoCAD, "A.dxf"),
            ("Read current tab", H2ApplicationKind.Browser, "A.html")
        })
            test("AR-066 live source requirement " + goal, () =>
            {
                var requirement = H2AgentLiveResourceRequirement.FromUserRequest(goal);
                Check(requirement.Required && requirement.ApplicationKind == kind
                    && requirement.RejectsDiskPath(Path.GetFullPath(path)), "Live request became a disk target.");
            });
        foreach (var goal in new[] { "Tạo file Excel mới trong workspace", "Read the saved disk file A.docx", "Đọc bản đã lưu trên đĩa", "Hello" })
            test("AR-066 ordinary disk or creation request remains available " + goal, () =>
                Check(!H2AgentLiveResourceRequirement.FromUserRequest(goal).Required, "Ordinary disk work was made live-only."));
        test("AR-066 host selected live intent cannot be downgraded by goal wording", () =>
            Check(H2AgentLiveResourceRequirement.FromUserRequest("Read saved file", hostIntent: H2AgentTargetIntent.CapturedSelection).Required,
                "Host-selected live constraint was lost."));
        test("AR-066 exact captured live path is protected even without Office extension", () =>
        {
            var path = Path.GetFullPath("A.txt");
            var capture = Capture(path);
            var requirement = H2AgentLiveResourceRequirement.FromUserRequest("Read this document", capture);
            Check(requirement.Required && requirement.RejectsDiskPath(path), "Captured path was interpreted only by extension.");
            Check(!requirement.RejectsDiskPath(Path.GetFullPath("reference.txt")), "Unrelated reference text was blocked.");
        });
        test("AR-066 semantic rejection preserves typed preflight no-effect code", () =>
        {
            var call = new H2AgentLab.ToolCall("denied", "read_file", JsonSerializer.SerializeToElement(new { path = "A.docx" }));
            var result = ToolOutcomeBridge.Failure(call, null, "live_resource_required", ToolErrorPhase.Preflight, ToolMutationEffect.None);
            Check(result.Outcome.Error?.Code == "live_resource_required" && result.Outcome.Effect == ToolMutationEffect.None
                && result.Outcome.Error.Phase == ToolErrorPhase.Preflight, "Source semantics was flattened to permission/tool failure.");
        });
        foreach (var fault in new[] { "provider_busy", "modal_blocked", "native_object_unavailable" })
            test("AR-066 CONTROL same native object reacquired after " + fault, () =>
            {
                var probe = new Probe();
                using (var catalog = new OfficeWindowCatalog(probe))
                {
                    var first = catalog.Refresh("word").Single(); var id = first.SessionId; var native = first.Identity;
                    probe.Fault = fault;
                    Check(catalog.Refresh("word").Count == 0 && !catalog.LastReport.Complete
                        && catalog.LastReport.Issues.Single().Code == fault, "A failed view was returned as current/complete.");
                    Check(probe.Created - probe.Released == 1, "Last-proven comparison reference was discarded.");
                    probe.Fault = null;
                    var restored = catalog.Refresh("word").Single();
                    Check(restored.SessionId == id && restored.Identity == native && catalog.LastReport.Complete,
                        "Same-object recovery changed the pinned session.");
                    catalog.ValidateCurrent(restored, false);
                    Check(probe.Selections == 0, "Recovery read unrelated live content.");
                }
                Check(probe.Created == probe.Released, "Recovery leaked owned references.");
            });
        foreach (var change in new[] { "document", "process", "view", "path", "confirmed-close" })
            test("AR-066 transient loss never binds a replacement " + change, () =>
            {
                var probe = new Probe();
                using var catalog = new OfficeWindowCatalog(probe);
                var first = catalog.Refresh("word").Single(); var id = first.SessionId;
                probe.Fault = "native_object_unavailable"; _ = catalog.Refresh("word"); probe.Fault = null;
                switch (change)
                {
                    case "document": probe.Document = new object(); break;
                    case "process": probe.Candidate = probe.Candidate with { ProcessStartUtcTicks = 202 }; break;
                    case "view": probe.Candidate = probe.Candidate with { RootHandle = 2001, PaneHandle = 12001 }; break;
                    case "path": probe.Name = "Document2"; break;
                    case "confirmed-close": probe.Visible = false; _ = catalog.Refresh("word"); probe.Visible = true; break;
                }
                Check(catalog.Refresh("word").Single().SessionId != id, "Different resource resurrected the old session.");
            });
        test("AR-066 incomplete enumeration retains comparison identity without returning cached live view", () =>
        {
            var probe = new Probe(); using var catalog = new OfficeWindowCatalog(probe);
            var id = catalog.Refresh("word").Single().SessionId;
            probe.Incomplete = true;
            Check(catalog.Refresh("word").Count == 0 && !catalog.LastReport.Complete, "Cached view escaped failed enumeration.");
            probe.Incomplete = false;
            Check(catalog.Refresh("word").Single().SessionId == id, "Enumeration failure was treated as proof of closure.");
        });
        test("AR-066 observed stale native identity is retired rather than treated as transient", () =>
        {
            var probe = new Probe(); using var catalog = new OfficeWindowCatalog(probe);
            var id = catalog.Refresh("word").Single().SessionId;
            probe.Fault = "stale_resource"; _ = catalog.Refresh("word"); probe.Fault = null;
            Check(catalog.Refresh("word").Single().SessionId != id, "Stale identity was silently resumed.");
        });
        foreach (var project in new[] { false, true })
        foreach (var mode in new[] { "read", "write", "python", "command", "reference", "disk-only", "browser-notice", "cad-notice" })
            test((mode is "read" or "write" ? "AR-066 CONTROL production " : "AR-066 production ") + mode + " " + (project ? "Project" : "Global"),
                () => Task.Run(() => Production(project, mode)).GetAwaiter().GetResult());
    }

    private static async Task Production(bool project, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "h2-ar066-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
        var source = Path.Combine(workspace, "source.txt"); var reference = Path.Combine(workspace, "reference.txt");
        var diskWord = Path.Combine(workspace, "closed.docx"); var marker = Path.Combine(workspace, "unexpected.txt");
        await File.WriteAllTextAsync(source, "DISK-IS-NOT-UNSAVED-LIVE");
        await File.WriteAllTextAsync(reference, "INDEPENDENT-REFERENCE");
        await File.WriteAllTextAsync(diskWord, "DISPOSABLE-BLOCKED-BEFORE-READ");
        var before = Hash(source); var beforeWord = Hash(diskWord);
        var calls = new List<AgentTransportToolCall>();
        var notice = mode is "browser-notice" or "cad-notice";
        if (!notice && mode != "disk-only")
        {
            calls.Add(Search("word.get_active_document"));
            calls.Add(Call("native", "word.get_active_document", new { }));
        }
        if (notice) calls.Add(Search(mode == "browser-notice" ? "browser.live_tab" : "autocad.live_drawing"));
        else
        {
            var name = mode switch { "write" => "write_file", "python" => "run_python", "command" => "exec_command", _ => "read_file" };
            calls.Add(Search(name));
            object arguments = mode switch
            {
                "write" => new { path = "source.txt", text = "MUST-NOT-WRITE", expectedHash = before },
                "python" => new { code = "raise Exception('must not execute')", inputs = "closed.docx", previous_run = "" },
                "command" => new { command = "Set-Content -LiteralPath '" + marker.Replace("'", "''") + "' -Value UNEXPECTED", timeout_seconds = 10 },
                _ => new { path = mode == "reference" ? "reference.txt" : "source.txt", offset = "0" }
            };
            calls.Add(Call("fallback", name, arguments));
        }
        var wire = new Wire(calls.ToArray()); var factory = new Factory(wire); var client = new UnavailableOffice();
        var state = Path.Combine(root, "state"); var profile = new AiProfile { Protocol = AiProtocol.OpenAiChat, Model = "scripted-ar066", BaseUrl = "https://example.test/v1" };
        H2AgentTaskSummary original; Guid task;
        try
        {
            await using (var adapter = new H2ProductionAgentAdapter(state, () => new(profile, ""), factory,
                officeClientFactory: () => client, captureValidator: _ => true))
            {
                var permission = WorkAssistantPermissionScopeMapper.ForWorkspace(H2AgentPermissionMode.FullAccess, workspace, DateTime.UtcNow).PermissionScope;
                var goal = mode switch
                {
                    "disk-only" => "Read the saved disk file source.txt; this request uses its saved bytes.",
                    "browser-notice" => "Read current browser tab.",
                    "cad-notice" => "Inspect current AutoCAD drawing.",
                    _ => "Sửa tài liệu Word đang mở trên màn hình; bản live có nội dung chưa lưu."
                };
                task = await adapter.StartTaskAsync(project ? Guid.NewGuid() : null, goal,
                    new(workspace, "AR066 synthetic controlled corpus", PermissionScope: permission,
                        ActiveWorkContext: notice ? null : Capture(source)), readOnly: false);
                original = await Wait(adapter, task);
                if (notice)
                    Check(wire.Results.Any(r => r.Content.Contains(mode == "browser-notice" ? "browser.live_tab" : "autocad.live_drawing", StringComparison.Ordinal)
                        && r.Content.Contains("live_resource_required", StringComparison.Ordinal)), "Missing truthful live-provider readiness notice.");
                else if (mode == "disk-only")
                {
                    Check(original.Status == H2AgentTaskStatus.Completed && client.Discoveries == 0,
                        "A new explicit saved-file request was blocked or unnecessarily opened Office.");
                    Check(wire.Results.Single(r => r.ToolCallId == "fallback").Content.Contains("DISK-IS-NOT-UNSAVED-LIVE", StringComparison.Ordinal), "Saved-file source was unavailable.");
                }
                else
                {
                    Check(wire.Results.Any(r => r.ToolCallId == "native" && r.Content.Contains("native_object_unavailable", StringComparison.Ordinal)),
                        "Corpus did not reach the actual Office failure boundary.");
                    var fallback = wire.Results.Single(r => r.ToolCallId == "fallback").Content;
                    Check(mode == "reference" ? fallback.Contains("INDEPENDENT-REFERENCE", StringComparison.Ordinal)
                        : fallback.Contains("live_resource_required", StringComparison.Ordinal), "Host did not enforce live source semantics: " + fallback);
                    Check(!fallback.Contains("DISK-IS-NOT-UNSAVED-LIVE", StringComparison.Ordinal), "Disk body escaped as live source.");
                    Check(original.Status != H2AgentTaskStatus.Completed, "Unresolved live operation became completed.");
                }
                Check(Hash(source) == before && Hash(diskWord) == beforeWord && !File.Exists(marker), "A forbidden fallback changed test bytes.");
            }
            var rounds = wire.Rounds;
            await using (var reopened = new H2ProductionAgentAdapter(state, () => new(profile, ""), factory,
                officeClientFactory: () => client))
            {
                var restored = reopened.ObserveTask(task);
                Check(restored.Summary.Status == original.Status && factory.Created == 1 && wire.Rounds == rounds,
                    "Archive inspection restarted a provider or changed the terminal outcome.");
                Check(Hash(source) == before && Hash(diskWord) == beforeWord && !File.Exists(marker), "Reopen replayed a disk effect.");
                Save(mode + "-" + project, new { level = "E2", injected = "scripted transport and unavailable Office client",
                    task, mode, project, factory.Created, wire.Rounds, client.Discoveries, status = original.Status.ToString(),
                    sourceHash = before, wordHash = beforeWord, forbiddenMarkerAbsent = !File.Exists(marker), reopenedWithoutReplay = true,
                    errors = wire.Results.Select(r => new { r.ToolCallId, semanticRejection = r.Content.Contains("live_resource_required", StringComparison.Ordinal) }),
                    E3 = "AWAITING_ENVIRONMENT", E4 = "AWAITING_ENVIRONMENT", E5 = "DEFERRED_BY_USER" });
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class Probe : IOfficeWindowProbe
    {
        public OfficeWindowCandidate Candidate = new("word", 11, 101, 1, 1001, 11001);
        public object Document = new(); public string Name = "Document1";
        public string? Fault; public bool Visible = true, Incomplete; public int Created, Released, Selections;
        public long ForegroundRoot => 9999; // Deliberately not the bound view.
        public OfficeWindowScan Enumerate(string app, long? rootHandle = null) => Incomplete
            ? new([], [new("native_object_unavailable")], false, 0)
            : new(Visible && app == Candidate.Application && (rootHandle is null || rootHandle == Candidate.RootHandle) ? [Candidate] : [], [], true, Visible ? 1 : 0);
        public OfficeViewLease Open(OfficeWindowCandidate candidate)
        {
            if (Fault is { } code) throw new OfficeHostFaultException(code, "CONTROLLED", true);
            Created++;
            return new(candidate, candidate.RootHandle, new object(), Document, new object(), Name, Name, false, "FIXTURE",
                () => { Selections++; return "word-range:0:3"; }, () => Released++);
        }
    }
    private sealed class UnavailableOffice : IOfficeSessionClient
    {
        public string InstanceIdentity => "AR066-SCRIPTED-OFFICE"; public int Discoveries;
        public Task<WordDiscovery> DiscoverWordAsync(CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Discoveries++; return Task.FromResult(new WordDiscovery([], null)
            { Report = new(false, "CONTROLLED-UNAVAILABLE-NOT-NATIVE", 0, 1, 1, [new("native_object_unavailable")]) }); }
        public Task<ExcelDiscovery> DiscoverExcelAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WordLiveSnapshot> SnapshotWordAsync(string s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ExcelLiveSnapshot> SnapshotExcelAsync(string s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WordPatchResult> PatchWordAsync(WordPatchRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ExcelPatchResult> PatchExcelAsync(ExcelPatchRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ExcelLiveSnapshot> RecalculateExcelAsync(ExcelRecalculateRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OfficeSaveCopyResult> SaveExcelCopyAsync(OfficeSaveCopyRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OfficeSaveCopyResult> SaveWordCopyAsync(OfficeSaveCopyRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WordLanguageEvidenceResult> InspectWordLanguageAsync(WordLanguageEvidenceRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
    private sealed class Factory(Wire wire) : IAgentTransportFactory
    {
        public int Created;
        public IAgentTransport Create(AiProfile p, string key, AgentRunTelemetry t)
        { Created++; return wire; }
    }
    private sealed class Wire(AgentTransportToolCall[] calls) : IAgentTransport
    {
        private int _next; public int Rounds; public List<AgentToolResult> Results = [];
        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.ChatCompletionsFallback;
        public IAsyncEnumerable<AgentTransportEvent> StartAsync(AgentTransportStartRequest r, CancellationToken ct = default) => Round(ct);
        public IAsyncEnumerable<AgentTransportEvent> ContinueAsync(AgentTransportContinuationRequest r, CancellationToken ct = default)
        { Results.AddRange(r.ToolResults); return Round(ct); }
        private async IAsyncEnumerable<AgentTransportEvent> Round([EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Rounds++; await Task.CompletedTask;
            if (_next < calls.Length) yield return AgentTransportEvent.Tool(calls[_next++]);
            else yield return AgentTransportEvent.TextDeltaEvent("Scripted final candidate; host state remains authoritative.");
            yield return AgentTransportEvent.Complete();
        }
        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static H2ActiveWorkContext Capture(string path) => new(11, 101, "WINWORD", H2ApplicationKind.Word, 1001,
        "fixture-window", "AR066 fixture", null, path, null, "office-host", DateTime.UtcNow);
    private static AgentTransportToolCall Search(string name) => Call("load-" + name, "tool_search", new { query = name });
    private static AgentTransportToolCall Call(string id, string name, object args) => new(id, name, JsonSerializer.Serialize(args));
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task<H2AgentTaskSummary> Wait(IH2AgentAdapter adapter, Guid task)
    {
        var until = Environment.TickCount64 + 20_000;
        while (Environment.TickCount64 < until)
        { var result = adapter.GetTaskSummary(task); if (H2AgentActivity.IsTerminal(result.Status)) return result; await Task.Delay(10); }
        throw new TimeoutException("AR066 bounded production fixture deadline.");
    }
    private static void Save(string name, object evidence)
    {
        var root = Environment.GetEnvironmentVariable("H2_AR066_EVIDENCE");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N") + ".json"),
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    }
}

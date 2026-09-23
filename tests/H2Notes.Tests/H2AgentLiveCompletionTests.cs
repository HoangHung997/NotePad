using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Office;
using H2AgentLab.OfficeProtocol;
using H2AgentLab.Transport;
using H2Notes.Avalonia;
using H2Notes.Core;

// E2 source-observation gate. All native snapshots and model turns are declared fixtures.
// Passing this corpus is not native Office, live model or UI acceptance.
internal static class H2AgentLiveCompletionTests
{
    public static void Run(Action<string, Action> test)
    {
        foreach (var project in new[] { false, true })
        foreach (var mode in new[] { "no-tools", "discovery-only", "word-read", "excel-read", "wrong-kind", "wrong-session", "incomplete-after-read" })
            test($"AR-066 completion source {mode} project={project}",
                () => Task.Run(() => Execute(project, mode)).GetAwaiter().GetResult());
    }

    private static async Task Execute(bool project, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "h2-ar066-completion-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
        var control = Path.Combine(workspace, "control.txt");
        var marker = Path.Combine(workspace, "unexpected.txt");
        var word = Path.Combine(workspace, "source.docx");
        var excel = Path.Combine(workspace, "source.xlsx");
        await File.WriteAllTextAsync(control, "UNCHANGED-CONTROL");
        // These are sentinel disk bytes, deliberately not the separate live snapshot below.
        await File.WriteAllTextAsync(word, "DISK-IS-NOT-LIVE-WORD");
        await File.WriteAllTextAsync(excel, "DISK-IS-NOT-LIVE-EXCEL");
        var before = Hash(word); var excelBefore = Hash(excel); var controlBefore = Hash(control);
        var office = new OfficeFixture(word, excel, mode); var wire = new Wire(mode); var factory = new Factory(wire);
        var state = Path.Combine(root, "state");
        var profile = new AiProfile { Protocol = AiProtocol.OpenAiChat, Model = "ar066-scripted-completion", BaseUrl = "https://example.test/v1" };
        Guid task; H2AgentTaskSummary original;
        try
        {
            await using (var adapter = new H2ProductionAgentAdapter(state, () => new(profile, ""), factory, officeClientFactory: () => office))
            {
                var grant = WorkAssistantPermissionScopeMapper.ForWorkspace(H2AgentPermissionMode.FullAccess, workspace, DateTime.UtcNow).PermissionScope;
                task = await adapter.StartTaskAsync(project ? Guid.NewGuid() : null,
                    mode == "excel-read" ? "Đọc Excel đang mở." : "Đọc Word đang mở.",
                    new(workspace, "AR066 declared synthetic live snapshot", PermissionScope: grant,
                        TargetPaths: [new(word, false, "explicit-user"), new(excel, false, "explicit-user")]), readOnly: true);
                var until = Environment.TickCount64 + 20000;
                original = adapter.GetTaskSummary(task);
                while (!H2AgentActivity.IsTerminal(original.Status) && Environment.TickCount64 < until)
                { await Task.Delay(10); original = adapter.GetTaskSummary(task); }
                Check(H2AgentActivity.IsTerminal(original.Status), "Completion corpus timed out.");
                var positive = mode is "word-read" or "excel-read";
                Check(original.Status == (positive ? H2AgentTaskStatus.Completed : H2AgentTaskStatus.Blocked),
                    "Source gate returned wrong terminal state: " + original.Status + "; " + original.Error);
                if (positive)
                    Check(office.Reads == 1 && wire.Results.Any(r => r.Content.Contains("LIVE-SNAPSHOT-SENTINEL", StringComparison.Ordinal)),
                        "Successful completion has no actual selected-client snapshot observation.");
                if (mode is "no-tools" or "discovery-only")
                    Check(office.Reads == 0 && (original.Error ?? "").Contains("live_resource_required", StringComparison.Ordinal),
                        "Metadata or model prose was promoted to a live source observation.");
                if (mode == "wrong-kind")
                    Check(office.Reads == 1 && (original.Error ?? "").Contains("live_resource_required", StringComparison.Ordinal),
                        "A different application satisfied the required live source.");
                if (mode is "wrong-session" or "incomplete-after-read")
                    Check(office.Reads == 1 && wire.Results.All(r => !r.Content.Contains("LIVE-SNAPSHOT-SENTINEL", StringComparison.Ordinal)),
                        "Unvalidated native response was forwarded as current live content.");
                Check(Hash(word) == before && Hash(excel) == excelBefore && Hash(control) == controlBefore && !File.Exists(marker),
                    "Source-observation checks changed disk fixture bytes.");
            }
            var rounds = wire.Rounds; var reads = office.Reads; var discoveries = office.Discoveries;
            await using (var reopened = new H2ProductionAgentAdapter(state, () => new(profile, ""), factory, officeClientFactory: () => office))
            {
                var replay = reopened.ObserveTask(task);
                Check(replay.Summary.Status == original.Status && replay.Summary.Error == original.Error
                    && factory.Created == 1 && wire.Rounds == rounds && office.Reads == reads && office.Discoveries == discoveries,
                    "Archive reopening replayed provider/native work or lost source failure.");
                Check(Hash(word) == before && Hash(excel) == excelBefore && Hash(control) == controlBefore && !File.Exists(marker), "Reopen changed fixture bytes.");
                var receipts = Environment.GetEnvironmentVariable("H2_AR066_EVIDENCE");
                if (!string.IsNullOrWhiteSpace(receipts))
                {
                    Directory.CreateDirectory(receipts);
                    File.WriteAllText(Path.Combine(receipts, "source-completion-" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(new
                    {
                        level = "E2", injected = "scripted model and OfficeSessionClient snapshots; not native Office", task, project,
                        mode = "completion-" + mode, factory.Created, wire.Rounds, office.Reads, office.Discoveries,
                        status = original.Status.ToString(), sourceHash = before, wordHash = before, excelHash = excelBefore,
                        controlHash = controlBefore, forbiddenMarkerAbsent = !File.Exists(marker), reopenedWithoutReplay = true,
                        E3 = "AWAITING_ENVIRONMENT", E4 = "AWAITING_ENVIRONMENT", E5 = "DEFERRED_BY_USER"
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class OfficeFixture(string word, string excel, string mode) : IOfficeSessionClient
    {
        public string InstanceIdentity => "AR066-NATIVE-FIXTURE";
        public int Reads, Discoveries;
        private OfficeNativeIdentity Identity(string document) => new(11, 101, 1, 1001, 1001, 11001, document, "AR066-FIXTURE");
        private OfficeDiscoveryReport Report => mode == "incomplete-after-read" && Reads > 0
            ? new(false, "DECLARED-FIXTURE", 0, 1, 1, [new("native_object_unavailable")])
            : new(true, "DECLARED-FIXTURE", 0, 1, 1, []);
        public Task<WordDiscovery> DiscoverWordAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Discoveries++;
            return Task.FromResult(new WordDiscovery([new("word-session", "source.docx", word, false, 0, 0, "", "")
                { NativeIdentity = Identity("word-document") }], "word-session") { Report = Report });
        }
        public Task<ExcelDiscovery> DiscoverExcelAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Discoveries++;
            return Task.FromResult(new ExcelDiscovery([new("excel-session", "source.xlsx", excel, false, "Sheet1", "A1", "")
                { NativeIdentity = Identity("excel-document") }], "excel-session") { Report = Report });
        }
        public Task<WordLiveSnapshot> SnapshotWordAsync(string session, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Reads++; Check(session == "word-session", "Native session changed before read.");
            return Task.FromResult(new WordLiveSnapshot(mode == "wrong-session" ? "foreign-session" : session,
                "source.docx", word, false, 0, 0, "", [new(0, "LIVE-SNAPSHOT-SENTINEL", "Normal", [])], [], [], [], [], "live-word-state")
                { NativeIdentity = Identity("word-document") });
        }
        public Task<ExcelLiveSnapshot> SnapshotExcelAsync(string session, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Reads++; Check(session == "excel-session", "Native session changed before read.");
            return Task.FromResult(new ExcelLiveSnapshot(session, "source.xlsx", excel, false, "Sheet1", "A1",
                [new("Sheet1", "visible", [new("A1", "LIVE-SNAPSHOT-SENTINEL", "", false, false, null, "General", "General", "Bottom")], [], [], [])], "live-excel-state")
                { NativeIdentity = Identity("excel-document") });
        }
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
        public IAgentTransport Create(AiProfile profile, string key, AgentRunTelemetry telemetry) { Created++; return wire; }
    }
    private sealed class Wire(string mode) : IAgentTransport
    {
        public int Rounds; public List<AgentToolResult> Results = [];
        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.ChatCompletionsFallback;
        public IAsyncEnumerable<AgentTransportEvent> StartAsync(AgentTransportStartRequest r, CancellationToken ct = default) => Round(ct);
        public IAsyncEnumerable<AgentTransportEvent> ContinueAsync(AgentTransportContinuationRequest r, CancellationToken ct = default)
        { Results.AddRange(r.ToolResults); return Round(ct); }
        private async IAsyncEnumerable<AgentTransportEvent> Round([EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); await Task.CompletedTask; Rounds++;
            var name = mode == "discovery-only" ? "word.list_documents" : mode is "excel-read" or "wrong-kind"
                ? "excel.get_active_workbook" : "word.get_active_document";
            if (mode != "no-tools" && Rounds == 1)
                yield return AgentTransportEvent.Tool(new("search", "tool_search", JsonSerializer.Serialize(new { query = name })));
            else if (mode != "no-tools" && Rounds == 2)
                yield return AgentTransportEvent.Tool(new("live-observation", name, "{}"));
            else yield return AgentTransportEvent.TextDeltaEvent("Model asserts completion; host source and verification gates decide.");
            yield return AgentTransportEvent.Complete();
        }
        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

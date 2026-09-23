using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Office;
using H2AgentLab.OfficeProtocol;
using H2AgentLab.Transport;
using H2Notes.Core;
using W = DocumentFormat.OpenXml.Wordprocessing;

// AR-066 E2: actual production approval/permission/runtime/file/journal; declared scripted model
// and native client/capture. No external network, credential, personal document or native acceptance.
internal static class H2AgentSourceConsentTests
{
    public static void Run(Action<string, Action> test)
    {
        foreach (var project in new[] { false, true })
        foreach (var mode in new[] { "approve-read", "deny", "duplicate", "wrong-source", "ungrounded", "secret",
            "file-changed", "revision-pending", "capture-stale", "cancel", "expired-pending", "approved-no-read",
            "metadata-only", "partial-read", "post-read-change", "revision-after-read", "reference-read-only",
            "reference-with-live", "reference-write", "output-create", "output-no-live", "output-existing", "output-overwrite",
            "readonly-write", "return-live", "return-live-no-read", "unknown-binding", "two-references", "unknown-effect" })
            test($"AR-066 source consent {mode} project={project}",
                () => Task.Run(() => Execute(project, mode)).GetAwaiter().GetResult());
    }

    private static async Task Execute(bool project, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "h2-ar066-consent-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
        var isOutput = mode.StartsWith("output-", StringComparison.Ordinal);
        var source = Path.Combine(workspace, isOutput ? "source.csv" : "source.txt");
        var reference = Path.Combine(workspace, "reference.docx");
        var reference2 = Path.Combine(workspace, "reference-two.docx");
        var output = Path.Combine(workspace, "output.csv");
        var binary = Path.Combine(workspace, "metadata.pdf");
        var control = Path.Combine(workspace, "control.txt");
        var effect = Path.Combine(workspace, "native-fixture-effect.txt");
        var outside = Path.Combine(root, "outside.txt");
        var secret = Path.Combine(workspace, ".env");
        const string diskText = "DISK-SNAPSHOT-ĐÚNG";
        await File.WriteAllTextAsync(source, diskText);
        await File.WriteAllTextAsync(control, "UNCHANGED-CONTROL");
        await File.WriteAllTextAsync(outside, "OUTSIDE-NOT-GROUNDED");
        await File.WriteAllTextAsync(secret, "SYNTHETIC-NOT-A-CREDENTIAL");
        await File.WriteAllTextAsync(binary, "%PDF-1.4\nMetadata fixture only, not a rendered PDF.");
        CreateWord(reference, "REFERENCE-ONE"); CreateWord(reference2, "REFERENCE-TWO");
        if (mode == "output-existing") await File.WriteAllTextAsync(output, "EXISTING-OUTPUT");
        var sourceBefore = Hash(source); var controlBefore = Hash(control);
        var referenceBefore = Hash(reference); var reference2Before = Hash(reference2);
        var outsideBefore = Hash(outside); var secretBefore = Hash(secret);
        var state = Path.Combine(root, "state");
        var native = new NativeFixture(source, effect, mode);
        var captureValid = true;
        var capture = new H2ActiveWorkContext(11, 101, isOutput ? "EXCEL" : "WINWORD",
            isOutput ? H2ApplicationKind.Excel : H2ApplicationKind.Word, 1001,
            native.Identity.WindowIdentity, "AR066 disposable live window", mode == "unknown-binding" ? null : isOutput ? "excel-session" : "word-session",
            source, null, "office-host", DateTime.UtcNow) { NativeViewIdentity = native.Identity.ViewIdentity };
        var profile = new AiProfile { Protocol = AiProtocol.OpenAiChat, Model = "ar066-scripted-source-consent", BaseUrl = "https://example.test/v1" };
        var wire = new Wire(); var factory = new Factory(wire);
        var approvals = new List<Guid>(); var decisions = Array.Empty<H2AgentSourceDecision>();
        H2AgentToolOutcome? uncertainBeforeReopen = null;
        Guid task = Guid.Empty; H2AgentTaskSummary original;
        var expiry = DateTime.UtcNow.AddSeconds(mode == "expired-pending" ? 6 : 600);
        var grant = new H2AgentPermissionScope(H2AgentPermissionMode.FullAccess, H2AgentResourceScopeKind.Machine,
            H2AgentPermissionScope.CurrentMachineResourceKey, true, false, DateTime.UtcNow, expiry);
        var chosen = mode switch { "ungrounded" => outside, "secret" => secret, "metadata-only" => binary,
            "reference-read-only" or "reference-with-live" or "reference-write" or "two-references" => reference,
            _ when isOutput => output, _ => source };
        var role = isOutput ? "output" : mode.StartsWith("reference-", StringComparison.Ordinal) || mode == "two-references" ? "reference" : "replace_live";
        try
        {
            await using (var adapter = new H2ProductionAgentAdapter(state, () => new(profile, ""), factory,
                officeClientFactory: () => native, captureValidator: _ => captureValid))
            {
                var mutating = isOutput || mode is "reference-write" or "unknown-effect";
                // Even the mutation case never uses a real native application. Its effect is one
                // explicit file owned by this test; the Office reply is deliberately lost.
                wire.BeforeFinal = () =>
                {
                    if (mode == "post-read-change") File.WriteAllText(source, "CHANGED-AFTER-READ");
                    if (mode == "revision-after-read")
                    {
                        var id = Guid.NewGuid();
                        Check(adapter.SupplementTask(task, id, "Đọc Word đang mở.")
                            && adapter.SupplementTask(task, id, "Đọc Word đang mở."), "Trusted revision retry was not idempotent.");
                    }
                };
                void Add(string id, string name, object args) => wire.Add(id, name, () => args);
                void Inspect(string id) => Add(id, "resource_sources", new { });
                void Choose(string id, string inspector, string path, string selectedRole)
                    => wire.Add(id, "request_source_change", () => new { source_id = mode == "wrong-source" ? "foreign-source"
                        : Json(wire.Result(inspector)).GetProperty("source_id").GetString(), path, role = selectedRole });
                void Read(string id, string path, string offset = "0") => Add(id, "read_file", new { path, offset });
                void Live(string id) => Add(id, isOutput ? "excel.get_active_workbook" : "word.get_active_document", new { });
                if (mode is "output-create" or "output-overwrite" or "return-live-no-read" or "unknown-effect") Live("native-before");
                if (mode == "unknown-effect") Add("uncertain", "word.replace_range", new { session_id = "word-session", state_token = "live-state",
                    paragraphs = new[] { new { paragraphIndex = 0, text = "NATIVE-CHANGED" } } });
                Inspect("inspect"); Choose("choose", "inspect", chosen, role);
                if (mode is "duplicate" or "deny") Choose("choose-again", "inspect", chosen, role);
                if (mode == "two-references") Choose("choose-reference2", "inspect", reference2, "reference");
                if (mode is "readonly-write" or "reference-write")
                    Add("attempt-write", "write_text", new { path = chosen, text = "MUST-NOT-WRITE", expectedHash = Hash(chosen) });
                if (isOutput && mode != "output-existing") Add("create-output", "write_text", new { path = output, text = "Created,ĐÚNG", expectedHash = "" });
                if (mode != "approved-no-read" && mode != "output-existing" && mode != "unknown-effect") Read("disk-read", chosen, mode == "partial-read" ? "1" : "0");
                if (mode == "two-references") Read("reference2-read", reference2);
                if (mode == "output-overwrite") wire.Add("overwrite-output", "write_text", () => new { path = output, text = "MUST-NOT-OVERWRITE", expectedHash = Hash(output) });
                if (mode is "reference-with-live" or "two-references") Live("live-final");
                if (mode is "return-live" or "return-live-no-read")
                {
                    Choose("choose-live", "inspect", "", "live");
                    if (mode == "return-live") Live("live-final");
                }
                task = await adapter.StartTaskAsync(project ? Guid.NewGuid() : null,
                    isOutput ? "Đọc Excel đang mở." : mode == "unknown-effect" ? "Sửa Word đang mở." : "Đọc Word đang mở.",
                    new(workspace, "AR066 isolated source role fixture", PermissionScope: grant, ActiveWorkContext: capture),
                    readOnly: !mutating);
                wire.Release.TrySetResult();
                var until = Environment.TickCount64 + 25000;
                original = adapter.GetTaskSummary(task);
                while (!H2AgentActivity.IsTerminal(original.Status) && Environment.TickCount64 < until)
                {
                    if (original.PendingApproval is { } approval && !approvals.Contains(approval.ApprovalId))
                    {
                        approvals.Add(approval.ApprovalId);
                        Check(approval.Title.StartsWith("Chấp thuận nguồn", StringComparison.Ordinal), "Source selection attempted a hidden mutation approval.");
                        Check(approval.Details.Contains(task.ToString(), StringComparison.Ordinal)
                            && approval.Details.Contains(capture.DocumentSessionId ?? "chưa gắn", StringComparison.Ordinal)
                            && approval.Details.Contains("Quyền thay đổi vẫn kiểm tra riêng", StringComparison.Ordinal)
                            && approval.Details.Contains("Hiệu lực", StringComparison.Ordinal), "Approval lost source/authority/lifetime meaning.");
                        if (approvals.Count == 1) Check(approval.Details.Contains(chosen, StringComparison.Ordinal), "Approval hid the exact disk target.");
                        Check(Hash(source) == sourceBefore && Hash(control) == controlBefore && Hash(reference) == referenceBefore,
                            "A disk side effect occurred before source approval.");
                        Check(!adapter.RespondToApproval(task, Guid.NewGuid(), true), "Unrelated approval ID was accepted.");
                        Check(!adapter.RespondToApproval(Guid.NewGuid(), approval.ApprovalId, true), "Foreign task approved this source.");
                        if (mode == "file-changed") File.WriteAllText(source, "CHANGED-WHILE-APPROVAL-PENDING");
                        if (mode == "revision-pending") Check(adapter.SupplementTask(task, Guid.NewGuid(), "Đọc Word đang mở."), "User revision was refused while approval pending.");
                        if (mode == "capture-stale") captureValid = false;
                        if (mode == "expired-pending")
                            while (DateTime.UtcNow <= expiry) await Task.Delay(10);
                        if (mode == "cancel") adapter.CancelTask(task);
                        else Check(adapter.RespondToApproval(task, approval.ApprovalId, mode != "deny"), "Current explicit source response was not delivered.");
                        Check(!adapter.RespondToApproval(task, approval.ApprovalId, true), "Duplicate response reactivated source approval.");
                    }
                    await Task.Delay(10); original = adapter.GetTaskSummary(task);
                }
                Check(H2AgentActivity.IsTerminal(original.Status), "Source consent fixture did not terminate: " + mode);
                var positive = mode is "approve-read" or "duplicate" or "reference-with-live" or "two-references" or "output-create" or "return-live";
                Check(original.Status == (mode == "cancel" ? H2AgentTaskStatus.Cancelled : positive ? H2AgentTaskStatus.Completed : H2AgentTaskStatus.Blocked),
                    $"Source consent {mode} returned {original.Status}: {original.Error}. Tools: " + string.Join(" | ", wire.Results.Select(r => r.ToolName + ": " + r.Content[..Math.Min(200, r.Content.Length)])));
                decisions = adapter.ObserveTask(task).Progress.Where(p => p.SourceDecision is not null).Select(p => p.SourceDecision!).ToArray();
                var noApproval = mode is "wrong-source" or "ungrounded" or "secret" or "unknown-binding" or "output-existing" or "unknown-effect";
                Check(approvals.Count == (noApproval ? 0 : mode is "two-references" or "return-live" or "return-live-no-read" ? 2 : 1), "Unexpected number of source approvals.");
                Check(decisions.All(d => d.TaskId == task && d.ApprovalId != Guid.Empty && approvals.Contains(d.ApprovalId)
                    && d.OriginalSource is not null && d.GoalRevisionId.Length > 0 && d.SourceId.StartsWith("source-", StringComparison.Ordinal)), "Uncorrelated source decision was persisted.");
                var refused = mode is "deny" or "file-changed" or "revision-pending" or "capture-stale" or "expired-pending";
                Check(decisions.Length == (noApproval || mode == "cancel" ? 0 : approvals.Count)
                    && decisions.All(d => d.Approved == !refused), "Decision persistence did not match the actual current approval.");
                if (mode == "duplicate") Check(Json(wire.Result("choose-again")).GetProperty("decision").GetProperty("DecisionId").GetGuid() == decisions.Single().DecisionId,
                    "Identical proposal caused a new decision rather than reusing the exact receipt.");
                if (mode is "approve-read" or "duplicate" or "partial-read" or "post-read-change" or "revision-after-read" or "return-live" or "return-live-no-read")
                    Check(Json(wire.Result("disk-read")).GetProperty("content").GetString() == (mode == "partial-read" ? diskText[1..] : diskText), "Selected disk source was not really read.");
                if (mode.StartsWith("reference-", StringComparison.Ordinal) || mode == "two-references")
                    Check(Json(wire.Result("disk-read")).GetProperty("content").GetString()!.Contains("REFERENCE-ONE", StringComparison.Ordinal), "Approved reference read did not reach the real disk document.");
                if (mode is "reference-with-live" or "two-references" or "return-live")
                    Check(wire.Result("live-final").Contains("LIVE-SNAPSHOT-SENTINEL", StringComparison.Ordinal), "No newly selected native fixture observation reached the model.");
                if (mode is "readonly-write" or "reference-write")
                    Check(wire.Result("attempt-write").Contains(mode == "readonly-write" ? "permission_required" : "live_resource_required", StringComparison.Ordinal), "Source consent widened mutation authority.");
                if (mode == "output-overwrite") Check(wire.Result("overwrite-output").Contains("live_resource_required", StringComparison.Ordinal), "Output approval permitted overwriting a created file.");
                if (mode == "unknown-effect")
                {
                    Check(native.Patches == 1 && File.ReadAllText(effect) == "APPLIED-ONCE" && decisions.Length == 0
                        && wire.Result("choose").Contains("outcome_unknown", StringComparison.Ordinal), "Source selection cleared or repeated an uncertain effect.");
                    uncertainBeforeReopen = adapter.ObserveTask(task).Progress.Select(p => p.ToolOutcome)
                        .Single(o => o is { Effect: H2ToolMutationEffect.Unknown, ErrorCode: "connection_lost" });
                    Check(uncertainBeforeReopen is { InvocationId: var id, Verification: H2ToolVerificationStatus.NotRun }
                        && id != Guid.Empty, "Lost-response fixture did not retain the authoritative uncertain invocation.");
                }
                if (mode is not ("file-changed" or "post-read-change")) Check(Hash(source) == sourceBefore, "Unapproved source write occurred.");
                if (mode == "file-changed") Check(File.ReadAllText(source) == "CHANGED-WHILE-APPROVAL-PENDING", "Pending-file fixture was overwritten.");
                if (mode == "post-read-change") Check(File.ReadAllText(source) == "CHANGED-AFTER-READ", "Changed source was replayed.");
                Check(Hash(control) == controlBefore && Hash(reference) == referenceBefore && Hash(reference2) == reference2Before
                    && Hash(outside) == outsideBefore && Hash(secret) == secretBefore, "Consent affected a control/reference/ungrounded file.");
                if (isOutput) Check(File.ReadAllText(output) == (mode == "output-existing" ? "EXISTING-OUTPUT" : "Created,ĐÚNG"), "Output was lost or overwritten.");
            }
            var requests = wire.Rounds; var lastHash = Hash(source); var outputHash = File.Exists(output) ? Hash(output) : null;
            var effectHash = File.Exists(effect) ? Hash(effect) : null;
            await using (var reopened = new H2ProductionAgentAdapter(state, () => new(profile, ""), factory, officeClientFactory: () => native, captureValidator: _ => true))
            {
                var replay = reopened.ObserveTask(task);
                var retained = replay.Progress.Where(p => p.SourceDecision is not null).Select(p => p.SourceDecision!).ToArray();
                Check(replay.Summary.Status == original.Status, $"Reopen changed terminal state: {original.Status} -> {replay.Summary.Status}.");
                Check(retained.SequenceEqual(decisions), "Reopen changed source-decision receipts.");
                Check(factory.Created == 1 && wire.Rounds == requests, "Reopen reallocated provider or replayed source work.");
                if (mode == "unknown-effect")
                {
                    // Existing AR-031 archive.Get intentionally projects unresolved effects as
                    // ReconcileRequired. The original live error is not the restart authority.
                    Check(replay.Summary.Recovery is { ReconcileRequired: true, Interrupted: false }
                        && replay.Summary.Error == "Interrupted / ReconcileRequired: tác động cần đối soát; không tự lặp lệnh ghi.",
                        "Uncertain restart lost the required reconciliation projection: " + JsonSerializer.Serialize(replay.Summary.Recovery));
                    var operation = replay.Summary.Recovery!.Operations.Single(o => o.ToolCallId == "uncertain");
                    Check(uncertainBeforeReopen is not null && operation.InvocationId == uncertainBeforeReopen.InvocationId
                        && operation.LogicalOperationId == uncertainBeforeReopen.LogicalOperationId
                        && operation.ToolName == "word.replace_range" && operation.GoalRevisionId == original.GoalState!.RevisionId
                        && operation.State == "Result" && operation.Effect == "Unknown" && operation.ErrorCode == "connection_lost"
                        && operation.ArgumentsSha256.Length == 64 && operation.OutputSha256?.Length == 64,
                        "Restart lost exact failed mutation identity, hashes or unknown-effect receipt.");
                    Check(replay.Progress.Any(p => p.ToolOutcome == uncertainBeforeReopen)
                        && native.Patches == 1 && File.ReadAllText(effect) == "APPLIED-ONCE",
                        "Restart erased the original outcome or repeated its native fixture effect.");
                }
                else Check(replay.Summary.Error == original.Error, "Non-uncertain terminal error changed on reopen.");
                Check(Hash(source) == lastHash && Hash(control) == controlBefore && (File.Exists(output) ? Hash(output) : null) == outputHash
                    && (File.Exists(effect) ? Hash(effect) : null) == effectHash, "Reopen changed file/effect state.");
                var receipts = Environment.GetEnvironmentVariable("H2_AR066_EVIDENCE");
                if (!string.IsNullOrWhiteSpace(receipts))
                {
                    Directory.CreateDirectory(receipts);
                    File.WriteAllText(Path.Combine(receipts, "source-consent-" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(new
                    {
                        level = "E2", injected = "scripted model/native client/capture; real H2 approval API, file IO and archive", task, project, mode,
                        approvals, decisions, status = original.Status.ToString(), sourceBefore, sourceAfter = lastHash,
                        controlBefore, controlAfter = Hash(control), referenceBefore, referenceAfter = Hash(reference), outputHash,
                        native.Patches, native.Reads, providerAllocations = factory.Created, providerRequests = requests,
                        originalError = original.Error, reopenedError = replay.Summary.Error,
                        reopenedStatus = replay.Summary.Status.ToString(), recovery = replay.Summary.Recovery,
                        uncertainBeforeReopen, effectHash, reopenedEffectHash = File.Exists(effect) ? Hash(effect) : null,
                        reopenedWithoutReplay = true, E3 = "AWAITING_ENVIRONMENT", E4 = "AWAITING_ENVIRONMENT", E5 = "DEFERRED_BY_USER"
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }
        finally { wire.Release.TrySetResult(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void CreateWord(string path, string text)
    {
        using var doc = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var part = doc.AddMainDocumentPart(); part.Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text(text))))); part.Document.Save();
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static JsonElement Json(string text)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text)); using var document = JsonDocument.ParseValue(ref reader); return document.RootElement.Clone();
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Factory(Wire wire) : IAgentTransportFactory
    {
        public int Created;
        public IAgentTransport Create(AiProfile profile, string key, AgentRunTelemetry telemetry) { Created++; return wire; }
    }
    private sealed class Wire : IAgentTransport
    {
        private readonly List<(string Id, string Name, Func<object> Args)> _steps = [];
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<AgentToolResult> Results { get; } = [];
        public int Rounds; private int _step; private bool _finalObserved;
        public Action? BeforeFinal;
        public string Result(string id) => Results.Single(r => r.ToolCallId == id).Content;
        public void Add(string id, string name, Func<object> args)
        {
            _steps.Add(("discover-" + id, "tool_search", () => new { query = name }));
            _steps.Add((id, name, args));
        }
        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.ChatCompletionsFallback;
        public IAsyncEnumerable<AgentTransportEvent> StartAsync(AgentTransportStartRequest r, CancellationToken ct = default) => Round(ct);
        public IAsyncEnumerable<AgentTransportEvent> ContinueAsync(AgentTransportContinuationRequest r, CancellationToken ct = default)
        { Results.AddRange(r.ToolResults); return Round(ct); }
        private async IAsyncEnumerable<AgentTransportEvent> Round([EnumeratorCancellation] CancellationToken ct)
        {
            await Release.Task.WaitAsync(ct); ct.ThrowIfCancellationRequested(); Rounds++;
            if (_step < _steps.Count)
            {
                var step = _steps[_step++]; yield return AgentTransportEvent.Tool(new(step.Id, step.Name, JsonSerializer.Serialize(step.Args())));
            }
            else
            {
                if (!_finalObserved) { _finalObserved = true; BeforeFinal?.Invoke(); }
                yield return AgentTransportEvent.TextDeltaEvent("Scripted final candidate. Host source, permission, effect and verification remain authoritative.");
            }
            yield return AgentTransportEvent.Complete();
        }
        public void Cancel() => Release.TrySetResult();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class NativeFixture(string source, string effect, string mode) : IOfficeSessionClient
    {
        public OfficeNativeIdentity Identity => new(11, 101, 1, 1001, 1001, 11001, "source-document", "AR066-SCRIPTED-NATIVE");
        public string InstanceIdentity => "AR066-CONSENT-NATIVE-FIXTURE";
        public int Reads, Patches;
        private OfficeDiscoveryReport Report => new(true, "DECLARED-FIXTURE", 0, 1, 1, []);
        public Task<WordDiscovery> DiscoverWordAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new WordDiscovery([new("word-session", Path.GetFileName(source), source, false, 0, 0, "", "")
                { NativeIdentity = Identity }], "word-session") { Report = Report });
        }
        public Task<ExcelDiscovery> DiscoverExcelAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ExcelDiscovery([new("excel-session", Path.GetFileName(source), source, false, "Sheet1", "A1", "")
                { NativeIdentity = Identity }], "excel-session") { Report = Report });
        }
        public Task<WordLiveSnapshot> SnapshotWordAsync(string session, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Reads++; Check(session == "word-session", "Wrong live Word source.");
            return Task.FromResult(new WordLiveSnapshot(session, Path.GetFileName(source), source, false, 0, 0, "",
                [new(0, "LIVE-SNAPSHOT-SENTINEL", "Normal", [])], [], [], [], [], "live-state") { NativeIdentity = Identity });
        }
        public Task<ExcelLiveSnapshot> SnapshotExcelAsync(string session, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Reads++; Check(session == "excel-session", "Wrong live Excel source.");
            return Task.FromResult(new ExcelLiveSnapshot(session, Path.GetFileName(source), source, false, "Sheet1", "A1",
                [new("Sheet1", "visible", [new("A1", "LIVE-SNAPSHOT-SENTINEL", "", false, false, null, "General", "General", "Bottom")], [], [], [])], "live-state") { NativeIdentity = Identity });
        }
        public Task<WordPatchResult> PatchWordAsync(WordPatchRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Check(mode == "unknown-effect" && request.SessionId == "word-session", "Unexpected native mutation.");
            Patches++; Check(Patches == 1 && !File.Exists(effect), "An uncertain native mutation was replayed.");
            File.WriteAllText(effect, "APPLIED-ONCE"); throw new OfficeHostClientException("connection_lost", "Scripted response lost after the fixture effect.");
        }
        public Task<ExcelPatchResult> PatchExcelAsync(ExcelPatchRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ExcelLiveSnapshot> RecalculateExcelAsync(ExcelRecalculateRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OfficeSaveCopyResult> SaveExcelCopyAsync(OfficeSaveCopyRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OfficeSaveCopyResult> SaveWordCopyAsync(OfficeSaveCopyRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WordLanguageEvidenceResult> InspectWordLanguageAsync(WordLanguageEvidenceRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}

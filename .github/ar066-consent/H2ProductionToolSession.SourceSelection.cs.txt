using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using H2AgentLab.Runtime;
using H2AgentLab.Tools;
using H2Notes.Core;

namespace H2AgentLab.Integration;

internal sealed partial class H2ProductionToolSession
{
    internal sealed record SourceApprovalReply(Guid ApprovalId, bool Approved);
    private readonly Func<string, string, CancellationToken, Task<SourceApprovalReply>>? _approveSource;
    private readonly Func<string>? _sourceAuthorityStamp;
    private readonly Action<H2AgentSourceDecision>? _sourceDecisionObserved;
    private readonly object _sourceGate = new();
    // Bounded task-local projections. Durable decisions go ONLY to the existing Agent journal.
    // Nothing here is deserialized as a grant, nor carried to another task or process.
    private readonly Dictionary<string, H2AgentSourceDecision> _sourceDecisions = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> _sourceReadHashes = new();
    private readonly HashSet<string> _createdSourceOutputs = new(H2AgentTargetScope.PathComparer);
    private int _sourceUncertainEffect;
    private H2AgentSourceDecision? _selectedInputDecision;
    private readonly Dictionary<H2ApplicationKind, string> _liveObservationStamps = new();
    private const int MaxSourceDecisions = 32;

    private void RegisterSourceSelection(ToolRegistry registry)
    {
        if (_approveSource is null || _sourceAuthorityStamp is null || _sourceDecisionObserved is null || _jobRevision is null) return;
        var ns = new ToolNamespace("sources", "Host source selection. Approval changes source meaning, not execution permission.");
        void Add(string name, string description, object parameters, Func<ToolCall, CancellationToken, ValueTask<ToolExecutionOutput>> execute)
            => registry.Register(new ToolDescriptor(name, ns, description, AgentToolRisk.Low, AgentToolAccess.ReadOnly,
                false, "v1", JsonSerializer.SerializeToElement(new { type = "function", function = new { name, description, parameters } }),
                new DelegatingOutcomeToolExecutor("h2-source-selection", execute),
                resourceScope: new("task", "host-source-selection"), serializationKey: "source-selection",
                canProvideVerificationEvidence: false, resultFormat: ToolResultFormat.Json));
        Add("resource_sources", "Inspect the host's required live input and exact source_id before proposing disk reference/output or replacing a live input. Metadata only, not a native read or proof of completion.",
            new { type = "object", properties = new { }, additionalProperties = false }, (call, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var source = CurrentSource();
                return ValueTask.FromResult(SourceSuccess(call, new { ok = true, source_id = source.Id,
                    required = source.Requirement.Required, application = source.Requirement.ApplicationKind.ToString(),
                    original_source = source.Binding, can_replace = source.Requirement.Required && source.Requirement.ApplicationKind != H2ApplicationKind.Unknown
                        && source.Binding?.Kind == H2AgentResourceKind.LiveDocument,
                    goal_revision = _jobRevision(), decisions = CurrentDecisions(), selected_input = SelectedInput(),
                    notice = "References/outputs do not replace live input. Replacement requires a known exact live identity and explicit approval. No file scope or mutation permission is granted. Uncertain effects remain fenced." }));
            });
        Add("request_source_change", "Ask the user to approve an exact disk source role in this task. reference permits only reference reads; output permits create-only output and its readback; replace_live changes this exact live input to DiskSnapshot; live explicitly reselects the original live input (path must be empty). Always waits for explicit UI approval, even with Full Access. No automatic retry or permission expansion.",
            new { type = "object", properties = new {
                source_id = new { type = "string", description = "Exact current source_id from resource_sources." },
                path = new { type = "string", description = "Exact grounded file, not a folder/glob. Empty only for role live. No new external scope." },
                role = new { type = "string", @enum = new[] { "reference", "replace_live", "output", "live" } }
            }, required = new[] { "source_id", "path", "role" }, additionalProperties = false }, RequestSourceChangeAsync);
    }

    private (string Id, H2AgentLiveResourceRequirement Requirement, H2AgentResourceBinding? Binding) CurrentSource()
    {
        var requirement = Volatile.Read(ref _liveRequirement);
        H2AgentResourceBinding? binding = null;
        if (requirement.Required && requirement.ApplicationKind != H2ApplicationKind.Unknown)
        {
            if (_context?.ActiveWorkContext is { } capture && capture.ApplicationKind == requirement.ApplicationKind)
            {
                try { binding = H2AgentResourceBinding.FromCaptured(capture); }
                catch (ArgumentException) { /* Incomplete capture is not authority to pick a replacement. */ }
            }
            binding ??= _liveOffice?.SelectedSource(requirement.ApplicationKind);
        }
        var id = "source-" + Digest(JsonSerializer.Serialize(new { _taskId, stamp = _sourceAuthorityStamp?.Invoke(),
            requirement.Required, requirement.ApplicationKind, binding?.ResourceId, requirement.CapturedPath }));
        return (id, requirement, binding);
    }

    private H2AgentSourceDecision[] CurrentDecisions()
    {
        var stamp = _sourceAuthorityStamp?.Invoke();
        var source = CurrentSource();
        lock (_sourceGate) return _sourceDecisions.Values.Where(d => d.Approved && d.AuthorityStamp == stamp
            && d.SourceId == source.Id && d.ExpiresUtc > DateTime.UtcNow
            && (!ChangesInput(d.Role) || _selectedInputDecision?.DecisionId == d.DecisionId)).ToArray();
    }

    private async ValueTask<ToolExecutionOutput> RequestSourceChangeAsync(ToolCall call, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var role = Arg(call, "role") switch { "reference" => H2AgentSourceRole.Reference,
            "replace_live" => H2AgentSourceRole.ReplaceLive, "output" => H2AgentSourceRole.Output,
            "live" => H2AgentSourceRole.Live, _ => (H2AgentSourceRole)(-1) };
        if (!Enum.IsDefined(role) || _approveSource is null || _sourceAuthorityStamp is null || _sourceDecisionObserved is null
            || _jobRevision is null || _fileTargets is null || Arg(call, "path") is not { Length: <= 4096 } requested
            || (role == H2AgentSourceRole.Live ? requested.Length != 0 : requested.Length == 0))
            return SourceFailure(call, "invalid_arguments");
        // An approval is a user decision, never a way around the current grounded-file policy.
        string path = "";
        if (role != H2AgentSourceRole.Live)
        {
            try { path = _fileTargets.Resolve(requested); }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
            { return SourceFailure(call, "target_not_grounded"); }
            if (Directory.Exists(path) || !H2AgentTargetScope.HasNoReparsePoints(path)) return SourceFailure(call, "target_not_grounded");
        }
        await _approvalGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var source = CurrentSource();
            if (!source.Requirement.Required || Arg(call, "source_id") != source.Id) return SourceFailure(call, "stale_resource");
            if (ChangesInput(role) && (source.Binding?.Kind != H2AgentResourceKind.LiveDocument
                || source.Requirement.ApplicationKind == H2ApplicationKind.Unknown)) return SourceFailure(call, "ambiguous_target");
            if (ChangesInput(role) && Volatile.Read(ref _sourceUncertainEffect) != 0)
                return SourceFailure(call, "outcome_unknown");
            var stamp = _sourceAuthorityStamp();
            var fingerprint = Digest(JsonSerializer.Serialize(new { source.Id, role, path, stamp }));
            lock (_sourceGate)
            {
                if (_sourceDecisions.TryGetValue(fingerprint, out var prior))
                    return prior.Approved && prior.ExpiresUtc > DateTime.UtcNow
                        && (!ChangesInput(role) || _selectedInputDecision?.DecisionId == prior.DecisionId)
                        ? SourceSuccess(call, new { ok = true, decision = prior, replayed_decision = true, file_effect = "None" })
                        : SourceFailure(call, prior.Approved ? "stale_resource" : "permission_denied");
                if (_sourceDecisions.Count >= MaxSourceDecisions) return SourceFailure(call, "session_capacity");
            }
            if (role != H2AgentSourceRole.Live && (role == H2AgentSourceRole.Output ? File.Exists(path) : !File.Exists(path)))
                return SourceFailure(call, role == H2AgentSourceRole.Output ? "stale_resource" : "resource_not_found");
            if (!SourceCaptureCurrent()) return SourceFailure(call, "stale_resource");
            // Inspect metadata only before consent; no document body is used as a fallback.
            var fileStamp = role == H2AgentSourceRole.Live ? "live" : DiskMetadata(path);
            var revision = _jobRevision();
            var expires = DateTime.UtcNow.AddMinutes(10);
            if (_scope?.ExpiresUtc is { } scopeExpiry && scopeExpiry < expires) expires = scopeExpiry;
            var details = "Loại quyết định: chọn NGUỒN dữ liệu, không cấp quyền đọc/ghi mới.\n"
                + "Tác vụ: " + _taskId + "\nRevision: " + revision + "\nNguồn live: "
                + (source.Binding is { } bound ? $"{bound.ApplicationKind} / {bound.ResourceId} / {bound.DocumentSessionId} / {bound.CanonicalPath ?? "chưa lưu"}"
                    : source.Requirement.ApplicationKind + " (chưa gắn được tài nguyên)")
                + "\nĐầu vào đã chọn trước đó: " + (SelectedInput() is { } selected ? selected.Role + " / " + selected.DiskPath : "nguồn live gốc")
                + "\nTệp đích chính xác: " + (role == H2AgentSourceRole.Live ? "không dùng tệp đĩa; chọn lại đúng nguồn live gốc" : path)
                + "\nNgoài workspace: " + (role != H2AgentSourceRole.Live && _targetPolicy?.IsExternal(path) == true ? "Có" : "Không")
                + "\nVai trò: " + role + "\n"
                + (role == H2AgentSourceRole.ReplaceLive
                    ? "CHUYỂN đầu vào này sang bản đã lưu trên đĩa. Bản đó có thể thiếu mọi thay đổi chưa lưu; không đồng bộ/ngầm lưu tài liệu live."
                    : role == H2AgentSourceRole.Live ? "CHỌN LẠI nguồn live gốc. Phải đọc và xác minh lại đúng danh tính; không dùng quan sát cũ của bản đĩa hoặc bản live."
                    : role == H2AgentSourceRole.Reference ? "Chỉ đọc bản đĩa làm THAM KHẢO; vẫn phải hoàn thành yêu cầu trên nguồn live."
                    : "Chỉ tạo tệp đầu ra mới và đọc lại; không ghi đè tệp có sẵn, không thay thế đầu vào live.")
                + "\nQuyết định này không ghi/xóa tệp, không xóa lỗi, không phát lại thao tác. Quyền thay đổi vẫn kiểm tra riêng."
                + "\nHiệu lực: chỉ tác vụ/revision hiện tại, tối đa đến " + expires.ToString("O") + "; hết hiệu lực khi có lời nhắn mới hoặc khởi động lại.";
            // Never let the existing UI's 8,000-character bound hide the exact path or consequences.
            if (details.Length > 8000 || expires <= DateTime.UtcNow) return SourceFailure(call, "invalid_arguments");
            var reply = await _approveSource("Chấp thuận nguồn " + role + "?", details, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); _ownerCancellation.ThrowIfCancellationRequested();
            var current = CurrentSource();
            var stillCurrent = stamp == _sourceAuthorityStamp() && current.Id == source.Id && DateTime.UtcNow < expires
                && SourceCaptureCurrent() && SourcePathUnchanged(role, requested, path, fileStamp)
                && (!ChangesInput(role) || Volatile.Read(ref _sourceUncertainEffect) == 0);
            var accepted = reply.Approved && reply.ApprovalId != Guid.Empty && stillCurrent;
            var decision = new H2AgentSourceDecision(Guid.NewGuid(), reply.ApprovalId, _taskId, revision, stamp, source.Id,
                role, path, source.Binding, accepted, accepted ? "approved" : reply.Approved ? "stale_resource" : "permission_denied",
                DateTime.UtcNow, expires);
            // Persistence must succeed BEFORE any relaxation of the in-memory source fence.
            _sourceDecisionObserved(decision);
            lock (_sourceGate)
            {
                _sourceDecisions.Add(fingerprint, decision);
                if (accepted && ChangesInput(role)) _selectedInputDecision = decision;
            }
            return accepted ? SourceSuccess(call, new { ok = true, decision, source_kind = role == H2AgentSourceRole.Live ? "LiveDocument" : "DiskSnapshot",
                file_effect = "None", permission_granted = false, source_observed = false,
                notice = "Only source meaning was approved. Read the exact selected source afresh and verify all original outcomes. Other live sources and uncertain effects remain required." })
                : SourceFailure(call, decision.ReasonCode);
        }
        finally { _approvalGate.Release(); }
    }

    private bool SourceCaptureCurrent()
    {
        if (_context?.ActiveWorkContext is not { } capture) return true;
        return capture.CapturedUtc <= DateTime.UtcNow && DateTime.UtcNow - capture.CapturedUtc <= H2AgentTargetBindingPolicy.MaxCaptureAge
            && (_captureValidator ?? H2CapturedWindowIdentity.IsCurrent)(capture);
    }

    private static string DiskMetadata(string path)
    {
        var info = new FileInfo(path); info.Refresh();
        return info.Exists ? info.Length + ":" + info.LastWriteTimeUtc.Ticks + ":" + (int)info.Attributes : "absent";
    }

    private static bool ChangesInput(H2AgentSourceRole role) => role is H2AgentSourceRole.ReplaceLive or H2AgentSourceRole.Live;
    private H2AgentSourceDecision? SelectedInput() { lock (_sourceGate) return _selectedInputDecision; }
    private string ObservationStamp() => _sourceAuthorityStamp?.Invoke() + ":" + SelectedInput()?.DecisionId.ToString("N");

    private bool SourcePathUnchanged(H2AgentSourceRole role, string requested, string path, string before)
    {
        if (role == H2AgentSourceRole.Live) return true;
        try { return H2AgentTargetScope.PathComparer.Equals(path, _fileTargets!.Resolve(requested))
            && !Directory.Exists(path) && H2AgentTargetScope.HasNoReparsePoints(path) && DiskMetadata(path) == before; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    private bool HasReplacementFor(H2ApplicationKind application)
    {
        var selected = SelectedInput();
        // Expiration/revision invalidation never silently switches a chosen input back to live.
        return selected?.OriginalSource?.ApplicationKind == application
            && (selected.Role == H2AgentSourceRole.ReplaceLive || !CurrentDecisions().Any(d => d.DecisionId == selected.DecisionId));
    }

    private bool AllowsSelectedDisk(ToolDescriptor descriptor, ToolCall call, string path)
    {
        foreach (var decision in CurrentDecisions().Where(d => H2AgentTargetScope.PathComparer.Equals(d.DiskPath, path)))
        {
            if (decision.Role == H2AgentSourceRole.Reference && !descriptor.IsMutating) return true;
            if (decision.Role == H2AgentSourceRole.ReplaceLive && (!descriptor.IsMutating || Volatile.Read(ref _sourceUncertainEffect) == 0)) return true;
            if (decision.Role == H2AgentSourceRole.Output)
            {
                if (descriptor.IsMutating && call.Name is "write_text" or "publish_artifact"
                    && !File.Exists(path) && string.IsNullOrEmpty(Arg(call, "expectedHash"))) return true;
                lock (_sourceGate) if (!descriptor.IsMutating && _createdSourceOutputs.Contains(path)) return true;
            }
        }
        return false;
    }

    private void ObserveSourceResult(ToolDescriptor descriptor, ToolCall call, ToolExecutionOutput result)
    {
        if (result.Outcome.IsPending) Interlocked.Exchange(ref _sourceUncertainEffect, 1);
        if (descriptor.Namespace.Name is "word" or "excel")
        {
            var kind = descriptor.Namespace.Name == "word" ? H2ApplicationKind.Word : H2ApplicationKind.Excel;
            if (result.Outcome.Status == ToolOutcomeStatus.Succeeded
                && call.Name is not ("word.list_documents" or "excel.list_workbooks" or "word.save_copy" or "excel.save_copy")
                && _liveOffice?.HasCompletedLiveObservation(kind) == true)
                lock (_sourceGate) _liveObservationStamps[kind] = ObservationStamp();
            return;
        }
        var requested = Arg(call, "path") ?? (call.Name == "publish_artifact" ? Arg(call, "destination") : null);
        if (_fileTargets is null || requested is null
            || !(descriptor.Namespace.Name is "files" or "office" || call.Name == "publish_artifact")) return;
        if (!H2AgentTargetScope.TryNormalize(requested, out var path, _fileTargets.Root)) return;
        var decisions = CurrentDecisions().Where(d => H2AgentTargetScope.PathComparer.Equals(d.DiskPath, path)).ToArray();
        lock (_sourceGate)
        {
            foreach (var d in decisions) _sourceReadHashes.Remove(d.DecisionId);
            if (descriptor.IsMutating && result.Outcome.Status == ToolOutcomeStatus.Succeeded
                && decisions.Any(d => d.Role == H2AgentSourceRole.Output)) _createdSourceOutputs.Add(path);
        }
        // Only a real supported content read with its returned hash is source evidence. Structure
        // checks, metadata, file existence, approval and model prose are deliberately insufficient.
        if (result.Outcome.Status != ToolOutcomeStatus.Succeeded || call.Name is not ("read_file" or "word_paragraphs")) return;
        try
        {
            using var document = JsonDocument.Parse(result.DomainPayload);
            var data = document.RootElement;
            if (call.Name == "read_file")
            {
                if (!data.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String
                    || !data.TryGetProperty("offset", out var offset) || !offset.TryGetInt32(out var start) || start != 0
                    || !data.TryGetProperty("truncated", out var truncated) || truncated.ValueKind != JsonValueKind.False) return;
            }
            else if (!data.TryGetProperty("paragraphs", out var paragraphs) || paragraphs.ValueKind != JsonValueKind.Array
                || !data.TryGetProperty("limit", out var limit) || !limit.TryGetInt32(out var cap)
                || paragraphs.GetArrayLength() >= cap) return; // The legacy disk reader does not declare whether the cap truncated.
            if (!data.TryGetProperty("hash", out var hash) || hash.ValueKind != JsonValueKind.String) return;
            var actual = SafeWorkspace.Hash(_fileTargets.Read(path));
            if (!string.Equals(hash.GetString(), actual, StringComparison.OrdinalIgnoreCase)) return;
            lock (_sourceGate) foreach (var d in decisions.Where(d => d.Role == H2AgentSourceRole.ReplaceLive))
                _sourceReadHashes[d.DecisionId] = actual;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { /* No source proof. */ }
    }

    private bool HasRequiredSourceObservation(H2AgentLiveResourceRequirement requirement)
    {
        var replacements = CurrentDecisions().Where(d => d.Role == H2AgentSourceRole.ReplaceLive).ToArray();
        var selected = SelectedInput();
        if (selected is not null && !CurrentDecisions().Any(d => d.DecisionId == selected.DecisionId)) return false;
        if (replacements.Length == 0)
        {
            var stamp = ObservationStamp();
            lock (_sourceGate) return _liveObservationStamps.GetValueOrDefault(requirement.ApplicationKind) == stamp
                && _liveOffice?.HasCompletedLiveObservation(requirement.ApplicationKind) == true;
        }
        if (replacements.Length != 1) return false;
        foreach (var decision in replacements)
        {
            string? hash; lock (_sourceGate) _sourceReadHashes.TryGetValue(decision.DecisionId, out hash);
            if (hash is null || _fileTargets is null) continue;
            try { if (SafeWorkspace.Hash(_fileTargets.Read(decision.DiskPath)) == hash) return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return false;
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static ToolExecutionOutput SourceSuccess(ToolCall call, object payload)
        => new(JsonSerializer.Serialize(payload), ToolOutcome.Success(call, ToolMutationEffect.None, new(true)));
    private static ToolExecutionOutput SourceFailure(ToolCall call, string code)
        => ToolOutcomeBridge.Failure(call, null, code, ToolErrorPhase.Preflight, ToolMutationEffect.None);
}

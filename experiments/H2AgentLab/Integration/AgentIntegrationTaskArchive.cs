using System.Security.Cryptography;
using System.Text.Json;
using H2Notes.Core;

namespace H2AgentLab.Integration;

/// <summary>Finite local archive quotas. Retention trims only the rebuildable recent index;
/// journal sources, active work and referenced artifacts are never deleted by this archive.</summary>
public sealed record AgentArchiveOptions(int RecentLimit = 200, int MaxEvents = 100_000,
    long MaxJournalBytes = 256L * 1024 * 1024, int CheckpointEvery = 64)
{
    internal void Validate()
    {
        if (RecentLimit is < 1 or > 5000 || MaxEvents is < 1 or > 1_000_000
            || MaxJournalBytes is < 4096 or > 1024L * 1024 * 1024 || CheckpointEvery is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(AgentArchiveOptions));
    }
}

/// <summary>AR-031: the EXISTING integration archive now owns a versioned immutable journal.
/// Local volume only; not Coordinator, not a distributed lock or an automatic resume engine.
/// Source records are flushed and validated before activation. Checkpoints are disposable indexes.
/// Corrupt source yields a read-only verified prefix with explicit diagnostics, never healthy-empty.
/// Legacy JSON is imported once without modification and is not written in parallel with v2.</summary>
internal sealed partial class AgentIntegrationTaskArchive : IDisposable
{
    private const int Version = 2;
    internal const int MaxRecordBytes = 4 * 1024 * 1024;
    private static readonly string Zero = new('0', 64);
    private readonly object _gate = new();
    private readonly string _root;
    private string _store;
    private readonly AgentArchiveOptions _options;
    private readonly Action<string>? _fault;
    private FileStream? _lease;
    private bool _disposed, _readOnly, _capacityRequired;
    private Guid _storeId;
    private long _sequence, _bytes;
    private string _head = Zero;
    private readonly List<string> _diagnostics = [];
    private readonly Dictionary<Guid, H2AgentTaskSummary> _tasks = [];
    private readonly Dictionary<Guid, H2AgentThread> _threads = [];
    private readonly Dictionary<Guid, long> _taskHeads = [], _threadHeads = [];
    private readonly Dictionary<Guid, List<(long JournalSequence, H2AgentProgress Progress)>> _progress = [];
    private readonly Dictionary<(Guid TaskId, Guid InvocationId), H2AgentOperationRecord> _operations = [];
    private readonly HashSet<Guid> _interrupted = [], _eventIds = [];

    internal AgentIntegrationTaskArchive(string root, AgentArchiveOptions? options = null, Action<string>? fault = null)
    {
        _root = ValidateLocalRoot(root); _store = Path.Combine(_root, "journal-v2");
        _options = options ?? new(); _options.Validate(); _fault = fault;
        Directory.CreateDirectory(_root);
        // The handle, NOT the persistent filename, owns this one-machine writer lease.
        var lockPath = Path.Combine(_root, "agent-writer.lock");
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Agent writer lease cannot be a reparse alias.");
        try { _lease = new FileStream(lockPath, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("Agent archive is already open by another local writer.", ex); }
        try
        {
            if (!Directory.Exists(_store))
            {
                if (File.Exists(FormatPath)) throw new InvalidDataException("missing-journal");
                ImportLegacy();
            }
            else LoadJournal();
            if (!_readOnly)
            {
                ValidateRootMarker();
                ValidateHeadReceipt();
                ValidateOrRebuildCheckpoint();
            }
            foreach (var item in _tasks.Values.Where(t => !IsTerminal(t.Status))) _interrupted.Add(item.TaskId);
        }
        catch (Exception ex) when (StorageFailure(ex))
        {
            _readOnly = true; Issue("recovery-required:" + SafeCode(ex));
        }
        catch { _lease.Dispose(); _lease = null; throw; }
        foreach (var item in _tasks.Values.Where(t => !IsTerminal(t.Status))) _interrupted.Add(item.TaskId);
        if (!_readOnly && (_sequence >= _options.MaxEvents || _bytes >= _options.MaxJournalBytes))
            RequireCapacity();
    }

    private string FormatPath => Path.Combine(_root, "archive-format.json");
    public H2AgentArchiveStatus Status
    {
        get { lock (_gate) return new(!_disposed && !_readOnly && !_capacityRequired,
            _readOnly ? "RecoveryRequired" : _capacityRequired ? "CapacityRequired" : _diagnostics.Count > 0 ? "RecoveredWithDiagnostics" : "Healthy",
            _sequence, Array.AsReadOnly(_diagnostics.ToArray())); }
    }
    public void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_readOnly) throw new IOException("Agent archive cần phục hồi; chưa gửi công cụ hoặc khôi phục quyền cũ.");
        if (_capacityRequired) throw new IOException("Agent archive đã tới giới hạn lưu trữ; dữ liệu cũ vẫn được giữ nguyên.");
    }
    public void Upsert(H2AgentTaskSummary summary)
    {
        lock (_gate)
        {
            ValidateSummary(summary);
            var kind = _tasks.TryGetValue(summary.TaskId, out var old)
                && old.GoalState?.RevisionId != summary.GoalState?.RevisionId ? "revision" : "task-state";
            Append(summary.TaskId, kind, summary with { PendingApproval = null, Recovery = null });
        }
    }
    public bool AttachProject(Guid taskId, Guid projectId)
    {
        lock (_gate)
        {
            if (projectId == Guid.Empty) throw new ArgumentException("Project ID is required.");
            if (!_tasks.TryGetValue(taskId, out var old) || old.ProjectId is { } prior && prior != projectId) return false;
            Upsert(old with { ProjectId = projectId, UpdatedUtc = DateTime.UtcNow }); return true;
        }
    }
    public H2AgentTaskSummary? Get(Guid taskId)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var item)) return null;
            var operations = _operations.Where(p => p.Key.TaskId == taskId).Select(p => p.Value).ToArray();
            var interrupted = _interrupted.Contains(taskId);
            var pending = _readOnly || operations.Any(o => RequiresReconciliation(o, interrupted || !IsTerminal(item.Status)));
            var copy = Clone(item) with { PendingApproval = null,
                Recovery = new(interrupted, pending, _sequence, Array.AsReadOnly(operations)) };
            return interrupted || pending ? copy with { Status = H2AgentTaskStatus.Blocked,
                Completion = copy.Completion is null ? null : copy.Completion with { State = "Interrupted", PendingOperations = Math.Max(copy.Completion.PendingOperations, pending ? 1 : 0) },
                Error = _readOnly ? "RecoveryRequired: chỉ đọc được phần lịch sử đã xác thực; chưa đủ nguồn để xác nhận trạng thái mới nhất."
                    : pending ? "Interrupted / ReconcileRequired: tác động cần đối soát; không tự lặp lệnh ghi."
                    : "Interrupted: công việc còn dở; quyền cũ không được khôi phục và chưa tự chạy tiếp." } : copy;
        }
    }
    public IReadOnlyList<H2AgentTaskSummary> Recent(Guid? projectId, int limit)
    {
        lock (_gate) return _tasks.Values.Where(t => projectId is null || t.ProjectId == projectId)
            .OrderByDescending(t => t.UpdatedUtc).ThenBy(t => t.TaskId).Take(Math.Clamp(limit, 1, 5000))
            .Select(t => Get(t.TaskId)!).ToArray();
    }
    public H2AgentEvidence? GetEvidence(string id)
    {
        lock (_gate) return _tasks.Values.SelectMany(t => t.Evidence).FirstOrDefault(e => e.EvidenceId == id);
    }
    public IReadOnlyList<H2AgentThread> Threads()
    { lock (_gate) return _threads.Values.Select(Clone).ToArray(); }
    public void SaveThread(H2AgentThread thread)
    { lock (_gate) { ValidateThread(thread); Append(thread.ThreadId, "thread", thread); } }
    public void AppendProgress(Guid task, H2AgentProgress progress)
    {
        lock (_gate)
        {
            var prior = _progress.GetValueOrDefault(task)?.LastOrDefault().Progress;
            if (prior is not null && progress.Sequence == prior.Sequence && JsonSerializer.Serialize(prior) == JsonSerializer.Serialize(progress)) return;
            if (progress.Sequence != (prior?.Sequence + 1 ?? 0)) throw new InvalidDataException("progress-sequence");
            Append(task, "progress", progress);
        }
    }
    public IReadOnlyList<H2AgentProgress> ReadProgress(Guid task, long after)
    { lock (_gate) return (_progress.GetValueOrDefault(task) ?? []).Where(p => p.Progress.Sequence > after).Select(p => Clone(p.Progress)).ToArray(); }
    public void RecordOperation(Guid taskId, H2AgentOperationRecord item)
    {
        lock (_gate)
        {
            // The runtime's general result receipt must not erase native job ownership.
            if (item.Job is null && _operations.TryGetValue((taskId, item.InvocationId), out var previous))
                item = item with { Job = previous.Job };
            if (item.State == "Dispatched") Append(taskId, "operation-intent", item with { State = "Prepared", Status = "NotRun", Effect = "None" });
            Append(taskId, item.State == "Dispatched" ? "operation-dispatched" : "operation-result", item);
        }
    }
    internal H2AgentReconcileResult Reconcile(Guid taskId, IReadOnlyList<H2AgentReconcileObservation> observations)
    {
        lock (_gate)
        {
            EnsureWritable();
            if (taskId == Guid.Empty) throw new ArgumentException("TaskId cannot be empty.", nameof(taskId));
            if (!_tasks.TryGetValue(taskId, out var task)) throw new KeyNotFoundException("Agent task is not available.");
            observations ??= Array.Empty<H2AgentReconcileObservation>();
            if (observations.GroupBy(x => x.InvocationId).Any(g => g.Key == Guid.Empty || g.Count() > 1))
                throw new ArgumentException("Reconciliation observations require unique non-empty invocation IDs.", nameof(observations));

            // A prepared-only receipt is durable proof that BeforeExecute never returned, therefore the
            // executor never ran. Reconcile it to a terminal no-effect receipt without touching the resource.
            foreach (var item in _operations.Where(p => p.Key.TaskId == taskId).Select(p => p.Value).ToArray())
            {
                if (item.State == "Prepared")
                {
                    RecordOperation(taskId, item with
                    {
                        State = "Result",
                        Status = "RejectedBeforeEffect",
                        Effect = "None",
                        ReconciliationState = "NoEffect",
                        ReconciledObservedVersion = "not-dispatched",
                        ReconciledUtc = DateTime.UtcNow
                    });
                    continue;
                }

                // Current job policy is CancelOnHostExit. A persisted Running receipt after a new host
                // process starts is therefore known dead/interrupted, never proof the worker is still alive.
                if (item.Job is { Status: "Running", HostExitPolicy: "CancelOnHostExit" }
                    && string.IsNullOrWhiteSpace(item.ReconciliationState))
                {
                    RecordOperation(taskId, item with
                    {
                        State = "Result",
                        Status = "OutcomeUnknown",
                        Effect = "Unknown",
                        ReconciliationState = "NeedsUserWorkerDead",
                        ErrorCode = "worker_interrupted_on_host_exit",
                        ReconciledUtc = DateTime.UtcNow
                    });
                }
            }

            foreach (var observation in observations)
            {
                if (!Enum.IsDefined(observation.Disposition)
                    || observation.ObservedUtc.Kind != DateTimeKind.Utc
                    || observation.ObservedUtc > DateTime.UtcNow.AddMinutes(1))
                    throw new ArgumentException("Invalid restart reconciliation observation.", nameof(observations));
                if (!_operations.TryGetValue((taskId, observation.InvocationId), out var operation))
                    throw new KeyNotFoundException("Reconciliation operation is not available for this task.");

                var resourceMismatch = operation.ResourceKeySha256 is not null
                    && (string.IsNullOrWhiteSpace(observation.ResourceKey)
                        || Hash(System.Text.Encoding.UTF8.GetBytes(observation.ResourceKey.Trim())) != operation.ResourceKeySha256);
                var terminal = operation.ReconciliationState is "Verified" or "NoEffect";
                if (terminal)
                {
                    var expected = observation.Disposition == H2AgentReconcileDisposition.Verified ? "Verified"
                        : observation.Disposition == H2AgentReconcileDisposition.NoEffect ? "NoEffect" : null;
                    if (!resourceMismatch
                        && expected == operation.ReconciliationState
                        && string.Equals(operation.ReconciledObservedVersion, BoundObservation(observation.ObservedVersion, 512), StringComparison.Ordinal))
                        continue; // Idempotent duplicate host observation on the exact same resource.
                    throw new InvalidOperationException("A terminal reconciliation receipt cannot be rewritten or rebound.");
                }

                var disposition = observation.Disposition;
                if (resourceMismatch && (disposition is H2AgentReconcileDisposition.Verified
                    or H2AgentReconcileDisposition.AppliedUnverified
                    or H2AgentReconcileDisposition.NoEffect))
                    disposition = H2AgentReconcileDisposition.NeedsUser;

                if ((disposition is H2AgentReconcileDisposition.Verified
                    or H2AgentReconcileDisposition.AppliedUnverified
                    or H2AgentReconcileDisposition.NoEffect)
                    && string.IsNullOrWhiteSpace(observation.ObservedVersion))
                    throw new ArgumentException("A conclusive reconciliation requires an observed resource version.", nameof(observations));

                var reconciliationState = disposition switch
                {
                    H2AgentReconcileDisposition.NoEffect => "NoEffect",
                    H2AgentReconcileDisposition.AppliedUnverified => "AppliedUnverified",
                    H2AgentReconcileDisposition.Verified => "Verified",
                    H2AgentReconcileDisposition.RepairRequired => "RepairRequired",
                    _ => resourceMismatch ? "NeedsUserResourceMismatch" : "NeedsUser"
                };
                var status = disposition switch
                {
                    H2AgentReconcileDisposition.NoEffect => "RejectedBeforeEffect",
                    H2AgentReconcileDisposition.AppliedUnverified => "Succeeded",
                    H2AgentReconcileDisposition.Verified => "Succeeded",
                    H2AgentReconcileDisposition.RepairRequired => "PartiallyApplied",
                    _ => "OutcomeUnknown"
                };
                var effect = disposition switch
                {
                    H2AgentReconcileDisposition.NoEffect => "None",
                    H2AgentReconcileDisposition.AppliedUnverified => "Applied",
                    H2AgentReconcileDisposition.Verified => "Applied",
                    H2AgentReconcileDisposition.RepairRequired => "PartiallyApplied",
                    _ => "Unknown"
                };

                string? evidenceId = null;
                if (disposition is H2AgentReconcileDisposition.Verified
                    or H2AgentReconcileDisposition.NoEffect
                    or H2AgentReconcileDisposition.AppliedUnverified)
                    evidenceId = RecordReconciliationEvidence(taskId, operation, observation,
                        disposition == H2AgentReconcileDisposition.Verified);

                RecordOperation(taskId, operation with
                {
                    State = "Result",
                    Status = status,
                    Effect = effect,
                    ErrorCode = disposition == H2AgentReconcileDisposition.NeedsUser
                        ? resourceMismatch ? "resource_rebind_mismatch" : "needs_user"
                        : disposition == H2AgentReconcileDisposition.RepairRequired ? "repair_required" : null,
                    ReconciliationState = reconciliationState,
                    ReconciliationEvidenceId = evidenceId,
                    ReconciledObservedVersion = BoundObservation(observation.ObservedVersion, 512),
                    ReconciledUtc = observation.ObservedUtc
                });
            }

            var current = _operations.Where(p => p.Key.TaskId == taskId).Select(p => p.Value)
                .OrderBy(x => x.InvocationId).ToArray();
            var pending = current.Any(o => RequiresReconciliation(o, interruptedTask: true));
            var state = !pending ? "ReadyForResume"
                : current.Any(o => o.ReconciliationState?.StartsWith("NeedsUser", StringComparison.Ordinal) == true) ? "NeedsUser"
                : current.Any(o => o.ReconciliationState == "RepairRequired") ? "RepairRequired"
                : "ReconcileRequired";
            return new(taskId, state, pending, true, Array.AsReadOnly(current),
                pending
                    ? "Restart reconciliation is incomplete. Do not replay the mutation; reobserve the exact resource or ask the user."
                    : "All durable operations are reconciled. A later continuation still requires fresh permission and fresh resource binding.");
        }
    }

    private string RecordReconciliationEvidence(Guid taskId, H2AgentOperationRecord operation,
        H2AgentReconcileObservation observation, bool verified)
    {
        var version = BoundObservation(observation.ObservedVersion, 512) ?? "none";
        var evidenceId = "reconcile:" + taskId.ToString("N") + ":" + operation.InvocationId.ToString("N")
            + ":" + Hash(System.Text.Encoding.UTF8.GetBytes(observation.Disposition + "|" + version))[..16];
        var current = _tasks[taskId];
        if (current.Evidence.Any(e => e.EvidenceId == evidenceId)) return evidenceId;
        // Observation.Note is intentionally not persisted: it may contain transient document text.
        // The durable receipt keeps only typed disposition, operation identity and bounded version metadata.
        var summary = operation.ToolName + " restart observation: " + observation.Disposition;
        var evidence = new H2AgentEvidence(evidenceId, "reconciliation", null, summary,
            Provenance: "H2AgentLab.Integration.RestartReconcile",
            VerificationPassed: verified ? true : null);
        var next = current with
        {
            Evidence = current.Evidence.Concat([evidence]).ToArray(),
            UpdatedUtc = observation.ObservedUtc
        };
        Append(taskId, "task-state", next with { PendingApproval = null, Recovery = null });
        return evidenceId;
    }

    private static string? BoundObservation(string? value, int max)
    {
        value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (value.Length == 0) return null;
        return value.Length <= max ? value : value[..max];
    }

    private static bool RequiresReconciliation(H2AgentOperationRecord item, bool interruptedTask)
    {
        if (item.ReconciliationState is "Verified" or "NoEffect") return false;
        if (item.ReconciliationState is "AppliedUnverified" or "RepairRequired"
            || item.ReconciliationState?.StartsWith("NeedsUser", StringComparison.Ordinal) == true)
            return true;
        if (item.State == "Dispatched" || item.Status == "Running"
            || item.Effect is "Unknown" or "PartiallyApplied")
            return true;
        return interruptedTask && item.State == "Result"
            && item.Status == "Succeeded" && item.Effect == "Applied";
    }

    public void RecordJob(Guid taskId, Guid invocationId, H2AgentProcessJobInfo job)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue((taskId, invocationId), out var original)
                || original.State == "Prepared" || job.OwnerTaskId != taskId
                || original.GoalRevisionId != job.GoalRevisionId)
                throw new InvalidDataException("job-without-owned-dispatch");
            RecordOperation(taskId, original with { State = "Result", Job = job,
                Status = job.Status == "Running" ? "Running" : job.Status == "Succeeded" ? "Succeeded" : "OutcomeUnknown",
                Effect = job.Status == "Succeeded" ? "Applied" : "Unknown" });
        }
    }
    public void RecordVerification(Guid taskId, object value)
    { lock (_gate) Append(taskId, "verification", value); }
    internal IReadOnlyList<JournalEntry> ReadJournal(Guid streamId)
    {
        lock (_gate) return Directory.EnumerateFiles(_store, "event-*.json").Order(StringComparer.Ordinal)
            .Take((int)Math.Min(_sequence, int.MaxValue)).Select(p => Read<JournalEntry>(p))
            .Where(e => e.StreamId == streamId).ToArray();
    }

    private void Append<T>(Guid streamId, string kind, T payload)
    {
        EnsureWritable();
        if (streamId == Guid.Empty) throw new InvalidDataException("stream-identity");
        var data = JsonSerializer.SerializeToElement(payload);
        var record = new JournalEntry(Version, _storeId, Guid.NewGuid(), _sequence + 1, streamId,
            DateTime.UtcNow, kind, "Host", "H2AgentLab.Integration", data, Hash(JsonSerializer.SerializeToUtf8Bytes(data)), _head, "");
        record = record with { Sha256 = Hash(JsonSerializer.SerializeToUtf8Bytes(record)) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        if (bytes.Length > MaxRecordBytes) throw new InvalidDataException("record-size");
        if (_sequence >= _options.MaxEvents || _bytes + bytes.Length > _options.MaxJournalBytes)
        {
            RequireCapacity();
            throw new IOException("Agent archive quota reached; no journal or referenced evidence was deleted.");
        }
        ValidateEntry(record, _sequence + 1, _head);
        ValidateTransition(record);
        var path = Path.Combine(_store, $"event-{record.Sequence:D12}.json");
        try
        {
            WriteValidated(path, bytes, false, content => ValidateEntry(JsonSerializer.Deserialize<JournalEntry>(content)!, record.Sequence, record.PreviousHash), "journal");
            _fault?.Invoke("journal-activated"); // May terminate a deterministic test process before the index is changed.
            // A tiny durable high-water receipt detects deletion of an acknowledged suffix,
            // even between full checkpoint intervals. Source may be ahead after a crash, never behind.
            ProjectWorkspaceStore.AtomicWrite(Path.Combine(_store, "head.json"), JsonSerializer.SerializeToUtf8Bytes(
                new HeadReceipt(Version, _storeId, record.Sequence, record.Sha256)));
            Apply(record); _sequence = record.Sequence; _head = record.Sha256; _bytes += bytes.Length;
            if (_sequence >= _options.MaxEvents || _bytes >= _options.MaxJournalBytes) RequireCapacity();
            if (_sequence % _options.CheckpointEvery == 0) TryCheckpoint();
        }
        catch
        {
            // A write/ack failure is uncertain. Fence later writers until reload validates disk.
            _readOnly = true; Issue("journal-write-uncertain"); throw;
        }
    }

    private void RequireCapacity()
    { _capacityRequired = true; Issue("quota-reached-source-retained"); }

    private void LoadJournal()
    {
        var manifest = Read<StoreManifest>(Path.Combine(_store, "manifest.json"));
        if (manifest.Schema != Version || manifest.StoreId == Guid.Empty) throw new InvalidDataException("unsupported-store-schema");
        _storeId = manifest.StoreId;
        var entries = Directory.EnumerateFiles(_store, "event-*.json").Order(StringComparer.Ordinal).Take(_options.MaxEvents + 1).ToArray();
        foreach (var path in entries)
        {
            try
            {
                if (_sequence >= _options.MaxEvents || _bytes + new FileInfo(path).Length > _options.MaxJournalBytes)
                    throw new InvalidDataException("journal-quota");
                var record = Read<JournalEntry>(path);
                if (Path.GetFileName(path) != $"event-{_sequence + 1:D12}.json") throw new InvalidDataException("journal-gap");
                ValidateEntry(record, _sequence + 1, _head);
                ValidateTransition(record);
                Apply(record); _sequence = record.Sequence; _head = record.Sha256; _bytes += new FileInfo(path).Length;
            }
            catch (Exception ex) when (StorageFailure(ex))
            {
                PreserveCorrupt(path); _readOnly = true; Issue("journal-prefix-only:" + SafeCode(ex)); break;
            }
        }
        foreach (var path in Directory.EnumerateFiles(_store, "*.tmp").Take(32))
        { PreserveCorrupt(path); Issue("unactivated-write-retained"); }
    }
    private void ValidateEntry(JournalEntry e, long sequence, string previous)
    {
        if (e is null || e.Schema != Version || e.StoreId != _storeId || e.EventId == Guid.Empty
            || e.StreamId == Guid.Empty || e.Sequence != sequence || e.PreviousHash != previous
            || e.Utc.Kind != DateTimeKind.Utc || e.Actor != "Host" || e.Provenance != "H2AgentLab.Integration"
            || e.PayloadHash != Hash(JsonSerializer.SerializeToUtf8Bytes(e.Payload))
            || e.Sha256 != Hash(JsonSerializer.SerializeToUtf8Bytes(e with { Sha256 = "" })))
            throw new InvalidDataException("journal-integrity");
        switch (e.Kind)
        {
            case "task-state": case "revision":
                var task = e.Payload.Deserialize<H2AgentTaskSummary>()!; ValidateSummary(task);
                if (task.TaskId != e.StreamId) throw new InvalidDataException("task-identity"); break;
            case "thread":
                var thread = e.Payload.Deserialize<H2AgentThread>()!; ValidateThread(thread);
                if (thread.ThreadId != e.StreamId) throw new InvalidDataException("thread-identity"); break;
            case "progress":
                var progress = e.Payload.Deserialize<H2AgentProgress>()!;
                if (progress is null || progress.Sequence < 0 || progress.AtUtc.Kind != DateTimeKind.Utc) throw new InvalidDataException("progress-shape"); break;
            case "operation-intent": case "operation-dispatched": case "operation-result":
                var operation = e.Payload.Deserialize<H2AgentOperationRecord>()!;
                if (operation is null || operation.InvocationId == Guid.Empty || string.IsNullOrWhiteSpace(operation.LogicalOperationId)
                    || operation.ArgumentsSha256 is not { Length: 64 } || string.IsNullOrWhiteSpace(operation.GoalRevisionId)
                    || operation.State != (e.Kind == "operation-intent" ? "Prepared" : e.Kind == "operation-dispatched" ? "Dispatched" : "Result"))
                    throw new InvalidDataException("operation-shape");
                if (operation.Job is { } job && (job.OwnerTaskId != e.StreamId
                    || job.GoalRevisionId != operation.GoalRevisionId || job.ProcessId <= 0
                    || job.JobId is not { Length: > 0 and <= 128 } || job.JobId.Any(char.IsControl)
                    || job.ProcessStartedUtc.Kind != DateTimeKind.Utc || job.DeadlineUtc.Kind != DateTimeKind.Utc
                    || job.HostExitPolicy != "CancelOnHostExit" || job.OutputArtifacts is null || job.OutputArtifacts.Count > 2
                    || job.OutputArtifacts.Any(id => id is not { Length: > 0 and <= 128 } || id.Any(char.IsControl))
                    || job.Status == "Succeeded" && (!job.RootExited || !job.AllProcessesExited || !job.StreamsDrained || job.ExitCode != 0)))
                    throw new InvalidDataException("job-shape");
                break;
            case "context-source": ValidateContextSourceRecord(e); break;
            case "context-compaction": ValidateContextRecord(e); break;
            case "verification": if (e.Payload.ValueKind != JsonValueKind.Object) throw new InvalidDataException("verification-shape"); break;
            default: throw new InvalidDataException("unsupported-event-kind");
        }
    }
    private void ValidateTransition(JournalEntry e)
    {
        if (_eventIds.Contains(e.EventId)) throw new InvalidDataException("journal-event-identity");
        if (e.Kind == "context-compaction") ValidateContextTransition(e);
        if (e.Kind == "context-source") ValidateContextSourceTransition(e);
        if (e.Kind == "progress")
        {
            var item = e.Payload.Deserialize<H2AgentProgress>()!;
            var prior = _progress.GetValueOrDefault(e.StreamId)?.LastOrDefault().Progress;
            if (item.Sequence != (prior?.Sequence + 1 ?? 0)) throw new InvalidDataException("progress-gap-or-duplicate");
        }
        if (e.Kind.StartsWith("operation-", StringComparison.Ordinal))
        {
            var operation = e.Payload.Deserialize<H2AgentOperationRecord>()!;
            var prior = _operations.GetValueOrDefault((e.StreamId, operation.InvocationId));
            if (e.Kind == "operation-intent" && prior is not null) throw new InvalidDataException("duplicate-intent");
            if (e.Kind == "operation-dispatched" && prior?.State != "Prepared") throw new InvalidDataException("missing-intent");
            if (e.Kind == "operation-result" && prior is null && operation.Effect != "None") throw new InvalidDataException("missing-dispatch");
            if (prior is not null && (operation.LogicalOperationId != prior.LogicalOperationId || operation.ArgumentsSha256 != prior.ArgumentsSha256
                || operation.GoalRevisionId != prior.GoalRevisionId || operation.ToolName != prior.ToolName || operation.TurnId != prior.TurnId
                || operation.ResourceKeySha256 != prior.ResourceKeySha256 || operation.ToolCallId != prior.ToolCallId))
                throw new InvalidDataException("operation-identity-changed");
            if (prior?.Job is { } previousJob && (operation.Job is not { } currentJob
                || previousJob.JobId != currentJob.JobId || previousJob.OwnerTaskId != currentJob.OwnerTaskId
                || previousJob.GoalRevisionId != currentJob.GoalRevisionId || previousJob.ProcessId != currentJob.ProcessId
                || previousJob.ProcessStartedUtc != currentJob.ProcessStartedUtc || previousJob.DeadlineUtc != currentJob.DeadlineUtc))
                throw new InvalidDataException("job-identity-changed");
        }
    }
    private void Apply(JournalEntry e)
    {
        _eventIds.Add(e.EventId);
        switch (e.Kind)
        {
            case "task-state": case "revision": _tasks[e.StreamId] = e.Payload.Deserialize<H2AgentTaskSummary>()!; _taskHeads[e.StreamId] = e.Sequence; break;
            case "thread": _threads[e.StreamId] = e.Payload.Deserialize<H2AgentThread>()!; _threadHeads[e.StreamId] = e.Sequence; break;
            case "progress":
                var item = e.Payload.Deserialize<H2AgentProgress>()!;
                if (!_progress.TryGetValue(e.StreamId, out var list)) _progress[e.StreamId] = list = [];
                if (item.Sequence != (list.LastOrDefault().Progress?.Sequence + 1 ?? 0)) throw new InvalidDataException("progress-gap-or-duplicate");
                list.Add((e.Sequence, item)); break;
            case "operation-intent": case "operation-dispatched": case "operation-result":
                var operation = e.Payload.Deserialize<H2AgentOperationRecord>()!;
                var key = (e.StreamId, operation.InvocationId);
                if (e.Kind == "operation-intent" && _operations.ContainsKey(key)) throw new InvalidDataException("duplicate-intent");
                if (e.Kind == "operation-dispatched" && (!_operations.TryGetValue(key, out var intent) || intent.State != "Prepared")) throw new InvalidDataException("missing-intent");
                _operations[key] = operation; break;
        }
        if (e.Kind == "context-source")
        {
            var source = e.Payload.Deserialize<H2AgentLab.Session.AgentContextSourceRecord>()!;
            _contextSources[(e.StreamId, source.TurnId, source.Cycle)] = source;
        }
        if (e.Kind == "context-compaction")
        {
            var context = e.Payload.Deserialize<H2AgentLab.Session.AgentContextCompactionRecord>()!;
            _contextHeads[(e.StreamId, context.TurnId)] = context;
        }
        IndexHistory(e);
    }

    private IndexCheckpoint CurrentIndex()
        => new(Version, _storeId, _sequence, _head,
            _tasks.Values.OrderByDescending(t => t.UpdatedUtc).ThenBy(t => t.TaskId).Take(_options.RecentLimit)
                .Select(t => new IndexEntry(t.TaskId, _taskHeads[t.TaskId])).ToArray(),
            _taskHeads.OrderBy(p => p.Key).Select(p => new IndexEntry(p.Key, p.Value)).ToArray(),
            _threadHeads.OrderBy(p => p.Key).Select(p => new IndexEntry(p.Key, p.Value)).ToArray());
    private void ValidateHeadReceipt()
    {
        var path = Path.Combine(_store, "head.json");
        if (!File.Exists(path)) { if (_sequence != 0) Issue("missing-head-receipt-rebuilt"); }
        else
        {
            var receipt = Read<HeadReceipt>(path);
            if (receipt.Schema != Version || receipt.StoreId != _storeId || receipt.Sequence < 0 || receipt.Sequence > _sequence)
                throw new InvalidDataException("journal-high-water-mismatch");
            var expected = receipt.Sequence == 0 ? Zero : Read<JournalEntry>(Path.Combine(_store, $"event-{receipt.Sequence:D12}.json")).Sha256;
            if (receipt.HeadHash != expected) throw new InvalidDataException("journal-head-hash-mismatch");
            if (receipt.Sequence == _sequence) return;
            Issue("head-receipt-recovered-from-durable-journal");
        }
        ProjectWorkspaceStore.AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(new HeadReceipt(Version, _storeId, _sequence, _head)));
    }
    private void ValidateOrRebuildCheckpoint()
    {
        var path = Path.Combine(_store, "checkpoint.json");
        try
        {
            var checkpoint = Read<CheckpointEnvelope>(path);
            if (checkpoint.Index is null) throw new InvalidDataException("checkpoint-shape");
            if (checkpoint.Index.Schema > Version) throw new InvalidDataException("future-checkpoint-schema");
            if (checkpoint.Index.Sequence > _sequence) throw new InvalidDataException("journal-tail-missing");
            if (checkpoint.Hash != Hash(JsonSerializer.SerializeToUtf8Bytes(checkpoint.Index))) throw new InvalidDataException("checkpoint-integrity");
            // An index must agree with VERIFIED journal replay, not just be self-hashed.
            if (JsonSerializer.Serialize(checkpoint.Index) == JsonSerializer.Serialize(CurrentIndex())) return;
            Issue("checkpoint-stale-rebuilt");
        }
        catch (Exception ex) when (StorageFailure(ex))
        {
            if (ex is InvalidDataException && ex.Message is "future-checkpoint-schema" or "journal-tail-missing") throw;
            if (File.Exists(path)) PreserveCorrupt(path);
            Issue("checkpoint-missing-or-corrupt-rebuilt");
        }
        TryCheckpoint();
    }
    private void TryCheckpoint()
    {
        try
        {
            var index = CurrentIndex(); var envelope = new CheckpointEnvelope(index, Hash(JsonSerializer.SerializeToUtf8Bytes(index)));
            WriteValidated(Path.Combine(_store, "checkpoint.json"), JsonSerializer.SerializeToUtf8Bytes(envelope), true,
                data => { var test = JsonSerializer.Deserialize<CheckpointEnvelope>(data)!;
                    if (test.Hash != envelope.Hash || JsonSerializer.Serialize(test.Index) != JsonSerializer.Serialize(index)) throw new InvalidDataException("checkpoint-readback"); }, "checkpoint");
        }
        catch (Exception ex) when (StorageFailure(ex)) { Issue("checkpoint-write-failed-journal-retained"); }
    }

    private void ImportLegacy()
    {
        var legacy = new List<(Guid Id, string Kind, object Payload)>();
        var sources = new List<LegacySource>();
        long sourceBytes = 0;
        byte[] SourceBytes(string path)
        {
            // Bound total legacy material before reading/retaining another source. A huge
            // recent cache cannot bypass the import budget merely because task files exist.
            if (new FileInfo(path).Length > _options.MaxJournalBytes - sourceBytes)
                throw new InvalidDataException("migration-quota");
            var bytes = ReadBytes(path);
            sourceBytes += bytes.Length;
            sources.Add(new(Path.GetRelativePath(_root, path), Hash(bytes)));
            return bytes;
        }
        T Source<T>(string path) => JsonSerializer.Deserialize<T>(SourceBytes(path)) ?? throw new InvalidDataException("legacy-empty");
        if (Directory.EnumerateDirectories(_root, "migration-*").Any()) Issue("abandoned-migration-retained");
        var summaries = new Dictionary<Guid, H2AgentTaskSummary>();
        var records = Path.Combine(_root, "task-records");
        var legacyIndex = Path.Combine(_root, "recent-tasks-v1.json");
        if (Directory.Exists(records))
            foreach (var path in Directory.EnumerateFiles(records, "*.json").Order(StringComparer.Ordinal).Take(_options.MaxEvents + 1))
            {
                try
                {
                    var task = Source<H2AgentTaskSummary>(path); ValidateSummary(task);
                    if (Path.GetFileNameWithoutExtension(path) != task.TaskId.ToString("N")) throw new InvalidDataException("legacy-task-identity");
                    if (summaries.Count >= _options.MaxEvents) throw new InvalidDataException("migration-quota");
                    summaries.Add(task.TaskId, task);
                }
                catch (Exception ex) when (StorageFailure(ex)) { PreserveCorrupt(path); throw new InvalidDataException("legacy-task-invalid", ex); }
            }
        if (File.Exists(legacyIndex))
        {
            try
            {
                var index = Source<LegacyIndex>(legacyIndex);
                if (index.Schema != 1 || index.Tasks is null) throw new InvalidDataException("unsupported-legacy-schema");
                foreach (var task in index.Tasks)
                {
                    ValidateSummary(task);
                    if (!summaries.ContainsKey(task.TaskId) && summaries.Count >= _options.MaxEvents)
                        throw new InvalidDataException("migration-quota");
                    summaries.TryAdd(task.TaskId, task);
                }
            }
            catch (Exception ex) when (StorageFailure(ex))
            {
                PreserveCorrupt(legacyIndex);
                if (ex is InvalidDataException && ex.Message is "unsupported-legacy-schema" or "migration-quota" || summaries.Count == 0) throw;
                Issue("legacy-index-rebuilt-from-task-records");
            }
        }
        foreach (var task in summaries.Values.OrderBy(t => t.TaskId)) legacy.Add((task.TaskId, "task-state", task with { PendingApproval = null, Recovery = null }));
        var threads = Path.Combine(_root, "threads");
        if (Directory.Exists(threads))
            foreach (var path in Directory.EnumerateFiles(threads, "*.json").Order(StringComparer.Ordinal).Take(_options.MaxEvents + 1))
            {
                try
                {
                    var thread = Source<H2AgentThread>(path); ValidateThread(thread);
                    if (Path.GetFileNameWithoutExtension(path) != thread.ThreadId.ToString("N")) throw new InvalidDataException("legacy-thread-identity");
                    if (legacy.Count >= _options.MaxEvents) throw new InvalidDataException("migration-quota");
                    legacy.Add((thread.ThreadId, "thread", thread));
                }
                catch (Exception ex) when (StorageFailure(ex)) { PreserveCorrupt(path); throw new InvalidDataException("legacy-thread-invalid", ex); }
            }
        if (Directory.Exists(records))
            foreach (var path in Directory.EnumerateFiles(records, "*.jsonl").Order(StringComparer.Ordinal).Take(_options.MaxEvents + 1))
            {
                try
                {
                    if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)) throw new InvalidDataException("legacy-progress-identity");
                    var bytes = SourceBytes(path); var text = new System.Text.UTF8Encoding(false, true).GetString(bytes);
                    long sequence = 0;
                    foreach (var line in text.Split('\n').Where(s => !string.IsNullOrWhiteSpace(s)))
                    {
                        var progress = JsonSerializer.Deserialize<H2AgentProgress>(line)!;
                        if (progress is null || progress.Sequence != sequence++) throw new InvalidDataException("legacy-progress-gap");
                        legacy.Add((id, "progress", progress));
                        if (legacy.Count > _options.MaxEvents) throw new InvalidDataException("migration-quota");
                    }
                }
                catch (Exception ex) when (StorageFailure(ex)) { PreserveCorrupt(path); throw new InvalidDataException("legacy-progress-invalid", ex); }
            }
        if (legacy.Count > _options.MaxEvents) throw new InvalidDataException("migration-quota");
        var target = _store; _store = Path.Combine(_root, "migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_store); _storeId = Guid.NewGuid();
        try
        {
            // The legacy bytes are NEVER changed. A crash before activation leaves only a
            // staging directory; no partial migration is advertised as the new archive.
            foreach (var item in legacy) Append(item.Id, item.Kind, item.Payload);
            foreach (var source in sources)
                if (Hash(ReadBytes(Path.Combine(_root, source.Path))) != source.Sha256) throw new InvalidDataException("legacy-source-changed");
            var manifest = new StoreManifest(Version, _storeId, DateTime.UtcNow, "legacy-read-only-or-new", sources.ToArray());
            ProjectWorkspaceStore.AtomicWrite(Path.Combine(_store, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest));
            TryCheckpoint(); _fault?.Invoke("migration-before-activate");
            Directory.Move(_store, target); _store = target;
            ProjectWorkspaceStore.AtomicWrite(FormatPath, JsonSerializer.SerializeToUtf8Bytes(manifest));
            if (legacy.Count > 0) Issue("legacy-migrated-sources-retained");
        }
        catch { _store = target; throw; }
    }
    private void ValidateRootMarker()
    {
        if (!File.Exists(FormatPath))
        {
            ProjectWorkspaceStore.AtomicWrite(FormatPath, JsonSerializer.SerializeToUtf8Bytes(new StoreManifest(Version, _storeId, DateTime.UtcNow, "recovered-marker")));
            Issue("format-marker-recovered-from-journal"); return;
        }
        var marker = Read<StoreManifest>(FormatPath);
        if (marker.Schema != Version || marker.StoreId != _storeId) throw new InvalidDataException("format-marker-mismatch");
    }
    private void WriteValidated(string path, byte[] bytes, bool replace, Action<byte[]> validate, string boundary)
    {
        ValidateLocalRoot(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        // Same local primitive as ProjectWorkspaceStore.AtomicWrite, with pre-activation
        // disk readback and create-only journal records. No NAS atomicity is implied.
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        validate(ReadBytes(temp)); _fault?.Invoke(boundary + "-before-activate");
        File.Move(temp, path, replace);
    }
    private T Read<T>(string path) => JsonSerializer.Deserialize<T>(ReadBytes(path)) ?? throw new InvalidDataException("empty-json");
    private static byte[] ReadBytes(string path)
    {
        ValidateLocalRoot(Path.GetDirectoryName(path)!);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("reparse-source");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaxRecordBytes) throw new InvalidDataException("source-size");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
    private void PreserveCorrupt(string path)
    {
        try
        {
            var bytes = ReadBytes(path); var quarantine = Path.Combine(_root, "quarantine"); Directory.CreateDirectory(quarantine);
            var target = Path.Combine(quarantine, Hash(bytes) + ".bin");
            if (!File.Exists(target)) ProjectWorkspaceStore.AtomicWrite(target, bytes);
            Issue("quarantine-copy:" + Path.GetFileName(target));
        }
        catch (Exception ex) when (StorageFailure(ex)) { Issue("source-retained-quarantine-unavailable"); }
    }
    private void Issue(string code) { if (!_diagnostics.Contains(code) && _diagnostics.Count < 32) _diagnostics.Add(code); }
    private static bool StorageFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException;
    private static string SafeCode(Exception ex) => ex is InvalidDataException && ex.Message.Length < 80 && ex.Message.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? ex.Message : ex.GetType().Name;
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value))!;
    private static bool IsTerminal(H2AgentTaskStatus s) => s is H2AgentTaskStatus.Completed or H2AgentTaskStatus.Blocked or H2AgentTaskStatus.Cancelled or H2AgentTaskStatus.Failed;
    private static void ValidateSummary(H2AgentTaskSummary t)
    {
        if (t is null || t.TaskId == Guid.Empty || string.IsNullOrWhiteSpace(t.Goal) || t.Goal.Length > 8000
            || t.Evidence is null || !Enum.IsDefined(t.Status) || t.CreatedUtc.Kind != DateTimeKind.Utc || t.UpdatedUtc.Kind != DateTimeKind.Utc)
            throw new InvalidDataException("task-shape");
    }
    private static void ValidateThread(H2AgentThread t)
    { if (t is null || t.ThreadId == Guid.Empty || t.Title is null || t.Title.Length > 120) throw new InvalidDataException("thread-shape"); }
    private static string ValidateLocalRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root); var full = Path.GetFullPath(root);
        if (full.StartsWith("\\\\", StringComparison.Ordinal) || full.StartsWith("//", StringComparison.Ordinal)
            || new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network) throw new IOException("Agent journal requires local storage, not SMB/WebDAV.");
        for (var cursor = new DirectoryInfo(full); cursor is not null; cursor = cursor.Parent)
            if (cursor.Exists && (cursor.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Agent journal rejects reparse aliases.");
        return full;
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { if (!_readOnly) TryCheckpoint(); }
            finally { _disposed = true; _lease?.Dispose(); _lease = null; }
        }
    }
    internal sealed record JournalEntry(int Schema, Guid StoreId, Guid EventId, long Sequence, Guid StreamId,
        DateTime Utc, string Kind, string Actor, string Provenance, JsonElement Payload, string PayloadHash, string PreviousHash, string Sha256);
    private sealed record LegacySource(string Path, string Sha256);
    private sealed record StoreManifest(int Schema, Guid StoreId, DateTime CreatedUtc, string Origin, LegacySource[]? Sources = null);
    private sealed record HeadReceipt(int Schema, Guid StoreId, long Sequence, string HeadHash);
    private sealed record IndexEntry(Guid Id, long Sequence);
    private sealed record IndexCheckpoint(int Schema, Guid StoreId, long Sequence, string HeadHash,
        IndexEntry[] Recent, IndexEntry[] Tasks, IndexEntry[] Threads);
    private sealed record CheckpointEnvelope(IndexCheckpoint Index, string Hash);
    private sealed class LegacyIndex { public int Schema { get; set; } public List<H2AgentTaskSummary>? Tasks { get; set; } }
}

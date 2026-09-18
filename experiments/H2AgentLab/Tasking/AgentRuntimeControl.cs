using System.Text.Json;
using H2AgentLab.Context;

namespace H2AgentLab.Tasking;

public sealed class AgentCancellationCoordinator : IDisposable
{
    private readonly CancellationTokenSource _root = new();
    private readonly List<Action> _helperAbort = [];
    private bool _disposed;

    public CancellationToken Token => _root.Token;
    public bool IsCancellationRequested => _root.IsCancellationRequested;

    public CancellationTokenSource CreateLinked(CancellationToken external = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return external.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(_root.Token, external)
            : CancellationTokenSource.CreateLinkedTokenSource(_root.Token);
    }

    public IDisposable RegisterHelperAbort(Action abort)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(abort);
        lock (_helperAbort) _helperAbort.Add(abort);
        return new AbortRegistration(this, abort);
    }

    public void Cancel()
    {
        if (_disposed) return;
        _root.Cancel();
        Action[] callbacks;
        lock (_helperAbort) callbacks = _helperAbort.ToArray();
        foreach (var callback in callbacks)
        {
            try { callback(); } catch { }
        }
    }

    private void Remove(Action abort)
    {
        lock (_helperAbort) _helperAbort.Remove(abort);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Cancel();
        _disposed = true;
        _root.Dispose();
    }

    private sealed class AbortRegistration : IDisposable
    {
        private AgentCancellationCoordinator? _owner;
        private readonly Action _abort;

        public AbortRegistration(AgentCancellationCoordinator owner, Action abort)
        {
            _owner = owner;
            _abort = abort;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Remove(_abort);
        }
    }
}

public enum DurableMutationState
{
    None = 0,
    Planned = 1,
    StartedUncertain = 2,
    ObservedAfterMutation = 3,
    Verified = 4
}

public sealed record DurableAgentTaskState(
    Guid TaskId,
    AgentTaskState State,
    string UserGoal,
    string Scope,
    DurableMutationState MutationState,
    string? MutationResourceId,
    string? LastObservedStateId,
    IReadOnlyList<string> CriterionIds,
    DateTime UpdatedUtc);

public enum AgentResumeAction
{
    ContinueReadOnly = 0,
    ReobserveBeforeContinuing = 1,
    VerifyObservedMutation = 2,
    Terminal = 3
}

public sealed record AgentResumePlan(
    AgentResumeAction Action,
    string Reason,
    bool MayRepeatMutation);

public sealed class DurableAgentTaskStore
{
    private readonly string _root;

    public DurableAgentTaskStore(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        _root = Path.Combine(Path.GetFullPath(stateRoot), "v2-tasks");
        Directory.CreateDirectory(_root);
    }

    public void Save(DurableAgentTaskState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.TaskId == Guid.Empty) throw new ArgumentException("Task ID is empty.", nameof(state));
        var path = Path.Combine(_root, state.TaskId.ToString("N") + ".json");
        WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(state));
    }

    public DurableAgentTaskState Load(Guid taskId)
    {
        if (taskId == Guid.Empty) throw new ArgumentException("Task ID is empty.", nameof(taskId));
        var path = Path.Combine(_root, taskId.ToString("N") + ".json");
        if (!File.Exists(path)) throw new FileNotFoundException("Durable v2 task was not found.", taskId.ToString());
        if (new FileInfo(path).Length > 1_000_000) throw new IOException("Durable v2 task exceeds safety limit.");
        var state = JsonSerializer.Deserialize<DurableAgentTaskState>(File.ReadAllBytes(path))
            ?? throw new IOException("Durable v2 task JSON is invalid.");
        if (state.TaskId != taskId) throw new IOException("Durable v2 task identity mismatch.");
        return state;
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}

public static class AgentResumePlanner
{
    public static AgentResumePlan Plan(DurableAgentTaskState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (AgentTaskStateMachine.IsTerminalState(state.State))
            return new(AgentResumeAction.Terminal, "Task is already terminal.", false);

        return state.MutationState switch
        {
            DurableMutationState.StartedUncertain => new(
                AgentResumeAction.ReobserveBeforeContinuing,
                "A mutation may have happened before interruption; observe current resource state before any further mutation.",
                false),
            DurableMutationState.ObservedAfterMutation => new(
                AgentResumeAction.VerifyObservedMutation,
                "Post-mutation state is observed; run deterministic verification before additional changes.",
                false),
            DurableMutationState.Verified => new(
                AgentResumeAction.ContinueReadOnly,
                "Last mutation is verified; continue from durable state.",
                false),
            DurableMutationState.Planned => new(
                AgentResumeAction.ReobserveBeforeContinuing,
                "Mutation was planned but process boundary was crossed; refresh resource state before execution.",
                false),
            _ => new(
                AgentResumeAction.ContinueReadOnly,
                "No uncertain mutation is recorded.",
                false)
        };
    }
}

public sealed record AgentDiagnosticsSnapshot(
    int ActiveContextCharacters,
    long CandidateContextCharacters,
    bool RequiresCompaction,
    IReadOnlyList<string> CompactionReasons,
    int RecentTurnsSelected,
    int RecentTurnsDropped,
    int ToolSummariesSelected,
    int ToolSummariesDropped,
    bool TaskContractTruncated,
    bool CurrentStateTruncated,
    bool CompactedHistoryTruncated);

public static class AgentDiagnostics
{
    public static AgentDiagnosticsSnapshot FromContext(AgentContextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new AgentDiagnosticsSnapshot(
            snapshot.Usage.TotalCharacters,
            snapshot.Pressure.CandidateCharacters,
            snapshot.Pressure.RequiresCompaction,
            snapshot.Pressure.Reasons.ToArray(),
            snapshot.Usage.SelectedRecentTurns,
            snapshot.Usage.DroppedRecentTurns,
            snapshot.Usage.SelectedToolSummaries,
            snapshot.Usage.DroppedToolSummaries,
            snapshot.Usage.TaskContractTruncated,
            snapshot.Usage.CurrentStateTruncated,
            snapshot.Usage.CompactedHistoryTruncated);
    }
}

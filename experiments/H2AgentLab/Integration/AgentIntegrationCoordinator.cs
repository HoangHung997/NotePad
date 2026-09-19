namespace H2AgentLab.Integration;

internal enum AgentIntegrationExecutionDisposition
{
    Completed = 0,
    Blocked = 1
}

internal sealed record AgentIntegrationExecutionResult(
    AgentIntegrationExecutionDisposition Disposition,
    string? FinalText = null,
    string? Error = null);

internal interface IAgentIntegrationExecutor
{
    Task<AgentIntegrationExecutionResult> ExecuteAsync(
        AgentIntegrationExecutionContext context,
        CancellationToken cancellationToken);
}

internal sealed class AgentIntegrationExecutionContext
{
    private readonly Action<string, string, string> _progress;
    private readonly Action<AgentIntegrationEvidence> _evidence;
    private readonly Func<string, string, CancellationToken, Task<bool>> _approval;

    internal AgentIntegrationExecutionContext(
        Guid taskId,
        AgentIntegrationTaskRequest request,
        AgentIntegrationProjectContext project,
        Action<string, string, string> progress,
        Action<AgentIntegrationEvidence> evidence,
        Func<string, string, CancellationToken, Task<bool>> approval)
    {
        TaskId = taskId;
        Request = request;
        Project = project;
        _progress = progress;
        _evidence = evidence;
        _approval = approval;
    }

    public Guid TaskId { get; }
    public AgentIntegrationTaskRequest Request { get; }
    public AgentIntegrationProjectContext Project { get; }

    public void ReportProgress(
        string kind,
        string code,
        string message)
        => _progress(kind, code, message);

    public void AddEvidence(AgentIntegrationEvidence evidence)
        => _evidence(evidence ?? throw new ArgumentNullException(nameof(evidence)));

    public Task<bool> RequestApprovalAsync(
        string title,
        string details,
        CancellationToken cancellationToken)
        => _approval(title, details, cancellationToken);
}

/// <summary>
/// Internal lifecycle coordinator for the frozen public integration contract. Phase 13 may bind an
/// AgentRuntime-backed executor behind this coordinator, while H2 Notes continues to depend only on
/// IAgentIntegrationBoundary and its DTOs.
/// </summary>
internal sealed class AgentIntegrationCoordinator :
    IAgentIntegrationBoundary,
    IDisposable
{
    private readonly IAgentIntegrationExecutor _executor;
    private readonly object _contextsGate = new();
    private readonly Dictionary<string, AgentIntegrationProjectContext> _contexts =
        new(StringComparer.Ordinal);
    private readonly object _tasksGate = new();
    private readonly Dictionary<Guid, TaskState> _tasks = [];
    private bool _disposed;

    public AgentIntegrationCoordinator(IAgentIntegrationExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public void LoadProjectContext(AgentIntegrationProjectContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(context);

        lock (_contextsGate)
        {
            if (_contexts.TryGetValue(context.ProjectId, out var existing))
            {
                if (context.Version < existing.Version)
                    throw new InvalidOperationException(
                        "Project context version cannot move backwards.");
                if (context.Version == existing.Version && context != existing)
                    throw new InvalidOperationException(
                        "A project context version cannot be silently redefined.");
            }

            _contexts[context.ProjectId] = context;
        }
    }

    public Task<Guid> StartTaskAsync(
        AgentIntegrationTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        AgentIntegrationProjectContext project;
        lock (_contextsGate)
        {
            if (!_contexts.TryGetValue(request.ProjectId, out project!))
                throw new KeyNotFoundException(
                    "Load project context before starting an Agent task.");
        }

        var state = new TaskState(
            Guid.NewGuid(),
            request,
            project);

        lock (_tasksGate)
            _tasks.Add(state.TaskId, state);

        _ = ExecuteAsync(state);
        return Task.FromResult(state.TaskId);
    }

    public IReadOnlyList<AgentIntegrationProgress> ObserveProgress(
        Guid taskId,
        long afterSequence = -1)
    {
        if (afterSequence < -1)
            throw new ArgumentOutOfRangeException(nameof(afterSequence));

        var state = State(taskId);
        lock (state.Gate)
            return state.Progress
                .Where(x => x.Sequence > afterSequence)
                .ToArray();
    }

    public AgentIntegrationTaskSnapshot InspectTask(Guid taskId)
    {
        var state = State(taskId);
        lock (state.Gate)
        {
            return new AgentIntegrationTaskSnapshot(
                state.TaskId,
                state.Request.ProjectId,
                state.Request.Goal,
                state.Status,
                state.PendingApproval,
                state.Evidence.ToArray(),
                state.FinalResult,
                state.CreatedUtc,
                state.UpdatedUtc);
        }
    }

    public void Cancel(Guid taskId)
    {
        var state = State(taskId);
        var shouldCancel = false;
        lock (state.Gate)
        {
            if (!IsTerminal(state.Status))
            {
                AddProgressLocked(
                    state,
                    "lifecycle",
                    "cancel-requested",
                    "Cancellation requested by H2 host.");
                shouldCancel = true;
            }
        }

        if (shouldCancel)
            state.Cancellation.Cancel();
    }

    public bool ProvideApproval(
        Guid taskId,
        Guid approvalId,
        bool approved)
    {
        if (approvalId == Guid.Empty)
            return false;

        var state = State(taskId);
        TaskCompletionSource<bool>? completion;
        lock (state.Gate)
        {
            if (IsTerminal(state.Status)
                || state.PendingApproval is null
                || state.PendingApproval.ApprovalId != approvalId
                || state.ApprovalCompletion is null)
                return false;

            completion = state.ApprovalCompletion;
            state.PendingApproval = null;
            state.ApprovalCompletion = null;
            state.Status = AgentIntegrationTaskStatus.Running;
            state.UpdatedUtc = DateTime.UtcNow;
            AddProgressLocked(
                state,
                "approval",
                approved ? "approval-granted" : "approval-denied",
                approved
                    ? "Host granted the pending approval."
                    : "Host denied the pending approval.");
        }

        return completion.TrySetResult(approved);
    }

    public async Task<AgentIntegrationFinalResult> WaitForFinalResultAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        var state = State(taskId);
        return await state.FinalCompletion.Task
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ExecuteAsync(TaskState state)
    {
        lock (state.Gate)
        {
            state.Status = AgentIntegrationTaskStatus.Running;
            state.UpdatedUtc = DateTime.UtcNow;
            AddProgressLocked(
                state,
                "lifecycle",
                "started",
                "Agent task execution started.");
        }

        var context = new AgentIntegrationExecutionContext(
            state.TaskId,
            state.Request,
            state.Project,
            (kind, code, message) => AddProgress(state, kind, code, message),
            evidence => AddEvidence(state, evidence),
            (title, details, ct) => RequestApprovalAsync(
                state,
                title,
                details,
                ct));

        try
        {
            var result = await _executor.ExecuteAsync(
                context,
                state.Cancellation.Token).ConfigureAwait(false);

            var status = result.Disposition
                == AgentIntegrationExecutionDisposition.Completed
                    ? AgentIntegrationTaskStatus.Completed
                    : AgentIntegrationTaskStatus.Blocked;
            Complete(state, status, result.FinalText, result.Error);
        }
        catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
        {
            Complete(
                state,
                AgentIntegrationTaskStatus.Cancelled,
                finalText: null,
                error: null);
        }
        catch (Exception ex)
        {
            Complete(
                state,
                AgentIntegrationTaskStatus.Failed,
                finalText: null,
                error: Bound(ex.Message, 2_000));
        }
    }

    private async Task<bool> RequestApprovalAsync(
        TaskState state,
        string title,
        string details,
        CancellationToken cancellationToken)
    {
        title = BoundRequired(title, nameof(title), 500);
        details = Bound(details, 8_000);
        TaskCompletionSource<bool> completion;
        AgentIntegrationApproval approval;

        lock (state.Gate)
        {
            if (state.PendingApproval is not null)
                throw new InvalidOperationException(
                    "Only one approval may be pending for an Agent task.");

            approval = new AgentIntegrationApproval(
                Guid.NewGuid(),
                title,
                details,
                DateTime.UtcNow);
            completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            state.PendingApproval = approval;
            state.ApprovalCompletion = completion;
            state.Status = AgentIntegrationTaskStatus.WaitingForApproval;
            state.UpdatedUtc = DateTime.UtcNow;
            AddProgressLocked(
                state,
                "approval",
                "approval-requested",
                title);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            state.Cancellation.Token,
            cancellationToken);
        return await completion.Task
            .WaitAsync(linked.Token)
            .ConfigureAwait(false);
    }

    private static void AddProgress(
        TaskState state,
        string kind,
        string code,
        string message)
    {
        lock (state.Gate)
            AddProgressLocked(state, kind, code, message);
    }

    private static void AddProgressLocked(
        TaskState state,
        string kind,
        string code,
        string message)
    {
        kind = BoundRequired(kind, nameof(kind), 128);
        code = BoundRequired(code, nameof(code), 128);
        message = Bound(message, 2_000);
        state.Progress.Add(new AgentIntegrationProgress(
            state.Progress.Count,
            DateTime.UtcNow,
            kind,
            code,
            message));
        state.UpdatedUtc = DateTime.UtcNow;
    }

    private static void AddEvidence(
        TaskState state,
        AgentIntegrationEvidence evidence)
    {
        lock (state.Gate)
        {
            if (IsTerminal(state.Status))
                throw new InvalidOperationException(
                    "Cannot append evidence after a task is terminal.");
            if (!state.Evidence.Contains(evidence))
                state.Evidence.Add(evidence);
            state.UpdatedUtc = DateTime.UtcNow;
        }
    }

    private static void Complete(
        TaskState state,
        AgentIntegrationTaskStatus status,
        string? finalText,
        string? error)
    {
        AgentIntegrationFinalResult result;
        TaskCompletionSource<bool>? approval;
        lock (state.Gate)
        {
            if (IsTerminal(state.Status))
                return;

            state.Status = status;
            state.PendingApproval = null;
            approval = state.ApprovalCompletion;
            state.ApprovalCompletion = null;
            state.UpdatedUtc = DateTime.UtcNow;
            AddProgressLocked(
                state,
                "final",
                status.ToString().ToLowerInvariant(),
                "Agent task reached terminal host state " + status + ".");

            result = new AgentIntegrationFinalResult(
                state.TaskId,
                status,
                string.IsNullOrWhiteSpace(finalText)
                    ? null
                    : Bound(finalText, 16_000),
                state.Evidence.ToArray(),
                string.IsNullOrWhiteSpace(error)
                    ? null
                    : Bound(error, 2_000));
            state.FinalResult = result;
        }

        approval?.TrySetCanceled();
        state.FinalCompletion.TrySetResult(result);
    }

    private TaskState State(Guid taskId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (taskId == Guid.Empty)
            throw new ArgumentException("Task ID cannot be empty.", nameof(taskId));

        lock (_tasksGate)
        {
            return _tasks.TryGetValue(taskId, out var state)
                ? state
                : throw new KeyNotFoundException(
                    "Unknown Agent integration task.");
        }
    }

    private static bool IsTerminal(AgentIntegrationTaskStatus status)
        => status is AgentIntegrationTaskStatus.Completed
            or AgentIntegrationTaskStatus.Blocked
            or AgentIntegrationTaskStatus.Cancelled
            or AgentIntegrationTaskStatus.Failed;

    private static string BoundRequired(
        string? value,
        string parameterName,
        int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return Bound(value, maxLength);
    }

    private static string Bound(string? value, int maxLength)
    {
        value ??= "";
        value = value.Trim();
        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        TaskState[] states;
        lock (_tasksGate)
            states = _tasks.Values.ToArray();

        foreach (var state in states)
        {
            state.Cancellation.Cancel();
            state.Cancellation.Dispose();
        }
    }

    private sealed class TaskState
    {
        public TaskState(
            Guid taskId,
            AgentIntegrationTaskRequest request,
            AgentIntegrationProjectContext project)
        {
            TaskId = taskId;
            Request = request;
            Project = project;
            CreatedUtc = DateTime.UtcNow;
            UpdatedUtc = CreatedUtc;
        }

        public object Gate { get; } = new();
        public Guid TaskId { get; }
        public AgentIntegrationTaskRequest Request { get; }
        public AgentIntegrationProjectContext Project { get; }
        public AgentIntegrationTaskStatus Status { get; set; } =
            AgentIntegrationTaskStatus.Queued;
        public List<AgentIntegrationProgress> Progress { get; } = [];
        public List<AgentIntegrationEvidence> Evidence { get; } = [];
        public AgentIntegrationApproval? PendingApproval { get; set; }
        public TaskCompletionSource<bool>? ApprovalCompletion { get; set; }
        public AgentIntegrationFinalResult? FinalResult { get; set; }
        public TaskCompletionSource<AgentIntegrationFinalResult> FinalCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Cancellation { get; } = new();
        public DateTime CreatedUtc { get; }
        public DateTime UpdatedUtc { get; set; }
    }
}

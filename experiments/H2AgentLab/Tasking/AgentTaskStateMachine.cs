namespace H2AgentLab.Tasking;

public enum AgentTaskState
{
    Received = 0,
    Grounded = 1,
    Planned = 2,
    Executing = 3,
    Verifying = 4,
    Completed = 5,
    Repairing = 6,
    Blocked = 7,
    Cancelled = 8,
    Failed = 9
}

public sealed record AgentTaskTransition(
    long Sequence,
    AgentTaskState From,
    AgentTaskState To,
    string? Reason);

/// <summary>
/// Deterministic host-owned lifecycle for a single task. The model may propose a next state, but
/// only this transition graph decides whether the transition is legal.
/// </summary>
public sealed class AgentTaskStateMachine
{
    private static readonly IReadOnlyDictionary<AgentTaskState, AgentTaskState[]> MainTransitions =
        new Dictionary<AgentTaskState, AgentTaskState[]>
        {
            [AgentTaskState.Received] = [AgentTaskState.Grounded],
            [AgentTaskState.Grounded] = [AgentTaskState.Planned],
            [AgentTaskState.Planned] = [AgentTaskState.Executing],
            [AgentTaskState.Executing] = [AgentTaskState.Verifying],
            [AgentTaskState.Verifying] = [AgentTaskState.Repairing],
            [AgentTaskState.Repairing] = [AgentTaskState.Executing],
            [AgentTaskState.Completed] = [],
            [AgentTaskState.Blocked] = [],
            [AgentTaskState.Cancelled] = [],
            [AgentTaskState.Failed] = []
        };

    private static readonly AgentTaskState[] TerminalEscapeStates =
        [AgentTaskState.Blocked, AgentTaskState.Cancelled, AgentTaskState.Failed];

    private readonly List<AgentTaskTransition> _history = [];

    public AgentTaskState State { get; private set; } = AgentTaskState.Received;
    public IReadOnlyList<AgentTaskTransition> History => _history.AsReadOnly();
    public bool IsTerminal => IsTerminalState(State);

    public bool CanTransitionTo(AgentTaskState next)
    {
        if (next == AgentTaskState.Completed)
            return false;
        return CanTransitionCore(next);
    }

    public AgentTaskTransition TransitionTo(AgentTaskState next, string? reason = null)
    {
        if (next == AgentTaskState.Completed)
            throw new InvalidOperationException("Completed is protected by the verification gate. Use Complete(...).");
        if (!CanTransitionCore(next))
            throw new InvalidOperationException($"Illegal task transition {State} -> {next}.");

        return ApplyTransition(next, reason);
    }

    public AgentTaskTransition Complete(
        AgentTaskContract contract,
        AgentVerificationOutcome outcome,
        string? reason = null)
    {
        if (State != AgentTaskState.Verifying)
            throw new InvalidOperationException($"Task can only complete from Verifying, not {State}.");

        AgentTaskCompletionGate.EnsureCanComplete(contract, outcome);
        return ApplyTransition(AgentTaskState.Completed, reason);
    }

    private bool CanTransitionCore(AgentTaskState next)
    {
        if (!Enum.IsDefined(next) || IsTerminal)
            return false;

        if (TerminalEscapeStates.Contains(next))
            return true;

        return MainTransitions.TryGetValue(State, out var allowed) && allowed.Contains(next);
    }

    private AgentTaskTransition ApplyTransition(AgentTaskState next, string? reason)
    {
        var from = State;
        State = next;
        var transition = new AgentTaskTransition(
            Sequence: _history.Count,
            From: from,
            To: next,
            Reason: NormalizeReason(reason));
        _history.Add(transition);
        return transition;
    }

    public static bool IsTerminalState(AgentTaskState state)
        => state is AgentTaskState.Completed
            or AgentTaskState.Blocked
            or AgentTaskState.Cancelled
            or AgentTaskState.Failed;

    private static string? NormalizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var normalized = reason.Trim();
        if (normalized.Length > 2_000)
            throw new ArgumentException("Transition reason exceeds 2,000 characters.", nameof(reason));
        return normalized;
    }
}

using H2AgentLab.Context;

namespace H2AgentLab.Tasking;

public sealed class AgentOrchestrationSession
{
    internal AgentOrchestrationSession(
        AgentTaskContract contract,
        AgentTaskRouteDecision route,
        AgentTaskStateMachine stateMachine)
    {
        Contract = contract;
        Route = route;
        StateMachine = stateMachine;
    }

    public AgentTaskContract Contract { get; private set; }
    public AgentTaskRouteDecision Route { get; }
    public AgentTaskStateMachine StateMachine { get; }

    internal void ReplaceContract(AgentTaskContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (contract.TaskId != Contract.TaskId)
            throw new InvalidOperationException("Cannot replace an orchestration session contract with a different task ID.");
        Contract = contract;
    }
}

/// <summary>
/// Phase-04 orchestration shell. It owns host-side routing/state/context boundaries while the
/// existing AgentRunner remains available as a separate v1 compatibility path for A/B comparison.
/// Provider transport/tool execution migration is intentionally deferred to later phases.
/// </summary>
public sealed class AgentOrchestrator
{
    private readonly AgentFastPathRouter _router;
    private readonly Func<global::H2AgentLab.AgentRunner> _compatibilityRunnerFactory;

    public AgentOrchestrator(
        AgentFastPathRouter? router = null,
        AgentContextManager? contextManager = null,
        Func<global::H2AgentLab.AgentRunner>? compatibilityRunnerFactory = null)
    {
        _router = router ?? new AgentFastPathRouter();
        ContextManager = contextManager ?? new AgentContextManager();
        _compatibilityRunnerFactory = compatibilityRunnerFactory ?? (() => new global::H2AgentLab.AgentRunner());
    }

    public AgentContextManager ContextManager { get; }

    public AgentOrchestrationSession Receive(
        AgentTaskContract contract,
        AgentTaskRoutingSignals? routingSignals = null)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return new AgentOrchestrationSession(
            contract,
            _router.Route(contract, routingSignals),
            new AgentTaskStateMachine());
    }

    public AgentTaskTransition Ground(AgentOrchestrationSession session, string? reason = null)
        => Machine(session).TransitionTo(AgentTaskState.Grounded, reason);

    public AgentTaskTransition Plan(AgentOrchestrationSession session, string? reason = null)
        => Machine(session).TransitionTo(AgentTaskState.Planned, reason);

    public AgentTaskTransition Execute(AgentOrchestrationSession session, string? reason = null)
        => Machine(session).TransitionTo(AgentTaskState.Executing, reason);

    public AgentTaskTransition Verify(AgentOrchestrationSession session, string? reason = null)
        => Machine(session).TransitionTo(AgentTaskState.Verifying, reason);

    public AgentTaskTransition Repair(AgentOrchestrationSession session, string? reason = null)
        => Machine(session).TransitionTo(AgentTaskState.Repairing, reason);

    public AgentTaskTransition Complete(
        AgentOrchestrationSession session,
        AgentVerificationOutcome outcome,
        string? reason = null)
        => Machine(session).Complete(Session(session).Contract, outcome, reason);

    public AgentTaskTransition Block(AgentOrchestrationSession session, string? reason = null)
        => Machine(session).TransitionTo(AgentTaskState.Blocked, reason);

    public AgentTaskTransition Cancel(AgentOrchestrationSession session, string? reason = null)
        => Machine(session).TransitionTo(AgentTaskState.Cancelled, reason);

    public AgentTaskTransition Fail(AgentOrchestrationSession session, string? reason = null)
        => Machine(session).TransitionTo(AgentTaskState.Failed, reason);

    public void UpdateContract(
        AgentOrchestrationSession session,
        AgentTaskContract expandedOrEvidenceUpdatedContract)
    {
        var current = Session(session).Contract;
        ArgumentNullException.ThrowIfNull(expandedOrEvidenceUpdatedContract);
        if (expandedOrEvidenceUpdatedContract.TaskId != current.TaskId)
            throw new InvalidOperationException("Task contract update changed task identity.");

        var nextById = expandedOrEvidenceUpdatedContract.AcceptanceCriteria
            .ToDictionary(x => x.CriterionId, StringComparer.Ordinal);
        foreach (var existing in current.AcceptanceCriteria)
        {
            if (!nextById.TryGetValue(existing.CriterionId, out var next)
                || !string.Equals(next.Requirement, existing.Requirement, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Task contract update removed or redefined acceptance criterion '{existing.CriterionId}'.");
        }

        session.ReplaceContract(expandedOrEvidenceUpdatedContract);
    }

    /// <summary>
    /// Explicitly retained v1 path. The v2 orchestrator does not silently route through it.
    /// </summary>
    public global::H2AgentLab.AgentRunner CreateCompatibilityRunner()
        => _compatibilityRunnerFactory();

    private static AgentOrchestrationSession Session(AgentOrchestrationSession? session)
        => session ?? throw new ArgumentNullException(nameof(session));

    private static AgentTaskStateMachine Machine(AgentOrchestrationSession? session)
        => Session(session).StateMachine;
}

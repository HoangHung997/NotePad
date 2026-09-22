using H2AgentLab.Context;
using H2AgentLab.Runtime;
using H2AgentLab.Metrics;
using H2Notes.Core;
using H2AgentLab.Verification;

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
/// Host-side orchestration shell for the normal AgentRuntime path. Legacy baseline execution is
/// intentionally kept outside this production orchestration type.
/// </summary>
public sealed class AgentOrchestrator
{
    private readonly AgentFastPathRouter _router;
    private readonly IAgentRuntimeFactory _runtimeFactory;
    private readonly AgentCapabilityRefreshCoordinator? _capabilityRefresh;

    public AgentOrchestrator(
        AgentFastPathRouter? router = null,
        AgentContextManager? contextManager = null,
        AgentCapabilityRefreshCoordinator? capabilityRefresh = null,
        IAgentRuntimeFactory? runtimeFactory = null)
    {
        _router = router ?? new AgentFastPathRouter();
        ContextManager = contextManager ?? new AgentContextManager();
        _runtimeFactory = runtimeFactory ?? new AgentRuntimeFactory();
        _capabilityRefresh = capabilityRefresh;
    }

    public AgentContextManager ContextManager { get; }

    public AgentRuntime CreateRuntime(
        AiProfile profile,
        string apiKey,
        global::H2AgentLab.AgentTools tools,
        AgentRunTelemetry telemetry)
        => _runtimeFactory.Create(
            profile,
            apiKey,
            tools,
            ContextManager,
            telemetry);

    public async Task<AgentRuntimeResult> RunRuntimeAsync(
        AgentOrchestrationSession session,
        AgentRuntime runtime,
        AgentRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(request);
        var current = Session(session);
        if (request.Contract.TaskId != current.Contract.TaskId)
            throw new InvalidOperationException("Runtime request contract does not belong to this orchestration session.");

        Execute(current, "AgentRuntime execution started.");
        try
        {
            var result = await runtime.RunAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.EffectiveContract is { } effective)
                UpdateContract(current, effective);
            Verify(current, "AgentRuntime returned; host completion state evaluated.");

            if (result.VerificationHistory.Count > 0)
            {
                var latest = result.VerificationHistory[^1];
                if (latest.Passed)
                {
                    CompleteVerified(
                        current,
                        [latest],
                        "Latest deterministic verifier report passed.");
                }
                else
                {
                    Block(current, "Latest deterministic verifier report did not pass.");
                }
            }
            else if (current.Contract.IsMutating)
            {
                Block(
                    current,
                    "Mutating runtime path has no deterministic verifier report yet. MB-42 owns verifier integration.");
            }
            else
            {
                Complete(
                    current,
                    new AgentVerificationOutcome(passed: false),
                    "Read-only runtime task completed without mutation verification.");
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            if (!current.StateMachine.IsTerminal)
                Cancel(current, "AgentRuntime cancelled.");
            throw;
        }
        catch (AgentVerificationRequiredException ex)
        {
            if (!current.StateMachine.IsTerminal)
                Block(current, ex.Message);
            throw;
        }
        catch
        {
            if (!current.StateMachine.IsTerminal)
                Fail(current, "AgentRuntime failed.");
            throw;
        }
    }

    public Task<CapabilityRefreshResult> RefreshCapabilitiesAtTaskBoundaryAsync(
        CancellationToken cancellationToken = default)
        => _capabilityRefresh is null
            ? Task.FromResult(new CapabilityRefreshResult(0, 0, Array.Empty<string>(), DateTime.UtcNow))
            : _capabilityRefresh.RefreshAtTaskBoundaryAsync(cancellationToken);

    public IDisposable EnterCapabilityToolCallBoundary()
        => _capabilityRefresh?.EnterToolCall() ?? EmptyScope.Instance;

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

    public AgentTaskTransition CompleteVerified(
        AgentOrchestrationSession session,
        IEnumerable<VerificationReport> reports,
        string? reason = null)
    {
        var current = Session(session);
        var outcome = VerificationCompletionGate.Evaluate(current.Contract, reports);
        return Machine(current).Complete(current.Contract, outcome, reason);
    }

    public AgentTaskTransition CompleteNotMechanicallyVerifiable(
        AgentOrchestrationSession session,
        IEnumerable<AgentEvidenceReference> classificationEvidence,
        string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(classificationEvidence);
        var current = Session(session);
        var outcome = new AgentVerificationOutcome(
            passed: false,
            notMechanicallyVerifiable: true,
            nonMechanicalEvidence: classificationEvidence);
        return Machine(current).Complete(current.Contract, outcome, reason);
    }

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

    private static AgentOrchestrationSession Session(AgentOrchestrationSession? session)
        => session ?? throw new ArgumentNullException(nameof(session));

    private static AgentTaskStateMachine Machine(AgentOrchestrationSession? session)
        => Session(session).StateMachine;

    private sealed class EmptyScope : IDisposable
    {
        public static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}

using H2AgentLab.Tasking;

namespace H2AgentLab.Verification;

public sealed record VerificationRecoveryState(
    bool HasRuntimeRecoveryPending,
    bool HasSemanticVerificationFailures);

/// <summary>
/// Keeps runtime/protocol recovery below semantic verification. RecoverySupervisor may unblock a
/// failed tool/protocol step, but it never rewrites a VerificationReport or turns semantic failure
/// into completion. Semantic repair is always derived from verifier output.
/// </summary>
public sealed class VerificationRecoveryCoordinator
{
    private readonly global::H2AgentLab.RecoverySupervisor _runtimeRecovery;
    private readonly AgentRepairController _semanticRepair;

    public VerificationRecoveryCoordinator(
        global::H2AgentLab.RecoverySupervisor? runtimeRecovery = null,
        AgentRepairController? semanticRepair = null)
    {
        _runtimeRecovery = runtimeRecovery ?? new global::H2AgentLab.RecoverySupervisor();
        _semanticRepair = semanticRepair ?? new AgentRepairController();
    }

    public global::H2AgentLab.RecoverySupervisor RuntimeRecovery => _runtimeRecovery;

    public string? BlockRuntimeCall(global::H2AgentLab.ToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return _runtimeRecovery.Block(call);
    }

    public void ObserveRuntimeCall(global::H2AgentLab.ToolCall call, string result)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(result);
        _runtimeRecovery.Observe(call, result);
    }

    public bool NeedsRuntimeContinuation => _runtimeRecovery.NeedsContinuation;

    public string RuntimeContinuation()
        => _runtimeRecovery.Continuation();

    public AgentRepairContext BuildSemanticRepair(
        AgentTaskContract contract,
        VerificationReport report)
        => _semanticRepair.Build(contract, report);

    public VerificationRecoveryState GetState(VerificationReport? semanticReport = null)
        => new(
            HasRuntimeRecoveryPending: _runtimeRecovery.HasPending,
            HasSemanticVerificationFailures:
                semanticReport?.Criteria.Any(x => x.Status == VerificationCriterionStatus.Failed) == true);
}

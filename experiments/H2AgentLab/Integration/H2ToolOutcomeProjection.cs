using H2AgentLab.Tools;
using H2Notes.Core;

namespace H2AgentLab.Integration;

/// <summary>Maps typed engine metadata into the existing product activity projection.
/// No domain output, arguments, file body, credentials or exception text are copied.</summary>
public static class H2ToolOutcomeProjection
{
    public static H2AgentToolOutcome ToProduct(ToolOutcome outcome) => new(
        outcome.Invocation.InvocationId, outcome.Invocation.LogicalOperationId,
        outcome.Status switch {
            ToolOutcomeStatus.Succeeded => H2ToolRunStatus.Succeeded,
            ToolOutcomeStatus.Running => H2ToolRunStatus.Running,
            ToolOutcomeStatus.Rejected => H2ToolRunStatus.Rejected,
            ToolOutcomeStatus.Failed => H2ToolRunStatus.Failed,
            ToolOutcomeStatus.Cancelled => H2ToolRunStatus.Cancelled,
            ToolOutcomeStatus.PartiallyApplied => H2ToolRunStatus.PartiallyApplied,
            _ => H2ToolRunStatus.OutcomeUnknown },
        outcome.Effect switch {
            ToolMutationEffect.None => H2ToolMutationEffect.None,
            ToolMutationEffect.Applied => H2ToolMutationEffect.Applied,
            ToolMutationEffect.PartiallyApplied => H2ToolMutationEffect.PartiallyApplied,
            _ => H2ToolMutationEffect.Unknown },
        outcome.Verification.Status switch {
            ToolVerificationStatus.Passed => H2ToolVerificationStatus.Passed,
            ToolVerificationStatus.Failed => H2ToolVerificationStatus.Failed,
            _ => H2ToolVerificationStatus.NotRun },
        outcome.Completeness.Complete, outcome.Completeness.NextCursor, outcome.Completeness.Reason,
        outcome.Job?.JobId, outcome.Error?.Code, outcome.Error?.Phase.ToString(),
        outcome.Error?.SafeMessage, outcome.Error?.RetryClass.ToString());
}

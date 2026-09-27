namespace H2Notes.Core;

// Product projection only: no runtime/tool/backend dependency and no new persistence owner.
public enum H2ToolRunStatus { Succeeded, Running, Rejected, Failed, Cancelled, PartiallyApplied, OutcomeUnknown }
public enum H2ToolMutationEffect { None, Applied, PartiallyApplied, Unknown }
public enum H2ToolVerificationStatus { NotRun, Passed, Failed }

public sealed record H2AgentToolOutcome(Guid InvocationId, string LogicalOperationId,
    H2ToolRunStatus Status, H2ToolMutationEffect Effect, H2ToolVerificationStatus Verification,
    bool Complete, string? NextCursor, string? CompletenessReason, string? JobId,
    string? ErrorCode, string? ErrorPhase, string? SafeMessage, string? RetryClass);

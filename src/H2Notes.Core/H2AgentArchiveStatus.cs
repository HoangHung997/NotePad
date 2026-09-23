namespace H2Notes.Core;

/// <summary>Read-only Agent storage health; distinct from H2 project/Coordinator sync health.</summary>
public sealed record H2AgentArchiveStatus(bool CanWrite, string State, long LastSequence, IReadOnlyList<string> Diagnostics);
public interface IH2AgentArchiveStatus { H2AgentArchiveStatus GetArchiveStatus(); }
public sealed record H2AgentRecoverySnapshot(bool Interrupted, bool ReconcileRequired, long JournalSequence,
    IReadOnlyList<H2AgentOperationRecord> Operations);
/// <summary>No executable arguments, credentials or raw document output are persisted here.
/// Dispatched without a result is uncertain, not a retry instruction.</summary>
public sealed record H2AgentOperationRecord(Guid InvocationId, string LogicalOperationId, Guid TurnId,
    string GoalRevisionId, string ToolCallId, string ToolName, string State, string Status, string Effect,
    string ArgumentsSha256, string? ResourceKeySha256, string? OutputSha256, string? ErrorCode)
{
    public H2AgentProcessJobInfo? Job { get; init; }
}
/// <summary>Agent-owned process receipt, not H2 project state or authority to adopt a saved PID.</summary>
public sealed record H2AgentProcessJobInfo(string JobId, Guid OwnerTaskId, string GoalRevisionId,
    int ProcessId, DateTime ProcessStartedUtc, DateTime DeadlineUtc, string Status,
    bool RootExited, bool AllProcessesExited, bool StreamsDrained, bool OutputComplete,
    int? ExitCode, string HostExitPolicy, IReadOnlyList<string> OutputArtifacts);

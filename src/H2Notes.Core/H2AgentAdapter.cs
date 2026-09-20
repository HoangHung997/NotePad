namespace H2Notes.Core;

/// <summary>
/// H2-facing task lifecycle state. This is a product projection, not Agent runtime state.
/// </summary>
public enum H2AgentTaskStatus
{
    Queued = 0,
    Running = 1,
    WaitingForApproval = 2,
    Completed = 3,
    Blocked = 4,
    Cancelled = 5,
    Failed = 6
}

public sealed record H2AgentTaskContext(
    string? WorkspaceRoot,
    string? Summary,
    long Version = 0);

public sealed record H2AgentProgress(
    long Sequence,
    DateTime AtUtc,
    string Kind,
    string Code,
    string Message);

public sealed record H2AgentApproval(
    Guid ApprovalId,
    string Title,
    string Details,
    DateTime CreatedUtc);

public sealed record H2AgentEvidence(
    string EvidenceId,
    string Kind,
    string? Sha256,
    string? Summary,
    string? SourceUri = null,
    string? LocalPath = null,
    string? Provenance = null);

public sealed record H2AgentTaskSummary(
    Guid TaskId,
    Guid? ProjectId,
    string Goal,
    H2AgentTaskStatus Status,
    H2AgentApproval? PendingApproval,
    IReadOnlyList<H2AgentEvidence> Evidence,
    string? FinalText,
    string? Error,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

public sealed record H2AgentTaskObservation(
    H2AgentTaskSummary Summary,
    IReadOnlyList<H2AgentProgress> Progress);

/// <summary>
/// The only Agent-shaped service surface H2 product code should consume.
/// A concrete bridge may wrap the accepted Agent public integration boundary,
/// but H2 product code must not depend on model/provider/runtime/tool internals.
/// </summary>
public interface IH2AgentAdapter
{
    Task<Guid> StartTaskAsync(
        Guid? projectId,
        string goal,
        H2AgentTaskContext? context = null,
        bool readOnly = true,
        CancellationToken cancellationToken = default);

    H2AgentTaskObservation ObserveTask(
        Guid taskId,
        long afterSequence = -1);

    void CancelTask(Guid taskId);

    bool RespondToApproval(
        Guid taskId,
        Guid approvalId,
        bool approved);

    H2AgentTaskSummary GetTaskSummary(Guid taskId);

    IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(
        Guid? projectId = null,
        int limit = 50);

    H2AgentEvidence? GetEvidence(string evidenceId);

    bool AttachProject(
        Guid taskId,
        Guid projectId);
}

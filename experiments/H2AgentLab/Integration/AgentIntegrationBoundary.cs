namespace H2AgentLab.Integration;

/// <summary>
/// Stable H2 Notes-facing task state. Provider/model/runtime-specific states are intentionally not
/// part of this public contract.
/// </summary>
public enum AgentIntegrationTaskStatus
{
    Queued = 0,
    Running = 1,
    WaitingForApproval = 2,
    Completed = 3,
    Blocked = 4,
    Cancelled = 5,
    Failed = 6
}

public sealed record AgentIntegrationProjectContext
{
    public AgentIntegrationProjectContext(
        string projectId,
        string workspaceRoot,
        string summary,
        long version = 0)
    {
        ProjectId = Normalize(projectId, nameof(projectId), 256);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        WorkspaceRoot = Path.GetFullPath(workspaceRoot);
        Summary = Normalize(summary, nameof(summary), 16_000, allowEmpty: true);
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
        Version = version;
    }

    public string ProjectId { get; }
    public string WorkspaceRoot { get; }
    public string Summary { get; }
    public long Version { get; }

    private static string Normalize(
        string? value,
        string parameterName,
        int maxLength,
        bool allowEmpty = false)
    {
        value ??= "";
        var normalized = value.Trim();
        if ((!allowEmpty && normalized.Length == 0)
            || normalized.Length > maxLength
            || normalized.Any(char.IsControl))
            throw new ArgumentException(
                $"Value must be {(allowEmpty ? "at most" : "between 1 and")} {maxLength} characters and contain no control characters.",
                parameterName);
        return normalized;
    }
}

public sealed record AgentIntegrationTaskRequest
{
    public AgentIntegrationTaskRequest(
        string projectId,
        string goal,
        bool readOnly = true)
    {
        ProjectId = Normalize(projectId, nameof(projectId), 256);
        Goal = Normalize(goal, nameof(goal), 8_000);
        ReadOnly = readOnly;
    }

    public string ProjectId { get; }
    public string Goal { get; }
    public bool ReadOnly { get; }

    private static string Normalize(string? value, string parameterName, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maxLength || normalized.Any(char.IsControl))
            throw new ArgumentException(
                $"Value must be at most {maxLength} characters and contain no control characters.",
                parameterName);
        return normalized;
    }
}

public sealed record AgentIntegrationProgress(
    long Sequence,
    DateTime AtUtc,
    string Kind,
    string Code,
    string Message);

public sealed record AgentIntegrationApproval(
    Guid ApprovalId,
    string Title,
    string Details,
    DateTime CreatedUtc);

public sealed record AgentIntegrationEvidence(
    string EvidenceId,
    string Kind,
    string? Sha256,
    string? Summary);

public sealed record AgentIntegrationFinalResult(
    Guid TaskId,
    AgentIntegrationTaskStatus Status,
    string? Text,
    IReadOnlyList<AgentIntegrationEvidence> Evidence,
    string? Error);

public sealed record AgentIntegrationTaskSnapshot(
    Guid TaskId,
    string ProjectId,
    string Goal,
    AgentIntegrationTaskStatus Status,
    AgentIntegrationApproval? PendingApproval,
    IReadOnlyList<AgentIntegrationEvidence> Evidence,
    AgentIntegrationFinalResult? FinalResult,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

/// <summary>
/// The complete public Agent boundary that H2 Notes is allowed to call after user acceptance.
/// Construction/provider composition is deliberately outside the H2 UI contract.
/// </summary>
public interface IAgentIntegrationBoundary
{
    void LoadProjectContext(AgentIntegrationProjectContext context);

    Task<Guid> StartTaskAsync(
        AgentIntegrationTaskRequest request,
        CancellationToken cancellationToken = default);

    IReadOnlyList<AgentIntegrationProgress> ObserveProgress(
        Guid taskId,
        long afterSequence = -1);

    AgentIntegrationTaskSnapshot InspectTask(Guid taskId);

    void Cancel(Guid taskId);

    bool ProvideApproval(
        Guid taskId,
        Guid approvalId,
        bool approved);

    Task<AgentIntegrationFinalResult> WaitForFinalResultAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);
}

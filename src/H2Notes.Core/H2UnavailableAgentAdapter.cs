namespace H2Notes.Core;

/// <summary>
/// Safe placeholder until a production Agent bridge is composed into H2.
/// Read/query operations return no Agent runs; execution operations fail explicitly.
/// </summary>
public sealed class H2UnavailableAgentAdapter : IH2AgentAdapter
{
    public static H2UnavailableAgentAdapter Instance { get; } = new();

    private H2UnavailableAgentAdapter() { }

    public Task<Guid> StartTaskAsync(
        Guid? projectId,
        string goal,
        H2AgentTaskContext? context = null,
        bool readOnly = true,
        CancellationToken cancellationToken = default)
        => Task.FromException<Guid>(new InvalidOperationException(
            "H2 Agent production integration is not available yet."));

    public H2AgentTaskObservation ObserveTask(Guid taskId, long afterSequence = -1)
        => throw new KeyNotFoundException("Agent task is not available.");

    public void CancelTask(Guid taskId)
        => throw new KeyNotFoundException("Agent task is not available.");

    public bool RespondToApproval(Guid taskId, Guid approvalId, bool approved) => false;

    public H2AgentTaskSummary GetTaskSummary(Guid taskId)
        => throw new KeyNotFoundException("Agent task is not available.");

    public IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(Guid? projectId = null, int limit = 50)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        return Array.Empty<H2AgentTaskSummary>();
    }

    public H2AgentEvidence? GetEvidence(string evidenceId) => null;

    public bool AttachProject(Guid taskId, Guid projectId) => false;
}

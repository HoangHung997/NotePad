namespace H2Notes.Core;

public sealed record H2AgentTaskCorrelation(
    Guid TaskId,
    Guid? ProjectId,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

/// <summary>
/// Machine/runtime correlation only. This index does not own Agent task state and is not embedded
/// into ProjectRecord. Agent task/evidence truth remains behind IH2AgentAdapter.
/// </summary>
public sealed class H2AgentTaskCorrelationIndex
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, H2AgentTaskCorrelation> _entries = [];

    public H2AgentTaskCorrelation Register(Guid taskId, Guid? projectId, DateTime? nowUtc = null)
    {
        if (taskId == Guid.Empty) throw new ArgumentException("TaskId must not be empty.", nameof(taskId));
        if (projectId == Guid.Empty) throw new ArgumentException("ProjectId must be null or non-empty.", nameof(projectId));
        var now = nowUtc ?? DateTime.UtcNow;
        lock (_gate)
        {
            if (_entries.TryGetValue(taskId, out var existing))
            {
                if (existing.ProjectId != projectId)
                    throw new InvalidOperationException("Task correlation already exists with a different ProjectId.");
                return existing;
            }

            var entry = new H2AgentTaskCorrelation(taskId, projectId, now, now);
            _entries.Add(taskId, entry);
            return entry;
        }
    }

    public bool AttachProject(Guid taskId, Guid projectId, DateTime? nowUtc = null)
    {
        if (projectId == Guid.Empty) return false;
        lock (_gate)
        {
            if (!_entries.TryGetValue(taskId, out var existing)) return false;
            if (existing.ProjectId == projectId) return true;
            _entries[taskId] = existing with { ProjectId = projectId, UpdatedUtc = nowUtc ?? DateTime.UtcNow };
            return true;
        }
    }

    public H2AgentTaskCorrelation? Find(Guid taskId)
    {
        lock (_gate)
            return _entries.TryGetValue(taskId, out var value) ? value : null;
    }

    public IReadOnlyList<H2AgentTaskCorrelation> QueryByProject(Guid projectId, int limit = 50)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("ProjectId must not be empty.", nameof(projectId));
        return Query(limit, value => value.ProjectId == projectId);
    }

    public IReadOnlyList<H2AgentTaskCorrelation> QueryUnscoped(int limit = 50)
        => Query(limit, value => value.ProjectId is null);

    public IReadOnlyList<H2AgentTaskCorrelation> QueryAll(int limit = 50)
        => Query(limit, _ => true);

    public bool Remove(Guid taskId)
    {
        lock (_gate) return _entries.Remove(taskId);
    }

    private IReadOnlyList<H2AgentTaskCorrelation> Query(
        int limit,
        Func<H2AgentTaskCorrelation, bool> predicate)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_gate)
            return _entries.Values
                .Where(predicate)
                .OrderByDescending(value => value.UpdatedUtc)
                .ThenBy(value => value.TaskId)
                .Take(limit)
                .ToArray();
    }
}

using System.Text.Json;
using H2Notes.Core;

namespace H2AgentLab.Integration;

/// <summary>Host-owned selection, never deserialized from model arguments. Global defaults to
/// its own thread; Project may search that project, not every project or the whole machine.</summary>
internal sealed record AgentHistoryScope(Guid CallerTaskId, Guid? ProjectId, Guid ThreadId, bool ProjectWide = true);
internal sealed record AgentHistorySource(Guid StoreId, long Sequence, Guid TaskId, Guid EventId, string Kind, string Sha256);
internal sealed record AgentHistoryDocument(AgentHistorySource Source, JsonElement Data,
    string CurrentStatus, string? CurrentGoalRevisionId, bool ReconcileRequired);

internal sealed partial class AgentIntegrationTaskArchive
{
    // Rebuildable location/hash index over the SAME journal. No duplicate payload or truth store.
    private readonly Dictionary<long, AgentHistorySource> _historySources = [];
    private void IndexHistory(JournalEntry entry) => _historySources.Add(entry.Sequence,
        new(entry.StoreId, entry.Sequence, entry.StreamId, entry.EventId, entry.Kind, entry.Sha256));

    internal long HistoryFence { get { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return _sequence; } } }

    private bool HistoryAllowed(AgentHistoryScope scope, Guid taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task)) return false;
        if (taskId == scope.CallerTaskId) return true; // execution identity is frozen by the host
        return task.ProjectId == scope.ProjectId && (scope.ProjectId.HasValue && scope.ProjectWide
            || (task.ThreadId ?? task.TaskId) == scope.ThreadId);
    }

    internal IReadOnlyList<AgentHistorySource> HistorySources(AgentHistoryScope scope, long fence, long before,
        bool events, Guid? taskId = null, Guid? threadId = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (fence < 0 || fence > _sequence || before < 1 || before > fence + 1)
                throw new ArgumentException("Invalid history boundary.");
            // Unknown and out-of-scope identities are deliberately indistinguishable.
            if (taskId.HasValue && !HistoryAllowed(scope, taskId.Value))
                throw new UnauthorizedAccessException("History source is unavailable in this scope.");
            var candidates = _historySources.Values.Where(e => e.Sequence <= fence && e.Kind != "thread"
                && HistoryAllowed(scope, e.TaskId) && (e.TaskId != scope.CallerTaskId || taskId == e.TaskId)
                && (!taskId.HasValue || e.TaskId == taskId)
                && (!threadId.HasValue || (_tasks[e.TaskId].ThreadId ?? e.TaskId) == threadId));
            if (!events)
                candidates = candidates.Where(e => e.Kind is "task-state" or "revision")
                    .GroupBy(e => e.TaskId).Select(g => g.MaxBy(e => e.Sequence)!);
            return candidates.Where(e => e.Sequence < before).OrderByDescending(e => e.Sequence).ToArray();
        }
    }

    internal AgentHistoryDocument ReadHistorySource(AgentHistoryScope scope, long sequence)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_historySources.TryGetValue(sequence, out var source) || source.Kind == "thread"
                || !HistoryAllowed(scope, source.TaskId))
                throw new UnauthorizedAccessException("History source is unavailable in this scope.");
            try
            {
                // Revalidate disk against the hash observed during append/replay, not only its
                // self-reported hash. Reads cannot convert modified or missing source into fact.
                var entry = Read<JournalEntry>(Path.Combine(_store, $"event-{sequence:D12}.json"));
                var previous = sequence == 1 ? Zero : _historySources[sequence - 1].Sha256;
                ValidateEntry(entry, sequence, previous);
                if (entry.Sha256 != source.Sha256 || entry.EventId != source.EventId || entry.StreamId != source.TaskId)
                    throw new InvalidDataException("history-source-changed");
                var current = Get(source.TaskId)!;
                return new(source, entry.Payload.Clone(), current.Status.ToString(), current.GoalState?.RevisionId,
                    current.Recovery?.ReconcileRequired == true);
            }
            catch (Exception ex) when (StorageFailure(ex))
            {
                _readOnly = true; Issue("history-source-unavailable");
                throw new IOException("History source is missing or changed; archive recovery is required.", ex);
            }
        }
    }

    internal IReadOnlyList<AgentHistoryDocument> UnfinishedHistory(AgentHistoryScope scope, int limit)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Uses the entire validated task index, NOT the 200-item recent cache or completed
            // chat window. Same-thread unfinished work is preferred; never inferred from prose.
            return _tasks.Values.Where(t => t.TaskId != scope.CallerTaskId && HistoryAllowed(scope, t.TaskId))
                .Select(t => Get(t.TaskId)!).Where(t => t.Status is not (H2AgentTaskStatus.Completed or H2AgentTaskStatus.Cancelled))
                .OrderByDescending(t => (t.ThreadId ?? t.TaskId) == scope.ThreadId)
                .ThenByDescending(t => _taskHeads[t.TaskId]).Take(Math.Clamp(limit, 1, 3))
                .Select(t => ReadHistorySource(scope, _taskHeads[t.TaskId])).ToArray();
        }
    }
}

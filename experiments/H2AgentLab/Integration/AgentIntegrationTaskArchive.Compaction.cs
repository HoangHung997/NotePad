using System.Text.Json;
using H2AgentLab.Session;

namespace H2AgentLab.Integration;

internal sealed partial class AgentIntegrationTaskArchive
{
    // Rebuildable index only. Source is the existing hash-linked Agent journal.
    private readonly Dictionary<(Guid Task, Guid Turn), AgentContextCompactionRecord> _contextHeads = [];
    private readonly Dictionary<(Guid Task, Guid Turn, int Cycle), AgentContextSourceRecord> _contextSources = [];

    internal void RecordContextSource(AgentContextSourceRecord record)
    {
        lock (_gate)
        {
            EnsureWritable();
            var root = Path.Combine(Path.GetDirectoryName(_root)!, "tasks", record.TaskId.ToString("N"));
            var store = new ArtifactStore(root);
            if (store.LoadHandle(record.Source.Id) != record.Source || store.LoadHandle(record.Anchors.Id) != record.Anchors)
                throw new InvalidDataException("context-source-fingerprint");
            Append(record.TaskId, "context-source", record);
        }
    }

    private void ValidateContextSourceRecord(JournalEntry entry)
    {
        var r = entry.Payload.Deserialize<AgentContextSourceRecord>();
        if (r is null || r.Schema != 1 || r.TaskId != entry.StreamId || r.TurnId == Guid.Empty
            || r.Cycle is < 1 or > 32 || r.RequestIndex < r.Cycle || r.RevisionId is not { Length: > 0 and <= 128 }
            || r.RevisionId.Any(char.IsControl) || r.CreatedUtc.Kind != DateTimeKind.Utc
            || r.CompleteBatches is < 1 or > 128 || r.Source is null || r.Anchors is null
            || r.PreviousCheckpointId is not null && !ContextId(r.PreviousCheckpointId, "h2cp1_"))
            throw new InvalidDataException("context-source-shape");
        foreach (var pair in new[] { (r.Source, "source"), (r.Anchors, "anchors") })
            if (!ContextId(pair.Item1.Id, "h2a1_") || !ContextHash(pair.Item1.Sha256)
                || pair.Item1.Kind != AgentArtifactKind.DocumentExtract || pair.Item1.Bytes is < 1 or > ArtifactStore.MaxArtifactBytes
                || pair.Item1.CreatedUtc.Kind != DateTimeKind.Utc
                || pair.Item1.SourceId != $"context:{r.TaskId:N}:{r.TurnId:N}:{r.Cycle}:{pair.Item2}")
                throw new InvalidDataException("context-source-artifact");
        if (r.Source.Id == r.Anchors.Id) throw new InvalidDataException("context-source-alias");
    }

    private void ValidateContextSourceTransition(JournalEntry entry)
    {
        var r = entry.Payload.Deserialize<AgentContextSourceRecord>()!;
        if (!_tasks.TryGetValue(r.TaskId, out var task) || task.GoalState?.RevisionId != r.RevisionId
            || _contextSources.ContainsKey((r.TaskId, r.TurnId, r.Cycle)))
            throw new InvalidDataException("context-source-stale-or-duplicate");
        var prior = _contextHeads.GetValueOrDefault((r.TaskId, r.TurnId));
        if (r.Cycle != (prior?.Cycle ?? 0) + 1 || r.PreviousCheckpointId != prior?.CheckpointId
            || prior is not null && r.RequestIndex <= prior.RequestIndex)
            throw new InvalidDataException("context-source-lineage");
    }

    internal void RecordContextCompaction(AgentContextCompactionRecord record)
    {
        lock (_gate)
        {
            EnsureWritable();
            if (record is null) throw new InvalidDataException("context-record-missing");
            var root = Path.Combine(Path.GetDirectoryName(_root)!, "tasks", record.TaskId.ToString("N"));
            var store = new ArtifactStore(root);
            if (store.LoadHandle(record.Source.Id) != record.Source || store.LoadHandle(record.Anchors.Id) != record.Anchors
                || new CompactionManager(root).Fingerprint(record.CheckpointId) != record.CheckpointSha256)
                throw new InvalidDataException("context-source-fingerprint");
            using var anchors = JsonDocument.Parse(store.ReadText(record.Anchors.Id));
            if (anchors.RootElement.GetProperty("TaskId").GetGuid() != record.TaskId
                || anchors.RootElement.GetProperty("TurnId").GetGuid() != record.TurnId
                || anchors.RootElement.GetProperty("RevisionId").GetString() != record.RevisionId)
                throw new InvalidDataException("context-anchor-identity");
            _fault?.Invoke("context-before-activation");
            Append(record.TaskId, "context-compaction", record);
        }
    }

    private void ValidateContextRecord(JournalEntry entry)
    {
        var r = entry.Payload.Deserialize<AgentContextCompactionRecord>();
        if (r is null || r.Schema != 1 || r.TaskId != entry.StreamId || r.TurnId == Guid.Empty
            || r.Cycle is < 1 or > 32 || r.RequestIndex < r.Cycle || r.RevisionId is not { Length: > 0 and <= 128 }
            || r.RevisionId.Any(char.IsControl) || !ContextId(r.CheckpointId, "h2cp1_")
            || !ContextHash(r.CheckpointSha256) || !ContextHash(r.PreparedBodySha256)
            || r.PreviousCheckpointId is not null && !ContextId(r.PreviousCheckpointId, "h2cp1_")
            || r.CreatedUtc.Kind != DateTimeKind.Utc || r.CompleteBatches is < 1 or > 128
            || r.Summary is not { Length: > 0 and <= CompactionManager.MaxSummaryCharacters }
            || !ContextArtifact(r.Source, r, "source") || !ContextArtifact(r.Anchors, r, "anchors")
            || r.Source.Id == r.Anchors.Id)
            throw new InvalidDataException("context-record-shape");
    }

    private void ValidateContextTransition(JournalEntry entry)
    {
        var r = entry.Payload.Deserialize<AgentContextCompactionRecord>()!;
        if (!_tasks.TryGetValue(r.TaskId, out var task) || task.GoalState?.RevisionId != r.RevisionId)
            throw new InvalidDataException("context-stale-goal-revision");
        var prior = _contextHeads.GetValueOrDefault((r.TaskId, r.TurnId));
        var source = _contextSources.GetValueOrDefault((r.TaskId, r.TurnId, r.Cycle));
        if (r.Cycle != (prior?.Cycle ?? 0) + 1 || r.PreviousCheckpointId != prior?.CheckpointId
            || prior is not null && r.RequestIndex <= prior.RequestIndex
            || source is null || source.Source != r.Source || source.Anchors != r.Anchors
            || source.RevisionId != r.RevisionId || source.RequestIndex != r.RequestIndex
            || source.CompleteBatches != r.CompleteBatches)
            throw new InvalidDataException("context-activation-lineage");
    }

    internal static bool ContextArtifact(AgentArtifactHandle? h, AgentContextCompactionRecord r, string kind)
        => h is not null && ContextId(h.Id, "h2a1_") && ContextHash(h.Sha256)
            && h.Kind == AgentArtifactKind.DocumentExtract && h.Bytes is > 0 and <= ArtifactStore.MaxArtifactBytes
            && h.CreatedUtc.Kind == DateTimeKind.Utc
            && h.SourceId == $"context:{r.TaskId:N}:{r.TurnId:N}:{r.Cycle}:{kind}";
    private static bool ContextHash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool ContextId(string? id, string prefix) => id is not null && id.Length == prefix.Length + 32
        && id.StartsWith(prefix, StringComparison.Ordinal) && id[prefix.Length..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

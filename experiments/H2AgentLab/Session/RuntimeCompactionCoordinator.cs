using System.Text.Json;
using H2AgentLab.Context;

namespace H2AgentLab.Session;

public sealed record RuntimeCompactionResult(
    AgentContextInput Context,
    AgentContextSnapshot Snapshot,
    string? CheckpointId,
    bool CreatedCheckpoint,
    long CoveredThroughSequence);

public sealed class RuntimeCompactionCoordinator
{
    private const int RawTailEventsToKeep = 24;
    private readonly AgentContextManager _contextManager;
    private readonly CompactionManager _compaction;
    private readonly string _stateRoot;

    public RuntimeCompactionCoordinator(
        string stateRoot,
        AgentContextManager contextManager)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        _stateRoot = Path.GetFullPath(stateRoot);
        Directory.CreateDirectory(_stateRoot);
        _contextManager = contextManager ?? throw new ArgumentNullException(nameof(contextManager));
        _compaction = new CompactionManager(_stateRoot);
    }

    public RuntimeCompactionResult Prepare(
        global::H2AgentLab.LabSession session,
        AgentContextInput input)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(input);

        var initial = _contextManager.Build(input);
        if (!initial.Pressure.RequiresCompaction)
            return new(input, initial, null, false, -1);

        var maxSequence = Math.Max(
            input.RecentTurns?.Count > 0
                ? input.RecentTurns.Max(x => x.Sequence)
                : -1,
            input.ToolSummaries?.Count > 0
                ? input.ToolSummaries.Max(x => x.Sequence)
                : -1);

        var cutoff = maxSequence - RawTailEventsToKeep;
        if (cutoff < 0)
            return new(input, initial, null, false, -1);

        var marker = LoadMarker(session.Id);
        if (marker is not null && marker.CoveredThroughSequence > cutoff)
            cutoff = marker.CoveredThroughSequence;

        var trimmed = TrimInput(input, cutoff);
        string? checkpointId = marker?.CheckpointId;
        var created = false;

        if (marker is null || marker.CoveredThroughSequence < cutoff)
        {
            var previous = marker?.CheckpointId;
            var sources = BuildSources(session, marker?.CoveredThroughSequence ?? -1, cutoff, previous);
            if (sources.Count > 0)
            {
                var checkpoint = _compaction.CreateCheckpoint(
                    BuildSummary(session, cutoff),
                    sources,
                    cutoff,
                    previousCheckpointId: previous);
                checkpointId = checkpoint.Id;
                SaveMarker(
                    session.Id,
                    new RuntimeCompactionMarker(
                        checkpoint.Id,
                        cutoff,
                        DateTime.UtcNow));
                created = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(checkpointId))
        {
            var compacted = _compaction.RenderContext(checkpointId);
            trimmed = trimmed with { CompactedHistory = compacted };
        }

        var final = _contextManager.Build(trimmed);
        return new(
            trimmed,
            final,
            checkpointId,
            created,
            cutoff);
    }

    private static AgentContextInput TrimInput(
        AgentContextInput input,
        long cutoff)
    {
        var turns = (input.RecentTurns ?? [])
            .Where(x => x.Sequence > cutoff)
            .ToArray();
        var tools = (input.ToolSummaries ?? [])
            .Where(x => x.Sequence > cutoff)
            .ToArray();

        return input with
        {
            RecentTurns = turns,
            ToolSummaries = tools
        };
    }

    private static List<AgentCompactionSourceReference> BuildSources(
        global::H2AgentLab.LabSession session,
        long previousCovered,
        long cutoff,
        string? previousCheckpointId)
    {
        var refs = new List<AgentCompactionSourceReference>();

        if (!string.IsNullOrWhiteSpace(previousCheckpointId))
        {
            refs.Add(new AgentCompactionSourceReference(
                AgentCompactionSourceKind.Checkpoint,
                previousCheckpointId,
                Sequence: previousCovered));
        }

        var eligible = Enumerable.Range(0, session.Events.Count)
            .Where(index => index > previousCovered && index <= cutoff)
            .ToArray();

        var slots = CompactionManager.MaxSources - refs.Count;
        if (slots <= 0 || eligible.Length == 0)
            return refs;

        var selected = SelectRepresentativeIndexes(eligible, slots);
        foreach (var index in selected)
        {
            refs.Add(new AgentCompactionSourceReference(
                AgentCompactionSourceKind.JournalEvent,
                $"session:{session.Id:N}:event:{index}",
                Sequence: index));
        }

        return refs;
    }

    private static IReadOnlyList<int> SelectRepresentativeIndexes(
        IReadOnlyList<int> eligible,
        int max)
    {
        if (eligible.Count <= max)
            return eligible.ToArray();

        if (max == 1)
            return [eligible[^1]];

        var selected = new SortedSet<int>();
        selected.Add(eligible[0]);
        selected.Add(eligible[^1]);

        while (selected.Count < max)
        {
            var position = (double)(selected.Count - 1) / Math.Max(1, max - 1);
            var index = eligible[(int)Math.Round(position * (eligible.Count - 1))];
            selected.Add(index);

            if (selected.Count == max)
                break;

            foreach (var fallback in eligible)
            {
                if (selected.Add(fallback) && selected.Count == max)
                    break;
            }
        }

        return selected.ToArray();
    }

    private static string BuildSummary(
        global::H2AgentLab.LabSession session,
        long cutoff)
    {
        var covered = session.Events
            .Select((item, index) => (item, index))
            .Where(x => x.index <= cutoff)
            .ToArray();

        var users = covered.Count(x =>
            string.Equals(x.item.Kind, "user", StringComparison.OrdinalIgnoreCase));
        var assistants = covered.Count(x =>
            string.Equals(x.item.Kind, "assistant", StringComparison.OrdinalIgnoreCase));
        var tools = covered.Count(x =>
            x.item.Kind?.StartsWith("tool", StringComparison.OrdinalIgnoreCase) == true);

        return
            $"Historical session activity through journal event {cutoff} is compacted. "
            + $"Raw journal remains durable. Counts: user={users}; assistant={assistants}; tool={tools}. "
            + "Use the durable source references below for audit/recovery; recent un-compacted state is supplied separately.";
    }

    private RuntimeCompactionMarker? LoadMarker(Guid sessionId)
    {
        var path = MarkerPath(sessionId);
        if (!File.Exists(path))
            return null;
        if (new FileInfo(path).Length > 64 * 1024)
            throw new IOException("Runtime compaction marker exceeds safety limit.");

        var marker = JsonSerializer.Deserialize<RuntimeCompactionMarker>(
            File.ReadAllBytes(path))
            ?? throw new IOException("Runtime compaction marker is invalid.");

        _ = _compaction.Load(marker.CheckpointId);
        return marker;
    }

    private void SaveMarker(
        Guid sessionId,
        RuntimeCompactionMarker marker)
    {
        var root = Path.Combine(_stateRoot, "context-compaction");
        Directory.CreateDirectory(root);
        var path = MarkerPath(sessionId);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(marker));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private string MarkerPath(Guid sessionId)
        => Path.Combine(
            _stateRoot,
            "context-compaction",
            sessionId.ToString("N") + ".json");

    private sealed record RuntimeCompactionMarker(
        string CheckpointId,
        long CoveredThroughSequence,
        DateTime UpdatedUtc);
}

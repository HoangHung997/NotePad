using System.Text.Json;
using System.Text.RegularExpressions;

namespace H2AgentLab.Metrics;

public sealed record AgentTurnEvidence
{
    public int SchemaVersion { get; init; } = AgentTrace.SchemaVersion;
    public required Guid TaskId { get; init; }
    public required Guid TurnId { get; init; }
    public required DateTimeOffset StartedUtc { get; init; }
    public required DateTimeOffset SavedUtc { get; init; }
    public AgentVersionIdentifiers? Versions { get; init; }
    public required IReadOnlyList<PersistedAgentTraceEvent> Events { get; init; }
    public required AgentMetricsSnapshot Metrics { get; init; }
}

public sealed record PersistedAgentTraceEvent
{
    public required long Sequence { get; init; }
    public required AgentTraceKind Kind { get; init; }
    public required DateTimeOffset AtUtc { get; init; }
    public required double ElapsedMilliseconds { get; init; }
    public string Name { get; init; } = "";
}

/// <summary>
/// Persists bounded machine telemetry for a turn. It deliberately omits AgentTraceEvent.Detail,
/// prompt text, tool payloads, assistant reasoning and credentials. Detailed run/tool evidence
/// belongs in their existing stores and can be referenced by a safe ID instead of copied here.
/// </summary>
public static partial class AgentTraceStore
{
    public const int MaxEvidenceBytes = 2 * 1024 * 1024;

    public static string Save(string stateRoot, AgentTrace trace, AgentMetrics metrics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(metrics);
        var root = Path.GetFullPath(stateRoot);
        Directory.CreateDirectory(root);
        var traceRoot = Path.Combine(root, "traces");
        Directory.CreateDirectory(traceRoot);
        var events = trace.Snapshot().Select(e => new PersistedAgentTraceEvent
        {
            Sequence = e.Sequence,
            Kind = e.Kind,
            AtUtc = e.AtUtc,
            ElapsedMilliseconds = e.ElapsedMilliseconds,
            Name = SafeLabel(e.Name)
        }).ToArray();
        var evidence = new AgentTurnEvidence
        {
            TaskId = trace.TaskId,
            TurnId = trace.TurnId,
            StartedUtc = trace.StartedUtc,
            SavedUtc = DateTimeOffset.UtcNow,
            Versions = trace.Versions,
            Events = events,
            Metrics = metrics.Snapshot(trace)
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        if (bytes.Length > MaxEvidenceBytes)
            throw new InvalidOperationException("Agent turn telemetry exceeded the 2 MB persistence safety limit.");
        var target = Path.Combine(traceRoot, trace.TurnId.ToString("N") + ".json");
        AtomicWrite(target, bytes);
        return target;
    }

    private static void AtomicWrite(string target, byte[] bytes)
    {
        var folder = Path.GetDirectoryName(target) ?? throw new InvalidOperationException("Trace path has no parent folder.");
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, ".trace-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(true);
            }
            File.Move(temp, target, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static string SafeLabel(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var clipped = value.Length <= AgentTrace.MaxNameCharacters ? value : value[..AgentTrace.MaxNameCharacters];
        clipped = BearerSecret().Replace(clipped, "$1[redacted]");
        clipped = OpenAiStyleSecret().Replace(clipped, "[redacted-key]");
        return clipped;
    }

    [GeneratedRegex("(?i)(bearer\\s+)[A-Za-z0-9._~+\\-/=]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex BearerSecret();

    [GeneratedRegex("(?i)\\bsk-[A-Za-z0-9_-]{8,}\\b", RegexOptions.CultureInvariant)]
    private static partial Regex OpenAiStyleSecret();
}

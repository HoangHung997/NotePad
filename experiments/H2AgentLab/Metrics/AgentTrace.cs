using System.Diagnostics;
using System.Text.Json.Serialization;

namespace H2AgentLab.Metrics;

public enum AgentTraceKind
{
    Send,
    ContextReady,
    RequestStart,
    ConnectionReady,
    PrewarmStart,
    PrewarmFinish,
    FirstModelEvent,
    ToolStart,
    ToolFinish,
    Continuation,
    VerifierStart,
    VerifierFinish,
    Final,
    Error,
    Cancel
}

public sealed record AgentTraceEvent
{
    public required long Sequence { get; init; }
    public required AgentTraceKind Kind { get; init; }
    public required DateTimeOffset AtUtc { get; init; }
    public required double ElapsedMilliseconds { get; init; }
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
}

/// <summary>
/// Thread-safe, append-only trace for one user turn. Wall-clock time is useful for
/// persisted diagnostics while elapsed time comes from Stopwatch's monotonic clock,
/// so clock changes cannot create negative turn durations.
/// </summary>
public sealed class AgentTrace
{
    public const int SchemaVersion = 1;
    public const int MaxEvents = 4096;
    public const int MaxNameCharacters = 256;
    public const int MaxDetailCharacters = 4096;

    private readonly object _gate = new();
    private readonly List<AgentTraceEvent> _events = [];
    private readonly long _startedTimestamp = Stopwatch.GetTimestamp();
    private long _sequence;

    public AgentTrace(Guid? taskId = null, Guid? turnId = null, DateTimeOffset? startedUtc = null)
    {
        TaskId = taskId ?? Guid.NewGuid();
        TurnId = turnId ?? Guid.NewGuid();
        StartedUtc = startedUtc ?? DateTimeOffset.UtcNow;
    }

    public Guid TaskId { get; }
    public Guid TurnId { get; }
    public DateTimeOffset StartedUtc { get; }

    [JsonIgnore]
    public double ElapsedMilliseconds => Stopwatch.GetElapsedTime(_startedTimestamp).TotalMilliseconds;

    public AgentTraceEvent Mark(AgentTraceKind kind, string? name = null, string? detail = null)
    {
        var traceEvent = new AgentTraceEvent
        {
            Sequence = Interlocked.Increment(ref _sequence),
            Kind = kind,
            AtUtc = DateTimeOffset.UtcNow,
            ElapsedMilliseconds = ElapsedMilliseconds,
            Name = Clip(name, MaxNameCharacters),
            Detail = Clip(detail, MaxDetailCharacters)
        };
        lock (_gate)
        {
            if (_events.Count >= MaxEvents)
                throw new InvalidOperationException($"Agent trace exceeded the {MaxEvents} event safety limit.");
            _events.Add(traceEvent);
        }
        return traceEvent;
    }

    public IReadOnlyList<AgentTraceEvent> Snapshot()
    {
        lock (_gate) return _events.ToArray();
    }

    public AgentTraceEvent? First(AgentTraceKind kind)
    {
        lock (_gate) return _events.FirstOrDefault(e => e.Kind == kind);
    }

    public AgentTraceEvent? Last(AgentTraceKind kind)
    {
        lock (_gate) return _events.LastOrDefault(e => e.Kind == kind);
    }

    private static string Clip(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= max ? value : value[..max];
    }
}

using H2AgentLab.Tools;

namespace H2AgentLab.Tasking;

public sealed record CapabilityRefreshResult(
    long RegistryVersionBefore,
    long RegistryVersionAfter,
    IReadOnlyList<string> RefreshedSources,
    DateTime RefreshedUtc);

public interface IAgentCapabilityRefreshSource
{
    string SourceId { get; }
    long Version { get; }
    Task RefreshAsync(
        ToolRegistry registry,
        CancellationToken cancellationToken);
}

public sealed class AgentCapabilityRefreshCoordinator
{
    private readonly ToolRegistry _registry;
    private readonly List<IAgentCapabilityRefreshSource> _sources = [];
    private readonly Dictionary<string, long> _seenVersions = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private int _inFlightToolCalls;

    public AgentCapabilityRefreshCoordinator(ToolRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public void RegisterSource(IAgentCapabilityRefreshSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_sync)
        {
            if (_sources.Any(x => x.SourceId == source.SourceId))
                throw new InvalidOperationException(
                    $"Capability refresh source '{source.SourceId}' is already registered.");
            _sources.Add(source);
        }
    }

    public IDisposable EnterToolCall()
    {
        lock (_sync)
            _inFlightToolCalls++;
        return new ToolCallScope(this);
    }

    public async Task<CapabilityRefreshResult> RefreshAtTaskBoundaryAsync(
        CancellationToken cancellationToken)
    {
        IAgentCapabilityRefreshSource[] sources;
        long before;
        lock (_sync)
        {
            if (_inFlightToolCalls != 0)
                throw new InvalidOperationException(
                    "Capability refresh is forbidden while a tool call is in flight.");
            sources = _sources
                .OrderBy(x => x.SourceId, StringComparer.Ordinal)
                .ToArray();
            before = _registry.Version;
        }

        var refreshed = new List<string>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_seenVersions.TryGetValue(source.SourceId, out var seen)
                && seen == source.Version)
                continue;

            await source.RefreshAsync(_registry, cancellationToken).ConfigureAwait(false);
            _seenVersions[source.SourceId] = source.Version;
            refreshed.Add(source.SourceId);
        }

        return new CapabilityRefreshResult(
            before,
            _registry.Version,
            refreshed,
            DateTime.UtcNow);
    }

    private void LeaveToolCall()
    {
        lock (_sync)
        {
            if (_inFlightToolCalls <= 0)
                throw new InvalidOperationException("Capability tool-call boundary underflow.");
            _inFlightToolCalls--;
        }
    }

    private sealed class ToolCallScope : IDisposable
    {
        private AgentCapabilityRefreshCoordinator? _owner;

        public ToolCallScope(AgentCapabilityRefreshCoordinator owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.LeaveToolCall();
        }
    }
}

using H2AgentLab.Tools;

namespace H2AgentLab.Providers;

/// <summary>
/// Provider-neutral selected-schema adapter. Any ICapabilityProvider may lazily project selected
/// tool definitions into the authoritative ToolRegistry without AgentRuntime knowing provider type.
/// </summary>
public sealed class CapabilityProviderToolRegistryAdapter
{
    private readonly ToolRegistry _registry;
    private readonly object _gate = new();
    private readonly Dictionary<ICapabilityProvider, ProviderState> _states = new(ReferenceEqualityComparer.Instance);
    private sealed class ProviderState
    {
        public bool Revoked;
        public readonly Dictionary<string, long> Generations = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Calls = new(StringComparer.Ordinal);
    }
    private ProviderState State(ICapabilityProvider provider)
    {
        if (!_states.TryGetValue(provider, out var state)) _states.Add(provider, state = new());
        return state;
    }

    internal void Revoke(ICapabilityProvider provider, bool permanent)
    {
        lock (_gate)
        {
            var state = State(provider);
            if (state.Calls.Values.Any(n => n != 0))
                throw new InvalidOperationException("Provider tool is in flight; revoke at its safe boundary.");
            _registry.ReplaceWhere(d => d.Provenance?.ProviderId == provider.Provenance.ProviderId, [], () =>
            {
                foreach (var name in state.Generations.Keys.ToArray()) state.Generations[name]++;
                state.Revoked = permanent;
            });
        }
    }

    public CapabilityProviderToolRegistryAdapter(ToolRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public async Task<IReadOnlyList<ToolDescriptor>> LoadSelectedAsync(
        ICapabilityProvider provider,
        IReadOnlyList<string> selectedNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(selectedNames);

        var namesSnapshot = selectedNames.ToArray();
        var selected = namesSnapshot.ToHashSet(StringComparer.Ordinal);
        if (selected.Count != namesSnapshot.Length || selected.Count > 512 || selected.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Selected provider names must be bounded and unique.");
        var provenance = provider.Provenance;
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) if (State(provider).Revoked) throw new InvalidOperationException("Provider was unregistered.");
        var definitions = await provider.LoadToolDefinitionsAsync(
            namesSnapshot,
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (definitions.Count != selected.Count
            || definitions.Any(d => d is null || !selected.Contains(d.Summary.Name))
            || definitions.Select(d => d.Summary.Name).Distinct(StringComparer.Ordinal).Count() != definitions.Count)
            throw new InvalidDataException("Provider returned unselected or duplicate tool definitions.");

        lock (_gate)
        {
        var state = State(provider);
        if (state.Revoked || provider.Provenance != provenance)
            throw new InvalidOperationException("Provider identity changed during discovery.");
        if (selected.Any(name => state.Calls.GetValueOrDefault(name) != 0))
            throw new InvalidOperationException("Selected provider tool is in flight.");
        var descriptors = new List<ToolDescriptor>();
        foreach (var definition in definitions)
        {
            var summary = definition.Summary;
            var descriptor = new ToolDescriptor(
                summary.Name,
                new ToolNamespace(
                    summary.Namespace,
                    $"Capabilities provided by {provider.Provenance.ProviderId}."),
                summary.Description,
                summary.Risk,
                summary.Access,
                summary.SupportsParallel,
                summary.SchemaVersion,
                definition.CallableSchema,
                new ProviderRegistryExecutor(this, provider, provenance, state, summary.Name, state.Generations.GetValueOrDefault(summary.Name) + 1),
                provenance: new ToolProvenance(
                    provider.Provenance.ProviderId,
                    provider.Provenance.ProviderVersion,
                    provider.Provenance.ServerId,
                    summary.ToolVersion),
                resourceScope: new ToolResourceScope(
                    summary.ResourceScope,
                    summary.ResourceScope),
                serializationKey: summary.SerializationKey,
                canProvideVerificationEvidence: false,
                readiness: new(provider.Health.Status switch {
                    ProviderHealthStatus.Ready => ToolReadinessState.Ready,
                    ProviderHealthStatus.Degraded => ToolReadinessState.Degraded,
                    ProviderHealthStatus.Connecting => ToolReadinessState.Busy,
                    _ => ToolReadinessState.Unavailable }),
                dependencies: [provider.Provenance.ProviderId],
                readinessSnapshot: () => new(provider.Health.Status switch {
                    ProviderHealthStatus.Ready => ToolReadinessState.Ready,
                    ProviderHealthStatus.Degraded => ToolReadinessState.Degraded,
                    ProviderHealthStatus.Connecting => ToolReadinessState.Busy,
                    _ => ToolReadinessState.Unavailable }));

            descriptors.Add(descriptor);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var names = descriptors.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        _registry.ReplaceWhere(x => string.Equals(x.Provenance?.ProviderId,
            provenance.ProviderId, StringComparison.Ordinal) && names.Contains(x.Name), descriptors, () =>
            {
                foreach (var name in names) state.Generations[name] = state.Generations.GetValueOrDefault(name) + 1;
            });
        return descriptors;
        }
    }

    private sealed class ProviderRegistryExecutor(CapabilityProviderToolRegistryAdapter owner,
        ICapabilityProvider provider, ProviderProvenance provenance, ProviderState state, string name, long generation)
        : IAgentToolExecutor
    {
        public string ExecutorId => "provider." + provenance.ProviderId;
        public async ValueTask<string> ExecuteAsync(global::H2AgentLab.ToolCall call, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (owner._gate)
            {
                if (call.Name != name || state.Revoked || state.Generations.GetValueOrDefault(name) != generation
                    || provider.Provenance != provenance || provider.Health.Status is not (ProviderHealthStatus.Ready or ProviderHealthStatus.Degraded))
                    throw new InvalidOperationException("Provider binding is unavailable or requires rebinding.");
                state.Calls[name] = state.Calls.GetValueOrDefault(name) + 1;
            }
            try
            {
                return provider is IInvocationAwareCapabilityProvider aware
                    ? await aware.ExecuteToolAsync(name, call.Arguments, ToolInvocation.Bind(call), cancellationToken).ConfigureAwait(false)
                    : await provider.ExecuteToolAsync(name, call.Arguments, cancellationToken).ConfigureAwait(false);
            }
            finally { lock (owner._gate) state.Calls[name]--; }
        }
    }
}

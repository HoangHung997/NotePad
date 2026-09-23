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
        public long RevocationEpoch;
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
                // Removal also fences discovery admitted before this boundary, including
                // providers that have not published any descriptor yet.
                state.RevocationEpoch++;
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
        ProviderState state;
        long admittedEpoch;
        Dictionary<string, long> admittedGenerations;
        lock (_gate)
        {
            state = State(provider);
            if (state.Revoked) throw new InvalidOperationException("Provider was unregistered.");
            admittedEpoch = state.RevocationEpoch;
            admittedGenerations = selected.ToDictionary(name => name,
                name => state.Generations.GetValueOrDefault(name), StringComparer.Ordinal);
        }
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
            if (state.Revoked || state.RevocationEpoch != admittedEpoch || provider.Provenance != provenance
                || selected.Any(name => state.Generations.GetValueOrDefault(name) != admittedGenerations[name]))
                throw new InvalidOperationException("Provider binding changed during discovery; request a new explicit load.");
            if (selected.Any(name => state.Calls.GetValueOrDefault(name) != 0))
                throw new InvalidOperationException("Selected provider tool is in flight.");
            var descriptors = new List<ToolDescriptor>();
            foreach (var definition in definitions)
            {
                var summary = definition.Summary;
                var generation = state.Generations.GetValueOrDefault(summary.Name) + 1;
                var descriptor = new ToolDescriptor(
                    summary.Name,
                    new ToolNamespace(summary.Namespace, $"Capabilities provided by {provenance.ProviderId}."),
                    summary.Description,
                    summary.Risk,
                    summary.Access,
                    summary.SupportsParallel,
                    summary.SchemaVersion,
                    definition.CallableSchema,
                    new ProviderRegistryExecutor(this, provider, provenance, state, summary.Name, generation),
                    provenance: new ToolProvenance(provenance.ProviderId, provenance.ProviderVersion,
                        provenance.ServerId, summary.ToolVersion),
                    resourceScope: new ToolResourceScope(summary.ResourceScope, summary.ResourceScope),
                    serializationKey: summary.SerializationKey,
                    canProvideVerificationEvidence: false,
                    readiness: HealthReadiness(provider),
                    dependencies: [provenance.ProviderId],
                    readinessSnapshot: () => BindingReadiness(provider, provenance, state, summary.Name, generation));
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

    private static ToolReadiness HealthReadiness(ICapabilityProvider provider) => new(provider.Health.Status switch
    {
        ProviderHealthStatus.Ready => ToolReadinessState.Ready,
        ProviderHealthStatus.Degraded => ToolReadinessState.Degraded,
        ProviderHealthStatus.Connecting => ToolReadinessState.Busy,
        _ => ToolReadinessState.Unavailable
    });

    private ToolReadiness BindingReadiness(ICapabilityProvider provider, ProviderProvenance provenance,
        ProviderState state, string name, long generation)
    {
        lock (_gate)
        {
            if (state.Revoked || state.Generations.GetValueOrDefault(name) != generation
                || provider.Provenance != provenance)
                return new(ToolReadinessState.Unavailable);
            return HealthReadiness(provider);
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

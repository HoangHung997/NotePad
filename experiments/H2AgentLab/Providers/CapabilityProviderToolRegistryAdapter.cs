using H2AgentLab.Tools;

namespace H2AgentLab.Providers;

/// <summary>
/// Provider-neutral selected-schema adapter. Any ICapabilityProvider may lazily project selected
/// tool definitions into the authoritative ToolRegistry without AgentRuntime knowing provider type.
/// </summary>
public sealed class CapabilityProviderToolRegistryAdapter
{
    private readonly ToolRegistry _registry;

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

        var definitions = await provider.LoadToolDefinitionsAsync(
            selectedNames,
            cancellationToken).ConfigureAwait(false);

        _registry.UnregisterWhere(x =>
            string.Equals(
                x.Provenance?.ProviderId,
                provider.Provenance.ProviderId,
                StringComparison.Ordinal)
            && definitions.Any(d =>
                string.Equals(
                    d.Summary.Name,
                    x.Name,
                    StringComparison.Ordinal)));

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
                new ProviderRegistryExecutor(provider, summary.Name),
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

            _registry.Register(descriptor);
            descriptors.Add(descriptor);
        }

        return descriptors;
    }

    private sealed class ProviderRegistryExecutor : IAgentToolExecutor
    {
        private readonly ICapabilityProvider _provider;
        private readonly string _toolName;

        public ProviderRegistryExecutor(
            ICapabilityProvider provider,
            string toolName)
        {
            _provider = provider;
            _toolName = toolName;
        }

        public string ExecutorId
            => "provider." + _provider.Provenance.ProviderId;

        public ValueTask<string> ExecuteAsync(
            global::H2AgentLab.ToolCall call,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(
                    call.Name,
                    _toolName,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Provider executor expected '{_toolName}', received '{call.Name}'.");

            return _provider is IInvocationAwareCapabilityProvider aware
                ? aware.ExecuteToolAsync(_toolName, call.Arguments, ToolInvocation.Bind(call), cancellationToken)
                : _provider.ExecuteToolAsync(_toolName, call.Arguments, cancellationToken);
        }
    }
}

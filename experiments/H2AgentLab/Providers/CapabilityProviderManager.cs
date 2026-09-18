using H2AgentLab.Tools;

namespace H2AgentLab.Providers;

public sealed record ProviderExecutionEvidence(
    string ProviderId,
    string ServerId,
    string ToolName,
    string ToolVersion,
    string ScopeId,
    string ResultHash,
    bool TrustedAsVerificationEvidence);

public sealed class CapabilityProviderManager : IAsyncDisposable
{
    private readonly Dictionary<string, ICapabilityProvider> _providers = new(StringComparer.Ordinal);
    private readonly ToolRegistry _registry;
    private readonly CapabilityProviderToolRegistryAdapter _providerAdapter;

    public CapabilityProviderManager(ToolRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _providerAdapter = new CapabilityProviderToolRegistryAdapter(registry);
    }

    public IReadOnlyList<ProviderProvenance> Providers
        => _providers.Values
            .Select(x => x.Provenance)
            .OrderBy(x => x.ProviderId, StringComparer.Ordinal)
            .ToArray();

    public void Register(ICapabilityProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var id = Normalize(provider.Provenance.ProviderId);
        if (!_providers.TryAdd(id, provider))
            throw new InvalidOperationException($"Capability provider '{id}' is already registered.");
    }

    public async Task<IReadOnlyList<ProviderNamespaceSummary>> DiscoverNamespacesAsync(
        CancellationToken cancellationToken)
    {
        var summaries = new List<ProviderNamespaceSummary>();
        foreach (var provider in _providers.Values.OrderBy(x => x.Provenance.ProviderId, StringComparer.Ordinal))
        {
            if (provider.Health.Status != ProviderHealthStatus.Ready)
                await provider.ConnectAsync(cancellationToken).ConfigureAwait(false);
            summaries.AddRange(await provider.ListNamespacesAsync(cancellationToken).ConfigureAwait(false));
        }

        return summaries
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .Select(group => new ProviderNamespaceSummary(
                group.Key,
                string.Join(
                    " | ",
                    group.Select(x => x.Description)
                        .Distinct(StringComparer.Ordinal)
                        .Take(4)),
                group.SelectMany(x => x.CapabilityKeywords)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .Take(64)
                    .ToArray()))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<IReadOnlyList<ToolDescriptor>> LoadProviderToolsAsync(
        string providerId,
        IReadOnlyList<string> selectedNames,
        CancellationToken cancellationToken)
    {
        var provider = Provider(providerId);

        if (provider.Health.Status != ProviderHealthStatus.Ready)
            await provider.ConnectAsync(cancellationToken).ConfigureAwait(false);

        return await _providerAdapter.LoadSelectedAsync(
            provider,
            selectedNames,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ToolDescriptor>> LoadMcpToolsAsync(
        string providerId,
        IReadOnlyList<string> selectedNames,
        CancellationToken cancellationToken)
    {
        var provider = Provider(providerId);
        if (provider is not McpToolProvider)
            throw new InvalidOperationException(
                $"Provider '{providerId}' is not an MCP tool provider.");

        return await LoadProviderToolsAsync(
            providerId,
            selectedNames,
            cancellationToken).ConfigureAwait(false);
    }

    public ProviderExecutionEvidence BuildExecutionEvidence(
        ToolDescriptor descriptor,
        string resultHash)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultHash);
        var provenance = descriptor.Provenance
            ?? throw new InvalidOperationException("Tool has no provider provenance.");
        var scope = descriptor.ResourceScope
            ?? throw new InvalidOperationException("Provider tool has no resource scope.");

        return new ProviderExecutionEvidence(
            provenance.ProviderId,
            provenance.ServerId ?? "",
            descriptor.Name,
            provenance.ToolVersion,
            scope.ScopeId,
            resultHash.Trim().ToLowerInvariant(),
            TrustedAsVerificationEvidence: descriptor.CanProvideVerificationEvidence);
    }

    public void RemoveProviderTools(string providerId)
    {
        var normalized = Normalize(providerId);
        _registry.UnregisterWhere(x =>
            string.Equals(
                x.Provenance?.ProviderId,
                normalized,
                StringComparison.Ordinal));
    }

    private ICapabilityProvider Provider(string providerId)
    {
        var normalized = Normalize(providerId);
        return _providers.TryGetValue(normalized, out var provider)
            ? provider
            : throw new KeyNotFoundException($"Capability provider '{normalized}' is not registered.");
    }

    private static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 64
            || normalized.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException("Provider ID is invalid.", nameof(value));
        return normalized;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers.Values)
            await provider.DisposeAsync().ConfigureAwait(false);
        _providers.Clear();
    }
}

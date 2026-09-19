using H2AgentLab.Providers;
using H2AgentLab.Skills;
using H2AgentLab.Tools;
using H2AgentLab.Verification;

namespace H2AgentLab.Extensions;

public sealed record AgentExtensionMetadata(
    string ExtensionId,
    string Version,
    string DisplayName,
    string? Description = null,
    IReadOnlyDictionary<string, string>? Properties = null)
{
    public AgentExtensionMetadata Normalize()
    {
        var id = ToolNamespace.NormalizeId(ExtensionId, nameof(ExtensionId));
        var version = ToolNamespace.NormalizeId(Version, nameof(Version));
        var display = ToolNamespace.NormalizeText(DisplayName, nameof(DisplayName), 256);
        var description = string.IsNullOrWhiteSpace(Description)
            ? null
            : ToolNamespace.NormalizeText(Description, nameof(Description), 1_000);

        var properties = (Properties ?? new Dictionary<string, string>())
            .ToDictionary(
                pair => ToolNamespace.NormalizeId(pair.Key, nameof(Properties)),
                pair => ToolNamespace.NormalizeText(pair.Value, nameof(Properties), 1_000),
                StringComparer.Ordinal);

        return this with
        {
            ExtensionId = id,
            Version = version,
            DisplayName = display,
            Description = description,
            Properties = properties
        };
    }
}

public interface IAgentExtension
{
    AgentExtensionMetadata Metadata { get; }

    void Register(AgentExtensionRegistration registration);
}

/// <summary>
/// Narrow registration surface exposed to extensions. AgentRuntime remains unaware of concrete
/// application families and continues to depend only on ToolRegistry/runtime abstractions.
/// </summary>
internal sealed class AgentExtensionContributions
{
    public List<string> ToolNames { get; } = [];
    public List<string> SkillSourceIds { get; } = [];
    public List<string> VerifierIds { get; } = [];
    public List<string> ProviderIds { get; } = [];
}

public sealed class AgentExtensionRegistration
{
    private readonly ToolRegistry _tools;
    private readonly H2AgentLab.Skills.SkillCatalog _skills;
    private readonly ArtifactVerifierRegistry _verifiers;
    private readonly CapabilityProviderManager _providers;
    private readonly AgentExtensionContributions _contributions;

    internal AgentExtensionRegistration(
        ToolRegistry tools,
        H2AgentLab.Skills.SkillCatalog skills,
        ArtifactVerifierRegistry verifiers,
        CapabilityProviderManager providers,
        AgentExtensionContributions contributions)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _skills = skills ?? throw new ArgumentNullException(nameof(skills));
        _verifiers = verifiers ?? throw new ArgumentNullException(nameof(verifiers));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _contributions = contributions ?? throw new ArgumentNullException(nameof(contributions));
    }

    public void RegisterTool(ToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _tools.Register(descriptor);
        _contributions.ToolNames.Add(descriptor.Name);
    }

    public void RegisterSkillSource(ISkillSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _skills.Register(source);
        _contributions.SkillSourceIds.Add(source.SourceId);
    }

    public void RegisterVerifier(IArtifactVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        _verifiers.Register(verifier);
        _contributions.VerifierIds.Add(verifier.VerifierId);
    }

    public void RegisterProvider(ICapabilityProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers.Register(provider);
        _contributions.ProviderIds.Add(provider.Provenance.ProviderId);
    }
}

public sealed record RegisteredAgentExtension(
    AgentExtensionMetadata Metadata,
    DateTime RegisteredUtc);

/// <summary>
/// Host-owned extension bus. It coordinates registration only; lifecycle/trust/execution rules
/// remain owned by the authoritative registries/managers supplied by the host.
/// </summary>
public sealed class AgentExtensionRegistry
{
    private readonly ToolRegistry _tools;
    private readonly H2AgentLab.Skills.SkillCatalog _skills;
    private readonly ArtifactVerifierRegistry _verifiers;
    private readonly CapabilityProviderManager _providers;
    private readonly Dictionary<string, RegisteredAgentExtension> _extensions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentExtensionContributions> _contributions =
        new(StringComparer.Ordinal);

    public AgentExtensionRegistry(
        ToolRegistry tools,
        H2AgentLab.Skills.SkillCatalog skills,
        ArtifactVerifierRegistry verifiers,
        CapabilityProviderManager providers)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _skills = skills ?? throw new ArgumentNullException(nameof(skills));
        _verifiers = verifiers ?? throw new ArgumentNullException(nameof(verifiers));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    }

    public IReadOnlyList<RegisteredAgentExtension> Extensions
        => _extensions.Values
            .OrderBy(x => x.Metadata.ExtensionId, StringComparer.Ordinal)
            .ToArray();

    public RegisteredAgentExtension Register(IAgentExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        var metadata = (extension.Metadata
            ?? throw new InvalidOperationException("Extension metadata is required."))
            .Normalize();

        if (_extensions.ContainsKey(metadata.ExtensionId))
            throw new InvalidOperationException(
                $"Extension '{metadata.ExtensionId}' is already registered.");

        var contributions = new AgentExtensionContributions();
        var registration = new AgentExtensionRegistration(
            _tools,
            _skills,
            _verifiers,
            _providers,
            contributions);
        extension.Register(registration);

        var registered = new RegisteredAgentExtension(
            metadata,
            DateTime.UtcNow);
        _extensions.Add(metadata.ExtensionId, registered);
        _contributions.Add(metadata.ExtensionId, contributions);
        return registered;
    }

    public Task<bool> DisableAsync(
        string extensionId,
        CancellationToken cancellationToken = default)
        => RemoveAsync(extensionId, cancellationToken);

    public Task<bool> UnregisterAsync(
        string extensionId,
        CancellationToken cancellationToken = default)
        => RemoveAsync(extensionId, cancellationToken);

    private async Task<bool> RemoveAsync(
        string extensionId,
        CancellationToken cancellationToken)
    {
        var normalized = ToolNamespace.NormalizeId(
            extensionId,
            nameof(extensionId));
        if (!_extensions.ContainsKey(normalized)
            || !_contributions.TryGetValue(normalized, out var contributions))
            return false;

        foreach (var providerId in contributions.ProviderIds
            .Distinct(StringComparer.Ordinal))
        {
            await _providers.UnregisterProviderAsync(
                providerId,
                cancellationToken).ConfigureAwait(false);
        }

        var toolNames = contributions.ToolNames
            .ToHashSet(StringComparer.Ordinal);
        _tools.UnregisterWhere(x => toolNames.Contains(x.Name));

        foreach (var sourceId in contributions.SkillSourceIds
            .Distinct(StringComparer.Ordinal))
            _skills.UnregisterSource(sourceId);

        foreach (var verifierId in contributions.VerifierIds
            .Distinct(StringComparer.Ordinal))
            _verifiers.Unregister(verifierId);

        _contributions.Remove(normalized);
        _extensions.Remove(normalized);
        return true;
    }
}

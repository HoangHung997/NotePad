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
public sealed class AgentExtensionRegistration
{
    private readonly ToolRegistry _tools;
    private readonly H2AgentLab.Skills.SkillCatalog _skills;
    private readonly ArtifactVerifierRegistry _verifiers;
    private readonly CapabilityProviderManager _providers;

    internal AgentExtensionRegistration(
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

    public void RegisterTool(ToolDescriptor descriptor)
        => _tools.Register(descriptor);

    public void RegisterSkillSource(ISkillSource source)
        => _skills.Register(source);

    public void RegisterVerifier(IArtifactVerifier verifier)
        => _verifiers.Register(verifier);

    public void RegisterProvider(ICapabilityProvider provider)
        => _providers.Register(provider);
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
    private readonly AgentExtensionRegistration _registration;
    private readonly Dictionary<string, RegisteredAgentExtension> _extensions =
        new(StringComparer.Ordinal);

    public AgentExtensionRegistry(
        ToolRegistry tools,
        H2AgentLab.Skills.SkillCatalog skills,
        ArtifactVerifierRegistry verifiers,
        CapabilityProviderManager providers)
    {
        _registration = new AgentExtensionRegistration(
            tools,
            skills,
            verifiers,
            providers);
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

        extension.Register(_registration);

        var registered = new RegisteredAgentExtension(
            metadata,
            DateTime.UtcNow);
        _extensions.Add(metadata.ExtensionId, registered);
        return registered;
    }
}

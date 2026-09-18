using H2AgentLab.Extensions;
using H2AgentLab.Office;
using H2AgentLab.Tools;

namespace H2AgentLab.FirstPartyExtensions.Office;

public sealed class OfficeFirstPartyExtension : IAgentExtension
{
    private readonly IAgentToolExecutor _executor;
    private readonly string _providerVersion;

    public OfficeFirstPartyExtension(
        IAgentToolExecutor executor,
        string providerVersion = "1.0.0")
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _providerVersion = ToolNamespace.NormalizeId(
            providerVersion,
            nameof(providerVersion));
    }

    public AgentExtensionMetadata Metadata => new(
        "office-first-party-extension",
        _providerVersion,
        "Office Extension",
        "Reference card over the existing structured Word/Excel capability catalog.");

    public void Register(AgentExtensionRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var temporary = new ToolRegistry();
        var descriptors = StructuredOfficeCapabilityCatalog.RegisterInto(
            temporary,
            _executor,
            _providerVersion);
        foreach (var descriptor in descriptors)
            registration.RegisterTool(descriptor);
    }
}

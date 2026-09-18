using H2AgentLab.Extensions;
using H2AgentLab.Web;

namespace H2AgentLab.FirstPartyExtensions.Web;

public sealed class WebFirstPartyExtension : IAgentExtension
{
    public WebFirstPartyExtension(WebResearchHost provider)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public WebResearchHost Provider { get; }

    public AgentExtensionMetadata Metadata => new(
        "web-first-party-extension",
        Provider.Provenance.ProviderVersion,
        "Web Research Extension",
        "Reference card over the existing WebResearchHost provider.");

    public void Register(AgentExtensionRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        registration.RegisterProvider(Provider);
    }
}

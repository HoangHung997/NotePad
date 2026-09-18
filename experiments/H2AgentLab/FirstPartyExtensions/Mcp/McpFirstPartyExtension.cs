using H2AgentLab.Extensions;
using H2AgentLab.Providers;

namespace H2AgentLab.FirstPartyExtensions.Mcp;

public sealed class McpFirstPartyExtension : IAgentExtension
{
    public McpFirstPartyExtension(McpToolProvider provider)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public McpToolProvider Provider { get; }

    public AgentExtensionMetadata Metadata => new(
        "mcp-first-party-extension",
        Provider.Provenance.ProviderVersion,
        "MCP Extension",
        "Reference card over an existing MCP capability provider.");

    public void Register(AgentExtensionRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        registration.RegisterProvider(Provider);
    }
}

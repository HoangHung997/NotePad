using H2AgentLab.Extensions;
using H2AgentLab.Tools;

namespace H2AgentLab.FirstPartyExtensions.Computer;

public sealed class BrowserComputerExtension : ComputerCapabilityExtensionBase
{
    private static readonly ComputerToolCard[] Definitions =
    [
        Write("browser.navigate", "browser", "Fallback browser navigation when structured WebResearchHost is insufficient.", AgentToolRisk.Medium, ToolInteractionFidelity.Accessibility),
        Read("browser.inspect", "browser", "Fallback browser observation through approved browser adapter.", false, ToolInteractionFidelity.Accessibility),
        Write("browser.click", "browser", "Fallback browser click against current browser state.", AgentToolRisk.High, ToolInteractionFidelity.Accessibility),
        Write("browser.type", "browser", "Fallback browser typing against current browser state.", AgentToolRisk.High, ToolInteractionFidelity.Accessibility),
        Write("browser.download", "browser", "Fallback browser download with destination/scope policy.", AgentToolRisk.High, ToolInteractionFidelity.Accessibility),
        Read("browser.wait", "browser", "Wait for bounded browser state transition.", false, ToolInteractionFidelity.Accessibility)
    ];

    public BrowserComputerExtension(IAgentToolExecutor executor) : base(executor) { }

    public override AgentExtensionMetadata Metadata { get; } = new(
        "browser-computer-extension",
        "1.0.0",
        "Browser Computer Extension",
        "Registers browser fallback interaction tools.");

    protected override string ProviderId => "firstparty.computer.browser";
    protected override IReadOnlyList<ComputerToolCard> Cards => Definitions;
}

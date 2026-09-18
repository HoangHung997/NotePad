using H2AgentLab.Extensions;
using H2AgentLab.Tools;

namespace H2AgentLab.FirstPartyExtensions.Computer;

public sealed class DesktopComputerExtension : ComputerCapabilityExtensionBase
{
    private static readonly ComputerToolCard[] Definitions =
    [
        Read("app.list_running_apps", "app", "List safe running desktop applications.", true, ToolInteractionFidelity.Accessibility),
        Read("app.get_active_app", "app", "Read foreground safe application identity.", false, ToolInteractionFidelity.Accessibility),
        Write("app.launch", "app", "Launch an explicitly allowed application.", AgentToolRisk.High, ToolInteractionFidelity.Accessibility),
        Write("app.activate", "app", "Activate a previously observed safe application.", AgentToolRisk.Medium, ToolInteractionFidelity.Accessibility),
        Read("app.wait_for_window", "app", "Wait for an allowed application window to appear.", false, ToolInteractionFidelity.Accessibility),
        Read("window.enumerate", "window", "Enumerate safe windows through DesktopHost.", true, ToolInteractionFidelity.Accessibility),
        Read("window.get_bounds", "window", "Read observed window bounds/DPI.", true, ToolInteractionFidelity.Accessibility),
        Read("window.get_title", "window", "Read observed safe window title.", true, ToolInteractionFidelity.Accessibility),
        Read("window.get_process", "window", "Read observed safe window process identity.", true, ToolInteractionFidelity.Accessibility),
        Write("window.activate", "window", "Activate a previously observed safe window.", AgentToolRisk.Medium, ToolInteractionFidelity.Accessibility),
        Read("window.observe", "window", "Observe screenshot/UIA/state token for a safe window.", false, ToolInteractionFidelity.Accessibility),
        Read("uia.inspect_tree", "uia", "Read bounded compact UI Automation tree.", false, ToolInteractionFidelity.Accessibility),
        Read("uia.find_element", "uia", "Find an element inside the latest observed UIA state.", false, ToolInteractionFidelity.Accessibility),
        Write("uia.invoke", "uia", "Invoke a fresh observed UIA element token.", AgentToolRisk.High, ToolInteractionFidelity.Accessibility),
        Write("uia.set_value", "uia", "Set a non-password fresh observed UIA value.", AgentToolRisk.High, ToolInteractionFidelity.Accessibility),
        Write("uia.select", "uia", "Select a fresh observed UIA element.", AgentToolRisk.Medium, ToolInteractionFidelity.Accessibility),
        Write("uia.expand", "uia", "Expand a fresh observed UIA element.", AgentToolRisk.Medium, ToolInteractionFidelity.Accessibility),
        Write("input.click", "input", "Click only against current observed state/token/bounds.", AgentToolRisk.High, ToolInteractionFidelity.Accessibility),
        Write("input.double_click", "input", "Double-click only against current observed state/token/bounds.", AgentToolRisk.High, ToolInteractionFidelity.Accessibility),
        Write("input.type", "input", "Type/set text only against current observed safe state.", AgentToolRisk.High, ToolInteractionFidelity.Accessibility),
        Write("input.key", "input", "Send a bounded safe key/chord to a current observed window.", AgentToolRisk.High, ToolInteractionFidelity.Accessibility),
        Write("input.chord", "input", "Send a bounded safe chord to a current observed window.", AgentToolRisk.High, ToolInteractionFidelity.Accessibility),
        Write("input.scroll", "input", "Scroll inside current observed window state.", AgentToolRisk.Medium, ToolInteractionFidelity.Accessibility),
        Write("input.drag", "input", "Drag from current observed state with stale-state protection.", AgentToolRisk.High, ToolInteractionFidelity.Accessibility),
        Read("screen.capture", "screen", "Capture bounded pixels for a current safe observed window.", false, ToolInteractionFidelity.Visual),
        Read("screen.inspect_region", "screen", "Inspect a bounded region from a current screenshot.", false, ToolInteractionFidelity.Visual),
        Read("screen.observe_after_action", "screen", "Create post-mutation observation/evidence.", false, ToolInteractionFidelity.Visual)
    ];

    public DesktopComputerExtension(IAgentToolExecutor executor) : base(executor) { }

    public override AgentExtensionMetadata Metadata { get; } = new(
        "desktop-computer-extension",
        "1.0.0",
        "Desktop Computer Extension",
        "Registers application/window/UIA/input/screen tools.");

    protected override string ProviderId => "firstparty.computer.desktop";
    protected override IReadOnlyList<ComputerToolCard> Cards => Definitions;
}

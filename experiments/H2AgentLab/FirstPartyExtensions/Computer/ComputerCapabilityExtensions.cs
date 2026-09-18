using System.Text.Json;
using H2AgentLab.Extensions;
using H2AgentLab.Tools;

namespace H2AgentLab.FirstPartyExtensions.Computer;

internal sealed record ComputerToolCard(
    string Name,
    string Namespace,
    string Description,
    AgentToolAccess Access,
    AgentToolRisk Risk,
    bool SupportsParallel,
    ToolInteractionFidelity InteractionFidelity);

public abstract class ComputerCapabilityExtensionBase : IAgentExtension
{
    private readonly IAgentToolExecutor _executor;

    protected ComputerCapabilityExtensionBase(IAgentToolExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public abstract AgentExtensionMetadata Metadata { get; }
    protected abstract string ProviderId { get; }
    protected abstract IReadOnlyList<ComputerToolCard> Cards { get; }

    public void Register(AgentExtensionRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        foreach (var card in Cards)
            registration.RegisterTool(BuildDescriptor(card));
    }

    private ToolDescriptor BuildDescriptor(ComputerToolCard card)
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "function",
            function = new
            {
                name = card.Name,
                description = card.Description,
                parameters = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = true
                }
            }
        });

        var scopeId = "computer." + card.Namespace;
        return new ToolDescriptor(
            card.Name,
            new ToolNamespace(
                card.Namespace,
                NamespaceDescription(card.Namespace)),
            card.Description,
            card.Risk,
            card.Access,
            card.SupportsParallel,
            schemaVersion: "v1",
            callableSchema: schema,
            executor: _executor,
            provenance: new ToolProvenance(
                ProviderId,
                Metadata.Version,
                "local",
                "v1"),
            resourceScope: card.Access == AgentToolAccess.Mutating
                ? new ToolResourceScope(scopeId, scopeId)
                : null,
            serializationKey: card.Access == AgentToolAccess.Mutating
                ? scopeId
                : card.Namespace,
            canProvideVerificationEvidence: false,
            preference: new ToolPreferenceMetadata(
                "computer-interaction",
                card.InteractionFidelity));
    }

    private static string NamespaceDescription(string ns)
        => ns switch
        {
            "filesystem" => "Approved workspace filesystem capabilities.",
            "process" => "Bounded process lifecycle capabilities.",
            "shell" => "Bounded allow-listed shell/build/test capabilities.",
            "app" => "Observed desktop application lifecycle capabilities.",
            "window" => "Observed desktop window capabilities.",
            "uia" => "Structured UI Automation capabilities.",
            "input" => "State-guarded desktop input capabilities.",
            "screen" => "Bounded screenshot and pixel-observation capabilities.",
            "browser" => "Browser fallback interaction capabilities.",
            _ => "Registered computer capability namespace."
        };

    protected static ComputerToolCard Read(
        string name,
        string ns,
        string description,
        bool parallel,
        ToolInteractionFidelity fidelity)
        => new(
            name,
            ns,
            description,
            AgentToolAccess.ReadOnly,
            AgentToolRisk.Low,
            parallel,
            fidelity);

    protected static ComputerToolCard Write(
        string name,
        string ns,
        string description,
        AgentToolRisk risk,
        ToolInteractionFidelity fidelity)
        => new(
            name,
            ns,
            description,
            AgentToolAccess.Mutating,
            risk,
            SupportsParallel: false,
            fidelity);
}

public sealed class FileSystemComputerExtension : ComputerCapabilityExtensionBase
{
    private static readonly ComputerToolCard[] Definitions =
    [
        Read("filesystem.list", "filesystem", "List supported paths inside the approved workspace.", true, ToolInteractionFidelity.Structured),
        Read("filesystem.stat", "filesystem", "Read bounded metadata/hash for a workspace path.", true, ToolInteractionFidelity.Structured),
        Read("filesystem.search", "filesystem", "Search workspace paths by bounded literal name query.", true, ToolInteractionFidelity.Structured),
        Read("filesystem.read", "filesystem", "Read a bounded approved workspace file.", true, ToolInteractionFidelity.Structured),
        Write("filesystem.write", "filesystem", "Write a hash-guarded approved workspace file.", AgentToolRisk.Medium, ToolInteractionFidelity.Structured),
        Write("filesystem.copy", "filesystem", "Copy an approved workspace file with destination hash policy.", AgentToolRisk.Medium, ToolInteractionFidelity.Structured),
        Write("filesystem.move", "filesystem", "Move an approved workspace file after source/destination hash validation.", AgentToolRisk.High, ToolInteractionFidelity.Structured),
        Write("filesystem.delete_with_policy", "filesystem", "Delete only an approved hash-validated workspace file with backup.", AgentToolRisk.High, ToolInteractionFidelity.Structured),
        Read("filesystem.hash", "filesystem", "Hash an approved workspace file.", true, ToolInteractionFidelity.Structured),
        Read("filesystem.watch", "filesystem", "Produce/recompare bounded workspace snapshot tokens.", false, ToolInteractionFidelity.Structured)
    ];

    public FileSystemComputerExtension(IAgentToolExecutor executor)
        : base(executor)
    {
    }

    public override AgentExtensionMetadata Metadata { get; } = new(
        "filesystem-computer-extension",
        "1.0.0",
        "FileSystem Computer Extension",
        "Registers approved workspace filesystem tools.");

    protected override string ProviderId => "firstparty.computer.filesystem";
    protected override IReadOnlyList<ComputerToolCard> Cards => Definitions;
}

public sealed class ProcessShellComputerExtension : ComputerCapabilityExtensionBase
{
    private static readonly ComputerToolCard[] Definitions =
    [
        Read("process.list", "process", "List bounded process metadata without command lines or secrets.", false, ToolInteractionFidelity.Structured),
        Write("process.start", "process", "Start an allow-listed executable with separated arguments.", AgentToolRisk.High, ToolInteractionFidelity.Structured),
        Read("process.wait", "process", "Wait for a host-started process and read exit status.", true, ToolInteractionFidelity.Structured),
        Write("process.terminate_with_policy", "process", "Terminate only a process started by this host when policy allows.", AgentToolRisk.High, ToolInteractionFidelity.Structured),
        Read("process.read_exit_status", "process", "Read exit status for a host-started process.", true, ToolInteractionFidelity.Structured),

        Write("shell.run_bounded", "shell", "Run an allow-listed executable with separate bounded arguments/output/timeout.", AgentToolRisk.High, ToolInteractionFidelity.Structured),
        Write("shell.run_build", "shell", "Run bounded dotnet build for an approved workspace project.", AgentToolRisk.Medium, ToolInteractionFidelity.Structured),
        Write("shell.run_test", "shell", "Run bounded dotnet test for an approved workspace project.", AgentToolRisk.Medium, ToolInteractionFidelity.Structured),
        Write("shell.run_script", "shell", "Run an approved workspace script through an allow-listed interpreter.", AgentToolRisk.High, ToolInteractionFidelity.Structured)
    ];

    public ProcessShellComputerExtension(IAgentToolExecutor executor)
        : base(executor)
    {
    }

    public override AgentExtensionMetadata Metadata { get; } = new(
        "process-shell-computer-extension",
        "1.0.0",
        "Process/Shell Computer Extension",
        "Registers bounded process and shell tools.");

    protected override string ProviderId => "firstparty.computer.process-shell";
    protected override IReadOnlyList<ComputerToolCard> Cards => Definitions;
}

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

    public DesktopComputerExtension(IAgentToolExecutor executor)
        : base(executor)
    {
    }

    public override AgentExtensionMetadata Metadata { get; } = new(
        "desktop-computer-extension",
        "1.0.0",
        "Desktop Computer Extension",
        "Registers application/window/UIA/input/screen tools.");

    protected override string ProviderId => "firstparty.computer.desktop";
    protected override IReadOnlyList<ComputerToolCard> Cards => Definitions;
}

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

    public BrowserComputerExtension(IAgentToolExecutor executor)
        : base(executor)
    {
    }

    public override AgentExtensionMetadata Metadata { get; } = new(
        "browser-computer-extension",
        "1.0.0",
        "Browser Computer Extension",
        "Registers browser fallback interaction tools.");

    protected override string ProviderId => "firstparty.computer.browser";
    protected override IReadOnlyList<ComputerToolCard> Cards => Definitions;
}

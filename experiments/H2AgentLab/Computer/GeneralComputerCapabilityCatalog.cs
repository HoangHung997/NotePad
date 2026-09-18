using H2AgentLab.Tools;

namespace H2AgentLab.Computer;

public enum ComputerCapabilityBacking
{
    BuiltInWorkspace = 0,
    BoundedProcessHost = 1,
    DesktopHost = 2,
    WebResearchOrBrowserFallback = 3
}

public sealed record ComputerCapabilityDefinition(
    string Name,
    string Namespace,
    string Description,
    AgentToolAccess Access,
    AgentToolRisk Risk,
    bool SupportsParallel,
    ComputerCapabilityBacking Backing);

public static class GeneralComputerCapabilityCatalog
{
    private static readonly ComputerCapabilityDefinition[] Definitions =
    [
        Read("filesystem.list", "filesystem", "List supported paths inside the approved workspace.", true, ComputerCapabilityBacking.BuiltInWorkspace),
        Read("filesystem.stat", "filesystem", "Read bounded metadata/hash for a workspace path.", true, ComputerCapabilityBacking.BuiltInWorkspace),
        Read("filesystem.search", "filesystem", "Search workspace paths by bounded literal name query.", true, ComputerCapabilityBacking.BuiltInWorkspace),
        Read("filesystem.read", "filesystem", "Read a bounded approved workspace file.", true, ComputerCapabilityBacking.BuiltInWorkspace),
        Write("filesystem.write", "filesystem", "Write a hash-guarded approved workspace file.", AgentToolRisk.Medium, ComputerCapabilityBacking.BuiltInWorkspace),
        Write("filesystem.copy", "filesystem", "Copy an approved workspace file with destination hash policy.", AgentToolRisk.Medium, ComputerCapabilityBacking.BuiltInWorkspace),
        Write("filesystem.move", "filesystem", "Move an approved workspace file after source/destination hash validation.", AgentToolRisk.High, ComputerCapabilityBacking.BuiltInWorkspace),
        Write("filesystem.delete_with_policy", "filesystem", "Delete only an approved hash-validated workspace file with backup.", AgentToolRisk.High, ComputerCapabilityBacking.BuiltInWorkspace),
        Read("filesystem.hash", "filesystem", "Hash an approved workspace file.", true, ComputerCapabilityBacking.BuiltInWorkspace),
        Read("filesystem.watch", "filesystem", "Produce/recompare bounded workspace snapshot tokens.", false, ComputerCapabilityBacking.BuiltInWorkspace),

        Read("process.list", "process", "List bounded process metadata without command lines or secrets.", false, ComputerCapabilityBacking.BoundedProcessHost),
        Write("process.start", "process", "Start an allow-listed executable with separated arguments.", AgentToolRisk.High, ComputerCapabilityBacking.BoundedProcessHost),
        Read("process.wait", "process", "Wait for a host-started process and read exit status.", true, ComputerCapabilityBacking.BoundedProcessHost),
        Write("process.terminate_with_policy", "process", "Terminate only a process started by this host when policy allows.", AgentToolRisk.High, ComputerCapabilityBacking.BoundedProcessHost),
        Read("process.read_exit_status", "process", "Read exit status for a host-started process.", true, ComputerCapabilityBacking.BoundedProcessHost),

        Write("shell.run_bounded", "shell", "Run an allow-listed executable with separate bounded arguments/output/timeout.", AgentToolRisk.High, ComputerCapabilityBacking.BoundedProcessHost),
        Write("shell.run_build", "shell", "Run bounded dotnet build for an approved workspace project.", AgentToolRisk.Medium, ComputerCapabilityBacking.BoundedProcessHost),
        Write("shell.run_test", "shell", "Run bounded dotnet test for an approved workspace project.", AgentToolRisk.Medium, ComputerCapabilityBacking.BoundedProcessHost),
        Write("shell.run_script", "shell", "Run an approved workspace script through an allow-listed interpreter.", AgentToolRisk.High, ComputerCapabilityBacking.BoundedProcessHost),

        Read("app.list_running_apps", "app", "List safe running desktop applications.", true, ComputerCapabilityBacking.DesktopHost),
        Read("app.get_active_app", "app", "Read foreground safe application identity.", false, ComputerCapabilityBacking.DesktopHost),
        Write("app.launch", "app", "Launch an explicitly allowed application.", AgentToolRisk.High, ComputerCapabilityBacking.BoundedProcessHost),
        Write("app.activate", "app", "Activate a previously observed safe application.", AgentToolRisk.Medium, ComputerCapabilityBacking.DesktopHost),
        Read("app.wait_for_window", "app", "Wait for an allowed application window to appear.", false, ComputerCapabilityBacking.DesktopHost),

        Read("window.enumerate", "window", "Enumerate safe windows through DesktopHost.", true, ComputerCapabilityBacking.DesktopHost),
        Read("window.get_bounds", "window", "Read observed window bounds/DPI.", true, ComputerCapabilityBacking.DesktopHost),
        Read("window.get_title", "window", "Read observed safe window title.", true, ComputerCapabilityBacking.DesktopHost),
        Read("window.get_process", "window", "Read observed safe window process identity.", true, ComputerCapabilityBacking.DesktopHost),
        Write("window.activate", "window", "Activate a previously observed safe window.", AgentToolRisk.Medium, ComputerCapabilityBacking.DesktopHost),
        Read("window.observe", "window", "Observe screenshot/UIA/state token for a safe window.", false, ComputerCapabilityBacking.DesktopHost),

        Read("uia.inspect_tree", "uia", "Read bounded compact UI Automation tree.", false, ComputerCapabilityBacking.DesktopHost),
        Read("uia.find_element", "uia", "Find an element inside the latest observed UIA state.", false, ComputerCapabilityBacking.DesktopHost),
        Write("uia.invoke", "uia", "Invoke a fresh observed UIA element token.", AgentToolRisk.High, ComputerCapabilityBacking.DesktopHost),
        Write("uia.set_value", "uia", "Set a non-password fresh observed UIA value.", AgentToolRisk.High, ComputerCapabilityBacking.DesktopHost),
        Write("uia.select", "uia", "Select a fresh observed UIA element.", AgentToolRisk.Medium, ComputerCapabilityBacking.DesktopHost),
        Write("uia.expand", "uia", "Expand a fresh observed UIA element.", AgentToolRisk.Medium, ComputerCapabilityBacking.DesktopHost),

        Write("input.click", "input", "Click only against current observed state/token/bounds.", AgentToolRisk.High, ComputerCapabilityBacking.DesktopHost),
        Write("input.double_click", "input", "Double-click only against current observed state/token/bounds.", AgentToolRisk.High, ComputerCapabilityBacking.DesktopHost),
        Write("input.type", "input", "Type/set text only against current observed safe state.", AgentToolRisk.High, ComputerCapabilityBacking.DesktopHost),
        Write("input.key", "input", "Send a bounded safe key/chord to a current observed window.", AgentToolRisk.High, ComputerCapabilityBacking.DesktopHost),
        Write("input.chord", "input", "Send a bounded safe chord to a current observed window.", AgentToolRisk.High, ComputerCapabilityBacking.DesktopHost),
        Write("input.scroll", "input", "Scroll inside current observed window state.", AgentToolRisk.Medium, ComputerCapabilityBacking.DesktopHost),
        Write("input.drag", "input", "Drag from current observed state with stale-state protection.", AgentToolRisk.High, ComputerCapabilityBacking.DesktopHost),

        Read("screen.capture", "screen", "Capture bounded pixels for a current safe observed window.", false, ComputerCapabilityBacking.DesktopHost),
        Read("screen.inspect_region", "screen", "Inspect a bounded region from a current screenshot.", false, ComputerCapabilityBacking.DesktopHost),
        Read("screen.observe_after_action", "screen", "Create post-mutation observation/evidence.", false, ComputerCapabilityBacking.DesktopHost),

        Write("browser.navigate", "browser", "Fallback browser navigation when structured WebResearchHost is insufficient.", AgentToolRisk.Medium, ComputerCapabilityBacking.WebResearchOrBrowserFallback),
        Read("browser.inspect", "browser", "Fallback browser observation through approved browser adapter.", false, ComputerCapabilityBacking.WebResearchOrBrowserFallback),
        Write("browser.click", "browser", "Fallback browser click against current browser state.", AgentToolRisk.High, ComputerCapabilityBacking.WebResearchOrBrowserFallback),
        Write("browser.type", "browser", "Fallback browser typing against current browser state.", AgentToolRisk.High, ComputerCapabilityBacking.WebResearchOrBrowserFallback),
        Write("browser.download", "browser", "Fallback browser download with destination/scope policy.", AgentToolRisk.High, ComputerCapabilityBacking.WebResearchOrBrowserFallback),
        Read("browser.wait", "browser", "Wait for bounded browser state transition.", false, ComputerCapabilityBacking.WebResearchOrBrowserFallback)
    ];

    public static IReadOnlyList<ComputerCapabilityDefinition> All
        => Definitions.ToArray();

    public static IReadOnlyList<ComputerCapabilityDefinition> Namespace(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim().ToLowerInvariant();
        return Definitions
            .Where(x => x.Namespace == normalized)
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public static bool ContainsMonolithicUnsafeControl()
        => Definitions.Any(x =>
            x.Name.Contains("control_computer", StringComparison.OrdinalIgnoreCase)
            || x.Name.Contains("run_anything", StringComparison.OrdinalIgnoreCase)
            || x.Name.Contains("arbitrary_command", StringComparison.OrdinalIgnoreCase));

    private static ComputerCapabilityDefinition Read(
        string name,
        string ns,
        string description,
        bool parallel,
        ComputerCapabilityBacking backing)
        => new(
            name,
            ns,
            description,
            AgentToolAccess.ReadOnly,
            AgentToolRisk.Low,
            parallel,
            backing);

    private static ComputerCapabilityDefinition Write(
        string name,
        string ns,
        string description,
        AgentToolRisk risk,
        ComputerCapabilityBacking backing)
        => new(
            name,
            ns,
            description,
            AgentToolAccess.Mutating,
            risk,
            false,
            backing);
}

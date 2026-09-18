using H2AgentLab.Extensions;
using H2AgentLab.Tools;

namespace H2AgentLab.FirstPartyExtensions.Computer;

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

    public ProcessShellComputerExtension(IAgentToolExecutor executor) : base(executor) { }

    public override AgentExtensionMetadata Metadata { get; } = new(
        "process-shell-computer-extension",
        "1.0.0",
        "Process/Shell Computer Extension",
        "Registers bounded process and shell tools.");

    protected override string ProviderId => "firstparty.computer.process-shell";
    protected override IReadOnlyList<ComputerToolCard> Cards => Definitions;
}

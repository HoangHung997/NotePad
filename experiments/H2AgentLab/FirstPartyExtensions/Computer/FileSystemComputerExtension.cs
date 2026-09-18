using H2AgentLab.Extensions;
using H2AgentLab.Tools;

namespace H2AgentLab.FirstPartyExtensions.Computer;

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

    public FileSystemComputerExtension(IAgentToolExecutor executor) : base(executor) { }

    public override AgentExtensionMetadata Metadata { get; } = new(
        "filesystem-computer-extension",
        "1.0.0",
        "FileSystem Computer Extension",
        "Registers approved workspace filesystem tools.");

    protected override string ProviderId => "firstparty.computer.filesystem";
    protected override IReadOnlyList<ComputerToolCard> Cards => Definitions;
}

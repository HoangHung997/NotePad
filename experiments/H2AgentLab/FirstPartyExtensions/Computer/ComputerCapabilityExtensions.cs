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

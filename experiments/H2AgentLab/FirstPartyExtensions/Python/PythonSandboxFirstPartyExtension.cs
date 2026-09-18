using H2AgentLab.Extensions;
using H2AgentLab.Tools;

namespace H2AgentLab.FirstPartyExtensions.Python;

public sealed class PythonSandboxFirstPartyExtension : IAgentExtension
{
    private readonly IAgentToolExecutor _executor;
    private readonly string _version;

    public PythonSandboxFirstPartyExtension(
        IAgentToolExecutor executor,
        string version = "1.0.0")
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _version = ToolNamespace.NormalizeId(version, nameof(version));
    }

    public AgentExtensionMetadata Metadata => new(
        "python-sandbox-first-party-extension",
        _version,
        "Python Sandbox Extension",
        "Reference card over the preserved AgentTools/ScriptWorkspace/WindowsPythonSandbox path.",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sandbox"] = "windows-appcontainer",
            ["network"] = "disabled"
        });

    public void Register(AgentExtensionRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        // Reuse the existing v1-compatible Python tool schemas, risk/scope metadata and executor.
        // The temporary registry prevents unrelated compatibility tools from leaking onto the bus.
        var temporary = new ToolRegistry();
        V1ToolRegistryAdapter.Populate(temporary, _executor);

        foreach (var existing in temporary.GetNamespace("python"))
        {
            registration.RegisterTool(new ToolDescriptor(
                existing.Name,
                existing.Namespace,
                existing.Description,
                existing.Risk,
                existing.Access,
                existing.SupportsParallel,
                existing.SchemaVersion,
                existing.CallableSchema,
                existing.Executor,
                provenance: new ToolProvenance(
                    "firstparty.python-sandbox",
                    _version,
                    "windows-appcontainer",
                    existing.Provenance?.ToolVersion ?? "v1"),
                resourceScope: existing.ResourceScope,
                serializationKey: existing.SerializationKey,
                canProvideVerificationEvidence:
                    existing.CanProvideVerificationEvidence,
                preference: existing.Preference));
        }
    }
}

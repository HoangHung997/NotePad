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

        // Reuse the canonical normal-runtime Python schemas/risk/scope metadata while keeping
        // the supplied sandbox executor and first-party provenance.
        var temporary = new ToolRegistry();
        NormalRuntimeToolRegistry.PopulateNamespace(
            temporary,
            "python",
            _executor,
            new ToolProvenance(
                "firstparty.python-sandbox",
                _version,
                "windows-appcontainer",
                "2.0.0"));

        foreach (var existing in temporary.GetNamespace("python"))
            registration.RegisterTool(existing);
    }
}

using H2AgentLab.Context;
using H2AgentLab.Metrics;
using H2AgentLab.Session;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2AgentLab.Verification;
using H2Notes.Core;

namespace H2AgentLab.Runtime;

public interface IAgentRuntimeFactory
{
    AgentRuntime Create(
        AiProfile profile,
        string apiKey,
        global::H2AgentLab.AgentTools tools,
        AgentContextManager contextManager,
        AgentRunTelemetry telemetry);
}

/// <summary>
/// Normal Agent Lab runtime construction seam. Tool migration still uses V1ToolRegistryAdapter as a
/// temporary provider bridge; model-provider selection is delegated to IAgentTransportFactory.
/// </summary>
public sealed class AgentRuntimeFactory : IAgentRuntimeFactory
{
    private readonly IAgentTransportFactory _transportFactory;

    public AgentRuntimeFactory(IAgentTransportFactory? transportFactory = null)
    {
        _transportFactory = transportFactory ?? new AgentTransportFactory();
    }

    public AgentRuntime Create(
        AiProfile profile,
        string apiKey,
        global::H2AgentLab.AgentTools tools,
        AgentContextManager contextManager,
        AgentRunTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(contextManager);
        ArgumentNullException.ThrowIfNull(telemetry);

        var registry = V1ToolRegistryAdapter.Create(tools);
        var transport = _transportFactory.Create(profile, apiKey, telemetry);
        var verifier = new AgentRuntimeDomainVerifierRouter(
        [
            new FileRuntimeDomainVerifier(tools.Workspace),
            new PythonRuntimeDomainVerifier(tools.Workspace, tools.StateRoot),
            new StructuredOfficeRuntimeDomainVerifier()
        ]);

        return new AgentRuntime(
            transport,
            contextManager,
            registry,
            verifier: verifier,
            permissionPolicy: new ScopedAgentRuntimePermissionPolicy(
                _ => !tools.ReadOnly),
            evidenceProjector: new AgentRuntimeEvidenceProjector(
                new ArtifactStore(tools.StateRoot)));
    }
}

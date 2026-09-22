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
/// Normal Agent Lab runtime construction seam. ToolRegistry is the sole callable surface; domain
/// executors are registered directly and the legacy AgentTools giant switch is not reachable here.
/// Model-provider selection remains delegated to IAgentTransportFactory.
/// </summary>
public sealed class AgentRuntimeFactory : IAgentRuntimeFactory
{
    private readonly IAgentTransportFactory _transportFactory;
    private readonly Func<AgentRunTelemetry, IAgentRuntimeHooks> _hooksFactory;

    public AgentRuntimeFactory(IAgentTransportFactory? transportFactory = null,
        Func<AgentRunTelemetry, IAgentRuntimeHooks>? hooksFactory = null)
    {
        _transportFactory = transportFactory ?? new AgentTransportFactory();
        _hooksFactory = hooksFactory ?? (telemetry => new AgentRuntimeHooks(telemetry));
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

        var registry = NormalRuntimeToolRegistry.Create(tools);
        var domainVerifiers = new List<IAgentRuntimeDomainVerifier>
        {
            new FileRuntimeDomainVerifier(tools.Workspace),
            new PythonRuntimeDomainVerifier(tools.Workspace, tools.StateRoot),
            new StructuredOfficeRuntimeDomainVerifier()
        };
        if (tools.ProductionSession is { } session)
            registry = session.Configure(tools, registry, domainVerifiers);
        var transport = _transportFactory.Create(profile, apiKey, telemetry);
        var verifier = new AgentRuntimeDomainVerifierRouter(domainVerifiers);

        return new AgentRuntime(
            transport,
            contextManager,
            registry,
            verifier: verifier,
            permissionPolicy: tools.ProductionSession ?? (IAgentRuntimePermissionPolicy)new ScopedAgentRuntimePermissionPolicy(
                _ => !tools.ReadOnly),
            evidenceProjector: new AgentRuntimeEvidenceProjector(
                new ArtifactStore(tools.StateRoot)),
            hooks: _hooksFactory(telemetry) ?? throw new InvalidOperationException("Runtime hook factory returned null."));
    }
}

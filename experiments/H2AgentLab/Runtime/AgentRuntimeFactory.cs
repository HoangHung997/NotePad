using H2AgentLab.Context;
using H2AgentLab.Metrics;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
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
        return new AgentRuntime(
            transport,
            contextManager,
            registry);
    }
}

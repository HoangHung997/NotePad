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
/// Normal Agent Lab runtime construction seam. Provider selection details live here rather than in
/// AgentOrchestrator/AgentRuntime. MB-12 owns the full provider matrix acceptance; MB-11 only needs
/// the normal UI to cross this seam instead of creating AgentRunner.
/// </summary>
public sealed class AgentRuntimeFactory : IAgentRuntimeFactory
{
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
        var transport = CreateTransport(profile, apiKey, telemetry);
        return new AgentRuntime(
            transport,
            contextManager,
            registry);
    }

    internal static IAgentTransport CreateTransport(
        AiProfile profile,
        string apiKey,
        AgentRunTelemetry telemetry)
        => profile.Protocol switch
        {
            AiProtocol.Ollama => new OllamaTransport(profile),
            AiProtocol.OpenAiResponses => new OpenAiResponsesTransport(
                profile,
                apiKey,
                stateMode: OpenAiResponsesStateMode.Stateless),
            AiProtocol.OpenAiChat => new ChatCompletionsTransport(profile, apiKey),
            AiProtocol.Gemini => throw new NotSupportedException(
                "H2 AgentRuntime chưa hỗ trợ Gemini tool runtime. Dùng Ollama, OpenAI Responses hoặc Chat Completions; Gemini không được chuyển ngầm về AgentRunner."),
            _ => throw new NotSupportedException(
                "Giao thức model chưa được H2 AgentRuntime hỗ trợ.")
        };
}

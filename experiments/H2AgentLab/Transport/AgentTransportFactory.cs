using H2AgentLab.Metrics;
using H2Notes.Core;

namespace H2AgentLab.Transport;

public enum OpenAiResponsesTransportPreference
{
    Http = 0,
    WebSocket = 1
}

public sealed record AgentTransportFactoryOptions(
    OpenAiResponsesTransportPreference ResponsesPreference = OpenAiResponsesTransportPreference.Http);

public interface IAgentTransportFactory
{
    IAgentTransport Create(
        AiProfile profile,
        string apiKey,
        AgentRunTelemetry telemetry);
}

/// <summary>
/// Provider selection seam for the real AgentRuntime path. This class chooses an existing transport;
/// it never implements provider HTTP/WebSocket wire formats itself.
/// </summary>
public sealed class AgentTransportFactory : IAgentTransportFactory
{
    private readonly AgentTransportFactoryOptions _options;

    public AgentTransportFactory(AgentTransportFactoryOptions? options = null)
    {
        _options = options ?? new AgentTransportFactoryOptions();
    }

    public IAgentTransport Create(
        AiProfile profile,
        string apiKey,
        AgentRunTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(telemetry);

        IAgentTransport transport = profile.Protocol switch
        {
            AiProtocol.Ollama => new OllamaTransport(profile),
            AiProtocol.OpenAiResponses
                when _options.ResponsesPreference == OpenAiResponsesTransportPreference.WebSocket
                => new OpenAiResponsesWebSocketTransport(profile, apiKey, telemetry.Trace),
            AiProtocol.OpenAiResponses => new OpenAiResponsesTransport(
                profile,
                apiKey,
                stateMode: OpenAiResponsesStateMode.Stateless),
            AiProtocol.OpenAiChat => new ChatCompletionsTransport(profile, apiKey),
            AiProtocol.Gemini => throw new NotSupportedException(
                "H2 AgentRuntime chưa hỗ trợ Gemini tool transport. Không chuyển ngầm về AgentRunner."),
            _ => throw new NotSupportedException(
                "Giao thức model chưa được H2 AgentRuntime hỗ trợ.")
        };
        if (transport is IAgentRequestBudgetSource budgeted)
            budgeted.RequestBudgetEvaluated += receipt => telemetry.Trace.Mark(
                AgentTraceKind.RuntimeHook, "outgoing-request-budget", System.Text.Json.JsonSerializer.Serialize(receipt));
        return transport;
    }
}

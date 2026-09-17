using System.Text.Json;
using H2Notes.Core;

namespace H2AgentLab.Transport;

public enum AgentTransportEventKind
{
    ResponseStarted,
    TextDelta,
    ReasoningDelta,
    ReasoningSummaryDelta,
    ToolCall,
    Usage,
    Completed,
    Warning
}

public enum AgentTransportMessageRole
{
    System,
    User,
    Assistant
}

public sealed record AgentTransportMessage(
    AgentTransportMessageRole Role,
    string Content,
    IReadOnlyList<AiImage>? Images = null,
    IReadOnlyList<AiFile>? Files = null);

public sealed record AgentToolDefinition(
    string Name,
    string Description,
    JsonElement Parameters,
    bool SupportsParallelExecution = false)
{
    public AgentToolDefinition Clone()
        => this with { Parameters = Parameters.Clone() };
}

public sealed record AgentTransportStartRequest(
    Guid TaskId,
    Guid TurnId,
    IReadOnlyList<AgentTransportMessage> Messages,
    IReadOnlyList<AgentToolDefinition> Tools,
    string? PromptCacheKey = null,
    bool AllowParallelToolCalls = false);

public sealed record AgentToolResult(
    string ToolCallId,
    string ToolName,
    string Content,
    bool IsError = false);

public sealed record AgentTransportContinuationRequest(
    Guid TaskId,
    Guid TurnId,
    IReadOnlyList<AgentToolResult> ToolResults,
    IReadOnlyList<AgentToolDefinition>? NewlyLoadedTools = null);

public sealed record AgentTransportToolCall(
    string Id,
    string Name,
    string ArgumentsJson);

public sealed record AgentTransportUsage(
    long? InputTokens = null,
    long? CachedInputTokens = null,
    long? CacheWriteInputTokens = null,
    long? OutputTokens = null,
    long? TotalTokens = null);

public sealed record AgentTransportEvent(
    AgentTransportEventKind Kind,
    string Text = "",
    AgentTransportToolCall? ToolCall = null,
    AgentTransportUsage? Usage = null,
    string? ResponseId = null,
    string? FinishReason = null)
{
    public static AgentTransportEvent Started(string? responseId = null)
        => new(AgentTransportEventKind.ResponseStarted, ResponseId: responseId);

    public static AgentTransportEvent Text(string text)
        => new(AgentTransportEventKind.TextDelta, Text: text);

    public static AgentTransportEvent Reasoning(string text)
        => new(AgentTransportEventKind.ReasoningDelta, Text: text);

    public static AgentTransportEvent ReasoningSummary(string text)
        => new(AgentTransportEventKind.ReasoningSummaryDelta, Text: text);

    public static AgentTransportEvent Tool(AgentTransportToolCall call)
        => new(AgentTransportEventKind.ToolCall, ToolCall: call);

    public static AgentTransportEvent Meter(AgentTransportUsage usage)
        => new(AgentTransportEventKind.Usage, Usage: usage);

    public static AgentTransportEvent Complete(string? responseId = null, string? finishReason = null)
        => new(AgentTransportEventKind.Completed, ResponseId: responseId, FinishReason: finishReason);
}

/// <summary>
/// Turn-scoped, provider-neutral transport contract. Implementations own provider JSON, network
/// protocol and continuation state. The orchestrator sees only these typed requests/events.
/// One instance represents one active model turn; create a fresh instance for a new user turn.
/// </summary>
public interface IAgentTransport : IAsyncDisposable
{
    AgentTransportCapabilities Capabilities { get; }

    IAsyncEnumerable<AgentTransportEvent> StartAsync(
        AgentTransportStartRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
        AgentTransportContinuationRequest request,
        CancellationToken cancellationToken = default);

    void Cancel();
}

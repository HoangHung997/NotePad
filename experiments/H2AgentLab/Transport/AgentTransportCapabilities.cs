using H2Notes.Core;

namespace H2AgentLab.Transport;

/// <summary>
/// Provider-neutral capabilities exposed to the orchestrator. The orchestrator must branch on
/// these explicit capabilities instead of guessing from provider or model names.
/// </summary>
public sealed record AgentTransportCapabilities(
    bool NativeTools,
    bool IncrementalContinuation,
    bool WebSocket,
    bool ProviderCompaction,
    bool PromptCacheControl,
    bool ParallelToolCalls,
    bool NativeImageInput,
    bool NativeFileInput,
    bool UsageMetrics)
{
    public static AgentTransportCapabilities Minimal { get; } = new(
        NativeTools: false,
        IncrementalContinuation: false,
        WebSocket: false,
        ProviderCompaction: false,
        PromptCacheControl: false,
        ParallelToolCalls: false,
        NativeImageInput: false,
        NativeFileInput: false,
        UsageMetrics: false);

    public static AgentTransportCapabilities OllamaNative { get; } = new(
        NativeTools: true,
        IncrementalContinuation: false,
        WebSocket: false,
        ProviderCompaction: false,
        PromptCacheControl: false,
        ParallelToolCalls: false,
        NativeImageInput: true,
        NativeFileInput: false,
        UsageMetrics: true);

    public static AgentTransportCapabilities ChatCompletionsFallback { get; } = new(
        NativeTools: true,
        IncrementalContinuation: false,
        WebSocket: false,
        ProviderCompaction: false,
        PromptCacheControl: false,
        ParallelToolCalls: false,
        NativeImageInput: true,
        NativeFileInput: true,
        UsageMetrics: false);

    /// <summary>
    /// Public Responses HTTP/SSE implementation. Stateless mode leaves IncrementalContinuation false;
    /// stored HTTP continuation may opt into it at runtime. Prompt cache keys, parallel tools, native
    /// image/file input and usage counters are supported by the public Responses request shape.
    /// </summary>
    public static AgentTransportCapabilities OpenAiResponsesHttp { get; } = new(
        NativeTools: true,
        IncrementalContinuation: false,
        WebSocket: false,
        ProviderCompaction: false,
        PromptCacheControl: true,
        ParallelToolCalls: true,
        NativeImageInput: true,
        NativeFileInput: true,
        UsageMetrics: true);

    /// <summary>
    /// Turn-scoped official OpenAI Responses WebSocket. The same socket is reused for tool
    /// continuations, which can send only new function_call_output items plus previous_response_id.
    /// </summary>
    public static AgentTransportCapabilities OpenAiResponsesWebSocket { get; } = OpenAiResponsesHttp with
    {
        IncrementalContinuation = true,
        WebSocket = true
    };
}

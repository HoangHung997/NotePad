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
    /// Baseline public Responses HTTP/SSE implementation. Provider-side continuation is deliberately
    /// false until V2-0206; WebSocket remains false until V2-0207. Prompt cache keys, parallel tools,
    /// native image/file input and usage counters are already carried by the public HTTP request.
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
}

namespace H2AgentLab.Metrics;

public sealed record AgentMetricsSnapshot
{
    public long InputTokens { get; init; }
    public long CachedInputTokens { get; init; }
    public long CacheWriteInputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long ReasoningTokens { get; init; }
    public long BytesSent { get; init; }
    public long BytesReceived { get; init; }
    public int ModelCalls { get; init; }
    public int ToolCalls { get; init; }
    public int RepairCount { get; init; }
    public double? FirstModelEventMilliseconds { get; init; }
    public double TotalMilliseconds { get; init; }
    public decimal? EstimatedCostUsd { get; init; }
}

/// <summary>
/// Thread-safe numeric counters for one Agent Lab user turn. The collector stores only
/// provider/public usage numbers and timings; it never stores prompt text, API keys or hidden
/// reasoning content.
/// </summary>
public sealed class AgentMetrics
{
    private long _inputTokens;
    private long _cachedInputTokens;
    private long _cacheWriteInputTokens;
    private long _outputTokens;
    private long _reasoningTokens;
    private long _bytesSent;
    private long _bytesReceived;
    private int _modelCalls;
    private int _toolCalls;
    private int _repairCount;
    private decimal? _estimatedCostUsd;
    private readonly object _costGate = new();

    public void AddUsage(long inputTokens = 0, long cachedInputTokens = 0, long cacheWriteInputTokens = 0,
        long outputTokens = 0, long reasoningTokens = 0)
    {
        ValidateNonNegative(inputTokens, nameof(inputTokens));
        ValidateNonNegative(cachedInputTokens, nameof(cachedInputTokens));
        ValidateNonNegative(cacheWriteInputTokens, nameof(cacheWriteInputTokens));
        ValidateNonNegative(outputTokens, nameof(outputTokens));
        ValidateNonNegative(reasoningTokens, nameof(reasoningTokens));
        Interlocked.Add(ref _inputTokens, inputTokens);
        Interlocked.Add(ref _cachedInputTokens, cachedInputTokens);
        Interlocked.Add(ref _cacheWriteInputTokens, cacheWriteInputTokens);
        Interlocked.Add(ref _outputTokens, outputTokens);
        Interlocked.Add(ref _reasoningTokens, reasoningTokens);
    }

    public void AddTraffic(long bytesSent = 0, long bytesReceived = 0)
    {
        ValidateNonNegative(bytesSent, nameof(bytesSent));
        ValidateNonNegative(bytesReceived, nameof(bytesReceived));
        Interlocked.Add(ref _bytesSent, bytesSent);
        Interlocked.Add(ref _bytesReceived, bytesReceived);
    }

    public void IncrementModelCalls(int count = 1)
    {
        ValidateNonNegative(count, nameof(count));
        Interlocked.Add(ref _modelCalls, count);
    }

    public void IncrementToolCalls(int count = 1)
    {
        ValidateNonNegative(count, nameof(count));
        Interlocked.Add(ref _toolCalls, count);
    }

    public void IncrementRepairs(int count = 1)
    {
        ValidateNonNegative(count, nameof(count));
        Interlocked.Add(ref _repairCount, count);
    }

    public void SetEstimatedCostUsd(decimal? value)
    {
        if (value is < 0) throw new ArgumentOutOfRangeException(nameof(value));
        lock (_costGate) _estimatedCostUsd = value;
    }

    public AgentMetricsSnapshot Snapshot(AgentTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var firstRequest = trace.First(AgentTraceKind.RequestStart);
        var firstModel = trace.First(AgentTraceKind.FirstModelEvent);
        double? firstModelMs = null;
        if (firstModel is not null)
            firstModelMs = Math.Max(0, firstModel.ElapsedMilliseconds - (firstRequest?.ElapsedMilliseconds ?? 0));
        decimal? cost;
        lock (_costGate) cost = _estimatedCostUsd;
        return new AgentMetricsSnapshot
        {
            InputTokens = Interlocked.Read(ref _inputTokens),
            CachedInputTokens = Interlocked.Read(ref _cachedInputTokens),
            CacheWriteInputTokens = Interlocked.Read(ref _cacheWriteInputTokens),
            OutputTokens = Interlocked.Read(ref _outputTokens),
            ReasoningTokens = Interlocked.Read(ref _reasoningTokens),
            BytesSent = Interlocked.Read(ref _bytesSent),
            BytesReceived = Interlocked.Read(ref _bytesReceived),
            ModelCalls = Volatile.Read(ref _modelCalls),
            ToolCalls = Volatile.Read(ref _toolCalls),
            RepairCount = Volatile.Read(ref _repairCount),
            FirstModelEventMilliseconds = firstModelMs,
            TotalMilliseconds = trace.ElapsedMilliseconds,
            EstimatedCostUsd = cost
        };
    }

    private static void ValidateNonNegative(long value, string name)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(name);
    }
}

using System.Text;
using System.Text.Json;
using H2AgentLab.Context;
using H2AgentLab.Prompting;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2AgentLab.Verification;

namespace H2AgentLab.Runtime;

public sealed record AgentRuntimeRequest(
    AgentTaskContract Contract,
    string UserInput,
    AgentPromptStablePrefix StablePrefix,
    AgentContextInput Context,
    string? PromptCacheKey = null,
    int MaxToolRounds = 24,
    int MaxRepairRounds = 4,
    AgentPromptCacheScope? PromptCacheScope = null,
    IReadOnlyList<AgentStableSkillHash>? StableSkillHashes = null);

public sealed record AgentRuntimeUsage(
    long InputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    long OutputTokens,
    long TotalTokens);

public sealed record AgentRuntimeResult(
    string FinalText,
    int ToolRounds,
    int ToolCalls,
    int RepairRounds,
    AgentRuntimeUsage Usage,
    AgentContextSnapshot ContextSnapshot,
    IReadOnlyList<VerificationReport> VerificationHistory,
    IReadOnlyList<string> LoadedToolSchemas,
    AgentPromptCacheIdentity? PromptCacheIdentity = null);

public sealed record AgentRuntimeVerificationContext(
    AgentTaskContract Contract,
    int ToolRound,
    IReadOnlyList<global::H2AgentLab.ToolCall> Calls,
    IReadOnlyList<AgentToolResult> Results);

public interface IAgentRuntimeVerifier
{
    Task<VerificationReport?> VerifyAsync(
        AgentRuntimeVerificationContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Provider-neutral model/tool engine. It owns one complete model turn: bounded prompt assembly,
/// deferred schema exposure, typed tool execution/continuation, verification feedback, bounded repair
/// continuation and host-owned completion gating. Domain behavior lives behind ToolRegistry/verifiers.
/// </summary>
public sealed class AgentRuntime : IAsyncDisposable
{
    // MB-10: this is the new provider-neutral runtime path; UI ownership moves here in MB-11.
    private const int MaxToolOutputCharacters = 64_000;

    private readonly IAgentTransport _transport;
    private readonly AgentContextManager _contextManager;
    private readonly ToolRegistry _registry;
    private readonly DeferredToolDiscovery _discovery;
    private readonly ToolExecutionScheduler _scheduler;
    private readonly AgentRepairController _repairController;
    private readonly IAgentRuntimeVerifier? _verifier;
    private readonly IAgentRuntimePermissionPolicy _permissionPolicy;
    private bool _disposed;

    public AgentRuntime(
        IAgentTransport transport,
        AgentContextManager contextManager,
        ToolRegistry registry,
        DeferredToolDiscovery? discovery = null,
        ToolExecutionScheduler? scheduler = null,
        AgentRepairController? repairController = null,
        IAgentRuntimeVerifier? verifier = null,
        IAgentRuntimePermissionPolicy? permissionPolicy = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _contextManager = contextManager ?? throw new ArgumentNullException(nameof(contextManager));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _discovery = discovery ?? new DeferredToolDiscovery(registry);
        _scheduler = scheduler ?? new ToolExecutionScheduler();
        _repairController = repairController ?? new AgentRepairController();
        _verifier = verifier;
        _permissionPolicy = permissionPolicy ?? new ScopedAgentRuntimePermissionPolicy();
    }

    public async Task<AgentRuntimeResult> RunAsync(
        AgentRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Contract);
        ArgumentNullException.ThrowIfNull(request.StablePrefix);
        ArgumentNullException.ThrowIfNull(request.Context);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserInput);
        if (request.MaxToolRounds is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(request.MaxToolRounds));
        if (request.MaxRepairRounds is < 0 or > 16)
            throw new ArgumentOutOfRangeException(nameof(request.MaxRepairRounds));

        var contextSnapshot = _contextManager.Build(request.Context);
        var initialExposure = _discovery.BuildInitialExposure();
        var stable = request.StablePrefix with
        {
            ToolNamespaceMetadata = MergeNamespaceMetadata(
                request.StablePrefix.ToolNamespaceMetadata,
                initialExposure.Namespaces)
        };
        var layout = AgentPromptLayout.Create(
            stable,
            contextSnapshot.RuntimeContext,
            request.UserInput);

        AgentPromptCacheIdentity? promptCacheIdentity = null;
        if (request.PromptCacheScope is not null)
        {
            promptCacheIdentity = AgentPromptCacheIdentityBuilder.Build(
                layout,
                request.PromptCacheScope,
                request.StableSkillHashes);
        }
        var promptCacheKey = request.PromptCacheKey
            ?? promptCacheIdentity?.Key;

        var taskId = request.Contract.TaskId;
        var turnId = Guid.NewGuid();
        var initialTools = initialExposure.CallableSchemas
            .Select(ToTransportTool)
            .ToArray();

        var usage = new MutableUsage();
        var verificationHistory = new List<VerificationReport>();
        VerificationReport? latestVerification = null;
        var toolRounds = 0;
        var toolCalls = 0;
        var repairRounds = 0;

        try
        {
            var round = await ReadRoundAsync(
                _transport.StartAsync(
                    new AgentTransportStartRequest(
                        taskId,
                        turnId,
                        layout.Messages,
                        initialTools,
                        promptCacheKey,
                        _transport.Capabilities.ParallelToolCalls),
                    cancellationToken),
                usage,
                cancellationToken).ConfigureAwait(false);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (round.ToolCalls.Count == 0)
                {
                    EnsureFinalCompletionAllowed(request.Contract, latestVerification);
                    return new AgentRuntimeResult(
                        round.Text,
                        toolRounds,
                        toolCalls,
                        repairRounds,
                        usage.Snapshot(),
                        contextSnapshot,
                        verificationHistory.ToArray(),
                        _discovery.LoadedSchemaNames,
                        promptCacheIdentity);
                }

                if (++toolRounds > request.MaxToolRounds)
                    throw new InvalidOperationException(
                        $"AgentRuntime exceeded the {request.MaxToolRounds}-round tool budget.");

                toolCalls += round.ToolCalls.Count;
                var execution = await ExecuteCallsAsync(
                    request.Contract,
                    round.ToolCalls,
                    cancellationToken).ConfigureAwait(false);

                var results = execution.Results.ToArray();
                if (_verifier is not null)
                {
                    var report = await _verifier.VerifyAsync(
                        new AgentRuntimeVerificationContext(
                            request.Contract,
                            toolRounds,
                            execution.Calls,
                            results),
                        cancellationToken).ConfigureAwait(false);

                    if (report is not null)
                    {
                        latestVerification = report;
                        verificationHistory.Add(report);
                        if (!report.Passed)
                        {
                            if (report.Failures.Count == 0)
                                throw new InvalidOperationException(
                                    "Verifier returned non-passing state without actionable failures.");
                            if (++repairRounds > request.MaxRepairRounds)
                                throw new InvalidOperationException(
                                    $"AgentRuntime exceeded the {request.MaxRepairRounds}-round repair budget.");

                            var repair = _repairController.Build(request.Contract, report);
                            if (results.Length == 0)
                                throw new InvalidOperationException(
                                    "Verification failure has no tool result continuation target.");

                            results[0] = results[0] with
                            {
                                Content = BoundToolOutput(
                                    results[0].Content
                                    + Environment.NewLine
                                    + Environment.NewLine
                                    + "[HOST VERIFICATION FAILED]"
                                    + Environment.NewLine
                                    + repair.PromptContext),
                                IsError = true
                            };
                        }
                    }
                }

                round = await ReadRoundAsync(
                    _transport.ContinueAsync(
                        new AgentTransportContinuationRequest(
                            taskId,
                            turnId,
                            results,
                            execution.NewlyLoadedTools.Count == 0
                                ? null
                                : execution.NewlyLoadedTools),
                        cancellationToken),
                    usage,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _transport.Cancel();
            throw;
        }
    }

    public void Cancel()
        => _transport.Cancel();

    private async Task<ExecutionBatch> ExecuteCallsAsync(
        AgentTaskContract contract,
        IReadOnlyList<AgentTransportToolCall> transportCalls,
        CancellationToken cancellationToken)
    {
        var calls = new global::H2AgentLab.ToolCall[transportCalls.Count];
        var results = new AgentToolResult?[transportCalls.Count];
        var scheduled = new List<(int OriginalIndex, ToolExecutionRequest Request, AgentRuntimePermissionRequest Permission)>();
        var newlyLoadedSchemas = new List<AgentToolDefinition>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < transportCalls.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transportCall = transportCalls[i];
            if (string.IsNullOrWhiteSpace(transportCall.Id)
                || !seenIds.Add(transportCall.Id))
                throw new InvalidOperationException(
                    "Model proposed a missing or duplicate tool-call ID.");

            var arguments = ParseArguments(transportCall.ArgumentsJson);
            var call = new global::H2AgentLab.ToolCall(
                transportCall.Id,
                transportCall.Name,
                arguments);
            calls[i] = call;

            if (string.Equals(
                    transportCall.Name,
                    DeferredToolDiscovery.SearchToolName,
                    StringComparison.Ordinal))
            {
                var output = _discovery.ExecuteToolSearch(call);
                var trace = _discovery.LoadTrace.Last();
                foreach (var name in trace.NewlyLoadedNames)
                {
                    if (_registry.TryGet(name, out var loadedDescriptor))
                        newlyLoadedSchemas.Add(ToTransportTool(loadedDescriptor.CallableSchema));
                }

                results[i] = new AgentToolResult(
                    transportCall.Id,
                    transportCall.Name,
                    BoundToolOutput(output),
                    IsError: false);
                continue;
            }

            if (!_registry.TryGet(transportCall.Name, out var descriptor))
            {
                results[i] = new AgentToolResult(
                    transportCall.Id,
                    transportCall.Name,
                    JsonSerializer.Serialize(new
                    {
                        ok = false,
                        error = "unknown_tool",
                        message = "Tool is not registered or its schema was not loaded for this task."
                    }),
                    IsError: true);
                continue;
            }

            if (!_discovery.LoadedSchemaNames.Contains(
                    descriptor.Name,
                    StringComparer.Ordinal))
            {
                results[i] = new AgentToolResult(
                    transportCall.Id,
                    transportCall.Name,
                    JsonSerializer.Serialize(new
                    {
                        ok = false,
                        error = "tool_not_loaded",
                        message = "Tool schema was not selected through deferred discovery."
                    }),
                    IsError: true);
                continue;
            }

            var resourceKey = ResourceKey(descriptor);
            if (descriptor.IsMutating && string.IsNullOrWhiteSpace(resourceKey))
                throw new InvalidOperationException(
                    $"Mutating tool '{descriptor.Name}' requires a resource key.");

            var permissionRequest = new AgentRuntimePermissionRequest(
                contract,
                descriptor,
                call,
                resourceKey);
            var permission = await _permissionPolicy.AuthorizeAsync(
                permissionRequest,
                cancellationToken).ConfigureAwait(false);

            if (!permission.Allowed)
            {
                if (string.Equals(
                        permission.Code,
                        "missing_resource_scope",
                        StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Mutating tool '{descriptor.Name}' requires a resource key for serialization.");

                results[i] = new AgentToolResult(
                    transportCall.Id,
                    transportCall.Name,
                    JsonSerializer.Serialize(new
                    {
                        ok = false,
                        error = permission.Code,
                        message = permission.Message,
                        scope = permission.ResourceKey
                    }),
                    IsError: true);
                continue;
            }

            scheduled.Add((
                i,
                new ToolExecutionRequest(
                    descriptor,
                    call,
                    permission.ResourceKey),
                permissionRequest with { ResourceKey = permission.ResourceKey }));
        }

        if (scheduled.Count > 0)
        {
            var scheduledResults = await _scheduler.ExecuteBatchAsync(
                scheduled.Select(x => x.Request).ToArray(),
                cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < scheduled.Count; i++)
            {
                var original = scheduled[i].OriginalIndex;
                var scheduledResult = scheduledResults[i];
                var boundedOutput = BoundToolOutput(scheduledResult.Output);
                _permissionPolicy.ObserveResult(
                    scheduled[i].Permission,
                    boundedOutput);
                results[original] = new AgentToolResult(
                    calls[original].Id,
                    scheduledResult.ToolName,
                    boundedOutput,
                    IsError: false);
            }
        }

        if (results.Any(x => x is null))
            throw new InvalidOperationException(
                "AgentRuntime tool batch did not produce one result per tool call.");

        return new ExecutionBatch(
            calls,
            results.Select(x => x!).ToArray(),
            newlyLoadedSchemas
                .GroupBy(x => x.Name, StringComparer.Ordinal)
                .Select(x => x.First())
                .ToArray());
    }

    private static string? ResourceKey(ToolDescriptor descriptor)
        => descriptor.ResourceScope?.ScopeId;

    private static JsonElement ParseArguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            json = "{}";

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException(
                    "Tool arguments must be a complete JSON object.");
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Model proposed malformed/incomplete tool arguments; no tool was executed.",
                ex);
        }
    }

    private static async Task<RuntimeRound> ReadRoundAsync(
        IAsyncEnumerable<AgentTransportEvent> events,
        MutableUsage usage,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var calls = new List<AgentTransportToolCall>();
        var completed = false;
        string? finishReason = null;

        await foreach (var item in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            switch (item.Kind)
            {
                case AgentTransportEventKind.TextDelta:
                    text.Append(item.Text);
                    break;

                case AgentTransportEventKind.ToolCall:
                    if (item.ToolCall is null)
                        throw new InvalidOperationException(
                            "Transport emitted ToolCall event without a typed call.");
                    calls.Add(item.ToolCall);
                    break;

                case AgentTransportEventKind.Usage:
                    if (item.Usage is not null)
                        usage.Add(item.Usage);
                    break;

                case AgentTransportEventKind.Completed:
                    completed = true;
                    finishReason = item.FinishReason;
                    break;
            }
        }

        if (!completed)
            throw new IOException(
                "Transport ended without a completed response; proposed tools were not executed.");

        if (calls.Count > 0
            && finishReason is not null
            && finishReason.Contains("length", StringComparison.OrdinalIgnoreCase))
            throw new IOException(
                "Transport stopped for length while proposing tools; proposed tools were not executed.");

        return new RuntimeRound(
            text.ToString(),
            calls.ToArray(),
            finishReason);
    }

    private static void EnsureFinalCompletionAllowed(
        AgentTaskContract contract,
        VerificationReport? latestVerification)
    {
        if (!contract.VerificationPolicy.RequireVerification)
            return;

        if (latestVerification is null)
            throw new InvalidOperationException(
                "Task requires verification, but no verifier report exists before final completion.");

        var outcome = VerificationCompletionGate.Evaluate(
            contract,
            [latestVerification]);
        AgentTaskCompletionGate.EnsureCanComplete(contract, outcome);
    }

    private static AgentToolDefinition ToTransportTool(JsonElement callableSchema)
    {
        if (callableSchema.ValueKind != JsonValueKind.Object
            || !callableSchema.TryGetProperty("function", out var function)
            || function.ValueKind != JsonValueKind.Object
            || !function.TryGetProperty("name", out var nameNode)
            || nameNode.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(nameNode.GetString())
            || !function.TryGetProperty("parameters", out var parameters)
            || parameters.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(
                "Callable tool schema is missing function.name/parameters.");

        var name = nameNode.GetString()!;
        var description = function.TryGetProperty("description", out var descriptionNode)
            && descriptionNode.ValueKind == JsonValueKind.String
                ? descriptionNode.GetString() ?? name
                : name;

        return new AgentToolDefinition(
            name,
            description,
            parameters.Clone());
    }

    private static string MergeNamespaceMetadata(
        string existing,
        IReadOnlyList<ToolNamespaceSummary> namespaces)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(existing))
            builder.AppendLine(existing.Trim());

        if (namespaces.Count > 0)
        {
            builder.AppendLine("Available tool namespaces:");
            foreach (var item in namespaces.Take(64))
                builder.Append("- ")
                    .Append(item.Name)
                    .Append(": ")
                    .AppendLine(item.Description);
        }

        var value = builder.ToString().Trim();
        return value.Length <= 8_000
            ? value
            : value[..8_000];
    }

    private static string BoundToolOutput(string value)
    {
        value ??= "";
        return value.Length <= MaxToolOutputCharacters
            ? value
            : value[..MaxToolOutputCharacters] + "…[truncated]";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _scheduler.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record RuntimeRound(
        string Text,
        IReadOnlyList<AgentTransportToolCall> ToolCalls,
        string? FinishReason);

    private sealed record ExecutionBatch(
        IReadOnlyList<global::H2AgentLab.ToolCall> Calls,
        IReadOnlyList<AgentToolResult> Results,
        IReadOnlyList<AgentToolDefinition> NewlyLoadedTools);

    private sealed class MutableUsage
    {
        private long _input;
        private long _cached;
        private long _cacheWrite;
        private long _output;
        private long _total;

        public void Add(AgentTransportUsage usage)
        {
            _input += usage.InputTokens ?? 0;
            _cached += usage.CachedInputTokens ?? 0;
            _cacheWrite += usage.CacheWriteInputTokens ?? 0;
            _output += usage.OutputTokens ?? 0;
            _total += usage.TotalTokens
                ?? (usage.InputTokens ?? 0) + (usage.OutputTokens ?? 0);
        }

        public AgentRuntimeUsage Snapshot()
            => new(_input, _cached, _cacheWrite, _output, _total);
    }
}

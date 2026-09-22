using System.Text;
using System.Text.Json;
using H2AgentLab.Context;
using H2AgentLab.Prompting;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2AgentLab.Verification;
using H2Notes.Core;

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
    IReadOnlyList<AgentStableSkillHash>? StableSkillHashes = null,
    IReadOnlyList<AiImage>? Images = null,
    IReadOnlyList<AiFile>? Files = null,
    Action<AgentToolResult>? ToolResultObserver = null,
    Func<bool, IReadOnlyList<string>>? TakeSupplementalInput = null,
    Action<string>? PublicTextObserver = null,
    Action<string>? CommentaryObserver = null,
    Action<IReadOnlyList<AgentEvidenceReference>>? EvidenceObserver = null,
    Action<VerificationReport, int>? VerificationObserver = null);

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
    AgentPromptCacheIdentity? PromptCacheIdentity = null)
{
    public IReadOnlyList<AgentEvidenceReference> Evidence { get; init; }
        = Array.Empty<AgentEvidenceReference>();
    public AgentTaskContract? EffectiveContract { get; init; }
}

public sealed record AgentRuntimeVerificationContext(
    AgentTaskContract Contract,
    int ToolRound,
    IReadOnlyList<global::H2AgentLab.ToolCall> Calls,
    IReadOnlyList<AgentToolResult> Results)
{
    public IReadOnlyList<AgentEvidenceReference> Evidence { get; init; }
        = Array.Empty<AgentEvidenceReference>();

    public IReadOnlyDictionary<string, string> RawToolOutputs { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyList<string> MutationCallIds { get; init; } = [];
}

public interface IAgentRuntimeVerifier
{
    Task<VerificationReport?> VerifyAsync(
        AgentRuntimeVerificationContext context,
        CancellationToken cancellationToken);
}

public sealed class AgentVerificationRequiredException : InvalidOperationException
{
    public AgentVerificationRequiredException(string message)
        : base(message)
    {
    }

    public AgentVerificationRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
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
    private readonly AgentRuntimeEvidenceProjector? _evidenceProjector;
    private bool _disposed;

    public AgentRuntime(
        IAgentTransport transport,
        AgentContextManager contextManager,
        ToolRegistry registry,
        DeferredToolDiscovery? discovery = null,
        ToolExecutionScheduler? scheduler = null,
        AgentRepairController? repairController = null,
        IAgentRuntimeVerifier? verifier = null,
        IAgentRuntimePermissionPolicy? permissionPolicy = null,
        AgentRuntimeEvidenceProjector? evidenceProjector = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _contextManager = contextManager ?? throw new ArgumentNullException(nameof(contextManager));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _discovery = discovery ?? new DeferredToolDiscovery(registry);
        _scheduler = scheduler ?? new ToolExecutionScheduler();
        _repairController = repairController ?? new AgentRepairController();
        _verifier = verifier;
        _permissionPolicy = permissionPolicy ?? new ScopedAgentRuntimePermissionPolicy();
        _evidenceProjector = evidenceProjector;
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
        var messages = layout.Messages.ToArray();
        var userIndex = Array.FindLastIndex(messages, message => message.Role == AgentTransportMessageRole.User);
        if (userIndex >= 0)
            messages[userIndex] = messages[userIndex] with { Images = request.Images, Files = request.Files };

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
        var evidenceHistory = new List<AgentEvidenceReference>();
        VerificationReport? latestVerification = null;
        var toolRounds = 0;
        var toolCalls = 0;
        var repairRounds = 0;
        var failedMutationSignatures = new HashSet<string>(StringComparer.Ordinal);
        var effectiveContract = request.Contract;
        var mutationAwaitingVerification = false;
        var unresolvedCalls = new List<(string Name, string Code, string[] RecoveryTools, JsonElement Arguments, string? FailureId)>();
        var repeatedFailures = new Dictionary<string, int>(StringComparer.Ordinal);
        var completionRepairRequested = false;

        try
        {
            var round = await ReadRoundAsync(
                _transport.StartAsync(
                    new AgentTransportStartRequest(
                        taskId,
                        turnId,
                        messages,
                        initialTools,
                        promptCacheKey,
                        _transport.Capabilities.ParallelToolCalls),
                    cancellationToken),
                usage,
                cancellationToken, request.PublicTextObserver).ConfigureAwait(false);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (round.ToolCalls.Count == 0)
                {
                    var supplements = request.TakeSupplementalInput?.Invoke(true) ?? [];
                    if (supplements.Count > 0)
                    {
                        if (++toolRounds > request.MaxToolRounds)
                            throw new InvalidOperationException("Đã tới giới hạn số lượt bổ sung; nội dung bổ sung được giữ trong lịch sử.");
                        round = await ReadRoundAsync(_transport.ContinueAsync(new(taskId, turnId, [],
                            SupplementalUserMessages: supplements), cancellationToken), usage, cancellationToken, request.PublicTextObserver).ConfigureAwait(false);
                        continue;
                    }
                    if (unresolvedCalls.Any(f => f.FailureId is not null) && !completionRepairRequested
                        && toolRounds < request.MaxToolRounds && repairRounds < request.MaxRepairRounds)
                    {
                        completionRepairRequested = true; toolRounds++; repairRounds++;
                        var feedback = "The host still has unresolved failed attempts: " + string.Join("; ",
                            unresolvedCalls.Select(f => f.Name + " (failureId=" + f.FailureId + ")"))
                            + ". Follow the failed tool's recovery instructions and verify the requested output. Do not repeat an unrelated successful call or claim completion.";
                        round = await ReadRoundAsync(_transport.ContinueAsync(new(taskId, turnId, [],
                            SupplementalUserMessages: [feedback]), cancellationToken), usage, cancellationToken, request.PublicTextObserver).ConfigureAwait(false);
                        continue;
                    }
                    if (unresolvedCalls.Count > 0)
                        throw new AgentVerificationRequiredException(
                            "Chưa hoàn thành: công cụ vẫn còn lỗi — " + string.Join("; ",
                                unresolvedCalls.Take(8).Select(f => f.Name + " (" + f.Code + ")"))
                            + ". Kiểm tra phạm vi/thư mục hoặc thử lại.");
                    if (mutationAwaitingVerification)
                        throw new AgentVerificationRequiredException("The latest mutation has no verifier report.");
                    EnsureFinalCompletionAllowed(effectiveContract, latestVerification);
                    return new AgentRuntimeResult(
                        round.Text,
                        toolRounds,
                        toolCalls,
                        repairRounds,
                        usage.Snapshot(),
                        contextSnapshot,
                        verificationHistory.ToArray(),
                        _discovery.LoadedSchemaNames,
                        promptCacheIdentity)
                    {
                        Evidence = evidenceHistory.ToArray(),
                        EffectiveContract = effectiveContract
                    };
                }

                if (++toolRounds > request.MaxToolRounds)
                    throw new InvalidOperationException(
                        $"AgentRuntime exceeded the {request.MaxToolRounds}-round tool budget.");

                toolCalls += round.ToolCalls.Count;
                if (!string.IsNullOrWhiteSpace(round.Text)) request.CommentaryObserver?.Invoke(round.Text);
                request.PublicTextObserver?.Invoke("");
                var execution = await ExecuteCallsAsync(
                    effectiveContract,
                    turnId,
                    toolRounds,
                    round.ToolCalls,
                    failedMutationSignatures,
                    cancellationToken).ConfigureAwait(false);

                evidenceHistory.AddRange(execution.Evidence);
                request.EvidenceObserver?.Invoke(execution.Evidence);
                for (var resultIndex = 0; resultIndex < execution.Results.Count; resultIndex++)
                {
                    var result = execution.Results[resultIndex];
                    var executedCall = execution.Calls[resultIndex];
                    var executedName = executedCall.Name;
                    // Evidence projection is human/model-facing and may truncate or wrap JSON.
                    // Recovery bookkeeping must use the executor's complete structured output.
                    var recoveryOutput = execution.RawToolOutputs.TryGetValue(executedCall.Id, out var rawRecovery)
                        ? rawRecovery : result.Content;
                    request.ToolResultObserver?.Invoke(result);
                    if (result.IsError)
                    {
                        var code = "tool_error";
                        string[] recovery = [executedName];
                        string? failureId = null;
                        try
                        {
                            using var failure = JsonDocument.Parse(recoveryOutput);
                            if (failure.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                                code = error.GetString() ?? code;
                            if (failure.RootElement.TryGetProperty("recovery", out var detail)
                                && detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("code", out var typedCode)
                                && typedCode.ValueKind == JsonValueKind.String) code = typedCode.GetString() ?? code;
                            if (failure.RootElement.TryGetProperty("recoveryTools", out var choices))
                                recovery = choices.EnumerateArray().Select(c => c.GetString()!).ToArray();
                            if (failure.RootElement.TryGetProperty("failureId", out var id) && id.ValueKind == JsonValueKind.String)
                                failureId = id.GetString();
                        }
                        catch (JsonException) { }
                        // A prevented duplicate has no additional side effect. Its original
                        // failed verification remains authoritative until a verified correction.
                        if (code != "repeated_failed_mutation")
                            unresolvedCalls.Add((executedName, code, recovery, executedCall.Arguments.Clone(), failureId));
                        var failedSignature = MutationSignature(executedCall) + ":" + code;
                        repeatedFailures.TryGetValue(failedSignature, out var repeated);
                        repeatedFailures[failedSignature] = repeated + 1;
                        if (repeated >= 2)
                            throw new AgentVerificationRequiredException("Công cụ " + executedName + " đã lỗi 3 lần với cùng đầu vào: "
                                + code + ". Cần xử lý nguyên nhân trước khi thử lại.");
                    }
                    else if (executedName != DeferredToolDiscovery.SearchToolName
                        && _registry.TryGet(executedName, out var successful) && successful.Namespace.Name != "core")
                    {
                        // Recovery must match a discovered candidate and the original supplied
                        // arguments. A successful unrelated operation cannot clear earlier errors.
                        unresolvedCalls.RemoveAll(f => f.RecoveryTools.Contains(executedName, StringComparer.Ordinal)
                            && (CompatibleRetryArguments(f.Arguments, executedCall.Arguments,
                                f.Code is "unknown_tool" or "tool_not_loaded")
                                || f.Code == "stale_state" && SameMutationWithFreshToken(f.Arguments, executedCall.Arguments)));
                        // Recovery references come from the executor after checking its recorded
                        // failed attempt and resource identity, not from the model's final text.
                        try
                        {
                            using var succeeded = JsonDocument.Parse(recoveryOutput);
                            if (succeeded.RootElement.ValueKind == JsonValueKind.Object
                                && succeeded.RootElement.TryGetProperty("resolvedFailureIds", out var resolved)
                                && resolved.ValueKind == JsonValueKind.Array)
                            {
                                var ids = resolved.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToHashSet();
                                unresolvedCalls.RemoveAll(f => f.Name == executedName && f.FailureId is not null && ids.Contains(f.FailureId));
                            }
                        }
                        catch (JsonException) { }
                    }
                }
                if (execution.ExecutedMutationSignatures.Count > 0)
                {
                    effectiveContract = effectiveContract.WithExecutedMutation();
                    // An earlier successful mutation cannot verify a later, different write.
                    mutationAwaitingVerification = true;
                }
                var results = execution.Results.ToArray();
                if (_verifier is not null)
                {
                    var report = await _verifier.VerifyAsync(
                        new AgentRuntimeVerificationContext(
                            effectiveContract,
                            toolRounds,
                            execution.Calls,
                            results)
                        {
                            Evidence = execution.Evidence,
                            RawToolOutputs = execution.RawToolOutputs,
                            MutationCallIds = execution.Calls.Where(call =>
                                execution.RawToolOutputs.ContainsKey(call.Id)
                                && _registry.TryGet(call.Name, out var descriptor) && descriptor.IsMutating)
                                .Select(call => call.Id).ToArray()
                        },
                        cancellationToken).ConfigureAwait(false);

                    if (report is not null)
                    {
                        mutationAwaitingVerification = false;
                        var effectiveReport = MergeVerificationReports(
                            latestVerification,
                            report);
                        latestVerification = effectiveReport;
                        verificationHistory.Add(effectiveReport);
                        request.VerificationObserver?.Invoke(effectiveReport, verificationHistory.Count - 1);
                        if (!effectiveReport.Passed)
                        {
                            if (effectiveReport.Failures.Count == 0)
                                throw new InvalidOperationException(
                                    "Verifier returned non-passing state without actionable failures.");
                            if (++repairRounds > request.MaxRepairRounds)
                                throw new InvalidOperationException(
                                    $"AgentRuntime exceeded the {request.MaxRepairRounds}-round repair budget.");

                            foreach (var signature in execution.ExecutedMutationSignatures)
                                failedMutationSignatures.Add(signature);

                            var repair = _repairController.Build(
                                effectiveContract,
                                effectiveReport);
                            if (results.Length == 0)
                                throw new InvalidOperationException(
                                    "Verification failure has no tool result continuation target.");

                            var feedbackIndex = FirstRepairFeedbackIndex(
                                execution.Calls,
                                results);
                            results[feedbackIndex] = results[feedbackIndex] with
                            {
                                Content = BoundToolOutput(
                                    results[feedbackIndex].Content
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
                                : execution.NewlyLoadedTools,
                            request.TakeSupplementalInput?.Invoke(false)),
                        cancellationToken),
                    usage,
                    cancellationToken, request.PublicTextObserver).ConfigureAwait(false);
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
        Guid turnId,
        int toolRound,
        IReadOnlyList<AgentTransportToolCall> transportCalls,
        IReadOnlySet<string> failedMutationSignatures,
        CancellationToken cancellationToken)
    {
        var calls = new global::H2AgentLab.ToolCall[transportCalls.Count];
        var results = new AgentToolResult?[transportCalls.Count];
        var evidence = new List<AgentEvidenceReference>();
        var rawToolOutputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var executedMutationSignatures = new List<string>();
        var scheduled = new List<(int OriginalIndex, ToolExecutionRequest Request, AgentRuntimePermissionRequest Permission, string? MutationSignature)>();
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
                ResolveCallableName(transportCall.Name),
                arguments);
            calls[i] = call;

            if (string.Equals(
                    call.Name,
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

            if (!_registry.TryGet(call.Name, out var descriptor))
            {
                var query = new string(transportCall.Name.Take(128)
                    .Select(c => char.IsAsciiLetterOrDigit(c) ? c : ' ').ToArray()).Trim();
                var recovery = query.Length == 0 ? null : _discovery.SearchAndLoad(query, 4);
                if (recovery is not null)
                    newlyLoadedSchemas.AddRange(recovery.CallableSchemas.Select(ToTransportTool));
                results[i] = new AgentToolResult(
                    transportCall.Id,
                    transportCall.Name,
                    JsonSerializer.Serialize(new
                    {
                        ok = false,
                        error = "unknown_tool",
                        message = "No operation was executed. Use tool_search and then call the exact returned function name and argument schema. Do not add a namespace prefix or invent tool names. Candidate schemas are available on this continuation.",
                        recoveryTools = recovery?.Trace.SelectedNames.Take(1).ToArray() ?? []
                    }),
                    IsError: true);
                continue;
            }

            if (!_discovery.LoadedSchemaNames.Contains(
                    descriptor.Name,
                    StringComparer.Ordinal))
            {
                var recovery = _discovery.SearchAndLoad(descriptor.Name, 1);
                newlyLoadedSchemas.AddRange(recovery.CallableSchemas.Select(ToTransportTool));
                results[i] = new AgentToolResult(
                    transportCall.Id,
                    transportCall.Name,
                    JsonSerializer.Serialize(new
                    {
                        ok = false,
                        error = "tool_not_loaded",
                        message = "No operation was executed. The schema has now been loaded; retry using its exact arguments."
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

            var mutationSignature = descriptor.IsMutating
                ? MutationSignature(call)
                : null;
            if (mutationSignature is not null
                && failedMutationSignatures.Contains(mutationSignature))
            {
                results[i] = new AgentToolResult(
                    transportCall.Id,
                    transportCall.Name,
                    JsonSerializer.Serialize(new
                    {
                        ok = false,
                        error = "repeated_failed_mutation",
                        message = "Host blocked an identical mutation that already failed verification. Re-observe state or change the corrective action.",
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
                permissionRequest with { ResourceKey = permission.ResourceKey },
                mutationSignature));
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
                var rawOutput = scheduledResult.Output ?? "";
                var deniedBeforeExecution = IsPermissionDenial(rawOutput);
                if (!deniedBeforeExecution) rawToolOutputs[calls[original].Id] = rawOutput;
                if (!deniedBeforeExecution && scheduled[i].MutationSignature is { } mutationSignature)
                    executedMutationSignatures.Add(mutationSignature);
                _permissionPolicy.ObserveResult(
                    scheduled[i].Permission,
                    rawOutput);

                var projection = deniedBeforeExecution ? null : _evidenceProjector?.Project(
                    scheduled[i].Request.Descriptor,
                    $"tool-result:{turnId:N}:{toolRound}:{original}",
                    ((long)toolRound * 1_000L) + original,
                    rawOutput);
                if (projection is not null)
                    evidence.Add(projection.Evidence);

                results[original] = new AgentToolResult(
                    calls[original].Id,
                    transportCalls[original].Name,
                    BoundToolOutput(projection?.ModelContent ?? rawOutput),
                    IsError: deniedBeforeExecution || IsToolFailure(rawOutput));
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
                .ToArray(),
            evidence.ToArray(),
            rawToolOutputs,
            executedMutationSignatures.ToArray());
    }

    private static string? ResourceKey(ToolDescriptor descriptor)
        => descriptor.ResourceScope?.ScopeId;

    private string ResolveCallableName(string supplied)
    {
        if (_registry.TryGet(supplied, out var exact)) return exact.Name;
        // Some models qualify the exact callable name with its declared namespace. Accept
        // only a registered namespace + exact name, never guessed verbs or fuzzy aliases.
        var separator = supplied.IndexOf(':');
        if (separator > 0 && _registry.TryGet(supplied[(separator + 1)..], out var descriptor)
            && supplied[..separator] == descriptor.Namespace.Name)
            return descriptor.Name;
        return supplied;
    }

    private static bool CompatibleRetryArguments(JsonElement failed, JsonElement retry, bool discoveryFailure)
    {
        if (JsonElement.DeepEquals(failed, retry)) return true;
        if (!discoveryFailure || failed.ValueKind != JsonValueKind.Object || retry.ValueKind != JsonValueKind.Object) return false;
        var sharedValue = false;
        foreach (var property in failed.EnumerateObject())
        {
            if (!retry.TryGetProperty(property.Name, out var actual)) continue;
            if (!JsonElement.DeepEquals(property.Value, actual)) return false;
            if (actual.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(actual.GetString())) sharedValue = true;
        }
        return sharedValue;
    }

    private static bool SameMutationWithFreshToken(JsonElement failed, JsonElement retry)
    {
        if (failed.ValueKind != JsonValueKind.Object || retry.ValueKind != JsonValueKind.Object) return false;
        static bool Token(string name) => name is "expectedHash" or "expected_hash" or "state_token";
        var before = failed.EnumerateObject().Where(p => !Token(p.Name)).ToDictionary(p => p.Name, p => p.Value);
        var after = retry.EnumerateObject().Where(p => !Token(p.Name)).ToDictionary(p => p.Name, p => p.Value);
        return before.Count > 0 && before.Count == after.Count && before.All(p => after.TryGetValue(p.Key, out var value) && JsonElement.DeepEquals(p.Value, value));
    }

    private static bool IsToolFailure(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && ((root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                    || (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False));
        }
        catch (JsonException) { return false; }
    }

    private static bool IsPermissionDenial(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var failed = (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                || (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False);
            if (!failed) return false;
            foreach (var name in new[] { "code", "error" })
                if (root.TryGetProperty(name, out var code) && code.ValueKind == JsonValueKind.String
                    && code.GetString() is "denied" or "permission_denied" or "permission_required" or "outside_resource_scope" or "expired_permission")
                    return true;
        }
        catch (JsonException) { }
        return false;
    }

    private static VerificationReport MergeVerificationReports(
        VerificationReport? previous,
        VerificationReport current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is null
            || !string.Equals(
                previous.VerifierId,
                current.VerifierId,
                StringComparison.Ordinal))
            return current;

        var criteria = previous.Criteria
            .ToDictionary(x => x.CriterionId, StringComparer.Ordinal);
        foreach (var result in current.Criteria)
            criteria[result.CriterionId] = result;

        return new VerificationReport(
            current.VerifierId,
            criteria.Values
                .OrderBy(x => x.CriterionId, StringComparer.Ordinal)
                .ToArray(),
            previous.ReportEvidenceIds
                .Concat(current.ReportEvidenceIds)
                .Distinct(StringComparer.Ordinal)
                .Take(256)
                .ToArray());
    }

    private int FirstRepairFeedbackIndex(
        IReadOnlyList<global::H2AgentLab.ToolCall> calls,
        IReadOnlyList<AgentToolResult> results)
    {
        for (var i = 0; i < calls.Count; i++)
        {
            if (i >= results.Count)
                break;
            if (results[i].IsError)
                continue;
            if (_registry.TryGet(calls[i].Name, out var descriptor)
                && descriptor.IsMutating)
                return i;
        }
        return 0;
    }

    private static string MutationSignature(global::H2AgentLab.ToolCall call)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonicalJson(writer, call.Arguments);
        var canonical = Encoding.UTF8.GetString(stream.ToArray());
        var material = Encoding.UTF8.GetBytes(call.Name + "\n" + canonical);
        return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(material))
            .ToLowerInvariant();
    }

    private static void WriteCanonicalJson(
        Utf8JsonWriter writer,
        JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject()
                    .OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonicalJson(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException(
                    "Unsupported JSON value in mutation signature.");
        }
    }

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
        CancellationToken cancellationToken,
        Action<string>? publicTextObserver = null)
    {
        var text = new StringBuilder();
        var calls = new List<AgentTransportToolCall>();
        var completed = false;
        string? finishReason = null;
        var lastFlush = Environment.TickCount64;

        await foreach (var item in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            switch (item.Kind)
            {
                case AgentTransportEventKind.TextDelta:
                    text.Append(item.Text);
                    if (Environment.TickCount64 - lastFlush >= 100)
                    { publicTextObserver?.Invoke(text.ToString()); lastFlush = Environment.TickCount64; }
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

        publicTextObserver?.Invoke(text.ToString());
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
            throw new AgentVerificationRequiredException(
                "Task requires verification, but no verifier report exists before final completion.");

        var outcome = VerificationCompletionGate.Evaluate(
            contract,
            [latestVerification]);
        try
        {
            AgentTaskCompletionGate.EnsureCanComplete(contract, outcome);
        }
        catch (InvalidOperationException ex)
        {
            throw new AgentVerificationRequiredException(ex.Message, ex);
        }
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
        IReadOnlyList<AgentToolDefinition> NewlyLoadedTools,
        IReadOnlyList<AgentEvidenceReference> Evidence,
        IReadOnlyDictionary<string, string> RawToolOutputs,
        IReadOnlyList<string> ExecutedMutationSignatures);

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

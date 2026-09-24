using System.Text;
using System.Text.Json;
using H2AgentLab.Context;
using H2AgentLab.Prompting;
using H2AgentLab.Session;
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
    Action<VerificationReport, int>? VerificationObserver = null,
    AgentRuntimeInvocation? Invocation = null,
    RuntimeCompactionResult? ContextCheckpoint = null,
    Func<bool, IReadOnlyList<AgentGoalInput>>? TakeGoalInput = null,
    Action<AgentTaskContract>? ContractObserver = null,
    Action<H2AgentOperationRecord>? JournalObserver = null,
    Action<H2AgentCompletionAssessment>? CompletionObserver = null,
    Func<IReadOnlyList<AgentRuntimeJobObservation>>? ObserveJobResults = null,
    Action<AgentContextCompactionRecord>? ContextCheckpointObserver = null,
    Action<AgentContextSourceRecord>? ContextSourceObserver = null);

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
    public H2AgentCompletionAssessment? Completion { get; init; }
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
    public IReadOnlyList<AgentVerificationAttempt> Attempts { get; init; } = [];
}

public interface IAgentRuntimeVerifier
{
    Task<VerificationReport?> VerifyAsync(
        AgentRuntimeVerificationContext context,
        CancellationToken cancellationToken);
}

public sealed class AgentVerificationRequiredException : InvalidOperationException
{
    public H2AgentCompletionAssessment? Completion { get; init; }
    /// <summary>User-facing host fallback only when the model transport itself cannot produce
    /// an explanation. Normal blocked answers remain model-generated.</summary>
    public string? PublicText { get; init; }
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
    private const int MaxToolOutputCharacters = ToolOutcomeBridge.MaxModelOutputCharacters;

    private readonly IAgentTransport _transport;
    private readonly AgentContextManager _contextManager;
    private readonly ToolRegistry _registry;
    private readonly DeferredToolDiscovery _discovery;
    private readonly ToolExecutionScheduler _scheduler;
    private readonly AgentRepairController _repairController;
    private readonly IAgentRuntimeVerifier? _verifier;
    private readonly IAgentRuntimePermissionPolicy _permissionPolicy;
    private readonly AgentRuntimeEvidenceProjector? _evidenceProjector;
    private readonly IAgentRuntimeHooks _hooks;
    private readonly RuntimeCompactionCoordinator? _workCompaction;
    internal AgentCompactionOptions WorkCompactionOptions { get; init; } = new();
    internal Func<string, string, AgentWorkSummary>? WorkSummarizer { get; init; }
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
        AgentRuntimeEvidenceProjector? evidenceProjector = null,
        IAgentRuntimeHooks? hooks = null,
        RuntimeCompactionCoordinator? compactionCoordinator = null)
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
        _hooks = hooks ?? new AgentRuntimeHooks();
        _workCompaction = compactionCoordinator;
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

        if (request.TakeGoalInput is not null && request.TakeSupplementalInput is not null)
            throw new ArgumentException("Use one authoritative user-input source, not two queues.");
        if ((request.ContextCheckpointObserver is null) != (request.ContextSourceObserver is null))
            throw new ArgumentException("Context source and activation journals must be paired.");
        var initialContract = request.Contract.Goals is null
            ? request.Contract.WithUserInput(new(request.Contract.TaskId, request.UserInput)) : request.Contract;
        request.ContractObserver?.Invoke(initialContract);
        cancellationToken.ThrowIfCancellationRequested();
        var invocation = request.Invocation ?? new AgentRuntimeInvocation(AgentRuntimeEntryPoint.Lab);
        invocation.Validate();
        // Both composition roots use this same bounded-context primitive. Lab may also pass
        // metadata for the real CompactionManager checkpoint it prepared; production does not
        // invent a durable checkpoint when its current context is only an in-memory snapshot.
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
            request.UserInput + (initialContract.Goals!.HasOutcomes ? "\n" + initialContract.Goals.Describe() : ""));
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
        var hookScope = new AgentRuntimeHookScope(taskId, turnId, invocation);
        var modelRequests = 0;
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
        var effectiveContract = initialContract;
        IReadOnlyList<string> TakeInput(bool closing)
        {
            var inputs = request.TakeGoalInput?.Invoke(closing)
                ?? (request.TakeSupplementalInput?.Invoke(closing) ?? [])
                    .Select(text => new AgentGoalInput(Guid.NewGuid(), text)).ToArray();
            foreach (var input in inputs) effectiveContract = effectiveContract.WithUserInput(input);
            if (inputs.Count > 0)
            {
                // Keep historical reports, but a prospective user waiver/supersession changes
                // only active goal coverage. It cannot remove a host mutation failure/unknown.
                if (latestVerification is not null)
                {
                    var retired = effectiveContract.Goals!.Obligations.Where(x => !x.Active).Select(x => x.Id).ToHashSet();
                    latestVerification = new(latestVerification.VerifierId,
                        latestVerification.Criteria.Where(x => !retired.Contains(x.CriterionId)), latestVerification.ReportEvidenceIds)
                        { ContributingVerifierIds = latestVerification.ContributingVerifierIds };
                }
                request.ContractObserver?.Invoke(effectiveContract);
            }
            var messages = inputs.Select(x => x.Text).ToList();
            if (effectiveContract.Goals!.HasOutcomes) messages.Add(effectiveContract.Goals.Describe());
            return messages;
        }
        // Polling a completion boundary must distinguish new user input from our own summary.
        IReadOnlyList<string> TakeClosingInput()
        {
            var before = effectiveContract.Goals!.RevisionId;
            var messages = TakeInput(true);
            return before == effectiveContract.Goals!.RevisionId ? [] : messages;
        }
        var assessment = new AgentCompletionAssessment(_evidenceProjector is not null);
        H2AgentCompletionAssessment? completion = null;
        var mutationAwaitingVerification = false;
        var unresolvedCalls = new List<RuntimeFailureObservation>();
        var pendingOperations = new Dictionary<string, ToolOutcome>(StringComparer.Ordinal);
        var uncertainResources = new HashSet<string>(StringComparer.Ordinal);
        RuntimeFailureObservation ObserveFailure(global::H2AgentLab.ToolCall call, ToolOutcome? outcome,
            string code, IEnumerable<string>? recoveryTools, string? failureId)
        {
            var target = AgentCompletionAssessment.Target(call, outcome?.Resource?.Id);
            var observedVersion = outcome?.Resource?.ObservedVersion;
            var previous = unresolvedCalls.LastOrDefault(f => f.Name == call.Name && f.TargetId == target);
            var changed = previous is null ? (bool?)null
                : !string.Equals(previous.ObservedVersion, observedVersion, StringComparison.Ordinal);
            var attempt = unresolvedCalls.Count(f => f.Name == call.Name && f.TargetId == target && f.Code == code) + 1;
            var recovery = (recoveryTools ?? []).Where(x => !string.IsNullOrWhiteSpace(x))
                .Append(call.Name).Distinct(StringComparer.Ordinal).Take(8).ToArray();
            return new(call.Name, code, recovery, call.Arguments.Clone(), failureId,
                call.Invocation!.InvocationId, outcome, target, observedVersion, changed, attempt);
        }
        var jobs = new AgentRuntimeJobs(request.JournalObserver);
        async Task ObserveFinishedJobsAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observedJobs = request.ObserveJobResults?.Invoke() ?? [];
            foreach (var (call, output) in jobs.Observe(observedJobs, effectiveContract, _registry))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_registry.TryGet(call.Name, out var descriptor)) throw new AgentVerificationRequiredException("Job tool is no longer registered.");
                var projection = _evidenceProjector?.Project(descriptor,
                    $"job-result:{call.Invocation!.InvocationId:N}", (long)toolRounds * 1000 + 999, output.DomainPayload);
                var jobEvidence = (projection is null ? Array.Empty<AgentEvidenceReference>() : new[] { projection.Evidence })
                    .Concat(_evidenceProjector is null ? [] : outcomeArtifacts()).ToArray();
                IEnumerable<AgentEvidenceReference> outcomeArtifacts() => output.Outcome.ArtifactRefs
                    .Select(id => _evidenceProjector!.ObserveJobOutput(id, output.Outcome.Job!.JobId));
                var outcome = output.Outcome with { EvidenceRefs = jobEvidence.Select(x => x.ReferenceId).ToArray() };
                var result = new AgentToolResult(call.Id, call.Name, projection?.ModelContent ?? output.DomainPayload,
                    outcome.IsError) { Outcome = outcome };
                var observation = new AgentRuntimeVerificationContext(effectiveContract, toolRounds, [call], [result])
                { Evidence = jobEvidence, RawToolOutputs = new Dictionary<string, string> { [call.Id] = output.DomainPayload },
                    MutationCallIds = descriptor.IsMutating && outcome.Effect != ToolMutationEffect.None ? [call.Id] : [] };
                assessment.ObserveJob(observation);
                observation = observation with { Attempts = assessment.Attempts };
                evidenceHistory.AddRange(jobEvidence);
                request.EvidenceObserver?.Invoke(jobEvidence);
                request.ToolResultObserver?.Invoke(result); // Host progress only: never resend a protocol tool result ID.
                if (outcome.IsPending) pendingOperations[outcome.Invocation.LogicalOperationId] = outcome;
                else
                {
                    pendingOperations.Remove(outcome.Invocation.LogicalOperationId);
                    if (outcome.Resource is not null && !pendingOperations.Values.Any(p => p.Resource?.Id == outcome.Resource.Id))
                        uncertainResources.Remove(outcome.Resource.Id);
                    _scheduler.ObserveCompletedJob(outcome);
                }
                if (outcome.IsError)
                    unresolvedCalls.Add(ObserveFailure(call, outcome, outcome.Error?.Code ?? "outcome_unknown",
                        outcome.Error?.RecoveryCandidates, null));
                await _hooks.AfterToolObservationAsync(new(hookScope, observation), cancellationToken).ConfigureAwait(false);
                if (_verifier is not null)
                {
                    var report = await _verifier.VerifyAsync(observation, cancellationToken).ConfigureAwait(false);
                    if (report is not null)
                    {
                        latestVerification = assessment.Observe(observation, report);
                        effectiveContract = effectiveContract.WithGoals(effectiveContract.Goals!.Observe(
                            observation.Contract.Goals!.RevisionId, latestVerification, evidenceHistory.ToArray()));
                        request.ContractObserver?.Invoke(effectiveContract);
                        verificationHistory.Add(latestVerification);
                        request.VerificationObserver?.Invoke(latestVerification, verificationHistory.Count - 1);
                    }
                }
                mutationAwaitingVerification = assessment.UnverifiedMutations > 0 || assessment.OutstandingProofs > 0;
            }
        }

        RuntimeContextCompactionTurn? compactedTurn = null;
        RuntimeRound? previousRound = null;
        async Task<RuntimeRound> SendModelAsync(AgentTransportStartRequest? start, AgentTransportContinuationRequest? next)
        {
            // Compaction is an explicit optional transport capability. Concrete Agent transports
            // implement it; injected/legacy transports keep their existing guarded continuation.
            if (start is not null && request.ContextCheckpointObserver is not null
                && _transport is IAgentContextRebaseTransport)
                compactedTurn = _workCompaction?.CreateWorkTurn(start, request.ContextCheckpointObserver, request.ContextSourceObserver!,
                    WorkCompactionOptions, WorkSummarizer);
            var requestIndex = ++modelRequests;
            (AgentTransportStartRequest Context, string BodySha256)? rebase = null;
            if (next is not null && compactedTurn is not null && previousRound is not null)
            {
                // This is a host snapshot, not a model proposal. Complete prior source and all
                // current mandatory anchors are retained even if the new context cannot fit.
                var anchors = JsonSerializer.SerializeToElement(new
                {
                    TaskId = taskId, TurnId = turnId, RevisionId = effectiveContract.Goals!.RevisionId,
                    Invocation = invocation, Contract = effectiveContract,
                    PendingOperations = pendingOperations.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { p.Key, p.Value }).ToArray(),
                    UnresolvedCalls = unresolvedCalls.Select(f => new { f.Name, f.Code, f.FailureId, f.InvocationId, f.Arguments }).ToArray(),
                    UncertainResources = uncertainResources.Order(StringComparer.Ordinal).ToArray(),
                    ObservedEvidence = evidenceHistory.ToArray(), Verification = latestVerification,
                    Completion = assessment.Snapshot(effectiveContract, unresolvedCalls.Count, pendingOperations.Count, latestVerification)
                });
                rebase = compactedTurn.Prepare(_transport, requestIndex, previousRound.Text,
                    previousRound.ToolCalls, next, effectiveContract.Goals.RevisionId, anchors, cancellationToken);
                if (rebase is not null)
                    request.CommentaryObserver?.Invoke("Đã lưu checkpoint context có nguồn; giữ yêu cầu hiện hành, công việc còn dở và bằng chứng. Không chạy lại công cụ.");
            }
            try
            {
                previousRound = await SendRequestAsync(hookScope, contextSnapshot, requestIndex, start, next,
                    usage, cancellationToken, request.PublicTextObserver, rebase).ConfigureAwait(false);
                return previousRound;
            }
            catch (OperationCanceledException) { throw; }
            catch (AgentVerificationRequiredException) { throw; }
            catch (Exception ex) when (ex is IOException or TimeoutException or System.Net.Http.HttpRequestException)
            {
                // Initial transport failures remain owned by the provider/AR-065 path so exact
                // provider diagnostics and retry policy are preserved. Only a continuation that
                // was already carrying Agent-visible recovery state becomes this technical card.
                if (start is not null) throw;
                var blocked = assessment.Snapshot(effectiveContract, unresolvedCalls.Count,
                    pendingOperations.Count, latestVerification);
                throw new AgentVerificationRequiredException("model_continuation_unavailable", ex)
                {
                    Completion = blocked with { State = blocked.VerifiedOutcomes > 0 ? "PartiallyCompleted" : "Blocked" },
                    PublicText = BuildModelUnavailableCard("model_continuation",
                        effectiveContract, unresolvedCalls, pendingOperations, latestVerification)
                };
            }
        }

        AgentRuntimeResult RuntimeResult(RuntimeRound current, H2AgentCompletionAssessment state)
            => new(current.Text, toolRounds, toolCalls, repairRounds, usage.Snapshot(), contextSnapshot,
                verificationHistory.ToArray(), _discovery.LoadedSchemaNames, promptCacheIdentity)
            {
                Evidence = evidenceHistory.ToArray(),
                EffectiveContract = effectiveContract,
                Completion = state
            };

        try
        {
            await _hooks.OnCheckpointAsync(new(hookScope, AgentRuntimeCheckpointKind.ContextPrepared,
                contextSnapshot, ContextCheckpoint: request.ContextCheckpoint), cancellationToken).ConfigureAwait(false);
            var round = await SendModelAsync(
                new AgentTransportStartRequest(taskId, turnId, messages, initialTools,
                    promptCacheKey, _transport.Capabilities.ParallelToolCalls), null).ConfigureAwait(false);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (round.ToolCalls.Count == 0)
                {
                    await ObserveFinishedJobsAsync().ConfigureAwait(false);
                    var supplements = TakeClosingInput();
                    if (supplements.Count > 0)
                    {
                        if (++toolRounds > request.MaxToolRounds)
                            throw new InvalidOperationException("Đã tới giới hạn số lượt bổ sung; nội dung bổ sung được giữ trong lịch sử.");
                        round = await SendModelAsync(null,
                            new(taskId, turnId, [], SupplementalUserMessages: supplements)).ConfigureAwait(false);
                        continue;
                    }
                    latestVerification = assessment.ApplyRevision(effectiveContract);
                    unresolvedCalls.RemoveAll(f => assessment.IsResolved(f.InvocationId));
                    mutationAwaitingVerification = assessment.UnverifiedMutations > 0 || assessment.OutstandingProofs > 0;
                    completion = assessment.Snapshot(effectiveContract, unresolvedCalls.Count, pendingOperations.Count, latestVerification);
                    request.CompletionObserver?.Invoke(completion with { State = "Verifying" });
                    await _hooks.BeforeCompletionAsync(new(hookScope, effectiveContract, round.Text,
                        latestVerification, unresolvedCalls.Count, mutationAwaitingVerification), cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (pendingOperations.Count > 0)
                        throw new AgentVerificationRequiredException("Chưa thể hoàn tất: còn công việc đang chạy hoặc tác động cần đối soát. "
                            + string.Join("; ", pendingOperations.Values.Take(8).Select(o => o.Status + (o.Error is null ? "" : " (" + o.Error.Code + ")")
                                + (o.Job is null ? "" : " job=" + o.Job.JobId)))
                            + ". Không tự lặp thao tác ghi.");

                    if (unresolvedCalls.Count > 0)
                    {
                        var blocked = completion with { State = completion.VerifiedOutcomes > 0 ? "PartiallyCompleted" : "Blocked" };
                        request.CompletionObserver?.Invoke(blocked);
                        throw new AgentVerificationRequiredException(
                            "Agent blocked after recovery: " + string.Join("; ",
                                unresolvedCalls.Take(8).Select(f => f.Name + " (" + f.Code + ")")))
                        {
                            Completion = blocked,
                            PublicText = string.IsNullOrWhiteSpace(round.Text) ? null : round.Text
                        };
                    }

                    if (latestVerification is { Passed: false })
                    {
                        var blocked = completion with { State = completion.VerifiedOutcomes > 0 ? "PartiallyCompleted" : "Blocked" };
                        request.CompletionObserver?.Invoke(blocked);
                        throw new AgentVerificationRequiredException("Agent blocked after verification failure.")
                        {
                            Completion = blocked,
                            PublicText = string.IsNullOrWhiteSpace(round.Text) ? null : round.Text
                        };
                    }

                    if (mutationAwaitingVerification)
                        throw new AgentVerificationRequiredException("The latest mutation has no verifier report.");

                    try { effectiveContract.Goals?.EnsureComplete(); }
                    catch (InvalidOperationException ex) { throw new AgentVerificationRequiredException(ex.Message, ex); }
                    EnsureFinalCompletionAllowed(effectiveContract, latestVerification);
                    if (_evidenceProjector is not null)
                        _evidenceProjector.EnsureReachable(evidenceHistory.Where(e => assessment.ProofIds.Contains(e.ReferenceId)
                            || effectiveContract.Goals!.Active.SelectMany(o => o.Evidence).Any(p => p.ReferenceId == e.ReferenceId)));
                    await _hooks.OnCheckpointAsync(new(hookScope, AgentRuntimeCheckpointKind.CompletionValidated,
                        contextSnapshot, toolRounds), cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    request.CompletionObserver?.Invoke(completion);
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
                        EffectiveContract = effectiveContract,
                        Completion = completion
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
                    uncertainResources,
                    request.JournalObserver is null ? null : jobs.Record,
                    cancellationToken).ConfigureAwait(false);

                var results = execution.Results.ToArray();
                var observation = new AgentRuntimeVerificationContext(
                    execution.ExecutedMutationSignatures.Count > 0 ? effectiveContract.WithExecutedMutation() : effectiveContract,
                    toolRounds, execution.Calls, results)
                {
                    Evidence = execution.Evidence,
                    RawToolOutputs = execution.RawToolOutputs,
                    MutationCallIds = execution.Calls.Where(call =>
                        execution.RawToolOutputs.ContainsKey(call.Id)
                        && execution.Results.Any(r => r.ToolCallId == call.Id && r.Outcome?.Effect != ToolMutationEffect.None)
                        && _registry.TryGet(call.Name, out var descriptor) && descriptor.IsMutating)
                        .Select(call => call.Id).ToArray()
                };
                assessment.Register(observation);
                jobs.Register(observation);
                observation = observation with { Attempts = assessment.Attempts };
                var retryCandidates = new List<(Guid Failed, Guid Replacement)>();
                await _hooks.AfterToolObservationAsync(new(hookScope, observation), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

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
                    if (result.Outcome is { IsPending: true } pending)
                    {
                        pendingOperations[pending.Invocation.LogicalOperationId] = pending;
                        if (pending.Resource is not null) uncertainResources.Add(pending.Resource.Id);
                    }
                    if (result.IsError)
                    {
                        var code = result.Outcome?.Error?.Code ?? "tool_error";
                        var recovery = new HashSet<string>(StringComparer.Ordinal) { executedName };
                        if (result.Outcome?.Error is { } typedError)
                        {
                            code = typedError.Code;
                            foreach (var candidate in typedError.RecoveryCandidates) recovery.Add(candidate);
                        }
                        string? failureId = null;
                        try
                        {
                            using var failure = JsonDocument.Parse(recoveryOutput);
                            if (failure.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                                code = error.GetString() ?? code;
                            if (failure.RootElement.TryGetProperty("recovery", out var detail)
                                && detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("code", out var typedCode)
                                && typedCode.ValueKind == JsonValueKind.String) code = typedCode.GetString() ?? code;
                            if (failure.RootElement.TryGetProperty("recoveryTools", out var choices)
                                && choices.ValueKind == JsonValueKind.Array)
                                foreach (var candidate in choices.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String)
                                    .Select(c => c.GetString()).Where(c => !string.IsNullOrWhiteSpace(c)))
                                    recovery.Add(candidate!);
                            if (failure.RootElement.TryGetProperty("failureId", out var id) && id.ValueKind == JsonValueKind.String)
                                failureId = id.GetString();
                        }
                        catch (JsonException) { }
                        if (code != "repeated_failed_mutation")
                            unresolvedCalls.Add(ObserveFailure(executedCall, result.Outcome, code, recovery, failureId));
                        // Retry count is carried in RuntimeFailureObservation. Mutating duplicates
                        // are still fail-closed before dispatch by failedMutationSignatures; all
                        // work remains bounded by MaxToolRounds/MaxRepairRounds.
                    }
                    else if (executedName != DeferredToolDiscovery.SearchToolName
                        && _registry.TryGet(executedName, out var successful) && successful.Namespace.Name != "core")
                    {
                        // Recovery must match a discovered candidate and the original supplied
                        // arguments. A successful unrelated operation cannot clear earlier errors.
                        foreach (var failure in unresolvedCalls.Where(f => f.RecoveryTools.Contains(executedName, StringComparer.Ordinal)
                            && (CompatibleRetryArguments(f.Arguments, executedCall.Arguments, f.Code,
                                f.Outcome?.Error?.RetryClass)
                                || f.Code == "stale_state" && SameMutationWithFreshToken(f.Arguments, executedCall.Arguments))))
                            retryCandidates.Add((failure.InvocationId, executedCall.Invocation!.InvocationId));
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
                                // A provider string is not an alternate-verifier verdict. It can
                                // nominate an exact retry only; the assessment below checks proof.
                                foreach (var failure in unresolvedCalls.Where(f => f.Name == executedName && f.FailureId is not null
                                    && ids.Contains(f.FailureId) && SameMutationWithFreshToken(f.Arguments, executedCall.Arguments)))
                                    retryCandidates.Add((failure.InvocationId, executedCall.Invocation!.InvocationId));
                            }
                        }
                        catch (JsonException) { }
                    }
                }
                if (execution.ExecutedMutationSignatures.Count > 0)
                {
                    effectiveContract = effectiveContract.WithExecutedMutation();
                    effectiveContract = effectiveContract.WithGoals(effectiveContract.Goals!.RecordMutation(observation.Contract.Goals!.RevisionId));
                    request.ContractObserver?.Invoke(effectiveContract);
                    // An earlier successful mutation cannot verify a later, different write.
                    mutationAwaitingVerification = true;
                }
                if (_verifier is not null)
                {
                    var report = await _verifier.VerifyAsync(observation, cancellationToken).ConfigureAwait(false);

                    if (report is not null)
                    {
                        var effectiveReport = assessment.Observe(observation, report);
                        // The normalized target-aware report, not a contradictory raw claim,
                        // updates outcomes. All evidence is from this run, never retrieved memory.
                        effectiveContract = effectiveContract.WithGoals(effectiveContract.Goals!.Observe(
                            observation.Contract.Goals!.RevisionId, effectiveReport, evidenceHistory.ToArray()));
                        request.ContractObserver?.Invoke(effectiveContract);
                        mutationAwaitingVerification = assessment.UnverifiedMutations > 0 || assessment.OutstandingProofs > 0;
                        latestVerification = effectiveReport;
                        verificationHistory.Add(effectiveReport);
                        request.VerificationObserver?.Invoke(effectiveReport, verificationHistory.Count - 1);
                        if (!effectiveReport.Passed)
                        {
                            if (effectiveReport.Failures.Count == 0)
                                throw new AgentVerificationRequiredException(
                                    "Result remains mechanically unverified; no successful completion is certified.")
                                { Completion = assessment.Snapshot(effectiveContract, unresolvedCalls.Count, pendingOperations.Count, effectiveReport) };

                            foreach (var signature in execution.ExecutedMutationSignatures)
                                failedMutationSignatures.Add(signature);

                            if (results.Length == 0)
                                throw new InvalidOperationException(
                                    "Verification failure has no tool result continuation target.");

                            var feedbackIndex = FirstRepairFeedbackIndex(execution.Calls, results);
                            if (repairRounds < request.MaxRepairRounds)
                            {
                                repairRounds++;
                                var repair = _repairController.Build(effectiveContract, effectiveReport);
                                results[feedbackIndex] = results[feedbackIndex] with
                                {
                                    Content = BoundToolOutput(results[feedbackIndex].Content
                                        + Environment.NewLine + Environment.NewLine
                                        + "[HOST VERIFICATION FAILED]" + Environment.NewLine + repair.PromptContext),
                                    IsError = true
                                };
                            }
                            else
                            {
                                results[feedbackIndex] = results[feedbackIndex] with
                                {
                                    Content = BoundToolOutput(results[feedbackIndex].Content
                                        + Environment.NewLine + Environment.NewLine
                                        + "[HOST VERIFICATION FAILED]" + Environment.NewLine
                                        + "Repair budget is exhausted. Do not claim completion; use the authoritative host recovery state to explain what remains."),
                                    IsError = true
                                };
                            }
                        }
                    }
                }

                await ObserveFinishedJobsAsync().ConfigureAwait(false);
                unresolvedCalls.RemoveAll(f => assessment.IsResolved(f.InvocationId)
                    || retryCandidates.Any(c => c.Failed == f.InvocationId && assessment.CanResolveExactRetry(c.Failed, c.Replacement)));
                mutationAwaitingVerification = assessment.UnverifiedMutations > 0 || assessment.OutstandingProofs > 0;
                completion = assessment.Snapshot(effectiveContract, unresolvedCalls.Count, pendingOperations.Count, latestVerification);
                request.CompletionObserver?.Invoke(completion);

                // Attach the authoritative typed failure state to the SAME tool-result continuation
                // the model already receives. This avoids a second hidden planning turn while making
                // stage/provider/target/retry/effect/attempts/recovery/obligations visible to Agent.
                if (unresolvedCalls.Count > 0 && results.Length > 0)
                {
                    var failureIndex = Array.FindIndex(results, r => r.IsError);
                    if (failureIndex < 0) failureIndex = 0;
                    var recoveryState = BuildRecoveryState(effectiveContract, completion, unresolvedCalls,
                        pendingOperations, latestVerification);
                    results[failureIndex] = results[failureIndex] with
                    {
                        Content = BoundToolOutput(results[failureIndex].Content
                            + Environment.NewLine + Environment.NewLine + recoveryState),
                        IsError = true
                    };
                }

                await _hooks.OnCheckpointAsync(new(hookScope, AgentRuntimeCheckpointKind.ToolBatchObserved,
                    contextSnapshot, toolRounds), cancellationToken).ConfigureAwait(false);
                round = await SendModelAsync(null,
                    new AgentTransportContinuationRequest(taskId, turnId, results,
                        execution.NewlyLoadedTools.Count == 0 ? null : execution.NewlyLoadedTools,
                        TakeInput(false))).ConfigureAwait(false);
            }
        }
        catch (AgentVerificationRequiredException ex) when (ex.Completion is null)
        {
            var blocked = completion ?? assessment.Snapshot(effectiveContract, unresolvedCalls.Count, pendingOperations.Count, latestVerification);
            throw new AgentVerificationRequiredException(ex.Message, ex)
            { Completion = blocked with { State = blocked.VerifiedOutcomes > 0 ? "PartiallyCompleted" : "Blocked" } };
        }
        catch (ToolInvocationCancelledException cancelled)
        {
            request.ToolResultObserver?.Invoke(new(cancelled.ToolCallId, cancelled.ToolName,
                cancelled.Observed.DomainPayload, true) { Outcome = cancelled.Observed.Outcome });
            _transport.Cancel();
            throw;
        }
        catch (OperationCanceledException)
        {
            _transport.Cancel();
            throw;
        }
    }

    // Every engine-level start/continuation, including steering and completion-repair, goes
    // through this awaited boundary BEFORE invoking the transport. Wire serialization and
    // provider-native continuation state remain owned by the existing transport implementation.
    private async Task<RuntimeRound> SendRequestAsync(AgentRuntimeHookScope scope,
        AgentContextSnapshot context, int requestIndex, AgentTransportStartRequest? start,
        AgentTransportContinuationRequest? continuation, MutableUsage usage,
        CancellationToken cancellationToken, Action<string>? publicTextObserver,
        (AgentTransportStartRequest Context, string BodySha256)? rebase = null)
    {
        var registeredNames = Array.AsReadOnly(_registry.Tools.Select(tool => tool.Name).ToArray());
        await _hooks.BeforeModelRequestAsync(new(scope, requestIndex, context, registeredNames,
            start, continuation), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await ReadRoundAsync(rebase is { } prepared
                ? ((IAgentContextRebaseTransport)_transport).RebaseContextAsync(prepared.Context,
                    continuation!, prepared.BodySha256, cancellationToken)
                : start is not null ? _transport.StartAsync(start, cancellationToken)
                : _transport.ContinueAsync(continuation!, cancellationToken),
            usage, cancellationToken, publicTextObserver).ConfigureAwait(false);
    }

    private static H2AgentOperationRecord JournalRecord(AgentTaskContract contract, Guid turnId,
        global::H2AgentLab.ToolCall call, string? resourceKey, ToolExecutionOutput? output)
    {
        static string Hash(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        var invocation = call.Invocation ?? throw new InvalidOperationException("Journal requires a bound invocation.");
        return new(invocation.InvocationId, invocation.LogicalOperationId, turnId, contract.Goals!.RevisionId,
            call.Id, call.Name, output is null ? "Dispatched" : "Result", output?.Outcome.Status.ToString() ?? "NotKnown",
            output?.Outcome.Effect.ToString() ?? "Unknown", Hash(call.Arguments.GetRawText()),
            resourceKey is null ? null : Hash(resourceKey), output is null ? null : Hash(output.DomainPayload), output?.Outcome.Error?.Code);
    }

    public void Cancel()
        => _transport.Cancel();

    private async Task<ExecutionBatch> ExecuteCallsAsync(
        AgentTaskContract contract,
        Guid turnId,
        int toolRound,
        IReadOnlyList<AgentTransportToolCall> transportCalls,
        IReadOnlySet<string> failedMutationSignatures,
        IReadOnlySet<string> uncertainResources,
        Action<H2AgentOperationRecord>? journalObserver,
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

            JsonElement arguments;
            var invalidArguments = false;
            try { arguments = ParseArguments(transportCall.ArgumentsJson); }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            { arguments = JsonSerializer.SerializeToElement(new { }); invalidArguments = true; }
            var resolvedName = ResolveCallableName(transportCall.Name);
            var call = new global::H2AgentLab.ToolCall(transportCall.Id, resolvedName, arguments)
            { Invocation = ToolInvocation.Create(contract.TaskId, resolvedName, arguments) };
            calls[i] = call;
            if (invalidArguments)
            {
                var rejected = ToolOutcomeBridge.Failure(call, null, "invalid_arguments", ToolErrorPhase.Preflight, ToolMutationEffect.None);
                results[i] = new(call.Id, transportCall.Name, rejected.DomainPayload, true) { Outcome = rejected.Outcome };
                continue;
            }

            if (string.Equals(
                    call.Name,
                    DeferredToolDiscovery.SearchToolName,
                    StringComparison.Ordinal))
            {
                string output;
                try { output = _discovery.ExecuteToolSearch(call); }
                catch (ArgumentException)
                {
                    var rejected = ToolOutcomeBridge.Failure(call, null, "invalid_arguments", ToolErrorPhase.Preflight, ToolMutationEffect.None);
                    results[i] = new(call.Id, transportCall.Name, rejected.DomainPayload, true) { Outcome = rejected.Outcome };
                    continue;
                }
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

            if (descriptor.Preflight?.Invoke(call) is { } preflight)
            {
                preflight = ToolOutcomeBridge.Validate(preflight, call, descriptor);
                var projected = _evidenceProjector?.Project(descriptor, $"tool-result:{turnId:N}:{toolRound}:{i}",
                    ((long)toolRound * 1_000L) + i, preflight.DomainPayload);
                if (projected is not null) evidence.Add(projected.Evidence);
                results[i] = new(call.Id, transportCall.Name, BoundToolOutput(projected?.ModelContent ?? preflight.DomainPayload),
                    preflight.Outcome.IsError) { Outcome = preflight.Outcome };
                continue;
            }
            if (!descriptor.CurrentReadiness.CanExecute)
            {
                var rejected = ToolOutcomeBridge.Failure(call, descriptor, ToolOutcomeBridge.ReadinessCode(descriptor.CurrentReadiness),
                    ToolErrorPhase.Preflight, ToolMutationEffect.None);
                results[i] = new(call.Id, transportCall.Name, rejected.DomainPayload, true) { Outcome = rejected.Outcome };
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

            if (descriptor.IsMutating && uncertainResources.Contains(OutcomeResourceId(permission.ResourceKey ?? resourceKey ?? descriptor.Name)))
            {
                // This attempt did not execute. The earlier pending operation remains unresolved;
                // changing arguments or tool names does not permit blindly writing that resource.
                var rejected = ToolOutcomeBridge.Failure(call, descriptor, "outcome_unknown", ToolErrorPhase.Preflight, ToolMutationEffect.None);
                results[i] = new(call.Id, transportCall.Name, rejected.DomainPayload, true) { Outcome = rejected.Outcome };
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
                    permission.ResourceKey)
                {
                    BeforeExecute = descriptor.IsMutating && journalObserver is not null
                        ? bound => journalObserver(JournalRecord(contract, turnId, bound, permission.ResourceKey, null)) : null,
                    AfterExecute = descriptor.IsMutating && journalObserver is not null
                        ? (bound, output) => journalObserver(JournalRecord(contract, turnId, bound, permission.ResourceKey, output)) : null
                },
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
                if (!deniedBeforeExecution && scheduledResult.Outcome?.Effect != ToolMutationEffect.None
                    && scheduled[i].MutationSignature is { } mutationSignature)
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

                var outcome = scheduledResult.Outcome ?? ToolOutcomeBridge.FromLegacy(calls[original], scheduled[i].Request.Descriptor, rawOutput).Outcome;
                outcome = outcome with { Resource = outcome.Resource ?? new ToolOutcomeResource(
                    OutcomeResourceId(scheduled[i].Permission.ResourceKey ?? scheduled[i].Request.Descriptor.Name)),
                    EvidenceRefs = projection is null ? outcome.EvidenceRefs : [projection.Evidence.ReferenceId] };
                var outputLimit = scheduled[i].Request.Descriptor.Limits.MaxOutputCharacters;
                if (projection is not null && rawOutput.Length > Math.Min(AgentRuntimeEvidenceProjector.MaxInlineToolOutputCharacters, outputLimit))
                    outcome = outcome with { Completeness = outcome.Completeness with { Complete = false, Reason = "artifact_projection" },
                        ArtifactRefs = [projection.Evidence.ReferenceId] };
                var modelContent = projection?.ModelContent ?? rawOutput;
                if (modelContent.Length > outputLimit)
                {
                    // Never silently claim an excerpt is complete. Production always supplies
                    // the existing artifact projector; an unconfigured test host has no cursor.
                    outcome = outcome with { Completeness = outcome.Completeness with { Complete = false, Reason = projection is null ? "bounded_excerpt_without_artifact" : "artifact_projection" } };
                    modelContent = projection is null ? modelContent[..Math.Max(0, outputLimit - 32)] + "…[truncated]"
                        : JsonSerializer.Serialize(new { complete = false, artifactRef = projection.Evidence.ReferenceId, reason = "read_existing_artifact" });
                }
                // Transports serialize Content, not the out-of-band Outcome. Keep cursor/job/effect
                // control data on the actual wire and reserve its space BEFORE bounding the body.
                // Raw domain bytes remain untouched in RawToolOutputs/the existing artifact store.
                if (outcome.IsPending || outcome.Completeness.NextCursor is not null)
                {
                    const string marker = "\n[HOST TOOL OUTCOME] ";
                    var metadata = JsonSerializer.Serialize(outcome);
                    var suffix = marker + metadata;
                    if (suffix.Length > outputLimit - 256)
                    {
                        // A legal escaped cursor may exceed a small tool's whole wire budget.
                        // Preserve exact metadata in the SAME store, not a lossy summary/new store.
                        var stored = _evidenceProjector?.Project(scheduled[i].Request.Descriptor,
                            $"tool-outcome:{turnId:N}:{toolRound}:{original}",
                            ((long)toolRound * 1_000L) + original, metadata, forceEvidence: true)
                            ?? throw new AgentVerificationRequiredException(
                                "Tool outcome metadata exceeds the output budget and no evidence store is configured. Do not repeat the operation.");
                        evidence.Add(stored.Evidence);
                        suffix = marker + JsonSerializer.Serialize(new { status = outcome.Status,
                            effect = outcome.Effect, complete = false, controlMetadataRef = stored.Evidence.ReferenceId,
                            next = "Read exact control metadata with read_tool_output before continuing." });
                    }
                    var room = outputLimit - suffix.Length;
                    if (modelContent.Length > room)
                    {
                        modelContent = projection is null
                            ? modelContent[..Math.Max(0, room - 32)] + "…[domain excerpt truncated]"
                            : JsonSerializer.Serialize(new { complete = false, artifactRef = projection.Evidence.ReferenceId,
                                reason = "read_existing_artifact" });
                    }
                    if (modelContent.Length + suffix.Length > outputLimit)
                        throw new AgentVerificationRequiredException("Tool control projection exceeds its advertised bound.");
                    modelContent += suffix;
                }
                results[original] = new AgentToolResult(calls[original].Id, transportCalls[original].Name,
                    BoundToolOutput(modelContent), IsError: deniedBeforeExecution || outcome.IsError) { Outcome = outcome };
            }
        }

        if (results.Any(x => x is null))
            throw new InvalidOperationException(
                "AgentRuntime tool batch did not produce one result per tool call.");

        for (var i = 0; i < results.Length; i++)
        {
            if (results[i]!.Outcome is not null) continue;
            _registry.TryGet(calls[i].Name, out var d);
            var outcome = ToolOutcomeBridge.FromLegacy(calls[i], d, results[i]!.Content, preflight: true).Outcome;
            results[i] = results[i]! with { Outcome = outcome };
        }
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

    private static string OutcomeResourceId(string key)
        => "scope-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

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

    private static bool CompatibleRetryArguments(JsonElement failed, JsonElement retry, string code,
        ToolRetryClass? retryClass)
    {
        if (JsonElement.DeepEquals(failed, retry)) return true;
        if (failed.ValueKind != JsonValueKind.Object || retry.ValueKind != JsonValueKind.Object) return false;
        if (code is "unknown_tool" or "tool_not_loaded")
        {
            var sharedValue = false;
            foreach (var property in failed.EnumerateObject())
            {
                if (!retry.TryGetProperty(property.Name, out var actual)) continue;
                if (!JsonElement.DeepEquals(property.Value, actual)) return false;
                if (actual.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(actual.GetString())) sharedValue = true;
            }
            return sharedValue;
        }
        if (retryClass == ToolRetryClass.CorrectInput)
            return SameTargetSelectors(failed, retry);
        return false;
    }

    private static bool SameTargetSelectors(JsonElement before, JsonElement after)
    {
        string[] selectors = ["path", "destination", "url", "session_id", "resource_id", "window_id",
            "document_id", "sheet", "sheet_name", "range", "address"];
        var compared = false;
        foreach (var name in selectors)
        {
            var hasBefore = before.TryGetProperty(name, out var a);
            var hasAfter = after.TryGetProperty(name, out var b);
            if (hasBefore != hasAfter) return false;
            if (!hasBefore) continue;
            compared = true;
            if (!JsonElement.DeepEquals(a, b)) return false;
        }
        return compared;
    }

    private static bool SameMutationWithFreshToken(JsonElement failed, JsonElement retry)
    {
        if (failed.ValueKind != JsonValueKind.Object || retry.ValueKind != JsonValueKind.Object) return false;
        static bool Token(string name) => name is "expectedHash" or "expected_hash" or "state_token";
        var before = failed.EnumerateObject().Where(p => !Token(p.Name)).ToDictionary(p => p.Name, p => p.Value);
        var after = retry.EnumerateObject().Where(p => !Token(p.Name)).ToDictionary(p => p.Name, p => p.Value);
        return before.Count > 0 && before.Count == after.Count && before.All(p => after.TryGetValue(p.Key, out var value) && JsonElement.DeepEquals(p.Value, value));
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

    private sealed record RuntimeFailureObservation(
        string Name, string Code, string[] RecoveryTools, JsonElement Arguments, string? FailureId,
        Guid InvocationId, ToolOutcome? Outcome, string TargetId, string? ObservedVersion,
        bool? StateChangedSincePrevious, int Attempt);

    private static string BuildRecoveryState(AgentTaskContract contract, H2AgentCompletionAssessment completion,
        IReadOnlyList<RuntimeFailureObservation> failures, IReadOnlyDictionary<string, ToolOutcome> pending,
        VerificationReport? verification)
    {
        var unresolved = contract.Goals!.Active.Where(x => x.Status != AgentObligationStatus.Verified)
            .Select(x => new { id = x.Id, status = x.Status.ToString(), requirement = x.Requirement }).ToArray();
        var verified = contract.Goals.Active.Where(x => x.Status == AgentObligationStatus.Verified).Select(x => x.Id).ToArray();
        var payload = JsonSerializer.Serialize(new
        {
            schema = "h2-agent-recovery-state/v1",
            state = "recovery_required",
            completion,
            failures = failures.TakeLast(8).Select(f => new
            {
                failureId = f.FailureId, invocationId = f.InvocationId,
                stage = f.Outcome?.Error?.Phase.ToString() ?? "Execution", tool = f.Name,
                providerId = f.Outcome?.Error?.ProviderId, providerVersion = f.Outcome?.Error?.ProviderVersion,
                errorCode = f.Code, safeMessage = f.Outcome?.Error?.SafeMessage,
                targetResourceId = f.Outcome?.Resource?.Id ?? f.TargetId, observedVersion = f.ObservedVersion,
                retryClass = f.Outcome?.Error?.RetryClass.ToString() ?? ToolRetryClass.Never.ToString(),
                stateChangedSinceLastAttempt = f.StateChangedSincePrevious,
                mutationEffect = (f.Outcome?.Effect ?? ToolMutationEffect.None).ToString(),
                attemptsAlreadyMade = f.Attempt, safeRecoveryCandidates = f.RecoveryTools,
                forbiddenSemanticFallbacks = (f.Outcome?.Effect is ToolMutationEffect.Unknown or ToolMutationEffect.PartiallyApplied)
                    ? new[] { "Do not replay a mutation until effects are reconciled.",
                        "Do not change resource/source identity or broaden permission without explicit user approval." }
                    : new[] { "Do not change resource/source identity or broaden permission without explicit user approval." },
                evidenceOrFailureRefs = (f.Outcome?.EvidenceRefs ?? [])
                    .Concat(string.IsNullOrWhiteSpace(f.FailureId) ? [] : [f.FailureId!]).Distinct(StringComparer.Ordinal).ToArray()
            }).ToArray(),
            pendingOperations = pending.Values.Take(8).Select(o => new
            {
                operationId = o.Invocation.LogicalOperationId, status = o.Status.ToString(),
                mutationEffect = o.Effect.ToString(), resourceId = o.Resource?.Id,
                observedVersion = o.Resource?.ObservedVersion, jobId = o.Job?.JobId,
                errorCode = o.Error?.Code, retryClass = o.Error?.RetryClass.ToString()
            }).ToArray(),
            unresolvedObligations = unresolved, verifiedObligationIds = verified,
            verification = verification is null ? null : new
            {
                verification.VerifierId, passed = verification.Passed,
                criteria = verification.Criteria.Select(c => new
                { c.CriterionId, status = c.Status.ToString(), c.EvidenceIds, failure = c.Failure?.Message }).ToArray()
            }
        });
        var instruction = "Continue the original user goal without a new user prompt when a safe goal-preserving recovery remains. "
            + "Do not blindly repeat the same call without changed evidence. Unknown/partial mutation effects require reconciliation before replay. "
            + "If no allowed recovery remains, call no tool and write the specific user-facing blocked/partial explanation: what succeeded, what remains, exact known cause, attempts already made, unsafe/non-equivalent fallbacks not used, and the user/app/config change needed. Do not claim completion.";
        return "[HOST RECOVERY STATE]" + Environment.NewLine + payload
            + Environment.NewLine + "[HOST RECOVERY INSTRUCTION]" + Environment.NewLine + instruction;
    }

    private static string BuildModelUnavailableCard(string phase, AgentTaskContract contract,
        IReadOnlyList<RuntimeFailureObservation> failures, IReadOnlyDictionary<string, ToolOutcome> pending,
        VerificationReport? verification)
    {
        var remaining = contract.Goals!.Active.Where(x => x.Status != AgentObligationStatus.Verified)
            .Select(x => x.Id).Take(8).ToArray();
        var effects = failures.Select(f => f.Outcome?.Effect ?? ToolMutationEffect.None)
            .Concat(pending.Values.Select(p => p.Effect)).Distinct().ToArray();
        var effectText = effects.Length == 0 || effects.All(x => x == ToolMutationEffect.None)
            ? "không ghi nhận tác động ghi từ phần lỗi này"
            : "có trạng thái tác động cần giữ nguyên/đối soát: " + string.Join(", ", effects);
        var cause = failures.LastOrDefault()?.Code ?? verification?.Failures.FirstOrDefault()?.Message ?? "model_transport_unavailable";
        var remainText = remaining.Length == 0 ? "mục tiêu hiện hành chưa thể xác nhận hoàn tất"
            : "còn các nghĩa vụ " + string.Join(", ", remaining);
        return "Agent không thể tiếp tục ở pha " + phase + " vì kết nối model/API không còn khả dụng. "
            + "Nguyên nhân kỹ thuật gần nhất: " + cause + "; " + effectText + "; " + remainText + ". "
            + "Cần khôi phục kết nối/cấu hình model rồi tiếp tục từ trạng thái hiện có. H2 không tự lặp thao tác có tác động chưa chắc chắn.";
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

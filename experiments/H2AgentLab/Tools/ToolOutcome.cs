using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace H2AgentLab.Tools;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ToolOutcomeStatus { Succeeded, Running, Rejected, Failed, Cancelled, PartiallyApplied, OutcomeUnknown }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ToolMutationEffect { None, Applied, PartiallyApplied, Unknown }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ToolVerificationStatus { NotRun, Passed, Failed }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ToolErrorPhase { Preflight, Execution, Verification }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ToolRetryClass { Never, CorrectInput, Configure, Reobserve, WaitThenReobserve, ReconcileRequired }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ToolReadinessState { Ready, Degraded, NeedsConfiguration, Busy, Unavailable, Unsupported }

/// <summary>Host identity, not model-supplied arguments and not an idempotency guarantee.
/// Same task/name/arguments share an operation identity; every dispatch attempt has its own ID.
/// Cross-restart/chunk/job reconciliation remains owned by the later journal/job work.</summary>
public sealed record ToolInvocation(Guid InvocationId, string LogicalOperationId)
{
    public static ToolInvocation Create(Guid taskId, string name, JsonElement arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Canonical(writer, arguments);
        var bytes = Encoding.UTF8.GetBytes(taskId.ToString("N") + "\n" + name + "\n" + Encoding.UTF8.GetString(stream.ToArray()));
        return new(Guid.NewGuid(), "op-" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static void Canonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var p in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(p.Name); Canonical(writer, p.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Canonical(writer, item); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }

    internal static ToolInvocation Bind(global::H2AgentLab.ToolCall call)
        => call.Invocation ?? Create(Guid.NewGuid(), call.Name, call.Arguments);
}

public sealed record ToolCompleteness(bool Complete, string? NextCursor = null, string? Reason = null);
public sealed record ToolOutcomeVerification(ToolVerificationStatus Status, IReadOnlyList<string> ReportRefs);
public sealed record ToolOutcomeResource(string Id, string? ObservedVersion = null);
public sealed record ToolOutcomeJob(string JobId, string? OutputCursor = null);
public sealed record ToolOutcomeError(string Code, ToolErrorPhase Phase, string SafeMessage,
    ToolRetryClass RetryClass, IReadOnlyList<string> RecoveryCandidates,
    ToolMutationEffect MutationEffect, string? ProviderId = null, string? ProviderVersion = null);

/// <summary>Small out-of-band envelope metadata. Domain bytes stay in ToolExecutionOutput and
/// the existing artifact store, never in a second UI/evidence database. Execution is not verification.</summary>
public sealed record ToolOutcome(ToolInvocation Invocation, ToolOutcomeStatus Status, ToolMutationEffect Effect,
    ToolCompleteness Completeness, ToolOutcomeVerification Verification,
    ToolOutcomeError? Error = null, ToolOutcomeJob? Job = null, ToolOutcomeResource? Resource = null)
{
    public int SchemaVersion => 1;
    public IReadOnlyList<string> ArtifactRefs { get; init; } = [];
    public IReadOnlyList<string> EvidenceRefs { get; init; } = [];
    [JsonIgnore] public bool IsError => Status is not (ToolOutcomeStatus.Succeeded or ToolOutcomeStatus.Running);
    [JsonIgnore] public bool NeedsReconciliation => Effect is ToolMutationEffect.Unknown or ToolMutationEffect.PartiallyApplied;
    [JsonIgnore] public bool IsPending => Status == ToolOutcomeStatus.Running || NeedsReconciliation;

    public static ToolOutcome Success(global::H2AgentLab.ToolCall call, ToolMutationEffect effect,
        ToolCompleteness? completeness = null)
        => new(ToolInvocation.Bind(call), ToolOutcomeStatus.Succeeded, effect,
            completeness ?? new(false, Reason: "legacy_unspecified"), new(ToolVerificationStatus.NotRun, []));
}

public sealed record ToolExecutionOutput(string DomainPayload, ToolOutcome Outcome)
{
    // An explicit envelope is available to new consumers. Legacy wire payloads and verifier inputs
    // retain their exact shape during migration; metadata is attached to AgentToolResult separately.
    public string ToEnvelopeJson()
    {
        JsonElement data;
        try { using var parsed = JsonDocument.Parse(DomainPayload); data = parsed.RootElement.Clone(); }
        catch (JsonException) { data = JsonSerializer.SerializeToElement(DomainPayload); }
        return JsonSerializer.Serialize(new { schemaVersion = Outcome.SchemaVersion,
            invocationId = Outcome.Invocation.InvocationId, logicalOperationId = Outcome.Invocation.LogicalOperationId,
            status = Outcome.Status, effect = Outcome.Effect, resource = Outcome.Resource, data,
            artifactRefs = Outcome.ArtifactRefs, evidenceRefs = Outcome.EvidenceRefs,
            completeness = Outcome.Completeness, job = Outcome.Job, error = Outcome.Error, verification = Outcome.Verification });
    }
}

public interface IAgentToolOutcomeExecutor : IAgentToolExecutor
{
    ValueTask<ToolExecutionOutput> ExecuteOutcomeAsync(global::H2AgentLab.ToolCall call, CancellationToken cancellationToken);
}

/// <summary>Compatibility bridge: direct legacy callers still receive the domain payload.</summary>
public sealed class DelegatingOutcomeToolExecutor(string executorId,
    Func<global::H2AgentLab.ToolCall, CancellationToken, ValueTask<ToolExecutionOutput>> execute) : IAgentToolOutcomeExecutor
{
    public string ExecutorId { get; } = ToolNamespace.NormalizeId(executorId, nameof(executorId));
    private readonly Func<global::H2AgentLab.ToolCall, CancellationToken, ValueTask<ToolExecutionOutput>> _execute
        = execute ?? throw new ArgumentNullException(nameof(execute));
    public ValueTask<ToolExecutionOutput> ExecuteOutcomeAsync(global::H2AgentLab.ToolCall call, CancellationToken cancellationToken)
        => _execute(call with { Invocation = ToolInvocation.Bind(call) }, cancellationToken);
    public async ValueTask<string> ExecuteAsync(global::H2AgentLab.ToolCall call, CancellationToken cancellationToken)
        => (await ExecuteOutcomeAsync(call, cancellationToken).ConfigureAwait(false)).DomainPayload;
}

/// <summary>Only throw this before any effect; ordinary executor exceptions are NOT preflight proof.</summary>
public sealed class ToolPreflightException(string code) : InvalidOperationException(ToolOutcomeBridge.SafeMessage(code))
{
    public string Code { get; } = code;
}

public sealed class ToolInvocationCancelledException(ToolExecutionOutput observed, string toolName,
    string toolCallId, OperationCanceledException inner, CancellationToken token)
    : OperationCanceledException("Tool invocation cancelled; effects must be inspected separately.", inner, token)
{
    public ToolExecutionOutput Observed { get; } = observed;
    public string ToolName { get; } = toolName;
    public string ToolCallId { get; } = toolCallId;
}

public sealed record ToolReadiness(ToolReadinessState State, string? ReasonCode = null)
{
    public bool CanExecute => State is ToolReadinessState.Ready or ToolReadinessState.Degraded;
    public string SafeReason => ToolOutcomeBridge.SafeMessage(ReasonCode ?? (State switch {
        ToolReadinessState.Ready => "ready", ToolReadinessState.Degraded => "not_probed",
        ToolReadinessState.NeedsConfiguration => "needs_configuration", ToolReadinessState.Busy => "provider_busy",
        ToolReadinessState.Unsupported => "unsupported_operation", _ => "provider_unavailable" }));
}
public enum ToolResultFormat { Auto, Json, Text }
public sealed record ToolContractLimits(int MaxOutputCharacters = ToolOutcomeBridge.MaxModelOutputCharacters,
    int? MaxBatchItems = null, bool SupportsPagination = false);
public sealed record ToolCapabilityNotice(string Name, string Description, ToolReadiness Readiness);

/// <summary>Typed control metadata only: never searches for words such as "success" in prose,
/// never imports provider verification claims, never retries, and never logs exception bodies.</summary>
public static class ToolOutcomeBridge
{
    public const int MaxModelOutputCharacters = 64_000;
    private const int MaxMetadataCharacters = 8_192;

    public static async ValueTask<ToolExecutionOutput> ExecuteAsync(ToolDescriptor descriptor,
        global::H2AgentLab.ToolCall call, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        call = call with { Invocation = ToolInvocation.Bind(call) };
        if (descriptor.Preflight?.Invoke(call) is { } preflight) return Validate(preflight, call, descriptor);
        var readiness = descriptor.CurrentReadiness;
        if (!readiness.CanExecute)
            return Failure(call, descriptor, ReadinessCode(readiness), ToolErrorPhase.Preflight, ToolMutationEffect.None);
        try
        {
            var output = descriptor.Executor is IAgentToolOutcomeExecutor typed
                ? await typed.ExecuteOutcomeAsync(call, ct).ConfigureAwait(false)
                : FromLegacy(call, descriptor, await descriptor.Executor.ExecuteAsync(call, ct).ConfigureAwait(false));
            return Validate(output, call, descriptor);
        }
        catch (ToolInvocationCancelledException) { throw; }
        catch (OperationCanceledException ex)
        {
            var effect = descriptor.IsMutating ? ToolMutationEffect.Unknown : ToolMutationEffect.None;
            var cancelled = Failure(call, descriptor, "cancelled", ToolErrorPhase.Execution, effect);
            cancelled = cancelled with { Outcome = cancelled.Outcome with { Status = ToolOutcomeStatus.Cancelled } };
            throw new ToolInvocationCancelledException(cancelled, call.Name, call.Id, ex, ct);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ArgumentException
            or InvalidOperationException or UnauthorizedAccessException or TimeoutException or JsonException
            or NotSupportedException or FormatException or System.ComponentModel.Win32Exception)
        { return FromException(call, descriptor, ex); }
    }

    public static ToolExecutionOutput FromException(global::H2AgentLab.ToolCall call, ToolDescriptor descriptor,
        Exception ex, string? providerCode = null)
    {
        var preflight = ex is ToolPreflightException;
        var code = providerCode ?? (ex switch {
            ToolPreflightException known => known.Code,
            TimeoutException => "deadline_exceeded",
            HttpRequestException http when (int?)http.StatusCode == 429 => "rate_limited",
            HttpRequestException => "connection_lost",
            FileNotFoundException or DirectoryNotFoundException => "resource_not_found",
            UnauthorizedAccessException => "permission_denied",
            ArgumentException or JsonException or FormatException => "invalid_arguments",
            NotSupportedException => "unsupported_operation",
            IOException io when (io.HResult & 0xffff) is 32 or 33 => "provider_busy",
            IOException => "connection_lost", _ => "tool_failed" });
        return Failure(call, descriptor, code, preflight ? ToolErrorPhase.Preflight : ToolErrorPhase.Execution,
            !preflight && descriptor.IsMutating ? ToolMutationEffect.Unknown : ToolMutationEffect.None);
    }

    public static ToolExecutionOutput Failure(global::H2AgentLab.ToolCall call, ToolDescriptor? descriptor,
        string code, ToolErrorPhase phase, ToolMutationEffect effect, string? originalPayload = null)
    {
        code = NormalizeCode(code);
        var retry = effect is ToolMutationEffect.Unknown or ToolMutationEffect.PartiallyApplied
            ? ToolRetryClass.ReconcileRequired : code switch {
                "invalid_arguments" => ToolRetryClass.CorrectInput,
                "needs_configuration" or "provider_unavailable" => ToolRetryClass.Configure,
                "stale_resource" or "resource_not_found" or "ambiguous_target" or "target_not_grounded" => ToolRetryClass.Reobserve,
                "provider_busy" or "modal_blocked" or "rate_limited" or "deadline_exceeded" or "connection_lost" => ToolRetryClass.WaitThenReobserve,
                _ => ToolRetryClass.Never };
        var candidates = retry switch {
            ToolRetryClass.ReconcileRequired => new[] { "inspect_effects_without_repeating_write" },
            ToolRetryClass.Configure => ["configure_existing_provider"],
            ToolRetryClass.CorrectInput => ["inspect_advertised_schema"],
            ToolRetryClass.Reobserve => ["read_current_resource"],
            ToolRetryClass.WaitThenReobserve => ["wait_then_read_current_state"],
            _ => Array.Empty<string>() };
        var status = effect switch {
            ToolMutationEffect.PartiallyApplied => ToolOutcomeStatus.PartiallyApplied,
            ToolMutationEffect.Unknown => ToolOutcomeStatus.OutcomeUnknown,
            _ => phase == ToolErrorPhase.Preflight ? ToolOutcomeStatus.Rejected : ToolOutcomeStatus.Failed };
        var error = new ToolOutcomeError(code, phase, SafeMessage(code), retry, candidates, effect,
            descriptor?.Provenance?.ProviderId, descriptor?.Provenance?.ProviderVersion);
        var outcome = new ToolOutcome(ToolInvocation.Bind(call), status, effect,
            new(false, Reason: "operation_not_complete"), new(ToolVerificationStatus.NotRun, []), error);
        return new(originalPayload ?? JsonSerializer.Serialize(new { ok = false, error = code,
            message = error.SafeMessage, status, effect, invocationId = outcome.Invocation.InvocationId,
            logicalOperationId = outcome.Invocation.LogicalOperationId, retryClass = retry,
            next = candidates }), outcome);
    }

    public static ToolExecutionOutput FromLegacy(global::H2AgentLab.ToolCall call, ToolDescriptor? descriptor,
        string? payload, bool preflight = false)
    {
        payload ??= "";
        var mutating = descriptor?.IsMutating == true;
        var complete = new ToolCompleteness(false, Reason: "legacy_unspecified");
        try
        {
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                // Wrong scalar types and conflicting success flags are malformed control data.
                bool? ok = null;
                foreach (var key in new[] { "ok", "success", "isError" })
                    if (root.TryGetProperty(key, out var flag))
                    {
                        if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            return Failure(call, descriptor, "invalid_result", ToolErrorPhase.Execution,
                                mutating ? ToolMutationEffect.Unknown : ToolMutationEffect.None, payload);
                        var succeeded = key == "isError" ? !flag.GetBoolean() : flag.GetBoolean();
                        if (ok.HasValue && ok.Value != succeeded)
                            return Failure(call, descriptor, "invalid_result", ToolErrorPhase.Execution,
                                mutating ? ToolMutationEffect.Unknown : ToolMutationEffect.None, payload);
                        ok = succeeded;
                    }
                var code = String(root, "code") ?? String(root, "error") ?? "tool_failed";
                if (root.TryGetProperty("recovery", out var recovery) && recovery.ValueKind == JsonValueKind.Object)
                    code = String(recovery, "code") ?? code;
                if (root.TryGetProperty("timed_out", out var timed) && timed.ValueKind == JsonValueKind.True) code = "deadline_exceeded";
                if (ok == false)
                {
                    var rejectedBeforeEffect = preflight
                        || root.TryGetProperty("mutationApplied", out var applied) && applied.ValueKind == JsonValueKind.False;
                    var processReturned = (root.TryGetProperty("exit_code", out var exit) || root.TryGetProperty("exitCode", out exit))
                        && exit.ValueKind == JsonValueKind.Number && code != "deadline_exceeded";
                    var effect = !mutating || rejectedBeforeEffect ? ToolMutationEffect.None
                        : processReturned ? ToolMutationEffect.Applied : ToolMutationEffect.Unknown;
                    return Failure(call, descriptor, code, rejectedBeforeEffect ? ToolErrorPhase.Preflight : ToolErrorPhase.Execution,
                        effect, payload);
                }
                if (root.TryGetProperty("contentAvailable", out var available) && available.ValueKind == JsonValueKind.False)
                    complete = new(false, Reason: "metadata_only");
                else if (root.TryGetProperty("truncated", out var trunc) || root.TryGetProperty("Truncated", out trunc))
                {
                    if (trunc.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        return Failure(call, descriptor, "invalid_result", ToolErrorPhase.Execution,
                            mutating ? ToolMutationEffect.Unknown : ToolMutationEffect.None, payload);
                    var cursor = String(root, "nextCursor");
                    if (root.TryGetProperty("nextOffset", out var offset) && offset.ValueKind == JsonValueKind.Number) cursor = offset.GetRawText();
                    complete = new(!trunc.GetBoolean(), trunc.GetBoolean() ? cursor : null,
                        trunc.GetBoolean() ? "bounded_excerpt" : null);
                }
            }
        }
        catch (JsonException)
        {
            // A descriptor may explicitly permit text, but structured JSON must parse fully.
            // Text and a word such as "success" are never verification evidence.
            var start = payload.AsSpan().TrimStart();
            if (descriptor?.ResultFormat == ToolResultFormat.Text
                || descriptor?.ResultFormat == ToolResultFormat.Auto && !start.StartsWith("{") && !start.StartsWith("["))
                return new(payload, ToolOutcome.Success(call, mutating ? ToolMutationEffect.Applied : ToolMutationEffect.None));
            return Failure(call, descriptor, "invalid_result", ToolErrorPhase.Execution,
                mutating ? ToolMutationEffect.Unknown : ToolMutationEffect.None, payload);
        }
        return new(payload, ToolOutcome.Success(call,
            mutating ? ToolMutationEffect.Applied : ToolMutationEffect.None, complete));
    }

    public static ToolExecutionOutput Validate(ToolExecutionOutput? result, global::H2AgentLab.ToolCall call, ToolDescriptor descriptor)
    {
        var o = result?.Outcome;
        bool token(string? s, int max) => s is { Length: > 0 } && s.Length <= max && !s.Any(char.IsControl);
        bool refs(IReadOnlyList<string>? items, int count, int size) => items is not null && items.Count <= count && items.All(s => token(s, size));
        var valid = o is not null && o.Invocation is not null && o.Invocation == call.Invocation && o.Invocation.InvocationId != Guid.Empty
            && token(o.Invocation.LogicalOperationId, 128) && Enum.IsDefined(o.Status) && Enum.IsDefined(o.Effect)
            && o.Completeness is not null && o.Verification is not null && Enum.IsDefined(o.Verification.Status)
            && refs(o.ArtifactRefs, 32, 256) && refs(o.EvidenceRefs, 32, 256)
            && refs(o.Verification.ReportRefs, 32, 256)
            && (o.Completeness.NextCursor is null || token(o.Completeness.NextCursor, 512))
            && !(o.Completeness.Complete && o.Completeness.NextCursor is not null)
            && (o.Completeness.Reason is null || token(o.Completeness.Reason, 128))
            && (o.Resource is null || token(o.Resource.Id, 256) && (o.Resource.ObservedVersion is null || token(o.Resource.ObservedVersion, 256)))
            && (o.Job is null || token(o.Job.JobId, 128) && (o.Job.OutputCursor is null || token(o.Job.OutputCursor, 512)))
            && (o.Status != ToolOutcomeStatus.Running || o.Job is not null && !o.Completeness.Complete)
            && (o.Status != ToolOutcomeStatus.Rejected || o.Effect == ToolMutationEffect.None)
            && (o.Status != ToolOutcomeStatus.Succeeded || !o.NeedsReconciliation && o.Error is null)
            && (o.Status != ToolOutcomeStatus.PartiallyApplied || o.Effect == ToolMutationEffect.PartiallyApplied)
            && (o.Status != ToolOutcomeStatus.OutcomeUnknown || o.Effect == ToolMutationEffect.Unknown)
            && (descriptor.IsMutating || o.Effect == ToolMutationEffect.None)
            && (!o.IsError || o.Error is not null)
            && (o.Error is null || Enum.IsDefined(o.Error.Phase) && Enum.IsDefined(o.Error.RetryClass)
                && o.Error.MutationEffect == o.Effect && refs(o.Error.RecoveryCandidates, 8, 128)
                && !(o.Error.Phase == ToolErrorPhase.Preflight && o.Effect != ToolMutationEffect.None));
        if (!valid || result!.DomainPayload is null || JsonSerializer.Serialize(o).Length > MaxMetadataCharacters)
            return Failure(call, descriptor, "invalid_result", ToolErrorPhase.Execution,
                descriptor.IsMutating || o?.Effect is ToolMutationEffect.Applied or ToolMutationEffect.PartiallyApplied or ToolMutationEffect.Unknown
                    ? ToolMutationEffect.Unknown : ToolMutationEffect.None, result?.DomainPayload);
        // A provider's metadata cannot self-award host verification. Keep its original domain
        // payload for the existing verifier; only VerificationObserver awards a host report.
        // Retry advice is host-generated data, not an executable command from provider output.
        var error = o!.Error is null ? null : Failure(call, descriptor, o.Error.Code, o.Error.Phase, o.Effect).Outcome.Error;
        return result with { Outcome = o with { Verification = new(ToolVerificationStatus.NotRun, []), Error = error,
            ArtifactRefs = Array.AsReadOnly(o.ArtifactRefs.ToArray()), EvidenceRefs = Array.AsReadOnly(o.EvidenceRefs.ToArray()) } };
    }

    public static string ReadinessCode(ToolReadiness readiness) => readiness.State switch {
        ToolReadinessState.NeedsConfiguration => "needs_configuration", ToolReadinessState.Busy => "provider_busy",
        ToolReadinessState.Unsupported => "unsupported_operation", _ => "provider_unavailable" };
    public static string NormalizeCode(string? code) => code switch {
        "invalid_request" => "invalid_arguments", "stale_state" or "stale_hash" => "stale_resource",
        "not_found" => "resource_not_found", "denied" or "access_denied" or "permission_required"
            or "outside_resource_scope" or "expired_permission" or "boundary" => "permission_denied",
        "unavailable" => "provider_unavailable", "file_busy" => "provider_busy",
        "timeout" => "deadline_exceeded", "validation_failed" => "verification_failed",
        "target_not_grounded" or "invalid_arguments" or "unknown_tool" or "tool_not_loaded" or "repeated_failed_mutation"
            or "unsupported_operation" or "needs_configuration" or "resource_not_found" or "ambiguous_target"
            or "stale_resource" or "provider_busy" or "modal_blocked" or "permission_denied" or "connection_lost"
            or "deadline_exceeded" or "rate_limited" or "partial_result" or "verification_failed"
            or "partially_applied" or "outcome_unknown" or "invalid_result" or "provider_unavailable" or "cancelled" => code,
        _ => "tool_failed" };
    public static string SafeMessage(string? code) => code switch {
        "ready" => "Configured executor is available; this is not verification of a completed operation.",
        "not_probed" => "Helper is configured; native application readiness is checked only when invoked.",
        _ => NormalizeCode(code) switch {
            "invalid_arguments" => "Arguments were rejected. Inspect the advertised schema.",
            "unknown_tool" or "tool_not_loaded" => "Use an exact callable returned by tool discovery.",
            "needs_configuration" => "Configure an approved backend before using this capability.",
            "provider_unavailable" => "The configured backend or packaged helper is unavailable.",
            "unsupported_operation" => "This backend does not support the requested operation.",
            "resource_not_found" => "The selected resource could not be found; observe exact resources again.",
            "ambiguous_target" => "More than one target matches; select an exact resource.",
            "target_not_grounded" => "The target does not match a host-selected project, linked, explicit or captured resource. Select the exact target; more permission alone does not resolve it.",
            "stale_resource" => "The resource changed; read its current state before proceeding.",
            "provider_busy" or "modal_blocked" => "The provider is busy or blocked by a modal state.",
            "permission_denied" => "Permission is missing, expired or denied. Do not route around it.",
            "connection_lost" => "The provider response was lost. Effect metadata determines whether reconciliation is required.",
            "deadline_exceeded" => "The operation deadline expired. Do not assume that no changes occurred.",
            "rate_limited" => "The provider rate limit was reached; respect its configured budget.",
            "verification_failed" => "Host verification did not establish the required result.",
            "partially_applied" => "Only part of the requested change was applied; inspect before any further write.",
            "outcome_unknown" => "A change may already have occurred. Reconcile actual state; do not repeat the write.",
            "invalid_result" => "The executor returned malformed or inconsistent result metadata.",
            "cancelled" => "Cancellation was requested; effect and verification remain separate.",
            "partial_result" => "Only a bounded part of the requested data is available.",
            _ => "The operation failed. Inspect typed effect metadata and existing evidence before proceeding." } };
    private static string? String(JsonElement root, string key)
        => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

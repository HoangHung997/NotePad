namespace H2AgentLab.Tools;

/// <summary>
/// Host-owned recovery classification. This is policy data only: it never executes a retry,
/// changes permission, or changes resource/source identity.
/// </summary>
public sealed record ToolRecoveryPlan(
    ToolRetryClass RetryClass,
    IReadOnlyList<string> RecoveryCandidates,
    int MinimumBackoffMilliseconds,
    bool RequiresChangedEvidence,
    bool RequiresReconciliation,
    bool PreserveTargetIdentity,
    bool AllowsAlternateBackend);

public static class ToolRecoveryPolicy
{
    public static ToolRecoveryPlan For(string? code, ToolMutationEffect effect)
    {
        code = ToolOutcomeBridge.NormalizeCode(code);
        if (effect is ToolMutationEffect.Unknown or ToolMutationEffect.PartiallyApplied)
            return Plan(
                ToolRetryClass.ReconcileRequired,
                ["inspect_effects_without_repeating_write", "reconcile_same_resource"],
                requiresChangedEvidence: true,
                requiresReconciliation: true,
                preserveTargetIdentity: true);

        return code switch
        {
            "unknown_tool" or "tool_not_loaded" => Plan(
                ToolRetryClass.CorrectInput,
                ["use_exact_loaded_schema", "inspect_advertised_schema"],
                preserveTargetIdentity: true),

            "invalid_arguments" or "unsupported_selection" or "selection_too_large" => Plan(
                ToolRetryClass.CorrectInput,
                ["inspect_advertised_schema", "correct_arguments_preserve_target"],
                preserveTargetIdentity: true),

            "app_preflight_unavailable" => Plan(
                ToolRetryClass.Configure,
                ["repair_packaged_desktop_host", "check_desktop_host_protocol"],
                requiresChangedEvidence: true,
                preserveTargetIdentity: true,
                allowsAlternateBackend: false),

            "needs_configuration" or "provider_unavailable" or "unsupported_operation" => Plan(
                ToolRetryClass.Configure,
                ["check_provider_health", "rediscover_provider_capabilities", "configure_existing_provider",
                    "discover_semantically_equivalent_backend"],
                requiresChangedEvidence: true,
                preserveTargetIdentity: true,
                allowsAlternateBackend: true),

            "live_resource_required" or "native_object_unavailable" => Plan(
                ToolRetryClass.Reobserve,
                ["reacquire_same_live_resource", "reobserve_same_resource", "acquire_fresh_state_token"],
                requiresChangedEvidence: true,
                preserveTargetIdentity: true),

            "stale_resource" or "resource_not_found" or "ambiguous_target" or "target_not_grounded" => Plan(
                ToolRetryClass.Reobserve,
                ["reobserve_same_resource", "rediscover_exact_resource", "acquire_fresh_state_token"],
                requiresChangedEvidence: true,
                preserveTargetIdentity: true),

            "provider_busy" or "modal_blocked" => Plan(
                ToolRetryClass.WaitThenReobserve,
                ["bounded_backoff", "check_provider_health", "reobserve_same_resource", "rediscover_exact_resource"],
                backoffMilliseconds: 150,
                requiresChangedEvidence: true,
                preserveTargetIdentity: true),

            "connection_lost" => Plan(
                ToolRetryClass.WaitThenReobserve,
                ["bounded_backoff", "check_provider_health", "rediscover_exact_resource", "acquire_fresh_state_token"],
                backoffMilliseconds: 250,
                requiresChangedEvidence: true,
                preserveTargetIdentity: true),

            "deadline_exceeded" => Plan(
                ToolRetryClass.WaitThenReobserve,
                ["bounded_backoff", "check_provider_health", "reobserve_same_resource"],
                backoffMilliseconds: 250,
                requiresChangedEvidence: true,
                preserveTargetIdentity: true),

            "rate_limited" => Plan(
                ToolRetryClass.WaitThenReobserve,
                ["bounded_backoff", "check_provider_health"],
                backoffMilliseconds: 1_000,
                requiresChangedEvidence: true,
                preserveTargetIdentity: true),

            "discovery_limit" or "session_capacity" => Plan(
                ToolRetryClass.WaitThenReobserve,
                ["bounded_backoff", "rediscover_exact_resource"],
                backoffMilliseconds: 150,
                requiresChangedEvidence: true,
                preserveTargetIdentity: true),

            _ => Plan(ToolRetryClass.Never, [], preserveTargetIdentity: true)
        };
    }

    private static ToolRecoveryPlan Plan(
        ToolRetryClass retryClass,
        IReadOnlyList<string> candidates,
        int backoffMilliseconds = 0,
        bool requiresChangedEvidence = false,
        bool requiresReconciliation = false,
        bool preserveTargetIdentity = false,
        bool allowsAlternateBackend = false)
        => new(
            retryClass,
            Array.AsReadOnly(candidates.Distinct(StringComparer.Ordinal).Take(8).ToArray()),
            Math.Clamp(backoffMilliseconds, 0, 5_000),
            requiresChangedEvidence,
            requiresReconciliation,
            preserveTargetIdentity,
            allowsAlternateBackend);
}

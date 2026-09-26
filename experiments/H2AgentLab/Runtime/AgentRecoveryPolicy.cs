using System.Text.Json;
using H2AgentLab.Tools;

namespace H2AgentLab.Runtime;

/// <summary>
/// Run-local description of allowed recovery evidence. It is not a planner and never executes tools.
/// Alternate backends remain suggestions until the existing verifier proves the same target/postcondition.
/// </summary>
internal sealed record AgentRecoveryDirective(
    ToolRecoveryPlan Plan,
    IReadOnlyList<string> ProviderToolCandidates,
    IReadOnlyList<string> AlternateToolCandidates,
    IReadOnlyList<string> SafeChoices,
    ToolReadinessState? ProviderReadiness,
    string? ProviderReadinessReason);

internal static class AgentRecoveryPolicy
{
    private static readonly string[] TargetSelectors =
    [
        "path", "destination", "url", "session_id", "resource_id", "window_id",
        "document_id", "sheet", "sheet_name", "range", "address"
    ];

    private static readonly HashSet<string> FreshTokenFields = new(StringComparer.Ordinal)
    {
        "expectedHash", "expected_hash", "state_token", "stateToken", "etag", "version",
        "observed_version", "observedVersion"
    };

    public static AgentRecoveryDirective Describe(
        ToolRegistry registry,
        ToolDescriptor? descriptor,
        ToolOutcome? outcome,
        string code,
        IEnumerable<string>? providerRecoveryTools)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var plan = ToolRecoveryPolicy.For(code, outcome?.Effect ?? ToolMutationEffect.None);
        var providerTools = (providerRecoveryTools ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name) && registry.TryGet(name, out _))
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();

        var alternateTools = plan.AllowsAlternateBackend && descriptor?.Preference is { } preference
            ? registry.Tools
                .Where(tool => tool.Name != descriptor.Name
                    && tool.Preference?.CapabilityFamily == preference.CapabilityFamily
                    && tool.CurrentReadiness.CanExecute
                    && tool.Preference?.ExplicitRequestOnly != true)
                .Select(tool => tool.Name)
                .Distinct(StringComparer.Ordinal)
                .Take(4)
                .ToArray()
            : [];

        var choices = plan.RecoveryCandidates
            .Concat(providerTools.Select(name => "tool:" + name))
            .Concat(alternateTools.Select(name => "alternate_tool:" + name))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        var readiness = descriptor?.CurrentReadiness;
        return new(
            plan,
            Array.AsReadOnly(providerTools),
            Array.AsReadOnly(alternateTools),
            Array.AsReadOnly(choices),
            readiness?.State,
            readiness?.ReasonCode);
    }

    public static string? BlockRetry(
        string code,
        ToolRecoveryPlan plan,
        JsonElement failedArguments,
        JsonElement retryArguments,
        int observedProgress)
    {
        code = ToolOutcomeBridge.NormalizeCode(code);
        if (code is "unknown_tool" or "tool_not_loaded")
            return null; // discovery/loading is the host-observed state change for this exact retry.

        if (plan.RequiresReconciliation || plan.RetryClass == ToolRetryClass.ReconcileRequired)
            return "The prior operation may have changed state. Reconcile the same resource before any replay.";
        if (plan.RetryClass == ToolRetryClass.Never)
            return "The prior failure is not retryable by repeating the same tool.";

        var identical = JsonElement.DeepEquals(failedArguments, retryArguments);
        if (plan.RetryClass == ToolRetryClass.CorrectInput)
            return identical
                ? "The arguments are unchanged. Correct the rejected input before retrying."
                : null;

        if (plan.RetryClass is ToolRetryClass.Reobserve or ToolRetryClass.WaitThenReobserve)
        {
            if (FreshTokenOnly(failedArguments, retryArguments))
                return null;
            if (observedProgress > 0)
                return null;
            return "No changed resource/provider evidence was observed. Reobserve or refresh the same target before retrying.";
        }

        if (plan.RetryClass == ToolRetryClass.Configure)
            return observedProgress > 0
                ? null
                : "No provider/configuration health change was observed. Configure or rediscover the provider before retrying.";

        return identical ? "The repeated call has no new recovery evidence." : null;
    }

    public static bool SameRecoveryTarget(JsonElement failed, JsonElement retry)
        => JsonElement.DeepEquals(failed, retry) || SameTargetSelectors(failed, retry);

    public static bool CountsAsProgress(
        string failedTool,
        JsonElement failedArguments,
        string? failedProviderId,
        IReadOnlyList<string> providerToolCandidates,
        ToolRetryClass retryClass,
        global::H2AgentLab.ToolCall successfulCall,
        ToolOutcome? successfulOutcome,
        ToolDescriptor successfulDescriptor)
    {
        if (successfulDescriptor.IsMutating || successfulOutcome?.IsError == true)
            return false;
        if (successfulCall.Name == failedTool)
            return false;

        if (providerToolCandidates.Contains(successfulCall.Name, StringComparer.Ordinal))
            return true;
        if (SameTargetSelectors(failedArguments, successfulCall.Arguments))
            return true;

        var sameProvider = !string.IsNullOrWhiteSpace(failedProviderId)
            && string.Equals(failedProviderId, successfulDescriptor.Provenance?.ProviderId, StringComparison.Ordinal);
        return sameProvider && retryClass is ToolRetryClass.Configure
            or ToolRetryClass.Reobserve
            or ToolRetryClass.WaitThenReobserve;
    }

    public static bool FreshTokenOnly(JsonElement before, JsonElement after)
    {
        if (before.ValueKind != JsonValueKind.Object || after.ValueKind != JsonValueKind.Object)
            return false;

        var beforeProperties = before.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        var afterProperties = after.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        var nonTokenNames = beforeProperties.Keys.Concat(afterProperties.Keys)
            .Where(name => !FreshTokenFields.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (nonTokenNames.Length == 0)
            return false;
        foreach (var name in nonTokenNames)
        {
            if (!beforeProperties.TryGetValue(name, out var a)
                || !afterProperties.TryGetValue(name, out var b)
                || !JsonElement.DeepEquals(a, b))
                return false;
        }

        var tokenNames = beforeProperties.Keys.Concat(afterProperties.Keys)
            .Where(FreshTokenFields.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return tokenNames.Any(name =>
            !beforeProperties.TryGetValue(name, out var a)
            || !afterProperties.TryGetValue(name, out var b)
            || !JsonElement.DeepEquals(a, b));
    }

    private static bool SameTargetSelectors(JsonElement before, JsonElement after)
    {
        if (before.ValueKind != JsonValueKind.Object || after.ValueKind != JsonValueKind.Object)
            return false;
        var compared = false;
        foreach (var name in TargetSelectors)
        {
            var hasBefore = before.TryGetProperty(name, out var a);
            var hasAfter = after.TryGetProperty(name, out var b);
            if (hasBefore != hasAfter)
                return false;
            if (!hasBefore)
                continue;
            compared = true;
            if (!JsonElement.DeepEquals(a, b))
                return false;
        }
        return compared;
    }
}

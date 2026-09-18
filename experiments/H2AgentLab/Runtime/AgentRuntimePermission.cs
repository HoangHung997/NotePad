using H2AgentLab.Tasking;
using H2AgentLab.Tools;

namespace H2AgentLab.Runtime;

public sealed record AgentRuntimePermissionRequest(
    AgentTaskContract Contract,
    ToolDescriptor Descriptor,
    global::H2AgentLab.ToolCall Call,
    string? ResourceKey);

public sealed record AgentRuntimePermissionDecision(
    bool Allowed,
    string Code,
    string Message,
    string? ResourceKey)
{
    public static AgentRuntimePermissionDecision Allow(string? resourceKey)
        => new(true, "allowed", "Host policy allowed the tool call.", resourceKey);

    public static AgentRuntimePermissionDecision Deny(
        string code,
        string message,
        string? resourceKey)
        => new(false, code, message, resourceKey);
}

public interface IAgentRuntimePermissionPolicy
{
    ValueTask<AgentRuntimePermissionDecision> AuthorizeAsync(
        AgentRuntimePermissionRequest request,
        CancellationToken cancellationToken);

    void ObserveResult(
        AgentRuntimePermissionRequest request,
        string output);
}

public sealed class ScopedAgentRuntimePermissionPolicy : IAgentRuntimePermissionPolicy
{
    private readonly Func<AgentRuntimePermissionRequest, bool> _mutationAllowed;
    private readonly HashSet<string> _declinedScopes = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public ScopedAgentRuntimePermissionPolicy(
        Func<AgentRuntimePermissionRequest, bool>? mutationAllowed = null)
    {
        _mutationAllowed = mutationAllowed ?? (_ => true);
    }

    public ValueTask<AgentRuntimePermissionDecision> AuthorizeAsync(
        AgentRuntimePermissionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!request.Descriptor.IsMutating)
            return ValueTask.FromResult(AgentRuntimePermissionDecision.Allow(request.ResourceKey));

        if (!request.Contract.IsMutating
            || request.Contract.RiskClass == AgentTaskRiskClass.ReadOnly)
        {
            return ValueTask.FromResult(
                AgentRuntimePermissionDecision.Deny(
                    "permission_required",
                    "Host task contract is read-only; mutation is not permitted.",
                    request.ResourceKey));
        }

        if (string.IsNullOrWhiteSpace(request.ResourceKey))
        {
            return ValueTask.FromResult(
                AgentRuntimePermissionDecision.Deny(
                    "missing_resource_scope",
                    "Mutating tool has no host-owned resource identity.",
                    null));
        }

        lock (_sync)
        {
            if (_declinedScopes.Contains(request.ResourceKey))
            {
                return ValueTask.FromResult(
                    AgentRuntimePermissionDecision.Deny(
                        "denied",
                        "This resource scope was declined earlier in the task.",
                        request.ResourceKey));
            }
        }

        if (!_mutationAllowed(request))
        {
            RememberDeclined(request.ResourceKey);
            return ValueTask.FromResult(
                AgentRuntimePermissionDecision.Deny(
                    "denied",
                    "Host permission policy denied this resource scope.",
                    request.ResourceKey));
        }

        return ValueTask.FromResult(AgentRuntimePermissionDecision.Allow(request.ResourceKey));
    }

    public void ObserveResult(
        AgentRuntimePermissionRequest request,
        string output)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Descriptor.IsMutating
            || string.IsNullOrWhiteSpace(request.ResourceKey)
            || string.IsNullOrWhiteSpace(output))
            return;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(output);
            var root = document.RootElement;
            if (HasDeniedCode(root)
                || (root.TryGetProperty("recovery", out var recovery)
                    && recovery.ValueKind == System.Text.Json.JsonValueKind.Object
                    && HasDeniedCode(recovery)))
                RememberDeclined(request.ResourceKey);
        }
        catch (System.Text.Json.JsonException)
        {
        }
    }

    public bool IsDeclined(string resourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        lock (_sync) return _declinedScopes.Contains(resourceKey.Trim());
    }

    private void RememberDeclined(string resourceKey)
    {
        lock (_sync) _declinedScopes.Add(resourceKey.Trim());
    }

    private static bool HasDeniedCode(System.Text.Json.JsonElement node)
    {
        if (node.TryGetProperty("code", out var code)
            && code.ValueKind == System.Text.Json.JsonValueKind.String
            && code.GetString() is { } value
            && (value.Equals("denied", StringComparison.OrdinalIgnoreCase)
                || value.Equals("permission_denied", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (node.TryGetProperty("error", out var error)
            && error.ValueKind == System.Text.Json.JsonValueKind.String
            && error.GetString() is { } message
            && message.Contains("declined", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}

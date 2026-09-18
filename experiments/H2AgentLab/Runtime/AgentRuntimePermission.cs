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

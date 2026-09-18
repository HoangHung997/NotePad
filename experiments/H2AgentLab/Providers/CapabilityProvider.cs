using System.Text.Json;
using H2AgentLab.Tools;

namespace H2AgentLab.Providers;

public enum ProviderHealthStatus
{
    Disconnected = 0,
    Connecting = 1,
    Ready = 2,
    Degraded = 3,
    Failed = 4
}

public sealed record ProviderProvenance(
    string ProviderId,
    string ProviderVersion,
    string ServerId,
    string TransportKind);

public sealed record ProviderNamespaceSummary(
    string Name,
    string Description,
    IReadOnlyList<string> CapabilityKeywords);

public sealed record ProviderHealthState(
    ProviderHealthStatus Status,
    DateTime ChangedUtc,
    string? BoundedError = null,
    int ConsecutiveFailures = 0);

public sealed record ProviderToolSummary(
    string Name,
    string Namespace,
    string Description,
    AgentToolAccess Access,
    AgentToolRisk Risk,
    bool SupportsParallel,
    string SchemaVersion,
    string ResourceScope,
    string SerializationKey,
    string ToolVersion);

public sealed record ProviderToolDefinition(
    ProviderToolSummary Summary,
    JsonElement CallableSchema);

public sealed record ProviderResourceSummary(
    string ResourceId,
    string Namespace,
    string Description,
    string Scope);

public interface ICapabilityProvider : IAsyncDisposable
{
    ProviderProvenance Provenance { get; }
    ProviderHealthState Health { get; }

    Task ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderNamespaceSummary>> ListNamespacesAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderToolSummary>> ListToolSummariesAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderToolDefinition>> LoadToolDefinitionsAsync(
        IReadOnlyList<string> toolNames,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderResourceSummary>> ListResourcesAsync(
        CancellationToken cancellationToken);

    Task<string> ReadResourceAsync(
        string resourceId,
        CancellationToken cancellationToken);

    ValueTask<string> ExecuteToolAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken);
}

public sealed record CapabilityProviderPolicy(
    IReadOnlySet<string> AllowedReadScopes,
    IReadOnlySet<string> AllowedMutationScopes,
    bool AllowParallelReadOnly,
    bool TrustProviderVerificationClaims = false)
{
    public bool Allows(
        string scope,
        AgentToolAccess access)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return access == AgentToolAccess.ReadOnly
            ? AllowedReadScopes.Contains(scope)
            : AllowedMutationScopes.Contains(scope);
    }
}

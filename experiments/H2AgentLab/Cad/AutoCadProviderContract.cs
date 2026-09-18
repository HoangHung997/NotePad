using System.Text.Json;
using H2AgentLab.Tools;

namespace H2AgentLab.Cad;

public enum AutoCadOperationKind
{
    ListDocuments = 0,
    GetActiveDocument = 1,
    QueryEntities = 2,
    ReadAttributes = 3,
    ReadLayers = 4,
    UpdateAttribute = 5,
    UpdateEntity = 6,
    Plot = 7,
    VerifyEntity = 8,
    VerifyPlot = 9
}

public sealed record AutoCadDocumentRef(
    string SessionId,
    string Name,
    string FullName,
    string StateToken);

public sealed record AutoCadEntityRef(
    string DocumentSessionId,
    string Handle,
    string ObjectType,
    string Layer,
    string StateToken);

public sealed record AutoCadMutationRequest(
    AutoCadOperationKind Operation,
    string DocumentSessionId,
    string DocumentStateToken,
    string EntityHandle,
    string EntityStateToken,
    JsonElement Parameters,
    bool PermissionGranted);

public sealed record AutoCadMutationResult(
    string MutationId,
    AutoCadEntityRef Before,
    AutoCadEntityRef After,
    string EvidenceId);

public interface IAutoCadNativeBridge
{
    Task<IReadOnlyList<AutoCadDocumentRef>> ListDocumentsAsync(CancellationToken cancellationToken);
    Task<AutoCadDocumentRef?> GetActiveDocumentAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AutoCadEntityRef>> QueryEntitiesAsync(
        string documentSessionId,
        JsonElement query,
        CancellationToken cancellationToken);
    Task<JsonElement> ReadEntityAsync(
        AutoCadEntityRef entity,
        CancellationToken cancellationToken);
    Task<AutoCadMutationResult> MutateAsync(
        AutoCadMutationRequest request,
        CancellationToken cancellationToken);
    Task<JsonElement> PlotAsync(
        string documentSessionId,
        string documentStateToken,
        JsonElement request,
        CancellationToken cancellationToken);
    Task<bool> VerifyAsync(
        string documentSessionId,
        string currentDocumentStateToken,
        JsonElement verification,
        CancellationToken cancellationToken);
}

/// <summary>
/// Host-side contract guard for the required production boundary:
/// Agent -> provider/IPC -> AutoCAD plugin -> DocumentLock -> Transaction -> Database.
/// The provider never accepts arbitrary AutoCAD command strings as its default mutation API.
/// </summary>
public static class AutoCadProviderPolicy
{
    public static void ValidateMutation(AutoCadMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.PermissionGranted)
            throw new UnauthorizedAccessException("AutoCAD mutation requires explicit permission.");
        if (request.Operation is not (
                AutoCadOperationKind.UpdateAttribute
                or AutoCadOperationKind.UpdateEntity))
            throw new InvalidOperationException("AutoCAD mutation request uses a non-mutation operation.");
        if (string.IsNullOrWhiteSpace(request.DocumentSessionId)
            || string.IsNullOrWhiteSpace(request.DocumentStateToken)
            || string.IsNullOrWhiteSpace(request.EntityHandle)
            || string.IsNullOrWhiteSpace(request.EntityStateToken))
            throw new InvalidOperationException("AutoCAD mutation requires document/entity identity and fresh state tokens.");

        if (request.Parameters.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("AutoCAD mutation parameters must be structured JSON.");
        foreach (var property in request.Parameters.EnumerateObject())
        {
            if (property.Name.Contains("command", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("script", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("sendkeys", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "AutoCAD provider rejects arbitrary command/script mutation fields; use typed operations.");
        }
    }

    public static IReadOnlyList<ToolDescriptor> BuildDescriptors(
        IAgentToolExecutor executor,
        string providerVersion = "1.0.0")
    {
        ArgumentNullException.ThrowIfNull(executor);
        var operations = new[]
        {
            ("autocad.list_documents", AgentToolAccess.ReadOnly, AgentToolRisk.Low),
            ("autocad.get_active_document", AgentToolAccess.ReadOnly, AgentToolRisk.Low),
            ("autocad.query_entities", AgentToolAccess.ReadOnly, AgentToolRisk.Low),
            ("autocad.read_attributes", AgentToolAccess.ReadOnly, AgentToolRisk.Low),
            ("autocad.read_layers", AgentToolAccess.ReadOnly, AgentToolRisk.Low),
            ("autocad.update_attribute", AgentToolAccess.Mutating, AgentToolRisk.High),
            ("autocad.update_entity", AgentToolAccess.Mutating, AgentToolRisk.High),
            ("autocad.plot", AgentToolAccess.Mutating, AgentToolRisk.Medium),
            ("autocad.verify_entity", AgentToolAccess.ReadOnly, AgentToolRisk.Low),
            ("autocad.verify_plot", AgentToolAccess.ReadOnly, AgentToolRisk.Low)
        };

        return operations.Select(item =>
        {
            var schema = JsonSerializer.SerializeToElement(new
            {
                type = "function",
                function = new
                {
                    name = item.Item1,
                    description = "Structured AutoCAD native-plugin bridge capability.",
                    parameters = new
                    {
                        type = "object",
                        properties = new { },
                        additionalProperties = true
                    }
                }
            });
            return new ToolDescriptor(
                item.Item1,
                new ToolNamespace("autocad", "Structured AutoCAD native plugin capabilities."),
                "Structured AutoCAD native-plugin bridge capability.",
                item.Item3,
                item.Item2,
                supportsParallel: item.Item2 == AgentToolAccess.ReadOnly,
                "v1",
                schema,
                executor,
                provenance: new ToolProvenance(
                    "autocad-native-plugin",
                    providerVersion,
                    "local-autocad-ipc",
                    providerVersion),
                resourceScope: new ToolResourceScope(
                    "autocad:active-document",
                    "autocad:active-document"),
                serializationKey: "autocad-document",
                canProvideVerificationEvidence: false);
        }).ToArray();
    }
}

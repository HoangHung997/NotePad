using System.Text;
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
    string DocumentStateToken,
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
    Task<IReadOnlyList<AutoCadDocumentRef>> ListDocumentsAsync(
        CancellationToken cancellationToken);

    Task<AutoCadDocumentRef?> GetActiveDocumentAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AutoCadEntityRef>> QueryEntitiesAsync(
        string documentSessionId,
        string documentStateToken,
        JsonElement query,
        CancellationToken cancellationToken);

    Task<JsonElement> ReadAttributesAsync(
        AutoCadEntityRef entity,
        CancellationToken cancellationToken);

    Task<JsonElement> ReadLayersAsync(
        string documentSessionId,
        string documentStateToken,
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
/// Executes the public AutoCAD tool surface against the typed native bridge. Mutation permission
/// is host-owned: the model never supplies PermissionGranted. The authorizer must explicitly
/// approve each mutating operation before a bridge request is created.
/// </summary>
public sealed class AutoCadNativeToolExecutor : IAgentToolExecutor
{
    private readonly IAutoCadNativeBridge _bridge;
    private readonly Func<string, CancellationToken, ValueTask<bool>> _authorizeMutation;

    public AutoCadNativeToolExecutor(
        IAutoCadNativeBridge bridge,
        Func<string, CancellationToken, ValueTask<bool>>? authorizeMutation = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _authorizeMutation = authorizeMutation
            ?? ((_, _) => ValueTask.FromResult(false));
    }

    public string ExecutorId => "autocad.native";

    public async ValueTask<string> ExecuteAsync(
        global::H2AgentLab.ToolCall call,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        cancellationToken.ThrowIfCancellationRequested();
        if (call.Arguments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("AutoCAD tool arguments must be an object.");

        object result = call.Name switch
        {
            "autocad.list_documents" =>
                await _bridge.ListDocumentsAsync(cancellationToken).ConfigureAwait(false),

            "autocad.get_active_document" =>
                (object?)await _bridge.GetActiveDocumentAsync(cancellationToken).ConfigureAwait(false)
                    ?? new { unavailable = true },

            "autocad.query_entities" =>
                await QueryAsync(call, cancellationToken).ConfigureAwait(false),

            "autocad.read_attributes" =>
                await ReadAttributesAsync(call, cancellationToken).ConfigureAwait(false),

            "autocad.read_layers" =>
                await _bridge.ReadLayersAsync(
                    Required(call, "document_session_id"),
                    Required(call, "document_state_token"),
                    cancellationToken).ConfigureAwait(false),

            "autocad.update_attribute" =>
                await MutateAttributeAsync(call, cancellationToken).ConfigureAwait(false),

            "autocad.update_entity" =>
                await MutateEntityAsync(call, cancellationToken).ConfigureAwait(false),

            "autocad.plot" =>
                await PlotAsync(call, cancellationToken).ConfigureAwait(false),

            "autocad.verify_entity" =>
                await VerifyEntityAsync(call, cancellationToken).ConfigureAwait(false),

            "autocad.verify_plot" =>
                await VerifyPlotAsync(call, cancellationToken).ConfigureAwait(false),

            _ => throw new InvalidOperationException(
                "Unknown AutoCAD native tool: " + call.Name)
        };

        return JsonSerializer.Serialize(result);
    }

    private Task<IReadOnlyList<AutoCadEntityRef>> QueryAsync(
        global::H2AgentLab.ToolCall call,
        CancellationToken cancellationToken)
        => _bridge.QueryEntitiesAsync(
            Required(call, "document_session_id"),
            Required(call, "document_state_token"),
            RequiredObject(call, "query"),
            cancellationToken);

    private Task<JsonElement> ReadAttributesAsync(
        global::H2AgentLab.ToolCall call,
        CancellationToken cancellationToken)
        => _bridge.ReadAttributesAsync(
            Entity(call),
            cancellationToken);

    private async Task<AutoCadMutationResult> MutateAttributeAsync(
        global::H2AgentLab.ToolCall call,
        CancellationToken cancellationToken)
    {
        var parameters = JsonSerializer.SerializeToElement(new
        {
            attributeTag = Required(call, "attribute_tag"),
            value = Required(call, "value")
        });
        return await MutateAsync(
            call,
            AutoCadOperationKind.UpdateAttribute,
            parameters,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AutoCadMutationResult> MutateEntityAsync(
        global::H2AgentLab.ToolCall call,
        CancellationToken cancellationToken)
        => await MutateAsync(
            call,
            AutoCadOperationKind.UpdateEntity,
            RequiredObject(call, "changes"),
            cancellationToken).ConfigureAwait(false);

    private async Task<AutoCadMutationResult> MutateAsync(
        global::H2AgentLab.ToolCall call,
        AutoCadOperationKind operation,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var approved = await _authorizeMutation(
            call.Name,
            cancellationToken).ConfigureAwait(false);
        if (!approved)
            throw new UnauthorizedAccessException(
                "AutoCAD mutation requires host approval.");

        var request = new AutoCadMutationRequest(
            operation,
            Required(call, "document_session_id"),
            Required(call, "document_state_token"),
            Required(call, "entity_handle"),
            Required(call, "entity_state_token"),
            parameters,
            PermissionGranted: true);
        AutoCadProviderPolicy.ValidateMutation(request);
        return await _bridge.MutateAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<JsonElement> PlotAsync(
        global::H2AgentLab.ToolCall call,
        CancellationToken cancellationToken)
    {
        var approved = await _authorizeMutation(
            call.Name,
            cancellationToken).ConfigureAwait(false);
        if (!approved)
            throw new UnauthorizedAccessException(
                "AutoCAD plot requires host approval.");

        var request = RequiredObject(call, "request");
        AutoCadProviderPolicy.ValidateBoundedPayload(request, "plot request");
        return await _bridge.PlotAsync(
            Required(call, "document_session_id"),
            Required(call, "document_state_token"),
            request,
            cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> VerifyEntityAsync(
        global::H2AgentLab.ToolCall call,
        CancellationToken cancellationToken)
    {
        var verification = JsonSerializer.SerializeToElement(new
        {
            kind = "entity",
            entityHandle = Required(call, "entity_handle"),
            entityStateToken = Required(call, "entity_state_token"),
            expectation = RequiredObject(call, "expectation")
        });
        return _bridge.VerifyAsync(
            Required(call, "document_session_id"),
            Required(call, "document_state_token"),
            verification,
            cancellationToken);
    }

    private Task<bool> VerifyPlotAsync(
        global::H2AgentLab.ToolCall call,
        CancellationToken cancellationToken)
    {
        var verification = JsonSerializer.SerializeToElement(new
        {
            kind = "plot",
            expectation = RequiredObject(call, "expectation")
        });
        return _bridge.VerifyAsync(
            Required(call, "document_session_id"),
            Required(call, "document_state_token"),
            verification,
            cancellationToken);
    }

    private static AutoCadEntityRef Entity(global::H2AgentLab.ToolCall call)
        => new(
            Required(call, "document_session_id"),
            Required(call, "document_state_token"),
            Required(call, "entity_handle"),
            Required(call, "object_type"),
            Required(call, "layer"),
            Required(call, "entity_state_token"));

    private static string Required(
        global::H2AgentLab.ToolCall call,
        string name)
    {
        if (!call.Arguments.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException(
                "AutoCAD tool requires non-empty string '" + name + "'.");
        return value.GetString()!;
    }

    private static JsonElement RequiredObject(
        global::H2AgentLab.ToolCall call,
        string name)
    {
        if (!call.Arguments.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException(
                "AutoCAD tool requires object '" + name + "'.");
        return value.Clone();
    }
}

/// <summary>
/// Host-side contract guard for the required production boundary:
/// Agent -> provider/IPC -> AutoCAD plugin -> DocumentLock -> Transaction -> Database.
/// The provider never accepts arbitrary AutoCAD command strings as its default mutation API.
/// </summary>
public static class AutoCadProviderPolicy
{
    public const int MaxStructuredPayloadBytes = 32 * 1024;
    public const int MaxStructuredPropertyCount = 64;
    public const int MaxStructuredStringCharacters = 8 * 1024;

    public static void ValidateMutation(AutoCadMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.PermissionGranted)
            throw new UnauthorizedAccessException(
                "AutoCAD mutation requires explicit permission.");
        if (request.Operation is not (
                AutoCadOperationKind.UpdateAttribute
                or AutoCadOperationKind.UpdateEntity))
            throw new InvalidOperationException(
                "AutoCAD mutation request uses a non-mutation operation.");
        if (string.IsNullOrWhiteSpace(request.DocumentSessionId)
            || string.IsNullOrWhiteSpace(request.DocumentStateToken)
            || string.IsNullOrWhiteSpace(request.EntityHandle)
            || string.IsNullOrWhiteSpace(request.EntityStateToken))
            throw new InvalidOperationException(
                "AutoCAD mutation requires document/entity identity and fresh state tokens.");

        ValidateBoundedPayload(request.Parameters, "mutation parameters");

        if (request.Operation == AutoCadOperationKind.UpdateAttribute)
        {
            var names = request.Parameters.EnumerateObject()
                .Select(x => x.Name)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            if (!names.SequenceEqual(
                    new[] { "attributeTag", "value" }.OrderBy(x => x, StringComparer.Ordinal)))
                throw new InvalidOperationException(
                    "AutoCAD attribute mutation accepts only attributeTag and value.");
        }
    }

    public static void ValidateBoundedPayload(
        JsonElement payload,
        string label)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new ArgumentException(
                "AutoCAD " + label + " must be structured JSON.");

        var bytes = Encoding.UTF8.GetByteCount(payload.GetRawText());
        if (bytes > MaxStructuredPayloadBytes)
            throw new InvalidOperationException(
                "AutoCAD " + label + " exceeds the structured payload limit.");

        var properties = 0;
        Inspect(payload, depth: 0, ref properties);
        if (properties > MaxStructuredPropertyCount)
            throw new InvalidOperationException(
                "AutoCAD " + label + " contains too many properties.");
    }

    private static void Inspect(
        JsonElement value,
        int depth,
        ref int properties)
    {
        if (depth > 8)
            throw new InvalidOperationException(
                "AutoCAD structured payload nesting is too deep.");

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    properties++;
                    if (IsArbitraryExecutionField(property.Name))
                        throw new InvalidOperationException(
                            "AutoCAD provider rejects arbitrary command/script mutation fields; use typed operations.");
                    Inspect(property.Value, depth + 1, ref properties);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    Inspect(item, depth + 1, ref properties);
                break;

            case JsonValueKind.String:
                if ((value.GetString() ?? "").Length > MaxStructuredStringCharacters)
                    throw new InvalidOperationException(
                        "AutoCAD structured string value exceeds the limit.");
                break;
        }
    }

    private static bool IsArbitraryExecutionField(string name)
        => name.Contains("command", StringComparison.OrdinalIgnoreCase)
            || name.Contains("script", StringComparison.OrdinalIgnoreCase)
            || name.Contains("sendkeys", StringComparison.OrdinalIgnoreCase)
            || name.Contains("lisp", StringComparison.OrdinalIgnoreCase)
            || name.Contains("macro", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<ToolDescriptor> BuildDescriptors(
        IAgentToolExecutor executor,
        string providerVersion = "1.0.0")
    {
        ArgumentNullException.ThrowIfNull(executor);

        var definitions = new[]
        {
            Definition(
                "autocad.list_documents",
                AgentToolAccess.ReadOnly,
                AgentToolRisk.Low,
                Properties(),
                Array.Empty<string>()),
            Definition(
                "autocad.get_active_document",
                AgentToolAccess.ReadOnly,
                AgentToolRisk.Low,
                Properties(),
                Array.Empty<string>()),
            Definition(
                "autocad.query_entities",
                AgentToolAccess.ReadOnly,
                AgentToolRisk.Low,
                Properties(
                    StringProperty("document_session_id"),
                    StringProperty("document_state_token"),
                    ObjectProperty("query")),
                ["document_session_id", "document_state_token", "query"]),
            Definition(
                "autocad.read_attributes",
                AgentToolAccess.ReadOnly,
                AgentToolRisk.Low,
                EntityProperties(),
                EntityRequired()),
            Definition(
                "autocad.read_layers",
                AgentToolAccess.ReadOnly,
                AgentToolRisk.Low,
                Properties(
                    StringProperty("document_session_id"),
                    StringProperty("document_state_token")),
                ["document_session_id", "document_state_token"]),
            Definition(
                "autocad.update_attribute",
                AgentToolAccess.Mutating,
                AgentToolRisk.High,
                Properties(
                    EntityProperties().Concat(new[]
                    {
                        StringProperty("attribute_tag"),
                        StringProperty("value")
                    }).ToArray()),
                EntityRequired().Concat(new[]
                {
                    "attribute_tag",
                    "value"
                }).ToArray()),
            Definition(
                "autocad.update_entity",
                AgentToolAccess.Mutating,
                AgentToolRisk.High,
                Properties(
                    EntityProperties().Concat(new[]
                    {
                        ObjectProperty("changes")
                    }).ToArray()),
                EntityRequired().Concat(new[] { "changes" }).ToArray()),
            Definition(
                "autocad.plot",
                AgentToolAccess.Mutating,
                AgentToolRisk.Medium,
                Properties(
                    StringProperty("document_session_id"),
                    StringProperty("document_state_token"),
                    ObjectProperty("request")),
                ["document_session_id", "document_state_token", "request"]),
            Definition(
                "autocad.verify_entity",
                AgentToolAccess.ReadOnly,
                AgentToolRisk.Low,
                Properties(
                    StringProperty("document_session_id"),
                    StringProperty("document_state_token"),
                    StringProperty("entity_handle"),
                    StringProperty("entity_state_token"),
                    ObjectProperty("expectation")),
                [
                    "document_session_id",
                    "document_state_token",
                    "entity_handle",
                    "entity_state_token",
                    "expectation"
                ]),
            Definition(
                "autocad.verify_plot",
                AgentToolAccess.ReadOnly,
                AgentToolRisk.Low,
                Properties(
                    StringProperty("document_session_id"),
                    StringProperty("document_state_token"),
                    ObjectProperty("expectation")),
                ["document_session_id", "document_state_token", "expectation"])
        };

        return definitions.Select(item =>
        {
            var schema = JsonSerializer.SerializeToElement(new
            {
                type = "function",
                function = new
                {
                    name = item.Name,
                    description = "Structured AutoCAD native-plugin bridge capability.",
                    parameters = new
                    {
                        type = "object",
                        properties = item.Properties,
                        required = item.Required,
                        additionalProperties = false
                    }
                }
            });

            var mutating = item.Access == AgentToolAccess.Mutating;
            return new ToolDescriptor(
                item.Name,
                new ToolNamespace(
                    "autocad",
                    "Structured AutoCAD native plugin capabilities."),
                "Structured AutoCAD native-plugin bridge capability.",
                item.Risk,
                item.Access,
                supportsParallel: !mutating,
                "v2",
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
                canProvideVerificationEvidence: true,
                preference: new ToolPreferenceMetadata(
                    "autocad",
                    ToolInteractionFidelity.Structured));
        }).ToArray();
    }

    private static (
        string Name,
        AgentToolAccess Access,
        AgentToolRisk Risk,
        IReadOnlyDictionary<string, object> Properties,
        IReadOnlyList<string> Required) Definition(
            string name,
            AgentToolAccess access,
            AgentToolRisk risk,
            IReadOnlyDictionary<string, object> properties,
            IReadOnlyList<string> required)
        => (name, access, risk, properties, required);

    private static IReadOnlyDictionary<string, object> EntityProperties()
        => Properties(
            StringProperty("document_session_id"),
            StringProperty("document_state_token"),
            StringProperty("entity_handle"),
            StringProperty("object_type"),
            StringProperty("layer"),
            StringProperty("entity_state_token"));

    private static string[] EntityRequired()
        =>
        [
            "document_session_id",
            "document_state_token",
            "entity_handle",
            "object_type",
            "layer",
            "entity_state_token"
        ];

    private static IReadOnlyDictionary<string, object> Properties(
        params KeyValuePair<string, object>[] items)
        => items.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

    private static KeyValuePair<string, object> StringProperty(string name)
        => new(
            name,
            new
            {
                type = "string",
                minLength = 1,
                maxLength = MaxStructuredStringCharacters
            });

    private static KeyValuePair<string, object> ObjectProperty(string name)
        => new(
            name,
            new
            {
                type = "object"
            });
}

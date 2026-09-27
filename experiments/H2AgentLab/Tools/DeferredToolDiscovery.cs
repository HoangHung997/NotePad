using H2AgentLab.Verification;
using System.Text.Json;

namespace H2AgentLab.Tools;

public sealed record ToolNamespaceSummary(string Name, string Description);

public sealed record InitialToolExposure(
    IReadOnlyList<JsonElement> CallableSchemas,
    IReadOnlyList<ToolNamespaceSummary> Namespaces);

public sealed record ToolSchemaLoadRecord(
    long Sequence,
    long RegistryVersion,
    string Query,
    IReadOnlyList<string> SelectedNames,
    IReadOnlyList<string> NewlyLoadedNames);

public sealed record ToolSchemaLoadBatch(
    IReadOnlyList<JsonElement> CallableSchemas,
    ToolSchemaLoadRecord Trace);

/// <summary>
/// Creates the deliberately small first-turn tool surface. Detailed schemas stay in ToolRegistry
/// until lexical discovery selects them. One instance represents one task/session load state.
/// </summary>
public sealed class DeferredToolDiscovery
{
    public const string SearchToolName = "tool_search";

    private readonly ToolRegistry _registry;
    private readonly ToolSearchIndex _search;
    private readonly HashSet<string> _loaded = new(StringComparer.Ordinal)
    {
        SearchToolName
    };
    private readonly List<ToolSchemaLoadRecord> _trace = [];

    public DeferredToolDiscovery(ToolRegistry registry, ToolSearchIndex? search = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _search = search ?? new ToolSearchIndex(registry);
        if (_registry.TryGet("update_plan", out _))
            _loaded.Add("update_plan");
    }

    public IReadOnlyList<string> LoadedSchemaNames
        => _loaded.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<ToolSchemaLoadRecord> LoadTrace
        => _trace.ToArray();

    public InitialToolExposure BuildInitialExposure()
    {
        var schemas = new List<JsonElement> { BuildToolSearchSchema() };

        // update_plan is intentionally the only v1 detailed schema retained as stable core.
        if (_registry.TryGet("update_plan", out var planningTool))
            schemas.Add(planningTool.CallableSchema.Clone());

        var namespaces = _registry.Namespaces
            .Where(x => x.Name != "core")
            .Select(x => new ToolNamespaceSummary(x.Name, x.Description))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();

        return new InitialToolExposure(
            schemas.Select(x => x.Clone()).ToArray(),
            namespaces);
    }

    public IReadOnlyList<ToolSearchResult> Search(string query, int maxResults = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (maxResults is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(maxResults));

        var candidateLimit = Math.Min(50, Math.Max(maxResults * 4, maxResults));
        var candidates = _search.Search(query, candidateLimit);
        var preferred = DocumentToolPreference.Apply(
            query,
            candidates,
            candidateLimit);
        return preferred.Take(maxResults).ToArray();
    }

    public ToolSchemaLoadBatch SearchAndLoad(string query, int maxResults = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var selected = Search(query, maxResults);
        var selectedNames = selected.Select(x => x.Descriptor.Name).ToArray();

        var schemas = new List<JsonElement>();
        var newlyLoaded = new List<string>();
        foreach (var result in selected)
        {
            if (!result.Descriptor.CurrentReadiness.CanExecute || !_loaded.Add(result.Descriptor.Name))
                continue;
            schemas.Add(result.Descriptor.CallableSchema.Clone());
            newlyLoaded.Add(result.Descriptor.Name);
        }

        var trace = new ToolSchemaLoadRecord(
            Sequence: _trace.Count,
            RegistryVersion: _registry.Version,
            Query: query.Trim(),
            SelectedNames: Array.AsReadOnly(selectedNames),
            NewlyLoadedNames: Array.AsReadOnly(newlyLoaded.ToArray()));
        _trace.Add(trace);

        return new ToolSchemaLoadBatch(
            schemas.Select(x => x.Clone()).ToArray(),
            trace);
    }

    /// <summary>
    /// Runtime handler for the initial tool_search callable. It records exactly what was selected
    /// and newly loaded; the host supplies CallableSchemas on the next model request.
    /// </summary>
    public string ExecuteToolSearch(global::H2AgentLab.ToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!string.Equals(call.Name, SearchToolName, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected '{SearchToolName}', got '{call.Name}'.");
        if (call.Arguments.ValueKind != JsonValueKind.Object
            || !call.Arguments.TryGetProperty("query", out var queryNode)
            || queryNode.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(queryNode.GetString()))
            throw new ArgumentException("tool_search requires a non-empty string query.");

        var maxResults = 8;
        if (call.Arguments.TryGetProperty("max_results", out var maxNode))
        {
            if (maxNode.ValueKind == JsonValueKind.Number && maxNode.TryGetInt32(out var number))
                maxResults = number;
            else if (maxNode.ValueKind == JsonValueKind.String
                && int.TryParse(maxNode.GetString(), out number))
                maxResults = number;
            else
                throw new ArgumentException("tool_search max_results must be an integer.");
        }

        var batch = SearchAndLoad(queryNode.GetString()!, maxResults);
        return JsonSerializer.Serialize(new
        {
            registryVersion = batch.Trace.RegistryVersion,
            selected = batch.Trace.SelectedNames,
            newlyLoaded = batch.Trace.NewlyLoadedNames,
            nextRequestSchemas = batch.CallableSchemas.Select(SchemaName).ToArray(),
            capabilities = batch.Trace.SelectedNames.Select(name => {
                _registry.TryGet(name, out var d);
                return new { name, readiness = d.CurrentReadiness.State, reason = d.CurrentReadiness.SafeReason,
                    executable = d.CurrentReadiness.CanExecute, outputSchemaVersion = d.OutputSchemaVersion,
                    supportedOperations = d.SupportedOperations, dependencies = d.Dependencies, limits = d.Limits,
                    effectClass = d.EffectClass, canProvideVerificationEvidence = d.CanProvideVerificationEvidence };
            }).ToArray(),
            unavailableCapabilities = _registry.CapabilityNotices.Where(n =>
                queryNode.GetString()!.Split([' ', '.', '_'], StringSplitOptions.RemoveEmptyEntries)
                    .Any(term => term.Length >= 2 && (n.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || n.Description.Contains(term, StringComparison.OrdinalIgnoreCase))))
                .Take(8).Select(n => new { name = n.Name, description = n.Description,
                    readiness = n.Readiness.State, reason = n.Readiness.SafeReason, executable = false }).ToArray()
        });
    }

    public static JsonElement BuildToolSearchSchema()
        => JsonSerializer.SerializeToElement(new
        {
            type = "function",
            function = new
            {
                name = SearchToolName,
                description = "Find relevant callable tools by task intent. Detailed schemas are loaded only for selected results.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        query = new
                        {
                            type = "string",
                            description = "Concise capability or operation to find."
                        },
                        max_results = new
                        {
                            type = "integer",
                            minimum = 1,
                            maximum = 8,
                            description = "Maximum relevant tools to load."
                        }
                    },
                    required = new[] { "query" },
                    additionalProperties = false
                }
            }
        });

    public static string SchemaName(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("function", out var function)
            || !function.TryGetProperty("name", out var name)
            || name.ValueKind != JsonValueKind.String)
            throw new ArgumentException("Tool schema has no function.name.", nameof(schema));
        return name.GetString()!;
    }
}

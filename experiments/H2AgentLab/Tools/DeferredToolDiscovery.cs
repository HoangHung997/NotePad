using System.Text.Json;

namespace H2AgentLab.Tools;

public sealed record ToolNamespaceSummary(string Name, string Description);

public sealed record InitialToolExposure(
    IReadOnlyList<JsonElement> CallableSchemas,
    IReadOnlyList<ToolNamespaceSummary> Namespaces);

/// <summary>
/// Creates the deliberately small first-turn tool surface. Detailed schemas stay in ToolRegistry
/// until lexical discovery selects them.
/// </summary>
public sealed class DeferredToolDiscovery
{
    public const string SearchToolName = "tool_search";

    private readonly ToolRegistry _registry;
    private readonly ToolSearchIndex _search;

    public DeferredToolDiscovery(ToolRegistry registry, ToolSearchIndex? search = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _search = search ?? new ToolSearchIndex(registry);
    }

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
        => _search.Search(query, maxResults);

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

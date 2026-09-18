using System.Text.Json;

namespace H2AgentLab.Tools;

public sealed class V1AgentToolsExecutor : IAgentToolExecutor
{
    private readonly global::H2AgentLab.AgentTools _tools;

    public V1AgentToolsExecutor(global::H2AgentLab.AgentTools tools)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
    }

    public string ExecutorId => "v1-agent-tools";

    public async ValueTask<string> ExecuteAsync(
        global::H2AgentLab.ToolCall call,
        CancellationToken cancellationToken)
        => await _tools.Execute(call, cancellationToken);
}

public static class V1ToolRegistryAdapter
{
    private sealed record Metadata(
        string Namespace,
        AgentToolRisk Risk,
        AgentToolAccess Access,
        bool Parallel);

    private static readonly IReadOnlyDictionary<string, Metadata> MetadataByName =
        new Dictionary<string, Metadata>(StringComparer.Ordinal)
        {
            ["list_skills"] = new("skills", AgentToolRisk.Low, AgentToolAccess.ReadOnly, true),
            ["read_skill"] = new("skills", AgentToolRisk.Low, AgentToolAccess.ReadOnly, true),
            ["update_plan"] = new("core", AgentToolRisk.Low, AgentToolAccess.Mutating, false),

            ["run_python"] = new("python", AgentToolRisk.Medium, AgentToolAccess.Mutating, false),
            ["inspect_artifact"] = new("python", AgentToolRisk.Low, AgentToolAccess.ReadOnly, true),
            ["read_run"] = new("python", AgentToolRisk.Low, AgentToolAccess.ReadOnly, true),
            ["view_artifact"] = new("python", AgentToolRisk.Low, AgentToolAccess.Mutating, false),
            ["publish_artifact"] = new("python", AgentToolRisk.High, AgentToolAccess.Mutating, false),

            ["list_files"] = new("files", AgentToolRisk.Low, AgentToolAccess.ReadOnly, true),
            ["find_files"] = new("files", AgentToolRisk.Low, AgentToolAccess.ReadOnly, true),
            ["read_file"] = new("files", AgentToolRisk.Low, AgentToolAccess.ReadOnly, true),
            ["search_files"] = new("files", AgentToolRisk.Low, AgentToolAccess.ReadOnly, true),
            ["write_text"] = new("files", AgentToolRisk.Medium, AgentToolAccess.Mutating, false),
            ["open_file"] = new("files", AgentToolRisk.Medium, AgentToolAccess.Mutating, false),

            ["word_paragraphs"] = new("office", AgentToolRisk.Low, AgentToolAccess.ReadOnly, true),
            ["check_word"] = new("office", AgentToolRisk.Low, AgentToolAccess.ReadOnly, true),

            ["inspect_window"] = new("desktop", AgentToolRisk.Low, AgentToolAccess.ReadOnly, false),
            ["click_control"] = new("desktop", AgentToolRisk.High, AgentToolAccess.Mutating, false),
            ["type_control"] = new("desktop", AgentToolRisk.High, AgentToolAccess.Mutating, false)
        };

    private static readonly IReadOnlyDictionary<string, string> NamespaceDescriptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["core"] = "Small stable planning/status tools.",
            ["files"] = "Workspace file discovery, reading, writing and open-file operations.",
            ["office"] = "Structured Office document inspection and validation.",
            ["desktop"] = "User-selected window inspection and UI actions.",
            ["python"] = "Sandboxed Python execution and generated artifact inspection/publication.",
            ["skills"] = "Progressive skill discovery and guidance loading."
        };

    public static ToolRegistry Create(global::H2AgentLab.AgentTools tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var registry = new ToolRegistry();
        Populate(registry, new V1AgentToolsExecutor(tools));
        return registry;
    }

    public static void Populate(ToolRegistry registry, IAgentToolExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(executor);

        var definitions = JsonSerializer.SerializeToElement(global::H2AgentLab.AgentTools.Definitions);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var definition in definitions.EnumerateArray())
        {
            var function = definition.GetProperty("function");
            var name = function.GetProperty("name").GetString()
                ?? throw new InvalidOperationException("V1 tool schema has no function name.");
            if (!seen.Add(name))
                throw new InvalidOperationException($"V1 tool definition '{name}' is duplicated.");
            if (!MetadataByName.TryGetValue(name, out var metadata))
                throw new InvalidOperationException(
                    $"V1 tool '{name}' has no registry metadata. Add metadata before exposing it through v2.");

            var description = function.GetProperty("description").GetString()
                ?? throw new InvalidOperationException($"V1 tool '{name}' has no description.");
            var toolNamespace = new ToolNamespace(
                metadata.Namespace,
                NamespaceDescriptions[metadata.Namespace]);

            registry.Register(new ToolDescriptor(
                name,
                toolNamespace,
                description,
                metadata.Risk,
                metadata.Access,
                metadata.Parallel,
                schemaVersion: "v1",
                callableSchema: definition,
                executor: executor));
        }

        var missingDefinitions = MetadataByName.Keys.Where(x => !seen.Contains(x)).ToArray();
        if (missingDefinitions.Length > 0)
            throw new InvalidOperationException(
                "Registry metadata references missing v1 tool(s): " + string.Join(", ", missingDefinitions));
    }
}

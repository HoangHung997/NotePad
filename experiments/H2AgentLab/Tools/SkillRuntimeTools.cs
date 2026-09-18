using System.Text.Json;
using H2AgentLab.Skills;

namespace H2AgentLab.Tools;

/// <summary>
/// Read-only runtime bridge for canonical SkillCatalog progressive disclosure.
/// Search returns bounded metadata; read_skill loads exactly one selected entry/resource.
/// Script/asset content is returned as untrusted text and is never executed here.
/// </summary>
public sealed class SkillRuntimeToolExecutor : IAgentToolExecutor
{
    public const string SearchToolName = "list_skills";
    public const string ReadToolName = "read_skill";
    private const int MaxProjectedContentCharacters = 32_000;
    private readonly H2AgentLab.Skills.SkillCatalog _catalog;

    public SkillRuntimeToolExecutor(H2AgentLab.Skills.SkillCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public string ExecutorId => "canonical-skill-catalog";

    public ValueTask<string> ExecuteAsync(
        global::H2AgentLab.ToolCall call,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        cancellationToken.ThrowIfCancellationRequested();

        return call.Name switch
        {
            SearchToolName => ValueTask.FromResult(Search(call)),
            ReadToolName => ValueTask.FromResult(Read(call)),
            _ => throw new InvalidOperationException(
                $"Skill runtime executor cannot execute '{call.Name}'.")
        };
    }

    private string Search(global::H2AgentLab.ToolCall call)
    {
        var query = OptionalString(call.Arguments, "query");
        var skills = _catalog.Search(query, 100)
            .Select(x => new
            {
                x.Name,
                x.Description,
                sourceKind = x.Identity.SourceKind.ToString(),
                x.Identity.SourceId,
                x.Identity.PluginId,
                x.Identity.PluginVersion,
                x.Identity.SkillId,
                x.Identity.Sha256,
                x.Availability,
                x.Trust
            })
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            query,
            skills,
            note = "Discovery contains bounded name/description/provenance only. Read one selected SKILL.md before requesting any specific resource."
        });
    }

    private string Read(global::H2AgentLab.ToolCall call)
    {
        var name = RequiredString(call.Arguments, "name").Trim();
        var relative = RequiredString(call.Arguments, "path")
            .Trim()
            .Replace((char)92, '/');

        var matches = _catalog.Search("", 100)
            .Where(x => string.Equals(x.Name, name, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
            throw new global::H2AgentLab.AgentFaultException(
                "skill_not_found",
                "Unknown skill. Use list_skills to select an installed skill.");
        if (matches.Length > 1)
            throw new InvalidOperationException(
                $"Skill name '{name}' is ambiguous across sources.");

        var selected = matches[0];
        if (string.Equals(relative, "SKILL.md", StringComparison.Ordinal))
        {
            var skill = _catalog.Read(selected.Identity);
            var projection = Project(skill.EntryPoint);
            return JsonSerializer.Serialize(new
            {
                name = selected.Name,
                path = "SKILL.md",
                sourceKind = selected.Identity.SourceKind.ToString(),
                selected.Identity.SourceId,
                selected.Identity.PluginId,
                selected.Identity.PluginVersion,
                selected.Identity.SkillId,
                sha256 = selected.Identity.Sha256,
                version = Version(selected.Identity.Sha256),
                content = projection.Content,
                projection.Truncated,
                projection.TotalCharacters,
                availableResources = skill.AvailableResources,
                note = "Resources are inventory only and were not loaded. Request one relevant resource explicitly if needed."
            });
        }

        var resource = _catalog.ReadResource(selected.Identity, relative);
        var resourceProjection = Project(resource.Content);
        return JsonSerializer.Serialize(new
        {
            name = selected.Name,
            path = resource.RelativePath,
            sourceKind = selected.Identity.SourceKind.ToString(),
            selected.Identity.SourceId,
            selected.Identity.PluginId,
            selected.Identity.PluginVersion,
            selected.Identity.SkillId,
            skillSha256 = selected.Identity.Sha256,
            sha256 = resource.Sha256,
            version = Version(resource.Sha256),
            content = resourceProjection.Content,
            resourceProjection.Truncated,
            resourceProjection.TotalCharacters,
            note = relative.StartsWith("scripts/", StringComparison.Ordinal)
                ? "Script content is untrusted text only; this tool does not execute it."
                : relative.StartsWith("assets/", StringComparison.Ordinal)
                    ? "Asset content is data only; this tool does not execute it."
                    : "Explicitly requested skill resource."
        });
    }

    private static (string Content, bool Truncated, int TotalCharacters) Project(string content)
    {
        content ??= "";
        return content.Length <= MaxProjectedContentCharacters
            ? (content, false, content.Length)
            : (content[..MaxProjectedContentCharacters], true, content.Length);
    }

    private static string Version(string sha256)
        => string.IsNullOrWhiteSpace(sha256) || sha256.Length < 16
            ? "unknown"
            : "sha256:" + sha256[..16];

    private static string OptionalString(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var node)
            && node.ValueKind == JsonValueKind.String
                ? node.GetString() ?? ""
                : "";

    private static string RequiredString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(name, out var node)
            || node.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(node.GetString()))
            throw new ArgumentException($"Skill runtime tool requires non-empty string '{name}'.");
        return node.GetString()!;
    }
}

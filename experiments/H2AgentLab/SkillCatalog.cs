namespace H2AgentLab;

public sealed record LabSkill(string Name, string Description, string Directory);

/// <summary>
/// Legacy v1 facade kept for AgentTools/UI compatibility. Discovery, metadata parsing and content
/// loading are owned by H2AgentLab.Skills.SkillCatalog + BuiltInSkillSource.
/// </summary>
public sealed class SkillCatalog
{
    private readonly global::H2AgentLab.Skills.BuiltInSkillSource _source;

    public string Root { get; }
    public IReadOnlyList<LabSkill> Skills { get; }
    public global::H2AgentLab.Skills.SkillCatalog Canonical { get; }

    public SkillCatalog(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(AppContext.BaseDirectory, "skills"));
        _source = new global::H2AgentLab.Skills.BuiltInSkillSource(Root);
        Canonical = new global::H2AgentLab.Skills.SkillCatalog();
        Canonical.Register(_source);
        Skills = _source.InstalledSkills;
    }

    public string Discovery
        => string.Join("\n", Skills.Select(s => $"- {s.Name}: {s.Description}"));

    public object Discover(string query)
    {
        if (Skills.Count == 0)
            throw new AgentFaultException(
                "unavailable",
                "Không tìm thấy skill trong " + Root + ". Giải nén cả thư mục skills từ gói Portable; không tự tải hoặc bịa tên skill.",
                false);

        query ??= "";
        var matches = Canonical.Search(query, 100)
            .Select(s => new { s.Name, s.Description })
            .ToArray();
        if (matches.Length != 0)
            return matches;

        return new
        {
            matches,
            availableSkills = Canonical.Search("", 100)
                .Select(s => new { s.Name, s.Description })
                .ToArray(),
            note = "No skill matched this topic. Select an actual name from availableSkills, or call list_skills with {} to list all. This is not evidence that skills are missing."
        };
    }

    public string Read(string name, string relative)
    {
        if (Skills.Count == 0)
            _ = Discover("");

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);
        var summary = Canonical.Search("", 100)
            .SingleOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.Ordinal))
            ?? throw new AgentFaultException(
                "skill_not_found",
                "Unknown skill. Use list_skills. Available names: " + string.Join(", ", Skills.Select(s => s.Name)));

        var path = relative.Trim().Replace('\\', '/');
        if (path == "SKILL.md")
            return Canonical.Read(summary.Identity).EntryPoint;

        if (Path.GetExtension(path).ToLowerInvariant() is not (".md" or ".py" or ".json" or ".txt"))
            throw new IOException("Unsupported skill resource.");

        try
        {
            return Canonical.ReadResource(summary.Identity, path).Content;
        }
        catch (FileNotFoundException)
        {
            var skill = Skills.Single(s => s.Name == summary.Name);
            var scope = new SafeWorkspace(skill.Directory);
            throw new AgentFaultException(
                "resource_not_found",
                "Resource not found. Read path SKILL.md first. Available resources: "
                + string.Join(", ", scope.Files().Take(30)));
        }
    }
}

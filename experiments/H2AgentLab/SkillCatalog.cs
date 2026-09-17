using System.Text.RegularExpressions;

namespace H2AgentLab;

public sealed record LabSkill(string Name, string Description, string Directory);

public sealed class SkillCatalog
{
    public string Root { get; }
    public IReadOnlyList<LabSkill> Skills { get; }
    public SkillCatalog(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(AppContext.BaseDirectory, "skills"));
        var skills = new List<LabSkill>();
        if (System.IO.Directory.Exists(Root))
        {
            foreach (var dir in System.IO.Directory.EnumerateDirectories(Root).Order())
            {
                var scope = new SafeWorkspace(dir);
                var file = scope.Resolve("SKILL.md");
                if (!File.Exists(file)) continue;
                var text = File.ReadAllText(file);
                if (text.Length > 60000 || !text.StartsWith("---")) throw new IOException("Invalid skill: " + dir);
                var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
                if (end < 0) throw new IOException("Missing skill metadata: " + dir);
                var header = text[..end];
                string Field(string name) => Regex.Match(header, "(?m)^" + name + @":\s*(.+)$").Groups[1].Value.Trim().Trim('"', '\'');
                var name = Field("name"); var description = Field("description");
                if (!Regex.IsMatch(name, "^[a-z0-9-]{1,64}$") || description.Length is < 5 or > 1500 || skills.Any(s => s.Name == name))
                    throw new IOException("Invalid or duplicate skill metadata: " + dir);
                skills.Add(new(name, description, dir));
            }
        }
        Skills = skills;
    }
    public string Discovery => string.Join("\n", Skills.Select(s => $"- {s.Name}: {s.Description}"));
    public object Discover(string query)
    {
        if (Skills.Count == 0)
            throw new AgentFaultException("unavailable", "Không tìm thấy skill trong " + Root + ". Giải nén cả thư mục skills từ gói Portable; không tự tải hoặc bịa tên skill.", false);
        var matches = Skills.Where(s => query.Length == 0 || (s.Name + " " + s.Description).Contains(query, StringComparison.OrdinalIgnoreCase)).Select(s => new { s.Name, s.Description }).ToArray();
        if (matches.Length != 0) return matches;
        return new { matches, availableSkills = Skills.Select(s => new { s.Name, s.Description }).ToArray(),
            note = "No skill matched this topic. Select an actual name from availableSkills, or call list_skills with {} to list all. This is not evidence that skills are missing." };
    }
    public string Read(string name, string relative)
    {
        if (Skills.Count == 0) _ = Discover("");
        var skill = Skills.SingleOrDefault(s => s.Name == name) ?? throw new AgentFaultException("skill_not_found", "Unknown skill. Use list_skills. Available names: " + string.Join(", ", Skills.Select(s => s.Name)));
        if (relative == "references/runtime.md") return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "runtime-guide.md"));
        var scope = new SafeWorkspace(skill.Directory);
        if (Path.GetExtension(relative).ToLowerInvariant() is not (".md" or ".py" or ".json" or ".txt")) throw new IOException("Unsupported skill resource.");
        var path = scope.Resolve(relative);
        if (!File.Exists(path)) throw new AgentFaultException("resource_not_found", "Resource not found. Read path SKILL.md first. Available resources: " + string.Join(", ", scope.Files().Take(30)));
        if (new FileInfo(path).Length > 80000) throw new IOException("Skill resource exceeds 80 KB.");
        return File.ReadAllText(path);
    }
}

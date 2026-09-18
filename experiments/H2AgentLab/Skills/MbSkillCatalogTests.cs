using H2AgentLab.Tools;

namespace H2AgentLab.Skills;

public static class MbSkillCatalogTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-50 test directory.");
        Directory.CreateDirectory(root);

        var lines = new List<string>();
        var failed = 0;

        async Task Test(string name, Func<Task> action)
        {
            try
            {
                await action();
                lines.Add("PASS " + name);
            }
            catch (Exception ex)
            {
                failed++;
                lines.Add("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        await Test("MB-50 canonical SkillCatalog owns built-in search read and provenance", () =>
        {
            var skillsRoot = Path.Combine(root, "built-in-skills");
            WriteSkill(
                skillsRoot,
                "formula-audit",
                "Inspect spreadsheet formulas and workbook consistency.",
                "Use this skill to inspect formulas.",
                "references/checks.md",
                "Check formulas, ranges, and workbook state.");

            var catalog = new H2AgentLab.Skills.SkillCatalog();
            catalog.Register(new BuiltInSkillSource(skillsRoot));

            var selected = catalog.Search("spreadsheet formulas", 10).Single();
            Check(selected.Name == "formula-audit"
                && selected.Identity.SourceKind == SkillSourceKind.BuiltIn
                && selected.Identity.SourceId == "built-in"
                && selected.Identity.Sha256.Length == 64,
                "Canonical built-in search lost source/hash provenance.");

            var content = catalog.Read(selected.Identity);
            var resource = catalog.ReadResource(
                selected.Identity,
                "references/checks.md");
            Check(content.EntryPoint.Contains("inspect formulas", StringComparison.OrdinalIgnoreCase)
                && resource.Content.Contains("workbook state", StringComparison.OrdinalIgnoreCase)
                && resource.Sha256.Length == 64,
                "Canonical read APIs did not return selected entry/resource content.");
            return Task.CompletedTask;
        });

        await Test("MB-50 one catalog merges source adapters while retaining plugin provenance", () =>
        {
            var skillsRoot = Path.Combine(root, "mixed-skills");
            WriteSkill(
                skillsRoot,
                "general-writing",
                "General writing and document guidance.",
                "Built-in writing guidance.");

            var catalog = new H2AgentLab.Skills.SkillCatalog();
            catalog.Register(new BuiltInSkillSource(skillsRoot));
            catalog.Register(new FixturePluginSkillSource());

            var results = catalog.Search("", 10);
            Check(catalog.SourceIds.SequenceEqual(new[] { "built-in", "fixture-plugin" }),
                "Canonical catalog did not expose both registered sources.");
            var plugin = results.Single(x => x.Name == "plugin-review");
            Check(plugin.Identity.SourceKind == SkillSourceKind.Plugin
                && plugin.Identity.PluginId == "fixture.productivity"
                && plugin.Identity.PluginVersion == "2.3.0"
                && plugin.Identity.SourceId == "fixture-plugin",
                "Plugin source provenance was lost in canonical search.");

            var content = catalog.Read(plugin.Identity);
            Check(content.EntryPoint == "Plugin review instructions.",
                "Canonical read did not dispatch through the selected plugin source.");
            return Task.CompletedTask;
        });

        await Test("MB-50 legacy catalog and deferred session are compatibility adapters over canonical catalog", () =>
        {
            var skillsRoot = Path.Combine(root, "legacy-skills");
            WriteSkill(
                skillsRoot,
                "compatibility",
                "Compatibility adapter fixture guidance.",
                "Canonical compatibility body.",
                "references/detail.md",
                "Canonical detail resource.");

            var legacy = new global::H2AgentLab.SkillCatalog(skillsRoot);
            Check(legacy.Canonical.SourceIds.SequenceEqual(new[] { "built-in" }),
                "Legacy SkillCatalog did not delegate to canonical catalog.");
            Check(legacy.Read("compatibility", "SKILL.md")
                    .Contains("Canonical compatibility body.", StringComparison.Ordinal)
                && legacy.Read("compatibility", "references/detail.md")
                    .Contains("Canonical detail resource.", StringComparison.Ordinal),
                "Legacy facade did not delegate entry/resource reads.");

            var session = new DeferredSkillSession(legacy);
            Check(ReferenceEquals(session.Catalog, legacy.Canonical),
                "DeferredSkillSession created a competing skill catalog.");
            var first = session.Read("compatibility", "SKILL.md");
            var second = session.Read("compatibility", "SKILL.md");
            Check(!first.Unchanged
                && first.Content?.Contains("Canonical compatibility body.", StringComparison.Ordinal) == true
                && second.Unchanged
                && second.Content is null
                && first.Sha256 == second.Sha256,
                "Deferred compatibility cache did not reuse canonical selected content.");
            return Task.CompletedTask;
        });

        await Test("MB-50 source guard leaves no duplicate built-in parser/catalog implementation", () =>
        {
            var repo = FindRepoRoot();
            var legacySource = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "SkillCatalog.cs"));
            var deferredSource = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "Tools", "DeferredSkillSession.cs"));
            var canonicalSource = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "Skills", "UnifiedSkillCatalog.cs"));
            var indexesSource = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "Capabilities", "CapabilityIndexes.cs"));

            Check(legacySource.Contains("Skills.SkillCatalog", StringComparison.Ordinal)
                && legacySource.Contains("BuiltInSkillSource", StringComparison.Ordinal)
                && !legacySource.Contains("RegularExpressions", StringComparison.Ordinal)
                && !legacySource.Contains("Regex.", StringComparison.Ordinal),
                "Legacy SkillCatalog still owns duplicate built-in metadata parsing.");
            Check(deferredSource.Contains("H2AgentLab.Skills.SkillCatalog", StringComparison.Ordinal)
                && !deferredSource.Contains("SafeWorkspace", StringComparison.Ordinal)
                && !deferredSource.Contains("FileInfo", StringComparison.Ordinal),
                "DeferredSkillSession still owns a competing filesystem skill path.");
            Check(canonicalSource.Contains("public class SkillCatalog", StringComparison.Ordinal)
                && canonicalSource.Contains(
                    "public sealed class UnifiedSkillCatalog : SkillCatalog",
                    StringComparison.Ordinal),
                "UnifiedSkillCatalog is not a thin compatibility name over canonical SkillCatalog.");
            Check(indexesSource.Contains("SkillCatalog skills", StringComparison.Ordinal)
                && !indexesSource.Contains("UnifiedSkillCatalog skills", StringComparison.Ordinal),
                "Installed capability projection still requires the historical unified catalog type.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-skill-catalog-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static void WriteSkill(
        string root,
        string name,
        string description,
        string body,
        string? resourcePath = null,
        string? resourceContent = null)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "SKILL.md"),
            "---\nname: " + name + "\ndescription: " + description + "\n---\n" + body + "\n");
        if (!string.IsNullOrWhiteSpace(resourcePath))
        {
            var full = Path.Combine(
                directory,
                resourcePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, resourceContent ?? "");
        }
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(current.FullName, "experiments")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed class FixturePluginSkillSource : ISkillSource
    {
        private static readonly SkillIdentity Identity = new(
            SkillSourceKind.Plugin,
            "fixture-plugin",
            "fixture.productivity",
            "2.3.0",
            "plugin-review",
            new string('a', 64));

        public string SourceId => "fixture-plugin";
        public SkillSourceKind SourceKind => SkillSourceKind.Plugin;

        public IReadOnlyList<SkillSummary> Search(string query, int maxResults = 20)
        {
            if (maxResults is < 1 or > 100)
                throw new ArgumentOutOfRangeException(nameof(maxResults));
            var summary = new SkillSummary(
                Identity,
                "plugin-review",
                "Review plugin-provided structured work.",
                "installed",
                "plugin");
            return BuiltInSkillSource.Rank([summary], query, maxResults);
        }

        public SkillContent Read(SkillIdentity identity)
        {
            Ensure(identity);
            return new SkillContent(
                new SkillSummary(
                    Identity,
                    "plugin-review",
                    "Review plugin-provided structured work.",
                    "installed",
                    "plugin"),
                "Plugin review instructions.",
                ["references/plugin.md"]);
        }

        public SkillResourceContent ReadResource(
            SkillIdentity identity,
            string relativePath)
        {
            Ensure(identity);
            if (relativePath != "references/plugin.md")
                throw new FileNotFoundException("Fixture plugin resource is unavailable.");
            const string content = "Plugin resource.";
            return new SkillResourceContent(
                Identity,
                relativePath,
                BuiltInSkillSource.HashText(content),
                content);
        }

        private static void Ensure(SkillIdentity identity)
        {
            if (identity != Identity)
                throw new InvalidOperationException("Fixture identity mismatch.");
        }
    }
}

namespace H2AgentLab.Skills;

public static class MbSkillDiscoveryTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-51 test directory.");
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

        await Test("MB-51 differently named cad-integrity is selected from description metadata", () =>
        {
            var skillsRoot = Path.Combine(root, "description-search");
            WriteSkill(
                skillsRoot,
                "cad-integrity",
                "Inspect AutoCAD dynamic blocks, attributes, parameters, actions and visibility-state consistency.",
                "Detailed instructions are intentionally not required for discovery.");
            WriteSkill(
                skillsRoot,
                "general-writing",
                "General writing and document formatting guidance.",
                "Unrelated writing instructions.");

            var catalog = new H2AgentLab.Skills.SkillCatalog();
            catalog.Register(new BuiltInSkillSource(skillsRoot));

            const string query = "check parameters and actions of these dynamic blocks";
            Check(!query.Contains("cad-integrity", StringComparison.OrdinalIgnoreCase),
                "Acceptance fixture accidentally contains the exact skill name.");

            var results = catalog.Search(query, 10);
            Check(results.Count > 0
                && results[0].Name == "cad-integrity"
                && results[0].Description.Contains("dynamic blocks", StringComparison.OrdinalIgnoreCase),
                "Name+description discovery did not select cad-integrity without exact-name matching.");
            return Task.CompletedTask;
        });

        await Test("MB-51 local discovery ignores full SKILL body content", () =>
        {
            var skillsRoot = Path.Combine(root, "metadata-only");
            WriteSkill(
                skillsRoot,
                "metadata-target",
                "Spreadsheet formulas and workbook consistency checks.",
                "Normal selected body.");
            WriteSkill(
                skillsRoot,
                "unrelated-guide",
                "General prose editing guidance.",
                "sentinelbodyterm sentinelbodyterm sentinelbodyterm");

            var catalog = new H2AgentLab.Skills.SkillCatalog();
            catalog.Register(new BuiltInSkillSource(skillsRoot));

            var bodyOnly = catalog.Search("sentinelbodyterm", 10);
            Check(bodyOnly.Count == 0,
                "Local discovery searched full SKILL.md body instead of bounded metadata.");

            var metadata = catalog.Search("workbook formulas", 10);
            Check(metadata.Count == 1
                && metadata[0].Name == "metadata-target"
                && !metadata[0].Description.Contains("Normal selected body.", StringComparison.Ordinal),
                "Discovery surface leaked full instructions instead of name+description metadata.");
            return Task.CompletedTask;
        });

        await Test("MB-51 authoring requires only bounded name and description metadata", () =>
        {
            var minimalRoot = Path.Combine(root, "minimal-authoring");
            WriteSkill(
                minimalRoot,
                "minimal-skill",
                "Minimal local skill description for simple discovery.",
                "Body.");

            var source = new BuiltInSkillSource(minimalRoot);
            var result = source.Search("simple discovery", 5).Single();
            Check(result.Name == "minimal-skill",
                "Minimal name+description skill was not accepted without aliases/intents/examples.");

            var oversizedRoot = Path.Combine(root, "oversized-metadata");
            WriteSkill(
                oversizedRoot,
                "oversized-skill",
                new string('x', 1501),
                "Body.");
            try
            {
                _ = new BuiltInSkillSource(oversizedRoot);
                throw new InvalidOperationException(
                    "Oversized description metadata was accepted without a bound.");
            }
            catch (IOException ex) when (
                ex.Message.Contains("metadata", StringComparison.OrdinalIgnoreCase))
            {
            }

            return Task.CompletedTask;
        });

        await Test("MB-51 generic core contains no domain synonym dictionary or mandatory rich intent schema", () =>
        {
            var repo = FindRepoRoot();
            var capabilitySource = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "Capabilities", "CapabilityIndexes.cs"));
            var skillSource = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "Skills", "UnifiedSkillCatalog.cs"));
            var pluginSource = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab", "Plugins", "PluginSkillCatalog.cs"));

            Check(!capabilitySource.Contains("Synonyms(", StringComparison.Ordinal)
                && !capabilitySource.Contains("\"dynamic\" =>", StringComparison.Ordinal)
                && !capabilitySource.Contains("\"autocad\" =>", StringComparison.Ordinal)
                && !capabilitySource.Contains("\"legal\" =>", StringComparison.Ordinal),
                "Generic capability core still contains a domain-specific synonym dictionary.");

            Check(skillSource.Contains("Field(\"name\")", StringComparison.Ordinal)
                && skillSource.Contains("Field(\"description\")", StringComparison.Ordinal)
                && !skillSource.Contains("Field(\"aliases\")", StringComparison.Ordinal)
                && !skillSource.Contains("Field(\"intents\")", StringComparison.Ordinal)
                && !skillSource.Contains("Field(\"examples\")", StringComparison.Ordinal),
                "Built-in skill parser requires metadata beyond name+description.");

            Check(!pluginSource.Contains("aliases:", StringComparison.OrdinalIgnoreCase)
                && !pluginSource.Contains("intents:", StringComparison.OrdinalIgnoreCase),
                "Plugin skill discovery introduced a mandatory alias/intent authoring schema.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-skill-discovery-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static void WriteSkill(
        string root,
        string name,
        string description,
        string body)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "SKILL.md"),
            "---\nname: " + name + "\ndescription: " + description + "\n---\n" + body + "\n");
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
}

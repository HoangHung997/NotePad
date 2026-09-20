using H2Notes.Core;

internal static class H2LegacyRequestContextRetirementTests
{
    public static void Run(Action<string, Action> test)
    {
        test("AiProjectContext type is fully retired from runtime source", () =>
        {
            var repo = FindRepoRoot();
            var src = Path.Combine(repo, "src");
            var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).ToArray();

            Check(!File.Exists(Path.Combine(src, "H2Notes.Core", "AiProjectContext.cs")),
                "Retired AiProjectContext.cs file returned.");

            foreach (var file in files)
            {
                var source = File.ReadAllText(file);
                Check(!source.Contains("class AiProjectContext", StringComparison.Ordinal)
                    && !source.Contains("AiProjectContext.", StringComparison.Ordinal),
                    "Retired AiProjectContext symbol returned in runtime source: " + file);
            }
        });

        test("Legacy request context is confined to standalone direct-chat compatibility paths", () =>
        {
            var repo = FindRepoRoot();
            var src = Path.Combine(repo, "src");

            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(src, "H2Notes.Core", "AiLegacyRequestContext.cs"),
                Path.Combine(src, "H2Notes.Core", "AiPdfProcessor.cs"),
                Path.Combine(src, "H2Notes.Avalonia", "Controls", "AiChatPanel.cs"),
                Path.Combine(src, "H2Notes.Avalonia", "Controls", "AiChatPanel.Files.cs"),
                Path.Combine(src, "H2Notes.Avalonia", "Controls", "AiChatPanel.Pdf.cs")
            };

            var references = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
                .Where(path => File.ReadAllText(path).Contains("AiLegacyRequestContext", StringComparison.Ordinal))
                .ToArray();

            Check(references.Length != 0, "Legacy compatibility helper unexpectedly disappeared before standalone retirement.");
            foreach (var path in references)
                Check(allowed.Contains(path),
                    "AiLegacyRequestContext leaked outside standalone/PDF compatibility boundary: " + path);
        });

        test("Project Agent grounding is bounded and independent from legacy giant history builder", () =>
        {
            var repo = FindRepoRoot();
            var agent = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "Controls", "AiChatPanel.Agent.cs"));

            Check(agent.Contains("new H2AgentTaskContext", StringComparison.Ordinal)
                || agent.Contains("return new H2AgentTaskContext", StringComparison.Ordinal),
                "Project Agent path no longer constructs H2AgentTaskContext.");

            Check(agent.Contains("BoundAgentContext(summary.ToString(), 15_000)", StringComparison.Ordinal),
                "Project Agent context no longer has the explicit 15,000-character H2 bound.");

            foreach (var forbidden in new[]
            {
                "AiLegacyRequestContext",
                "BuildForRequest(",
                "BuildWorkspace(",
                "RecentConversationMessages",
                "MaxRequestCharacters",
                "JsonSerializer.Serialize(project.Conversations"
            })
                Check(!agent.Contains(forbidden, StringComparison.Ordinal),
                    "Project Agent path depends on legacy giant-history context marker: " + forbidden);
        });

        test("Legacy request context naming makes standalone-only compatibility explicit", () =>
        {
            var repo = FindRepoRoot();
            var path = Path.Combine(repo, "src", "H2Notes.Core", "AiLegacyRequestContext.cs");
            var source = File.ReadAllText(path);

            Check(source.Contains("public static class AiLegacyRequestContext", StringComparison.Ordinal),
                "Legacy helper was not renamed.");
            Check(source.Contains("New project Agent tasks must never use this type", StringComparison.Ordinal),
                "Legacy helper is missing the project-Agent prohibition.");
        });
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(current.FullName, "src")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}

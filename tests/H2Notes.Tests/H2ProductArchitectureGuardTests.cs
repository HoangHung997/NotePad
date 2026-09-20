using System.Text.RegularExpressions;
using H2Notes.Core;

internal static class H2ProductArchitectureGuardTests
{
    public static void Run(Action<string, Action> test)
    {
        test("H2M-100 no giant ProjectState or second durable project-agent state store", () =>
        {
            GuardNoRuntimeType("ProjectState");
            var projectProperties = typeof(ProjectRecord).GetProperties().Select(p => p.Name).ToArray();
            Check(!projectProperties.Any(name => name.Contains("AgentTask", StringComparison.OrdinalIgnoreCase)
                                                 || name.Contains("Evidence", StringComparison.OrdinalIgnoreCase)
                                                 || name.Contains("Verification", StringComparison.OrdinalIgnoreCase)),
                "ProjectRecord embeds Agent-owned task/evidence/verification state.");
        });

        test("H2M-101 NeedsAttention remains rebuildable projection with no AIInboxStore", () =>
        {
            GuardNoRuntimeType("AIInboxStore");
            Check(typeof(NeedsAttentionProjection).IsClass,
                "NeedsAttentionProjection contract disappeared.");
            var service = ReadSource("src/H2Notes.Core/H2ProductProjections.cs");
            Check(service.Contains("BuildNeedsAttention(", StringComparison.Ordinal),
                "NeedsAttention is no longer built through product projection service.");
            GuardNoPersistenceMarkers(service, "NeedsAttention projection service");
        });

        test("H2M-102 research remains evidence view with no independent ResearchStore", () =>
        {
            GuardNoRuntimeType("ResearchStore");
            var projection = ReadSource("src/H2Notes.Core/H2EvidenceInspection.cs");
            var ui = ReadSource("src/H2Notes.Avalonia/MainWindow.AgentEvidenceInspector.cs");
            Check(projection.Contains("_agent.GetEvidence(", StringComparison.Ordinal)
                  && projection.Contains("H2EvidenceInspectionProjection", StringComparison.Ordinal),
                "Project evidence projection no longer resolves authoritative Agent evidence.");
            Check(ui.Contains("H2EvidenceInspectionProjectionService", StringComparison.Ordinal),
                "Project evidence inspector UI bypasses the evidence projection service.");
            GuardNoPersistenceMarkers(projection, "Project evidence projection");
            GuardNoPersistenceMarkers(ui, "Project evidence inspector");
        });

        test("H2M-103 formal ProjectDecision subsystem remains deferred", () =>
        {
            GuardNoRuntimeType("ProjectDecision");
            var projectProperties = typeof(ProjectRecord).GetProperties().Select(p => p.Name).ToArray();
            Check(!projectProperties.Any(name => name.Contains("Decision", StringComparison.OrdinalIgnoreCase)),
                "ProjectRecord gained formal durable decision workflow state.");
        });

        test("H2M-104 quick work is Agent task with nullable ProjectId and no QuickWorkSession", () =>
        {
            GuardNoRuntimeType("QuickWorkSession");
            var app = ReadSource("src/H2Notes.Avalonia/App.axaml.cs");
            Check(app.Contains("_agentAdapter.StartTaskAsync(", StringComparison.Ordinal)
                  && app.Contains("projectId: null", StringComparison.Ordinal),
                "Work Assistant quick work is no longer an unscoped Agent task.");
            Check(!typeof(ProjectRecord).GetProperties().Any(p =>
                    p.Name.Contains("QuickWork", StringComparison.OrdinalIgnoreCase)),
                "ProjectRecord embeds quick-work session state.");
        });

        test("H2M-105 project progress is deterministic task projection, never model-written percentage", () =>
        {
            var overviewFields = typeof(ProjectOverviewProjection)
                .GetProperties()
                .Select(p => p.Name)
                .ToArray();
            Check(overviewFields.Contains(nameof(ProjectOverviewProjection.CompletedTasks), StringComparer.Ordinal)
                  && overviewFields.Contains(nameof(ProjectOverviewProjection.TotalTasks), StringComparer.Ordinal),
                "Project overview lost deterministic completed/total task counts.");
            Check(!overviewFields.Any(name => name.Contains("Percent", StringComparison.OrdinalIgnoreCase)
                                              || name.Contains("Percentage", StringComparison.OrdinalIgnoreCase)),
                "Project overview gained durable/model-authored percent field.");

            var projectFields = typeof(ProjectRecord).GetProperties().Select(p => p.Name).ToArray();
            Check(!projectFields.Any(name => name.Contains("Percent", StringComparison.OrdinalIgnoreCase)
                                             || name.Contains("Percentage", StringComparison.OrdinalIgnoreCase)),
                "ProjectRecord gained arbitrary progress percentage.");

            var service = ReadSource("src/H2Notes.Core/H2ProductProjections.cs");
            Check(service.Contains("ProjectProgressCalculator.Calculate(project)", StringComparison.Ordinal)
                  && service.Contains("progress.Completed", StringComparison.Ordinal)
                  && service.Contains("progress.Total", StringComparison.Ordinal),
                "Project overview is not derived from deterministic ProjectProgressCalculator.");
        });
    }

    private static void GuardNoRuntimeType(string typeName)
    {
        var src = Path.Combine(FindRepoRoot(), "src");
        var pattern = new Regex(
            @"\b(class|record|struct)\s+" + Regex.Escape(typeName) + @"\b",
            RegexOptions.CultureInvariant);
        foreach (var path in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(path);
            if (pattern.IsMatch(source))
                throw new Exception($"Forbidden runtime type {typeName} found in {path}");
        }
    }

    private static void GuardNoPersistenceMarkers(string source, string owner)
    {
        foreach (var marker in new[]
        {
            "File.Write", "FileStream", "AtomicWrite(", "Sqlite",
            "Database", "ProjectWorkspaceStore.Save", "JsonSerializer.SerializeToUtf8Bytes"
        })
            Check(!source.Contains(marker, StringComparison.Ordinal),
                owner + " contains persistence marker: " + marker);
    }

    private static string ReadSource(string relative)
        => File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            relative.Replace('/', Path.DirectorySeparatorChar)));

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
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
}

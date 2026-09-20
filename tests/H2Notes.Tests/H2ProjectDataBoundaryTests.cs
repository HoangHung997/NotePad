using H2Notes.Core;

internal static class H2ProjectDataBoundaryTests
{
    public static void Run(Action<string, Action> test)
    {
        test("ProjectRecord remains durable project truth and does not embed Agent execution state", () =>
        {
            var names = typeof(ProjectRecord).GetProperties()
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var required in new[]
            {
                "Id", "Name", "NameRich", "Notes", "NotesRich",
                "ChecklistItems", "Links", "Extra"
            })
                if (!names.Contains(required))
                    throw new Exception("ProjectRecord lost required durable field: " + required);

            GuardProperties(typeof(ProjectRecord));
        });

        test("TaskRecord remains a human project checklist item not an Agent execution step", () =>
        {
            var names = typeof(TaskRecord).GetProperties()
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var required in new[]
            {
                "Id", "Text", "TextRich", "Comment", "CommentRich",
                "IsCompleted", "CreatedAtUtc", "UpdatedAtUtc", "CompletedAtUtc", "Extra"
            })
                if (!names.Contains(required))
                    throw new Exception("TaskRecord lost required checklist field: " + required);

            GuardProperties(typeof(TaskRecord));
        });

        test("Project model source contains no new Agent execution persistence markers", () =>
        {
            var repo = FindRepoRoot();
            var source = File.ReadAllText(Path.Combine(repo, "src", "H2Notes.Core", "SheetModels.cs"));

            foreach (var marker in ForbiddenMarkers)
                if (source.Contains(marker, StringComparison.Ordinal))
                    throw new Exception("SheetModels gained forbidden Agent execution marker: " + marker);

            foreach (var marker in new[] { "IH2AgentAdapter", "H2AgentTaskSummary", "H2AgentEvidence", "H2AgentProgress" })
                if (source.Contains(marker, StringComparison.Ordinal))
                    throw new Exception("Project persistence source now depends on Agent projection type: " + marker);
        });

        test("Agent correlation and projections leave ProjectRecord serialization independent", () =>
        {
            var project = new ProjectRecord { Name = "Boundary project" };
            project.ChecklistItems.Add(new TaskRecord { Text = "Human task" });
            var before = System.Text.Json.JsonSerializer.Serialize(project);

            var correlations = new H2AgentTaskCorrelationIndex();
            var agentTaskId = Guid.NewGuid();
            correlations.Register(agentTaskId, project.Id);

            var after = System.Text.Json.JsonSerializer.Serialize(project);
            if (before != after)
                throw new Exception("External Agent correlation changed ProjectRecord JSON.");
            if (after.Contains(agentTaskId.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new Exception("Agent task identity leaked into ProjectRecord JSON.");
        });
    }

    private static readonly string[] ForbiddenMarkers =
    [
        "AgentTasks",
        "AgentRuns",
        "AgentSteps",
        "VerificationReports",
        "VerificationReport",
        "ToolRuns",
        "ToolRun",
        "AgentEvidence",
        "AgentTrace",
        "ApprovalQueue",
        "AgentProgressPercent",
        "ProjectState"
    ];

    private static void GuardProperties(Type type)
    {
        foreach (var property in type.GetProperties())
        {
            foreach (var marker in ForbiddenMarkers)
            {
                if (property.Name.Contains(marker, StringComparison.OrdinalIgnoreCase)
                    || TypeContains(property.PropertyType, marker))
                    throw new Exception($"{type.Name}.{property.Name} embeds forbidden Agent execution state: {marker}");
            }

            foreach (var marker in new[]
            {
                "H2AgentTaskSummary",
                "H2AgentTaskObservation",
                "H2AgentProgress",
                "H2AgentEvidence",
                "AgentIntegrationTask",
                "AgentIntegrationEvidence"
            })
                if (TypeContains(property.PropertyType, marker))
                    throw new Exception($"{type.Name}.{property.Name} depends on Agent execution DTO: {marker}");
        }
    }

    private static bool TypeContains(Type type, string marker)
    {
        if ((type.FullName ?? type.Name).Contains(marker, StringComparison.OrdinalIgnoreCase))
            return true;
        if (type.IsArray && type.GetElementType() is { } element && TypeContains(element, marker))
            return true;
        if (type.IsGenericType && type.GetGenericArguments().Any(argument => TypeContains(argument, marker)))
            return true;
        return false;
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

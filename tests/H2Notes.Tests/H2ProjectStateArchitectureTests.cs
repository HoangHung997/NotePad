using System.Reflection;
using H2Notes.Core;

internal static class H2ProjectStateArchitectureTests
{
    public static void Run(Action<string, Action> test)
    {
        test("No durable ProjectState type exists in H2 runtime source", () =>
        {
            var repo = FindRepoRoot();
            var src = Path.Combine(repo, "src");

            foreach (var path in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
            {
                var source = File.ReadAllText(path);
                if (System.Text.RegularExpressions.Regex.IsMatch(
                    source,
                    @"\b(class|record|struct)\s+ProjectState\b",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                    throw new Exception("Forbidden durable ProjectState type found: " + path);
            }
        });

        test("Command Center data source is projection-only and owns no truth store", () =>
        {
            var fields = typeof(H2CommandCenterQueryService)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (fields.Length != 1 || fields[0].FieldType != typeof(H2ProductProjectionService))
                throw new Exception("Command Center query service gained non-projection state.");

            foreach (var method in typeof(H2CommandCenterQueryService).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Where(method => method.DeclaringType == typeof(H2CommandCenterQueryService)))
            {
                var returnName = method.ReturnType.FullName ?? method.ReturnType.Name;
                if (!returnName.Contains("IReadOnlyList", StringComparison.Ordinal))
                    throw new Exception("Command Center query exposed non-query return type: " + method.Name);
            }
        });

        test("Command Center query rebuilds from projects and Agent projections without mutating truth", () =>
        {
            var project = new ProjectRecord { Name = "ProjectState guard" };
            project.ChecklistItems.Add(new TaskRecord { Text = "Task A", IsCompleted = true });
            project.ChecklistItems.Add(new TaskRecord { Text = "Task B" });
            var before = System.Text.Json.JsonSerializer.Serialize(project);

            var agent = new EmptyAgentAdapter();
            var query = new H2CommandCenterQueryService(new H2ProductProjectionService(agent));
            var first = query.GetProjects([project]).Single();
            query = new H2CommandCenterQueryService(new H2ProductProjectionService(agent));
            var second = query.GetProjects([project]).Single();

            if (first != second)
                throw new Exception("Fresh projection/query service did not rebuild equivalent card.");
            if (before != System.Text.Json.JsonSerializer.Serialize(project))
                throw new Exception("Command Center query mutated ProjectRecord truth.");
            if (first.CompletedTasks != 1 || first.TotalTasks != 2 || first.NextTask != "Task B")
                throw new Exception("Command Center query did not use deterministic project projection.");
        });

        test("Command Center projection source contains no persistence implementation", () =>
        {
            var repo = FindRepoRoot();
            var source = File.ReadAllText(Path.Combine(repo, "src", "H2Notes.Core", "H2CommandCenterQueryService.cs"));
            foreach (var marker in new[]
            {
                "File.Write", "FileStream", "AtomicWrite(", "Sqlite", "Database",
                "ProjectWorkspaceStore.Save", "JsonSerializer.SerializeToUtf8Bytes"
            })
                if (source.Contains(marker, StringComparison.Ordinal))
                    throw new Exception("Command Center query contains persistence marker: " + marker);
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

    private sealed class EmptyAgentAdapter : IH2AgentAdapter
    {
        public Task<Guid> StartTaskAsync(Guid? projectId, string goal, H2AgentTaskContext? context = null, bool readOnly = true, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public H2AgentTaskObservation ObserveTask(Guid taskId, long afterSequence = -1)
            => throw new NotSupportedException();

        public void CancelTask(Guid taskId) => throw new NotSupportedException();

        public bool RespondToApproval(Guid taskId, Guid approvalId, bool approved)
            => throw new NotSupportedException();

        public H2AgentTaskSummary GetTaskSummary(Guid taskId)
            => throw new KeyNotFoundException();

        public IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(Guid? projectId = null, int limit = 50)
            => Array.Empty<H2AgentTaskSummary>();

        public H2AgentEvidence? GetEvidence(string evidenceId) => null;

        public bool AttachProject(Guid taskId, Guid projectId) => false;
    }
}

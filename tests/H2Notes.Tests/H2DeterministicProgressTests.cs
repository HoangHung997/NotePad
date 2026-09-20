using System.Reflection;
using H2Notes.Core;

internal static class H2DeterministicProgressTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Project progress is completed human tasks over total human tasks", () =>
        {
            var project = new ProjectRecord();
            var empty = ProjectProgressCalculator.Calculate(project);
            if (empty.Completed != 0 || empty.Total != 0 || empty.Percent != 0 || empty.Label != "0/0")
                throw new Exception("Empty deterministic progress is wrong.");

            project.ChecklistItems.Add(new TaskRecord { Text = "A", IsCompleted = true });
            project.ChecklistItems.Add(new TaskRecord { Text = "B", IsCompleted = false });
            project.ChecklistItems.Add(new TaskRecord { Text = "C", IsCompleted = true });
            project.ChecklistItems.Add(new TaskRecord { Text = "D", IsCompleted = false });

            var progress = ProjectProgressCalculator.Calculate(project);
            if (progress.Completed != 2 || progress.Total != 4 || progress.Percent != 50d || progress.Label != "2/4")
                throw new Exception("Project progress formula drifted from completed/total.");
            if (project.Progress != "2/4")
                throw new Exception("ProjectRecord.Progress does not use canonical formula.");
            if (project.Next != project.ChecklistItems[1])
                throw new Exception("Next project task is not first incomplete task.");
        });

        test("Project progress query is read-only and not a persisted AI percentage", () =>
        {
            var project = new ProjectRecord { Name = "Deterministic" };
            project.ChecklistItems.Add(new TaskRecord { Text = "Legacy task", IsCompleted = false });
            var before = System.Text.Json.JsonSerializer.Serialize(project);

            _ = project.Progress;
            _ = project.Next;
            _ = ProjectProgressCalculator.Calculate(project);

            var after = System.Text.Json.JsonSerializer.Serialize(project);
            if (before != after)
                throw new Exception("Reading deterministic progress mutated project truth.");

            var progressProperty = typeof(ProjectRecord).GetProperty(nameof(ProjectRecord.Progress))
                ?? throw new Exception("ProjectRecord.Progress missing.");
            if (progressProperty.SetMethod is not null)
                throw new Exception("Project progress became writable.");

            foreach (var property in typeof(ProjectRecord).GetProperties())
            {
                var name = property.Name;
                if (name.Contains("AgentProgress", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("AiProgress", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("ProgressPercent", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Percent", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("ProjectRecord gained free-form progress field: " + name);
            }
        });

        test("Command Center project counts use canonical calculator while Agent state stays separate", () =>
        {
            var project = new ProjectRecord { Name = "Separate progress" };
            project.ChecklistItems.Add(new TaskRecord { Text = "One", IsCompleted = true });
            project.ChecklistItems.Add(new TaskRecord { Text = "Two", IsCompleted = false });

            var agent = new RunningAgent(project.Id);
            var service = new H2ProductProjectionService(agent);
            var overview = service.BuildProjectOverview(project);
            var deterministic = ProjectProgressCalculator.Calculate(project);

            if (overview.CompletedTasks != deterministic.Completed || overview.TotalTasks != deterministic.Total)
                throw new Exception("Command Center counts do not use deterministic project formula.");
            if (overview.AgentStatus != H2AgentTaskStatus.Running)
                throw new Exception("Agent state is not projected separately.");
            if (overview.CompletedTasks != 1 || overview.TotalTasks != 2)
                throw new Exception("Agent run changed project task completion.");
        });

        test("Current H2 project UI sources delegate project percent to canonical calculator", () =>
        {
            var repo = FindRepoRoot();
            var model = File.ReadAllText(Path.Combine(repo, "src", "H2Notes.Core", "SheetModels.cs"));
            var projections = File.ReadAllText(Path.Combine(repo, "src", "H2Notes.Core", "H2ProductProjections.cs"));
            var responsive = File.ReadAllText(Path.Combine(repo, "src", "H2Notes.Avalonia", "MainWindow.Responsive.cs"));

            if (!model.Contains("ProjectProgressCalculator.Calculate(this).Label", StringComparison.Ordinal))
                throw new Exception("ProjectRecord.Progress bypasses canonical calculator.");
            if (!projections.Contains("ProjectProgressCalculator.Calculate(project)", StringComparison.Ordinal))
                throw new Exception("Product projection bypasses canonical calculator.");
            if (!responsive.Contains("ProjectProgressCalculator.Calculate(Project).Percent", StringComparison.Ordinal))
                throw new Exception("Navigator percent bypasses canonical calculator.");

            foreach (var source in new[] { model, projections, responsive })
                if (source.Contains("AgentProgressPercent", StringComparison.Ordinal)
                    || source.Contains("AIProgressPercent", StringComparison.Ordinal)
                    || source.Contains("AiProgressPercent", StringComparison.Ordinal))
                    throw new Exception("Free-form AI project percentage marker found.");
        });

        test("Agent execution progress DTO cannot write project completion", () =>
        {
            var project = new ProjectRecord();
            project.ChecklistItems.Add(new TaskRecord { Text = "User task", IsCompleted = false });
            var before = ProjectProgressCalculator.Calculate(project);

            var agentProgress = new H2AgentProgress(
                Sequence: 99,
                AtUtc: DateTime.UtcNow,
                Kind: "verification",
                Code: "criteria-progress",
                Message: "4/6 criteria passed");

            if (agentProgress.Sequence != 99) throw new Exception("Fixture failure.");
            var after = ProjectProgressCalculator.Calculate(project);
            if (after != before || after.Completed != 0 || after.Total != 1)
                throw new Exception("Agent execution progress altered project task progress.");
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

    private sealed class RunningAgent : IH2AgentAdapter
    {
        private readonly Guid _projectId;
        private readonly H2AgentTaskSummary _task;

        public RunningAgent(Guid projectId)
        {
            _projectId = projectId;
            var now = DateTime.UtcNow;
            _task = new H2AgentTaskSummary(
                Guid.NewGuid(),
                projectId,
                "Agent fixture",
                H2AgentTaskStatus.Running,
                null,
                Array.Empty<H2AgentEvidence>(),
                null,
                null,
                now,
                now);
        }

        public Task<Guid> StartTaskAsync(Guid? projectId, string goal, H2AgentTaskContext? context = null, bool readOnly = true, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public H2AgentTaskObservation ObserveTask(Guid taskId, long afterSequence = -1)
            => new(_task, Array.Empty<H2AgentProgress>());

        public void CancelTask(Guid taskId) => throw new NotSupportedException();

        public bool RespondToApproval(Guid taskId, Guid approvalId, bool approved)
            => throw new NotSupportedException();

        public H2AgentTaskSummary GetTaskSummary(Guid taskId) => _task;

        public IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(Guid? projectId = null, int limit = 50)
            => projectId is null || projectId == _projectId ? [_task] : Array.Empty<H2AgentTaskSummary>();

        public H2AgentEvidence? GetEvidence(string evidenceId) => null;

        public bool AttachProject(Guid taskId, Guid projectId) => false;
    }
}

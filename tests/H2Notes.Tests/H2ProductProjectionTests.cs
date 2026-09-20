using System.Text.Json;
using H2Notes.Core;

internal static class H2ProductProjectionTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Project overview is rebuilt from ProjectRecord AgentAdapter and workspace health", () =>
        {
            var now = DateTime.UtcNow;
            var project = Project(now);
            var agent = new ProjectionAgentFake();
            agent.Add(Task(project.Id, H2AgentTaskStatus.Completed, now.AddMinutes(4),
                evidence: [new H2AgentEvidence("ev-ok", "verification", new string('a', 64), "verified output")]));
            agent.Add(Task(project.Id, H2AgentTaskStatus.WaitingForApproval, now.AddMinutes(5),
                approval: new H2AgentApproval(Guid.NewGuid(), "Approve change", "fixture", now.AddMinutes(5))));

            var service = new H2ProductProjectionService(agent);
            var health = new H2WorkspaceHealthSnapshot(
                H2WorkspaceSyncState.PendingLocal,
                "pending-local",
                "Local changes await NAS.",
                now.AddMinutes(6),
                HasPendingChanges: true);

            var overview = service.BuildProjectOverview(project, health);
            if (overview.ProjectId != project.Id || overview.Name != "Projection project")
                throw new Exception("Project identity/name projection mismatch.");
            if (overview.CompletedTasks != 1 || overview.TotalTasks != 2)
                throw new Exception("Deterministic project completion is wrong.");
            if (overview.NextTask != "Second task" || overview.NextTaskId != project.ChecklistItems[1].Id)
                throw new Exception("Deterministic next task is wrong.");
            if (overview.AgentStatus != H2AgentTaskStatus.WaitingForApproval)
                throw new Exception("Latest Agent status not projected.");
            if (overview.AttentionCount != 2)
                throw new Exception("Expected Agent approval + workspace pending attention.");
            if (overview.LatestVerifiedActivityUtc != now.AddMinutes(4))
                throw new Exception("Latest verified activity must ignore unverified waiting task.");
            if (overview.SyncState != H2WorkspaceSyncState.PendingLocal)
                throw new Exception("Workspace health projection lost.");
        });

        test("Needs attention derives only actionable Agent states plus one workspace item", () =>
        {
            var now = DateTime.UtcNow;
            var a = Project(now);
            var b = Project(now.AddHours(1));
            b.NameRich = RichDocument.Plain("Second project");

            var agent = new ProjectionAgentFake();
            agent.Add(Task(a.Id, H2AgentTaskStatus.Running, now.AddMinutes(1)));
            agent.Add(Task(a.Id, H2AgentTaskStatus.Blocked, now.AddMinutes(2), error: "Need a required file."));
            agent.Add(Task(b.Id, H2AgentTaskStatus.Failed, now.AddMinutes(3), error: "Verification failed."));
            agent.Add(Task(b.Id, H2AgentTaskStatus.Completed, now.AddMinutes(4)));

            var service = new H2ProductProjectionService(agent);
            var attention = service.BuildNeedsAttention(
                [a, b],
                new H2WorkspaceHealthSnapshot(
                    H2WorkspaceSyncState.Warning,
                    "sync-warning",
                    "Workspace sync warning.",
                    now.AddMinutes(5)));

            if (attention.Count != 3)
                throw new Exception("Expected blocked + failed + one global workspace attention item.");
            if (attention.Count(x => x.Code == "agent-blocked") != 1
                || attention.Count(x => x.Code == "agent-failed") != 1
                || attention.Count(x => x.Code == "sync-warning") != 1)
                throw new Exception("Attention sources are incorrect.");
            if (attention.Count(x => x.Kind == "workspace" && x.ProjectId is null) != 1)
                throw new Exception("Workspace health must be one global attention item.");
            if (attention.Any(x => x.Code.Contains("running", StringComparison.OrdinalIgnoreCase)
                || x.Code.Contains("completed", StringComparison.OrdinalIgnoreCase)))
                throw new Exception("Non-actionable Agent states leaked into attention.");
        });

        test("Project activity is chronological source projection with bounded Agent evidence", () =>
        {
            var now = DateTime.UtcNow;
            var project = Project(now);
            var agent = new ProjectionAgentFake();
            var run = Task(
                project.Id,
                H2AgentTaskStatus.Completed,
                now.AddMinutes(10),
                finalText: "Finished inspection.",
                evidence:
                [
                    new H2AgentEvidence("ev-1", "file", new string('b', 64), "report.pdf verified"),
                    new H2AgentEvidence("ev-2", "hash", new string('c', 64), null)
                ]);
            agent.Add(run);

            var service = new H2ProductProjectionService(agent);
            var activity = service.BuildProjectActivity(project, limit: 20);

            if (activity.Count == 0 || activity[0].AtUtc != now.AddMinutes(10))
                throw new Exception("Activity is not newest-first.");
            if (!activity.Any(x => x.Kind == "agent-task" && x.AgentTaskId == run.TaskId))
                throw new Exception("Agent task activity missing.");
            if (activity.Count(x => x.Kind == "agent-evidence" && x.AgentTaskId == run.TaskId) != 2)
                throw new Exception("Agent evidence activity missing.");
            if (!activity.Any(x => x.Kind == "project-task" && x.Summary.Contains("Task completed", StringComparison.Ordinal)))
                throw new Exception("Project task activity missing.");
            if (activity.Any(x => x.Summary.Length > 300))
                throw new Exception("Projection activity summary is not bounded.");
        });

        test("Product projections are disposable views and rebuild without mutating project truth", () =>
        {
            var now = DateTime.UtcNow;
            var project = Project(now);
            var before = JsonSerializer.Serialize(project);
            var agent = new ProjectionAgentFake();
            agent.Add(Task(project.Id, H2AgentTaskStatus.Completed, now.AddMinutes(3),
                evidence: [new H2AgentEvidence("ev", "fixture", new string('d', 64), "evidence")]));

            var firstService = new H2ProductProjectionService(agent);
            var firstOverview = firstService.BuildProjectOverview(project);
            var firstAttention = firstService.BuildNeedsAttention([project]);
            var firstActivity = firstService.BuildProjectActivity(project);

            // "Delete the projection cache": there is no durable cache. A fresh service instance
            // rebuilds from the same authoritative sources.
            firstService = null!;
            var rebuiltService = new H2ProductProjectionService(agent);
            var secondOverview = rebuiltService.BuildProjectOverview(project);
            var secondAttention = rebuiltService.BuildNeedsAttention([project]);
            var secondActivity = rebuiltService.BuildProjectActivity(project);

            if (firstOverview != secondOverview)
                throw new Exception("Overview did not rebuild deterministically.");
            if (JsonSerializer.Serialize(firstAttention) != JsonSerializer.Serialize(secondAttention))
                throw new Exception("Attention did not rebuild deterministically.");
            if (JsonSerializer.Serialize(firstActivity) != JsonSerializer.Serialize(secondActivity))
                throw new Exception("Activity did not rebuild deterministically.");
            if (before != JsonSerializer.Serialize(project))
                throw new Exception("Projection queries mutated ProjectRecord truth.");

            var fields = typeof(H2ProductProjectionService)
                .GetFields(System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public);
            if (fields.Length != 1 || fields[0].FieldType != typeof(IH2AgentAdapter))
                throw new Exception("Projection service gained hidden durable/cache state.");
        });

        test("Product projection source has no database file or persistence path", () =>
        {
            var repo = FindRepoRoot();
            var source = File.ReadAllText(Path.Combine(repo, "src", "H2Notes.Core", "H2ProductProjections.cs"));
            foreach (var marker in new[]
            {
                "File.Write", "FileStream", "Sqlite", "Database", "ProjectWorkspaceStore.Save",
                "JsonSerializer.SerializeToUtf8Bytes", "AtomicWrite("
            })
                if (source.Contains(marker, StringComparison.Ordinal))
                    throw new Exception("Projection layer contains persistence marker: " + marker);
        });
    }

    private static ProjectRecord Project(DateTime now)
    {
        var project = new ProjectRecord
        {
            NameRich = RichDocument.Plain("Projection project"),
            CreatedAtUtc = now,
            UpdatedAtUtc = now.AddMinutes(1)
        };
        project.ChecklistItems.Add(new TaskRecord
        {
            Text = "First task",
            IsCompleted = true,
            CreatedAtUtc = now.AddSeconds(10),
            UpdatedAtUtc = now.AddMinutes(1),
            CompletedAtUtc = now.AddMinutes(2)
        });
        project.ChecklistItems.Add(new TaskRecord
        {
            Text = "Second task",
            CreatedAtUtc = now.AddSeconds(20),
            UpdatedAtUtc = now.AddMinutes(1)
        });
        return project;
    }

    private static H2AgentTaskSummary Task(
        Guid? projectId,
        H2AgentTaskStatus status,
        DateTime updated,
        H2AgentApproval? approval = null,
        IReadOnlyList<H2AgentEvidence>? evidence = null,
        string? finalText = null,
        string? error = null)
        => new(
            Guid.NewGuid(),
            projectId,
            "fixture goal",
            status,
            approval,
            evidence ?? Array.Empty<H2AgentEvidence>(),
            finalText,
            error,
            updated.AddMinutes(-1),
            updated);

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

    private sealed class ProjectionAgentFake : IH2AgentAdapter
    {
        private readonly List<H2AgentTaskSummary> _tasks = [];

        public void Add(H2AgentTaskSummary task) => _tasks.Add(task);

        public Task<Guid> StartTaskAsync(
            Guid? projectId,
            string goal,
            H2AgentTaskContext? context = null,
            bool readOnly = true,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public H2AgentTaskObservation ObserveTask(Guid taskId, long afterSequence = -1)
            => new(GetTaskSummary(taskId), Array.Empty<H2AgentProgress>());

        public void CancelTask(Guid taskId) => throw new NotSupportedException();

        public bool RespondToApproval(Guid taskId, Guid approvalId, bool approved)
            => throw new NotSupportedException();

        public H2AgentTaskSummary GetTaskSummary(Guid taskId)
            => _tasks.Single(task => task.TaskId == taskId);

        public IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(Guid? projectId = null, int limit = 50)
            => _tasks
                .Where(task => projectId is null || task.ProjectId == projectId)
                // Deliberately unsorted: projection layer must not trust adapter ordering.
                .Take(limit)
                .ToArray();

        public H2AgentEvidence? GetEvidence(string evidenceId)
            => _tasks.SelectMany(task => task.Evidence)
                .FirstOrDefault(evidence => evidence.EvidenceId == evidenceId);

        public bool AttachProject(Guid taskId, Guid projectId)
            => throw new NotSupportedException();
    }
}

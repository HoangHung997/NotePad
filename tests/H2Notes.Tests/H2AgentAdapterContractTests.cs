using H2Notes.Core;

internal static class H2AgentAdapterContractTests
{
    public static void Run(Action<string, Action> test)
    {
        test("H2AgentAdapter boundary is provider-neutral and fake-testable", () =>
        {
            var forbidden = new[]
            {
                "AiProfile", "IAgentTransport", "AgentRuntime", "AgentOrchestrator",
                "AgentTools", "ToolRegistry", "HttpRequestMessage", "HttpResponseMessage"
            };

            foreach (var method in typeof(IH2AgentAdapter).GetMethods())
            {
                var signature = method.ReturnType.FullName + " "
                    + string.Join(" ", method.GetParameters().Select(p => p.ParameterType.FullName));
                foreach (var marker in forbidden)
                    if (signature.Contains(marker, StringComparison.Ordinal))
                        throw new Exception("Adapter leaked forbidden type marker: " + marker);
            }

            var fake = new FakeAdapter();
            var projectId = Guid.NewGuid();
            var task = fake.StartTaskAsync(
                projectId,
                "Inspect project state",
                new H2AgentTaskContext("C:\\workspace", "fixture", 2),
                readOnly: true).GetAwaiter().GetResult();

            var observation = fake.ObserveTask(task);
            if (observation.Summary.ProjectId != projectId) throw new Exception("Project correlation lost.");
            if (!observation.Progress.Any(p => p.Code == "started")) throw new Exception("Progress projection missing.");
            if (fake.GetRecentTasks(projectId).Single().TaskId != task) throw new Exception("Recent task query failed.");
            if (fake.GetEvidence("evidence:1")?.Sha256 != new string('a', 64)) throw new Exception("Evidence lookup failed.");
        });

        test("H2AgentAdapter accepts unscoped quick work and later project attachment", () =>
        {
            var fake = new FakeAdapter();
            var task = fake.StartTaskAsync(
                null,
                "Quick desktop task",
                context: null,
                readOnly: false).GetAwaiter().GetResult();

            if (fake.GetTaskSummary(task).ProjectId is not null) throw new Exception("Quick task unexpectedly has a project.");
            if (fake.GetRecentTasks(projectId: null).All(t => t.TaskId != task)) throw new Exception("Unscoped recent query omitted task.");

            var projectId = Guid.NewGuid();
            if (!fake.AttachProject(task, projectId)) throw new Exception("AttachProject failed.");
            if (fake.GetTaskSummary(task).ProjectId != projectId) throw new Exception("Attached ProjectId not reflected.");
        });

        test("H2AgentAdapter exposes approval and cancellation without Agent internals", () =>
        {
            var fake = new FakeAdapter();
            var task = fake.StartTaskAsync(Guid.NewGuid(), "Mutating task", readOnly: false).GetAwaiter().GetResult();
            var approvalId = fake.RequireApproval(task);
            var waiting = fake.GetTaskSummary(task);
            if (waiting.Status != H2AgentTaskStatus.WaitingForApproval || waiting.PendingApproval?.ApprovalId != approvalId)
                throw new Exception("Approval state not projected.");

            if (!fake.RespondToApproval(task, approvalId, approved: true))
                throw new Exception("Valid approval rejected.");
            if (fake.RespondToApproval(task, approvalId, approved: true))
                throw new Exception("Stale approval accepted.");

            fake.CancelTask(task);
            if (fake.GetTaskSummary(task).Status != H2AgentTaskStatus.Cancelled)
                throw new Exception("Cancellation not projected.");
        });

        test("H2AgentAdapter source does not reference runtime transport or ToolRegistry", () =>
        {
            var repo = FindRepoRoot();
            var source = File.ReadAllText(Path.Combine(repo, "src", "H2Notes.Core", "H2AgentAdapter.cs"));
            foreach (var marker in new[]
            {
                "AiProfile", "IAgentTransport", "AgentRuntime", "AgentOrchestrator",
                "AgentTools", "ToolRegistry", "ChatCompletions", "OpenAiResponses"
            })
                if (source.Contains(marker, StringComparison.Ordinal))
                    throw new Exception("H2 adapter contract contains forbidden implementation marker: " + marker);
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

    private sealed class FakeAdapter : IH2AgentAdapter
    {
        private readonly Dictionary<Guid, MutableTask> _tasks = [];
        private readonly Dictionary<string, H2AgentEvidence> _evidence = new(StringComparer.Ordinal);

        public Task<Guid> StartTaskAsync(
            Guid? projectId,
            string goal,
            H2AgentTaskContext? context = null,
            bool readOnly = true,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(goal)) throw new ArgumentException("Goal required.", nameof(goal));
            var id = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var evidence = new H2AgentEvidence("evidence:1", "fixture", new string('a', 64), "fixture evidence");
            _evidence[evidence.EvidenceId] = evidence;
            _tasks[id] = new MutableTask
            {
                TaskId = id,
                ProjectId = projectId,
                Goal = goal,
                Status = H2AgentTaskStatus.Running,
                Evidence = [evidence],
                CreatedUtc = now,
                UpdatedUtc = now,
                Progress = [new H2AgentProgress(0, now, "lifecycle", "started", "Started")]
            };
            return Task.FromResult(id);
        }

        public H2AgentTaskObservation ObserveTask(Guid taskId, long afterSequence = -1)
        {
            var task = State(taskId);
            return new(ToSummary(task), task.Progress.Where(p => p.Sequence > afterSequence).ToArray());
        }

        public void CancelTask(Guid taskId)
        {
            var task = State(taskId);
            task.PendingApproval = null;
            task.Status = H2AgentTaskStatus.Cancelled;
            task.UpdatedUtc = DateTime.UtcNow;
        }

        public bool RespondToApproval(Guid taskId, Guid approvalId, bool approved)
        {
            var task = State(taskId);
            if (task.PendingApproval?.ApprovalId != approvalId) return false;
            task.PendingApproval = null;
            task.Status = approved ? H2AgentTaskStatus.Running : H2AgentTaskStatus.Blocked;
            task.UpdatedUtc = DateTime.UtcNow;
            return true;
        }

        public H2AgentTaskSummary GetTaskSummary(Guid taskId) => ToSummary(State(taskId));

        public IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(Guid? projectId = null, int limit = 50)
        {
            if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
            return _tasks.Values
                .Where(t => projectId is null || t.ProjectId == projectId)
                .OrderByDescending(t => t.UpdatedUtc)
                .Take(limit)
                .Select(ToSummary)
                .ToArray();
        }

        public H2AgentEvidence? GetEvidence(string evidenceId)
            => _evidence.TryGetValue(evidenceId, out var evidence) ? evidence : null;

        public bool AttachProject(Guid taskId, Guid projectId)
        {
            if (projectId == Guid.Empty) return false;
            var task = State(taskId);
            task.ProjectId = projectId;
            task.UpdatedUtc = DateTime.UtcNow;
            return true;
        }

        public Guid RequireApproval(Guid taskId)
        {
            var task = State(taskId);
            var approval = new H2AgentApproval(Guid.NewGuid(), "Approve", "Fixture approval", DateTime.UtcNow);
            task.PendingApproval = approval;
            task.Status = H2AgentTaskStatus.WaitingForApproval;
            task.UpdatedUtc = DateTime.UtcNow;
            return approval.ApprovalId;
        }

        private MutableTask State(Guid id)
            => _tasks.TryGetValue(id, out var value) ? value : throw new KeyNotFoundException();

        private static H2AgentTaskSummary ToSummary(MutableTask task)
            => new(
                task.TaskId,
                task.ProjectId,
                task.Goal,
                task.Status,
                task.PendingApproval,
                task.Evidence,
                task.FinalText,
                task.Error,
                task.CreatedUtc,
                task.UpdatedUtc);

        private sealed class MutableTask
        {
            public Guid TaskId { get; init; }
            public Guid? ProjectId { get; set; }
            public string Goal { get; init; } = "";
            public H2AgentTaskStatus Status { get; set; }
            public H2AgentApproval? PendingApproval { get; set; }
            public List<H2AgentEvidence> Evidence { get; init; } = [];
            public List<H2AgentProgress> Progress { get; init; } = [];
            public string? FinalText { get; set; }
            public string? Error { get; set; }
            public DateTime CreatedUtc { get; init; }
            public DateTime UpdatedUtc { get; set; }
        }
    }
}

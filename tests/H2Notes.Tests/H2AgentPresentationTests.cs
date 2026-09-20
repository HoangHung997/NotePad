using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using H2Notes.Avalonia;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

internal static class H2AgentPresentationTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Project AiChatPanel sends new requests through H2AgentAdapter and never direct AiClient", () =>
        {
            var now = DateTime.UtcNow;
            var project = new ProjectRecord
            {
                Name = "Agent presentation project",
                Notes = "Current project notes",
                CreatedAtUtc = now.AddMinutes(-10),
                UpdatedAtUtc = now,
                ChecklistItems =
                [
                    new TaskRecord { Text = "Open project task", CreatedAtUtc = now.AddMinutes(-5), UpdatedAtUtc = now.AddMinutes(-4) }
                ]
            };
            var adapter = new PresentationAgentFake(project.Id, holdUntilCancel: false);
            var app = new App { AgentAdapter = adapter };
            var clientCalls = 0;
            var panel = new AiChatPanel(app, () =>
            {
                clientCalls++;
                return new AiClient();
            });
            panel.ReadContext = () => "Selected project context";
            panel.SetProject(project);

            var window = new Window { Width = 720, Height = 640, Content = panel };
            window.Show();
            Pump();
            try
            {
                var composer = panel.GetVisualDescendants().OfType<TextBox>().Single(c => c.Name == "ChatComposer");
                var send = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ChatSend");
                composer.Text = "Inspect current project and summarize next work.";
                send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                WaitUntil(() => adapter.StartCalls == 1
                    && project.Conversations.Count == 1
                    && project.Conversations[0].Messages.Count == 2
                    && project.Conversations[0].Messages[1].Status == "complete");

                Check(clientCalls == 0, "Project Agent request called legacy AiClient factory.");
                Check(adapter.StartCalls == 1, "AgentAdapter StartTaskAsync was not called exactly once.");
                Check(adapter.ProjectId == project.Id, "ProjectId was not correlated to Agent task.");
                Check(adapter.Goal == "Inspect current project and summarize next work.",
                    "Composer prompt did not reach AgentAdapter.");
                Check(adapter.Context is not null
                    && adapter.Context.Summary!.Contains("Agent presentation project", StringComparison.Ordinal)
                    && adapter.Context.Summary.Contains("Selected project context", StringComparison.Ordinal)
                    && adapter.Context.Summary.Contains("Open project task", StringComparison.Ordinal),
                    "Bounded H2AgentTaskContext did not carry project grounding.");
                Check(adapter.Context!.Summary!.Length <= 15_000, "Agent project context exceeded H2 bound.");

                var conversation = project.Conversations.Single();
                var user = conversation.Messages[0];
                var answer = conversation.Messages[1];
                Check(user.Role == "user" && answer.Role == "assistant", "Agent presentation message roles are wrong.");
                Check(user.Provider == "H2 Agent" && answer.Provider == "H2 Agent",
                    "Agent presentation was mislabeled as a legacy provider.");
                Check(user.AiRunId == adapter.TaskId && answer.AiRunId == adapter.TaskId,
                    "Agent task identity was not preserved in presentation messages.");
                Check(answer.Content == "Agent final result." && answer.Status == "complete",
                    "Agent final result was not rendered into the existing chat presentation.");
                Check(adapter.ObserveCalls >= 2, "Agent progress/lifecycle was not observed.");
            }
            finally
            {
                window.Close();
            }
        });

        test("Project AiChatPanel stop delegates cancellation to H2AgentAdapter", () =>
        {
            var project = new ProjectRecord { Name = "Cancelable Agent project", UpdatedAtUtc = DateTime.UtcNow };
            var adapter = new PresentationAgentFake(project.Id, holdUntilCancel: true);
            var app = new App { AgentAdapter = adapter };
            var clientCalls = 0;
            var panel = new AiChatPanel(app, () =>
            {
                clientCalls++;
                return new AiClient();
            });
            panel.SetProject(project);

            var window = new Window { Width = 720, Height = 640, Content = panel };
            window.Show();
            Pump();
            try
            {
                var composer = panel.GetVisualDescendants().OfType<TextBox>().Single(c => c.Name == "ChatComposer");
                var send = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ChatSend");
                composer.Text = "Wait until I stop this Agent task.";
                send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                WaitUntil(() => adapter.StartCalls == 1 && adapter.ObserveCalls > 0);
                panel.Cancel();
                WaitUntil(() => adapter.CancelCalls > 0
                    && project.Conversations.Single().Messages.Last().Status == "interrupted");

                Check(clientCalls == 0, "Cancel-path project request called legacy AiClient.");
                Check(adapter.CancelCalls >= 1, "Cancel did not reach H2AgentAdapter.CancelTask.");
                Check(project.Conversations.Single().Messages.Last().ErrorText.Contains("dừng", StringComparison.OrdinalIgnoreCase),
                    "Cancelled Agent presentation did not keep an explicit interrupted result.");
            }
            finally
            {
                window.Close();
            }
        });

        test("Project Agent presentation source does not use legacy direct execution internals", () =>
        {
            var repo = FindRepoRoot();
            var route = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "Controls", "AiChatPanel.cs"));
            var agent = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "Controls", "AiChatPanel.Agent.cs"));

            var sendStart = route.IndexOf("private async Task Send()", StringComparison.Ordinal);
            var legacyStart = route.IndexOf("private async Task SendLegacy()", StringComparison.Ordinal);
            Check(sendStart >= 0 && legacyStart > sendStart, "Project/legacy send routing was not separated.");
            var sendRoute = route[sendStart..legacyStart];
            Check(sendRoute.Contains("SendProjectAgent", StringComparison.Ordinal)
                && sendRoute.Contains("SendLegacy", StringComparison.Ordinal),
                "Send router does not explicitly split project Agent and standalone legacy execution.");

            Check(agent.Contains("_app.AgentAdapter.StartTaskAsync", StringComparison.Ordinal)
                && agent.Contains("_app.AgentAdapter.ObserveTask", StringComparison.Ordinal)
                && agent.Contains("_app.AgentAdapter.CancelTask", StringComparison.Ordinal),
                "Project Agent presentation does not use H2AgentAdapter lifecycle.");

            foreach (var forbidden in new[]
            {
                "new AiClient", "_createClient", "StreamEvents(", "SecretVault",
                "AiProjectContext", "AiProjectActions", "ApplyAutomaticProjectActions"
            })
                Check(!agent.Contains(forbidden, StringComparison.Ordinal),
                    "Project Agent presentation references legacy execution marker: " + forbidden);
        });
    }

    private static void WaitUntil(Func<bool> predicate, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            Pump();
            if (predicate()) return;
            Thread.Sleep(20);
        }
        Pump();
        if (!predicate()) throw new TimeoutException("Timed out waiting for Agent presentation state.");
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
            Dispatcher.UIThread.RunJobs();
    }

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

    private sealed class PresentationAgentFake : IH2AgentAdapter
    {
        private readonly Guid _projectId;
        private readonly bool _holdUntilCancel;
        private bool _cancelled;
        private H2AgentTaskSummary? _summary;

        public PresentationAgentFake(Guid projectId, bool holdUntilCancel)
        {
            _projectId = projectId;
            _holdUntilCancel = holdUntilCancel;
            TaskId = Guid.NewGuid();
        }

        public Guid TaskId { get; }
        public int StartCalls { get; private set; }
        public int ObserveCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public Guid? ProjectId { get; private set; }
        public string? Goal { get; private set; }
        public H2AgentTaskContext? Context { get; private set; }

        public Task<Guid> StartTaskAsync(
            Guid? projectId,
            string goal,
            H2AgentTaskContext? context = null,
            bool readOnly = true,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCalls++;
            ProjectId = projectId;
            Goal = goal;
            Context = context;
            var now = DateTime.UtcNow;
            _summary = new H2AgentTaskSummary(
                TaskId,
                projectId,
                goal,
                H2AgentTaskStatus.Running,
                null,
                Array.Empty<H2AgentEvidence>(),
                null,
                null,
                now,
                now);
            return Task.FromResult(TaskId);
        }

        public H2AgentTaskObservation ObserveTask(Guid taskId, long afterSequence = -1)
        {
            if (taskId != TaskId || _summary is null) throw new KeyNotFoundException();
            ObserveCalls++;
            var now = DateTime.UtcNow;

            if (_cancelled)
            {
                _summary = _summary with
                {
                    Status = H2AgentTaskStatus.Cancelled,
                    UpdatedUtc = now,
                    FinalText = null,
                    Error = null
                };
                return new(
                    _summary,
                    [new H2AgentProgress(ObserveCalls, now, "lifecycle", "cancelled", "Agent cancelled.")]);
            }

            if (!_holdUntilCancel && ObserveCalls >= 2)
            {
                _summary = _summary with
                {
                    Status = H2AgentTaskStatus.Completed,
                    UpdatedUtc = now,
                    FinalText = "Agent final result.",
                    Evidence =
                    [
                        new H2AgentEvidence(
                            "agent:evidence:fixture",
                            "verification",
                            new string('a', 64),
                            "Verified fixture evidence.")
                    ]
                };
                return new(
                    _summary,
                    [new H2AgentProgress(ObserveCalls, now, "final", "completed", "Agent completed.")]);
            }

            return new(
                _summary,
                [new H2AgentProgress(ObserveCalls, now, "work", "fixture-progress", "Agent is inspecting the project.")]);
        }

        public void CancelTask(Guid taskId)
        {
            if (taskId != TaskId) throw new KeyNotFoundException();
            CancelCalls++;
            _cancelled = true;
        }

        public bool RespondToApproval(Guid taskId, Guid approvalId, bool approved) => false;

        public H2AgentTaskSummary GetTaskSummary(Guid taskId)
            => taskId == TaskId && _summary is not null ? _summary : throw new KeyNotFoundException();

        public IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(Guid? projectId = null, int limit = 50)
            => _summary is not null && (projectId is null || projectId == _summary.ProjectId)
                ? [_summary]
                : Array.Empty<H2AgentTaskSummary>();

        public H2AgentEvidence? GetEvidence(string evidenceId)
            => _summary?.Evidence.FirstOrDefault(item => item.EvidenceId == evidenceId);

        public bool AttachProject(Guid taskId, Guid projectId) => false;
    }
}

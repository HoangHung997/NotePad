using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using H2Notes.Avalonia;
using H2Notes.Core;

internal static class H2ProjectHistoryTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Project History combines meaningful project Agent evidence and sync events without raw trace", () =>
        {
            var now = DateTime.UtcNow;
            var project = new ProjectRecord
            {
                Name = "History project",
                CreatedAtUtc = now,
                UpdatedAtUtc = now.AddMinutes(2),
                ChecklistItems =
                [
                    new TaskRecord
                    {
                        Text = "Completed work",
                        CreatedAtUtc = now.AddSeconds(20),
                        UpdatedAtUtc = now.AddMinutes(1),
                        CompletedAtUtc = now.AddMinutes(1),
                        IsCompleted = true
                    },
                    new TaskRecord
                    {
                        Text = "Edited work",
                        CreatedAtUtc = now.AddSeconds(30),
                        UpdatedAtUtc = now.AddMinutes(3)
                    }
                ]
            };

            var agent = new HistoryAgentFake(project.Id, now.AddMinutes(5));
            var service = new H2ProductProjectionService(agent);
            var before = JsonSerializer.Serialize(project);
            var health = new H2WorkspaceHealthSnapshot(
                H2WorkspaceSyncState.Warning,
                "sync-warning",
                "NAS generation needed a retry.",
                now.AddMinutes(6));

            var history = service.BuildProjectHistory(project, health, 100);

            Check(history.Any(item => item.Kind == "project-created"), "Project create event missing.");
            Check(history.Any(item => item.Kind == "project-edited"), "Project edit event missing.");
            Check(history.Any(item => item.Kind == "project-task-created"), "Task create event missing.");
            Check(history.Any(item => item.Kind == "project-task-completed"), "Task completion event missing.");
            Check(history.Any(item => item.Kind == "project-task-updated"), "Task update event missing.");
            Check(history.Count(item => item.Kind == "agent-lifecycle") == 1,
                "History must contain one lifecycle row per Agent run, not low-level trace.");
            Check(history.Count(item => item.Kind == "verified-mutation") == 1,
                "Verified mutation evidence was not classified.");
            Check(history.Count(item => item.Kind == "agent-evidence") == 1,
                "Meaningful non-mutation evidence missing.");
            Check(history.Count(item => item.Kind == "sync") == 1,
                "Meaningful storage/sync event missing.");

            Check(history.All(item => !item.Summary.Contains("RAW_PROGRESS_SHOULD_NOT_COPY", StringComparison.Ordinal)),
                "Raw Agent progress leaked into H2 project history.");
            Check(history.All(item => item.Kind != "agent-progress" && item.Kind != "tool-trace"),
                "Low-level trace kind leaked into H2 project history.");

            var inspection = agent.ObserveTask(agent.TaskId);
            Check(inspection.Progress.Any(progress => progress.Message == "RAW_PROGRESS_SHOULD_NOT_COPY"),
                "Detailed trace is no longer available through Agent inspection.");
            Check(history.Any(item => item.AgentTaskId == agent.TaskId),
                "History did not retain AgentTaskId for trace correlation.");
            Check(history.Any(item => item.EvidenceId == "ev-mutation")
                && history.Any(item => item.EvidenceId == "ev-web"),
                "History lost evidence identity.");
            Check(before == JsonSerializer.Serialize(project),
                "History projection mutated ProjectRecord truth.");
        });

        test("Project History detail opens in one click and remains a projection-only UI", () =>
        {
            var now = DateTime.UtcNow;
            var project = new ProjectRecord
            {
                Name = "History UI project",
                CreatedAtUtc = now,
                UpdatedAtUtc = now.AddMinutes(1),
                ChecklistItems =
                [
                    new TaskRecord
                    {
                        Text = "UI task",
                        CreatedAtUtc = now.AddSeconds(10),
                        UpdatedAtUtc = now.AddMinutes(1)
                    }
                ]
            };
            var board = new NoteRecord
            {
                Title = "History UI board",
                NoteKind = "project-hub",
                Projects = [project]
            };
            var agent = new HistoryAgentFake(project.Id, now.AddMinutes(2));
            var app = new App { AgentAdapter = agent };
            app.State.Notes.Add(board);

            var window = new MainWindow(app, board) { Width = 1040, Height = 760 };
            window.Show();
            Pump();
            try
            {
                H2UiTestNavigation.OpenProjectWorkspace(window, project.Id);
                Pump();

                window.FindControl<Button>("HistoryTabButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();

                var historyPane = window.FindControl<Border>("ProjectHistoryPane")!;
                var list = window.FindControl<ListBox>("ProjectHistoryList")!;
                Check(historyPane.IsVisible, "Project History pane did not open.");
                Check(!window.FindControl<Grid>("EditorSplit")!.IsVisible,
                    "Task/Notes detail remained over Project History.");
                Check(!window.FindControl<Border>("ProjectResourcesPane")!.IsVisible,
                    "Resources detail remained over Project History.");

                var items = list.ItemsSource!.Cast<object>().ToArray();
                Check(items.Length >= 4, "Project History did not render meaningful timeline rows.");
                Check(items.Any(item => Text(item, "KindText") == "Agent"),
                    "Agent lifecycle is not visible in Project History.");
                Check(items.Any(item => Text(item, "KindText") == "Đã xác minh"),
                    "Verified mutation is not visible in Project History.");
                Check(items.Any(item => (Guid?)item.GetType().GetProperty("AgentTaskId")!.GetValue(item)==agent.TaskId),
                    "History UI lost Agent task correlation.");

                window.FindControl<Button>("AgentTabButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                Check(window.FindControl<Border>("AiHostBorder")!.IsVisible
                    && !window.FindControl<Border>("ProjectHistoryPane")!.IsVisible,
                    "History → Agent one-click navigation failed.");
            }
            finally
            {
                window.Close();
            }
        });

        test("Project History implementation has no duplicate history or trace store", () =>
        {
            var repo = FindRepoRoot();
            var core = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Core", "H2ProductProjections.cs"));
            var ui = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "MainWindow.History.cs"));

            foreach (var forbidden in new[]
            {
                "HistoryStore", "HistoryDatabase", "ProjectHistoryStore",
                "TraceDatabase", "TraceJournal", "File.Write", "AtomicWrite("
            })
                Check(!ui.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    "Project History UI contains duplicate persistence marker: " + forbidden);

            Check(core.Contains("BuildProjectHistory", StringComparison.Ordinal)
                && core.Contains("AgentActivitySummary", StringComparison.Ordinal)
                && core.Contains("verified-mutation", StringComparison.Ordinal)
                && core.Contains("\"sync\"", StringComparison.Ordinal),
                "History projection is missing required real-source event classes.");

            Check(!ui.Contains("H2AgentProgress", StringComparison.Ordinal)
                && ui.Contains("AgentTurnView", StringComparison.Ordinal),
                "History UI copied low-level Agent trace instead of retaining correlation.");
        });
    }

    private static string Text(object value, string property)
        => value.GetType().GetProperty(property)!.GetValue(value)?.ToString() ?? "";

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

    private sealed class HistoryAgentFake : IH2AgentAdapter
    {
        private readonly H2AgentTaskSummary _task;
        public Guid TaskId => _task.TaskId;

        public HistoryAgentFake(Guid projectId, DateTime updated)
        {
            _task = new H2AgentTaskSummary(
                Guid.NewGuid(),
                projectId,
                "Apply verified project change",
                H2AgentTaskStatus.Completed,
                null,
                [
                    new H2AgentEvidence(
                        "ev-mutation",
                        "verification-mutation",
                        new string('a', 64),
                        "Updated output was verified.", VerificationPassed: true),
                    new H2AgentEvidence(
                        "ev-web",
                        "web",
                        new string('b', 64),
                        "Official source inspected.")
                ],
                "Completed verified project change.",
                null,
                updated.AddMinutes(-2),
                updated);
        }

        public Task<Guid> StartTaskAsync(Guid? projectId, string goal, H2AgentTaskContext? context = null, bool readOnly = true, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public H2AgentTaskObservation ObserveTask(Guid taskId, long afterSequence = -1)
            => new(
                _task,
                [
                    new H2AgentProgress(
                        1,
                        _task.UpdatedUtc.AddSeconds(-20),
                        "tool",
                        "raw",
                        "RAW_PROGRESS_SHOULD_NOT_COPY")
                ]);

        public void CancelTask(Guid taskId) => throw new NotSupportedException();
        public bool RespondToApproval(Guid taskId, Guid approvalId, bool approved) => false;
        public H2AgentTaskSummary GetTaskSummary(Guid taskId) => _task;
        public IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(Guid? projectId = null, int limit = 50)
            => projectId is null || projectId == _task.ProjectId ? [_task] : Array.Empty<H2AgentTaskSummary>();
        public H2AgentEvidence? GetEvidence(string evidenceId)
            => _task.Evidence.FirstOrDefault(item => item.EvidenceId == evidenceId);
        public bool AttachProject(Guid taskId, Guid projectId) => false;
    }
}

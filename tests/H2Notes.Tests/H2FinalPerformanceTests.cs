using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using H2Notes.Avalonia;
using H2Notes.Core;

internal static class H2FinalPerformanceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(Action<string, Action> test)
    {
        test("H2M-132 measures final product performance/resource proxies on Windows CI", () =>
        {
            var demo = SheetStorage.Demo();
            var board = demo.Notes.First(note => note.IsBoard);
            var app = new App();
            app.State.Notes.Add(board);

            var startup = Stopwatch.StartNew();
            var window = new MainWindow(app, board)
            {
                Width = 1280,
                Height = 800
            };
            window.Show();
            Pump();
            startup.Stop();

            try
            {
                Check(startup.Elapsed < TimeSpan.FromSeconds(20),
                    "UI startup proxy exceeded the broad regression budget.");
                Metric("startup_proxy_ui_ms", startup.Elapsed.TotalMilliseconds);

                var commandCenter = Stopwatch.StartNew();
                for (var i = 0; i < 20; i++)
                {
                    window.RefreshAfterSave();
                    Pump();
                }
                commandCenter.Stop();
                Check(commandCenter.Elapsed < TimeSpan.FromSeconds(20),
                    "Command Center refresh loop exceeded the broad regression budget.");
                Metric("command_center_refresh_20_total_ms", commandCenter.Elapsed.TotalMilliseconds);

                var switchWatch = Stopwatch.StartNew();
                for (var i = 0; i < 25; i++)
                {
                    var project = board.Projects[i % board.Projects.Count];
                    Check(window.OpenProjectWorkspace(project.Id),
                        "Project switch benchmark could not open project.");
                    Pump();
                }
                switchWatch.Stop();
                Check(switchWatch.Elapsed < TimeSpan.FromSeconds(20),
                    "Project switch loop exceeded the broad regression budget.");
                Metric("project_switch_25_total_ms", switchWatch.Elapsed.TotalMilliseconds);
            }
            finally
            {
                window.Close();
            }

            MeasureAgentProgress();
            MeasureBubbleIdle();
            MeasureWorkspaceSave();
            MeasureLargeProjectLayout();
        });
    }

    private static void MeasureAgentProgress()
    {
        var now = DateTime.UtcNow;
        var taskId = Guid.NewGuid();
        var adapter = new PerfAgent(taskId, now);
        var app = new App { AgentAdapter = adapter };
        app.LocalSettings.WorkAssistant.Enabled = true;

        typeof(App).GetField("_workAssistantQuickTaskId", Private)!.SetValue(app, taskId);
        typeof(App).GetField("_workAssistantLastGoal", Private)!.SetValue(app, "Performance progress fixture");
        var refresh = typeof(App).GetMethod("RefreshWorkAssistantTaskState", Private)
            ?? throw new MissingMethodException("RefreshWorkAssistantTaskState");

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
        {
            adapter.Sequence = i;
            refresh.Invoke(app, null);
            Pump();
        }
        watch.Stop();

        CloseAssistantWindows(app);
        Check(watch.Elapsed < TimeSpan.FromSeconds(20),
            "Agent progress presentation loop exceeded the broad regression budget.");
        Metric("agent_progress_refresh_100_total_ms", watch.Elapsed.TotalMilliseconds);
    }

    private static void MeasureBubbleIdle()
    {
        var app = new App();
        app.LocalSettings.WorkAssistant.Enabled = true;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalMemory(forceFullCollection: true);

        app.ShowWorkAssistantBubble();
        Pump();

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 250; i++)
            Pump();
        watch.Stop();

        var after = GC.GetTotalMemory(forceFullCollection: false);
        CloseAssistantWindows(app);

        Check(watch.Elapsed < TimeSpan.FromSeconds(20),
            "Idle Work Assistant dispatcher loop exceeded the broad regression budget.");
        Check(after - before < 512L * 1024 * 1024,
            "Idle Work Assistant managed-memory delta exceeded 512 MB.");
        Metric("background_bubble_idle_250_pumps_ms", watch.Elapsed.TotalMilliseconds);
        Console.WriteLine($"H2M-132 METRIC background_bubble_managed_delta_bytes={after - before}");
    }

    private static void MeasureWorkspaceSave()
    {
        var root = Temp("save");
        try
        {
            var board = LargeBoard(projects: 250, tasksPerProject: 5);
            var state = new SheetState { Notes = [board] };
            var store = new ProjectWorkspaceStore(
                root,
                writerId: "h2m132",
                recoveryRoot: Path.Combine(root, ".recovery-local"),
                pendingRoot: Path.Combine(root, ".pending-local"));

            var watch = Stopwatch.StartNew();
            store.Save(state);
            watch.Stop();

            Check(watch.Elapsed < TimeSpan.FromSeconds(30),
                "Workspace save exceeded the broad regression budget.");
            Metric("workspace_save_250_projects_1250_tasks_ms", watch.Elapsed.TotalMilliseconds);
        }
        finally
        {
            Delete(TempRoot(root));
        }
    }

    private static void MeasureLargeProjectLayout()
    {
        var board = LargeBoard(projects: 1000, tasksPerProject: 10);
        var sheet = new ProjectGrid();
        var window = new Window
        {
            Width = 1000,
            Height = 600,
            Content = sheet
        };
        window.Show();
        Pump();

        try
        {
            var watch = Stopwatch.StartNew();
            sheet.SetBoard(board);
            Pump();
            watch.Stop();

            var visualCount = sheet.GetVisualDescendants().Count();
            Check(watch.Elapsed < TimeSpan.FromSeconds(30),
                "Large project first layout exceeded the broad regression budget.");
            Check(visualCount < 100,
                "Large project UI lost bounded/virtualized visual count.");

            Metric("large_1000_projects_10000_tasks_layout_ms", watch.Elapsed.TotalMilliseconds);
            Console.WriteLine($"H2M-132 METRIC large_1000_projects_10000_tasks_visual_count={visualCount}");
        }
        finally
        {
            window.Close();
        }
    }

    private static NoteRecord LargeBoard(int projects, int tasksPerProject)
    {
        var board = new NoteRecord
        {
            Title = "Performance board",
            NoteKind = "project-hub"
        };

        for (var i = 0; i < projects; i++)
        {
            var project = new ProjectRecord
            {
                Name = "Project " + i,
                ChecklistItems = Enumerable.Range(0, tasksPerProject)
                    .Select(task => new TaskRecord
                    {
                        Text = "Task " + task,
                        IsCompleted = task % 3 == 0
                    })
                    .ToList()
            };
            board.Projects.Add(project);
        }

        return board;
    }

    private static void Metric(string name, double milliseconds)
        => Console.WriteLine($"H2M-132 METRIC {name}={milliseconds:F2}");

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
            Dispatcher.UIThread.RunJobs();
    }

    private static void CloseAssistantWindows(App app)
    {
        try
        {
            ((Window?)typeof(App).GetField("_workAssistantCompact", Private)?.GetValue(app))?.Close();
            ((Window?)typeof(App).GetField("_workAssistantBubble", Private)?.GetValue(app))?.Close();
        }
        catch
        {
        }
    }

    private static string Temp(string name)
        => Path.Combine(
            Path.GetTempPath(),
            "h2-final-perf-" + name + "-" + Guid.NewGuid().ToString("N"));

    private static string TempRoot(string path)
        => path;

    private static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private sealed class PerfAgent : IH2AgentAdapter
    {
        private readonly Guid _taskId;
        private readonly DateTime _created;

        public PerfAgent(Guid taskId, DateTime created)
        {
            _taskId = taskId;
            _created = created;
        }

        public int Sequence { get; set; }

        private H2AgentTaskSummary Summary => new(
            _taskId,
            ProjectId: null,
            Goal: "Performance progress fixture",
            Status: H2AgentTaskStatus.Running,
            PendingApproval: null,
            Evidence: Array.Empty<H2AgentEvidence>(),
            FinalText: null,
            Error: null,
            CreatedUtc: _created,
            UpdatedUtc: _created.AddMilliseconds(Sequence));

        public Task<Guid> StartTaskAsync(
            Guid? projectId,
            string goal,
            H2AgentTaskContext? context = null,
            bool readOnly = true,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_taskId);

        public H2AgentTaskObservation ObserveTask(Guid taskId, long afterSequence = -1)
            => new(
                Summary,
                [new H2AgentProgress(
                    Sequence,
                    _created.AddMilliseconds(Sequence),
                    "progress",
                    "perf",
                    "Progress " + Sequence)]);

        public void CancelTask(Guid taskId) { }
        public bool RespondToApproval(Guid taskId, Guid approvalId, bool approved) => false;
        public H2AgentTaskSummary GetTaskSummary(Guid taskId) => Summary;
        public IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(Guid? projectId = null, int limit = 50) => [Summary];
        public H2AgentEvidence? GetEvidence(string evidenceId) => null;
        public bool AttachProject(Guid taskId, Guid projectId) => false;
    }
}

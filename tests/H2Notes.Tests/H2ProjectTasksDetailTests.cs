using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Headless;
using Avalonia.VisualTree;
using H2Notes.Avalonia;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

internal static class H2ProjectTasksDetailTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(Action<string, Action> test)
    {
        test("Project Tasks detail reuses ProjectGrid editing search completion drag comments and rich text", () =>
        {
            var now = DateTime.UtcNow;
            var first = new TaskRecord
            {
                Text = "Alpha task",
                Comment = "Alpha comment",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            var second = new TaskRecord
            {
                Text = "Needle task",
                Comment = "Needle comment",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            var third = new TaskRecord
            {
                Text = "Third task",
                Comment = "Third comment",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            first.TextRich = RichDocument.Plain(first.Text);
            first.TextRich.Format(0, 5, style => style with { Bold = true });

            var project = new ProjectRecord
            {
                Name = "Task detail project",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                ChecklistItems = [first, second, third]
            };
            var board = new NoteRecord
            {
                Title = "Task detail board",
                NoteKind = "project-hub",
                Projects = [project]
            };
            var app = new App { AgentAdapter = new AgentRunFake(project.Id, now.AddMinutes(1)) };
            app.State.Notes.Add(board);

            var window = new MainWindow(app, board) { Width = 1040, Height = 760 };
            window.Show();
            Pump();
            try
            {
                H2UiTestNavigation.OpenProjectWorkspace(window, project.Id);
                Pump();
                window.FindControl<Button>("TasksTabButton")!
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Pump();

                var sheet = window.FindControl<ProjectGrid>("Sheet")!;
                Check(sheet.TasksOnly, "ProjectGrid is not in focused-project Tasks detail mode.");
                Check(window.FindControl<Grid>("EditorSplit")!.IsVisible
                    && window.FindControl<Border>("TasksPane")!.IsVisible,
                    "Tasks detail is not visible.");

                Check(LayoutRows(sheet).Count == 3,
                    "Agent execution leaked into ProjectGrid TaskRecord rows.");
                Check(project.ChecklistItems.Count == 3
                    && project.ChecklistItems.All(task => !task.DisplayText.Contains("Agent execution", StringComparison.Ordinal)),
                    "Agent execution was persisted as a project TaskRecord.");

                // Search is the existing project search box, but in Tasks detail it now filters
                // the focused project's task title/comment rows.
                var search = window.FindControl<TextBox>("SearchBox")!;
                search.Text = "Needle comment";
                WaitSearch();
                Check(LayoutRows(sheet).Count == 1, "Task/comment search did not filter Tasks detail.");
                Check(LayoutTask(LayoutRows(sheet)[0]!).Id == second.Id,
                    "Task detail search returned the wrong TaskRecord.");

                search.Text = "";
                WaitSearch();
                Check(LayoutRows(sheet).Count == 3, "Clearing task search did not restore all project tasks.");

                // Rich task text edit remains ProjectGrid/RichEditor-owned.
                var secondRow = new SheetRow(project, second, 1);
                sheet.BeginEdit(secondRow, 1);
                Pump();
                var editor = sheet.GetVisualDescendants().OfType<RichEditor>().Single();
                editor.Editor.SelectAll();
                editor.Apply(style => style with { Bold = true, Color = "#AA3300" });
                sheet.CommitEdit();
                Check(second.ReadText().StyleAt(0).Bold
                    && second.ReadText().StyleAt(0).Color == "#AA3300",
                    "Rich task text formatting was lost in Tasks detail.");

                // Rich comment edit stays on TaskRecord.CommentRich.
                sheet.BeginEdit(secondRow, 3);
                Pump();
                editor.Editor.SelectAll();
                editor.Editor.SelectedText = "Updated needle comment";
                editor.Editor.SelectAll();
                editor.Apply(style => style with { Italic = true });
                sheet.CommitEdit();
                Check(second.CommentText == "Updated needle comment"
                    && second.ReadComment().StyleAt(0).Italic,
                    "Task comment edit/rich formatting was lost.");

                // Completion remains the ProjectGrid checkbox behavior.
                var firstLayout = LayoutRows(sheet).Cast<object>()
                    .Single(row => LayoutTask(row).Id == first.Id);
                var firstPoint = LocalPoint(firstLayout, x: 20, verticalFraction: .5);
                ClickAt(window, sheet, firstPoint);
                Check(first.IsCompleted, "ProjectGrid completion toggle stopped working in Tasks detail.");

                // Drag/drop remains task ordering; clear filter is already active.
                sheet.Refresh();
                Pump();
                var thirdLayout = LayoutRows(sheet).Cast<object>()
                    .Single(row => LayoutTask(row).Id == third.Id);
                firstLayout = LayoutRows(sheet).Cast<object>()
                    .Single(row => LayoutTask(row).Id == first.Id);
                var source = Translate(sheet, window, LocalPoint(thirdLayout, x: 120, verticalFraction: .5));
                var target = Translate(sheet, window, LocalPoint(firstLayout, x: 120, verticalFraction: .2));
                window.MouseDown(source, MouseButton.Left);
                window.MouseMove(target, RawInputModifiers.LeftMouseButton);
                window.MouseUp(target, MouseButton.Left);
                Pump();
                Check(project.ChecklistItems[0].Id == third.Id,
                    "ProjectGrid task drag/drop stopped reordering TaskRecord rows.");
            }
            finally
            {
                window.Close();
            }
        });

        test("Project Tasks detail source stays independent from Agent execution models", () =>
        {
            var repo = FindRepoRoot();
            var grid = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "Controls", "ProjectGrid.cs"));

            foreach (var forbidden in new[]
            {
                "IH2AgentAdapter", "H2AgentTaskSummary", "H2AgentProgress",
                "AgentRuntime", "ToolRegistry", "VerificationReport"
            })
                Check(!grid.Contains(forbidden, StringComparison.Ordinal),
                    "ProjectGrid gained Agent execution dependency: " + forbidden);

            foreach (var type in new[] { typeof(ProjectRecord), typeof(TaskRecord) })
                foreach (var forbidden in new[]
                {
                    "AgentTask", "AgentRun", "ToolRun", "VerificationReport",
                    "ExecutionStep", "AgentProgress"
                })
                    Check(type.GetProperty(forbidden) is null && type.GetField(forbidden) is null,
                        $"{type.Name} gained Agent execution field: {forbidden}");
        });
    }

    private static IList LayoutRows(ProjectGrid sheet)
        => (IList)typeof(ProjectGrid).GetField("_layout", Private)!.GetValue(sheet)!;

    private static TaskRecord LayoutTask(object layout)
    {
        var row = layout.GetType().GetProperty("Row")!.GetValue(layout)!;
        return (TaskRecord)row.GetType().GetProperty("Task")!.GetValue(row)!;
    }

    private static Point LocalPoint(object layout, double x, double verticalFraction)
    {
        var y = (double)layout.GetType().GetProperty("Y")!.GetValue(layout)!;
        var height = (double)layout.GetType().GetProperty("Height")!.GetValue(layout)!;
        return new Point(x, 39 + y + height * verticalFraction);
    }

    private static Point Translate(Control source, Control target, Point point)
        => source.TranslatePoint(point, target)
           ?? throw new Exception("Could not translate ProjectGrid test point.");

    private static void ClickAt(Window window, ProjectGrid sheet, Point local)
    {
        var point = Translate(sheet, window, local);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Pump();
    }

    private static void WaitSearch()
    {
        Thread.Sleep(240);
        Pump();
        Thread.Sleep(20);
        Pump();
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

    private sealed class AgentRunFake : IH2AgentAdapter
    {
        private readonly H2AgentTaskSummary _task;

        public AgentRunFake(Guid projectId, DateTime updated)
        {
            _task = new H2AgentTaskSummary(
                Guid.NewGuid(),
                projectId,
                "Agent execution step fixture",
                H2AgentTaskStatus.Running,
                null,
                Array.Empty<H2AgentEvidence>(),
                null,
                null,
                updated.AddSeconds(-10),
                updated);
        }

        public Task<Guid> StartTaskAsync(Guid? projectId, string goal, H2AgentTaskContext? context = null, bool readOnly = true, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public H2AgentTaskObservation ObserveTask(Guid taskId, long afterSequence = -1)
            => new(_task, Array.Empty<H2AgentProgress>());
        public void CancelTask(Guid taskId) => throw new NotSupportedException();
        public bool RespondToApproval(Guid taskId, Guid approvalId, bool approved) => false;
        public H2AgentTaskSummary GetTaskSummary(Guid taskId) => _task;
        public IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(Guid? projectId = null, int limit = 50)
            => projectId is null || projectId == _task.ProjectId ? [_task] : Array.Empty<H2AgentTaskSummary>();
        public H2AgentEvidence? GetEvidence(string evidenceId) => null;
        public bool AttachProject(Guid taskId, Guid projectId) => false;
    }
}

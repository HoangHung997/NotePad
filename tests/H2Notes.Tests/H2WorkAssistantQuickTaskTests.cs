using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using H2Notes.Avalonia;
using H2Notes.Core;

internal static class H2WorkAssistantQuickTaskTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(Action<string, Action> test)
    {
        test("Quick Work Assistant request starts normal unscoped Agent task and survives panel collapse", () =>
        {
            var app = new App();
            app.LocalSettings.WorkAssistant.Enabled = true;
            app.WorkAssistantContextCapture = new ContextCapture(Context());

            var project = new ProjectRecord { Name = "Link target" };
            app.State.Notes.Add(new NoteRecord
            {
                Title = "Projects",
                NoteKind = "project-hub",
                Projects = [project]
            });

            var agent = new QuickAgentFake();
            app.AgentAdapter = agent;
            var sharedBefore = JsonSerializer.Serialize(app.State);

            app.ShowWorkAssistantCompact();
            Pump();
            var compact = Compact(app);
            compact.RemoveContextScope(WorkAssistantContextScope.Selection);
            compact.PromptText = "Tóm tắt workbook hiện tại";
            Send(compact);
            Pump();

            Check(agent.StartCalls == 1,
                "Quick prompt did not call the existing Agent adapter exactly once.");
            Check(agent.StartProjectId is null,
                "Quick Work Assistant task was incorrectly scoped to an H2 project.");
            Check(agent.StartGoal == "Tóm tắt workbook hiện tại",
                "Quick task goal changed before reaching Agent adapter.");
            Check(agent.StartReadOnly,
                "H2M-085 quick task must remain read-only until H2M-086 permission mapping.");
            Check(agent.StartContext?.Summary?.Contains("Document=C:\\Projects\\DuToan.xlsx", StringComparison.Ordinal) == true,
                "Selected document context did not reach normal Agent task context.");
            Check(agent.StartContext?.Summary?.Contains("Selection=D51:F80", StringComparison.Ordinal) == false,
                "Removed selection scope leaked into Agent task context.");

            var taskId = app.CurrentWorkAssistantTaskId
                ?? throw new Exception("App did not retain quick Agent TaskId in runtime state.");
            Check(taskId == agent.TaskId
                && agent.GetTaskSummary(taskId).Status == H2AgentTaskStatus.Running,
                "Quick task stopped/disappeared when compact panel collapsed.");
            Check(!app.IsWorkAssistantCompactVisible,
                "Compact assistant did not collapse after successful quick-task start.");
            Check(app.IsWorkAssistantBubbleVisible
                && Bubble(app).State == WorkAssistantBubbleState.Working,
                "Work Assistant bubble did not show running state after panel collapse.");
            Check(sharedBefore == JsonSerializer.Serialize(app.State),
                "Starting an unscoped quick task mutated shared project truth.");

            var projectBeforeLink = JsonSerializer.Serialize(project);
            Check(app.LinkCurrentWorkAssistantTaskToProject(project.Id),
                "User could not later associate quick Agent task with a real project.");
            Check(agent.AttachCalls == 1
                && agent.AttachedTaskId == taskId
                && agent.AttachedProjectId == project.Id,
                "Project link did not route through AgentAdapter.AttachProject.");
            Check(agent.GetTaskSummary(taskId).ProjectId == project.Id,
                "Agent task association was not updated after linking.");
            Check(projectBeforeLink == JsonSerializer.Serialize(project),
                "Linking Agent task copied task state into ProjectRecord.");
            Check(!typeof(ProjectRecord).GetProperties().Any(property =>
                    property.Name.Contains("AgentTask", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Contains("QuickWork", StringComparison.OrdinalIgnoreCase)),
                "ProjectRecord gained embedded quick/Agent task state.");
        });

        test("Quick Work Assistant fails closed when selected active context is stale", () =>
        {
            var app = new App();
            app.LocalSettings.WorkAssistant.Enabled = true;
            var capture = new ContextCapture(Context()) { RevalidateResult = false };
            app.WorkAssistantContextCapture = capture;
            var agent = new QuickAgentFake();
            app.AgentAdapter = agent;

            app.ShowWorkAssistantCompact();
            Pump();
            var compact = Compact(app);
            compact.PromptText = "Đọc vùng đang chọn";
            Send(compact);
            Pump();

            Check(agent.StartCalls == 0,
                "Stale active context still started an Agent task.");
            Check(app.CurrentWorkAssistantTaskId is null,
                "Stale context created a runtime TaskId.");
            Check(app.IsWorkAssistantCompactVisible,
                "Stale-context failure hid the compact panel instead of asking user to recapture/remove scope.");
            Check(compact.CapturedContext is null
                && compact.SelectedContextScope == WorkAssistantContextScope.None,
                "Stale context was not cleared from context chips.");
            Check(capture.RevalidateCalls == 1,
                "Quick send bypassed ActiveWorkContext revalidation.");
        });

        test("Quick work uses one Agent adapter and introduces no QuickWorkSession model", () =>
        {
            var repo = FindRepoRoot();
            var appSource = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "App.axaml.cs"));
            var compactSource = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "WorkAssistantCompactWindow.cs"));

            Check(appSource.Contains("_agentAdapter.StartTaskAsync(", StringComparison.Ordinal)
                && appSource.Contains("projectId: null", StringComparison.Ordinal),
                "Quick work is not routed through the existing H2 Agent adapter with ProjectId=null.");
            Check(appSource.Contains("_agentAdapter.AttachProject(", StringComparison.Ordinal),
                "Quick work has no Agent-owned later project association path.");

            foreach (var forbidden in new[]
            {
                "QuickWorkSession",
                "new AgentRuntime",
                "new AgentOrchestrator",
                "new ToolRegistry",
                "SecondAgent",
                "QuickAgentRuntime"
            })
                Check(!appSource.Contains(forbidden, StringComparison.Ordinal)
                    && !compactSource.Contains(forbidden, StringComparison.Ordinal),
                    "Quick work introduced a second runtime/session subsystem: " + forbidden);

            Check(!compactSource.Contains("ProjectRecord", StringComparison.Ordinal)
                && !compactSource.Contains("ScheduleSave(", StringComparison.Ordinal),
                "Compact prompt became a project persistence surface.");
        });
    }

    private static H2ActiveWorkContext Context()
        => new(
            ProcessId: 4242,
            ProcessStartUtcTicks: 987654321,
            ProcessName: "excel",
            ApplicationKind: H2ApplicationKind.Excel,
            NativeWindowHandle: 0x1234,
            WindowIdentity: "win32:1234:4242:987654321",
            WindowTitle: "DuToan.xlsx - Excel",
            DocumentSessionId: "BAOCAOGS",
            DocumentPath: @"C:\Projects\DuToan.xlsx",
            Selection: "D51:F80",
            Provider: "OfficeHost",
            CapturedUtc: DateTime.UtcNow);

    private static WorkAssistantCompactWindow Compact(App app)
    {
        var field = typeof(App).GetField("_workAssistantCompact", Private)
            ?? throw new Exception("App._workAssistantCompact field missing.");
        return (WorkAssistantCompactWindow)(field.GetValue(app)
            ?? throw new Exception("Compact Work Assistant was not created."));
    }

    private static WorkAssistantBubbleWindow Bubble(App app)
    {
        var field = typeof(App).GetField("_workAssistantBubble", Private)
            ?? throw new Exception("App._workAssistantBubble field missing.");
        return (WorkAssistantBubbleWindow)(field.GetValue(app)
            ?? throw new Exception("Work Assistant bubble was not created."));
    }

    private static void Send(WorkAssistantCompactWindow compact)
    {
        var field = typeof(WorkAssistantCompactWindow).GetField("_send", Private)
            ?? throw new Exception("Work Assistant send button missing.");
        var button = (Button)(field.GetValue(compact)
            ?? throw new Exception("Work Assistant send control missing."));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void Pump()
    {
        for (var i = 0; i < 8; i++)
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

    private sealed class ContextCapture : IWorkAssistantActiveContextCapture
    {
        private readonly H2ActiveWorkContext _context;
        public ContextCapture(H2ActiveWorkContext context) => _context = context;
        public bool RevalidateResult { get; set; } = true;
        public int RevalidateCalls { get; private set; }

        public H2ActiveWorkContext? Capture() => _context;

        public bool Revalidate(H2ActiveWorkContext context)
        {
            RevalidateCalls++;
            return RevalidateResult
                && context.WindowIdentity == _context.WindowIdentity;
        }
    }

    private sealed class QuickAgentFake : IH2AgentAdapter
    {
        private H2AgentTaskSummary? _task;
        public Guid TaskId { get; private set; }
        public int StartCalls { get; private set; }
        public Guid? StartProjectId { get; private set; }
        public string? StartGoal { get; private set; }
        public H2AgentTaskContext? StartContext { get; private set; }
        public bool StartReadOnly { get; private set; }
        public int AttachCalls { get; private set; }
        public Guid? AttachedTaskId { get; private set; }
        public Guid? AttachedProjectId { get; private set; }

        public Task<Guid> StartTaskAsync(
            Guid? projectId,
            string goal,
            H2AgentTaskContext? context = null,
            bool readOnly = true,
            CancellationToken cancellationToken = default)
        {
            StartCalls++;
            StartProjectId = projectId;
            StartGoal = goal;
            StartContext = context;
            StartReadOnly = readOnly;
            TaskId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            _task = new(
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
            => new(GetTaskSummary(taskId), Array.Empty<H2AgentProgress>());

        public void CancelTask(Guid taskId)
            => throw new NotSupportedException();

        public bool RespondToApproval(Guid taskId, Guid approvalId, bool approved)
            => false;

        public H2AgentTaskSummary GetTaskSummary(Guid taskId)
            => _task is { } task && task.TaskId == taskId
                ? task
                : throw new KeyNotFoundException();

        public IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(Guid? projectId = null, int limit = 50)
            => _task is { } task
                && (projectId is null || task.ProjectId == projectId)
                ? [task]
                : Array.Empty<H2AgentTaskSummary>();

        public H2AgentEvidence? GetEvidence(string evidenceId) => null;

        public bool AttachProject(Guid taskId, Guid projectId)
        {
            AttachCalls++;
            AttachedTaskId = taskId;
            AttachedProjectId = projectId;
            if (_task is null || _task.TaskId != taskId)
                return false;
            _task = _task with { ProjectId = projectId, UpdatedUtc = DateTime.UtcNow };
            return true;
        }
    }
}

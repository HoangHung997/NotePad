using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using H2Notes.Avalonia;
using H2Notes.Core;

internal static class H2WorkAssistantCompletionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(Action<string, Action> test)
    {
        test("Work Assistant bubble follows live progress and chat visibility without reopening the chat", () =>
        {
            var fixture = Fixture();
            try
            {
                StartQuickTask(fixture.App, "Sửa tài liệu mẫu");
                var compact = Compact(fixture.App); var bubble = Bubble(fixture.App);
                Check(compact.IsVisible && !bubble.IsExpanded, "Busy visible chat duplicated the activity bar");
                fixture.App.HideWorkAssistantCompact(); Pump();
                Check(bubble.IsExpanded && !compact.IsVisible, "Hidden busy chat did not show its bar");
                fixture.Agent.Progress.Add(new(1, DateTime.UtcNow, "tool", "tool-start", "word.replace_range")); Refresh(fixture.App);
                Check(bubble.ActivityText == "Đang sửa nội dung Word…" && !compact.IsVisible, "Bubble missed live tool activity or opened chat automatically");
                fixture.Agent.Progress.Add(new(2, DateTime.UtcNow, "commentary", "commentary", "Đang kiểm tra\nđịnh dạng tiếng Việt")); Refresh(fixture.App);
                Check(bubble.ActivityText == "Đang kiểm tra định dạng tiếng Việt", "Public activity was not refreshed on one line");
                OpenDetails(fixture.App);
                Check(!bubble.IsExpanded && compact.IsVisible, "Opening actual chat did not collapse the bar");
                compact.Hide(); Pump();
                Check(bubble.IsExpanded, "Closing chat directly did not restore busy bar");
                fixture.Agent.Set(H2AgentTaskStatus.Completed, finalText: "Xong"); Refresh(fixture.App);
                Check(!bubble.IsExpanded && !compact.IsVisible, "Completion did not restore circle or reopened hidden chat");
                Check(WorkAssistantActivityText.FromProgress(new(3, DateTime.UtcNow, "tool", "tool-result", "private document body")) is null,
                    "Raw document/tool output leaked onto desktop ticker");
            }
            finally { CloseFixture(fixture); }
        });
        test("Work Assistant projects running attention and verified completion from one Agent task", () =>
        {
            var fixture = Fixture();
            try
            {
                StartQuickTask(fixture.App, "Kiểm tra workbook");
                Check(fixture.Agent.StartCalls == 1, "Quick task did not start exactly once.");
                Check(Bubble(fixture.App).State == WorkAssistantBubbleState.Working,
                    "Running Agent task did not project Working bubble state.");

                fixture.Agent.Set(
                    H2AgentTaskStatus.WaitingForApproval,
                    pending: new H2AgentApproval(Guid.NewGuid(), "Xác nhận sửa workbook", "Chỉ workbook hiện tại", DateTime.UtcNow));
                Refresh(fixture.App);
                Check(Bubble(fixture.App).State == WorkAssistantBubbleState.Attention,
                    "Waiting approval did not project Attention bubble state.");

                OpenDetails(fixture.App);
                var compact = Compact(fixture.App);
                Check(compact.IsTaskResultVisible, "Task result panel did not open.");
                Check(compact.TaskPresentation?.NeedsAttention == true,
                    "Waiting approval result lost attention state.");
                Check(Text(compact, "_completionState").Contains("Cần bạn xác nhận", StringComparison.Ordinal),
                    "Attention result did not explain approval state.");

                fixture.Agent.Set(
                    H2AgentTaskStatus.Completed,
                    finalText: "Đã cập nhật và kiểm tra lại workbook.",
                    evidence:
                    [
                        new H2AgentEvidence(
                            "ev-verified",
                            "verification",
                            new string('a', 64),
                            "Workbook reread passed",
                            LocalPath: @"C:\Projects\DuToan.xlsx",
                            Provenance: "OfficeHost", VerificationPassed: true)
                    ]);
                Refresh(fixture.App);

                Check(Bubble(fixture.App).State == WorkAssistantBubbleState.Completed,
                    "Verified completed task did not project Completed bubble state.");
                Check(compact.TaskPresentation is { Verified: true, NeedsAttention: false },
                    "Verified completion flags are wrong.");
                Check(Text(compact, "_completionResult").Contains("Đã cập nhật", StringComparison.Ordinal),
                    "Concise final result is missing.");

                Click(compact, "_completionDetails");
                Check(Text(compact, "_completionEvidence").Contains("ev-verified", StringComparison.Ordinal)
                    && Text(compact, "_completionEvidence").Contains("Workbook reread passed", StringComparison.Ordinal),
                    "Evidence details did not retain Agent evidence identity/summary.");
            }
            finally
            {
                CloseFixture(fixture);
            }
        });

        test("Work Assistant cancel and retry are explicit and retry never auto-repeats mutation", () =>
        {
            var fixture = Fixture();
            try
            {
                StartQuickTask(fixture.App, "Sửa workbook theo yêu cầu");
                OpenDetails(fixture.App);
                var compact = Compact(fixture.App);

                Click(compact, "_completionCancel");
                Check(fixture.Agent.CancelCalls == 1
                    && fixture.Agent.Summary.Status == H2AgentTaskStatus.Cancelled,
                    "Cancel did not route through AgentAdapter.CancelTask.");
                Check(Bubble(fixture.App).State == WorkAssistantBubbleState.Attention,
                    "Cancelled task did not surface an attention/result state.");

                fixture.Agent.Set(
                    H2AgentTaskStatus.Failed,
                    error: "Verification failed.");
                // Restore the current task id after the explicit cancellation fixture transition.
                SetPrivate(fixture.App, "_workAssistantQuickTaskId", fixture.Agent.TaskId);
                Refresh(fixture.App);
                OpenDetails(fixture.App);

                var startsBeforeRetry = fixture.Agent.StartCalls;
                Click(compact, "_completionRetry");
                Pump();

                Check(fixture.Agent.StartCalls == startsBeforeRetry,
                    "Retry button silently started another Agent task.");
                Check(fixture.App.CurrentWorkAssistantTaskId is null,
                    "Retry preparation retained the old current task as active.");
                Check(compact.PromptText == "Sửa workbook theo yêu cầu",
                    "Retry did not restore the previous goal.");
                Check(compact.SelectedPermissionMode == H2AgentPermissionMode.ObserveOnly,
                    "Retry reused a prior mutation permission preset.");
                Check(compact.CapturedContext is null,
                    "Retry reused stale ActiveWorkContext without recapture.");
            }
            finally
            {
                CloseFixture(fixture);
            }
        });

        test("Work Assistant can link completed task to project and open full Project Workspace", () =>
        {
            var fixture = Fixture(withMainWindow: true);
            try
            {
                var projectBefore = JsonSerializer.Serialize(fixture.Project);
                StartQuickTask(fixture.App, "Tóm tắt kết quả");
                fixture.Agent.Set(
                    H2AgentTaskStatus.Completed,
                    finalText: "Hoàn tất.",
                    evidence:
                    [
                        new H2AgentEvidence("ev-file", "file", null, "Artifact ready")
                    ]);
                Refresh(fixture.App);
                OpenDetails(fixture.App);
                var compact = Compact(fixture.App);

                var picker = PrivateField<ComboBox>(compact, "_completionProjectPicker");
                var choice = picker.ItemsSource!.Cast<WorkAssistantProjectChoice>()
                    .Single(item => item.ProjectId == fixture.Project.Id);
                picker.SelectedItem = choice;
                Click(compact, "_completionLink");
                Pump();

                Check(fixture.Agent.AttachCalls == 1
                    && fixture.Agent.Summary.ProjectId == fixture.Project.Id,
                    "Link project did not route through AgentAdapter.AttachProject.");
                Check(projectBefore == JsonSerializer.Serialize(fixture.Project),
                    "Linking Work Assistant task copied Agent state into ProjectRecord.");

                Click(compact, "_completionOpenWorkspace");
                Pump();
                Check(fixture.Main is not null
                    && fixture.Main.SelectedProjectId == fixture.Project.Id,
                    "Open full workspace did not navigate to the linked project.");
            }
            finally
            {
                CloseFixture(fixture);
            }
        });

        test("Work Assistant completion UX keeps one Agent runtime and has no generic Undo promise", () =>
        {
            var repo = FindRepoRoot();
            var app = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "App.WorkAssistantCompletion.cs"));
            var compact = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "WorkAssistantCompactWindow.Completion.cs"));

            Check(app.Contains("_agentAdapter.ObserveTask(", StringComparison.Ordinal)
                && app.Contains("_agentAdapter.CancelTask(", StringComparison.Ordinal)
                && app.Contains("LinkCurrentWorkAssistantTaskToProject(", StringComparison.Ordinal),
                "Completion UX is not projecting the existing Agent task lifecycle.");

            foreach (var forbidden in new[]
            {
                "QuickWorkSession", "TaskDatabase", "TaskStore",
                "new AgentRuntime", "new AgentOrchestrator", "new ToolRegistry"
            })
                Check(!app.Contains(forbidden, StringComparison.Ordinal)
                    && !compact.Contains(forbidden, StringComparison.Ordinal),
                    "Completion UX introduced a second task/runtime store: " + forbidden);

            var actionButtonLines = compact.Split('\n')
                .Where(line => line.Contains("ActionButton(", StringComparison.Ordinal)
                    || line.Contains("Content =", StringComparison.Ordinal))
                .ToArray();
            Check(!actionButtonLines.Any(line =>
                    line.Contains("\"Undo\"", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("\"Hoàn tác\"", StringComparison.OrdinalIgnoreCase)),
                "Completion UX advertises a generic undo action.");
        });
    }

    private static CompletionFixture Fixture(bool withMainWindow = false)
    {
        var app = new App();
        app.LocalSettings.WorkAssistant.Enabled = true;
        app.WorkAssistantContextCapture = new ContextCapture(Context());

        var project = new ProjectRecord { Name = "Completion project" };
        var board = new NoteRecord
        {
            Title = "Projects",
            NoteKind = "project-hub",
            Projects = [project]
        };
        app.State.Notes.Add(board);

        var agent = new CompletionAgentFake();
        app.AgentAdapter = agent;

        MainWindow? main = null;
        if (withMainWindow)
        {
            main = new MainWindow(app, board);
            typeof(App).GetField("_main", Private)!.SetValue(app, main);
        }

        return new(app, agent, board, project, main);
    }

    private static void StartQuickTask(App app, string goal)
    {
        app.ShowWorkAssistantCompact();
        Pump();
        var compact = Compact(app);
        compact.PromptText = goal;
        Click(compact, "_send");
        Pump();
    }

    private static void Refresh(App app)
        => CallPrivate(app, "RefreshWorkAssistantTaskState");

    private static void OpenDetails(App app)
    {
        CallPrivate(app, "ShowCurrentWorkAssistantTaskDetails");
        Pump();
    }

    private static WorkAssistantCompactWindow Compact(App app)
        => PrivateField<WorkAssistantCompactWindow>(app, "_workAssistantCompact");

    private static WorkAssistantBubbleWindow Bubble(App app)
        => PrivateField<WorkAssistantBubbleWindow>(app, "_workAssistantBubble");

    private static T PrivateField<T>(object owner, string name)
        where T : class
    {
        for (var type = owner.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (field?.GetValue(owner) is T value)
                return value;
        }

        throw new Exception($"Missing field {name} on {owner.GetType().FullName} or its base types.");
    }

    private static string Text(object owner, string field)
        => PrivateField<TextBlock>(owner, field).Text ?? "";

    private static void Click(object owner, string field)
    {
        var button = PrivateField<Button>(owner, field);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static object? CallPrivate(object owner, string name, params object?[] args)
    {
        for (var type = owner.GetType(); type is not null; type = type.BaseType)
        {
            var method = type.GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (method is not null)
                return method.Invoke(owner, args);
        }

        throw new Exception($"Missing method {name} on {owner.GetType().FullName} or its base types.");
    }

    private static void SetPrivate(object owner, string name, object? value)
    {
        for (var type = owner.GetType(); type is not null; type = type.BaseType)
        {
            var field = type.GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (field is null)
                continue;
            field.SetValue(owner, value);
            return;
        }

        throw new Exception($"Missing field {name} on {owner.GetType().FullName} or its base types.");
    }

    private static void Pump()
    {
        for (var i = 0; i < 12; i++)
            Dispatcher.UIThread.RunJobs();
    }

    private static H2ActiveWorkContext Context()
        => new(
            4242,
            987654321,
            "excel",
            H2ApplicationKind.Excel,
            0x1234,
            "win32:1234:4242:987654321",
            "DuToan.xlsx - Excel",
            "BAOCAOGS",
            @"C:\Projects\DuToan.xlsx",
            "D51:F80",
            "OfficeHost",
            DateTime.UtcNow);

    private static void CloseFixture(CompletionFixture fixture)
    {
        try { fixture.Main?.Close(); } catch { }
        try { Compact(fixture.App).Close(); } catch { }
        try { Bubble(fixture.App).Close(); } catch { }
        try
        {
            var timer = PrivateField<DispatcherTimer>(fixture.App, "_workAssistantTaskTimer");
            timer.Stop();
        }
        catch { }
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

    private sealed record CompletionFixture(
        App App,
        CompletionAgentFake Agent,
        NoteRecord Board,
        ProjectRecord Project,
        MainWindow? Main);

    private sealed class ContextCapture : IWorkAssistantActiveContextCapture
    {
        private readonly H2ActiveWorkContext _context;
        public ContextCapture(H2ActiveWorkContext context) => _context = context;
        public H2ActiveWorkContext? Capture() => _context;
        public bool Revalidate(H2ActiveWorkContext context)
            => context.WindowIdentity == _context.WindowIdentity;
    }

    private sealed class CompletionAgentFake : IH2AgentAdapter
    {
        private H2AgentTaskSummary? _summary;
        private readonly Dictionary<string, H2AgentEvidence> _evidence = new(StringComparer.Ordinal);
        public List<H2AgentProgress> Progress { get; } = [];

        public int StartCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public int AttachCalls { get; private set; }
        public Guid TaskId => Summary.TaskId;
        public H2AgentTaskSummary Summary
            => _summary ?? throw new InvalidOperationException("Task not started.");

        public Task<Guid> StartTaskAsync(
            Guid? projectId,
            string goal,
            H2AgentTaskContext? context = null,
            bool readOnly = true,
            CancellationToken cancellationToken = default)
        {
            StartCalls++;
            var now = DateTime.UtcNow;
            _summary = new(
                Guid.NewGuid(),
                projectId,
                goal,
                H2AgentTaskStatus.Running,
                null,
                Array.Empty<H2AgentEvidence>(),
                null,
                null,
                now,
                now);
            return Task.FromResult(_summary.TaskId);
        }

        public void Set(
            H2AgentTaskStatus status,
            string? finalText = null,
            string? error = null,
            H2AgentApproval? pending = null,
            IReadOnlyList<H2AgentEvidence>? evidence = null)
        {
            var current = Summary;
            var values = evidence ?? current.Evidence;
            foreach (var item in values)
                _evidence[item.EvidenceId] = item;
            _summary = current with
            {
                Status = status,
                FinalText = finalText,
                Error = error,
                PendingApproval = pending,
                Evidence = values,
                UpdatedUtc = DateTime.UtcNow
            };
        }

        public H2AgentTaskObservation ObserveTask(Guid taskId, long afterSequence = -1)
            => new(GetTaskSummary(taskId), Progress.Where(p => p.Sequence > afterSequence).ToArray());

        public void CancelTask(Guid taskId)
        {
            CancelCalls++;
            if (Summary.TaskId != taskId) throw new KeyNotFoundException();
            Set(H2AgentTaskStatus.Cancelled);
        }

        public bool RespondToApproval(Guid taskId, Guid approvalId, bool approved) => false;

        public H2AgentTaskSummary GetTaskSummary(Guid taskId)
            => Summary.TaskId == taskId ? Summary : throw new KeyNotFoundException();

        public IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(Guid? projectId = null, int limit = 50)
            => _summary is { } summary
                && (projectId is null || summary.ProjectId == projectId)
                ? [summary]
                : Array.Empty<H2AgentTaskSummary>();

        public H2AgentEvidence? GetEvidence(string evidenceId)
            => _evidence.TryGetValue(evidenceId, out var value) ? value : null;

        public bool AttachProject(Guid taskId, Guid projectId)
        {
            AttachCalls++;
            if (Summary.TaskId != taskId || projectId == Guid.Empty) return false;
            _summary = Summary with
            {
                ProjectId = projectId,
                UpdatedUtc = DateTime.UtcNow
            };
            return true;
        }
    }
}

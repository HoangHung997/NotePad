using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using H2Notes.Avalonia;
using H2Notes.Core;

internal static class H2WorkAssistantPermissionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(Action<string, Action> test)
    {
        test("Work Assistant allow-document preset maps to expiring workbook session scope, not full PC", () =>
        {
            var now = DateTime.SpecifyKind(new DateTime(2026, 9, 20, 8, 0, 0), DateTimeKind.Utc);
            var context = Context(session: "BAOCAOGS", path: @"C:\Projects\DuToan.xlsx");

            Check(WorkAssistantPermissionScopeMapper.TryMap(
                    H2AgentPermissionMode.AllowScopedChanges,
                    context,
                    WorkAssistantContextScope.All,
                    projectId: null,
                    now,
                    out var mapping,
                    out var error),
                "Allow-scoped mapping failed: " + error);

            Check(mapping is not null && !mapping.ReadOnly,
                "Allow-scoped preset stayed read-only.");
            var scope = mapping!.PermissionScope;
            Check(scope.Mode == H2AgentPermissionMode.AllowScopedChanges
                && scope.ScopeKind == H2AgentResourceScopeKind.Session,
                "Workbook/session scope was not selected.");
            Check(scope.MutationAllowed && !scope.ApprovalRequired,
                "Allow-scoped permission flags are wrong.");
            Check(scope.ResourceKey is not null
                && scope.ResourceKey.StartsWith("session:", StringComparison.Ordinal)
                && !scope.ResourceKey.Contains("full", StringComparison.OrdinalIgnoreCase)
                && !scope.ResourceKey.Contains("pc", StringComparison.OrdinalIgnoreCase),
                "Workbook grant looks like a full-PC resource scope.");
            Check(scope.ExpiresUtc - scope.IssuedUtc == TimeSpan.FromMinutes(10),
                "Workbook mutation grant lifetime is not bounded to ten minutes.");
            Check(scope.MatchesActiveContext(context, now.AddMinutes(1)),
                "Grant did not match the workbook/session it was issued for.");

            var otherWorkbook = context with
            {
                DocumentSessionId = "OTHER-SHEET",
                DocumentPath = @"C:\Projects\Other.xlsx"
            };
            Check(!scope.MatchesActiveContext(otherWorkbook, now.AddMinutes(1)),
                "Workbook grant crossed into a different document/session.");
            Check(!scope.MatchesActiveContext(context, scope.ExpiresUtc),
                "Expired workbook grant remained active.");
        });

        test("Work Assistant ask-before-changes maps to scoped mutation plus approval", () =>
        {
            var now = DateTime.SpecifyKind(new DateTime(2026, 9, 20, 8, 30, 0), DateTimeKind.Utc);
            var context = Context(session: "Sheet1", path: @"C:\Projects\Book1.xlsx");

            Check(WorkAssistantPermissionScopeMapper.TryMap(
                    H2AgentPermissionMode.AskBeforeChanges,
                    context,
                    WorkAssistantContextScope.Application | WorkAssistantContextScope.Document,
                    projectId: null,
                    now,
                    out var mapping,
                    out var error),
                "Ask-before-changes mapping failed: " + error);

            Check(mapping is not null && !mapping.ReadOnly,
                "Ask-before-changes did not enable a scoped mutation contract.");
            Check(mapping!.PermissionScope.MutationAllowed
                && mapping.PermissionScope.ApprovalRequired,
                "Ask-before-changes did not require approval.");
            Check(mapping.PermissionScope.ScopeKind == H2AgentResourceScopeKind.Document,
                "Removing session scope did not narrow permission to document.");
        });

        test("Mutation presets fail closed without a concrete document or session scope", () =>
        {
            var now = DateTime.UtcNow;
            var context = Context(session: "Sheet1", path: @"C:\Projects\Book1.xlsx");

            Check(!WorkAssistantPermissionScopeMapper.TryMap(
                    H2AgentPermissionMode.AllowScopedChanges,
                    context,
                    WorkAssistantContextScope.Application | WorkAssistantContextScope.Selection,
                    projectId: null,
                    now,
                    out _,
                    out var error),
                "Allow-scoped changes accepted application/selection without document identity.");
            Check(!string.IsNullOrWhiteSpace(error),
                "Fail-closed permission mapping returned no explanation.");

            Check(!WorkAssistantPermissionScopeMapper.TryMap(
                    H2AgentPermissionMode.UseProjectPolicy,
                    context,
                    WorkAssistantContextScope.All,
                    projectId: null,
                    now,
                    out _,
                    out _),
                "Unscoped quick task incorrectly inherited project policy.");
        });

        test("Compact Work Assistant resets mutation preset whenever foreground context is recaptured", () =>
        {
            var compact = new WorkAssistantCompactWindow(new WorkAssistantSettings());
            compact.SelectedPermissionMode = H2AgentPermissionMode.AllowScopedChanges;
            Check(compact.SelectedPermissionMode == H2AgentPermissionMode.AllowScopedChanges,
                "Test could not select mutation preset.");

            compact.SetActiveContext(Context(session: "A", path: @"C:\A.xlsx"));
            Check(compact.SelectedPermissionMode == H2AgentPermissionMode.ObserveOnly,
                "New context silently inherited a previous mutation grant preset.");

            var combo = compact.FindControl<ComboBox>("WorkAssistantPermissionPreset")
                ?? throw new Exception("Permission preset ComboBox missing.");
            Check(combo.ItemsSource!.Cast<object>().Count() == 4,
                "Work Assistant does not expose the four product permission presets.");
            compact.Close();
        });

        test("Quick Work Assistant forwards workbook-scoped grant through existing Agent adapter", () =>
        {
            var app = new App();
            app.LocalSettings.WorkAssistant.Enabled = true;
            app.WorkAssistantContextCapture = new ContextCapture(
                Context(session: "BAOCAOGS", path: @"C:\Projects\DuToan.xlsx"));
            var agent = new PermissionAgentFake();
            app.AgentAdapter = agent;

            var sharedBefore = JsonSerializer.Serialize(app.State);
            var localBefore = JsonSerializer.Serialize(app.LocalSettings);

            app.ShowWorkAssistantCompact();
            Pump();
            var compact = Compact(app);
            compact.SelectedPermissionMode = H2AgentPermissionMode.AllowScopedChanges;
            compact.PromptText = "Đổi công thức trong workbook hiện tại";
            Send(compact);
            Pump();

            Check(agent.StartCalls == 1 && !agent.StartReadOnly,
                "Mutation preset did not start the normal Agent task as scoped mutating work.");
            var scope = agent.StartContext?.PermissionScope
                ?? throw new Exception("Agent task context lost permission scope.");
            Check(scope.ScopeKind == H2AgentResourceScopeKind.Session
                && scope.DocumentSessionId == "BAOCAOGS"
                && scope.DocumentPath == @"C:\Projects\DuToan.xlsx",
                "Agent task did not receive the exact workbook/session identity.");
            Check(scope.MutationAllowed && !scope.ApprovalRequired,
                "Allow-scoped preset flags changed before reaching Agent adapter.");
            Check(scope.IsActiveAt(DateTime.UtcNow),
                "Fresh task-local permission grant was already expired.");
            Check(sharedBefore == JsonSerializer.Serialize(app.State),
                "Permission mapping mutated shared project truth.");
            Check(localBefore == JsonSerializer.Serialize(app.LocalSettings),
                "Task-local permission grant leaked into local configuration.");
            Check(!typeof(WorkAssistantSettings).GetProperties().Any(property =>
                    property.Name.Contains("Permission", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Contains("Grant", StringComparison.OrdinalIgnoreCase)),
                "Work Assistant permission grant became persistent settings state.");
        });

        test("Quick Work Assistant project-policy preset fails closed until task is project-scoped", () =>
        {
            var app = new App();
            app.LocalSettings.WorkAssistant.Enabled = true;
            app.WorkAssistantContextCapture = new ContextCapture(Context("Sheet1", @"C:\Projects\Book1.xlsx"));
            var agent = new PermissionAgentFake();
            app.AgentAdapter = agent;

            app.ShowWorkAssistantCompact();
            Pump();
            var compact = Compact(app);
            compact.SelectedPermissionMode = H2AgentPermissionMode.UseProjectPolicy;
            compact.PromptText = "Sửa workbook";
            Send(compact);
            Pump();

            Check(agent.StartCalls == 0,
                "Project-policy preset started an unscoped quick Agent task.");
            Check(app.IsWorkAssistantCompactVisible,
                "Fail-closed project-policy mapping hid the compact panel.");
            var status = compact.FindControl<TextBlock>("WorkAssistantCompactStatus")?.Text ?? "";
            Check(status.Contains("chưa gắn dự án", StringComparison.OrdinalIgnoreCase),
                "User did not receive a clear project-policy scope error.");
        });

        test("Work Assistant permission mapping is a scope mapper, not a second execution engine", () =>
        {
            var repo = FindRepoRoot();
            var mapper = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "WorkAssistantPermissionScope.cs"));
            var app = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "App.axaml.cs"));

            Check(mapper.Contains("H2AgentPermissionScope", StringComparison.Ordinal)
                && app.Contains("PermissionScope: permission.PermissionScope", StringComparison.Ordinal)
                && app.Contains("readOnly: permission.ReadOnly", StringComparison.Ordinal),
                "Work Assistant permission preset is not mapped into the Agent task contract.");

            foreach (var forbidden in new[]
            {
                "PermissionEngine", "PermissionDatabase", "PermissionStore",
                "new AgentRuntime", "new AgentOrchestrator", "new ToolRegistry",
                "FullPc", "AllowEverything"
            })
                Check(!mapper.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    "H2M-086 introduced a second/broad permission subsystem: " + forbidden);
        });
    }

    private static H2ActiveWorkContext Context(string session, string path)
        => new(
            ProcessId: 4242,
            ProcessStartUtcTicks: 987654321,
            ProcessName: "excel",
            ApplicationKind: H2ApplicationKind.Excel,
            NativeWindowHandle: 0x1234,
            WindowIdentity: "win32:1234:4242:987654321",
            WindowTitle: Path.GetFileName(path) + " - Excel",
            DocumentSessionId: session,
            DocumentPath: path,
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
        for (var i = 0; i < 10; i++)
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
        public H2ActiveWorkContext? Capture() => _context;
        public bool Revalidate(H2ActiveWorkContext context)
            => context.WindowIdentity == _context.WindowIdentity
               && context.DocumentSessionId == _context.DocumentSessionId
               && context.DocumentPath == _context.DocumentPath;
    }

    private sealed class PermissionAgentFake : IH2AgentAdapter
    {
        public int StartCalls { get; private set; }
        public bool StartReadOnly { get; private set; }
        public H2AgentTaskContext? StartContext { get; private set; }
        public Guid TaskId { get; private set; }

        public Task<Guid> StartTaskAsync(
            Guid? projectId,
            string goal,
            H2AgentTaskContext? context = null,
            bool readOnly = true,
            CancellationToken cancellationToken = default)
        {
            StartCalls++;
            StartReadOnly = readOnly;
            StartContext = context;
            TaskId = Guid.NewGuid();
            return Task.FromResult(TaskId);
        }

        public H2AgentTaskObservation ObserveTask(Guid taskId, long afterSequence = -1)
            => new(
                new H2AgentTaskSummary(
                    taskId, null, "fixture", H2AgentTaskStatus.Running,
                    null, Array.Empty<H2AgentEvidence>(), null, null,
                    DateTime.UtcNow, DateTime.UtcNow),
                Array.Empty<H2AgentProgress>());
        public void CancelTask(Guid taskId) { }
        public bool RespondToApproval(Guid taskId, Guid approvalId, bool approved) => false;
        public H2AgentTaskSummary GetTaskSummary(Guid taskId)
            => ObserveTask(taskId).Summary;
        public IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(Guid? projectId = null, int limit = 50)
            => Array.Empty<H2AgentTaskSummary>();
        public H2AgentEvidence? GetEvidence(string evidenceId) => null;
        public bool AttachProject(Guid taskId, Guid projectId) => false;
    }
}

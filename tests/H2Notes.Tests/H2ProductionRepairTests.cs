using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Transport;
using H2AgentLab.OfficeProtocol;
using H2AgentLab.Verification;
using H2Notes.Avalonia;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

internal static class H2ProductionRepairTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Office readback rejects unexpected target formatting and unsafe mixed-run replacement", () =>
        {
            var before = new ExcelCellState("A1", "old", "", false, true, 123, "General", "left", "top");
            var after = before with { Value = "new", Bold = true };
            Check(OfficeMutationReadback.ExpectedExcelCell(before, new("A1", Value: "new"), after) != after,
                "Excel expected state copied unexpected formatting from observed result");
            var paragraph = new WordParagraphState(0, "hello", "Normal", [new(0, "hello", "Normal", false, true, false)]);
            var changed = paragraph with { Runs = [new(0, "hello", "Normal", true, false, false)] };
            Check(!OfficeMutationReadback.VerifyWordTarget(paragraph, changed, new(0, Bold: true)), "Word lost italic outside patch");
            Check(!OfficeMutationReadback.CanReplaceWordText(paragraph with { Runs = [paragraph.Runs[0], new(1, "world", "Normal", true, true, false)] }),
                "Mixed run formatting was treated as safe to overwrite");
        });
        test("Production ConfirmChanges greeting and multiline message complete without invented mutation", () => InWorkspace(root =>
        {
            foreach (var goal in new[] { "xin chào", "Dòng một\r\nDòng hai\tchi tiết" })
            {
                var script = new Script();
                using var adapter = Adapter(root, script);
                var done = Wait(adapter, adapter.StartTaskAsync(null, goal, new(root, ""), readOnly: false).Result);
                Check(done.Status == H2AgentTaskStatus.Completed, done.Error ?? done.Status.ToString());
                Check(script.Start!.Messages.Any(m => m.Content.Contains(goal)), "Multiline content was lost");
                Check(!H2AgentVerification.IsVerified(done), "Plain conversation was labeled verified mutation");
            }
        }));

        test("Production task planning works in read-only and scoped modes without granting file changes", () => InWorkspace(root =>
        {
            foreach (var readOnly in new[] { true, false })
            {
                var script = new Script("update_plan", JsonSerializer.Serialize(new { plan = "Inspect synthetic fixture; report result." }));
                using var adapter = Adapter(root, script); var now = DateTime.UtcNow;
                var scope = new H2AgentPermissionScope(readOnly ? H2AgentPermissionMode.ObserveOnly : H2AgentPermissionMode.AllowScopedChanges,
                    H2AgentResourceScopeKind.Workspace, "workspace:" + root, !readOnly, false, now, now.AddMinutes(5), documentPath: root);
                var done = Wait(adapter, adapter.StartTaskAsync(null, "Prepare plan only", new(root, "", PermissionScope: scope), readOnly).Result, true);
                Check(done.Status == H2AgentTaskStatus.Completed && script.Results.Any(r => r.Content.Contains("saved")), done.Error ?? done.Status.ToString());
                Check(!File.Exists(Path.Combine(root, "output.txt")), "Planning touched user files");
            }
        }));

        test("Production request uses selected model reasoning history and native attachment snapshot", () => InWorkspace(root =>
        {
            var id = Guid.NewGuid(); var script = new Script();
            var profile = Profile(); profile.Model = "selected-model";
            using var adapter = new H2ProductionAgentAdapter(Path.Combine(root, "state"), () => throw new Exception("Global model used"), script,
                requestModelResolver: (selected, effort) =>
                { Check(selected == id && effort == "high", "Selected model/effort was ignored"); return new(profile, ""); });
            var context = new H2AgentTaskContext(root, "project context", ModelProfileId: id, ReasoningEffort: "high",
                RecentTurns: [new("prior-user", "user", "previous question unique"), new("prior-answer", "assistant", "previous answer unique")],
                Images: [new("image/png", [1, 2, 3])], Files: [new("sample.pdf", "application/pdf", [4, 5, 6])]);
            var done = Wait(adapter, adapter.StartTaskAsync(null, "follow-up", context, false).Result);
            Check(done.Status == H2AgentTaskStatus.Completed, done.Error ?? "Request failed");
            Check(script.Profile?.Model == "selected-model", "Transport got global profile");
            var text = string.Join("\n", script.Start!.Messages.Select(m => m.Content));
            Check(text.Contains("previous question unique") && text.Contains("previous answer unique"), "Conversation history missing");
            var user = script.Start.Messages.Last(m => m.Role == AgentTransportMessageRole.User);
            Check(user.Images?.Single().Data.SequenceEqual(new byte[] { 1, 2, 3 }) == true
                && user.Files?.Single().Name == "sample.pdf", "Native media was discarded");
        }));

        foreach (var approve in new[] { true, false })
            test("Production project change waits for visible approval and obeys " + (approve ? "Allow" : "Deny"), () => InWorkspace(root =>
            {
                var project = new ProjectRecord { Name = "Approval project", UpdatedAtUtc = DateTime.UtcNow };
                var script = new Script("add_project_task", Args(project, "Requested task"));
                using var adapter = Adapter(root, script);
                adapter.BindProjectToolHost(new H2ProjectToolHost(id => id == project.Id ? project : null));
                var taskId = adapter.StartTaskAsync(project.Id, "Add Requested task", new(root, "selected project"), false).Result;
                var waiting = Wait(adapter, taskId, allowPending: true);
                Check(waiting.Status == H2AgentTaskStatus.WaitingForApproval && project.ChecklistItems.Count == 0,
                    "Mutation happened before approval");
                var panel = new AgentApprovalPanel();
                panel.Present(adapter, taskId, waiting.PendingApproval);
                var window = new Window { Content = panel, Width = 560, Height = 360 }; window.Show(); Pump(window);
                try
                {
                    panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == (approve ? "AgentApprove" : "AgentDeny"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var done = Wait(adapter, taskId);
                    Check(project.ChecklistItems.Count == (approve ? 1 : 0), "Approval decision was not enforced");
                    if (approve) Check(done.Status == H2AgentTaskStatus.Completed && H2AgentVerification.IsVerified(done), done.Error ?? "Readback not verified");
                    else Check(!H2AgentVerification.IsVerified(done), "Denied change was labeled verified");
                }
                finally { window.Close(); }
            }));

        test("Production scoped project access verifies notes past 20000 chars and tasks past 200", () => InWorkspace(root =>
        {
            var project = new ProjectRecord { Name = "Large project", Notes = new string('x', 24000) + "\r\nLast line", UpdatedAtUtc = DateTime.UtcNow };
            project.ChecklistItems = Enumerable.Range(0, 205).Select(i => new TaskRecord { Text = "Task " + i }).ToList();
            foreach (var tool in new[] { "append_project_note", "add_project_task" })
            {
                var script = new Script(tool, Args(project, "Added text"));
                using var adapter = Adapter(root, script);
                adapter.BindProjectToolHost(new H2ProjectToolHost(id => id == project.Id ? project : null));
                var now = DateTime.UtcNow;
                var scope = new H2AgentPermissionScope(H2AgentPermissionMode.AllowScopedChanges, H2AgentResourceScopeKind.Project,
                    "h2-project:" + project.Id.ToString("N"), true, false, now, now.AddMinutes(10));
                var done = Wait(adapter, adapter.StartTaskAsync(project.Id, "Apply requested text", new(root, "", PermissionScope: scope), false).Result, true);
                Check(done.Status == H2AgentTaskStatus.Completed && H2AgentVerification.IsVerified(done), done.Error ?? done.Status.ToString());
            }
            Check(project.ChecklistItems.Count == 206 && project.NotesText.EndsWith("Added text"), "Large project readback lost mutation");
        }));

        test("Production workbook grant cannot authorize generic workspace file write", () => InWorkspace(root =>
        {
            var script = new Script("write_text", JsonSerializer.Serialize(new { path = "outside.txt", text = "must not write", expectedHash = "" }));
            using var adapter = Adapter(root, script); var now = DateTime.UtcNow;
            var scope = new H2AgentPermissionScope(H2AgentPermissionMode.AllowScopedChanges, H2AgentResourceScopeKind.Session,
                "session:office:one", true, false, now, now.AddMinutes(10), H2ApplicationKind.Excel, documentSessionId: "one");
            var done = Wait(adapter, adapter.StartTaskAsync(null, "Write file", new(root, "", PermissionScope: scope), false).Result);
            Check(!File.Exists(Path.Combine(root, "outside.txt")), "Workbook grant escaped into filesystem");
            Check(script.Results.Any(r => r.Content.Contains("outside_resource_scope")), "Missing exact resource denial");
            Check(!H2AgentVerification.IsVerified(done), "Denied operation marked verified");
        }));

        test("Production attachment tool reads selected content beyond summary truncation", () => InWorkspace(root =>
        {
            var attachment = new AiAttachment { Name = "details.txt", Text = new string('a', 3000) + "TAIL-EVIDENCE", MimeType = "text/plain" };
            var script = new Script("read_attachment", JsonSerializer.Serialize(new { attachment_id = attachment.Id, offset = 2500 }));
            using var adapter = Adapter(root, script);
            var done = Wait(adapter, adapter.StartTaskAsync(null, "Read attached details", new(root, "", Attachments: [attachment])).Result);
            Check(done.Status == H2AgentTaskStatus.Completed && script.Results.Any(r => r.Content.Contains("TAIL-EVIDENCE")),
                "Only truncated attachment summary was available");
        }));

        test("Production prompt supplies attachment identity even without a UI summary", () => InWorkspace(root =>
        {
            var attachment = new AiAttachment { Name = "ocr.md", Text = "Alpha 12; Beta 30", MimeType = "text/markdown" };
            var script = new Script(); using var adapter = Adapter(root, script);
            var done = Wait(adapter, adapter.StartTaskAsync(null, "Read this attachment", new(root, "", Attachments: [attachment])).Result);
            Check(done.Status == H2AgentTaskStatus.Completed && script.Start!.Messages.Any(m => m.Content.Contains(attachment.Id.ToString())), "Attachment identity was left to the UI to supply");
        }));

        test("Production default composition discovers live Office and Web schemas", () => InWorkspace(root =>
        {
            foreach (var name in new[] { "excel.write_range", "word.replace_range", "web.fetch" })
            {
                var script = new Script(name, "{}", discoveryOnly: true);
                using var adapter = Adapter(root, script);
                var done = Wait(adapter, adapter.StartTaskAsync(null, "Discover capabilities", new(root, "")).Result);
                Check(done.Status == H2AgentTaskStatus.Completed && script.Loaded.Contains(name), "Production registry omitted " + name);
            }
        }));

        test("Verification projection rejects labels failed reports and later unverified completion", () =>
        {
            H2AgentTaskSummary Summary(params H2AgentEvidence[] evidence) => new(Guid.NewGuid(), null, "Task", H2AgentTaskStatus.Completed,
                null, evidence, "done", null, DateTime.UtcNow, DateTime.UtcNow);
            Check(!H2AgentVerification.IsVerified(Summary(new H2AgentEvidence("1", "verification", null, "PASS"))), "Label promoted to verified");
            Check(!H2AgentVerification.IsVerified(Summary(new("1", "verification", null, "", VerificationPassed: true),
                new("2", "verification", null, "", VerificationPassed: false))), "Earlier pass hid later failed verification");
        });

        test("Agent layout keeps every tab clickable and chat below header at wide and narrow sizes", () =>
        {
            var app = new App(); var board = SheetStorage.Demo().Notes[0]; app.State.Notes.Add(board);
            board.Projects[0].NameRich = RichDocument.Plain("DỰ ÁN HỒ SƠ NGHIỆM THU VÀ QUẢN LÝ TIẾN ĐỘ CÔNG TRÌNH CÓ TÊN RẤT DÀI");
            var window = new MainWindow(app, board); window.Show(); window.OpenProjectWorkspace(board.Projects[0].Id);
            try
            {
                foreach (var size in new[] { (1440, 860), (1040, 760), (560, 600) })
                {
                    window.Width = size.Item1; window.Height = size.Item2; Pump(window);
                    var tabs = window.FindControl<StackPanel>("CompactTabs")!;
                    var chat = window.FindControl<Border>("AiHostBorder")!;
                    var tabTop = tabs.TranslatePoint(default, window)!.Value;
                    Check(chat.TranslatePoint(default, window)!.Value.Y >= tabTop.Y + tabs.Bounds.Height - 1, "Chat overlays tabs");
                    foreach (var name in new[] { "AgentTabButton", "TasksTabButton", "NotesTabButton", "ResourcesTabButton", "HistoryTabButton", "EvidenceTabButton" })
                    {
                        var button = window.FindControl<Button>(name)!;
                        if(!button.IsVisible) { Check(window.FindControl<Button>("TabsOverflowButton")!.IsVisible,"Hidden tabs need overflow access");continue; }
                        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                        var hit = window.InputHitTest(center) as Visual;
                        Check(center.X >= 0 && center.X < window.Bounds.Width && (!button.IsEnabled || hit == button || hit?.GetVisualAncestors().Contains(button) == true),
                            $"{name} clipped or covered at {size}; center={center}; hit=" + string.Join("/", hit?.GetVisualAncestors().OfType<Control>().Select(c => c.GetType().Name + ":" + c.Name) ?? []));
                    }
                }
            }
            finally { typeof(App).GetProperty(nameof(App.IsExiting))!.SetValue(app, true); window.Close(); }
        });
    }

    private static string Args(ProjectRecord project, string text) => JsonSerializer.Serialize(new
        { project_id = project.Id, expected_version = project.UpdatedAtUtc!.Value.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), text });
    private static AiProfile Profile() => new() { Name = "script", Model = "script", Protocol = AiProtocol.OpenAiChat, BaseUrl = "https://example.test/v1" };
    private static H2ProductionAgentAdapter Adapter(string root, Script script)
        => new(Path.Combine(root, "state-" + Guid.NewGuid().ToString("N")), () => new(Profile(), ""), script);
    private static void InWorkspace(Action<string> action)
    {
        var path = Path.Combine(Path.GetTempPath(), "h2-repair-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        try { action(path); } finally { Directory.Delete(path, true); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Pump(Window? window = null)
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs(); window?.UpdateLayout();
            if (window is not null) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(5); }
        }
    }
    private static H2AgentTaskSummary Wait(IH2AgentAdapter adapter, Guid id, bool allowPending = false)
    {
        var until = Environment.TickCount64 + 10000;
        while (Environment.TickCount64 < until)
        {
            Pump(); var state = adapter.GetTaskSummary(id);
            if (state.Status is H2AgentTaskStatus.Completed or H2AgentTaskStatus.Blocked or H2AgentTaskStatus.Failed or H2AgentTaskStatus.Cancelled
                || (allowPending && state.Status == H2AgentTaskStatus.WaitingForApproval)) return state;
            Thread.Sleep(10);
        }
        throw new TimeoutException("Production Agent did not reach expected state");
    }

    private sealed class Script(string? tool = null, string arguments = "{}", bool discoveryOnly = false) : IAgentTransportFactory
    {
        private readonly string? _tool = tool;
        private readonly string _arguments = arguments;
        private readonly bool _discoveryOnly = discoveryOnly;
        public AiProfile? Profile; public AgentTransportStartRequest? Start;
        public List<AgentToolResult> Results = []; public List<string> Loaded = [];
        public IAgentTransport Create(AiProfile profile, string apiKey, AgentRunTelemetry telemetry) { Profile = profile; return new Transport(this); }
        private sealed class Transport(Script owner) : IAgentTransport
        {
            private int _round;
            public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.ChatCompletionsFallback;
            public async IAsyncEnumerable<AgentTransportEvent> StartAsync(AgentTransportStartRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                owner.Start = request; await Task.CompletedTask;
                if (owner._tool is null) yield return AgentTransportEvent.TextDeltaEvent("Xin chào!");
                else yield return AgentTransportEvent.Tool(new("search", "tool_search", JsonSerializer.Serialize(new { query = owner._tool })));
                yield return AgentTransportEvent.Complete("start", owner._tool is null ? "stop" : "tool_calls");
            }
            public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(AgentTransportContinuationRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                owner.Results.AddRange(request.ToolResults); owner.Loaded.AddRange(request.NewlyLoadedTools?.Select(t => t.Name) ?? []);
                await Task.CompletedTask;
                if (++_round == 1 && !owner._discoveryOnly) yield return AgentTransportEvent.Tool(new("act", owner._tool!, owner._arguments));
                else yield return AgentTransportEvent.TextDeltaEvent("Agent result.");
                yield return AgentTransportEvent.Complete("round" + _round, _round == 1 && !owner._discoveryOnly ? "tool_calls" : "stop");
            }
            public void Cancel() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

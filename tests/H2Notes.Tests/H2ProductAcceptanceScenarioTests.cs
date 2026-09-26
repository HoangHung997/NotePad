using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using H2AgentLab;
using H2AgentLab.Cad;
using H2AgentLab.Context;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Office;
using H2AgentLab.OfficeProtocol;
using H2AgentLab.Runtime;
using H2AgentLab.Session;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2AgentLab.Verification;
using H2Notes.Avalonia;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

internal static class H2ProductAcceptanceScenarioTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(Action<string, Action> test)
    {
        test("H2M-110 Command Center answers the five-second operational questions on real UI", () =>
        {
            var now = DateTime.UtcNow;
            var needs = Project("Hồ sơ cần duyệt", 0, 2, "Bổ sung pháp lý", now);
            var working = Project("Dự toán đang làm", 1, 3, "Kiểm tra công thức", now.AddMinutes(1));
            var waiting = Project("Bản vẽ đang chờ", 0, 1, "Chờ chủ đầu tư", now.AddMinutes(2));
            var board = new NoteRecord
            {
                Title = "Dự án thực tế",
                NoteKind = "project-hub",
                Projects = [needs, working, waiting]
            };

            var agent = new ProjectionAgent();
            agent.Add(AgentTaskSummary(needs.Id, H2AgentTaskStatus.WaitingForApproval, now.AddMinutes(4),
                approval: new H2AgentApproval(Guid.NewGuid(), "Duyệt cập nhật pháp lý", "Fixture", now.AddMinutes(4))));
            agent.Add(AgentTaskSummary(working.Id, H2AgentTaskStatus.Running, now.AddMinutes(5)));
            agent.Add(AgentTaskSummary(waiting.Id, H2AgentTaskStatus.Queued, now.AddMinutes(3)));

            var app = new App { AgentAdapter = agent };
            app.State.Notes.Add(board);

            var watch = Stopwatch.StartNew();
            var window = new MainWindow(app, board) { Width = 1280, Height = 800 };
            window.Show();
            Pump();
            try
            {
                var items = window.FindControl<ListBox>("CommandCenterList")!.ItemsSource!
                    .Cast<object>().ToArray();
                var attentionList = window.FindControl<ListBox>("CommandCenterAttentionList")!;
                var attentionHeader = window.FindControl<TextBlock>("CommandCenterAttentionHeader")!;
                var sync = window.FindControl<TextBlock>("CommandCenterSync")!.Text ?? "";

                string Get(object item, string name)
                    => item.GetType().GetProperty(name)!.GetValue(item)?.ToString() ?? "";

                Check(items.Length == 3, "Command Center did not present the realistic project set.");
                Check(attentionHeader.Text == "Cần bạn xử lý · 1" && !attentionList.IsVisible,
                    "Collapsed Command Center did not expose the attention count without covering project cards.");
                Check(items.Any(item => Get(item, "Name") == "Hồ sơ cần duyệt"
                    && Get(item, "AttentionText") == "1 cần xem"),
                    "Project card did not expose what needs attention while the section is collapsed.");

                window.FindControl<Button>("CommandCenterAttentionToggle")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                var attention = attentionList.ItemsSource!.Cast<object>().ToArray();
                Check(attentionList.IsVisible
                    && attention.Any(item => Get(item, "Title").Contains("Duyệt", StringComparison.Ordinal)),
                    "User cannot expand and see the concrete attention item.");
                Check(items.Any(item => Get(item, "Name") == "Dự toán đang làm"
                    && Get(item, "AgentText").Contains("đang làm", StringComparison.OrdinalIgnoreCase)),
                    "User cannot see what is working.");
                Check(items.Any(item => Get(item, "NextText").Contains("Kiểm tra công thức", StringComparison.Ordinal)),
                    "User cannot see what is next.");
                Check(items.Any(item => Get(item, "Name") == "Hồ sơ cần duyệt"
                    && Get(item, "AgentText").Contains("chờ phê duyệt", StringComparison.OrdinalIgnoreCase)),
                    "User cannot see that Agent is waiting.");
                Check(sync == "Đã đồng bộ", "User cannot see healthy sync state.");
                Check(watch.Elapsed < TimeSpan.FromSeconds(5),
                    "Automated real-UI Command Center read exceeded the 5-second acceptance budget.");
            }
            finally
            {
                window.Close();
            }
        });

        test("H2M-111 Project AI-first workflow uses concrete bridge without polluting project checklist", () =>
        {
            var root = Temp("project-ai");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "dossier.txt"), "Hồ sơ dự án đã có biên bản và phụ lục.");
            try
            {
                var project = Project("Dự án AI-first", 0, 2, "Chốt hồ sơ", DateTime.UtcNow);
                project.Conversations.Add(new AiConversation
                {
                    Title = "Acceptance review",
                    PermissionMode = AiPermissionMode.ReadOnly
                });
                project.SelectedAiConversationId = project.Conversations[0].Id;
                var board = new NoteRecord
                {
                    Title = "Dự án",
                    NoteKind = "project-hub",
                    Projects = [project]
                };
                var app = new App();
                app.State.Notes.Add(board);
                SetStorage(app, new ProjectWorkspaceStore(root, writerId: "h2m111"));
                // The production UI deliberately uses its isolated Agent workspace, not
                // the shared project data folder. Seed the file where the tool will read it.
                var projectWorkspace = Path.Combine(app.AgentWorkspaceRoot!, "projects", project.Id.ToString("N"));
                Directory.CreateDirectory(projectWorkspace);
                File.Copy(Path.Combine(root, "dossier.txt"), Path.Combine(projectWorkspace, "dossier.txt"), true);
                var transport = new ScriptedToolTransportFactory(
                    [new("read_file", "{\"path\":\"dossier.txt\",\"offset\":\"0\"}")],
                    "Đã kiểm tra toàn bộ hồ sơ và hoàn thành mọi việc có thể.");
                using var adapter = ProductionAdapter(root, transport);
                app.AgentAdapter = adapter;
                H2UiTestNavigation.ConfigureAgentProfile(app);

                var before = JsonSerializer.Serialize(project.ChecklistItems);
                var panel = new AiChatPanel(app);
                panel.SetProject(project);
                var window = new Window { Width = 760, Height = 680, Content = panel };
                window.Show();
                Pump();
                try
                {
                    panel.GetVisualDescendants().OfType<TextBox>()
                        .Single(x => x.Name == "ChatComposer").Text =
                        "Kiểm tra toàn bộ hồ sơ dự án và hoàn thành mọi thứ có thể.";
                    panel.GetVisualDescendants().OfType<Button>()
                        .Single(x => x.Name == "ChatSend")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                    WaitUntil(() => project.Conversations.Count == 1
                        && project.Conversations[0].Messages.Count == 2
                        && project.Conversations[0].Messages[^1].Status == "complete",
                        timeoutMs: 10_000);

                    var answer = project.Conversations[0].Messages[^1];
                    var taskId = answer.AiRunId ?? throw new Exception("Project UI lost Agent TaskId.");
                    var summary = adapter.GetTaskSummary(taskId);
                    var observation = adapter.ObserveTask(taskId);

                    Check(summary.ProjectId == project.Id,
                        "Project association did not reach concrete production bridge.");
                    Check(observation.Progress.Any(),
                        "Agent execution progress is not observable.");
                    Check(summary.Evidence.Any(),
                        "Project Agent result has no execution evidence.");
                    Check(before == JsonSerializer.Serialize(project.ChecklistItems),
                        "Agent execution plan polluted the durable H2 project checklist.");

                    var overview = new H2ProductProjectionService(adapter).BuildProjectOverview(project);
                    Check(overview.AgentStatus == H2AgentTaskStatus.Completed,
                        "Final Project Workspace overview did not update through Agent projection.");
                    Check(answer.Content.Contains("Đã kiểm tra", StringComparison.Ordinal),
                        "Final project answer did not return through existing project UI.");
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Delete(root);
            }
        });

        test("H2M-112 Excel Work Assistant uses structured tools verifies and runs without MainWindow", () =>
        {
            var root = Temp("excel");
            Directory.CreateDirectory(root);
            try
            {
                var recorder = new RecordingExecutor();
                var transport = new ScriptedToolTransportFactory(
                [
                    new("excel.read_formulas", "{}"),
                    new("excel.set_formula", "{}"),
                    new("excel.verify_range", "{}")
                ], "Đã chuyển công thức sang tham chiếu tuyệt đối và xác minh.");
                var runtime = ScenarioRuntime(transport, registry =>
                {
                    StructuredOfficeCapabilityCatalog.RegisterInto(registry, recorder);
                }, "excel.verify_range");
                using var adapter = ProductionAdapter(root, transport, runtimeFactory: runtime);

                var context = Context(
                    H2ApplicationKind.Excel,
                    "excel",
                    "DuToan.xlsx - Excel",
                    session: "excel-session-01",
                    path: Path.Combine(root, "DuToan.xlsx"),
                    selection: "Sheet1!A1:F80",
                    provider: "OfficeHost");
                var app = WorkAssistantApp(root, adapter, context);
                try
                {
                    var compact = OpenCompact(app);
                    Check(compact.SelectedContextSummary.Contains("DuToan.xlsx", StringComparison.Ordinal),
                        "Current workbook context is not visible in Work Assistant.");
                    compact.SelectedPermissionMode = H2AgentPermissionMode.AllowScopedChanges;
                    compact.PromptText = "Đổi toàn bộ công thức trong file đang mở thành tuyệt đối.";
                    ClickSend(compact);

                    WaitUntil(() => app.CurrentWorkAssistantTaskId is not null);
                    Check(app.IsWorkAssistantCompactVisible && app.IsWorkAssistantBubbleVisible,
                        "Work Assistant must keep conversation and pending approvals visible while task runs.");
                    Check(!app.OpenWindows.OfType<MainWindow>().Any(),
                        "Excel quick task required opening H2 main window.");

                    var summary = WaitTerminal(adapter, app.CurrentWorkAssistantTaskId!.Value);
                    CallPrivate(app, "RefreshWorkAssistantTaskState");

                    Check(summary.ProjectId is null,
                        "Excel Work Assistant quick task unexpectedly required ProjectId.");
                    Check(recorder.Calls.SequenceEqual(new[]
                    {
                        "excel.read_formulas", "excel.set_formula", "excel.verify_range"
                    }), "Structured Excel tool path was not preferred.");
                    Check(summary.Evidence.Any(x => x.Kind.Contains("verification", StringComparison.OrdinalIgnoreCase)),
                        "Excel mutation completed without projected verification evidence.");
                    Check(!recorder.Calls.Any(IsPixelTool),
                        "Excel scenario used pixel/computer fallback despite structured provider sufficing.");
                }
                finally
                {
                    CloseAssistant(app);
                }
            }
            finally
            {
                Delete(root);
            }
        });

        test("H2M-113 Word legal Work Assistant uses unsaved structured Word and authoritative Web evidence", () =>
        {
            var root = Temp("word");
            Directory.CreateDirectory(root);
            try
            {
                var recorder = new RecordingExecutor();
                var transport = new ScriptedToolTransportFactory(
                [
                    new("word.get_spelling_errors", "{}"),
                    new("word.extract_legal_citations", "{}"),
                    new("web.search", "{\"query\":\"Nghị định pháp lý hiện hành\"}"),
                    new("word.replace_range", "{}"),
                    new("word.verify_range", "{}")
                ], "Đã rà chính tả, đối chiếu nguồn pháp lý và cập nhật tài liệu.");
                var runtime = ScenarioRuntime(transport, registry =>
                {
                    StructuredOfficeCapabilityCatalog.RegisterInto(registry, recorder);
                    RegisterWebSearch(registry, recorder);
                }, "word.verify_range");
                using var adapter = ProductionAdapter(root, transport, runtimeFactory: runtime);

                var context = Context(
                    H2ApplicationKind.Word,
                    "winword",
                    "Document1 - Word",
                    session: "word-unsaved-session",
                    path: null,
                    selection: "Toàn bộ tài liệu",
                    provider: "OfficeHost");
                var app = WorkAssistantApp(root, adapter, context);
                try
                {
                    var compact = OpenCompact(app);
                    compact.SelectedPermissionMode = H2AgentPermissionMode.AllowScopedChanges;
                    compact.PromptText = "Kiểm tra chính tả và rà văn bản pháp lý rồi cập nhật tài liệu.";
                    ClickSend(compact);
                    WaitUntil(() => app.CurrentWorkAssistantTaskId is not null);
                    var summary = WaitTerminal(adapter, app.CurrentWorkAssistantTaskId!.Value);

                    Check(compact.CapturedContext?.DocumentPath is null
                        && compact.CapturedContext?.DocumentSessionId == "word-unsaved-session",
                        "Unsaved Word session was not preserved as live context.");
                    Check(recorder.Calls.Contains("word.get_spelling_errors")
                        && recorder.Calls.Contains("word.extract_legal_citations")
                        && recorder.Calls.Contains("word.replace_range")
                        && recorder.Calls.Contains("word.verify_range"),
                        "Structured Word path did not cover language/legal/edit/verify work.");
                    Check(recorder.Calls.Contains("web.search")
                        && recorder.Outputs.Any(x => x.Contains("vanban.chinhphu.vn", StringComparison.OrdinalIgnoreCase)),
                        "Legal review did not use authoritative Web evidence fixture.");
                    Check(summary.Evidence.Any(x => x.Kind.Contains("verification", StringComparison.OrdinalIgnoreCase)),
                        "Word update lacks verification evidence.");
                    Check(!recorder.Calls.Any(IsPixelTool),
                        "Word scenario used pixel/computer calls despite structured providers sufficing.");
                }
                finally
                {
                    CloseAssistant(app);
                }
            }
            finally
            {
                Delete(root);
            }
        });

        test("AR-024 E2 Global Work Assistant crosses production bridge into OfficeHost Excel", () =>
        {
            var root = Temp("ar024-global-excel");
            Directory.CreateDirectory(root);
            try
            {
                var host = OfficeHostExecutable();
                ExcelLiveSnapshot before;
                using (var probe = new OfficeHostClient(host, fixtureMode: true))
                {
                    var session = probe.DiscoverExcelAsync().GetAwaiter().GetResult().ActiveSessionId
                        ?? throw new Exception("AR-024 Excel fixture has no active session.");
                    before = probe.SnapshotExcelAsync(session).GetAwaiter().GetResult();
                }

                var transport = new ScriptedToolTransportFactory(
                [
                    new("excel.read_range", JsonSerializer.Serialize(new
                    {
                        session_id = before.SessionId,
                        sheet_name = "Data",
                        range = "A1:A2",
                        page_size = 2
                    })),
                    new("excel.write_range", JsonSerializer.Serialize(new
                    {
                        session_id = before.SessionId,
                        content_token = before.ContentToken,
                        sheet_name = "Data",
                        cells = new[] { new { address = "A2", value = "84" } }
                    })),
                    new("excel.verify_range", JsonSerializer.Serialize(new
                    {
                        session_id = before.SessionId,
                        sheet_name = "Data",
                        range = "A2",
                        page_size = 1
                    }))
                ],
                "Đã đọc đúng vùng, cập nhật A2 và xác minh bằng readback production.");

                var spawned = new List<OfficeHostClient>();
                IOfficeSessionClient OfficeFactory()
                {
                    var client = new OfficeHostClient(host, fixtureMode: true, defaultTimeout: TimeSpan.FromSeconds(30));
                    spawned.Add(client);
                    return client;
                }

                var stateRoot = Path.Combine(root, "agent-state");
                using var adapter = ProductionAdapter(root, transport,
                    stateRoot: stateRoot,
                    officeClientFactory: OfficeFactory,
                    captureValidator: _ => true);

                var context = Context(
                    H2ApplicationKind.Excel,
                    "excel",
                    "UnsavedFixture.xlsx - Excel",
                    before.SessionId,
                    before.FullName,
                    "Data!A1:A2",
                    "OfficeHost");
                var app = WorkAssistantApp(root, adapter, context);
                try
                {
                    var compact = OpenCompact(app);
                    // OfficeHost fixture has no real HWND/PID identity. Do not fabricate a captured
                    // native window just to satisfy an E2 gate: drop the capture chip and exercise
                    // the same production Global UI/adapter/runtime path with an explicit task-local
                    // FullAccess grant plus the exact OfficeHost session selected by the scripted model.
                    compact.RemoveContextScope(WorkAssistantContextScope.All);
                    Check(compact.SelectedContextScope == WorkAssistantContextScope.None,
                        "AR-024 Global fixture retained a fake captured-window authority.");
                    compact.SelectedPermissionMode = H2AgentPermissionMode.FullAccess;
                    compact.PromptText = "Đọc A1:A2 của workbook đang mở rồi đổi A2 thành 84 và xác minh.";
                    ClickSend(compact);
                    WaitUntil(() => app.CurrentWorkAssistantTaskId is not null);
                    var taskId = app.CurrentWorkAssistantTaskId!.Value;
                    var summary = WaitTerminal(adapter, taskId, 20_000);

                    if (summary.Status != H2AgentTaskStatus.Completed)
                    {
                        var completion = summary.Completion is null ? "<null>" : JsonSerializer.Serialize(summary.Completion);
                        var goals = summary.GoalState is null ? "<null>" : JsonSerializer.Serialize(summary.GoalState);
                        var evidenceDump = JsonSerializer.Serialize(summary.Evidence.Select(x => new
                        {
                            x.EvidenceId, x.Kind, x.Summary, x.Provenance, x.VerificationPassed
                        }));
                        var outcomesPath = Path.Combine(stateRoot, "tasks", taskId.ToString("N"), "tool-outcomes.jsonl");
                        var outcomeDump = File.Exists(outcomesPath) ? File.ReadAllText(outcomesPath) : "<missing>";
                        throw new Exception("Global production Office slice did not complete: status=" + summary.Status
                            + "; error=" + summary.Error + "; final=" + summary.FinalText
                            + "; completion=" + completion + "; goals=" + goals
                            + "; evidence=" + evidenceDump + "; outcomes=" + outcomeDump);
                    }
                    Check(summary.ProjectId is null, "Global Work Assistant unexpectedly became project-scoped.");
                    Check(summary.Evidence.Any(x => x.Kind.Contains("verification", StringComparison.OrdinalIgnoreCase)),
                        "Global Excel mutation has no production verification evidence.");
                    var outcomes = File.ReadAllText(Path.Combine(stateRoot, "tasks", taskId.ToString("N"), "tool-outcomes.jsonl"));
                    Check(outcomes.Contains("\"excel.read_range\"", StringComparison.Ordinal)
                        && outcomes.Contains("\"excel.write_range\"", StringComparison.Ordinal)
                        && outcomes.Contains("\"excel.verify_range\"", StringComparison.Ordinal),
                        "Global UI did not traverse read/mutate/verify production Office tools.");
                    Check(spawned.Any(client => client.StartCount > 0),
                        "Global production Office slice did not start an OfficeHost process.");
                }
                finally
                {
                    CloseAssistant(app);
                }
            }
            finally
            {
                Delete(root);
            }
        });

        test("AR-024 E2 Project Agent crosses production bridge into OfficeHost Word", () =>
        {
            var root = Temp("ar024-project-word");
            Directory.CreateDirectory(root);
            try
            {
                var host = OfficeHostExecutable();
                WordLiveSnapshot before;
                using (var probe = new OfficeHostClient(host, fixtureMode: true))
                {
                    var session = probe.DiscoverWordAsync().GetAwaiter().GetResult().ActiveSessionId
                        ?? throw new Exception("AR-024 Word fixture has no active session.");
                    before = probe.SnapshotWordAsync(session).GetAwaiter().GetResult();
                }

                var project = Project("AR-024 production project", 0, 1, "Cập nhật Word", DateTime.UtcNow);
                project.Links.Add(new ProjectLink(Guid.NewGuid(), "Bound live Word fixture", before.FullName));
                var conversation = new AiConversation
                {
                    Title = "AR-024 production",
                    PermissionMode = AiPermissionMode.ProjectAccess
                };
                project.Conversations.Add(conversation);
                project.SelectedAiConversationId = conversation.Id;
                var board = new NoteRecord { Title = "AR-024", NoteKind = "project-hub", Projects = [project] };

                var transport = new ScriptedToolTransportFactory(
                [
                    new("word.read_paragraphs", JsonSerializer.Serialize(new
                    {
                        session_id = before.SessionId,
                        page_size = 2
                    })),
                    new("word.replace_range", JsonSerializer.Serialize(new
                    {
                        session_id = before.SessionId,
                        content_version = before.ContentVersion,
                        paragraphs = new[] { new { paragraphIndex = 1, text = "AR-024-PROJECT-WORD" } }
                    }))
                ],
                "Đã đọc đúng tài liệu Word liên kết của dự án, cập nhật đoạn mục tiêu và xác minh.");

                var spawned = new List<OfficeHostClient>();
                IOfficeSessionClient OfficeFactory()
                {
                    var client = new OfficeHostClient(host, fixtureMode: true, defaultTimeout: TimeSpan.FromSeconds(30));
                    spawned.Add(client);
                    return client;
                }

                var stateRoot = Path.Combine(root, "agent-state");
                using var adapter = ProductionAdapter(root, transport,
                    stateRoot: stateRoot,
                    officeClientFactory: OfficeFactory,
                    captureValidator: _ => true);
                var app = new App { AgentAdapter = adapter };
                app.State.Notes.Add(board);
                SetStorage(app, new ProjectWorkspaceStore(root, writerId: "ar024-project"));
                app.LocalSettings.Ai.ProjectAccessConversationIds.Add(conversation.Id);
                H2UiTestNavigation.ConfigureAgentProfile(app);

                var panel = new AiChatPanel(app);
                panel.SetProject(project);
                var window = new Window { Width = 760, Height = 680, Content = panel };
                window.Show();
                Pump();
                try
                {
                    panel.GetVisualDescendants().OfType<TextBox>()
                        .Single(x => x.Name == "ChatComposer").Text =
                        "Đọc tài liệu Word liên kết của dự án, đổi đoạn thứ hai thành AR-024-PROJECT-WORD và xác minh.";
                    panel.GetVisualDescendants().OfType<Button>()
                        .Single(x => x.Name == "ChatSend")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                    WaitUntil(() => conversation.Messages.Count >= 2
                        && conversation.Messages[^1].Status is "complete" or "error",
                        timeoutMs: 20_000);

                    var answer = conversation.Messages[^1];
                    var taskId = answer.AiRunId ?? throw new Exception("Project UI lost AR-024 TaskId.");
                    var summary = adapter.GetTaskSummary(taskId);
                    Check(answer.Status == "complete" && summary.Status == H2AgentTaskStatus.Completed,
                        "Project production Office slice did not complete: " + (answer.ErrorText ?? summary.Error));
                    Check(summary.ProjectId == project.Id, "Project identity did not reach production bridge.");
                    Check(summary.Evidence.Any(x => x.Kind.Contains("verification", StringComparison.OrdinalIgnoreCase)),
                        "Project Word mutation has no production verification evidence.");
                    var outcomes = File.ReadAllText(Path.Combine(stateRoot, "tasks", taskId.ToString("N"), "tool-outcomes.jsonl"));
                    Check(outcomes.Contains("\"word.read_paragraphs\"", StringComparison.Ordinal)
                        && outcomes.Contains("\"word.replace_range\"", StringComparison.Ordinal),
                        "Project UI did not traverse production Word tools.");
                    Check(spawned.Any(client => client.StartCount > 0),
                        "Project production Office slice did not start an OfficeHost process.");
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Delete(root);
            }
        });

        test("H2M-114 AutoCAD Work Assistant performs bounded selected-block mutation and reread verification", () =>
        {
            var root = Temp("cad");
            Directory.CreateDirectory(root);
            try
            {
                var recorder = new RecordingExecutor();
                var transport = new ScriptedToolTransportFactory(
                [
                    new("autocad.query_entities",
                        "{\"document_session_id\":\"dwg-01\",\"document_state_token\":\"doc-v1\",\"query\":{\"handles\":[\"ABCD\"]}}"),
                    new("autocad.read_attributes",
                        "{\"document_session_id\":\"dwg-01\",\"document_state_token\":\"doc-v1\",\"entity_handle\":\"ABCD\",\"object_type\":\"INSERT\",\"layer\":\"OTC\",\"entity_state_token\":\"ent-v1\"}"),
                    new("autocad.update_attribute",
                        "{\"document_session_id\":\"dwg-01\",\"document_state_token\":\"doc-v1\",\"entity_handle\":\"ABCD\",\"object_type\":\"INSERT\",\"layer\":\"OTC\",\"entity_state_token\":\"ent-v1\",\"attribute_tag\":\"KM\",\"value\":\"12+345\"}"),
                    new("autocad.verify_entity",
                        "{\"document_session_id\":\"dwg-01\",\"document_state_token\":\"doc-v2\",\"entity_handle\":\"ABCD\",\"entity_state_token\":\"ent-v2\",\"expectation\":{\"attribute_tag\":\"KM\",\"value\":\"12+345\"}}")
                ], "Đã sửa attribute KM của block OTC đang chọn và xác minh lại.");
                var runtime = ScenarioRuntime(transport, registry =>
                {
                    foreach (var descriptor in AutoCadProviderPolicy.BuildDescriptors(recorder))
                        registry.Register(descriptor);
                }, "autocad.verify_entity");
                using var adapter = ProductionAdapter(root, transport, runtimeFactory: runtime);

                var context = Context(
                    H2ApplicationKind.AutoCAD,
                    "acad",
                    "Drawing1.dwg - AutoCAD",
                    session: "dwg-01",
                    path: Path.Combine(root, "Drawing1.dwg"),
                    selection: "Block=OTC;Handle=ABCD",
                    provider: "AutoCADNative");
                var app = WorkAssistantApp(root, adapter, context);
                try
                {
                    var compact = OpenCompact(app);
                    compact.SelectedPermissionMode = H2AgentPermissionMode.AllowScopedChanges;
                    compact.PromptText = "Kiểm tra block OTC đang chọn và sửa attribute sai.";
                    ClickSend(compact);
                    WaitUntil(() => app.CurrentWorkAssistantTaskId is not null);
                    var summary = WaitTerminal(adapter, app.CurrentWorkAssistantTaskId!.Value);

                    Check(recorder.Calls.SequenceEqual(new[]
                    {
                        "autocad.query_entities",
                        "autocad.read_attributes",
                        "autocad.update_attribute",
                        "autocad.verify_entity"
                    }), "AutoCAD scenario did not use structured query/read/mutate/verify flow. Calls="
                        + string.Join(",", recorder.Calls) + "; status=" + summary.Status + "; error=" + summary.Error);
                    Check(recorder.Arguments
                        .Where(pair => pair.Name == "autocad.update_attribute")
                        .All(pair => pair.Json.Contains("\"ABCD\"", StringComparison.Ordinal)
                                     && !pair.Json.Contains("OTHER", StringComparison.OrdinalIgnoreCase)),
                        "AutoCAD mutation was not bounded to selected entity identity.");
                    Check(summary.Evidence.Any(x => x.Kind.Contains("verification", StringComparison.OrdinalIgnoreCase)),
                        "AutoCAD update lacks reread/verification evidence.");
                    Check(!recorder.Calls.Any(IsPixelTool),
                        "AutoCAD scenario fell back to unrelated pixel automation.");
                }
                finally
                {
                    CloseAssistant(app);
                }
            }
            finally
            {
                Delete(root);
            }
        });

        test("H2M-115 legacy migration preserves project history attachments saved files layout and new Agent work", () =>
        {
            var root = Temp("legacy");
            var legacy = Path.Combine(root, "legacy.json");
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(root);
            try
            {
                var attachment = new AiAttachment
                {
                    Name = "old.txt",
                    MimeType = "text/plain",
                    Data = [65, 66, 67],
                    Text = "old attachment",
                    Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([65, 66, 67])).ToLowerInvariant()
                };
                var saved = new AiSavedFile("result.txt", "saved/result.txt", new string('a', 64), DateTime.UtcNow.AddDays(-1));
                var project = new ProjectRecord
                {
                    Name = "Legacy project",
                    Notes = "Legacy notes",
                    Layout = new ProjectLayout
                    {
                        Tab = "notes",
                        AiDock = "right",
                        TasksCollapsed = true,
                        NotesCollapsed = false
                    },
                    ChecklistItems =
                    [
                        new TaskRecord { Text = "Old task A", IsCompleted = true },
                        new TaskRecord { Text = "Old task B" }
                    ],
                    Conversations =
                    [
                        new AiConversation
                        {
                            Title = "Old chat 1",
                            Messages =
                            [
                                new AiMessage
                                {
                                    Role = "user",
                                    Content = "Old question",
                                    Attachments = [attachment],
                                    SavedFiles = [saved],
                                    CreatedAt = DateTime.UtcNow.AddDays(-2)
                                },
                                new AiMessage { Role = "assistant", Content = "Old answer", CreatedAt = DateTime.UtcNow.AddDays(-2) }
                            ]
                        },
                        new AiConversation
                        {
                            Title = "Old chat 2",
                            Messages = [new AiMessage { Content = "Second history", CreatedAt = DateTime.UtcNow.AddDays(-1) }]
                        }
                    ]
                };
                var legacyState = new SheetState
                {
                    Notes =
                    [
                        new NoteRecord
                        {
                            Title = "Projects",
                            NoteKind = "project-hub",
                            Projects = [project]
                        },
                        new NoteRecord
                        {
                            Title = "Legacy note",
                            NoteKind = "general",
                            Content = "Standalone legacy note"
                        }
                    ]
                };
                new SheetStorage(legacy).Save(legacyState);

                var store = new ProjectWorkspaceStore(workspace, writerId: "migration");
                var migrated = store.LoadOrImport(legacy);
                var migratedProject = migrated.Notes.Single(x => x.IsBoard).Projects.Single();

                Check(migratedProject.ChecklistItems.Count == 2
                    && migratedProject.NotesText.Contains("Legacy", StringComparison.Ordinal),
                    "Legacy tasks/notes were lost.");
                Check(migratedProject.Conversations.Count == 2
                    && migratedProject.Conversations[0].Messages[0].Attachments.Single().Text == "old attachment"
                    && migratedProject.Conversations[0].Messages[0].SavedFiles.Single().Name == "result.txt",
                    "Legacy AI history/attachments/saved files were lost.");
                Check(migratedProject.Layout.Tab == "notes"
                    && migratedProject.Layout.AiDock == "right"
                    && migratedProject.Layout.TasksCollapsed,
                    "Portable old layout preference was lost.");
                Check(migrated.Notes.Any(x => x.Title == "Legacy note" && x.ReadContent().Text == "Standalone legacy note"),
                    "Standalone legacy note was lost.");

                File.WriteAllText(Path.Combine(workspace, "migration-check.txt"), "migrated");
                var transport = new ScriptedToolTransportFactory(
                    [new("read_file", "{\"path\":\"migration-check.txt\",\"offset\":\"0\"}")],
                    "New Agent task works after migration.");
                using var adapter = ProductionAdapter(workspace, transport);
                adapter.BindProjectToolHost(new H2ProjectToolHost(
                    id => id == migratedProject.Id ? migratedProject : null));
                var taskId = adapter.StartTaskAsync(
                    migratedProject.Id,
                    "Kiểm tra workspace sau migration.",
                    new H2AgentTaskContext(workspace, "Migrated project", migratedProject.UpdatedAtUtc?.Ticks ?? 0),
                    readOnly: true).GetAwaiter().GetResult();
                var summary = WaitTerminal(adapter, taskId);
                Check(summary.Status == H2AgentTaskStatus.Completed && summary.Evidence.Any(),
                    "New concrete Agent task does not work after legacy migration.");
            }
            finally
            {
                Delete(root);
            }
        });

        test("H2M-116 two-PC projection keeps shared project truth and local Agent/layout state separate", () =>
        {
            var root = Temp("multipc");
            var agentA = Path.Combine(root, "agent-a");
            var agentB = Path.Combine(root, "agent-b");
            Directory.CreateDirectory(root);
            try
            {
                var storeA = new ProjectWorkspaceStore(root, writerId: "PC-A",
                    recoveryRoot: Path.Combine(root, ".rec-a"),
                    pendingRoot: Path.Combine(root, ".pending-a"));
                var stateA = storeA.LoadOrImport();
                var project = Project("Shared project", 0, 1, "Shared next", DateTime.UtcNow);
                var board = new NoteRecord { Title = "Shared board", NoteKind = "project-hub", Projects = [project] };
                stateA.Notes.Add(board);
                storeA.Save(stateA);

                var storeB = new ProjectWorkspaceStore(root, writerId: "PC-B",
                    recoveryRoot: Path.Combine(root, ".rec-b"),
                    pendingRoot: Path.Combine(root, ".pending-b"));
                var stateB = storeB.Read();
                var projectB = stateB.Notes.Single(x => x.IsBoard).Projects.Single();
                Check(projectB.DisplayName == "Shared project"
                    && projectB.ChecklistItems.Single().DisplayText == "Shared next",
                    "PC-B did not receive consistent shared project/task truth.");

                var localA = new LocalConfiguration();
                var localB = new LocalConfiguration();
                localA.GetProjectLayout(project.Id).AiX = 111;
                localB.GetProjectLayout(project.Id).AiX = 777;
                Check(localA.GetProjectLayout(project.Id).AiX != localB.GetProjectLayout(project.Id).AiX,
                    "Machine-local layout collapsed into shared state.");

                var blocking = new BlockingTransportFactory();
                using var pc1Agent = ProductionAdapter(root, blocking, stateRoot: agentA);
                pc1Agent.BindProjectToolHost(new H2ProjectToolHost(
                    id => id == project.Id ? project : null));
                var pc1Task = pc1Agent.StartTaskAsync(
                    project.Id,
                    "Long running local PC1 task",
                    new H2AgentTaskContext(root, "PC1"),
                    readOnly: true).GetAwaiter().GetResult();
                WaitUntil(() => pc1Agent.GetTaskSummary(pc1Task).Status == H2AgentTaskStatus.Running);

                using var pc2Agent = ProductionAdapter(root, new ScriptedToolTransportFactory([], "unused"), stateRoot: agentB);
                Check(pc2Agent.GetRecentTasks(project.Id).Count == 0,
                    "PC2 falsely represented PC1 local Agent task as locally running.");

                project.ChecklistItems[0].IsCompleted = true;
                project.ChecklistItems[0].CompletedAtUtc = DateTime.UtcNow;
                project.UpdatedAtUtc = DateTime.UtcNow;
                storeA.SaveIncremental(stateA, new HashSet<Guid> { project.Id });
                Check(storeB.RefreshFromDisk(stateB, new HashSet<Guid>()),
                    "PC-B did not observe completed shared project state.");
                projectB = stateB.Notes.Single(x => x.IsBoard).Projects.Single();
                Check(projectB.ChecklistItems.Single().IsCompleted,
                    "Completed shared project state was corrupted during projection.");

                pc1Agent.CancelTask(pc1Task);
                WaitTerminal(pc1Agent, pc1Task);
            }
            finally
            {
                Delete(root);
            }
        });
    }

    private static ProjectRecord Project(
        string name,
        int completed,
        int total,
        string next,
        DateTime updated)
    {
        var project = new ProjectRecord
        {
            Name = name,
            CreatedAtUtc = updated.AddHours(-1),
            UpdatedAtUtc = updated
        };
        for (var i = 0; i < total; i++)
        {
            var done = i < completed;
            project.ChecklistItems.Add(new TaskRecord
            {
                Text = i == completed ? next : "Task " + (i + 1),
                IsCompleted = done,
                CreatedAtUtc = updated.AddMinutes(-30 + i),
                UpdatedAtUtc = updated.AddMinutes(-10 + i),
                CompletedAtUtc = done ? updated.AddMinutes(-5 + i) : null
            });
        }
        return project;
    }

    private static H2AgentTaskSummary AgentTaskSummary(
        Guid? projectId,
        H2AgentTaskStatus status,
        DateTime updated,
        H2AgentApproval? approval = null)
        => new(
            Guid.NewGuid(),
            projectId,
            "acceptance fixture",
            status,
            approval,
            [],
            null,
            null,
            updated.AddMinutes(-1),
            updated);

    private static H2ProductionAgentAdapter ProductionAdapter(
        string workspace,
        IAgentTransportFactory transport,
        IAgentRuntimeFactory? runtimeFactory = null,
        string? stateRoot = null,
        Func<IOfficeSessionClient>? officeClientFactory = null,
        Func<H2ActiveWorkContext, bool>? captureValidator = null)
        => new(
            stateRoot ?? Path.Combine(workspace, ".agent-state-" + Guid.NewGuid().ToString("N")),
            () => new H2ProductionAgentModel(
                new AiProfile
                {
                    Name = "H11 deterministic provider",
                    Protocol = AiProtocol.OpenAiChat,
                    BaseUrl = "https://example.test/v1",
                    Model = "h11-ci"
                },
                ""),
            transport,
            runtimeFactory,
            officeClientFactory: officeClientFactory,
            captureValidator: captureValidator);

    private static ScenarioRuntimeFactory ScenarioRuntime(
        IAgentTransportFactory transport,
        Action<ToolRegistry> configure,
        params string[] verificationTools)
        => new(
            transport,
            configure,
            new ScenarioVerifier(verificationTools));

    private static void RegisterWebSearch(
        ToolRegistry registry,
        IAgentToolExecutor executor)
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "function",
            function = new
            {
                name = "web.search",
                description = "Search authoritative current legal sources.",
                parameters = new
                {
                    type = "object",
                    properties = new { query = new { type = "string" } },
                    required = new[] { "query" },
                    additionalProperties = false
                }
            }
        });
        registry.Register(new ToolDescriptor(
            "web.search",
            new ToolNamespace("web", "Authoritative Web research."),
            "Search authoritative current legal sources.",
            AgentToolRisk.Low,
            AgentToolAccess.ReadOnly,
            supportsParallel: true,
            "v1",
            schema,
            executor,
            provenance: new ToolProvenance("web-reference", "1.0.0", "fixture-authoritative", "1.0.0"),
            resourceScope: new ToolResourceScope("web:public", "web:public"),
            serializationKey: "web",
            canProvideVerificationEvidence: true,
            preference: new ToolPreferenceMetadata("web", ToolInteractionFidelity.Structured)));
    }

    private static App WorkAssistantApp(
        string workspace,
        IH2AgentAdapter adapter,
        H2ActiveWorkContext context)
    {
        var app = new App { AgentAdapter = adapter };
        app.LocalSettings.WorkAssistant.Enabled = true;
        app.LocalSettings.WorkAssistant.StartCollapsed = true;
        app.WorkAssistantContextCapture = new FixedContextCapture(context);
        SetStorage(app, new ProjectWorkspaceStore(workspace, writerId: "h11-work-assistant"));
        return app;
    }

    private static WorkAssistantCompactWindow OpenCompact(App app)
    {
        app.ShowWorkAssistantCompact();
        Pump();
        var compact = (WorkAssistantCompactWindow)(typeof(App)
            .GetField("_workAssistantCompact", Private)!.GetValue(app)
            ?? throw new Exception("Work Assistant compact window not created."));
        return compact;
    }

    private static void ClickSend(WorkAssistantCompactWindow compact)
    {
        var send = (Button)(typeof(WorkAssistantCompactWindow)
            .GetField("_send", Private)!.GetValue(compact)
            ?? throw new Exception("Work Assistant send control missing."));
        send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    private static H2ActiveWorkContext Context(
        H2ApplicationKind kind,
        string process,
        string title,
        string? session,
        string? path,
        string? selection,
        string provider)
        => new(
            ProcessId: 4242,
            ProcessStartUtcTicks: 987654321,
            ProcessName: process,
            ApplicationKind: kind,
            NativeWindowHandle: 0x1234,
            WindowIdentity: "win32:1234:4242:987654321",
            WindowTitle: title,
            DocumentSessionId: session,
            DocumentPath: path,
            Selection: selection,
            Provider: provider,
            CapturedUtc: DateTime.UtcNow);

    private static H2AgentTaskSummary WaitTerminal(
        IH2AgentAdapter adapter,
        Guid taskId,
        int timeoutMs = 12_000)
    {
        H2AgentTaskSummary? last = null;
        WaitUntil(() =>
        {
            Pump();
            last = adapter.GetTaskSummary(taskId);
            return last.Status is H2AgentTaskStatus.Completed
                or H2AgentTaskStatus.Blocked
                or H2AgentTaskStatus.Cancelled
                or H2AgentTaskStatus.Failed;
        }, timeoutMs);
        return last!;
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs = 8_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            Pump();
            if (condition()) return;
            Thread.Sleep(15);
        }
        Pump();
        if (!condition()) throw new TimeoutException("Timed out waiting for H11 acceptance state.");
    }

    private static void SetStorage(App app, INoteStorage storage)
        => typeof(App).GetField("_storage", Private)!.SetValue(app, storage);

    private static object? CallPrivate(object owner, string name)
        => owner.GetType().GetMethod(name, Private)!.Invoke(owner, null);

    private static void CloseAssistant(App app)
    {
        try
        {
            ((Window?)typeof(App).GetField("_workAssistantCompact", Private)?.GetValue(app))?.Close();
            ((Window?)typeof(App).GetField("_workAssistantBubble", Private)?.GetValue(app))?.Close();
        }
        catch { }
        if (app.AgentAdapter is IDisposable disposable) disposable.Dispose();
        Pump();
    }

    private static void Pump()
    {
        for (var i = 0; i < 6; i++)
            Dispatcher.UIThread.RunJobs();
    }

    private static bool IsPixelTool(string name)
        => name.Contains("screen", StringComparison.OrdinalIgnoreCase)
           || name.Contains("pixel", StringComparison.OrdinalIgnoreCase)
           || name.Contains("click", StringComparison.OrdinalIgnoreCase)
           || name.Contains("uia.", StringComparison.OrdinalIgnoreCase)
           || name.Contains("computer", StringComparison.OrdinalIgnoreCase);

    private static string OfficeHostExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "H2AgentLab.OfficeHost.exe"),
            Path.GetFullPath(Path.Combine("experiments", "H2AgentLab.OfficeHost", "bin", "Release", "net10.0-windows", "H2AgentLab.OfficeHost.exe"))
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("AR-024 requires the built OfficeHost executable.");
    }

    private static string Temp(string name)
        => Path.Combine(Path.GetTempPath(), "h2-h11-" + name + "-" + Guid.NewGuid().ToString("N"));

    private static void Delete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private sealed class FixedContextCapture : IWorkAssistantActiveContextCapture
    {
        private readonly H2ActiveWorkContext _context;
        public FixedContextCapture(H2ActiveWorkContext context) => _context = context;
        public H2ActiveWorkContext? Capture() => _context;
        public bool Revalidate(H2ActiveWorkContext context) => context == _context;
    }

    private sealed class ProjectionAgent : IH2AgentAdapter
    {
        private readonly List<H2AgentTaskSummary> _tasks = [];
        public void Add(H2AgentTaskSummary task) => _tasks.Add(task);
        public Task<Guid> StartTaskAsync(Guid? projectId, string goal, H2AgentTaskContext? context = null, bool readOnly = true, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public H2AgentTaskObservation ObserveTask(Guid taskId, long afterSequence = -1)
            => new(GetTaskSummary(taskId), []);
        public void CancelTask(Guid taskId) => throw new NotSupportedException();
        public bool RespondToApproval(Guid taskId, Guid approvalId, bool approved) => false;
        public H2AgentTaskSummary GetTaskSummary(Guid taskId) => _tasks.Single(x => x.TaskId == taskId);
        public IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(Guid? projectId = null, int limit = 50)
            => _tasks.Where(x => projectId is null || x.ProjectId == projectId).Take(limit).ToArray();
        public H2AgentEvidence? GetEvidence(string evidenceId) => null;
        public bool AttachProject(Guid taskId, Guid projectId) => false;
    }

    private sealed record ScenarioAction(string Name, string ArgumentsJson);

    private sealed class ScriptedToolTransportFactory : IAgentTransportFactory
    {
        private readonly IReadOnlyList<ScenarioAction> _actions;
        private readonly string _finalText;
        private readonly int _delayMs;
        public ScriptedToolTransportFactory(
            IReadOnlyList<ScenarioAction> actions,
            string finalText,
            int delayMs = 35)
        {
            _actions = actions;
            _finalText = finalText;
            _delayMs = delayMs;
        }

        public IAgentTransport Create(AiProfile profile, string apiKey, AgentRunTelemetry telemetry)
            => new Transport(_actions, _finalText, _delayMs);

        private sealed class Transport : IAgentTransport
        {
            private readonly IReadOnlyList<ScenarioAction> _actions;
            private readonly string _finalText;
            private readonly int _delayMs;
            private int _index;
            private bool _awaitingAction;

            public Transport(IReadOnlyList<ScenarioAction> actions, string finalText, int delayMs)
            {
                _actions = actions;
                _finalText = finalText;
                _delayMs = delayMs;
            }

            public AgentTransportCapabilities Capabilities
                => AgentTransportCapabilities.ChatCompletionsFallback;

            public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
                AgentTransportStartRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Delay(cancellationToken);
                yield return AgentTransportEvent.Started("h11-start");
                if (_actions.Count == 0)
                {
                    yield return AgentTransportEvent.TextDeltaEvent(_finalText);
                    yield return AgentTransportEvent.Complete("h11-final", "stop");
                    yield break;
                }

                _awaitingAction = true;
                yield return Search(_actions[0]);
                yield return AgentTransportEvent.Complete("h11-search-0", "tool_calls");
            }

            public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
                AgentTransportContinuationRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Delay(cancellationToken);
                yield return AgentTransportEvent.Started("h11-cont-" + _index);

                if (_awaitingAction)
                {
                    var action = _actions[_index];
                    Check(request.NewlyLoadedTools?.Any(tool => tool.Name == action.Name) == true,
                        "Deferred discovery did not load scenario tool " + action.Name);
                    _awaitingAction = false;
                    yield return AgentTransportEvent.Tool(new AgentTransportToolCall(
                        "call-" + _index,
                        action.Name,
                        action.ArgumentsJson));
                    yield return AgentTransportEvent.Complete("h11-tool-" + _index, "tool_calls");
                    yield break;
                }

                _index++;
                if (_index >= _actions.Count)
                {
                    yield return AgentTransportEvent.TextDeltaEvent(_finalText);
                    yield return AgentTransportEvent.Complete("h11-final", "stop");
                    yield break;
                }

                _awaitingAction = true;
                yield return Search(_actions[_index]);
                yield return AgentTransportEvent.Complete("h11-search-" + _index, "tool_calls");
            }

            private static AgentTransportEvent Search(ScenarioAction action)
                => AgentTransportEvent.Tool(new AgentTransportToolCall(
                    "search-" + action.Name.Replace('.', '-'),
                    "tool_search",
                    JsonSerializer.Serialize(new { query = action.Name, max_results = 1 })));

            private async Task Delay(CancellationToken token)
            {
                if (_delayMs > 0) await Task.Delay(_delayMs, token);
            }

            public void Cancel() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingTransportFactory : IAgentTransportFactory
    {
        public IAgentTransport Create(AiProfile profile, string apiKey, AgentRunTelemetry telemetry)
            => new BlockingTransport();

        private sealed class BlockingTransport : IAgentTransport
        {
            public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.Minimal;
            public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
                AgentTransportStartRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                yield return AgentTransportEvent.Started("blocking");
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
                AgentTransportContinuationRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                yield break;
            }
            public void Cancel() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingExecutor : IAgentToolExecutor
    {
        public string ExecutorId => "h11-structured-fixture";
        public List<string> Calls { get; } = [];
        public List<(string Name, string Json)> Arguments { get; } = [];
        public List<string> Outputs { get; } = [];

        public ValueTask<string> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(call.Name);
            Arguments.Add((call.Name, call.Arguments.GetRawText()));
            var output = call.Name switch
            {
                "excel.read_formulas" => "{\"sessionId\":\"excel-session-01\",\"formulas\":[\"=A1+B1\"],\"stateToken\":\"excel-v1\"}",
                "excel.set_formula" => "{\"mutationId\":\"excel-mutation-1\",\"stateToken\":\"excel-v2\",\"changedCells\":12}",
                "excel.verify_range" => "{\"verified\":true,\"stateToken\":\"excel-v2\",\"absoluteFormulas\":true}",
                "word.get_spelling_errors" => "{\"sessionId\":\"word-unsaved-session\",\"saved\":false,\"errors\":[\"chinh ta\"]}",
                "word.extract_legal_citations" => "{\"citations\":[\"Nghị định 214/2025/NĐ-CP\"],\"stateToken\":\"word-v1\"}",
                "web.search" => "{\"results\":[{\"url\":\"https://vanban.chinhphu.vn/\",\"publisher\":\"Chính phủ\",\"authoritative\":true,\"freshness\":\"current-check\"}]}",
                "word.replace_range" => "{\"mutationId\":\"word-mutation-1\",\"sessionId\":\"word-unsaved-session\",\"stateToken\":\"word-v2\",\"saved\":false}",
                "word.verify_range" => "{\"verified\":true,\"sessionId\":\"word-unsaved-session\",\"stateToken\":\"word-v2\"}",
                "autocad.query_entities" => "[{\"documentSessionId\":\"dwg-01\",\"handle\":\"ABCD\",\"objectType\":\"INSERT\",\"layer\":\"OTC\",\"stateToken\":\"ent-v1\"}]",
                "autocad.read_attributes" => "{\"handle\":\"ABCD\",\"attributes\":{\"KM\":\"12+34\"},\"stateToken\":\"ent-v1\"}",
                "autocad.update_attribute" => "{\"mutationId\":\"cad-mutation-1\",\"entityHandle\":\"ABCD\",\"documentStateToken\":\"doc-v2\",\"entityStateToken\":\"ent-v2\"}",
                "autocad.verify_entity" => "{\"verified\":true,\"entityHandle\":\"ABCD\",\"documentStateToken\":\"doc-v2\",\"entityStateToken\":\"ent-v2\"}",
                _ => "{}"
            };
            Outputs.Add(output);
            return ValueTask.FromResult(output);
        }
    }

    private sealed class ScenarioRuntimeFactory : IAgentRuntimeFactory
    {
        private readonly IAgentTransportFactory _transport;
        private readonly Action<ToolRegistry> _configure;
        private readonly IAgentRuntimeVerifier _verifier;

        public ScenarioRuntimeFactory(
            IAgentTransportFactory transport,
            Action<ToolRegistry> configure,
            IAgentRuntimeVerifier verifier)
        {
            _transport = transport;
            _configure = configure;
            _verifier = verifier;
        }

        public AgentRuntime Create(
            AiProfile profile,
            string apiKey,
            AgentTools tools,
            AgentContextManager contextManager,
            AgentRunTelemetry telemetry)
        {
            var registry = NormalRuntimeToolRegistry.Create(tools);
            _configure(registry);
            return new AgentRuntime(
                _transport.Create(profile, apiKey, telemetry),
                contextManager,
                registry,
                verifier: _verifier,
                permissionPolicy: new ScopedAgentRuntimePermissionPolicy(_ => !tools.ReadOnly),
                evidenceProjector: new AgentRuntimeEvidenceProjector(new ArtifactStore(tools.StateRoot)));
        }
    }

    private sealed class ScenarioVerifier : IAgentRuntimeVerifier
    {
        private readonly HashSet<string> _verificationTools;
        public ScenarioVerifier(IEnumerable<string> tools)
            => _verificationTools = tools.ToHashSet(StringComparer.Ordinal);

        public Task<VerificationReport?> VerifyAsync(
            AgentRuntimeVerificationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.Calls.Any(call => _verificationTools.Contains(call.Name)))
                return Task.FromResult<VerificationReport?>(null);

            var evidence = context.Evidence.Select(item => item.ReferenceId).ToArray();
            return Task.FromResult<VerificationReport?>(new VerificationReport(
                AgentRuntimeDomainVerifierRouter.VerifierId,
                [
                    new VerificationCriterionResult(
                        AgentRuntimeDomainVerifierRouter.MutationCriterionId,
                        VerificationCriterionStatus.Passed,
                        evidence)
                ],
                evidence));
        }
    }
}

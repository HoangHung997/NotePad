using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using H2Notes.Avalonia;
using H2Notes.Core;

internal static class H2CommandCenterUiTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Command Center is the default project surface and shows all high-value fields", () =>
        {
            var now = DateTime.UtcNow;
            var first = Project("Hồ sơ A", now, completed: 1, total: 2);
            var second = Project("Hồ sơ B", now.AddMinutes(1), completed: 0, total: 1);
            var third = Project("Hồ sơ C", now.AddMinutes(2), completed: 2, total: 2);

            var boardA = new NoteRecord { Title = "Khối A", NoteKind = "project-hub", Projects = [first, second] };
            var boardB = new NoteRecord { Title = "Khối B", NoteKind = "project-hub", Projects = [third] };

            var app = new App { AgentAdapter = new CommandCenterAgentFake(second.Id, now.AddMinutes(5)) };
            app.State.Notes.Add(boardA);
            app.State.Notes.Add(boardB);

            var window = new MainWindow(app, boardA);
            window.Show();
            Pump();
            try
            {
                var center = window.FindControl<Grid>("CommandCenter")!;
                var work = window.FindControl<Grid>("WorkContent")!;
                Check(center.IsVisible, "Command Center is not the default project surface.");
                Check(!work.IsVisible, "Project editor should stay behind Command Center until a project opens.");

                var list = window.FindControl<ListBox>("CommandCenterList")!;
                var items = list.ItemsSource!.Cast<object>().ToArray();
                Check(items.Length == 3, "Command Center did not aggregate projects from all active boards.");

                var item = items.Single(value => Text(value, "Name") == "Hồ sơ B");
                Check(Text(item, "ProgressText") == "0/1 công việc", "Deterministic task completion is missing.");
                Check(Text(item, "NextText").Contains("Task 1", StringComparison.Ordinal), "Next project task is missing.");
                Check(Text(item, "AgentText").Contains("chờ phê duyệt", StringComparison.OrdinalIgnoreCase), "Agent state is missing.");
                Check(Text(item, "AttentionText") == "1 cần xem", "Attention count is missing.");
                Check(Text(item, "ActivityText") == "Chưa có hoạt động xác minh", "Latest verified activity is missing.");
                Check(Text(item, "SyncText") == "Đã đồng bộ", "Sync health is missing.");
                Check(window.FindControl<TextBlock>("CommandCenterSummary")!.Text!.Contains("3 dự án", StringComparison.Ordinal), "Global project summary is missing.");
                Check(window.FindControl<TextBlock>("CommandCenterSync")!.Text == "Đã đồng bộ", "Global sync summary is missing.");
            }
            finally
            {
                window.Close();
            }
        });

        test("Command Center project selection opens existing workspace and Projects nav returns to overview", () =>
        {
            var now = DateTime.UtcNow;
            var projectA = Project("Project A", now, 0, 1);
            var projectB = Project("Project B", now.AddMinutes(1), 1, 2);
            var boardA = new NoteRecord { Title = "Board A", NoteKind = "project-hub", Projects = [projectA] };
            var boardB = new NoteRecord { Title = "Board B", NoteKind = "project-hub", Projects = [projectB] };

            var app = new App();
            app.State.Notes.Add(boardA);
            app.State.Notes.Add(boardB);

            var window = new MainWindow(app, boardA);
            window.Show();
            Pump();
            try
            {
                var list = window.FindControl<ListBox>("CommandCenterList")!;
                var target = list.ItemsSource!.Cast<object>()
                    .Single(value => Text(value, "Name") == "Project B");

                list.SelectedItem = target;
                Pump();

                Check(window.BoardId == boardB.Id, "Cross-board project selection did not switch the owning board.");
                Check(window.SelectedProjectId == projectB.Id, "Command Center selection did not open the selected project.");
                Check(!window.FindControl<Grid>("CommandCenter")!.IsVisible, "Command Center stayed visible after opening a project.");
                Check(window.FindControl<Grid>("WorkContent")!.IsVisible, "Existing project workspace did not become visible.");

                window.FindControl<Button>("ProjectsNavButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();

                Check(window.FindControl<Grid>("CommandCenter")!.IsVisible, "Projects navigation did not return to Command Center.");
                Check(!window.FindControl<Grid>("WorkContent")!.IsVisible, "Project workspace stayed visible over Command Center.");
                Check(window.FindControl<ListBox>("CommandCenterList")!.ItemsSource!.Cast<object>().Count() == 2,
                    "Command Center did not remain a global overview after cross-board navigation.");
            }
            finally
            {
                window.Close();
            }
        });

        test("Needs attention deep-links to source and disappears when source resolves", () =>
        {
            var now = DateTime.UtcNow;
            var project = Project("Attention project", now, 0, 1);
            var board = new NoteRecord { Title = "Attention board", NoteKind = "project-hub", Projects = [project] };
            var agent = new CommandCenterAgentFake(project.Id, now.AddMinutes(5));
            var app = new App { AgentAdapter = agent };
            app.State.Notes.Add(board);

            var window = new MainWindow(app, board);
            window.Show();
            Pump();
            try
            {
                var section = window.FindControl<Border>("CommandCenterAttentionSection")!;
                var list = window.FindControl<ListBox>("CommandCenterAttentionList")!;
                Check(section.IsVisible, "Needs-attention section is hidden despite a waiting approval.");
                window.FindControl<Button>("CommandCenterAttentionToggle")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                Check(list.IsVisible, "Needs-attention section did not expand for deep-link selection.");
                var item = list.ItemsSource!.Cast<object>().Single();

                Check((Guid?)Value(item, "ProjectId") == project.Id, "Attention item lost ProjectId source.");
                Check((Guid?)Value(item, "AgentTaskId") == agent.TaskId, "Attention item lost AgentTaskId source.");
                Check(Text(item, "Code") == "agent-waiting-approval", "Wrong attention source code.");
                Check(Text(item, "Title") == "Approve file change", "Approval title is not projected.");
                Check(Text(item, "SourceText").Contains("Attention project", StringComparison.Ordinal),
                    "Attention item does not describe its real project source.");

                list.SelectedItem = item;
                Pump();
                Check(window.SelectedProjectId == project.Id && !window.FindControl<Grid>("CommandCenter")!.IsVisible,
                    "Attention item did not deep-link to its source project.");

                window.FindControl<Button>("ProjectsNavButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                agent.Resolve();
                window.RefreshAfterSave();
                Pump();

                Check(!window.FindControl<Border>("CommandCenterAttentionSection")!.IsVisible,
                    "Resolved source left a stale attention section.");
                Check(window.FindControl<ListBox>("CommandCenterAttentionList")!.ItemsSource!.Cast<object>().Count() == 0,
                    "Resolved source left a stale attention row.");
                Check(window.FindControl<TextBlock>("CommandCenterSummary")!.Text!.Contains("0 cần xem", StringComparison.Ordinal),
                    "Global attention summary did not rebuild after resolution.");
            }
            finally
            {
                window.Close();
            }
        });

        test("AR-068 attention defaults collapsed, bounds preview, and remembers machine-local collapse preference", () =>
        {
            var now = DateTime.UtcNow;
            var project = Project("Attention flood", now, 0, 1);
            var board = new NoteRecord { Title = "Attention board", NoteKind = "project-hub", Projects = [project] };
            var agent = new CommandCenterAttentionAgentFake(project.Id, now.AddMinutes(5), 21);
            var app = new App { AgentAdapter = agent };
            app.LocalSettings.DeviceId = "ar068-test-device";
            app.State.Notes.Add(board);

            var window = new MainWindow(app, board);
            window.Show();
            Pump();
            try
            {
                var section = window.FindControl<Border>("CommandCenterAttentionSection")!;
                var list = window.FindControl<ListBox>("CommandCenterAttentionList")!;
                var toggle = window.FindControl<Button>("CommandCenterAttentionToggle")!;
                var showAll = window.FindControl<Button>("CommandCenterAttentionShowAllButton")!;

                Check(section.IsVisible, "Attention section should remain visible while collapsed.");
                Check(app.LocalSettings.CommandCenter.AttentionCollapsed, "Attention did not default collapsed.");
                Check(!list.IsVisible && list.ItemsSource!.Cast<object>().Count() == 0,
                    "Collapsed attention leaked rows into the dashboard.");
                Check(window.FindControl<TextBlock>("CommandCenterAttentionHeader")!.Text == "Cần bạn xử lý · 21",
                    "Collapsed header lost the active attention count.");

                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                Check(!app.LocalSettings.CommandCenter.AttentionCollapsed && list.IsVisible,
                    "Attention section did not expand.");
                Check(list.ItemsSource!.Cast<object>().Count() == 5,
                    "Expanded attention preview was not bounded to five rows.");
                Check(showAll.IsVisible && showAll.Content?.ToString() == "Xem tất cả · 21",
                    "Long attention preview did not offer Xem tất cả.");

                showAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                Check(list.ItemsSource!.Cast<object>().Count() == 21 && !showAll.IsVisible,
                    "Xem tất cả did not reveal exactly the active attention rows.");

                var projectItem = window.FindControl<ListBox>("CommandCenterList")!.ItemsSource!.Cast<object>().Single();
                window.FindControl<ListBox>("CommandCenterList")!.SelectedItem = projectItem;
                Pump();
                window.FindControl<Button>("ProjectsNavButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                Check(list.IsVisible && list.ItemsSource!.Cast<object>().Count() == 5 && showAll.IsVisible,
                    "Returning to Command Center did not restore the bounded attention preview.");

                var reloaded = LocalConfiguration.Read();
                Check(!reloaded.CommandCenter.AttentionCollapsed,
                    "Expanded/collapsed preference was not persisted machine-locally.");
            }
            finally
            {
                window.Close();
            }

            var reopened = new MainWindow(app, board);
            reopened.Show();
            Pump();
            try
            {
                var list = reopened.FindControl<ListBox>("CommandCenterAttentionList")!;
                Check(list.IsVisible && list.ItemsSource!.Cast<object>().Count() == 5,
                    "Reopened Command Center did not restore expanded preference with bounded preview.");
            }
            finally
            {
                reopened.Close();
            }
        });

        test("AR-068 selecting one attention acknowledges only that event and a new same-task failure reappears", () =>
        {
            var now = DateTime.UtcNow;
            var project = Project("Acknowledgement project", now, 0, 1);
            var board = new NoteRecord { Title = "Attention board", NoteKind = "project-hub", Projects = [project] };
            var agent = new CommandCenterAttentionAgentFake(project.Id, now.AddMinutes(5), 2);
            var app = new App { AgentAdapter = agent };
            app.LocalSettings.DeviceId = "ar068-ack-device";
            app.LocalSettings.CommandCenter.AttentionCollapsed = false;
            app.State.Notes.Add(board);

            var window = new MainWindow(app, board);
            window.Show();
            Pump();
            try
            {
                var list = window.FindControl<ListBox>("CommandCenterAttentionList")!;
                var items = list.ItemsSource!.Cast<object>().ToArray();
                Check(items.Length == 2, "Acknowledgement fixture did not expose two independent attention events.");
                var item = items.Single(value => (Guid?)Value(value, "AgentTaskId") == agent.PrimaryTaskId);
                var other = items.Single(value => (Guid?)Value(value, "AgentTaskId") != agent.PrimaryTaskId);
                var attentionId = Text(item, "AttentionId");
                var otherAttentionId = Text(other, "AttentionId");

                list.SelectedItem = item;
                Pump();

                Check(agent.GetTaskSummary(agent.PrimaryTaskId).Status == H2AgentTaskStatus.WaitingForApproval,
                    "Acknowledgement mutated authoritative Agent task status.");
                Check(app.LocalSettings.CommandCenter.AttentionAcknowledgedUtc.ContainsKey(attentionId),
                    "Selected attention event was not acknowledged locally.");

                window.FindControl<Button>("ProjectsNavButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                Check(window.FindControl<Border>("CommandCenterAttentionSection")!.IsVisible,
                    "Acknowledging one event incorrectly hid the remaining independent event.");
                var remaining = window.FindControl<ListBox>("CommandCenterAttentionList")!.ItemsSource!.Cast<object>().ToArray();
                Check(remaining.Length == 1 && Text(remaining[0], "AttentionId") == otherAttentionId,
                    "Acknowledgement removed or replaced an unrelated attention event.");
                Check(window.FindControl<TextBlock>("CommandCenterSummary")!.Text!.Contains("1 cần xem", StringComparison.Ordinal),
                    "Acknowledged event did not decrement active attention count by exactly one.");

                agent.TouchPrimaryWaiting(now.AddMinutes(10));
                window.RefreshAfterSave();
                Pump();
                remaining = window.FindControl<ListBox>("CommandCenterAttentionList")!.ItemsSource!.Cast<object>().ToArray();
                Check(remaining.Length == 1 && Text(remaining[0], "AttentionId") == otherAttentionId,
                    "Same pending approval resurfaced only because task UpdatedUtc changed.");

                agent.FailPrimary(now.AddMinutes(30));
                window.RefreshAfterSave();
                Pump();

                var replacementItems = window.FindControl<ListBox>("CommandCenterAttentionList")!.ItemsSource!.Cast<object>().ToArray();
                Check(replacementItems.Length == 2,
                    "New same-task failure did not reappear alongside the unrelated event.");
                var replacement = replacementItems.Single(value => (Guid?)Value(value, "AgentTaskId") == agent.PrimaryTaskId);
                var replacementId = Text(replacement, "AttentionId");
                Check(replacementId != attentionId,
                    "New failure revision reused an acknowledged attention identity.");
                Check(agent.GetTaskSummary(agent.PrimaryTaskId).Status == H2AgentTaskStatus.Failed,
                    "New failure was hidden by mutating Agent history.");
                Check(!app.LocalSettings.CommandCenter.AttentionAcknowledgedUtc.ContainsKey(attentionId),
                    "Resolved/superseded attention acknowledgement was not pruned.");
            }
            finally
            {
                window.Close();
            }
        });

        test("AR-068 source resolution disappears without acknowledgement and overlay never enters shared project JSON", () =>
        {
            var now = DateTime.UtcNow;
            var project = Project("Resolved condition", now, 0, 1);
            var board = new NoteRecord { Title = "Attention board", NoteKind = "project-hub", Projects = [project] };
            var agent = new CommandCenterAttentionAgentFake(project.Id, now.AddMinutes(5), 1);
            var app = new App { AgentAdapter = agent };
            app.LocalSettings.CommandCenter.AttentionCollapsed = false;
            app.State.Notes.Add(board);

            var window = new MainWindow(app, board);
            window.Show();
            Pump();
            try
            {
                Check(window.FindControl<Border>("CommandCenterAttentionSection")!.IsVisible,
                    "Fixture did not expose active attention before resolution.");
                Check(app.LocalSettings.CommandCenter.AttentionAcknowledgedUtc.Count == 0,
                    "Fixture unexpectedly started acknowledged.");

                agent.ResolvePrimary();
                window.RefreshAfterSave();
                Pump();

                Check(!window.FindControl<Border>("CommandCenterAttentionSection")!.IsVisible,
                    "Resolved source condition remained in active attention without an acknowledgement.");
                Check(app.LocalSettings.CommandCenter.AttentionAcknowledgedUtc.Count == 0,
                    "Source resolution created acknowledgement metadata.");
                var shared = System.Text.Json.JsonSerializer.Serialize(app.State);
                Check(!shared.Contains("AttentionAcknowledgedUtc", StringComparison.Ordinal)
                      && !shared.Contains("AttentionCollapsed", StringComparison.Ordinal),
                    "Machine-local attention overlay leaked into shared project/NAS state.");
                var localJson = System.Text.Json.JsonSerializer.Serialize(app.LocalSettings);
                Check(localJson.Contains("AttentionAcknowledgedUtc", StringComparison.Ordinal)
                      && localJson.Contains("AttentionCollapsed", StringComparison.Ordinal),
                    "Attention overlay is not owned by LocalConfiguration.");
            }
            finally
            {
                window.Close();
            }
        });

        test("Command Center derived classifier covers all five groups without persisted board status", () =>
        {
            ProjectOverviewProjection P(
                int done,
                int total,
                H2AgentTaskStatus? status = null,
                int attention = 0)
                => new(
                    Guid.NewGuid(),
                    "fixture",
                    done,
                    total,
                    null,
                    null,
                    status,
                    attention,
                    null,
                    H2WorkspaceSyncState.Healthy);

            Check(H2CommandCenterQueryService.GroupFor(P(0, 2, H2AgentTaskStatus.WaitingForApproval, attention: 1))
                == H2CommandCenterGroup.NeedsAttention, "NeedsAttention classifier failed.");
            Check(H2CommandCenterQueryService.GroupFor(P(0, 2, H2AgentTaskStatus.Running))
                == H2CommandCenterGroup.Working, "Working classifier failed.");
            Check(H2CommandCenterQueryService.GroupFor(P(0, 2, H2AgentTaskStatus.Queued))
                == H2CommandCenterGroup.Waiting, "Waiting classifier failed.");
            Check(H2CommandCenterQueryService.GroupFor(P(1, 2))
                == H2CommandCenterGroup.Normal, "Normal classifier failed.");
            Check(H2CommandCenterQueryService.GroupFor(P(2, 2, H2AgentTaskStatus.Completed))
                == H2CommandCenterGroup.Completed, "Completed classifier failed.");

            foreach (var type in new[] { typeof(ProjectRecord), typeof(TaskRecord) })
                foreach (var name in new[] { "BoardStatus", "CommandCenterGroup", "GroupStatus", "DashboardStatus" })
                    Check(type.GetProperty(name) is null && type.GetField(name) is null,
                        $"Persisted grouping field leaked into {type.Name}: {name}");
        });

        test("Command Center group filter changes only the projection and never project JSON", () =>
        {
            var now = DateTime.UtcNow;
            var attention = Project("Needs review", now, 0, 1);
            var completed = Project("Done", now.AddMinutes(1), 2, 2);
            var normal = Project("Normal", now.AddMinutes(2), 1, 2);
            var board = new NoteRecord
            {
                Title = "Grouping board",
                NoteKind = "project-hub",
                Projects = [attention, completed, normal]
            };

            var agent = new CommandCenterAgentFake(attention.Id, now.AddMinutes(5));
            var app = new App { AgentAdapter = agent };
            app.State.Notes.Add(board);

            var window = new MainWindow(app, board);
            window.Show();
            Pump();
            try
            {
                // MainWindow initialization legitimately updates board/session metadata.
                // The grouping guarantee is narrower: changing the derived filter must not
                // mutate durable ProjectRecord/TaskRecord truth.
                var before = System.Text.Json.JsonSerializer.Serialize(board.Projects);
                var filter = window.FindControl<ComboBox>("CommandCenterGroupFilter")!;
                var list = window.FindControl<ListBox>("CommandCenterList")!;
                Check(filter.ItemsSource!.Cast<object>().Count() == 6,
                    "Expected All plus five derived group filters.");

                var completedFilter = filter.ItemsSource!.Cast<object>()
                    .Single(item => Text(item, "Text") == "Hoàn thành");
                filter.SelectedItem = completedFilter;
                Pump();

                var completedItems = list.ItemsSource!.Cast<object>().ToArray();
                Check(completedItems.Length == 1 && Text(completedItems[0], "Name") == "Done",
                    "Completed filter did not derive from project/Agent truth.");

                var attentionFilter = filter.ItemsSource!.Cast<object>()
                    .Single(item => Text(item, "Text") == "Cần xử lý");
                filter.SelectedItem = attentionFilter;
                Pump();

                var attentionItems = list.ItemsSource!.Cast<object>().ToArray();
                Check(attentionItems.Length == 1 && Text(attentionItems[0], "Name") == "Needs review",
                    "Needs-attention filter did not derive from source projection.");

                var allFilter = filter.ItemsSource!.Cast<object>()
                    .Single(item => Text(item, "Text") == "Tất cả");
                filter.SelectedItem = allFilter;
                Pump();
                Check(list.ItemsSource!.Cast<object>().Count() == 3,
                    "All filter did not restore all projected projects.");

                var after = System.Text.Json.JsonSerializer.Serialize(board.Projects);
                Check(before == after, "Filtering mutated persisted ProjectRecord/TaskRecord truth.");
            }
            finally
            {
                window.Close();
            }
        });

        test("Sync health UI comes from storage lifecycle state, never Agent/model text", () =>
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "H2Notes-sync-health-ui",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var store = new ProjectWorkspaceStore(root);
            store.LoadOrImport();
            var state = SheetStorage.Demo();
            store.Save(state);
            var board = state.Notes.First(note => note.IsBoard);
            var project = board.Projects[0];

            var agent = new CommandCenterAgentFake(project.Id, DateTime.UtcNow);
            agent.Resolve();
            var app = new App { AgentAdapter = agent };
            typeof(App).GetProperty("State")!.SetValue(app, state);
            typeof(App).GetField("_storage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(app, store);

            Check(app.CurrentWorkspaceHealth.State == H2WorkspaceSyncState.Healthy,
                "Clean store did not project Healthy.");

            typeof(App).GetField("_saving", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(app, true);
            Check(app.CurrentWorkspaceHealth.State == H2WorkspaceSyncState.Busy
                && app.CurrentWorkspaceHealth.Code == "saving",
                "Real host saving state did not project Busy/Saving.");

            typeof(App).GetField("_saving", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(app, false);
            store.RecordSyncFailure(new IOException("network unavailable"));
            Check(app.CurrentWorkspaceHealth.State == H2WorkspaceSyncState.Offline,
                "Storage I/O failure did not project Offline.");

            var window = new MainWindow(app, board);
            window.Show();
            Pump();
            try
            {
                // The Agent task says it is resolved/completed; that must not override storage truth.
                Check(window.FindControl<TextBlock>("CommandCenterSync")!.Text == "Ngoại tuyến",
                    "Agent/model state overrode offline storage health.");

                var projectItem = window.FindControl<ListBox>("CommandCenterList")!.ItemsSource!
                    .Cast<object>().Single(value => (Guid?)Value(value, "ProjectId") == project.Id);
                Check(Text(projectItem, "SyncText") == "Ngoại tuyến",
                    "Project card did not use storage health projection.");

                var nested = typeof(MainWindow).GetNestedType(
                    "CommandCenterProjectItem",
                    System.Reflection.BindingFlags.NonPublic)!;
                var method = nested.GetMethod(
                    "SyncTextFor",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;

                string Map(H2WorkspaceSyncState value)
                    => (string)method.Invoke(null, [value])!;

                Check(Map(H2WorkspaceSyncState.Healthy) == "Đã đồng bộ", "Healthy label is wrong.");
                Check(Map(H2WorkspaceSyncState.Busy) == "Đang lưu", "Saving label is wrong.");
                Check(Map(H2WorkspaceSyncState.Offline) == "Ngoại tuyến", "Offline label is wrong.");
                Check(Map(H2WorkspaceSyncState.RecoveryRequired) == "Lỗi · cần phục hồi",
                    "Recovery-required label is wrong.");
                Check(Map(H2WorkspaceSyncState.Warning) == "Lỗi đồng bộ",
                    "Sync-error label is wrong.");
            }
            finally
            {
                window.Close();
                try { Directory.Delete(root, true); } catch { }
            }
        });

        test("Command Center XAML binds only the required high-value overview fields", () =>
        {
            var repo = FindRepoRoot();
            var xaml = File.ReadAllText(Path.Combine(repo, "src", "H2Notes.Avalonia", "MainWindow.axaml"));
            foreach (var binding in new[]
            {
                "{Binding Name}",
                "{Binding ProgressText}",
                "{Binding NextText}",
                "{Binding AgentText}",
                "{Binding AttentionText}",
                "{Binding ActivityText}",
                "{Binding SyncText}"
            })
                Check(xaml.Contains(binding, StringComparison.Ordinal), "Missing Command Center binding: " + binding);

            var source = File.ReadAllText(Path.Combine(repo, "src", "H2Notes.Avalonia", "MainWindow.CommandCenter.cs"));
            Check(source.Contains("H2CommandCenterQueryService", StringComparison.Ordinal),
                "Command Center UI bypasses the projection/query service.");
            Check(source.Contains("GetNeedsAttention", StringComparison.Ordinal)
                && source.Contains("_commandCenterRefreshTimer", StringComparison.Ordinal),
                "Command Center attention is not rebuilt from live projection sources.");
            Check(source.Contains("CommandCenterGroupFilter", StringComparison.Ordinal)
                && source.Contains("H2CommandCenterQueryService.GroupFor", StringComparison.Ordinal),
                "Command Center grouping/filtering is not projection-derived.");
            Check(source.Contains("SyncTextFor(health.State)", StringComparison.Ordinal),
                "Command Center sync badge is not derived from workspace health.");
            Check(source.Contains("AgentTaskId", StringComparison.Ordinal)
                && source.Contains("ProjectId", StringComparison.Ordinal),
                "Attention deep-link source identifiers are missing.");
            foreach (var forbidden in new[] { "ToolRegistry", "AgentRuntime", "IAgentTransport", "File.Write", "AtomicWrite(", "AttentionDatabase", "AiInboxStore", "BoardStatus =", "CommandCenterGroup =" })
                Check(!source.Contains(forbidden, StringComparison.Ordinal),
                    "Command Center UI contains forbidden runtime/persistence marker: " + forbidden);
        });
    }

    private static ProjectRecord Project(string name, DateTime updated, int completed, int total)
    {
        var project = new ProjectRecord
        {
            Name = name,
            CreatedAtUtc = updated.AddMinutes(-10),
            UpdatedAtUtc = updated
        };
        for (var i = 0; i < total; i++)
        {
            var done = i < completed;
            project.ChecklistItems.Add(new TaskRecord
            {
                Text = "Task " + (i + 1),
                IsCompleted = done,
                CreatedAtUtc = updated.AddMinutes(-9 + i),
                UpdatedAtUtc = updated.AddMinutes(-5 + i),
                CompletedAtUtc = done ? updated.AddMinutes(-4 + i) : null
            });
        }
        return project;
    }

    private static object? Value(object value, string property)
        => value.GetType().GetProperty(property)!.GetValue(value);

    private static string Text(object value, string property)
        => Value(value, property)?.ToString() ?? "";

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
            Dispatcher.UIThread.RunJobs();
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

    private sealed class CommandCenterAttentionAgentFake : IH2AgentAdapter
    {
        private readonly List<H2AgentTaskSummary> _tasks;

        public Guid PrimaryTaskId => _tasks[0].TaskId;

        public CommandCenterAttentionAgentFake(Guid projectId, DateTime updated, int count)
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
            _tasks = Enumerable.Range(0, count)
                .Select(index =>
                {
                    var at = updated.AddSeconds(index);
                    return new H2AgentTaskSummary(
                        Guid.NewGuid(),
                        projectId,
                        "Review attention " + (index + 1),
                        H2AgentTaskStatus.WaitingForApproval,
                        new H2AgentApproval(Guid.NewGuid(), "Approve fixture " + (index + 1), "Fixture", at),
                        [],
                        null,
                        null,
                        at.AddMinutes(-1),
                        at);
                })
                .ToList();
        }

        public Task<Guid> StartTaskAsync(Guid? projectId, string goal, H2AgentTaskContext? context = null, bool readOnly = true, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public H2AgentTaskObservation ObserveTask(Guid taskId, long afterSequence = -1)
            => new(GetTaskSummary(taskId), Array.Empty<H2AgentProgress>());

        public void CancelTask(Guid taskId) => throw new NotSupportedException();
        public bool RespondToApproval(Guid taskId, Guid approvalId, bool approved) => false;

        public H2AgentTaskSummary GetTaskSummary(Guid taskId)
            => _tasks.Single(task => task.TaskId == taskId);

        public IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(Guid? projectId = null, int limit = 50)
            => _tasks.Where(task => projectId is null || projectId == task.ProjectId)
                .OrderByDescending(task => task.UpdatedUtc)
                .Take(limit)
                .ToArray();

        public H2AgentEvidence? GetEvidence(string evidenceId) => null;
        public bool AttachProject(Guid taskId, Guid projectId) => false;

        public void TouchPrimaryWaiting(DateTime updated)
        {
            _tasks[0] = _tasks[0] with { UpdatedUtc = updated };
        }

        public void ResolvePrimary()
        {
            _tasks[0] = _tasks[0] with
            {
                Status = H2AgentTaskStatus.Completed,
                PendingApproval = null,
                Error = null,
                FinalText = "Resolved",
                UpdatedUtc = _tasks[0].UpdatedUtc.AddMinutes(1)
            };
        }

        public void FailPrimary(DateTime updated)
        {
            _tasks[0] = _tasks[0] with
            {
                Status = H2AgentTaskStatus.Failed,
                PendingApproval = null,
                Error = "New specific failure",
                FinalText = null,
                UpdatedUtc = updated
            };
        }
    }

    private sealed class CommandCenterAgentFake : IH2AgentAdapter
    {
        private H2AgentTaskSummary _task;

        public Guid TaskId => _task.TaskId;

        public CommandCenterAgentFake(Guid projectId, DateTime updated)
        {
            _task = new H2AgentTaskSummary(
                Guid.NewGuid(),
                projectId,
                "Review project",
                H2AgentTaskStatus.WaitingForApproval,
                new H2AgentApproval(Guid.NewGuid(), "Approve file change", "Fixture", updated),
                [new H2AgentEvidence("command-center-evidence", "fixture", new string('a', 64), "Verified fixture")],
                null,
                null,
                updated.AddMinutes(-1),
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

        public H2AgentEvidence? GetEvidence(string evidenceId)
            => _task.Evidence.FirstOrDefault(evidence => evidence.EvidenceId == evidenceId);

        public bool AttachProject(Guid taskId, Guid projectId) => false;

        public void Resolve()
        {
            _task = _task with
            {
                Status = H2AgentTaskStatus.Completed,
                PendingApproval = null,
                UpdatedUtc = DateTime.UtcNow,
                FinalText = "Resolved"
            };
        }
    }
}

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
                Check(Text(item, "ActivityText").StartsWith("Hoạt động mới nhất:", StringComparison.Ordinal), "Latest verified activity is missing.");
                Check(Text(item, "SyncText") == "Đồng bộ: ổn", "Sync health is missing.");
                Check(window.FindControl<TextBlock>("CommandCenterSummary")!.Text!.Contains("3 dự án", StringComparison.Ordinal), "Global project summary is missing.");
                Check(window.FindControl<TextBlock>("CommandCenterSync")!.Text == "Đồng bộ: ổn", "Global sync summary is missing.");
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

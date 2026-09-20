using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using H2Notes.Avalonia;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

internal static class H2TypedProjectToolTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Typed project host validates read-only confirm approval scope and stale version", () =>
        {
            var now = new DateTime(2026, 9, 20, 1, 2, 3, DateTimeKind.Utc);
            var project = new ProjectRecord
            {
                Name = "Typed tool project",
                Notes = "Current note",
                UpdatedAtUtc = now,
                ChecklistItems = [new TaskRecord { Text = "Existing task", UpdatedAtUtc = now }]
            };
            var changed = 0;
            var host = new H2ProjectToolHost(
                id => id == project.Id ? project : null,
                _ => changed++);

            var summary = host.ReadProject(project.Id);
            Check(summary.ProjectId == project.Id
                && summary.Version == now.Ticks
                && summary.Name == "Typed tool project"
                && summary.Tasks.Count == 1,
                "Typed read_project summary is wrong.");
            Check(changed == 0, "Read project unexpectedly marked project dirty.");

            Reject(() => host.AddTask(new(
                project.Id,
                summary.Version,
                "Should not happen",
                new H2ProjectToolAuthorization(AiPermissionMode.ReadOnly))),
                "Read-only mutation was accepted.");

            Reject(() => host.AddTask(new(
                project.Id,
                summary.Version,
                "Needs approval",
                new H2ProjectToolAuthorization(AiPermissionMode.ConfirmChanges, Approved: false))),
                "ConfirmChanges mutation without approval was accepted.");

            var receipt = host.AddTask(new(
                project.Id,
                summary.Version,
                "Approved typed task",
                new H2ProjectToolAuthorization(
                    AiPermissionMode.ConfirmChanges,
                    Approved: true,
                    AgentTaskId: Guid.NewGuid())));

            Check(project.ChecklistItems.Count == 2
                && project.ChecklistItems[^1].DisplayText == "Approved typed task",
                "Approved typed task was not added.");
            Check(receipt.Tool == "add_project_task"
                && receipt.EntityId == project.ChecklistItems[^1].Id
                && receipt.Version == project.UpdatedAtUtc!.Value.Ticks
                && receipt.Version > receipt.PreviousVersion,
                "Typed task mutation receipt is wrong.");
            Check(changed == 1, "Typed mutation callback count is wrong.");

            Reject(() => host.AppendNote(new(
                project.Id,
                summary.Version,
                "stale write",
                new H2ProjectToolAuthorization(AiPermissionMode.ProjectAccess))),
                "Stale project version did not block mutation.");

            Reject(() => host.ReadProject(Guid.NewGuid()),
                "Unknown project scope was accepted.");
        });

        test("Typed project host supports bounded add task append note replace note only", () =>
        {
            var project = new ProjectRecord
            {
                Name = "Typed project",
                NotesRich = RichDocument.Plain("Alpha unique sentence.\nKeep this."),
                UpdatedAtUtc = new DateTime(2026, 9, 20, 2, 0, 0, DateTimeKind.Utc)
            };
            var host = new H2ProjectToolHost(id => id == project.Id ? project : null);

            var version = host.ReadProject(project.Id).Version;
            var add = host.AddTask(new(
                project.Id,
                version,
                "Review typed tools",
                new H2ProjectToolAuthorization(AiPermissionMode.ProjectAccess)));
            version = add.Version;

            var append = host.AppendNote(new(
                project.Id,
                version,
                "Agent typed note",
                new H2ProjectToolAuthorization(AiPermissionMode.ProjectAccess)));
            version = append.Version;

            var replace = host.ReplaceNote(new(
                project.Id,
                version,
                "Alpha unique sentence.",
                "Alpha updated safely.",
                new H2ProjectToolAuthorization(AiPermissionMode.ProjectAccess)));

            Check(project.ChecklistItems.Single().DisplayText == "Review typed tools",
                "Typed add_task result missing.");
            Check(project.NotesText.Contains("Alpha updated safely.", StringComparison.Ordinal)
                && project.NotesText.Contains("Agent typed note", StringComparison.Ordinal)
                && !project.NotesText.Contains("Alpha unique sentence.", StringComparison.Ordinal),
                "Typed note mutations are wrong.");
            Check(replace.Tool == "replace_project_note"
                && replace.Version == project.UpdatedAtUtc!.Value.Ticks,
                "Typed note mutation receipt is wrong.");

            var ambiguous = new ProjectRecord
            {
                NotesRich = RichDocument.Plain("same same"),
                UpdatedAtUtc = DateTime.UtcNow
            };
            var ambiguousHost = new H2ProjectToolHost(id => id == ambiguous.Id ? ambiguous : null);
            Reject(() => ambiguousHost.ReplaceNote(new(
                ambiguous.Id,
                ambiguous.UpdatedAtUtc!.Value.Ticks,
                "same",
                "x",
                new H2ProjectToolAuthorization(AiPermissionMode.ProjectAccess))),
                "Ambiguous note match was accepted.");
        });

        test("Typed project tool boundary exposes no unrestricted ProjectRecord mutation or Agent runtime types", () =>
        {
            foreach (var method in typeof(IH2ProjectToolHost).GetMethods())
            {
                var types = method.GetParameters().Select(p => p.ParameterType)
                    .Append(method.ReturnType)
                    .ToArray();
                Check(types.All(type => type != typeof(ProjectRecord)),
                    "IH2ProjectToolHost exposed ProjectRecord directly: " + method.Name);
            }

            var repo = FindRepoRoot();
            var source = File.ReadAllText(Path.Combine(repo, "src", "H2Notes.Core", "H2ProjectTools.cs"));
            foreach (var forbidden in new[]
            {
                "ToolRegistry", "AgentRuntime", "AgentOrchestrator",
                "AiProjectAction", "JsonDocument", "Regex", "Process.Start"
            })
                Check(!source.Contains(forbidden, StringComparison.Ordinal),
                    "Typed project tool host contains forbidden marker: " + forbidden);

            Check(typeof(IH2ProjectToolHost).GetMethod("ReadProject") is not null
                && typeof(IH2ProjectToolHost).GetMethod("AddTask") is not null
                && typeof(IH2ProjectToolHost).GetMethod("AppendNote") is not null
                && typeof(IH2ProjectToolHost).GetMethod("ReplaceNote") is not null
                && typeof(IH2ProjectToolHost).GetMethods().Length == 4,
                "Typed project tool surface expanded beyond the minimal allowlist.");
        });

        test("App binds typed project host only through optional Agent adapter capability", () =>
        {
            var app = new App();
            var adapter = new ToolAwareAdapter();
            app.AgentAdapter = adapter;

            Check(adapter.Host is not null
                && ReferenceEquals(adapter.Host, app.ProjectToolHost),
                "App did not bind its host-owned typed project tools to capable Agent adapter.");

            var plain = new PlainAdapter();
            app.AgentAdapter = plain;
            Check(ReferenceEquals(app.AgentAdapter, plain),
                "Plain Agent adapter assignment failed.");
        });

        test("H2 Agent fenced h2-actions text is never parsed or applied as project mutation", () =>
        {
            var project = new ProjectRecord
            {
                Name = "Typed-only Agent project",
                UpdatedAtUtc = DateTime.UtcNow
            };
            var adapter = new PseudoTextAgentAdapter(project.Id);
            var app = new App { AgentAdapter = adapter };
            var panel = new AiChatPanel(app);
            panel.SetProject(project);

            var window = new Window { Width = 720, Height = 640, Content = panel };
            window.Show();
            Pump();
            try
            {
                var composer = panel.GetVisualDescendants().OfType<TextBox>()
                    .Single(c => c.Name == "ChatComposer");
                composer.Text = "Add a task safely.";
                panel.GetVisualDescendants().OfType<Button>()
                    .Single(b => b.Name == "ChatSend")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                WaitUntil(() => project.Conversations.Count == 1
                    && project.Conversations[0].Messages.Count == 2
                    && project.Conversations[0].Messages[^1].Status == "complete");

                var answer = project.Conversations[0].Messages[^1];
                Check(answer.Provider == "H2 Agent"
                    && answer.Content.Contains("h2-actions", StringComparison.Ordinal),
                    "Fixture did not deliver pseudo-action text through H2 Agent.");
                Check(project.ChecklistItems.Count == 0,
                    "H2 Agent prose/fenced pseudo-action mutated ProjectRecord.");
                Check(!panel.GetVisualDescendants().OfType<Button>()
                    .Any(button => button.Name == "ChatProjectActions"),
                    "H2 Agent pseudo-action text was turned into legacy action UI.");
            }
            finally
            {
                window.Close();
            }
        });

        test("New project Agent source has no pseudo-action parser path", () =>
        {
            var repo = FindRepoRoot();
            var agent = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "Controls", "AiChatPanel.Agent.cs"));
            var legacyUi = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "Controls", "AiChatPanel.Actions.cs"));

            foreach (var forbidden in new[]
            {
                "AiProjectActions", "h2-actions", "ApplyProjectActions", "ApplyAutomaticProjectActions"
            })
                Check(!agent.Contains(forbidden, StringComparison.Ordinal),
                    "New project Agent execution references pseudo-action marker: " + forbidden);

            var addStart = legacyUi.IndexOf("private void AddProjectActions", StringComparison.Ordinal);
            var guard = legacyUi.IndexOf("message.Provider, \"H2 Agent\"", addStart, StringComparison.Ordinal);
            var parse = legacyUi.IndexOf("AiProjectActions.Parse(message.Content)", addStart, StringComparison.Ordinal);
            Check(addStart >= 0 && guard > addStart && parse > guard,
                "Legacy action renderer does not fail closed before parsing H2 Agent output.");

            var autoStart = legacyUi.IndexOf("private async Task ApplyAutomaticProjectActionsAsync", StringComparison.Ordinal);
            var autoGuard = legacyUi.IndexOf("answer.Provider, \"H2 Agent\"", autoStart, StringComparison.Ordinal);
            var autoParse = legacyUi.IndexOf("AiProjectActions.Parse(answer.Content)", autoStart, StringComparison.Ordinal);
            Check(autoStart >= 0 && autoGuard > autoStart && autoParse > autoGuard,
                "Automatic legacy action path can parse H2 Agent output.");
        });
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

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
            Dispatcher.UIThread.RunJobs();
    }

    private static void WaitUntil(Func<bool> predicate, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            Pump();
            if (predicate()) return;
            Thread.Sleep(20);
        }
        Pump();
        if (!predicate()) throw new TimeoutException("Timed out waiting for typed project tool test state.");
    }

    private static void Reject(Action action, string message)
    {
        try
        {
            action();
            throw new Exception(message);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or ArgumentException
                                   or KeyNotFoundException)
        {
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private sealed class ToolAwareAdapter : PlainAdapter, IH2ProjectToolHostConsumer
    {
        public IH2ProjectToolHost? Host { get; private set; }
        public void BindProjectToolHost(IH2ProjectToolHost host) => Host = host;
    }

    private class PlainAdapter : IH2AgentAdapter
    {
        public virtual Task<Guid> StartTaskAsync(Guid? projectId, string goal, H2AgentTaskContext? context = null, bool readOnly = true, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public virtual H2AgentTaskObservation ObserveTask(Guid taskId, long afterSequence = -1) => throw new NotSupportedException();
        public virtual void CancelTask(Guid taskId) => throw new NotSupportedException();
        public virtual bool RespondToApproval(Guid taskId, Guid approvalId, bool approved) => false;
        public virtual H2AgentTaskSummary GetTaskSummary(Guid taskId) => throw new KeyNotFoundException();
        public virtual IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(Guid? projectId = null, int limit = 50) => Array.Empty<H2AgentTaskSummary>();
        public virtual H2AgentEvidence? GetEvidence(string evidenceId) => null;
        public virtual bool AttachProject(Guid taskId, Guid projectId) => false;
    }

    private sealed class PseudoTextAgentAdapter : PlainAdapter
    {
        private readonly Guid _projectId;
        private H2AgentTaskSummary? _summary;
        public PseudoTextAgentAdapter(Guid projectId) => _projectId = projectId;

        public override Task<Guid> StartTaskAsync(Guid? projectId, string goal, H2AgentTaskContext? context = null, bool readOnly = true, CancellationToken cancellationToken = default)
        {
            Check(projectId == _projectId, "Wrong project scope reached pseudo-text fixture.");
            var id = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var fence = new string((char)96, 3);
            var pseudo = "This text must remain presentation-only.\n"
                + fence + "h2-actions\n"
                + "[{\"kind\":\"add_task\",\"text\":\"MUST NOT APPLY\"}]\n"
                + fence;
            _summary = new H2AgentTaskSummary(
                id,
                projectId,
                goal,
                H2AgentTaskStatus.Completed,
                null,
                Array.Empty<H2AgentEvidence>(),
                pseudo,
                null,
                now,
                now);
            return Task.FromResult(id);
        }

        public override H2AgentTaskObservation ObserveTask(Guid taskId, long afterSequence = -1)
        {
            if (_summary is null || _summary.TaskId != taskId) throw new KeyNotFoundException();
            return new(_summary, [new H2AgentProgress(0, _summary.UpdatedUtc, "final", "completed", "Completed")]);
        }

        public override H2AgentTaskSummary GetTaskSummary(Guid taskId)
            => _summary is not null && _summary.TaskId == taskId ? _summary : throw new KeyNotFoundException();

        public override IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(Guid? projectId = null, int limit = 50)
            => _summary is not null && (projectId is null || projectId == _projectId)
                ? [_summary]
                : Array.Empty<H2AgentTaskSummary>();
    }
}

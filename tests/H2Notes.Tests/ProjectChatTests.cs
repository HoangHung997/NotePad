using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using H2Notes.Avalonia;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

internal static class ProjectChatTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run(Action<string, Action> test, string folder)
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        void Pump() => Dispatcher.UIThread.RunJobs();
        T Named<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
        void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
        (App App, MainWindow Main) Session(SheetState state, string file)
        {
            var app = new App(); typeof(App).GetProperty("State")!.SetValue(app, state);
            typeof(App).GetField("_storage", Private)!.SetValue(app, new SheetStorage(Path.Combine(folder, file)));
            var main = new MainWindow(app, state.Notes.First(n => n.IsBoard));
            typeof(App).GetField("_main", Private)!.SetValue(app, main);
            typeof(App).GetField("_storageReady", Private)!.SetValue(app, true);
            typeof(App).GetMethod("TrackWindow", Private)!.Invoke(app, [main]);
            app.ShowMain(); Pump();
            var board = state.Notes.First(n => n.IsBoard);
            var project = board.Projects.FirstOrDefault(p => p.Id == board.SelectedProjectId) ?? board.Projects.FirstOrDefault();
            if (project is not null) { H2UiTestNavigation.OpenProjectWorkspace(main, project.Id); Pump(); }
            return (app, main);
        }
        void Stop(App app)
        {
            typeof(App).GetProperty("IsExiting")!.SetValue(app, true);
            ((DispatcherTimer)typeof(App).GetField("_saveTimer", Private)!.GetValue(app)!).Stop();
            foreach (var window in app.OpenWindows.ToArray()) window.Close();
        }

        test("Detaching AI reuses project editor, history and draft without creating notebooks", () =>
        {
            var state = SheetStorage.Demo(); var project = state.Notes[0].Projects[0];
            project.Conversations = [new() { Draft = "Project draft", Messages = [new() { Content = "Saved project message", CreatedAt = DateTime.UtcNow }] }];
            var oldChat = new NoteRecord { NoteKind = "ai-chat", AiConversations = [new() { Draft = "Legacy draft" }] }; state.Notes.Add(oldChat);
            var oldData = JsonSerializer.Serialize(oldChat); var count = state.Notes.Count;
            var (app, main) = Session(state, "project-chat-shared.json");
            try
            {
                var original = (AiChatPanel)main.FindControl<ContentControl>("AiHost")!.Content!;
                main.ShowProjectAiWindow(); Pump(); var detached = main.DetachedAiWindow!;
                var panel = detached.GetVisualDescendants().OfType<AiChatPanel>().Single();
                Check(ReferenceEquals(panel, original), "A second chat editor was created");
                Check(detached.ProjectId == project.Id && Named<TextBlock>(detached, "AiProjectTitle").Text == project.DisplayName, "Project identity missing");
                Check(Named<TextBox>(panel, "ChatComposer").Text == "Project draft", "Draft not shared");
                Check(panel.GetVisualDescendants().OfType<ChatMessageView>().Single().Body.Text == "Saved project message", "Different conversation loaded");
                Check(!main.FindControl<Border>("AiHostBorder")!.IsVisible, "Inline panel duplicated detached panel");
                Named<TextBox>(panel, "ChatComposer").Text = "Updated outside"; Pump();
                main.DockProjectAi("right"); Pump();
                Check(ReferenceEquals(original, main.FindControl<ContentControl>("AiHost")!.Content), "Redock replaced editor");
                Check(!detached.IsVisible && Named<TextBox>(original, "ChatComposer").Text == "Updated outside", "Redock lost draft");
                app.SaveNow(); var restored = SheetStorage.Read(app.DataPath);
                Check(restored.Notes[0].Projects[0].Conversations[0].Draft == "Updated outside", "Draft not saved inside project");
                Check(state.Notes.Count == count && JsonSerializer.Serialize(oldChat) == oldData, "Legacy history changed or new notebook created");
            }
            finally { Stop(app); }
        });
        test("Detached AI follows project selection both ways with isolated draft, notes and markers", () =>
        {
            var state = SheetStorage.Demo(); var board = state.Notes[0]; var a = board.Projects[0]; var b = board.Projects[1];
            a.NotesRich = RichDocument.Plain("Notes A"); b.NotesRich = RichDocument.Plain("Notes B");
            a.Conversations = [new() { Draft = "Draft A" }]; b.Conversations = [new() { Draft = "Draft B" }];
            var (app, main) = Session(state, "project-chat-selection.json");
            try
            {
                main.ShowProjectAiWindow(); Pump(); var detached = main.DetachedAiWindow!;
                var panel = detached.GetVisualDescendants().OfType<AiChatPanel>().Single();
                main.SelectAiProject(b.Id); Pump();
                Check(board.SelectedProjectId == b.Id && detached.ProjectId == b.Id && panel.ReadContext!() == "Notes B", "Project selection/context did not follow");
                Check(Named<TextBox>(panel, "ChatComposer").Text == "Draft B", "Wrong project draft");
                Named<CheckBox>(panel, "ChatMarkerMode").IsChecked = true; Named<TextBox>(panel, "ChatComposer").Text = "Milestone B"; Pump(); Click(Named<Button>(panel, "ChatSend"));
                Check(b.Conversations[0].Messages.Single().IsTimelineMarker && a.Conversations[0].Messages.Count == 0 && a.Conversations[0].Draft == "Draft A", "Message leaked across projects");
                main.SelectAiProject(a.Id); Pump();
                Check(detached.IsVisible && detached.ProjectId == a.Id && panel.ReadContext!() == "Notes A", "Detached window closed or stale notes");
                Check(Named<TextBox>(panel, "ChatComposer").Text == "Draft A", "First project's draft was lost");
                app.SaveNow(); Check(SheetStorage.Read(app.DataPath).Notes[0].SelectedProjectId == a.Id, "Selected project not persisted");
            }
            finally { Stop(app); }
        });
        test("Detached AI survives hiding the board; X hides only AI and preserves geometry/history", () =>
        {
            var state = SheetStorage.Demo(); var (app, main) = Session(state, "project-chat-hide.json");
            try
            {
                main.ShowProjectAiWindow(); Pump(); var detached = main.DetachedAiWindow!;
                Named<TextBox>(detached, "ChatComposer").Text = "Keep me"; Pump(); main.Close(); Pump();
                Check(!main.IsVisible && detached.IsVisible, "Hiding board closed desktop AI");
                Check(detached.Topmost && !detached.ShowInTaskbar && !detached.CanMinimize && !detached.CanMaximize, "Wrong desktop shell");
                detached.Position = new PixelPoint(245, 125); detached.Width = 450; Pump();
                var closed = false; detached.Closed += (_, _) => closed = true; Click(Named<Button>(detached, "CloseButton"));
                Check(!detached.IsVisible && !closed && !main.IsVisible, "AI X closed app or reopened board");
                var restored = SheetStorage.Read(app.DataPath); var placement = restored.DesktopSession!.ProjectAiWindow!;
                Check(!placement.IsVisible && placement.Left == 245 && placement.Width == 450 && placement.IsPinned, "Desktop geometry lost");
                Check(restored.Notes[0].Projects[0].Conversations.Single().Draft == "Keep me", "Project draft lost after hide");
                app.ShowProjectAiWindow(); Pump();
                Check(detached.IsVisible && !main.IsVisible && Named<TextBox>(detached, "ChatComposer").Text == "Keep me", "Reopen failed or showed board");
            }
            finally { Stop(app); }
        });
        test("Moving project Agent to desktop and hiding board does not cancel an active reply", () =>
        {
            var state = SheetStorage.Demo(); var (app, main) = Session(state, "project-chat-stream.json");
            try
            {
                var project = state.Notes[0].Projects[0];
                var panel = (AiChatPanel)main.FindControl<ContentControl>("AiHost")!.Content!;
                var gate = new GateAgentAdapter(project.Id);
                app.AgentAdapter = gate;

                main.DockProjectAi("floating"); Pump(); Named<TextBox>(panel, "ChatComposer").Text = "Question A"; Pump();
                var send = (Task)typeof(AiChatPanel).GetMethod("SendOrSave", Private)!.Invoke(panel, null)!;
                Pump();

                var startDeadline = DateTime.UtcNow.AddSeconds(5);
                while (gate.Calls == 0 && DateTime.UtcNow < startDeadline) { Pump(); Thread.Sleep(5); }
                Check(gate.Calls == 1, "Project request did not start through AgentAdapter.");
                Check(!main.OwnedWindows.Any(), "Unexpected send confirmation");

                main.ShowProjectAiWindow(); main.Close(); Pump();
                Check(!send.IsCompleted && gate.CancelCalls == 0, "Detaching or hiding board cancelled the Agent task");

                gate.Release.SetResult();
                var deadline = DateTime.UtcNow.AddSeconds(5); while (!send.IsCompleted && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(5); }
                Check(send.IsCompleted, "Agent request timed out"); send.GetAwaiter().GetResult(); Pump();

                var answer = project.Conversations.Single().Messages.Last();
                Check(answer.Status == "complete" && answer.Content == "Project answer" && gate.Calls == 1,
                    "Detached Agent result was lost or started twice");
                Check(answer.AiRunId == gate.TaskId, "Detached Agent result lost task identity.");
                Check(main.DetachedAiWindow!.GetVisualDescendants().OfType<ChatMessageView>().Last().Body.Text == "Project answer",
                    "Reply rendered in wrong host");
            }
            finally { Stop(app); }
        });
        test("Repeated AI reparent drains pending old-root layout and keeps the same composer/progress", () =>
        {
            var state = SheetStorage.Demo();
            state.Notes[0].Projects[0].Conversations = [new() { Draft = "Keep this draft", Messages = [new() { Role = "assistant", Content = "Fixture answer" }] }];
            var (app, main) = Session(state, "project-chat-layout-transfer.json");
            try
            {
                main.DockProjectAi("right"); Pump();
                var panel = (AiChatPanel)main.FindControl<ContentControl>("AiHost")!.Content!;
                var editor = Named<TextBox>(panel, "ChatComposer");
                var bubble = panel.GetVisualDescendants().OfType<ChatMessageView>().Single();
                bubble.Message.Content = ""; bubble.Message.Status = "streaming"; bubble.SetThinking("Fixture progress");
                for (var attempt = 0; attempt < 6; attempt++)
                {
                    bubble.InvalidateMeasure(); panel.InvalidateArrange();
                    main.ShowProjectAiWindow(); Pump();
                    Check(ReferenceEquals(editor, Named<TextBox>(main.DetachedAiWindow!, "ChatComposer")), "Detach replaced editor");
                    bubble.InvalidateMeasure(); panel.InvalidateArrange();
                    main.DockProjectAi("right"); Pump();
                    Check(ReferenceEquals(editor, Named<TextBox>(main, "ChatComposer")) && editor.Text == "Keep this draft", "Redock lost draft/editor");
                }
            }
            finally { Stop(app); }
        });
        test("Desktop AI-only session restores selected project and window on restart/startup", () =>
        {
            var state = SheetStorage.Demo(); var (app, main) = Session(state, "project-chat-restart.json");
            try
            {
                main.SelectAiProject(state.Notes[0].Projects[2].Id); main.ShowProjectAiWindow(); main.Close(); Pump(); app.SaveNow();
                var saved = SheetStorage.Read(app.DataPath); var plan = DesktopRestorePlan.Create(saved, true);
                Check(plan.OpenWindows.Count == 0 && plan.ProjectAiWindowId == main.DetachedAiWindow!.WindowId, "Wrong restore plan");
                foreach (var startup in new[] { false, true })
                {
                    var info = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                    info.ArgumentList.Add("--desktop-session-probe"); info.ArgumentList.Add(app.DataPath); if (startup) info.ArgumentList.Add("--startup");
                    using var process = Process.Start(info)!; var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd();
                    Check(process.WaitForExit(20000) && process.ExitCode == 0, "Restart failed: " + output + error);
                }
                saved.SheetPreferences.RestoreVisibleNotes = false;
                Check(DesktopRestorePlan.Create(saved, true).ProjectAiWindowId is null, "Ignored restore preference");
                saved.SheetPreferences.RestoreVisibleNotes = true; saved.Notes[0].Projects.Clear();
                Check(DesktopRestorePlan.Create(saved, true).ProjectAiWindowId is null, "Restored AI with no project");
            }
            finally { Stop(app); }
        });
        test("Detached chat resize follows latest message but respects reading old history", () =>
        {
            var state = SheetStorage.Demo(); state.Notes[0].Projects[0].Conversations = [new() { Messages = Enumerable.Range(0, 20)
                .Select(i => new AiMessage { Content = "Message " + i + " with several words that wrap across narrow windows", CreatedAt = DateTime.UtcNow.AddMinutes(i) }).ToList() }];
            var (app, main) = Session(state, "project-chat-scroll.json");
            try
            {
                main.ShowProjectAiWindow(); Pump(); var detached = main.DetachedAiWindow!;
                var panel = detached.GetVisualDescendants().OfType<AiChatPanel>().Single();
                var scroll = (ScrollViewer)typeof(AiChatPanel).GetField("_scroll", Private)!.GetValue(panel)!;
                detached.Width = 340; detached.Height = 420; Pump();
                Check(scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y < 2, "Resize hid latest timestamp");
                scroll.Offset = new Vector(0, 20); Pump(); detached.Width = 500; Pump();
                Check(scroll.Offset.Y < scroll.Extent.Height - scroll.Viewport.Height - 100, "Resize jumped from older history to bottom");
            }
            finally { Stop(app); }
        });
    }
    private sealed class GateAgentAdapter : IH2AgentAdapter
    {
        private readonly Guid _projectId;
        private bool _cancelled;
        private H2AgentTaskSummary? _summary;

        public GateAgentAdapter(Guid projectId)
        {
            _projectId = projectId;
            TaskId = Guid.NewGuid();
        }

        public Guid TaskId { get; }
        public int Calls { get; private set; }
        public int CancelCalls { get; private set; }
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Guid> StartTaskAsync(Guid? projectId, string goal, H2AgentTaskContext? context = null, bool readOnly = true, CancellationToken cancellationToken = default)
        {
            if (projectId != _projectId) throw new InvalidOperationException("Wrong project.");
            Calls++;
            var now = DateTime.UtcNow;
            _summary = new H2AgentTaskSummary(TaskId, projectId, goal, H2AgentTaskStatus.Running, null,
                Array.Empty<H2AgentEvidence>(), null, null, now, now);
            return Task.FromResult(TaskId);
        }

        public H2AgentTaskObservation ObserveTask(Guid taskId, long afterSequence = -1)
        {
            if (taskId != TaskId || _summary is null) throw new KeyNotFoundException();
            var now = DateTime.UtcNow;
            if (_cancelled)
            {
                _summary = _summary with { Status = H2AgentTaskStatus.Cancelled, UpdatedUtc = now };
                return new(_summary, [new H2AgentProgress(1, now, "final", "cancelled", "Cancelled")]);
            }

            if (Release.Task.IsCompleted)
            {
                _summary = _summary with
                {
                    Status = H2AgentTaskStatus.Completed,
                    FinalText = "Project answer",
                    UpdatedUtc = now
                };
                return new(_summary, [new H2AgentProgress(1, now, "final", "completed", "Completed")]);
            }

            return new(_summary, [new H2AgentProgress(0, now, "work", "working", "Fixture progress")]);
        }

        public void CancelTask(Guid taskId)
        {
            if (taskId != TaskId) throw new KeyNotFoundException();
            CancelCalls++;
            _cancelled = true;
        }

        public bool RespondToApproval(Guid taskId, Guid approvalId, bool approved) => false;
        public H2AgentTaskSummary GetTaskSummary(Guid taskId) => taskId == TaskId && _summary is not null ? _summary : throw new KeyNotFoundException();
        public IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(Guid? projectId = null, int limit = 50)
            => _summary is not null && (projectId is null || projectId == _projectId) ? [_summary] : Array.Empty<H2AgentTaskSummary>();
        public H2AgentEvidence? GetEvidence(string evidenceId) => null;
        public bool AttachProject(Guid taskId, Guid projectId) => false;
    }
}

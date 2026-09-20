using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using H2Notes.Avalonia;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

internal static class H2LegacyConversationHistoryTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Legacy project conversations round-trip without metadata attachment or saved-file loss", () =>
        {
            var root = Folder();
            var project = LegacyProject();
            var originalConversation = project.Conversations.Single();
            var unknown = originalConversation.Messages[0];
            var known = originalConversation.Messages[1];

            var board = new NoteRecord
            {
                Title = "Legacy board",
                NoteKind = "project-hub",
                Projects = [project]
            };

            var store = new ProjectWorkspaceStore(root);
            store.LoadOrImport();
            store.Save(new SheetState { Notes = [board] });

            var restored = store.Read().Notes.Single().Projects.Single();
            var conversation = restored.Conversations.Single();
            var restoredUnknown = conversation.Messages.Single(message => message.Id == unknown.Id);
            var restoredKnown = conversation.Messages.Single(message => message.Id == known.Id);

            Check(conversation.Id == originalConversation.Id, "Conversation ID changed during storage round-trip.");
            Check(conversation.Title == originalConversation.Title
                && conversation.ProfileId == originalConversation.ProfileId
                && conversation.ReasoningEffort == originalConversation.ReasoningEffort
                && conversation.PermissionMode == originalConversation.PermissionMode,
                "Conversation metadata changed during storage round-trip.");

            Check(restoredUnknown.CreatedAt == default
                && AiHistory.LocalTime(restoredUnknown) is null
                && AiHistory.TimeMetadata(restoredUnknown) == "",
                "Unknown legacy timestamp was fabricated.");

            Check(restoredKnown.CreatedAt == known.CreatedAt
                && restoredKnown.Provider == known.Provider
                && restoredKnown.Model == known.Model
                && restoredKnown.AiRunId == known.AiRunId
                && restoredKnown.DeviceId == known.DeviceId
                && restoredKnown.ParentId == known.ParentId,
                "Known legacy message metadata changed.");

            var attachment = restoredKnown.Attachments.Single();
            Check(attachment.Id == known.Attachments.Single().Id
                && attachment.Name == "legacy.docx"
                && attachment.MimeType.Contains("wordprocessingml", StringComparison.Ordinal)
                && attachment.Data.SequenceEqual([1, 2, 3, 4])
                && attachment.Text == "Legacy extracted text"
                && attachment.Notice == "Legacy extraction notice"
                && attachment.Sha256 == "ATTACH-SHA"
                && attachment.SourceName == "source.docx"
                && attachment.SourceSha256 == "SOURCE-SHA",
                "Legacy attachment metadata/content changed.");

            var saved = restoredKnown.SavedFiles.Single();
            Check(saved.Name == "legacy-output.pdf"
                && saved.Path == @"C:\legacy\output.pdf"
                && saved.Sha256 == "SAVED-SHA"
                && saved.SavedAt == known.SavedFiles.Single().SavedAt,
                "Legacy saved-file metadata changed.");
        });

        test("New Agent presentation appends without rewriting legacy messages", () =>
        {
            var project = LegacyProject();
            var conversation = project.Conversations.Single();
            var legacySnapshot = conversation.Messages
                .Select(message => JsonSerializer.Serialize(message))
                .ToArray();
            var legacyIds = conversation.Messages.Select(message => message.Id).ToArray();

            var adapter = new CompletionAgentAdapter(project.Id);
            var app = new App { AgentAdapter = adapter };
            var panel = new AiChatPanel(app);
            panel.SetProject(project);

            var window = new Window { Width = 720, Height = 640, Content = panel };
            window.Show();
            Pump();
            try
            {
                var composer = panel.GetVisualDescendants().OfType<TextBox>().Single(c => c.Name == "ChatComposer");
                composer.Text = "Continue this project with Agent.";
                panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ChatSend")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                WaitUntil(() => conversation.Messages.Count == legacySnapshot.Length + 2
                    && conversation.Messages.Last().Status == "complete");

                for (var i = 0; i < legacySnapshot.Length; i++)
                {
                    var message = conversation.Messages.Single(m => m.Id == legacyIds[i]);
                    Check(JsonSerializer.Serialize(message) == legacySnapshot[i],
                        "Legacy message was rewritten when Agent presentation appended.");
                }

                Check(conversation.Messages[0].CreatedAt == default,
                    "Appending Agent presentation fabricated timestamp on untimed legacy message.");

                var user = conversation.Messages[^2];
                var answer = conversation.Messages[^1];
                Check(user.Provider == "H2 Agent" && answer.Provider == "H2 Agent"
                    && user.AiRunId == adapter.TaskId && answer.AiRunId == adapter.TaskId,
                    "New Agent presentation is not clearly separated from legacy metadata.");
                Check(answer.Content == "Agent continuation result.",
                    "Agent continuation result missing.");

                var bubbles = panel.GetVisualDescendants().OfType<ChatMessageView>().ToArray();
                Check(bubbles.Length == conversation.Messages.Count,
                    "Legacy history is no longer readable in the presentation thread.");
                Check(bubbles.Any(b => b.Message.Id == legacyIds[0])
                    && bubbles.Any(b => b.Message.Id == legacyIds[1]),
                    "Legacy messages disappeared from the thread.");
            }
            finally
            {
                window.Close();
            }
        });

        test("Legacy untimed chat renders unknown time instead of current time", () =>
        {
            var message = new AiMessage
            {
                Role = "assistant",
                Content = "Legacy untimed answer",
                Model = "legacy-model",
                Provider = "legacy-provider",
                CreatedAt = default
            };
            var bubble = new ChatMessageView(message);
            var window = new Window { Content = bubble, Width = 420, Height = 240 };
            window.Show();
            Pump();
            try
            {
                var time = bubble.GetVisualDescendants().OfType<TextBlock>()
                    .Single(control => control.Name == "MessageTime");
                Check(time.Text!.StartsWith("Không rõ giờ", StringComparison.Ordinal),
                    "Legacy untimed message was shown with a fabricated clock time.");
                Check(AiHistory.LocalTime(message) is null && message.CreatedAt == default,
                    "Rendering mutated the legacy timestamp.");
            }
            finally
            {
                window.Close();
            }
        });

        test("Legacy history migration has no destructive conversion store", () =>
        {
            var repo = FindRepoRoot();
            var panel = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "Controls", "AiChatPanel.cs"));
            var model = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Core", "AiModels.cs"));

            foreach (var forbidden in new[]
            {
                "LegacyConversationStore", "ConversationMigrationDatabase",
                "RewriteLegacyMessage", "FabricateTimestamp", "DateTime.UtcNow // legacy"
            })
                Check(!panel.Contains(forbidden, StringComparison.Ordinal)
                    && !model.Contains(forbidden, StringComparison.Ordinal),
                    "Destructive legacy-history migration marker found: " + forbidden);

            Check(model.Contains("Missing legacy timestamps stay unknown", StringComparison.Ordinal)
                && model.Contains("if (message.CreatedAt == default) return", StringComparison.Ordinal),
                "Unknown-timestamp preservation guard is missing.");
        });
    }

    private static ProjectRecord LegacyProject()
    {
        var profileId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var legacyRun = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var knownTime = new DateTime(2025, 5, 12, 3, 4, 5, DateTimeKind.Utc);
        var savedAt = new DateTime(2025, 5, 12, 3, 5, 6, DateTimeKind.Utc);

        var unknown = new AiMessage
        {
            Id = userId,
            Role = "user",
            Content = "Legacy question without timestamp",
            Provider = "legacy-provider",
            Model = "legacy-model",
            CreatedAt = default
        };

        var known = new AiMessage
        {
            Id = Guid.NewGuid(),
            ParentId = userId,
            Sequence = 7,
            AiRunId = legacyRun,
            DeviceId = "OLD-PC",
            Role = "assistant",
            Content = "Legacy answer",
            Provider = "legacy-provider",
            Model = "legacy-model-v2",
            Context = "legacy context snapshot",
            Status = "complete",
            CreatedAt = knownTime,
            Attachments =
            [
                new AiAttachment
                {
                    Id = Guid.NewGuid(),
                    Name = "legacy.docx",
                    MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    Data = [1, 2, 3, 4],
                    Text = "Legacy extracted text",
                    Notice = "Legacy extraction notice",
                    Sha256 = "ATTACH-SHA",
                    SourceName = "source.docx",
                    SourceSha256 = "SOURCE-SHA"
                }
            ],
            SavedFiles =
            [
                new AiSavedFile("legacy-output.pdf", @"C:\legacy\output.pdf", "SAVED-SHA", savedAt)
            ]
        };

        return new ProjectRecord
        {
            Name = "Legacy conversation project",
            Conversations =
            [
                new AiConversation
                {
                    Id = conversationId,
                    Revision = 12,
                    CreatedAtUtc = new DateTime(2025, 5, 12, 2, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2025, 5, 12, 4, 0, 0, DateTimeKind.Utc),
                    Title = "Legacy thread",
                    Draft = "Legacy unsent draft",
                    ProfileId = profileId,
                    PermissionMode = AiPermissionMode.ConfirmChanges,
                    ReasoningEffort = "high",
                    Messages = [unknown, known]
                }
            ],
            SelectedAiConversationId = conversationId
        };
    }

    private static string Folder()
    {
        var path = Path.Combine(Path.GetTempPath(), "H2Notes-legacy-history-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
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
        if (!predicate()) throw new TimeoutException("Timed out waiting for legacy-history acceptance state.");
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

    private sealed class CompletionAgentAdapter : IH2AgentAdapter
    {
        private readonly Guid _projectId;
        private H2AgentTaskSummary? _summary;
        public CompletionAgentAdapter(Guid projectId) { _projectId = projectId; TaskId = Guid.NewGuid(); }
        public Guid TaskId { get; }

        public Task<Guid> StartTaskAsync(Guid? projectId, string goal, H2AgentTaskContext? context = null, bool readOnly = true, CancellationToken cancellationToken = default)
        {
            Check(projectId == _projectId, "Wrong project reached history Agent.");
            var now = DateTime.UtcNow;
            _summary = new H2AgentTaskSummary(TaskId, projectId, goal, H2AgentTaskStatus.Completed, null,
                Array.Empty<H2AgentEvidence>(), "Agent continuation result.", null, now, now);
            return Task.FromResult(TaskId);
        }

        public H2AgentTaskObservation ObserveTask(Guid taskId, long afterSequence = -1)
            => taskId == TaskId && _summary is not null
                ? new(_summary, [new H2AgentProgress(0, DateTime.UtcNow, "final", "completed", "Completed")])
                : throw new KeyNotFoundException();

        public void CancelTask(Guid taskId) { }
        public bool RespondToApproval(Guid taskId, Guid approvalId, bool approved) => false;
        public H2AgentTaskSummary GetTaskSummary(Guid taskId) => taskId == TaskId && _summary is not null ? _summary : throw new KeyNotFoundException();
        public IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(Guid? projectId = null, int limit = 50)
            => _summary is not null && (projectId is null || projectId == _projectId) ? [_summary] : Array.Empty<H2AgentTaskSummary>();
        public H2AgentEvidence? GetEvidence(string evidenceId) => null;
        public bool AttachProject(Guid taskId, Guid projectId) => false;
    }
}

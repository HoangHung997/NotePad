using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

internal static class ChatTests
{
    public static void Run(Action<string, Action> test, string folder)
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        void Pump() => Dispatcher.UIThread.RunJobs();
        T Named<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
        void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
        H2Notes.Avalonia.App Session(SheetState state, string file)
        {
            var app = new H2Notes.Avalonia.App(); var type = app.GetType();
            type.GetProperty("State")!.SetValue(app, state);
            type.GetField("_storage", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, new SheetStorage(Path.Combine(folder, file)));
            type.GetField("_storageReady", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, true);
            return app;
        }
        void Stop(H2Notes.Avalonia.App app)
        {
            app.GetType().GetProperty("IsExiting")!.SetValue(app, true);
            ((DispatcherTimer)app.GetType().GetField("_saveTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!).Stop();
            foreach (var window in app.OpenWindows) window.Close();
        }

        test("Legacy messages never acquire a fabricated timestamp on loading", () =>
        {
            var message = JsonSerializer.Deserialize<AiMessage>("{\"Role\":\"user\",\"Content\":\"old note\"}")!;
            Check(AiHistory.LocalTime(message) is null, "Missing timestamp was invented");
            var again = JsonSerializer.Deserialize<AiMessage>(JsonSerializer.Serialize(message))!;
            Check(again.CreatedAt == default, "Unknown timestamp changed on save");
            var dated = new AiMessage { CreatedAt = new DateTime(2026, 9, 15, 4, 12, 31, DateTimeKind.Utc) };
            Check(AiHistory.LocalTime(dated) == dated.CreatedAt.ToLocalTime(), "UTC not converted for display");
        });
        test("Local timeline markers and failed answers never enter AI requests", () =>
        {
            var conversation = new AiConversation { Messages = [new() { Content = "question" }, new() { Content = "private marker", IsTimelineMarker = true },
                new() { Role = "assistant", Content = "failed", Status = "error" }, new() { Role = "assistant", Content = "reply" }] };
            var turns = AiHistory.RequestTurns(conversation);
            Check(turns.Count == 2 && turns[0].Content == "question" && turns[1].Content == "reply", "Wrong context included");
        });
        test("Independent chat is saved in its own note file with history and time while draft stays device-local", () =>
        {
            var store = new ProjectWorkspaceStore(Path.Combine(folder, "independent-chat")); var state = store.LoadOrImport();
            var note = new NoteRecord { NoteKind = "ai-chat", Title = "AI độc lập", IsPinned = true, Width = 415, Height = 615 };
            var conversation = new AiConversation { Draft = "Đang soạn", Messages = [new() { Content = "Mốc công việc", IsTimelineMarker = true, CreatedAt = new DateTime(2026, 9, 15, 2, 30, 0, DateTimeKind.Utc) }] };
            note.AiConversations.Add(conversation); note.SelectedAiConversationId = conversation.Id; state.Notes.Add(note);
            var board = SheetStorage.Demo().Notes[0]; state.Notes.Add(board); store.Save(state);
            var read = new ProjectWorkspaceStore(store.Root).Read(); var chat = read.Notes.Single(n => n.IsChat);
            Check(chat.SelectedAiConversationId == conversation.Id && chat.AiConversations[0].Draft == "", "Conversation selection or device-local draft policy lost");
            Check(chat.AiConversations[0].Messages[0].CreatedAt == conversation.Messages[0].CreatedAt && chat.IsPinned, "Time or pin lost");
            Check(chat.Projects.Count == 0 && read.Notes.Where(n => n.IsBoard).Sum(n => n.Projects.Count) == board.Projects.Count, "Created a fake project");
            Check(Directory.GetFiles(Path.Combine(store.Root, "notes"), "*.h2note.json").Length == 1, "Chat has no independent file");
            Check(!File.ReadAllText(store.FilePath).Contains("Mốc công việc"), "Index duplicated chat contents");
        });
        test("Workspace v2 upgrades all files atomically so old clients reject local-only markers", () =>
        {
            var root = Path.Combine(folder, "chat-upgrade"); CreateVersionTwo(root);
            var before = File.ReadAllBytes(Path.Combine(root, "workspace.h2index.json"));
            var store = new ProjectWorkspaceStore(root); var state = store.Read(); var id = state.Notes[0].Projects[0].Id;
            store.SaveIncremental(state, new HashSet<Guid>());
            using var index = JsonDocument.Parse(File.ReadAllBytes(store.FilePath));
            Check(index.RootElement.GetProperty("SchemaVersion").GetInt32() == ProjectWorkspaceStore.SchemaVersion, "Index not upgraded");
            foreach (var file in index.RootElement.GetProperty("Files").EnumerateArray())
            {
                using var data = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, file.GetProperty("File").GetString()!)));
                Check(data.RootElement.GetProperty("SchemaVersion").GetInt32() == ProjectWorkspaceStore.SchemaVersion, "Mixed file versions after incremental upgrade");
            }
            Check(new ProjectWorkspaceStore(root).Read().Notes[0].Projects[0].Id == id, "Upgrade changed identity");
            Check(Directory.EnumerateFiles(Path.Combine(root, "backups"), "*.bak", SearchOption.AllDirectories).Any(p => File.ReadAllBytes(p).SequenceEqual(before)), "Previous index not backed up");
            var brokenRoot = Path.Combine(folder, "chat-upgrade-failure"); CreateVersionTwo(brokenRoot);
            var snapshots = Directory.EnumerateFiles(brokenRoot, "*.json", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
            var broken = new ProjectWorkspaceStore(brokenRoot, step => { if (step == 2) throw new IOException("Interrupted upgrade"); });
            var old = broken.Read(); try { broken.SaveIncremental(old, new HashSet<Guid>()); throw new Exception("Expected injected failure"); } catch (IOException) { }
            Check(snapshots.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)), "Interrupted upgrade did not restore v2 files");
            _ = new ProjectWorkspaceStore(brokenRoot).Read();
        });
        test("Merge keep-both remaps independent conversation and parent IDs", () =>
        {
            var user = new AiMessage { Content = "a", CreatedAt = DateTime.UtcNow };
            var c = new AiConversation { Messages = [user, new() { Role = "assistant", ParentId = user.Id, Content = "b" }] };
            var note = new NoteRecord { NoteKind = "ai-chat", AiConversations = [c], SelectedAiConversationId = c.Id };
            var source = new SheetState { Notes = [note] }; var destination = ProjectWorkspaceStore.Clone(source);
            destination.Notes[0].AiConversations[0].Draft = "conflict";
            var result = new WorkspaceTransfer(source, destination).Prepare(WorkspaceTransferMode.Merge, new Dictionary<Guid, ConflictResolution> { [note.Id] = ConflictResolution.KeepBoth });
            ProjectWorkspaceStore.ValidateState(result);
            var copy = result.Notes.Single(n => n.Id != note.Id); var copied = copy.AiConversations[0];
            Check(copied.Id != c.Id && copy.SelectedAiConversationId == copied.Id, "Selected conversation not remapped");
            Check(copied.Messages[1].ParentId == copied.Messages[0].Id && copied.Messages[0].Id != user.Id, "Parent not remapped");
            Check(copied.Messages[0].CreatedAt == user.CreatedAt, "Merge changed timestamp");
            var repeat = new WorkspaceTransfer(source, result).Prepare(WorkspaceTransferMode.Merge, new Dictionary<Guid, ConflictResolution> { [note.Id] = ConflictResolution.KeepBoth });
            Check(repeat.Notes.Count == 2, "Repeat merge duplicated chat again");
        });
        test("Chat aligns user right, assistant left, shows time and groups by date/gap", () =>
        {
            var start = new DateTime(2026, 9, 14, 4, 0, 0, DateTimeKind.Utc);
            var project = new ProjectRecord { Conversations = [new() { Messages = [new() { Content = "User", CreatedAt = start },
                new() { Role = "assistant", Content = "AI", CreatedAt = start.AddMinutes(1) }, new() { Content = "Later", CreatedAt = start.AddMinutes(30) },
                new() { Content = "Next day", CreatedAt = start.AddDays(1), IsTimelineMarker = true }] }] };
            var panel = new AiChatPanel(new H2Notes.Avalonia.App()); panel.SetProject(project);
            var window = new Window { Width = 420, Height = 660, Content = panel }; window.Show(); Pump();
            var bubbles = panel.GetVisualDescendants().OfType<ChatMessageView>().ToArray();
            Check(bubbles.Length == 4 && bubbles[0].HorizontalAlignment == HorizontalAlignment.Right && bubbles[1].HorizontalAlignment == HorizontalAlignment.Left, "Wrong bubble alignment");
            Check(bubbles.All(b => Named<TextBlock>(b, "MessageTime").Text!.Contains(':')), "Missing per-message clock");
            Check(panel.GetVisualDescendants().OfType<Border>().Count(b => b.Name == "ChatTimeDivider") == 3, "Missing day or gap separator");
            Check(bubbles[0].Bounds.Right > bubbles[1].Bounds.Right && bubbles[1].Bounds.Left < bubbles[0].Bounds.Left, "Alignment has no visual effect");
            foreach (var width in new[] { 340d, 560d, 900d })
            {
                window.Width = width; Pump(); var composer = Named<TextBox>(panel, "ChatComposer"); var send = Named<Button>(panel, "ChatSend");
                var a = composer.TranslatePoint(default, panel)!.Value; var b = send.TranslatePoint(default, panel)!.Value;
                Check(b.Y >= a.Y + composer.Bounds.Height, "Send is not below the multiline composer");
                Check(b.X + send.Bounds.Width <= panel.Bounds.Width, "Send clipped");
            }
            window.Close();
        });
        test("Save marker works with no AI profile and preserves standalone/project isolation", () =>
        {
            var app = new H2Notes.Avalonia.App(); app.LocalSettings.Ai.Profiles.Clear();
            var project = new ProjectRecord { Notes = "private project notes", Conversations = [new() { Draft = "Project draft" }] };
            var notebook = new NoteRecord { NoteKind = "ai-chat" };
            var calls = 0; var panel = new AiChatPanel(app, () => { calls++; throw new Exception("Network must not be used"); });
            var window = new Window { Width = 420, Height = 660, Content = panel }; panel.SetProject(project); window.Show(); Pump();
            panel.SetStandalone(notebook); Pump();
            Named<CheckBox>(panel, "ChatMarkerMode").IsChecked = true; Named<TextBox>(panel, "ChatComposer").Text = "Đã làm xong phần 1"; Pump();
            Click(Named<Button>(panel, "ChatSend"));
            var message = notebook.AiConversations.Single().Messages.Single();
            Check(calls == 0 && message.IsTimelineMarker && message.CreatedAt.Kind == DateTimeKind.Utc, "Marker called AI or lost timestamp");
            Check(notebook.AiConversations[0].Draft == "" && project.Conversations[0].Draft == "Project draft", "Draft leaked between scopes");
            panel.SetProject(project); Pump(); Check(Named<TextBox>(panel, "ChatComposer").Text == "Project draft", "Project draft did not restore");
            Check(!Named<CheckBox>(panel, "ChatMarkerMode").IsChecked.GetValueOrDefault(), "Marker mode leaked to project");
            panel.SetStandalone(ProjectWorkspaceStore.Clone(notebook)); Pump();
            Check(Named<CheckBox>(panel, "ChatMarkerMode").IsChecked == true, "Local-only mode was lost on reopening");
            window.Close();
        });
        test("Reopening returns to selected conversation rather than the last created", () =>
        {
            var selected = new AiConversation { Title = "Earlier", Draft = "Earlier draft", MarkerOnlyMode = true };
            var notebook = new NoteRecord { NoteKind = "ai-chat", AiConversations = [selected, new() { Title = "Later", Draft = "Other draft" }], SelectedAiConversationId = selected.Id };
            var panel = new AiChatPanel(new H2Notes.Avalonia.App()); panel.SetStandalone(ProjectWorkspaceStore.Clone(notebook));
            var window = new Window { Width = 420, Height = 660, Content = panel }; window.Show(); Pump();
            Check(Named<TextBox>(panel, "ChatComposer").Text == "Earlier draft" && Named<CheckBox>(panel, "ChatMarkerMode").IsChecked == true, "Reopened wrong chat"); window.Close();
        });
        test("Streaming response targets message body, not its timestamp (mock transport)", () =>
        {
            var app = new H2Notes.Avalonia.App(); var profile = new AiProfile { Model = "test-model", Protocol = AiProtocol.Ollama, BaseUrl = "http://localhost:11434" };
            app.LocalSettings.Ai.Profiles = [profile]; app.LocalSettings.Ai.SelectedId = profile.Id;
            var handler = new Stub(); var notebook = new NoteRecord { NoteKind = "ai-chat", AiConversations = [new() { Messages = [new() { Content = "private local milestone", IsTimelineMarker = true }] }] };
            var panel = new AiChatPanel(app, () => new AiClient(handler)); panel.SetStandalone(notebook);
            var window = new Window { Width = 420, Height = 660, Content = panel }; window.Show(); Pump();
            Named<TextBox>(panel, "ChatComposer").Text = "Test prompt"; Pump();
            var send = (Task)typeof(AiChatPanel).GetMethod("SendOrSave", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null)!;
            var deadline = DateTime.UtcNow.AddSeconds(5); while (!send.IsCompleted && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(5); }
            Check(send.IsCompleted, "Mock request did not finish"); send.GetAwaiter().GetResult(); Pump();
            var answer = panel.GetVisualDescendants().OfType<ChatMessageView>().Single(b => b.Message.Role == "assistant");
            Check(answer.Body.Text == "Mock reply" && answer.Message.Status == "complete", "Wrong response target");
            Check(Named<TextBlock>(answer, "MessageTime").Text != "Mock reply", "Reply overwrote time");
            Check(handler.Calls == 1 && !handler.Body!.Contains("private local milestone"), "Marker leaked online"); window.Close();
        });
        test("Interrupted session keeps partial AI answer and original time without resending", () =>
        {
            var stamp = DateTime.UtcNow.AddDays(-1);
            var message = new AiMessage { Role = "assistant", Status = "streaming", Content = "Partial response", CreatedAt = stamp };
            var notebook = new NoteRecord { NoteKind = "ai-chat", AiConversations = [new() { Messages = [message] }] };
            var panel = new AiChatPanel(new H2Notes.Avalonia.App(), () => throw new Exception("Must not resend on open"));
            panel.SetStandalone(notebook);
            Check(message.Status == "interrupted" && message.Content == "Partial response" && message.CreatedAt == stamp, "Lost partial response or changed its timestamp");
        });
        test("Standalone chat shows safe HTTP error in the saved bubble after reopening without retrying", () =>
        {
            var app = new H2Notes.Avalonia.App(); var profile = new AiProfile { Model = "minimax-m3:cloud" };
            app.LocalSettings.Ai.Profiles = [profile]; app.LocalSettings.Ai.SelectedId = profile.Id;
            var handler = new ErrorStub();
            var notebook = new NoteRecord { NoteKind = "ai-chat", Title = "HTTP error fixture" };
            var panel = new AiChatPanel(app, () => new AiClient(handler)); panel.SetStandalone(notebook);
            var window = new Window { Width = 420, Height = 660, Content = panel }; window.Show(); Pump();
            try
            {
                Named<TextBox>(panel, "ChatComposer").Text = "Test only"; Pump();
                var send = (Task)typeof(AiChatPanel).GetMethod("SendOrSave", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null)!;
                var deadline = DateTime.UtcNow.AddSeconds(5); while (!send.IsCompleted && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(5); }
                Check(send.IsCompleted, "Mock failure did not finish"); send.GetAwaiter().GetResult(); Pump();
                var answer = notebook.AiConversations.Single().Messages.Last();
                Check(answer.Status == "error" && answer.Content == "" && answer.ErrorText.Contains("HTTP 402") && !answer.ErrorText.Contains("private-key"), "Wrong persisted failure");
                Check(panel.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Name == "MessageError" && t.IsVisible && t.Text!.Contains("HTTP 402")), "Missing visible failure");

                var store = new ProjectWorkspaceStore(Path.Combine(folder, "chat-error-standalone")); _ = store.LoadOrImport();
                store.Save(new() { Notes = [notebook] });
                var restored = store.Read().Notes.Single(n => n.Id == notebook.Id);

                panel.SetProject(null); panel.SetStandalone(restored); Pump();
                Check(restored.AiConversations[0].Messages.Last().ErrorText == answer.ErrorText && handler.Calls == 1, "Reopen lost error or resent request");
                Check(AiHistory.RequestTurns(restored.AiConversations[0]).Count == 1, "Error leaked into future AI context");
            }
            finally { window.Close(); }
        });
        test("Saving a replacement AI connection refreshes the open panel without losing its draft", () =>
        {
            var app = new H2Notes.Avalonia.App(); var old = new AiProfile { Model = "old:cloud" };
            app.LocalSettings.Ai.Profiles = [old]; app.LocalSettings.Ai.SelectedId = old.Id;
            var project = new ProjectRecord { Conversations = [new() { ProfileId = old.Id, Draft = "Unsent draft" }] };
            var panel = new AiChatPanel(app); panel.SetProject(project);
            var window = new Window { Width = 420, Height = 660, Content = panel }; window.Show(); Pump();
            try
            {
                var replacement = new AiProfile { Id = old.Id, Name = "Updated", Model = "gemma3:4b" };
                app.LocalSettings.Ai.Profiles = [replacement]; panel.RefreshConnections(); Pump();
                var profiles = (ComboBox)typeof(AiChatPanel).GetField("_profiles", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(panel)!;
                Check(ReferenceEquals(profiles.SelectedItem, replacement), "Open chat retained stale connection object");
                Check(Named<TextBlock>(panel, "ChatConnectionStatus").Text!.Contains("trên máy") && !Named<TextBlock>(panel, "ChatConnectionStatus").Text!.Contains("old:cloud"), "Stale cloud/model label");
                Check(Named<TextBox>(panel, "ChatComposer").Text == "Unsent draft" && project.Conversations.Count == 1, "Connection refresh lost the draft/history");
            }
            finally { window.Close(); }
        });
        test("Independent chat X hides with saved draft and its own desktop placement", () =>
        {
            var legacyNote = new NoteRecord { NoteKind = "ai-chat", IsPinned = true, Width = 420, Height = 660 };
            var app = Session(new() { Notes = [legacyNote] }, "standalone-session.json"); app.OpenNote(legacyNote); Pump();
            var window = app.OpenWindows.OfType<H2Notes.Avalonia.AiChatWindow>().Single(); var note = app.State.Notes.Single();
            Check(note.IsChat && window.Topmost && !window.ShowInTaskbar && !window.CanMinimize && !window.CanMaximize, "Incorrect independent window shell");
            Named<TextBox>(window, "ChatComposer").Text = "Bản nháp riêng"; window.Position = new PixelPoint(245, 125); window.Width = 440; Pump();
            var closed = false; window.Closed += (_, _) => closed = true;
            Click(Named<Button>(window, "CloseButton"));
            Check(!window.IsVisible && !closed, "X destroyed window");
            var saved = SheetStorage.Read(app.DataPath).Notes.Single();
            Check(saved.AiConversations[0].Draft == "Bản nháp riêng" && saved.Left == 245 && saved.Width == 440, "Draft/placement lost on hide");
            app.OpenNote(legacyNote); Pump(); Check(window.IsVisible && app.State.Notes.Count == 1, "Reopen created duplicate chat");
            app.SaveNow(); var restored = SheetStorage.Read(app.DataPath);
            var plan = DesktopRestorePlan.Create(restored, true);
            Check(plan.OpenWindows.Single().IsChat && plan.OpenWindows.Single().Id == note.Id, "Startup did not restore chat-only working set");
            Stop(app);
        });
    }
    private static void CreateVersionTwo(string root)
    {
        var store = new ProjectWorkspaceStore(root); _ = store.LoadOrImport(); store.Save(SheetStorage.Demo());
        var index = JsonNode.Parse(File.ReadAllBytes(store.FilePath))!; index["SchemaVersion"] = 2;
        foreach (var file in index["Files"]!.AsArray())
        {
            var path = Path.Combine(root, file!["File"]!.GetValue<string>());
            var document = JsonNode.Parse(File.ReadAllBytes(path))!; document["SchemaVersion"] = 2;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document); File.WriteAllBytes(path, bytes);
            file["Hash"] = Convert.ToHexString(SHA256.HashData(bytes));
        }
        File.WriteAllBytes(store.FilePath, JsonSerializer.SerializeToUtf8Bytes(index));
    }
    private sealed class Stub : HttpMessageHandler
    {
        public int Calls; public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Body = await request.Content!.ReadAsStringAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"message\":{\"content\":\"Mock reply\"},\"done\":true}\n") };
        }
    }
    private sealed class ErrorStub : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PaymentRequired) { Content = new StringContent("{\"error\":\"payment required private-key\"}") });
        }
    }
}

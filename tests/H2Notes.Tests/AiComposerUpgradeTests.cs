using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

internal static class AiComposerUpgradeTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Call(AiChatPanel panel, string name, params object?[] args) => typeof(AiChatPanel).GetMethod(name, Instance)!.Invoke(panel, args)!;
    private static T Field<T>(AiChatPanel panel, string name) => (T)typeof(AiChatPanel).GetField(name, Instance)!.GetValue(panel)!;
    private static object? InputData(string name, params object?[] args) => typeof(AiChatPanel).Assembly
        .GetType("H2Notes.Avalonia.Controls.AiComposerInputData")!.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static void Wait(Task task)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < end) { Pump(); Thread.Sleep(2); }
        Check(task.IsCompleted, "Composer operation did not finish"); task.GetAwaiter().GetResult(); Pump();
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { return; }
        throw new Exception("Unsafe input was accepted");
    }

    internal static void Run(Action<string, Action> test, string folder)
    {
        test("Composer upgrade keeps input above one unclipped bottom row at 340x420", () =>
        {
            using var f = new Fixture();
            var plus = f.Panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ChatAttach");
            var send = f.Panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ChatSend");
            var inputPoint = f.Input.TranslatePoint(default, f.Panel)!.Value;
            var plusPoint = plus.TranslatePoint(default, f.Panel)!.Value;
            var sendPoint = send.TranslatePoint(default, f.Panel)!.Value;
            Check(plusPoint.X >= 12 && plusPoint.Y >= inputPoint.Y + f.Input.Bounds.Height, "Plus is not below the multiline input");
            Check(Math.Abs(sendPoint.Y - plusPoint.Y) <= 2 && sendPoint.X + send.Bounds.Width <= 328, "Send is not on the same bottom row or is clipped");
            Check(sendPoint.Y + send.Bounds.Height <= 408 && f.Input.Bounds.Width >= 260 && f.Input.Bounds.Height >= 44, "Input/send is clipped or too narrow");
            f.Conversation.DraftAttachments.Add(AiDocuments.Read("small.txt", [65])); Call(f.Panel, "RenderAttachments"); Pump();
            Check(Field<ScrollViewer>(f.Panel, "_scroll").Bounds.Height >= 90, "History has less than 90 DIP with one attachment");
            Check(f.Panel.GetVisualDescendants().OfType<Expander>().Count() == 1, "Composer added an expander");
        });
        test("Composer @ filters without accents, navigates and inserts without sending", () =>
        {
            using var f = new Fixture();
            f.Type("Please @ban");
            var popup = Field<Popup>(f.Panel, "_mentionPopup");
            var list = Field<ListBox>(f.Panel, "_mentionList");
            Check(popup.IsOpen && list.ItemCount >= 3, "Mention filtering did not open");
            var selected = list.SelectedIndex;
            f.Key(Key.Down); Check(list.SelectedIndex != selected, "Arrow did not navigate");
            f.Key(Key.Tab);
            Check(!popup.IsOpen && f.Input.Text!.StartsWith("Please @[") && !f.Input.Text.Contains("@ban"), "Mention did not replace token in place");
            Check(f.Conversation.Messages.Count == 0 && !f.Window.OwnedWindows.Any(), "Mention sent or opened a file");
        });
        test("Composer @ Ctrl+Enter selects suggestion before send and Escape preserves draft", () =>
        {
            using var f = new Fixture();
            f.Type("@word"); f.Key(Key.Enter, KeyModifiers.Control);
            Check(f.Input.Text!.Contains(".docx") && f.Conversation.Messages.Count == 0, "Ctrl+Enter sent instead of selecting");
            f.Type("@excel"); f.Key(Key.Escape);
            Check(!Field<Popup>(f.Panel, "_mentionPopup").IsOpen && f.Input.Text == "@excel", "Escape changed draft");
            f.Type("hello@example.com");
            Check(!Field<Popup>(f.Panel, "_mentionPopup").IsOpen, "Email address triggered a mention");
        });
        test("Composer + menu exposes real filenames and drafts, not autonomous tools", () =>
        {
            using var f = new Fixture();
            f.Conversation.DraftAttachments.Add(AiDocuments.Read("Nguon-du-lieu.csv", Encoding.UTF8.GetBytes("name,value\na,1")));
            Call(f.Panel, "RenderAttachments"); Call(f.Panel, "OpenComposerMenu");
            var menu = Field<ContextMenu>(f.Panel, "_composerMenu");
            Check(menu.Items[0] is MenuItem { Icon: not null } first && first.Header!.ToString()!.Contains("ảnh")
                && menu.Items[1] is MenuItem { Icon: not null } second && second.Header!.ToString()!.Contains("tệp"), "Attach actions need a direct first-level click");
            var labels = menu.Items.OfType<MenuItem>().SelectMany(i => i.Items.OfType<MenuItem>()).Select(i => i.Header?.ToString() ?? "").ToArray();
            Check(labels.Any(x => x.Contains("Nguon-du-lieu.csv")) && labels.Any(x => x.Contains(".docx")) && labels.Any(x => x.Contains(".xlsx")), "Missing file names/draft actions");
            Check(f.Conversation.Messages.Count == 0, "Opening + sent a request"); menu.Close();
        });
        test("Marker and data preview live only in context menus at every composer size", () =>
        {
            using var f = new Fixture();
            var marker = Field<CheckBox>(f.Panel, "_markerMode");
            foreach (var height in new[] { 420d, 820, 420 })
            {
                f.Window.Height = height; Pump();
                Check(!marker.IsVisible, "Marker checkbox reappeared after resize");
                Check(!f.Panel.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "ChatContextPreview"), "Duplicate data preview button remains");
                Call(f.Panel, "OpenComposerMenu"); var menu = Field<ContextMenu>(f.Panel, "_composerMenu");
                var context = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Ngữ cảnh"));
                Check(context.Items.OfType<MenuItem>().Count(i => Equals(i.Header, "Xem dữ liệu gửi")) == 1, "Preview missing from context");
                var item = context.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Chỉ lưu mốc, không hỏi AI"));
                var before = marker.IsChecked == true;
                Check(item.IsChecked == before, "Marker check out of sync");
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
                Check(marker.IsChecked == !before && f.Conversation.MarkerOnlyMode == !before, "Marker action not persisted");
                Check(f.Conversation.Messages.Count == 0, "Menu selection sent a message"); menu.Close();
            }
        });
        test("Marker mention toggles shared mode without sending then saves a private timestamped message", () =>
        {
            using var f = new Fixture(); f.Type("Work finished @moc");
            var list = Field<ListBox>(f.Panel, "_mentionList");
            Check(list.ItemCount == 1, "Marker mention did not filter");
            f.Key(Key.Enter, KeyModifiers.Control);
            Check(f.Conversation.MarkerOnlyMode && f.Input.Text == "Work finished " && f.Conversation.Messages.Count == 0, "Marker mention sent or lost draft");
            Check(f.Input.PlaceholderText!.Contains("không gửi AI"), "Marker mode is not discoverable in input");
            f.Key(Key.Enter, KeyModifiers.Control);
            var message = f.Conversation.Messages.Single();
            Check(message.IsTimelineMarker && message.Content == "Work finished" && message.CreatedAt != default, "Marker sent to AI or lost timestamp");
            f.Type("@moc"); f.Key(Key.Tab);
            Check(!f.Conversation.MarkerOnlyMode && f.Conversation.Messages.Count == 1, "Cannot exit marker mode from mention");
        });
        test("Marker action captured before a project switch cannot modify either draft", () =>
        {
            using var f = new Fixture();
            var action = ((System.Collections.IEnumerable)Call(f.Panel, "ComposerActions")).Cast<object>()
                .Single(a => Equals(a.GetType().GetProperty("Id")!.GetValue(a), "marker"));
            var target = Call(f.Panel, "CaptureComposerTarget"); var other = new ProjectRecord();
            f.Panel.SetProject(other);
            Wait((Task)Call(f.Panel, "ChooseComposerAction", action, target, false));
            Check(!f.Conversation.MarkerOnlyMode && other.Conversations.Count == 0, "Stale marker action crossed scope");
        });
        test("Context preview is reachable from plus and mention without sending a message", () =>
        {
            using var f = new Fixture(); f.Type("Inspect the current draft");
            Call(f.Panel, "OpenComposerMenu"); var menu = Field<ContextMenu>(f.Panel, "_composerMenu");
            var context = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Ngữ cảnh"));
            context.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Xem dữ liệu gửi")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
            var preview = f.Window.OwnedWindows.Single();
            Check(preview.Title == "Dữ liệu sẽ gửi cho AI" && f.Conversation.Messages.Count == 0, "Preview sent or failed to open");
            preview.Close(); Pump(); f.Type("@preview"); f.Key(Key.Tab);
            preview = f.Window.OwnedWindows.Single();
            Check(preview.Title == "Dữ liệu sẽ gửi cho AI" && f.Conversation.Messages.Count == 0, "Mention preview sent or failed to open");
            preview.Close(); Pump();
        });
        test("Composer clipboard Ctrl+V/context menu uses exactly one plain-text edit with undo", () =>
        {
            using var f = new Fixture();
            f.Type("Keep replace end"); f.Input.SelectionStart = 5; f.Input.SelectionEnd = 12;
            var clipboard = f.Window.Clipboard ?? throw new Exception("Headless clipboard unavailable");
            Wait(clipboard.SetTextAsync("dán văn bản"));
            f.Key(Key.V, KeyModifiers.Control); Wait(Field<Task>(f.Panel, "_composerPaste"));
            Check(f.Input.Text == "Keep dán văn bản end", "Paste duplicated or lost text/selection");
            f.Input.Undo(); Pump(); Check(f.Input.Text == "Keep replace end", "Paste destroyed undo");
            f.Input.SelectionStart = f.Input.SelectionEnd = f.Input.Text!.Length;
            Wait(clipboard.SetTextAsync(" https://example.invalid/file.png"));
            var paste = f.Input.ContextMenu!.Items.OfType<MenuItem>().Single(i => i.Header!.ToString()!.StartsWith("Dán"));
            paste.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Wait(Field<Task>(f.Panel, "_composerPaste"));
            Check(f.Input.Text!.EndsWith(" https://example.invalid/file.png") && f.Conversation.DraftAttachments.Count == 0, "Context paste downloaded a URL or lost text");
        });
        test("Composer plain text still pastes while attachment import is busy", () =>
        {
            using var f = new Fixture();
            var target = Call(f.Panel, "CaptureComposerTarget");
            var gate = new TaskCompletionSource<AiAttachment[]>();
            var import = (Task)Call(f.Panel, "ImportComposerAttachments", target, (Func<Task<AiAttachment[]>>)(() => gate.Task));
            using var data = new DataTransfer(); data.Add(DataTransferItem.CreateText("normal text"));
            Wait((Task)Call(f.Panel, "PasteComposerData", target, data));
            Check(f.Input.Text == "normal text", "Busy import ate plain text");
            gate.SetResult([]); Wait(import);
        });
        test("Composer deferred clipboard cannot paste into a changed project or conversation", () =>
        {
            foreach (var switchProject in new[] { true, false })
            {
                using var f = new Fixture(); using var data = new DeferredData(DataFormat.Text);
                var target = Call(f.Panel, "CaptureComposerTarget");
                var paste = (Task)Call(f.Panel, "PasteComposerData", target, data);
                Check(data.Requested && !paste.IsCompleted, "Deferred clipboard was not awaited");
                if (switchProject) f.Panel.SetProject(new ProjectRecord { Name = "Other project" });
                else Call(f.Panel, "StartNewConversation");
                data.Completion.SetResult("must not appear"); Wait(paste);
                Check(string.IsNullOrEmpty(f.Input.Text) && f.Conversation.Draft == "", "Late clipboard changed another draft");
            }
        });
        test("Composer deferred bitmap cannot attach after project switch", () =>
        {
            using var f = new Fixture(); using var data = new DeferredData(DataFormat.Bitmap);
            var target = Call(f.Panel, "CaptureComposerTarget");
            var paste = (Task)Call(f.Panel, "PasteComposerData", target, data);
            Check(data.Requested && !paste.IsCompleted, "Bitmap was not awaited");
            var other = new ProjectRecord(); f.Panel.SetProject(other);
            data.Completion.SetResult(null); Wait(paste);
            Check(f.Conversation.DraftAttachments.Count == 0 && other.Conversations.Count == 0, "Late bitmap changed project");
            Check(!Field<bool>(f.Panel, "_preparing"), "Import lock leaked");
        });
        test("Composer delayed text does not overwrite a newly changed draft or selection", () =>
        {
            using var f = new Fixture(); f.Type("original");
            using var data = new DeferredData(DataFormat.Text);
            var target = Call(f.Panel, "CaptureComposerTarget");
            var paste = (Task)Call(f.Panel, "PasteComposerData", target, data);
            f.Type("typed while waiting"); data.Completion.SetResult("late paste"); Wait(paste);
            Check(f.Input.Text == "typed while waiting", "Late clipboard replaced newly typed text");
            Check(Field<TextBlock>(f.Panel, "_status").Text!.Contains("Ctrl+V"), "No retry guidance after paste position changed");
        });
        test("Composer bitmap bounds reject oversized images without allocating image pixels", () =>
        {
            InputData("CheckBitmapSize", 8000, 5000);
            Reject(() => InputData("CheckBitmapSize", 8001, 5000));
            Reject(() => InputData("CheckBitmapSize", int.MaxValue, int.MaxValue));
            Reject(() => InputData("CheckBitmapSize", 0, 1));
        });
        test("Composer deferred import commits atomically and discards results after switching away/back", () =>
        {
            using var f = new Fixture(); var target = Call(f.Panel, "CaptureComposerTarget");
            var gate = new TaskCompletionSource<AiAttachment[]>();
            var import = (Task)Call(f.Panel, "ImportComposerAttachments", target, (Func<Task<AiAttachment[]>>)(() => gate.Task));
            f.Panel.SetProject(new ProjectRecord()); f.Panel.SetProject(f.Project);
            gate.SetResult([AiDocuments.Read("late.txt", Encoding.UTF8.GetBytes("late"))]); Wait(import);
            Check(f.Conversation.DraftAttachments.Count == 0 && f.Conversation.Messages.Count == 0, "Stale import mutated original history");
        });
        test("Composer invalid attachment batches report errors without throwing or partially committing", () =>
        {
            using var f = new Fixture(); var target = Call(f.Panel, "CaptureComposerTarget");
            var files = Enumerable.Range(0, 5).Select(i => AiDocuments.Read("file-" + i + ".txt", [65])).ToArray();
            Wait((Task)Call(f.Panel, "ImportComposerAttachments", target, (Func<Task<AiAttachment[]>>)(() => Task.FromResult(files))));
            Check(f.Conversation.DraftAttachments.Count == 0 && Field<TextBlock>(f.Panel, "_status").Text!.Contains("4 tệp"), "Over-limit batch was partially committed or silently failed");
            Wait((Task)Call(f.Panel, "ImportComposerAttachments", target,
                (Func<Task<AiAttachment[]>>)(() => Task.FromException<AiAttachment[]>(new InvalidDataException("invalid document")))));
            Check(f.Conversation.DraftAttachments.Count == 0 && Field<TextBlock>(f.Panel, "_status").Text!.Contains("invalid document"), "Invalid document did not report a safe error");
            Check(!Field<bool>(f.Panel, "_preparing"), "Error left importer locked");
        });
        test("Composer drop file URI attaches local file; remote URI stays text", () =>
        {
            using var f = new Fixture();
            var path = Path.Combine(folder, "composer-local-file.txt"); File.WriteAllText(path, "local content", Encoding.UTF8);
            using var local = new DataTransfer(); local.Add(DataTransferItem.CreateText(new Uri(path).AbsoluteUri));
            var over = new DragEventArgs(DragDrop.DragOverEvent, local, f.Input, default, KeyModifiers.None);
            f.Input.RaiseEvent(over);
            Check(over.Handled && over.DragEffects == DragDropEffects.Copy, "Drag-over event never reached the composer");
            var drop = new DragEventArgs(DragDrop.DropEvent, local, f.Input, default, KeyModifiers.None);
            f.Input.RaiseEvent(drop);
            Check(drop.Handled, "Drop event never reached the composer");
            var end = DateTime.UtcNow.AddSeconds(5);
            while (Field<bool>(f.Panel, "_preparing") && DateTime.UtcNow < end) { Pump(); Thread.Sleep(2); }
            Pump();
            Check(f.Conversation.DraftAttachments.Count == 1, "Local drop did not attach: " + Field<TextBlock>(f.Panel, "_status").Text);
            Check(f.Conversation.DraftAttachments[0].Name == "composer-local-file.txt" && f.Input.Text == "", "Local drop lost file name or also pasted its URI");
            using var remote = new DataTransfer(); remote.Add(DataTransferItem.CreateText("https://example.invalid/remote.txt"));
            f.Input.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, remote, f.Input, default, KeyModifiers.None)); Pump();
            Check(f.Conversation.DraftAttachments.Count == 1 && f.Input.Text == "https://example.invalid/remote.txt", "Remote drop tried to attach/download");
        });
        test("Composer local paths reject HTTP, remote file hosts and UNC shares", () =>
        {
            foreach (var path in new[] { "https://example.invalid/a.txt", "file://server/share/a.txt", @"\\server\share\a.txt", "relative.txt" })
                Reject(() => InputData("LocalPath", path));
            var pathWithSpace = Path.Combine(folder, "composer local.txt");
            Check((string)InputData("LocalPath", new Uri(pathWithSpace).AbsoluteUri)! == pathWithSpace, "file URI escaping not decoded");
        });
        test("Composer local file safety walk accepts ordinary ancestors and a Windows drive root", () =>
        {
            var path = Path.Combine(folder, "composer-root-walk.txt"); File.WriteAllText(path, "root walk is safe", Encoding.UTF8);
            var file = (AiAttachment)InputData("ReadLocalFile", path)!;
            Check(file.Name == "composer-root-walk.txt" && file.Text == "root walk is safe", "Ordinary local file was rejected by ancestor checks");
            var fromUri = (AiAttachment)InputData("ReadLocalFile", new Uri(path).AbsoluteUri)!;
            Check(fromUri.Sha256 == file.Sha256, "file URI changed the selected local content");
        });
        test("Composer enforces four files, eight MB per file and 32 MB total without trimming history", () =>
        {
            var bytes = new byte[AiDocuments.MaxFileBytes];
            var full = new AiAttachment { Name = "large.png", Data = bytes };
            var conversation = new AiConversation { DraftAttachments = [full, full, full, full] };
            Reject(() => InputData("CheckBudget", new[] { conversation }, conversation, new[] { full }));
            conversation.DraftAttachments.Clear(); conversation.Messages.Add(new AiMessage { Attachments = [full, full, full, full] });
            Reject(() => InputData("CheckBudget", new[] { conversation }, conversation, new[] { AiDocuments.Read("tiny.txt", [65]) }));
            Reject(() => InputData("CheckBudget", Array.Empty<AiConversation>(), conversation, new[] { new AiAttachment { Data = new byte[AiDocuments.MaxFileBytes + 1] } }));
            Check(conversation.Messages.Single().Attachments.Count == 4, "Budget check trimmed history");
            var large = Path.Combine(folder, "composer-too-large.txt"); using (var stream = File.Create(large)) stream.SetLength(AiDocuments.MaxFileBytes + 1L);
            Reject(() => InputData("ReadLocalFile", large));
        });
    }

    private sealed class Fixture : IDisposable
    {
        internal AiConversation Conversation { get; } = new();
        internal ProjectRecord Project { get; }
        internal AiChatPanel Panel { get; }
        internal Window Window { get; }
        internal TextBox Input { get; }
        internal Fixture()
        {
            Project = new ProjectRecord { Name = "Composer fixture", Conversations = [Conversation] };
            Panel = new AiChatPanel(new H2Notes.Avalonia.App(), () => throw new Exception("Composer must not call AI"));
            Panel.SetProject(Project);
            Window = new Window { Width = 340, Height = 420, Content = Panel }; Window.Show(); Pump();
            Input = Field<TextBox>(Panel, "_composer"); Input.Focus(); Pump();
        }
        internal void Type(string value)
        {
            Input.Text = value; Input.CaretIndex = value.Length; Input.SelectionStart = Input.SelectionEnd = value.Length;
            Input.Focus(); Pump();
        }
        internal void Key(Key key, KeyModifiers modifiers = KeyModifiers.None)
        { Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers }); Pump(); }
        public void Dispose() { Window.Close(); Pump(); }
    }

    private sealed class DeferredData(DataFormat format) : IAsyncDataTransfer, IAsyncDataTransferItem
    {
        internal readonly TaskCompletionSource<object?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Requested;
        public IReadOnlyList<DataFormat> Formats => [format];
        public IReadOnlyList<IAsyncDataTransferItem> Items => [this];
        public Task<object?> TryGetRawAsync(DataFormat requested)
        { Requested = true; return requested == format ? Completion.Task : Task.FromResult<object?>(null); }
        public void Dispose() { }
    }
}

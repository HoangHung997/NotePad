using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using H2Notes.Avalonia.Controls;
using H2Notes.Avalonia.Services;
using H2Notes.Core;

internal static class AiComposerOptionsTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private static object? Call(object instance, string method, params object?[] args) => instance.GetType().GetMethod(method, Private)!.Invoke(instance, args);
    private static T Named<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static void Wait(Task task)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < end) { Pump(); Thread.Sleep(2); }
        Check(task.IsCompleted, "Timed out"); task.GetAwaiter().GetResult(); Pump();
    }
    private static Rect InPanel(Control control, AiChatPanel panel) => new(control.TranslatePoint(default, panel)!.Value, control.Bounds.Size);

    public static void Run(Action<string, Action> test)
    {
        test("Composer options keep a single row and collapse permission text without losing draft or selection", () =>
        {
            using var f = new Fixture();
            f.Input.Text = "First line\nGiữ nguyên bản nháp";
            f.Input.SelectionStart = 2; f.Input.SelectionEnd = 8;
            foreach (var width in new[] { 340d, 420, 560, 660, 700, 960, 340 })
            {
                f.Window.Width = width; Pump(); f.Window.UpdateLayout();
                var input = InPanel(f.Input, f.Panel);
                var plus = InPanel(Named<Button>(f.Panel, "ChatAttach"), f.Panel);
                var permission = InPanel(Named<Button>(f.Panel, "ChatPermission"), f.Panel);
                var model = InPanel(Field<Button>(f.Panel, "_modelPickerButton"), f.Panel);
                var mic = InPanel(Named<Button>(f.Panel, "ChatDictation"), f.Panel);
                var send = InPanel(Named<Button>(f.Panel, "ChatSend"), f.Panel);
                Check(input.Height >= 76 && input.Width >= 260, "Multiline composer too small at " + width);
                Check(plus.Top >= input.Bottom && model.Top >= input.Bottom, "Footer overlaps the editor at " + width);
                Check(permission.Left >= plus.Right && model.Left >= permission.Right && mic.Left >= model.Right && send.Left >= mic.Right,
                    "Bottom controls overlap at " + width);
                foreach (var rect in new[] { input, plus, permission, model, mic, send })
                    Check(rect.Left >= 12 && rect.Right <= width - 12 + .1 && rect.Bottom <= f.Window.Height - 12 + .1, "Clipping at " + width + ": " + rect);
                Check(new[] { plus, permission, model, mic, send }.All(r => Math.Abs(r.Center.Y - send.Center.Y) <= 1), "Footer wrapped to another row at " + width);
                var narrow = Named<Grid>(f.Panel, "ChatComposerOptions").Bounds.Width < 460;
                Check(Field<TextBlock>(f.Panel, "_permissionLabel").IsVisible == !narrow
                    && Named<AppIcon>(f.Panel, "ChatPermissionChevron").IsVisible == !narrow, "Permission did not collapse at " + width);
                Check(!f.Panel.GetVisualDescendants().Contains(Field<ComboBox>(f.Panel, "_reasoningEffort")), "Separate effort control remains in footer");
                Check(f.Input.Text == "First line\nGiữ nguyên bản nháp" && f.Input.SelectionStart == 2 && f.Input.SelectionEnd == 8, "Resize replaced editor state");
                Check(f.Panel.GetVisualDescendants().OfType<ComboBox>().All(c => c.Name != "ChatProfile"), "Profile selector remains outside model popup");
            }
            Check(!Field<StackPanel>(f.Panel, "_optionsPanel").Children.Contains(Field<ComboBox>(f.Panel, "_profiles")), "Picker still parented in header");
        });
        test("Composer send and stop share a black circular slot; timeline icon retains contrast", () =>
        {
            using var f = new Fixture();
            var send = Named<Button>(f.Panel, "ChatSend"); var stop = Field<Button>(f.Panel, "_stop");
            Check(send.Parent == stop.Parent && send.Width == send.Height && send.CornerRadius.TopLeft == send.Width / 2, "Send/stop slot is not circular");
            Check(((AppIcon)send.Content!).Kind == IconKind.ArrowUp && ((AppIcon)stop.Content!).Kind == IconKind.Stop, "Wrong vectors");
            Field<CheckBox>(f.Panel, "_markerMode").IsChecked = true; Pump();
            Check(((AppIcon)send.Content!).Kind == IconKind.Clipboard && ((AppIcon)send.Content!).Foreground == Brushes.White, "Timeline action lost contrast");
            Field<CheckBox>(f.Panel, "_markerMode").IsChecked = false; Pump();
            Check(((AppIcon)send.Content!).Kind == IconKind.ArrowUp, "Normal send did not restore");
        });
        test("Short composer keeps history room and moves checked marker/history controls into plus", () =>
        {
            using var f = new Fixture(); f.Window.Height = 420; Pump();
            f.Conversation.DraftAttachments.Add(AiDocuments.Read("small.txt", [65])); Call(f.Panel, "RenderAttachments"); Pump();
            Check(f.Input.MinHeight == 44 && f.Input.MaxHeight == 80, "Compact input policy not applied");
            Check(Field<ScrollViewer>(f.Panel, "_scroll").Bounds.Height >= 90, "One attachment leaves less than 90 DIP history");
            var marker = Field<CheckBox>(f.Panel, "_markerMode"); var status = Field<TextBlock>(f.Panel, "_status");
            Check(!marker.IsVisible && status.TextWrapping == TextWrapping.NoWrap && status.TextTrimming == TextTrimming.CharacterEllipsis, "Compact footer did not collapse");
            status.Text = "Full connection and destination description";
            Check(Equals(ToolTip.GetTip(status), status.Text), "Compact status loses full tooltip");
            Call(f.Panel, "OpenComposerMenu");
            var menu = Field<ContextMenu>(f.Panel, "_composerMenu");
            Check(menu.Items.OfType<MenuItem>().Any(i => Equals(i.Header, "Lịch sử trao đổi"))
                && menu.Items.OfType<MenuItem>().Any(i => Equals(i.Header, "Trao đổi mới")), "Hidden history actions have no replacement");
            var markerItem = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Ngữ cảnh")).Items.OfType<MenuItem>()
                .Single(i => Equals(i.Header, "Chỉ lưu mốc, không hỏi AI"));
            markerItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
            Check(marker.IsChecked == true && f.Conversation.MarkerOnlyMode, "Compact marker action did not persist");
            menu.Close(); Call(f.Panel, "OpenComposerMenu"); menu = Field<ContextMenu>(f.Panel, "_composerMenu");
            Check(menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Ngữ cảnh")).Items.OfType<MenuItem>()
                .Single(i => Equals(i.Header, "Chỉ lưu mốc, không hỏi AI")).IsChecked, "Menu does not reflect checked marker");
            menu.Close(); f.Window.Height = 700; Pump();
            Check(!marker.IsVisible && marker.IsChecked == true && f.Input.MinHeight == 76, "Resize lost marker state or exposed removed checkbox");
        });
        test("Composer permissions persist to the conversation and rebuild actions without replacing editor", () =>
        {
            using var f = new Fixture();
            f.Input.Text = "Keep this draft"; f.Input.SelectionStart = 2; f.Input.SelectionEnd = 7;
            Check(f.Panel.CurrentPermission == AiPermissionMode.ConfirmChanges, "Unsafe default");
            var changed = 0; f.Panel.ComposerOptionsChanged += () => changed++;
            Wait((Task)Call(f.Panel, "SelectPermissionMode", AiPermissionMode.ReadOnly)!);
            Check(f.Conversation.PermissionMode == AiPermissionMode.ReadOnly && f.Panel.CurrentPermission == AiPermissionMode.ReadOnly && changed == 1, "Permission was not persisted");
            Check(ReferenceEquals(f.Input, Named<TextBox>(f.Panel, "ChatComposer")) && f.Input.Text == "Keep this draft" && f.Input.SelectionStart == 2 && f.Input.SelectionEnd == 7, "Permission replaced draft/selection");
            var actions = ((System.Collections.IEnumerable)Call(f.Panel, "ComposerActions")!).Cast<object>().ToArray();
            Check(actions.All(a => a.GetType().GetProperty("Id")!.GetValue(a) is not ("word" or "excel" or "csv")), "Read-only still offers artifact requests");
            f.Panel.SetProject(new ProjectRecord());
            Check(f.Panel.CurrentPermission == AiPermissionMode.ConfirmChanges, "Permission leaked to another project");
            f.Panel.SetProject(f.Project);
            Check(f.Panel.CurrentPermission == AiPermissionMode.ReadOnly, "Conversation permission did not reload");
        });
        test("Changing permission on an empty chat creates exactly one conversation", () =>
        {
            using var f = new Fixture(); var project = new ProjectRecord(); f.Panel.SetProject(project);
            Wait((Task)Call(f.Panel, "SelectPermissionMode", AiPermissionMode.ReadOnly)!);
            Check(project.Conversations.Count == 1 && project.Conversations.Single().PermissionMode == AiPermissionMode.ReadOnly, "No conversation persisted");
            Wait((Task)Call(f.Panel, "SelectPermissionMode", AiPermissionMode.ConfirmChanges)!);
            Check(project.Conversations.Count == 1, "Permission selection duplicated chat");
        });
        test("Project access warning defaults to cancel and cannot grant access after scope changes", () =>
        {
            using var f = new Fixture();
            var change = (Task)Call(f.Panel, "SelectPermissionMode", AiPermissionMode.ProjectAccess)!; Pump();
            var dialog = f.Window.OwnedWindows.Single();
            Check(!change.IsCompleted && f.Panel.CurrentPermission == AiPermissionMode.ConfirmChanges, "Access granted before warning");
            var text = string.Join(" ", dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Check(text.Contains("không chạy lệnh/shell") && text.Contains("Không tự xóa hoặc ghi đè"), "Warning implies OS access");
            Check(dialog.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault).Content?.ToString() == "Hủy", "Confirmation is the default button");
            dialog.Close(false); Wait(change);
            Check(f.Conversation.PermissionMode == AiPermissionMode.ConfirmChanges, "Cancel granted permission");
            change = (Task)Call(f.Panel, "SelectPermissionMode", AiPermissionMode.ProjectAccess)!; Pump();
            dialog = f.Window.OwnedWindows.Single();
            f.Panel.SetProject(new ProjectRecord()); f.Panel.SetProject(f.Project);
            dialog.Close(true); Wait(change);
            Check(f.Conversation.PermissionMode == AiPermissionMode.ConfirmChanges, "Late dialog granted permission after away/back");
            change = (Task)Call(f.Panel, "SelectPermissionMode", AiPermissionMode.ProjectAccess)!; Pump();
            f.Window.OwnedWindows.Single().Close(true); Wait(change);
            Check(f.Conversation.PermissionMode == AiPermissionMode.ProjectAccess, "Explicit warning approval not saved");
            f.Panel.SetStandalone(new NoteRecord { NoteKind = "ai-chat" });
            Wait((Task)Call(f.Panel, "SelectPermissionMode", AiPermissionMode.ProjectAccess)!);
            Check(f.Panel.CurrentPermission == AiPermissionMode.ConfirmChanges && !f.Window.OwnedWindows.Any(), "Standalone granted project access");
        });
        test("Imported project permission needs a local grant; revocation saves locally and failed save cannot grant", () =>
        {
            using var f = new Fixture();
            f.Conversation.PermissionMode = AiPermissionMode.ProjectAccess; Call(f.Panel, "RefreshComposerOptions");
            Check(f.Panel.CurrentPermission == AiPermissionMode.ConfirmChanges, "Imported JSON granted project access");
            f.App.LocalSettings.Ai.ProjectAccessConversationIds.Add(f.Conversation.Id); Call(f.Panel, "RefreshComposerOptions");
            Check(f.Panel.CurrentPermission == AiPermissionMode.ProjectAccess, "Local grant not recognized");
            var saves = 0; f.Panel.PersistComposerPermissions = _ => saves++;
            Wait((Task)Call(f.Panel, "SelectPermissionMode", AiPermissionMode.ReadOnly)!);
            Check(saves == 1 && !f.App.LocalSettings.Ai.ProjectAccessConversationIds.Contains(f.Conversation.Id)
                && f.Panel.CurrentPermission == AiPermissionMode.ReadOnly, "Revocation not persisted locally");
            f.Panel.PersistComposerPermissions = _ => throw new IOException("Synthetic write failure");
            var change = (Task)Call(f.Panel, "SelectPermissionMode", AiPermissionMode.ProjectAccess)!; Pump();
            f.Window.OwnedWindows.Single().Close(true); Wait(change);
            Check(!f.App.LocalSettings.Ai.ProjectAccessConversationIds.Contains(f.Conversation.Id)
                && f.Panel.CurrentPermission == AiPermissionMode.ReadOnly, "Failed local save granted project access");
        });
        test("Preparation and active request disable permission model and effort without applying hidden selections", () =>
        {
            using var f = new Fixture();
            foreach (var preparing in new[] { true, false })
            {
                using var cancellation = new CancellationTokenSource();
                typeof(AiChatPanel).GetField(preparing ? "_preparing" : "_request", Private)!.SetValue(f.Panel, preparing ? true : cancellation);
                Call(f.Panel, "RefreshComposerOptions");
                foreach (var field in new[] { "_permissionButton", "_profiles", "_reasoningEffort", "_modelPickerButton" })
                    Check(!Field<Control>(f.Panel, field).IsEnabled, field + " enabled while busy");
                Wait((Task)Call(f.Panel, "SelectPermissionMode", AiPermissionMode.ReadOnly)!);
                Check(f.Panel.CurrentPermission == AiPermissionMode.ConfirmChanges, "Busy permission changed");
                typeof(AiChatPanel).GetField(preparing ? "_preparing" : "_request", Private)!.SetValue(f.Panel, preparing ? false : null);
                Call(f.Panel, "RefreshComposerOptions");
                Check(Named<Button>(f.Panel, "ChatPermission").IsEnabled && Field<ComboBox>(f.Panel, "_profiles").IsEnabled, "Controls stayed locked");
            }
        });
        test("Reasoning is capability-driven and request cloning never mutates the saved profile", () =>
        {
            using var f = new Fixture();
            Call(f.Panel, "OpenModelPicker"); Pump();
            var effort = Field<ComboBox>(f.Panel, "_reasoningEffort");
            var values = effort.ItemsSource!.Cast<object>().Select(i => i.GetType().GetProperty("Value")!.GetValue(i) as string).ToArray();
            Check(values.Skip(1).SequenceEqual(AiModelCapabilities.GetReasoningOptions(f.Profile)), "Invented options");
            effort.SelectedIndex = Array.IndexOf(values, "high"); Pump();
            Check(f.Conversation.ReasoningEffort == "high" && f.Panel.SelectedReasoningEffort == "high", "Effort was not persisted");
            var copy = f.Panel.CreateRequestProfile(f.Profile);
            Check(!ReferenceEquals(copy, f.Profile) && copy.Id == f.Profile.Id && copy.Model == f.Profile.Model && copy.BaseUrl == f.Profile.BaseUrl && copy.ReasoningEffort == "high", "Request profile not cloned correctly");
            Check(f.Profile.ReasoningEffort == "low" && copy.RequestReasoningSummary == f.Profile.RequestReasoningSummary && copy.TimeoutSeconds == f.Profile.TimeoutSeconds, "Saved profile mutated or settings dropped");
            effort.SelectedIndex = 0; Pump();
            Check(f.Conversation.ReasoningEffort is null && f.Panel.CreateRequestProfile(f.Profile).ReasoningEffort == "low", "Default did not inherit saved profile");
            var unknown = new AiProfile { Model = "custom-no-effort-api", ReasoningEffort = "high" };
            f.App.LocalSettings.Ai.Profiles.Add(unknown); f.Conversation.ProfileId = unknown.Id; f.Panel.RefreshConnections(); Pump();
            Check(!effort.IsEnabled && effort.ItemCount == 1, "Unknown API offers effort choices");
            var unknownChoice = effort.ItemsSource!.Cast<object>().Single();
            var label = unknownChoice.GetType().GetProperty("Label")!.GetValue(unknownChoice);
            Check(Equals(label, "Tối đa") && f.Panel.CreateRequestProfile(unknown).ReasoningEffort == "" && unknown.ReasoningEffort == "high", "Unknown API received invented max parameter or mutated profile");
        });
        test("One model popup contains the saved profile and a supported effort slider with default reset", () =>
        {
            using var f = new Fixture(); f.Input.Text = "Unsent text"; f.Input.SelectionStart = 2; f.Input.SelectionEnd = 5;
            Field<Button>(f.Panel, "_modelPickerButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            var popup = Field<Popup>(f.Panel, "_modelPickerPopup");
            Check(popup.IsOpen && popup.Child is Border { CornerRadius.TopLeft: > 0 }, "Model card failed to open");
            Check(popup.Child!.Bounds.Height <= 200, "Model card is no longer compact");
            Check(popup.Child!.GetVisualDescendants().Contains(Field<ComboBox>(f.Panel, "_profiles")), "Model choice is not in the same popup");
            var effort = Field<ComboBox>(f.Panel, "_reasoningEffort"); var slider = Field<Slider>(f.Panel, "_reasoningSlider");
            Check(slider.IsSnapToTickEnabled && slider.TickFrequency == 1 && slider.Maximum == effort.ItemCount - 1, "Slider invents continuous/unsupported efforts");
            slider.Value = slider.Maximum; Pump();
            Check(f.Conversation.ReasoningEffort == AiModelCapabilities.GetReasoningOptions(f.Profile)[^1], "Slider selection did not persist");
            Field<Button>(f.Panel, "_reasoningReset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(f.Conversation.ReasoningEffort is null && slider.Value == 0 && f.Profile.ReasoningEffort == "low", "Reset modified the saved profile");
            Check(f.Input.Text == "Unsent text" && f.Input.SelectionStart == 2 && f.Input.SelectionEnd == 5, "Model popup lost editor state");
            slider.RaiseEvent(new global::Avalonia.Input.KeyEventArgs { RoutedEvent = global::Avalonia.Input.InputElement.KeyDownEvent, Key = global::Avalonia.Input.Key.Escape }); Pump();
            Check(!popup.IsOpen, "Escape did not dismiss model card");
            slider.Value = slider.Maximum; Pump();
            Check(f.Conversation.ReasoningEffort is null, "Hidden slider changed reasoning");
        });
        test("Model card closes on project change and unknown models never expose a fake effort slider", () =>
        {
            using var f = new Fixture(); Call(f.Panel, "OpenModelPicker"); Pump();
            var popup = Field<Popup>(f.Panel, "_modelPickerPopup"); var slider = Field<Slider>(f.Panel, "_reasoningSlider");
            var other = new ProjectRecord(); f.Panel.SetProject(other); Pump();
            Check(!popup.IsOpen, "Model card survived project change");
            slider.Value = slider.Maximum; Pump();
            Check(other.Conversations.Count == 0 && f.Conversation.ReasoningEffort is null, "Stale slider modified a conversation");
            f.Panel.SetProject(f.Project); var unknown = new AiProfile { Model = new string('x', 150), ReasoningEffort = "high" };
            f.App.LocalSettings.Ai.Profiles.Add(unknown); f.Conversation.ProfileId = unknown.Id; f.Panel.RefreshConnections();
            Call(f.Panel, "OpenModelPicker"); Pump();
            Check(popup.IsOpen && !slider.IsVisible && !slider.IsEnabled && Field<TextBlock>(f.Panel, "_unknownReasoning").IsVisible, "Unknown model offers editable effort");
            Check(Field<TextBlock>(f.Panel, "_modelPickerLabel").TextTrimming == TextTrimming.CharacterEllipsis, "Long model name may push send out");
            f.Window.Width = 340; f.Window.Height = 420; Pump();
            Check(popup.Child!.Bounds.Width <= 316 && popup.Child.Bounds.Height <= 388, "Popup does not fit minimum window");
        });
        test("Dictation is an honest Windows launcher and is never started by load resize or settings refresh", () =>
        {
            using var f = new Fixture(); var fake = new NoRecordingDictation(); f.Panel.DictationService = fake;
            f.Window.Width = 700; f.Panel.RefreshConnections(); f.Panel.SetProject(new ProjectRecord()); f.Panel.SetProject(f.Project); Pump();
            Check(fake.Calls == 0, "Lifecycle automatically started microphone");
            var mic = Named<Button>(f.Panel, "ChatDictation");
            Check(mic.Content is AppIcon { Kind: IconKind.Microphone }, "Microphone is not vector artwork");
            var tooltip = ToolTip.GetTip(mic)?.ToString() ?? "";
            Check(tooltip.Contains("Windows") && tooltip.Contains("Microsoft") && tooltip.Contains("bản nháp"), "Voice typing privacy/draft hint is misleading");
            var unsupported = new ChatDictationService().TryOpen(0);
            Check(!unsupported.ShortcutSent, "Invalid window received shortcut");
        });
        test("Win H native INPUT layout and partial key cleanup contain no Enter or unrelated modifiers", () =>
        {
            var type = typeof(ChatDictationService);
            var input = type.GetNestedType("Input", BindingFlags.NonPublic)!;
            Check(Marshal.SizeOf(input) == (IntPtr.Size == 8 ? 40 : 28), "Incorrect native INPUT size");
            static (ushort Key, uint Flags) Stroke(object value)
            {
                var union = value.GetType().GetField("Data")!.GetValue(value)!;
                var keyboard = union.GetType().GetField("Keyboard")!.GetValue(union)!;
                return ((ushort)keyboard.GetType().GetField("VirtualKey")!.GetValue(keyboard)!, (uint)keyboard.GetType().GetField("Flags")!.GetValue(keyboard)!);
            }
            var sequence = ((Array)type.GetMethod("BuildShortcut", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!).Cast<object>().Select(Stroke).ToArray();
            Check(sequence.SequenceEqual(new[] { ((ushort)0x5B, 0u), ((ushort)0x48, 0u), ((ushort)0x48, 2u), ((ushort)0x5B, 2u) }), "Shortcut includes unexpected key input");
            foreach (var count in new[] { 0u, 1u, 2u, 3u, 4u })
            {
                var cleanup = ((Array)type.GetMethod("BuildCleanup", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [count])!).Cast<object>().Select(Stroke).ToArray();
                Check(cleanup.Length == (count is 0 or 4 ? 0 : count == 2 ? 2 : 1), "Cleanup releases keys never injected");
                Check(cleanup.All(k => k.Flags == 2 && k.Key is 0x48 or 0x5B), "Cleanup alters unrelated held keys");
            }
        });
    }

    private sealed class Fixture : IDisposable
    {
        public H2Notes.Avalonia.App App { get; } = new();
        public AiProfile Profile { get; } = new() { Name = "Saved test profile", Protocol = AiProtocol.OpenAiResponses,
            BaseUrl = "https://api.openai.com/v1", Model = "gpt-5.2", ReasoningEffort = "low", RequestReasoningSummary = true, TimeoutSeconds = 123 };
        public AiConversation Conversation { get; } = new();
        public ProjectRecord Project { get; }
        public AiChatPanel Panel { get; }
        public Window Window { get; }
        public TextBox Input { get; }
        public Fixture()
        {
            Conversation.ProfileId = Profile.Id; App.LocalSettings.Ai.Profiles = [Profile];
            Project = new() { Name = "Options test project", Conversations = [Conversation] };
            Panel = new AiChatPanel(App, () => throw new Exception("Options tests must not call AI"))
            { PersistComposerPermissions = _ => { } };
            Panel.SetProject(Project);
            Window = new Window { Width = 340, Height = 700, Content = Panel }; Window.Show(); Pump();
            Input = Named<TextBox>(Panel, "ChatComposer");
        }
        public void Dispose() { foreach (var dialog in Window.OwnedWindows.ToArray()) dialog.Close(false); Window.Close(); Pump(); }
    }

    private sealed class NoRecordingDictation : IChatDictationService
    {
        public int Calls;
        public bool IsSupported => true;
        public ChatDictationResult TryOpen(nint handle) { Calls++; return new(false, "Test fake: no native shortcut"); }
    }
}

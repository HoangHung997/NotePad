using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

internal static class ThinkingUiTests
{
    public static void Run(Action<string, Action> test)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static void PumpUntil(Func<bool> done, Func<string>? diagnostic = null)
        {
            var end = DateTime.UtcNow.AddSeconds(8);
            while (!done() && DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
            Check(done(), "Timed out waiting for test stream/UI. " + diagnostic?.Invoke());
        }
        static T Named<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
        test("Thinking follows newest wrapped text, rolling buffer, reopen and resize", () =>
        {
            var message = new AiMessage { Role = "assistant", Status = "streaming" };
            var bubble = new ChatMessageView(message);
            var window = new Window { Content = new StackPanel { Children = { bubble } }, Width = 360, Height = 500 };
            window.Show();
            try
            {
                var thinking = Named<Expander>(bubble, "MessageThinking");
                var summary = Named<TextBlock>(bubble, "MessageThinkingSummary");
                var scroll = (ScrollViewer)thinking.Content!;
                void Layout() { for (var i = 0; i < 4; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); } }
                void AtEnd()
                {
                    Layout();
                    Check(scroll.Extent.Height > scroll.Viewport.Height + 100, "Fixture did not overflow");
                    Check(Math.Abs(scroll.Offset.Y - (scroll.Extent.Height - scroll.Viewport.Height)) < 2,
                        $"Thinking is not at end: {scroll.Offset.Y}, extent={scroll.Extent.Height}, viewport={scroll.Viewport.Height}");
                }
                thinking.IsExpanded = true;
                bubble.SetThinking(string.Join("\n", Enumerable.Range(1, 80).Select(i => $"Provider progress {i}: newest streaming line.")));
                AtEnd();
                scroll.Offset = default;
                bubble.SetThinking(string.Join("\n", Enumerable.Range(81, 80).Select(i => $"Provider progress {i}: newest streaming line.")));
                AtEnd();
                thinking.IsExpanded = false; Layout();
                bubble.SetThinking(string.Join("\n", Enumerable.Range(161, 80).Select(i => $"Provider progress {i}: newest streaming line.")));
                Layout();
                Check(summary.Text!.Contains("Provider progress 240") && !summary.Text.Contains("Provider progress 161"),
                    "Collapsed thinking did not replace its one-line summary with the newest provider line");
                thinking.IsExpanded = true; AtEnd();
                window.Width = 280; AtEnd();
                message.Content = "Answer"; bubble.Refresh(); Layout();
                Check(!thinking.IsVisible && ((SelectableTextBlock)scroll.Content!).Text == "", "Thinking remained after answer");
            }
            finally { window.Close(); }
        });
        test("Assistant Markdown renders natural structure, fenced tables and Unicode escapes", () =>
        {
            var markdown = "## \\uD83D\\uDCDD Tổng quan\n\n**Tchom** đã xong.\n\n- [x] Hợp đồng\n- [ ] Hoàn công\n\n| Dự án | Trạng thái |\n|---|---|\n| Tchom | Đã xong |\n\n> Diện tích đã được cập nhật.\n\n```text\nKrong 11,49 ha\n```\n\n```text\n| TT | Dự án | Diện tích |\n|---|---|---|\n| 1 | Ia Tchom 1 | 3 |\n| 2 | Sê San 4A | 12,51 |\n```";
            var assistant = new AiMessage { Role = "assistant", Status = "complete", Content = markdown };
            var bubble = new ChatMessageView(assistant);
            var window = new Window { Content = bubble, Width = 480, Height = 700 }; window.Show(); Dispatcher.UIThread.RunJobs();
            try
            {
                var rendered = Named<MarkdownMessageView>(bubble, "MessageMarkdownBody");
                Check(rendered.IsVisible && !bubble.Body.IsVisible && bubble.Body.Text == markdown, "Assistant Markdown source/render surface mismatch");
                var heading = rendered.GetVisualDescendants().OfType<SelectableTextBlock>().Single(c => c.Name == "MarkdownHeading");
                var headingText = string.Concat(heading.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
                Check(heading.FontSize is > 13 and <= 18, "Chat heading is still oversized");
                Check(headingText.Contains("📝") && !rendered.Markdown.Contains("\\uD83D", StringComparison.Ordinal), "Literal Unicode escape was not normalized for display");
                Check(rendered.GetVisualDescendants().Count(c => c.Name == "MarkdownListItem") == 2, "Markdown checklist/list not rendered");
                Check(rendered.GetVisualDescendants().Count(c => c.Name == "MarkdownTable") == 2, "Markdown or fenced table not rendered as a real table");
                Check(rendered.GetVisualDescendants().Any(c => c.Name == "MarkdownQuote"), "Markdown quote not rendered");
                Check(rendered.GetVisualDescendants().Count(c => c.Name == "MarkdownCode") == 1, "A fenced table stayed as code or ordinary code was lost");
                Check(AiProjectContext.Instructions.Contains("bảng Markdown", StringComparison.Ordinal)
                    && AiProjectContext.Instructions.Contains("không bọc bảng trong code fence", StringComparison.Ordinal),
                    "Vision transcription guidance no longer preserves table structure");

                var user = new ChatMessageView(new AiMessage { Role = "user", Status = "complete", Content = "**literal user text**" });
                Check(user.Body.IsVisible && user.Body.Text == "**literal user text**", "User-authored text was unexpectedly reformatted");
            }
            finally { window.Close(); }
        });
        test("Composer Enter sends and Shift Enter stays available for a newline", () =>
        {
            var app = new H2Notes.Avalonia.App();
            var project = new ProjectRecord { Name = "Keyboard fixture" };
            var panel = new AiChatPanel(app); panel.SetProject(project);
            var window = new Window { Content = panel, Width = 420, Height = 700 }; window.Show(); Dispatcher.UIThread.RunJobs();
            try
            {
                var input = Named<TextBox>(panel, "ChatComposer");
                var marker = (CheckBox)typeof(AiChatPanel).GetField("_markerMode", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
                marker.IsChecked = true; input.Focus(); input.Text = "dòng chưa gửi"; input.CaretIndex = input.Text.Length;
                var shift = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Shift };
                input.RaiseEvent(shift); Dispatcher.UIThread.RunJobs();
                Check(input.AcceptsReturn && project.Conversations.SelectMany(c => c.Messages).Count() == 0,
                    "Shift+Enter sent a message instead of remaining a multiline editor gesture");

                input.Text = "gửi bằng Enter"; input.CaretIndex = input.Text.Length;
                var enter = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.None };
                input.RaiseEvent(enter);
                PumpUntil(() => project.Conversations.SelectMany(c => c.Messages).Any());
                var sent = project.Conversations.SelectMany(c => c.Messages).Single();
                Check(enter.Handled && sent.IsTimelineMarker && sent.Content == "gửi bằng Enter", "Plain Enter did not send the composer message");
            }
            finally { panel.Cancel(); window.Close(); }
        });
        test("Provider thinking is expandable, ephemeral and hidden at first answer; Send no longer opens confirmation", () =>
        {
            var app = new H2Notes.Avalonia.App(); var profile = new AiProfile { Model = "fixture" };
            app.LocalSettings.Ai.Profiles = [profile]; app.LocalSettings.Ai.SelectedId = profile.Id;
            var notebook = new NoteRecord { NoteKind = "ai-chat", Title = "Thinking fixture" };
            var stream = new GatedStream(); var handler = new Handler(stream);
            var panel = new AiChatPanel(app, () => new AiClient(handler)); panel.SetStandalone(notebook);
            var window = new Window { Content = panel, Width = 420, Height = 700 }; window.Show(); Dispatcher.UIThread.RunJobs();
            Task? send = null;
            try
            {
                Named<TextBox>(panel, "ChatComposer").Text = "Summarize the selected project";
                send = (Task)typeof(AiChatPanel).GetMethod("SendOrSave", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null)!;
                PumpUntil(() => handler.Calls == 1 && panel.GetVisualDescendants().OfType<ChatMessageView>().Any(b => b.Message.Role == "assistant"));
                var bubble = panel.GetVisualDescendants().OfType<ChatMessageView>().Single(b => b.Message.Role == "assistant");
                var thinking = Named<Expander>(bubble, "MessageThinking"); thinking.IsExpanded = true; Dispatcher.UIThread.RunJobs();
                var thinkingText = (SelectableTextBlock)((ScrollViewer)thinking.Content!).Content!;
                PumpUntil(() => thinkingText.Text == "Provider progress fixture", () => $"Status={bubble.Message.Status}; error={bubble.Message.ErrorText}; display={thinkingText.Text}; chunks={stream.ChunksRead}; task={send.Status}");
                Check(thinking.IsVisible && thinking.IsExpanded, "Thinking not expandable");
                Check(bubble.Message.Content == "", "Thinking leaked to answer");
                Check(handler.Body!.Contains("Summarize the selected project"), "Standalone prompt omitted from provider request");
                Check(!JsonSerializer.Serialize(notebook).Contains("Provider progress fixture"), "Transient reasoning was persisted");
                stream.Answer.TrySetResult();
                PumpUntil(() => bubble.Message.Content == "Visible answer" && !thinking.IsVisible);
                Check(!send.IsCompleted && thinkingText.Text == "", "Thinking not cleared before response completion");
                stream.Finish.TrySetResult(); PumpUntil(() => send.IsCompleted); send.GetAwaiter().GetResult();
                Check(notebook.AiConversations.Single().Messages.Last().Content == "Visible answer", "Final answer lost");
                Check(!JsonSerializer.Serialize(notebook).Contains("Provider progress fixture"), "Saved final history contains reasoning");
                Check(handler.Calls == 1, "Unexpected retry");
            }
            finally { stream.Answer.TrySetResult(); stream.Finish.TrySetResult(); panel.Cancel(); if (send is not null) PumpUntil(() => send.IsCompleted); window.Close(); }
        });
        test("Cancelled and failed thinking does not remain visible or become answer text", () =>
        {
            foreach (var status in new[] { "interrupted", "error", "complete" })
            {
                var message = new AiMessage { Role = "assistant", Status = "streaming" };
                var bubble = new ChatMessageView(message); var window = new Window { Content = bubble }; window.Show(); Dispatcher.UIThread.RunJobs();
                try
                {
                    bubble.SetThinking("Transient provider text"); var thinking = Named<Expander>(bubble, "MessageThinking"); thinking.IsExpanded = true;
                    var thinkingText = (SelectableTextBlock)((ScrollViewer)thinking.Content!).Content!;
                    message.Status = status; bubble.Refresh();
                    Check(!thinking.IsVisible && !thinking.IsExpanded, "Thinking visible after " + status);
                    Check(thinkingText.Text == "" && message.Content == "", "Thinking not discarded");
                }
                finally { window.Close(); }
            }
        });
    }

    private sealed class Handler(GatedStream stream) : HttpMessageHandler
    {
        public int Calls;
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Body = await request.Content!.ReadAsStringAsync(token);
            return new(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        }
    }
    private sealed class GatedStream : Stream
    {
        public TaskCompletionSource Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly byte[][] _chunks = [Encoding.UTF8.GetBytes("{\"message\":{\"thinking\":\"Provider progress fixture\",\"content\":\"\"},\"done\":false}\n"),
            Encoding.UTF8.GetBytes("{\"message\":{\"content\":\"Visible answer\"},\"done\":false}\n"), Encoding.UTF8.GetBytes("{\"done\":true}\n")];
        private int _chunk, _offset;
        public int ChunksRead => _chunk;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_chunk >= _chunks.Length) return 0;
            if (_chunk == 1) await Answer.Task.WaitAsync(token);
            if (_chunk == 2) await Finish.Task.WaitAsync(token);
            var count = Math.Min(buffer.Length, _chunks[_chunk].Length - _offset);
            _chunks[_chunk].AsMemory(_offset, count).CopyTo(buffer); _offset += count;
            if (_offset == _chunks[_chunk].Length) { _chunk++; _offset = 0; }
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }
}

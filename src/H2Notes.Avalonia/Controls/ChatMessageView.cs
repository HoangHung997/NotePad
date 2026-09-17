using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

public sealed class ChatMessageView : Border
{
    public AiMessage Message { get; }
    // Kept as the canonical display text surface for existing actions/tests. Assistant messages
    // render this text through MarkdownMessageView while Body itself stays hidden.
    public SelectableTextBlock Body { get; }
    public StackPanel Actions { get; } = new() { Spacing = 5 };
    private readonly TextBlock _time;
    private readonly MarkdownMessageView _markdownBody = new() { Name = "MessageMarkdownBody" };
    private readonly SelectableTextBlock _thinkingText = new() { Name = "MessageThinkingText", FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _thinkingSummary = new()
    {
        Name = "MessageThinkingSummary", FontSize = 12, TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly Expander _thinking;
    private readonly ScrollViewer _thinkingScroll;
    private bool _thinkingScrollQueued;
    private readonly TextBlock _error = new() { Name = "MessageError", FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = RichEditor.Brush("#9C422B") };

    public ChatMessageView(AiMessage message)
    {
        Message = message;
        var user = message.Role == "user";
        HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        Padding = new Thickness(12, 9); MaxWidth = 300;
        CornerRadius = user ? new CornerRadius(12, 12, 3, 12) : new CornerRadius(12, 12, 12, 3);
        Background = RichEditor.Brush(user ? "#F4E7DC" : "#FFFFFF");
        BorderBrush = RichEditor.Brush(user ? "#E7CCBA" : "#E5DCD3"); BorderThickness = new Thickness(1);
        Body = new SelectableTextBlock { Name = "MessageBody", Text = message.Content, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        Body.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty) RefreshRenderedBody();
        };
        _time = new TextBlock { Name = "MessageTime", FontSize = 10, Foreground = RichEditor.Brush("#857568"), HorizontalAlignment = HorizontalAlignment.Right };
        var title = new TextBlock { Text = message.IsTimelineMarker ? "Mốc ghi nhớ · chỉ lưu trong H2" : user ? "Bạn" : "AI · " + message.Model,
            FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = RichEditor.Brush("#796C62"), TextWrapping = TextWrapping.Wrap };
        _thinkingScroll = new ScrollViewer { Name = "MessageThinkingScroll", MaxHeight = 150, Content = _thinkingText,
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _thinking = new Expander { Name = "MessageThinking", Header = _thinkingSummary, FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch, Content = _thinkingScroll };
        _thinking.Expanded += (_, _) => QueueThinkingScroll();
        _thinkingScroll.ScrollChanged += (_, e) =>
        {
            if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0) FollowThinking();
        };
        ToolTip.SetTip(_thinking, "Thu gọn: xem một dòng tiến trình mới nhất. Mở rộng: xem toàn bộ tiến trình model cung cấp. Không lưu vào lịch sử.");
        Child = new StackPanel { Spacing = 6, Children = { title, _thinking, _markdownBody, Body, _error, Actions, _time } };
        SetThinking("");
        Refresh();
    }

    public void SetThinking(string text)
    {
        var visible = Message.Role == "assistant" && Message.Status == "streaming" && Message.Content.Length == 0;
        _thinking.IsVisible = visible;
        var next = !visible ? "" : text.Length > 0 ? text
            : "Đang chờ model. Nội dung suy nghĩ hoặc bản tóm tắt sẽ hiện ở đây nếu máy chủ cung cấp.";
        _thinkingSummary.Text = visible ? LatestThinkingSummary(next) : "Đang suy nghĩ…";
        if (_thinkingText.Text != next)
        {
            _thinkingText.Text = next;
            QueueThinkingScroll();
        }
        if (!visible) _thinking.IsExpanded = false;
    }

    private static string LatestThinkingSummary(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Đang chờ model…";
        var lines = text.Replace("\r", "", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var latest = lines.LastOrDefault() ?? text.Trim();
        // Keep the newest provider line rather than a growing transcript. TextTrimming guarantees
        // the collapsed header occupies one visual line at narrow widths.
        return "Đang suy nghĩ · " + latest;
    }

    private void RefreshRenderedBody()
    {
        var text = Body.Text ?? "";
        var renderMarkdown = Message.Role == "assistant" && !Message.IsTimelineMarker;
        _markdownBody.IsVisible = renderMarkdown && text.Length > 0;
        Body.IsVisible = !renderMarkdown && text.Length > 0;
        if (renderMarkdown) _markdownBody.SetMarkdown(text);
        else if (_markdownBody.Markdown.Length > 0) _markdownBody.SetMarkdown("");
    }

    private void QueueThinkingScroll()
    {
        if (_thinkingScrollQueued) return;
        _thinkingScrollQueued = true;
        // Wait for wrapped text to be measured, including updates to the rolling 24k buffer.
        Dispatcher.UIThread.Post(() => { _thinkingScrollQueued = false; FollowThinking(); }, DispatcherPriority.Background);
    }

    private void FollowThinking()
    {
        if (_thinking.IsVisible && _thinking.IsExpanded && Message.Status == "streaming" && Message.Content.Length == 0)
            _thinkingScroll.ScrollToEnd();
    }

    public void Refresh()
    {
        if (Body.Text != Message.Content) Body.Text = Message.Content;
        RefreshRenderedBody();
        if (Message.Status != "streaming" || Message.Content.Length > 0) SetThinking("");
        _error.Text = Message.ErrorText;
        _error.IsVisible = Message.ErrorText.Length > 0;
        var time = AiHistory.LocalTime(Message);
        var status = Message.IsTimelineMarker ? "Mốc ghi nhớ" : Message.Status switch
        {
            "streaming" => Message.Content.Length > 0 ? "Đang trả lời" : "Đang xử lý", "interrupted" => "Chưa hoàn tất", "error" => "Có lỗi", _ => ""
        };
        _time.Text = (time?.ToString("HH:mm") ?? "Không rõ giờ") + (status.Length == 0 ? "" : " · " + status);
        ToolTip.SetTip(_time, time is null ? "Tin cũ không có thời gian lưu; không tự đặt lại giờ."
            : time.Value.ToString("dd/MM/yyyy HH:mm:ss") + " · giờ trên máy này");
    }

    public static Control TimeDivider(AiMessage message)
    {
        var local = AiHistory.LocalTime(message);
        return new Border
        {
            Name = "ChatTimeDivider", HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(10, 4), Margin = new Thickness(0, 8),
            CornerRadius = new CornerRadius(12), Background = RichEditor.Brush("#EAE4DC"),
            Child = new TextBlock { Text = local?.ToString("HH:mm · dd/MM/yyyy") ?? "Tin cũ · không rõ thời gian", FontSize = 10, Foreground = RichEditor.Brush("#796C62") }
        };
    }
}

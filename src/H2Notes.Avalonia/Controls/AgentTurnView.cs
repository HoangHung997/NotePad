using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

/// <summary>A turn keeps activity, decisions and the final document separate. Event sequence is the replay identity.</summary>
public sealed class AgentTurnView : StackPanel
{
    private readonly SelectableTextBlock _user = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
    private readonly Expander _activity;
    private readonly StackPanel _activityRows = new() { Spacing = 7 };
    private readonly TextBlock _activityTitle = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#796C62") };
    private readonly AgentApprovalPanel _approval = new();
    private readonly MarkdownMessageView _answer = new();
    private readonly SelectableTextBlock _error = new() { Name = "AgentTurnError", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9C422B") };
    private readonly StackPanel _artifacts = new() { Spacing = 6 };
    private readonly Button _copyAnswer = new() { Content = "Sao chép trả lời", FontSize = 11, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _cancelQueued = new() { Content = "Hủy lượt xếp hàng này", FontSize = 11, IsVisible = false };
    private readonly SortedDictionary<long, H2AgentProgress> _events = [];
    private H2AgentTaskSummary? _last;
    private IH2AgentAdapter? _adapter;
    private int _visibleEvents = 40;
    public long LastSequence => _events.Count == 0 ? -1 : _events.Keys.Last();
    public int EventCount => _events.Count;

    public AgentTurnView(bool includeUser = false)
    {
        Name = "AgentTurn"; Spacing = 12; HorizontalAlignment = HorizontalAlignment.Stretch;
        _cancelQueued.Click += (_, _) => { if (_last is { } task) _adapter?.CancelTask(task.TaskId); };
        _copyAnswer.Click += async (_, _) => { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(_last?.FinalText ?? ""); };
        _answer.OpenResource = target =>
        {
            var file = _last?.Evidence.FirstOrDefault(e => e.EvidenceId == target || e.LocalPath == target || e.SourceUri == target);
            if (file is not null) OpenArtifact(file);
            else if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception ex) { _error.Text = ex.Message; _error.IsVisible = true; }
            }
            else { _error.Text = "Liên kết chưa có tệp/bằng chứng đã đăng ký. Xem mục kết quả của lượt này."; _error.IsVisible = true; }
        };
        Children.Add(new Border { IsVisible = includeUser, Background = Brush.Parse("#F4E7DC"), CornerRadius = new CornerRadius(18),
            Padding = new Thickness(14, 10), Margin = new Thickness(42, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Right, Child = _user });
        if(includeUser)Children.Add(AgentBrand.Heading());
        _activity = new Expander { Name = "AgentTurnActivity", Header = _activityTitle, Content = _activityRows,
            HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8),
            Background=Brush.Parse("#F2F3F4"),BorderBrush=Brush.Parse("#DADDE1"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(6) };
        Children.Add(_activity); Children.Add(_cancelQueued); Children.Add(_approval); Children.Add(_error); Children.Add(_answer); Children.Add(_artifacts); Children.Add(_copyAnswer);
    }

    public bool Present(IH2AgentAdapter? adapter, H2AgentTaskObservation observation)
    {
        var task = observation.Summary;
        var changed = _last is null || _last.UpdatedUtc != task.UpdatedUtc || _last.Status != task.Status;
        var newEvents = false;
        foreach (var item in observation.Progress)
            if (_events.TryAdd(item.Sequence, item)) newEvents = true;
        var answerText = task.FinalText ?? (H2AgentActivity.IsTerminal(task.Status) ? "" : observation.StreamingText) ?? "";
        var contentChanged = changed || newEvents || _answer.Markdown != answerText;
        if (!contentChanged && H2AgentActivity.IsTerminal(task.Status)) return false;
        var wasTerminal = _last is not null && H2AgentActivity.IsTerminal(_last.Status);
        var terminal = H2AgentActivity.IsTerminal(task.Status);
        _last = task; _adapter = adapter; _user.Text = task.Goal;
        _cancelQueued.IsVisible = task.Status == H2AgentTaskStatus.Queued;
        _answer.SetMarkdown(answerText);
        _copyAnswer.IsVisible = terminal && answerText.Length > 0;
        _error.Text = task.Error; _error.IsVisible = !string.IsNullOrWhiteSpace(task.Error);
        if (adapter is not null) _approval.Present(adapter, task.TaskId, task.PendingApproval);
        else _approval.IsVisible = false;
        var stopping = !terminal && _events.Values.LastOrDefault()?.Code == "cancel-requested";
        var duration = (terminal ? task.UpdatedUtc : DateTime.UtcNow) - task.CreatedUtc;
        var elapsed = duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes} phút {duration.Seconds} giây" : $"{Math.Max(0, (int)duration.TotalSeconds)} giây";
        var count = _events.Values.Count(e => e.Code is "tool-ok" or "tool-error");
        _activityTitle.Text = task.PendingApproval is not null ? "Cần bạn xác nhận · " + elapsed
            : stopping ? "Đang dừng…" : task.Status == H2AgentTaskStatus.Queued ? "Đã xếp lượt tiếp theo"
            : terminal ? $"Đã xử lý trong {elapsed} · {count} thao tác"
            : "Đang làm việc · " + elapsed + (_events.Count == 0 ? "" : " · " + H2AgentActivity.Label(_events.Values.Last()));
        if (terminal && !wasTerminal) _activity.IsExpanded = false;
        if (newEvents) RenderEvents();
        if (changed) RenderArtifacts(adapter, task);
        return contentChanged;
    }

    private void RenderEvents()
    {
        _activityRows.Children.Clear();
        if (_events.Count > _visibleEvents)
        {
            var older = new Button { Content = $"Xem hoạt động trước · {_events.Count} mục", FontSize = 11 };
            older.Click += (_, _) => { _visibleEvents += 40; RenderEvents(); };
            _activityRows.Children.Add(older);
        }
        foreach (var item in _events.Values.TakeLast(_visibleEvents))
        {
            var row = new SelectableTextBlock { Name = "AgentActivityRow", Text = H2AgentActivity.Label(item), FontSize = 12,
                TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse(item.Code == "tool-error" ? "#9C422B" : "#796C62") };
            ToolTip.SetTip(row, item.AtUtc.ToLocalTime().ToString("HH:mm:ss") + " · " + item.Kind + "/" + item.Code);
            if (item.Code is "tool-result" or "script" or "plan")
                _activityRows.Children.Add(new Expander { Header = row, FontSize = 11,
                    Content = new SelectableTextBlock { Text = item.Message, TextWrapping = TextWrapping.Wrap, FontSize = 11 } });
            else _activityRows.Children.Add(row);
        }
    }

    private void RenderArtifacts(IH2AgentAdapter? adapter, H2AgentTaskSummary task)
    {
        _artifacts.Children.Clear();
        var evidenceRows = new StackPanel { Spacing = 5 };
        foreach (var evidence in task.Evidence.GroupBy(e => e.Kind == "artifact" && e.Sha256 is { Length: > 0 }
            ? "file:" + e.Sha256 + ":" + Path.GetFileName(e.LocalPath) : e.EvidenceId).Select(group => group.Last()))
        {
            var verified = evidence.VerificationPassed;
            var caption = verified.HasValue ? (verified.Value ? "✓ Kiểm tra đạt" : "! Kiểm tra chưa đạt")
                : !string.IsNullOrWhiteSpace(evidence.LocalPath) ? "Tệp · " + Path.GetFileName(evidence.LocalPath) : "Bằng chứng · " + evidence.Kind;
            var button = new Button { Name = "AgentArtifact", Content = caption, HorizontalAlignment = HorizontalAlignment.Left,
                MaxWidth = 540, FontSize = 12 };
            ToolTip.SetTip(button, evidence.Summary);
            button.Click += (_, _) => OpenArtifact(adapter?.GetEvidence(evidence.EvidenceId) ?? evidence);
            if (evidence.Kind == "artifact") _artifacts.Children.Add(button);
            else evidenceRows.Children.Add(button);
        }
        if (evidenceRows.Children.Count > 0) _artifacts.Children.Add(new Expander { Header = $"Bằng chứng và kiểm tra · {evidenceRows.Children.Count}",
            Content = evidenceRows, HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 });
    }

    private void OpenArtifact(H2AgentEvidence evidence)
    {
        if (this.GetVisualAncestors().OfType<AgentChatSurface>().FirstOrDefault() is { } surface) surface.OpenArtifact(evidence, _adapter);
        else new AgentArtifactWindow(evidence, _adapter).Show();
    }
}

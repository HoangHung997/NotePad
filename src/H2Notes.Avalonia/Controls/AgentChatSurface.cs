using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

/// <summary>Shared timeline/inspector host. It projects Agent facts and never executes transcript text.</summary>
public sealed class AgentChatSurface : Grid
{
    public StackPanel Timeline { get; } = new() { Spacing = 16, Margin = new Thickness(18, 12), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Stretch };
    public ScrollViewer Scroll { get; } = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Button _latest = new() { Name = "AgentLatestActivity", Content = "↓ Hoạt động mới", HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 10), IsVisible = false };
    private readonly Dictionary<Guid, AgentTurnView> _turns = [];
    private bool _follow = true;
    private string _identity = "";
    private int _visibleTurns = 30;
    private IReadOnlyList<H2AgentTaskSummary> _tasks = [];
    private IH2AgentAdapter? _adapter;
    private readonly Button _older = new() { Name = "AgentOlderTurns", Content = "Xem các lượt trước", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Grid _timelineHost = new();
    private readonly Border _inspector = new() { IsVisible = false, Background = Brush.Parse("#FCFAF7"), Padding = new Thickness(12) };
    public event Action<H2AgentEvidence>? ExternalArtifactRequested;
    public event Action<bool>? DocumentPageChanged;
    public bool IsDocumentPage => _inspector.IsVisible && Bounds.Width < 760;
    public void AttachComposer(Control composer)
    {
        if(composer.Parent==_timelineHost)return;
        (composer.Parent as Panel)?.Children.Remove(composer);
        _timelineHost.RowDefinitions = new RowDefinitions("*,Auto");
        Grid.SetRow(composer,1); _timelineHost.Children.Add(composer);
    }

    public AgentChatSurface()
    {
        Name = "AgentChatSurface";
        AutomationProperties.SetName(this, "Hội thoại Agent");
        Scroll.Name = "AgentConversationScroll";
        AutomationProperties.SetName(Scroll, "Lịch sử hội thoại Agent");
        AutomationProperties.SetName(_latest, "Đi tới hoạt động Agent mới nhất");
        AutomationProperties.SetName(_older, "Xem các lượt Agent trước");
        ColumnDefinitions = new ColumnDefinitions("*,0");
        Scroll.Content = Timeline; _timelineHost.Children.Add(Scroll); _timelineHost.Children.Add(_latest);
        Children.Add(_timelineHost); Children.Add(_inspector);
        SizeChanged += (_, _) => ArrangeInspector();
        Scroll.ScrollChanged += (_, e) =>
        {
            if (e.ExtentDelta == default && e.ViewportDelta == default && e.OffsetDelta != default)
                _follow = Scroll.Extent.Height - Scroll.Viewport.Height - Scroll.Offset.Y < 48;
            if (_follow && e.ExtentDelta.Y != 0) FollowLatest();
        };
        _latest.Click += (_, _) => { _follow = true; FollowLatest(); };
        _older.Click += (_, _) =>
        {
            _follow = false;
            var height = Scroll.Extent.Height; var offset = Scroll.Offset;
            _visibleTurns += 30;
            PresentTasks(_adapter, _tasks, _identity);
            Dispatcher.UIThread.Post(() => Scroll.Offset = new Vector(offset.X, offset.Y + Math.Max(0, Scroll.Extent.Height - height)), DispatcherPriority.Loaded);
        };
    }

    public void NotifyActivity()
    {
        if (_follow) FollowLatest(); else _latest.IsVisible = true;
    }

    public void FollowLatest() => Dispatcher.UIThread.Post(() => { Scroll.ScrollToEnd(); _latest.IsVisible = false; }, DispatcherPriority.Loaded);

    public void OpenArtifact(H2AgentEvidence evidence, IH2AgentAdapter? adapter = null)
    {
        if (ExternalArtifactRequested is not null) { ExternalArtifactRequested(evidence); return; }
        var close = new Button { Content = "← Hội thoại", FontSize = 12 };
        AutomationProperties.SetName(close, "Quay lại hội thoại Agent");
        close.Click += (_, _) => { _inspector.IsVisible = false; _inspector.Child = null; ArrangeInspector(); };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 8 };
        root.Children.Add(close);
        var preview = new AgentArtifactView(evidence, adapter); Grid.SetRow(preview, 1); root.Children.Add(preview);
        _inspector.Child = root; _inspector.IsVisible = true; ArrangeInspector();
    }

    private void ArrangeInspector()
    {
        var wide = Bounds.Width >= 760;
        ColumnDefinitions[1].Width = _inspector.IsVisible && wide ? new GridLength(Math.Min(440, Bounds.Width * .4)) : new GridLength(0);
        Grid.SetColumn(_inspector, wide ? 1 : 0);
        _timelineHost.IsVisible = !_inspector.IsVisible || wide;
        DocumentPageChanged?.Invoke(IsDocumentPage);
    }

    public void PresentTasks(IH2AgentAdapter? adapter, IReadOnlyList<H2AgentTaskSummary> tasks, string identity)
    {
        if (_identity != identity)
        {
            _identity = identity; Timeline.Children.Clear(); _turns.Clear(); _follow = true; _visibleTurns = 30;
            _inspector.Child = null; _inspector.IsVisible = false; ArrangeInspector();
        }
        _tasks = tasks; _adapter = adapter;
        var visible = tasks.OrderBy(t => t.CreatedUtc).TakeLast(_visibleTurns).ToArray();
        foreach (var old in _turns.Keys.Except(visible.Select(t => t.TaskId)).ToArray())
        { Timeline.Children.Remove(_turns[old]); _turns.Remove(old); }
        if (tasks.Count > _visibleTurns) { if (!Timeline.Children.Contains(_older)) Timeline.Children.Insert(0, _older); }
        else Timeline.Children.Remove(_older);
        if (tasks.Count == 0 && Timeline.Children.Count == 0)
            Timeline.Children.Add(new TextBlock { Name = "AgentChatEmpty", Text = "Bạn muốn làm gì hôm nay?",
                FontSize = 19, Margin = new Thickness(10, 35), Foreground = Brush.Parse("#796C62") });
        foreach (var task in visible)
        {
            if (!_turns.TryGetValue(task.TaskId, out var turn))
            {
                var empty = Timeline.Children.FirstOrDefault(c => c.Name == "AgentChatEmpty");
                if (empty is not null) Timeline.Children.Remove(empty);
                turn = new AgentTurnView(true); _turns.Add(task.TaskId, turn);
                Timeline.Children.Insert(Array.IndexOf(visible, task) + (Timeline.Children.Contains(_older) ? 1 : 0), turn);
            }
            if (turn.Present(adapter, adapter?.ObserveTask(task.TaskId, turn.LastSequence) ?? new H2AgentTaskObservation(task, []))) NotifyActivity();
        }
    }
}

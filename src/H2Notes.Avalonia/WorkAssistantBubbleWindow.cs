using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace H2Notes.Avalonia;

public enum WorkAssistantBubbleState
{
    Idle = 0,
    Working = 1,
    Attention = 2,
    Completed = 3
}

internal sealed record WorkAssistantScreenSnapshot(
    string Id,
    PixelRect WorkArea,
    double Scaling,
    bool IsPrimary);

internal static class WorkAssistantPlacement
{
    internal static PixelPoint AttachPanel(PixelPoint bubble, PixelSize bubbleSize,
        PixelSize panelSize, PixelRect area, int gap)
    {
        var right = bubble.X + bubbleSize.Width + gap;
        var left = bubble.X - panelSize.Width - gap;
        var x = right + panelSize.Width <= area.Right ? right : left;
        var y = bubble.Y + bubbleSize.Height - panelSize.Height;
        return Clamp(new PixelPoint(x, y), area, panelSize);
    }

    internal static PixelPoint Resolve(
        WorkAssistantSettings settings,
        IReadOnlyList<WorkAssistantScreenSnapshot> screens,
        double widthDip,
        double heightDip)
    {
        if (screens.Count == 0)
            return new PixelPoint(30, 30);

        var screen = screens.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(settings.PreferredMonitor)
                && item.Id.Equals(settings.PreferredMonitor, StringComparison.Ordinal))
            ?? screens.FirstOrDefault(item => item.IsPrimary)
            ?? screens[0];

        var scale = SafeScale(screen.Scaling);
        var width = Math.Max(1, (int)Math.Ceiling(widthDip * scale));
        var height = Math.Max(1, (int)Math.Ceiling(heightDip * scale));

        PixelPoint desired;
        if (settings.BubblePosition is { } saved
            && double.IsFinite(saved.XDip)
            && double.IsFinite(saved.YDip))
        {
            desired = new PixelPoint(
                screen.WorkArea.X + (int)Math.Round(saved.XDip * scale),
                screen.WorkArea.Y + (int)Math.Round(saved.YDip * scale));
        }
        else
        {
            const int margin = 18;
            desired = new PixelPoint(
                screen.WorkArea.Right - width - margin,
                screen.WorkArea.Bottom - height - margin);
        }

        return Clamp(desired, screen.WorkArea, new PixelSize(width, height));
    }

    internal static WorkAssistantBubblePosition Capture(
        PixelPoint position,
        WorkAssistantScreenSnapshot screen)
    {
        var scale = SafeScale(screen.Scaling);
        return new WorkAssistantBubblePosition(
            (position.X - screen.WorkArea.X) / scale,
            (position.Y - screen.WorkArea.Y) / scale);
    }

    internal static PixelPoint Clamp(
        PixelPoint desired,
        PixelRect workArea,
        PixelSize windowSize,
        int margin = 4)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
            return desired;

        var minX = workArea.X + margin;
        var minY = workArea.Y + margin;
        var maxX = Math.Max(minX, workArea.Right - Math.Max(1, windowSize.Width) - margin);
        var maxY = Math.Max(minY, workArea.Bottom - Math.Max(1, windowSize.Height) - margin);
        return new PixelPoint(
            Math.Clamp(desired.X, minX, maxX),
            Math.Clamp(desired.Y, minY, maxY));
    }

    internal static string ScreenId(PixelRect workArea, double scaling)
        => $"{workArea.X},{workArea.Y},{workArea.Width},{workArea.Height}@{SafeScale(scaling):0.###}";

    private static double SafeScale(double value)
        => double.IsFinite(value) && value > 0 ? value : 1d;
}

public sealed class WorkAssistantBubbleWindow : Window
{
    private const double BubbleWidth = 320;
    private const double BubbleSize = 54;
    private readonly WorkAssistantSettings _settings;
    private readonly Action _saveLocal;
    private readonly Action _activate;
    private readonly Border _shell;
    private readonly TextBlock _glyph;
    private readonly TextBlock _label;
    private readonly Border _brand;
    private readonly Grid _row;
    private readonly Button _menu;
    private readonly Button _close;
    private readonly ScrollViewer _ticker;
    private readonly DispatcherTimer _tickerTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private int _tickerPause;
    private bool _chatVisible;
    private bool _taskActive;
    private bool _expandLeft;
    private readonly DispatcherTimer _placementTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(220)
    };
    private bool _adjusting;
    private bool _opened;

    public WorkAssistantBubbleState State { get; private set; } = WorkAssistantBubbleState.Idle;
    public bool IsExpanded { get; private set; }
    public string ActivityText => _label.Text ?? "";

    public WorkAssistantBubbleWindow(
        WorkAssistantSettings settings,
        Action saveLocal,
        Action? activate = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _saveLocal = saveLocal ?? throw new ArgumentNullException(nameof(saveLocal));
        _activate = activate ?? (() => { });

        Title = "H2 Notes · Work Assistant";
        Width = MinWidth = MaxWidth = BubbleSize;
        Height = MinHeight = MaxHeight = BubbleSize;
        CanResize = false;
        Topmost = settings.AlwaysOnTop;
        ShowInTaskbar = false;
        CanMinimize = false;
        CanMaximize = false;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.Blur];

        _glyph = new TextBlock
        {
            Text = "H2",
            FontSize = 17,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _label = new TextBlock
        {
            Text = "Sẵn sàng",
            FontSize = 13,
            Name = "WorkAssistantBubbleActivity",
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            MaxLines = 1
        };
        _brand=new Border { Background=Brush.Parse("#A4573D"),CornerRadius=new CornerRadius(22),Width=40,Height=40,
            Child=_glyph }; _glyph.VerticalAlignment=VerticalAlignment.Center; _glyph.Foreground=Brushes.White;
        _row=new Grid { ColumnDefinitions=new ColumnDefinitions("Auto,*,Auto,Auto"),ColumnSpacing=0 };
        _ticker = new ScrollViewer { Name = "WorkAssistantBubbleTicker", Content = _label,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        _row.Children.Add(_brand); Grid.SetColumn(_ticker,1); _row.Children.Add(_ticker);
        var menu=Controls.AppIcon.Button(Controls.IconKind.More,"Tùy chọn Assistant"); menu.Padding=new Thickness(4); menu.MinWidth=24;
        menu.Click+=(_,_)=> { var popup=new ContextMenu(); var open=new MenuItem { Header="Mở trao đổi" }; open.Click+=(_,_)=>_activate();
            var hide=new MenuItem { Header="Ẩn bong bóng" }; hide.Click+=(_,_)=>Hide(); popup.Items.Add(open); popup.Items.Add(hide); popup.Open(menu); };
        var close=Controls.AppIcon.Button(Controls.IconKind.Close,"Ẩn bong bóng"); close.Padding=new Thickness(4); close.MinWidth=24; close.Click+=(_,_)=>Hide();
        _menu = menu; _close = close;
        Grid.SetColumn(menu,2); _row.Children.Add(menu); Grid.SetColumn(close,3); _row.Children.Add(close);
        _shell=new Border { Name = "WorkAssistantBubbleShell", CornerRadius=new CornerRadius(27),BorderThickness=new Thickness(1),Padding=new Thickness(6),Child=_row };
        var openItem = new MenuItem { Header = "Mở trao đổi" }; openItem.Click += (_, _) => _activate();
        var hideItem = new MenuItem { Header = "Ẩn bong bóng" }; hideItem.Click += (_, _) => Hide();
        _shell.ContextMenu = new ContextMenu { ItemsSource = new[] { openItem, hideItem } };
        Content = _shell;
        DesktopWindowChrome.AttachClickable(this, _shell, _activate);
        SetState(WorkAssistantBubbleState.Idle);

        _tickerTimer.Tick += (_, _) =>
        {
            if (!IsVisible || !IsExpanded) return;
            var max = Math.Max(0, _ticker.Extent.Width - _ticker.Viewport.Width);
            if (max <= 0 || _tickerPause-- > 0) return;
            if (_ticker.Offset.X >= max) { _ticker.Offset = default; _tickerPause = 35; }
            else { _ticker.Offset = new Vector(Math.Min(max, _ticker.Offset.X + 1.2), 0); if (_ticker.Offset.X >= max) _tickerPause = 40; }
        };
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) UpdateTicker(); };

        _placementTimer.Tick += (_, _) =>
        {
            _placementTimer.Stop();
            ClampAndPersistPosition();
        };
        PositionChanged += (_, _) =>
        {
            if (!_opened || _adjusting || !IsVisible) return;
            _placementTimer.Stop();
            _placementTimer.Start();
        };
        Opened += (_, _) =>
        {
            if (_opened) return;
            _opened = true;
            RestorePlacement();
        };
        Closed += (_, _) => { _placementTimer.Stop(); _tickerTimer.Stop(); };
    }

    public void ApplySettings()
    {
        Topmost = _settings.AlwaysOnTop;
        if (_opened && IsVisible)
            RestorePlacement();
    }

    public void SetState(WorkAssistantBubbleState state, string? detail = null, bool? taskActive = null)
    {
        State = state;
        _taskActive = taskActive ?? state == WorkAssistantBubbleState.Working;
        var (label, background, border, foreground) = state switch
        {
            WorkAssistantBubbleState.Working =>
                ("Đang làm", "#FFF2E8", "#D9A27B", "#9A4C31"),
            WorkAssistantBubbleState.Attention =>
                ("Cần xem", "#FFF5DD", "#D8B365", "#8A5A12"),
            WorkAssistantBubbleState.Completed =>
                ("Xong", "#EAF5EC", "#9CBFA2", "#3F7449"),
            _ =>
                ("Sẵn sàng", "#FCFAF7", "#D8CEC5", "#A4573D")
        };

        var text = string.IsNullOrWhiteSpace(detail) ? label : string.Join(" ", detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (_label.Text != text) { _label.Text = text; _ticker.Offset = default; _tickerPause = 30; }
        _shell.Background = Brush.Parse(background);
        _shell.BorderBrush = Brush.Parse(border);
        _glyph.Foreground = Brushes.White;
        _label.Foreground = Brush.Parse(foreground);
        ToolTip.SetTip(_shell, "Work Assistant · " + _label.Text + " · bấm để mở, kéo để di chuyển");
        ApplyShape();
    }

    public void SetChatVisible(bool visible)
    {
        if (_chatVisible == visible) return;
        _chatVisible = visible;
        ApplyShape();
    }

    private PixelPoint CircleAnchor()
        => new(Position.X + (IsExpanded && _expandLeft ? (int)Math.Round((Width - BubbleSize) * RenderScaling) : 0), Position.Y);

    private void ApplyShape()
    {
        var expanded = _taskActive && !_chatVisible;
        if (expanded == IsExpanded && _ticker.IsVisible == expanded) return;
        var anchor = CircleAnchor();
        var screen = ScreenSnapshotForCurrentWindow();
        var scale = double.IsFinite(RenderScaling) && RenderScaling > 0 ? RenderScaling : 1;
        if (expanded) _expandLeft = screen is not null && anchor.X + BubbleWidth * scale > screen.WorkArea.Right - 4;
        IsExpanded = expanded;
        ArrangeRow();
        _adjusting = true;
        MinWidth = 0; MaxWidth = double.PositiveInfinity;
        Width = expanded ? BubbleWidth : BubbleSize;
        MinWidth = MaxWidth = Width;
        if (_opened)
        {
            var desired = new PixelPoint(anchor.X - (expanded && _expandLeft ? (int)Math.Round((BubbleWidth - BubbleSize) * scale) : 0), anchor.Y);
            Position = screen is null ? desired : WorkAssistantPlacement.Clamp(desired, screen.WorkArea,
                new PixelSize((int)Math.Ceiling(Width * scale), (int)Math.Ceiling(BubbleSize * scale)));
        }
        _adjusting = false;
        _ticker.Offset = default; _tickerPause = 30;
        UpdateTicker();
    }

    private void ArrangeRow()
    {
        var expanded = IsExpanded;
        _ticker.IsVisible = _menu.IsVisible = _close.IsVisible = expanded;
        _row.ColumnSpacing = expanded ? 6 : 0;
        _row.ColumnDefinitions = new ColumnDefinitions(expanded && _expandLeft ? "Auto,Auto,*,Auto" : "Auto,*,Auto,Auto");
        Grid.SetColumn(_brand, expanded && _expandLeft ? 3 : 0);
        Grid.SetColumn(_ticker, expanded && _expandLeft ? 2 : 1);
        Grid.SetColumn(_menu, expanded && _expandLeft ? 1 : 2);
        Grid.SetColumn(_close, expanded && _expandLeft ? 0 : 3);
    }

    private void UpdateTicker()
    {
        if (IsVisible && IsExpanded) _tickerTimer.Start(); else _tickerTimer.Stop();
    }

    private void RestorePlacement()
    {
        var snapshots = ScreenSnapshots();
        var desired = WorkAssistantPlacement.Resolve(
            _settings,
            snapshots,
            BubbleSize,
            BubbleSize);
        _adjusting = true;
        var screen = snapshots.FirstOrDefault(s => s.WorkArea.Contains(desired));
        var scale = screen?.Scaling ?? RenderScaling;
        _expandLeft = IsExpanded && screen is not null && desired.X + Width * scale > screen.WorkArea.Right - 4;
        ArrangeRow();
        Position = new PixelPoint(desired.X - (IsExpanded && _expandLeft ? (int)Math.Round((Width - BubbleSize) * scale) : 0), desired.Y);
        _adjusting = false;
    }

    private void ClampAndPersistPosition()
    {
        var screen = ScreenSnapshotForCurrentWindow();
        if (screen is null) return;

        var scale = double.IsFinite(RenderScaling) && RenderScaling > 0
            ? RenderScaling
            : screen.Scaling;
        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale)));
        var clamped = WorkAssistantPlacement.Clamp(Position, screen.WorkArea, size);
        if (clamped != Position)
        {
            _adjusting = true;
            Position = clamped;
            _adjusting = false;
        }

        _settings.PreferredMonitor = screen.Id;
        _settings.BubblePosition = WorkAssistantPlacement.Capture(CircleAnchor(), screen);
        _saveLocal();
    }

    private WorkAssistantScreenSnapshot? ScreenSnapshotForCurrentWindow()
    {
        var current = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (current is null) return null;
        return Snapshot(current);
    }

    private IReadOnlyList<WorkAssistantScreenSnapshot> ScreenSnapshots()
        => Screens.All.Select(Snapshot).ToArray();

    private WorkAssistantScreenSnapshot Snapshot(Screen screen)
        => new(
            WorkAssistantPlacement.ScreenId(screen.WorkingArea, screen.Scaling),
            screen.WorkingArea,
            screen.Scaling,
            screen == Screens.Primary);
}

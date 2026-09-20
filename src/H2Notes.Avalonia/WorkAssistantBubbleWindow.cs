using Avalonia;
using Avalonia.Controls;
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
    private const double BubbleSize = 74;
    private readonly WorkAssistantSettings _settings;
    private readonly Action _saveLocal;
    private readonly Border _shell;
    private readonly TextBlock _glyph;
    private readonly TextBlock _label;
    private readonly DispatcherTimer _placementTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(220)
    };
    private bool _adjusting;
    private bool _opened;

    public WorkAssistantBubbleState State { get; private set; } = WorkAssistantBubbleState.Idle;

    public WorkAssistantBubbleWindow(
        WorkAssistantSettings settings,
        Action saveLocal)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _saveLocal = saveLocal ?? throw new ArgumentNullException(nameof(saveLocal));

        Width = Height = BubbleSize;
        MinWidth = MinHeight = BubbleSize;
        MaxWidth = MaxHeight = BubbleSize;
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
            Text = "✦",
            FontSize = 24,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _label = new TextBlock
        {
            Text = "Sẵn sàng",
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        _shell = new Border
        {
            CornerRadius = new CornerRadius(22),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 7),
            Child = new StackPanel
            {
                Spacing = 1,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _glyph, _label }
            }
        };
        Content = _shell;
        DesktopWindowChrome.Attach(this, _shell);
        SetState(WorkAssistantBubbleState.Idle);

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
        Closed += (_, _) => _placementTimer.Stop();
    }

    public void ApplySettings()
    {
        Topmost = _settings.AlwaysOnTop;
        if (_opened && IsVisible)
            RestorePlacement();
    }

    public void SetState(WorkAssistantBubbleState state, string? detail = null)
    {
        State = state;
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

        _label.Text = string.IsNullOrWhiteSpace(detail) ? label : detail.Trim();
        _shell.Background = Brush.Parse(background);
        _shell.BorderBrush = Brush.Parse(border);
        _glyph.Foreground = Brush.Parse(foreground);
        _label.Foreground = Brush.Parse(foreground);
        ToolTip.SetTip(_shell, "Work Assistant · " + _label.Text + " · kéo để di chuyển");
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
        Position = desired;
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
        _settings.BubblePosition = WorkAssistantPlacement.Capture(Position, screen);
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

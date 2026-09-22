using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace H2Notes.Avalonia.Controls;

public enum IconKind
{
    Chat, Folder, Document, Sparkle, SparkleFilled, Settings, Pin, PinFilled, More, Close,
    Plus, ChevronDown, ChevronUp, ChevronLeft, Grip, Search, Clipboard, Star,
    ResizeCorner, Dock, Send, Stop, Bold, Italic, Underline, Strike, TextColor,
    Highlight, Bullets, NumberedList, Link, Undo, Redo, Microphone, Shield, ArrowUp, Waveform
}

// Original 24-unit vectors traced to the approved H2 mockup's silhouettes.
// Shared geometry avoids font substitutions and stays sharp at every display scale.
public sealed class AppIcon : Control
{
    public static readonly StyledProperty<IconKind> KindProperty =
        AvaloniaProperty.Register<AppIcon, IconKind>(nameof(Kind));
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextBlock.ForegroundProperty.AddOwner<AppIcon>();
    public IconKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private sealed record Shape(Geometry Geometry, bool Filled = false);
    private static Shape Line(string data) => new(Geometry.Parse(data));
    private static Shape Fill(string data) => new(Geometry.Parse(data), true);
    private const string PinPath = "M14 3 L20 7 L17 8 L14 13 L15 17 L10 14 L5 11 L9 10 L13 5 Z";
    private const string SparklePath = "M12 2 C13.3 8.2 15.8 10.7 22 12 C15.8 13.3 13.3 15.8 12 22 C10.7 15.8 8.2 13.3 2 12 C8.2 10.7 10.7 8.2 12 2 Z";
    private static readonly IReadOnlyDictionary<IconKind, Shape[]> Shapes = new Dictionary<IconKind, Shape[]>
    {
        [IconKind.Chat] = [Line("M4 3 H20 Q22 3 22 5 V16 Q22 18 20 18 H10 L4 22 V18 Q2 18 2 16 V5 Q2 3 4 3 Z")],
        [IconKind.Folder] = [Fill("M3 5 Q3 3 5 3 H9 L11 6 H20 Q22 6 22 8 V19 Q22 21 20 21 H4 Q2 21 2 19 V7 Q2 5 3 5 Z")],
        [IconKind.Document] = [Line("M5 2 H14 L20 8 V22 H5 Z M14 2 V8 H20 M8 12 H16 M8 16 H16 M8 19 H13")],
        [IconKind.Sparkle] = [Line(SparklePath)],
        [IconKind.SparkleFilled] = [Fill(SparklePath)],
        [IconKind.Settings] = [Line("M10 2 H14 L14.7 5 L16.3 5.8 L19 4.9 L21 8.2 L19 10.3 V13.7 L21 15.8 L19 19.1 L16.3 18.2 L14.7 19 L14 22 H10 L9.3 19 L7.7 18.2 L5 19.1 L3 15.8 L5 13.7 V10.3 L3 8.2 L5 4.9 L7.7 5.8 L9.3 5 Z"), Line("M16 12 A4 4 0 1 1 8 12 A4 4 0 1 1 16 12 Z")],
        [IconKind.Pin] = [Line(PinPath), Line("M10 14 L7 21")],
        [IconKind.PinFilled] = [Fill(PinPath), Line("M10 14 L7 21")],
        [IconKind.More] = [Fill("M13.5 4 A1.5 1.5 0 1 1 10.5 4 A1.5 1.5 0 1 1 13.5 4 Z M13.5 12 A1.5 1.5 0 1 1 10.5 12 A1.5 1.5 0 1 1 13.5 12 Z M13.5 20 A1.5 1.5 0 1 1 10.5 20 A1.5 1.5 0 1 1 13.5 20 Z")],
        [IconKind.Close] = [Line("M5 5 L19 19 M19 5 L5 19")],
        [IconKind.Plus] = [Line("M12 4 V20 M4 12 H20")],
        [IconKind.ChevronDown] = [Line("M5 9 L12 16 L19 9")],
        [IconKind.ChevronUp] = [Line("M5 15 L12 8 L19 15")],
        [IconKind.ChevronLeft] = [Line("M15 5 L8 12 L15 19")],
        [IconKind.Grip] = [Fill("M9 5 A1.5 1.5 0 1 1 6 5 A1.5 1.5 0 1 1 9 5 Z M17 5 A1.5 1.5 0 1 1 14 5 A1.5 1.5 0 1 1 17 5 Z M9 12 A1.5 1.5 0 1 1 6 12 A1.5 1.5 0 1 1 9 12 Z M17 12 A1.5 1.5 0 1 1 14 12 A1.5 1.5 0 1 1 17 12 Z M9 19 A1.5 1.5 0 1 1 6 19 A1.5 1.5 0 1 1 9 19 Z M17 19 A1.5 1.5 0 1 1 14 19 A1.5 1.5 0 1 1 17 19 Z")],
        [IconKind.Search] = [Line("M17 10 A7 7 0 1 1 3 10 A7 7 0 1 1 17 10 Z M15 15 L22 22")],
        [IconKind.Clipboard] = [Line("M8 5 H5 V20 H18 V5 H14 M8 3 H14 V7 H8 Z M8 11 H15 M8 15 H15 M20 8 H22 V23 H9")],
        [IconKind.Star] = [Fill("M12 2 L15 8.5 L22 9.5 L17 14.5 L18.2 22 L12 18.5 L5.8 22 L7 14.5 L2 9.5 L9 8.5 Z")],
        [IconKind.ResizeCorner] = [Line("M6 21 L21 6 M12 21 L21 12 M18 21 L21 18")],
        [IconKind.Dock] = [Line("M3 4 H21 V20 H3 Z M15 4 V20")],
        [IconKind.Send] = [Fill("M2 3 L22 12 L2 21 L6 12 Z")],
        [IconKind.Stop] = [Fill("M5 5 H19 V19 H5 Z")],
        [IconKind.Bold] = [Line("M7 3 H13 C20 3 20 11 13 11 H7 V3 M7 11 H14 C22 11 21 21 14 21 H7 V11")],
        [IconKind.Italic] = [Line("M10 3 H19 M5 21 H14 M15 3 L9 21")],
        [IconKind.Underline] = [Line("M6 3 V12 C6 20 18 20 18 12 V3 M5 22 H19")],
        [IconKind.Strike] = [Line("M18 5 C14 0 5 2 6 8 C6 10 9 11 12 12 M12 12 C17 13 19 14 18 18 C17 22 8 23 5 18 M3 12 H21")],
        [IconKind.TextColor] = [Line("M5 19 L12 3 L19 19 M8 13 H16")],
        [IconKind.Highlight] = [Line("M6 13 L14 3 L21 9 L13 19 Z M6 13 L4 19 L8 22 L13 19 M3 23 H13")],
        [IconKind.Bullets] = [Line("M9 5 H21 M9 12 H21 M9 19 H21"), Fill("M4 3.5 A1.5 1.5 0 1 1 4 6.5 A1.5 1.5 0 1 1 4 3.5 Z M4 10.5 A1.5 1.5 0 1 1 4 13.5 A1.5 1.5 0 1 1 4 10.5 Z M4 17.5 A1.5 1.5 0 1 1 4 20.5 A1.5 1.5 0 1 1 4 17.5 Z")],
        [IconKind.NumberedList] = [Line("M10 5 H22 M10 12 H22 M10 19 H22 M2 3 L4 2 V7 M2 7 H6 M2 11 C2 8 6 8 6 11 L2 15 H6 M2 18 C7 16 7 21 3 21 M3 21 C7 20 7 24 2 23")],
        [IconKind.Link] = [Line("M9 15 L15 9 M8 10 L5 13 C0 18 6 24 11 19 L14 16 M10 8 L13 5 C18 0 24 6 19 11 L16 14")],
        [IconKind.Undo] = [Line("M8 4 L3 9 L8 14 M3 9 H14 C23 9 23 21 14 21")],
        [IconKind.Redo] = [Line("M16 4 L21 9 L16 14 M21 9 H10 C1 9 1 21 10 21")],
        [IconKind.Microphone] = [Line("M8 6 A4 4 0 0 1 16 6 V12 A4 4 0 0 1 8 12 Z M5 11 V12 A7 7 0 0 0 19 12 V11 M12 19 V22 M8 22 H16")],
        [IconKind.Shield] = [Line("M12 2 L20 5 V11 C20 17 16 20 12 22 C8 20 4 17 4 11 V5 Z M12 7 V12 M12 16 V16.1")],
        [IconKind.ArrowUp] = [Line("M12 20 V4 M5 11 L12 4 L19 11")],
        [IconKind.Waveform] = [Line("M4 10 V14 M8 5 V19 M12 8 V16 M16 3 V21 M20 9 V15")]
    };

    static AppIcon() => AffectsRender<AppIcon>(KindProperty, ForegroundProperty);
    public AppIcon() { Width = Height = 20; IsHitTestVisible = false; VerticalAlignment = VerticalAlignment.Center; HorizontalAlignment = HorizontalAlignment.Center; }
    public AppIcon(IconKind kind, double size = 20) : this() { Kind = kind; Width = Height = size; }
    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var brush = Foreground ?? Brushes.Black;
        using (context.PushTransform(Matrix.CreateScale(size / 24, size / 24) * Matrix.CreateTranslation((Bounds.Width - size) / 2, (Bounds.Height - size) / 2)))
        {
            var pen = new Pen(brush, Kind == IconKind.Bold ? 2.8 : 1.7, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            foreach (var shape in Shapes[Kind]) context.DrawGeometry(shape.Filled ? brush : null, shape.Filled ? null : pen, shape.Geometry);
            if (Kind == IconKind.Folder) context.DrawLine(new Pen(Brushes.White, 1.4), new Point(5, 6), new Point(8, 6));
        }
    }

    public static StackPanel Label(IconKind kind, string text, double size = 20) => new()
    {
        Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center,
        Children = { new AppIcon(kind, size), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } }
    };
    public static Button Button(IconKind kind, string tip, string css = "quiet")
    {
        var button = new Button { Content = new AppIcon(kind), Classes = { css } };
        ToolTip.SetTip(button, tip); AutomationProperties.SetName(button, tip); return button;
    }
}

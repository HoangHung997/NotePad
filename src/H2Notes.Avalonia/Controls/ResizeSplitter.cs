using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace H2Notes.Avalonia.Controls;

public sealed class ResizeSplitter : GridSplitter
{
    protected override Type StyleKeyOverride => typeof(GridSplitter);
    public const double HitSize = 12;
    static ResizeSplitter() => AffectsRender<ResizeSplitter>(IsPointerOverProperty, ResizeDirectionProperty);
    public ResizeSplitter()
    {
        Background = Brushes.Transparent;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ResizeDirectionProperty)
                Cursor = new Cursor(ResizeDirection == GridResizeDirection.Columns ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth);
        };
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var vertical = ResizeDirection == GridResizeDirection.Columns;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var line = new Pen(RichEditor.Brush(IsPointerOver ? "#C58B72" : "#E1DDD7"), 2);
        var grip = new Pen(RichEditor.Brush(IsPointerOver ? "#A4573D" : "#A6A09A"), 2, lineCap: PenLineCap.Round);
        if (vertical)
        {
            context.DrawLine(line, new(center.X, 0), new(center.X, center.Y - 16));
            context.DrawLine(line, new(center.X, center.Y + 16), new(center.X, Bounds.Height));
            context.DrawLine(grip, new(center.X - 2, center.Y - 10), new(center.X - 2, center.Y + 10));
            context.DrawLine(grip, new(center.X + 2, center.Y - 10), new(center.X + 2, center.Y + 10));
        }
        else
        {
            context.DrawLine(line, new(0, center.Y), new(center.X - 16, center.Y));
            context.DrawLine(line, new(center.X + 16, center.Y), new(Bounds.Width, center.Y));
            context.DrawLine(grip, new(center.X - 10, center.Y - 2), new(center.X + 10, center.Y - 2));
            context.DrawLine(grip, new(center.X - 10, center.Y + 2), new(center.X + 10, center.Y + 2));
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Data;

namespace H2Notes.Avalonia;

internal static class DesktopWindowChrome
{
    internal const double ResizeBorder = 7;
    internal static WindowEdge? EdgeAt(Point p, Size size)
    {
        if (p.X < 0 || p.Y < 0 || p.X > size.Width || p.Y > size.Height) return null;
        var left = p.X < ResizeBorder; var right = p.X > size.Width - ResizeBorder;
        var top = p.Y < ResizeBorder; var bottom = p.Y > size.Height - ResizeBorder;
        if (!(left || right || top || bottom)) return null;
        // Larger corner targets still stay inside the same edge strip used for dragging.
        if (p.X < 14 && p.Y < 14) return WindowEdge.NorthWest;
        if (p.X > size.Width - 14 && p.Y < 14) return WindowEdge.NorthEast;
        if (p.X < 14 && p.Y > size.Height - 14) return WindowEdge.SouthWest;
        if (p.X > size.Width - 14 && p.Y > size.Height - 14) return WindowEdge.SouthEast;
        return left ? WindowEdge.West : right ? WindowEdge.East : top ? WindowEdge.North : WindowEdge.South;
    }

    internal static StandardCursorType CursorFor(WindowEdge edge) => edge switch
    {
        WindowEdge.West or WindowEdge.East => StandardCursorType.SizeWestEast,
        WindowEdge.North or WindowEdge.South => StandardCursorType.SizeNorthSouth,
        WindowEdge.NorthWest or WindowEdge.SouthEast => StandardCursorType.TopLeftCorner,
        _ => StandardCursorType.TopRightCorner
    };

    public static void Attach(Window window, Control titleBar)
    {
        window.ShowInTaskbar = false;
        window.CanMinimize = window.CanMaximize = false;
        window.WindowDecorations = WindowDecorations.None;
        IDisposable? hoverCursor = null;
        InputElement? hoverTarget = null;
        WindowEdge? hoverEdge = null;
        void ClearCursor() { hoverCursor?.Dispose(); hoverCursor = null; hoverTarget = null; hoverEdge = null; }
        window.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            var edge = window.CanResize && window.WindowState == WindowState.Normal
                && !e.GetCurrentPoint(window).Properties.IsLeftButtonPressed ? EdgeAt(e.GetPosition(window), window.Bounds.Size) : null;
            var target = e.Source as InputElement ?? window;
            if (edge == hoverEdge && target == hoverTarget) return;
            ClearCursor();
            if (edge is null) return;
            hoverTarget = target; hoverEdge = edge;
            // Temporary priority restores the child's original I-beam/hand or binding on exit.
            hoverCursor = target.SetValue(InputElement.CursorProperty, new Cursor(CursorFor(edge.Value)), BindingPriority.Animation);
        }, RoutingStrategies.Tunnel, true);
        window.PointerExited += (_, _) => ClearCursor();
        window.PointerCaptureLost += (_, _) => ClearCursor();
        window.Closed += (_, _) => ClearCursor();
        window.PropertyChanged += (_, e) => { if (e.Property == Window.WindowStateProperty || e.Property == Window.CanResizeProperty) ClearCursor(); };
        titleBar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;
            for (var element = e.Source as StyledElement; element is not null; element = element.Parent)
                if (element is Button or TextBox) return;
            window.BeginMoveDrag(e);
        };
        window.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!window.CanResize || !e.GetCurrentPoint(window).Properties.IsLeftButtonPressed || window.WindowState != WindowState.Normal) return;
            var edge = EdgeAt(e.GetPosition(window), window.Bounds.Size);
            if (edge is null) return;
            window.BeginResizeDrag(edge.Value, e); e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }
}

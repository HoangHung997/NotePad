using Avalonia;
using Avalonia.Controls;
using H2Notes.Core;

namespace H2Notes.Avalonia;

internal static class WindowPlacement
{
    public static void Attach(Window window, Func<NoteRecord> getNote, App app, bool board)
    {
        var initial = getNote();
        var width = board ? initial.SheetWidth ?? 1160 : initial.Width;
        var height = board ? initial.SheetHeight ?? 810 : initial.Height;
        Attach(window, app, width, height, () =>
        {
            var note = getNote();
            return new PixelPoint(board ? note.SheetLeft ?? 120 : (int)note.Left, board ? note.SheetTop ?? 100 : (int)note.Top);
        });
    }

    internal static void Attach(Window window, App app, double width, double height, Func<PixelPoint> getPosition)
    {
        var adjusting = false;
        var ready = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Width = double.IsFinite(width) ? Math.Max(window.MinWidth, width) : window.MinWidth;
        window.Height = double.IsFinite(height) ? Math.Max(window.MinHeight, height) : window.MinHeight;
        void RestorePosition()
        {
            var saved = getPosition();
            var screens = window.Screens.All.Select(s => s.WorkingArea).ToArray();
            window.Position = ReachablePosition(saved, screens, window.Screens.Primary?.WorkingArea);
        }
        RestorePosition();
        window.Opened += (_, _) =>
        {
            if (ready) return;
            // Restore exact coordinates before enabling snapping or geometry autosave.
            RestorePosition(); ready = true;
        };
        window.SizeChanged += (_, _) => { if (ready && window.IsVisible) app.ScheduleSave(); };
        window.PositionChanged += (_, _) =>
        {
            if (adjusting || !ready || window.WindowState != WindowState.Normal || !window.IsVisible) return;
            app.ScheduleSave();
            if (!app.State.SheetPreferences.SnapWindows) return;
            var pos = window.Position;
            var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
            if (screen is null) return;
            var scale = window.RenderScaling;
            var width = (int)Math.Round(window.Bounds.Width * scale); var height = (int)Math.Round(window.Bounds.Height * scale);
            var threshold = (int)Math.Round(10 * scale);
            var x = pos.X; var y = pos.Y; var dx = threshold + 1; var dy = threshold + 1;
            void SnapX(int candidate) { var distance = Math.Abs(candidate - pos.X); if (distance <= threshold && distance < dx) { dx = distance; x = candidate; } }
            void SnapY(int candidate) { var distance = Math.Abs(candidate - pos.Y); if (distance <= threshold && distance < dy) { dy = distance; y = candidate; } }
            var work = screen.WorkingArea;
            SnapX(work.X); SnapX(work.Right - width); SnapY(work.Y); SnapY(work.Bottom - height);
            foreach (var other in app.OpenWindows.Where(w => w != window && w.IsVisible && w.WindowState == WindowState.Normal))
            {
                var p = other.Position; var w = (int)(other.Bounds.Width * other.RenderScaling); var h = (int)(other.Bounds.Height * other.RenderScaling);
                if (pos.Y < p.Y + h && pos.Y + height > p.Y) { SnapX(p.X + w); SnapX(p.X - width); }
                if (pos.X < p.X + w && pos.X + width > p.X) { SnapY(p.Y + h); SnapY(p.Y - height); }
            }
            if (x == pos.X && y == pos.Y) return;
            adjusting = true; window.Position = new PixelPoint(x, y); adjusting = false;
        };
    }

    internal static PixelPoint ReachablePosition(PixelPoint saved, IReadOnlyList<PixelRect> screens, PixelRect? primary)
    {
        if (screens.Any(s => s.Contains(new PixelPoint(saved.X + 40, saved.Y + 18)))) return saved;
        var fallback = primary ?? screens.FirstOrDefault();
        return fallback.Width > 0 ? new PixelPoint(fallback.X + 30, fallback.Y + 30) : saved;
    }
}

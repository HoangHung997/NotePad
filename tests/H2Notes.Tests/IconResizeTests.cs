using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

internal static class IconResizeTests
{
    public static void Run(Action<string, Action> test)
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        void Pump() => Dispatcher.UIThread.RunJobs();
        var chrome = typeof(H2Notes.Avalonia.MainWindow).Assembly.GetType("H2Notes.Avalonia.DesktopWindowChrome")!;
        test("All original vector icons render at three sizes and inherit theme color", () =>
        {
            var panel = new WrapPanel();
            foreach (var size in new[] { 16, 20, 28 })
                foreach (var kind in Enum.GetValues<IconKind>()) panel.Children.Add(new AppIcon(kind, size));
            var window = new Window { Width = 600, Height = 400, Foreground = Brushes.Red, Content = panel };
            window.Show(); Pump();
            foreach (var icon in panel.Children.OfType<AppIcon>())
            {
                Check(icon.Bounds.Width > 0 && icon.Bounds.Height > 0, icon.Kind + " collapsed");
                Check(icon.Foreground == Brushes.Red, "Icon lost inherited foreground: " + icon.Kind);
                using var drawing = new DrawingGroup().Open(); icon.Render(drawing);
            }
            window.Close();
        });
        test("Window resize hit testing and cursor axes match all eight sides", () =>
        {
            var hit = chrome.GetMethod("EdgeAt", BindingFlags.Static | BindingFlags.NonPublic)!;
            var cursor = chrome.GetMethod("CursorFor", BindingFlags.Static | BindingFlags.NonPublic)!;
            foreach (var (point, edge, type) in new[]
            {
                (new Point(2, 150), WindowEdge.West, StandardCursorType.SizeWestEast),
                (new Point(398, 150), WindowEdge.East, StandardCursorType.SizeWestEast),
                (new Point(200, 2), WindowEdge.North, StandardCursorType.SizeNorthSouth),
                (new Point(200, 298), WindowEdge.South, StandardCursorType.SizeNorthSouth),
                (new Point(2, 2), WindowEdge.NorthWest, StandardCursorType.TopLeftCorner),
                (new Point(398, 2), WindowEdge.NorthEast, StandardCursorType.TopRightCorner),
                (new Point(2, 298), WindowEdge.SouthWest, StandardCursorType.TopRightCorner),
                (new Point(398, 298), WindowEdge.SouthEast, StandardCursorType.TopLeftCorner)
            })
            {
                Check(Equals(edge, hit.Invoke(null, [point, new Size(400, 300)])), "Wrong hit at " + point);
                Check(Equals(type, cursor.Invoke(null, [edge])), "Wrong cursor for " + edge);
            }
            foreach (var point in new[] { new Point(20, 20), new Point(200, 150), new Point(-1, 4), new Point(401, 30) })
                Check(hit.Invoke(null, [point, new Size(400, 300)]) is null, "False resize target " + point);
        });
        test("Window edge hover temporarily overrides child I-beam and restores it", () =>
        {
            var original = new Cursor(StandardCursorType.Ibeam);
            var surface = new Border { Background = Brushes.White, Cursor = original };
            var window = new Window { Width = 400, Height = 300, Content = surface };
            chrome.GetMethod("Attach")!.Invoke(null, [window, new Border()]);
            window.Show(); Pump();
            window.MouseMove(new Point(2, 150)); Pump();
            Check(surface.Cursor != original, "Resize cursor never reached hovered child");
            window.MouseMove(new Point(100, 100)); Pump();
            Check(surface.Cursor == original, "I-beam not restored");
            window.MouseMove(new Point(2, 150)); Pump(); window.CanResize = false;
            Check(surface.Cursor == original, "Non-resizable window keeps resize cursor");
            window.MouseMove(new Point(398, 100)); Pump(); Check(surface.Cursor == original, "Disabled resize still shows arrow");
            window.CanResize = true; window.MouseMove(new Point(2, 150)); Pump();
            window.WindowState = WindowState.Maximized; Pump(); Check(surface.Cursor == original, "Maximized window keeps resize cursor");
            window.Close();
        });
        test("Responsive shell uses vector icons and broad, working split handles", () =>
        {
            var board = SheetStorage.Demo().Notes[0]; board.SelectedProjectId = board.Projects[2].Id;
            var project = board.Projects[2];
            var window = new H2Notes.Avalonia.MainWindow(new H2Notes.Avalonia.App(), board);
            window.Show(); Pump();
            H2UiTestNavigation.OpenProjectWorkspace(window, project.Id); Pump();
            foreach (var name in new[] { "PinButton", "CloseButton", "MenuButton", "ProjectsNavButton", "NotesNavButton", "AiNavButton", "SettingsNavButton", "AskAiButton", "PriorityButton" })
                Check(window.FindControl<Button>(name)!.GetVisualDescendants().OfType<AppIcon>().Any(), "Missing vector: " + name);
            var splitter = window.FindControl<ResizeSplitter>("NotesSplitter")!;
            Check(splitter.Bounds.Height >= ResizeSplitter.HitSize && splitter.Cursor is not null, "Splitter not discoverable");
            var point = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, 1), window)!.Value;
            var pane = window.FindControl<Border>("TasksPane")!; var before = pane.Bounds.Height;
            window.MouseMove(point); window.MouseDown(point, MouseButton.Left);
            window.MouseMove(point + new Vector(0, 30), RawInputModifiers.LeftMouseButton);
            window.MouseUp(point + new Vector(0, 30), MouseButton.Left); Pump();
            Check(Math.Abs(pane.Bounds.Height - before) > 10, "Outer splitter hit area does not drag");
            window.FindControl<Button>("PriorityButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(board.Projects[0] == project, "Star does not prioritize current project");
            foreach (var mode in new[] { "right", "bottom" })
            {
                project.Layout.AiDock = mode;
                window.Width += 1; Pump();
                var aiSplit = window.FindControl<ResizeSplitter>(mode == "right" ? "AiVerticalSplitter" : "AiHorizontalSplitter")!;
                Check(aiSplit.IsVisible && aiSplit.Cursor is not null, "Missing AI resize cursor");
                Check((mode == "right" ? aiSplit.Bounds.Width : aiSplit.Bounds.Height) >= ResizeSplitter.HitSize, "AI splitter hit area too small");
            }
            window.Hide();
        });
        test("List toolbar actions preserve formatting and undo as one edit", () =>
        {
            var editor = new RichEditor(); var doc = RichDocument.Plain("Alpha\nBeta\nGamma");
            doc.Format(6, 4, style => style with { Bold = true, Color = "#FF0000" }); editor.Load(doc);
            editor.Editor.Select(0, 11); editor.ToggleList(false);
            Check(editor.Snapshot().Text == "\u2022 Alpha\n\u2022 Beta\nGamma", "Wrong list range");
            Check(editor.Snapshot().StyleAt(10).Bold, "Lost formatting");
            editor.Undo(); Check(editor.Snapshot().Text == doc.Text, "List undo was not atomic");
            editor.Editor.Select(0, 10); editor.ToggleList(true);
            Check(editor.Snapshot().Text == "1. Alpha\n2. Beta\nGamma", "Wrong numbering");
            editor.ToggleList(true); Check(editor.Snapshot().Text == doc.Text, "Toggle off leaves markers");
            var toolbar = (StackPanel)((ScrollViewer)editor.CreateToolbar()).Content!;
            Check(toolbar.Children.OfType<ToggleButton>().All(b => b.Content is AppIcon), "Glyph-based format button remains");
        });
        test("Column resize hover and drag use the same header boundary", () =>
        {
            var sheet = new ProjectGrid(); var board = SheetStorage.Demo().Notes[0];
            var window = new Window { Width = 1000, Height = 500, Content = sheet };
            window.Show(); sheet.SetBoard(board); sheet.FocusProject(board.Projects[0]); Pump();
            var edges = (double[])typeof(ProjectGrid).GetField("_edges", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sheet)!;
            window.MouseMove(new Point(edges[2], 8)); Pump(); Check(sheet.Cursor is not null, "No header resize cursor");
            window.MouseMove(new Point(200, 90)); Pump(); Check(sheet.Cursor is null, "Header cursor stuck on row");
            sheet.SetCompact(true); Pump(); window.MouseMove(new Point(edges[2], 8)); Pump();
            Check(sheet.Cursor is null, "Compact list falsely offers column resize"); window.Close();
        });
    }
}

using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using H2Notes.Avalonia;
using H2Notes.Core;

internal static class H2ResponsiveProductTests
{
    public static void Run(Action<string, Action> test)
    {
        test("H2M-092 narrow Project Workspace uses compact picker and overlay drawer", () =>
        {
            var (app, board, window) = Fixture(560, 820);
            try
            {
                var project = board.Projects[1];
                H2UiTestNavigation.OpenProjectWorkspace(window, project.Id);
                Pump(window);

                var picker = window.FindControl<Button>("CompactProjectPicker")!;
                var sidebar = window.FindControl<Border>("Sidebar")!;
                var shade = window.FindControl<Border>("DrawerShade")!;
                Check(picker.IsVisible, "Narrow Project Workspace lost compact project picker.");
                Check(!sidebar.IsVisible, "Narrow Project Workspace squeezed a permanent sidebar into the editor.");

                picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(window);
                Check(sidebar.IsVisible && shade.IsVisible,
                    "Compact project picker did not open the in-window drawer overlay.");
                Check(sidebar.Bounds.Width <= 360.5,
                    "Narrow drawer exceeded its bounded overlay width.");

                var projectList = window.FindControl<ListBox>("ProjectList")!;
                projectList.SelectedIndex = 2;
                Pump(window);
                Check(window.SelectedProjectId == board.Projects[2].Id,
                    "Narrow drawer did not switch project.");
                Check(!sidebar.IsVisible && !shade.IsVisible,
                    "Narrow drawer did not collapse after project selection.");
            }
            finally
            {
                Close(app, window);
            }
        });

        test("H2M-092 medium detail layout keeps usable task and note panes", () =>
        {
            var (app, board, window) = Fixture(1040, 760);
            try
            {
                H2UiTestNavigation.OpenProjectWorkspace(window, board.Projects[0].Id);
                Pump(window);
                window.FindControl<Button>("TasksTabButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(window);

                var sidebar = window.FindControl<Border>("Sidebar")!;
                var tasks = window.FindControl<Border>("TasksPane")!;
                var notes = window.FindControl<Border>("NotesPane")!;
                var splitter = window.FindControl<Control>("NotesSplitter")!;

                Check(sidebar.IsVisible, "Medium layout lost useful project navigation.");
                Check(tasks.IsVisible && notes.IsVisible,
                    "Medium detail mode unexpectedly hid task or note pane.");
                Check(tasks.Bounds.Height >= 90 && notes.Bounds.Height >= 38,
                    $"Medium layout produced unreadably small panes: tasks={tasks.Bounds.Height}, notes={notes.Bounds.Height}.");
                Check(splitter.IsVisible,
                    "Medium detail mode lost the task/note resize affordance.");
            }
            finally
            {
                Close(app, window);
            }
        });

        test("H2M-092 short narrow window switches between task and note detail instead of crushing both", () =>
        {
            var (app, board, window) = Fixture(560, 600);
            try
            {
                H2UiTestNavigation.OpenProjectWorkspace(window, board.Projects[0].Id);
                Pump(window);

                window.FindControl<Button>("TasksTabButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(window);
                var tasks = window.FindControl<Border>("TasksPane")!;
                var notes = window.FindControl<Border>("NotesPane")!;
                Check(tasks.IsVisible && !notes.IsVisible,
                    "Short narrow task mode tried to show both detail panes.");

                window.FindControl<Button>("NotesTabButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(window);
                Check(!tasks.IsVisible && notes.IsVisible,
                    "Short narrow note mode did not switch to the note pane.");
            }
            finally
            {
                Close(app, window);
            }
        });

        test("H2M-092 wide Project Workspace preserves Agent-first majority and optional docked AI", () =>
        {
            var (app, board, window) = Fixture(1440, 860);
            try
            {
                H2UiTestNavigation.OpenProjectWorkspace(window, board.Projects[0].Id);
                Pump(window);

                var agent = window.FindControl<Border>("AiHostBorder")!;
                var work = window.FindControl<Grid>("WorkAndAi")!;
                Check(agent.IsVisible, "Wide Project Workspace lost Agent-first surface.");
                Check(agent.Bounds.Width > 500,
                    "Agent-first wide surface became a tiny side pane.");
                Check(work.Bounds.Width > agent.Bounds.Width,
                    "Wide workspace root is not larger than the Agent surface.");

                window.FindControl<Button>("TasksTabButton")!
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.DockProjectAi("right");
                Pump(window);
                Check(agent.IsVisible && agent.Bounds.Width >= 300 && agent.Bounds.Width <= 380,
                    "Explicit right dock no longer uses a bounded readable AI pane.");
                Check(window.FindControl<Border>("TasksPane")!.Bounds.Width > 450,
                    "Docked AI crushed the project detail surface.");
            }
            finally
            {
                Close(app, window);
            }
        });

        test("H2M-092 placement infrastructure preserves valid multi-monitor coordinates and recovers missing monitor", () =>
        {
            var type = typeof(MainWindow).Assembly.GetType("H2Notes.Avalonia.WindowPlacement")
                ?? throw new Exception("WindowPlacement type missing.");
            var method = type.GetMethod("ReachablePosition", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new Exception("ReachablePosition missing.");
            var primary = new PixelRect(0, 0, 1920, 1040);
            var left = new PixelRect(-1920, 0, 1920, 1040);

            Equal(new PixelPoint(-1200, 80),
                (PixelPoint)method.Invoke(null, [new PixelPoint(-1200, 80), new[] { primary, left }, primary])!);
            Equal(new PixelPoint(30, 30),
                (PixelPoint)method.Invoke(null, [new PixelPoint(-1200, 80), new[] { primary }, primary])!);

            var repo = FindRepoRoot();
            var bubbleTests = File.ReadAllText(Path.Combine(
                repo, "tests", "H2Notes.Tests", "H2WorkAssistantBubbleTests.cs"));
            Check(bubbleTests.Contains("DIPs", StringComparison.Ordinal)
                  && bubbleTests.Contains("Scaling", StringComparison.Ordinal),
                "Work Assistant DPI/multi-monitor acceptance coverage disappeared.");
        });
    }

    private static (App App, NoteRecord Board, MainWindow Window) Fixture(double width, double height)
    {
        var app = new App();
        var board = SheetStorage.Demo().Notes[0];
        var window = new MainWindow(app, board)
        {
            Width = width,
            Height = height
        };
        window.Show();
        Pump(window);
        return (app, board, window);
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static void Close(App app, MainWindow window)
    {
        try { window.Hide(); } catch { }
        try { window.Close(); } catch { }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected {expected}; actual {actual}");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(current.FullName, "src")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}

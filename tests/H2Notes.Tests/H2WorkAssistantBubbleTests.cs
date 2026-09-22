using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Media;
using H2Notes.Avalonia;

internal static class H2WorkAssistantBubbleTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Work Assistant bubble is circular when idle or chat visible and stays one line while working", () =>
        {
            var bubble = new WorkAssistantBubbleWindow(new(), () => { }); bubble.Show(); Pump();
            try
            {
                var anchor = bubble.Position;
                Check(bubble.Width == 54 && bubble.Height == 54 && !bubble.IsExpanded, "Idle bubble is not a small circle");
                bubble.SetState(WorkAssistantBubbleState.Working, "Đang sửa Word\r\nKiểm tra dòng tiếp theo"); Pump();
                Check(bubble.IsExpanded && bubble.Width == 320 && bubble.Height == 54, "Working bubble grew vertically or did not expand");
                var label = bubble.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "WorkAssistantBubbleActivity");
                Check(label.Text == "Đang sửa Word Kiểm tra dòng tiếp theo" && label.MaxLines == 1 && label.TextWrapping == TextWrapping.NoWrap, "Activity is not a single readable line");
                bubble.SetChatVisible(true); Pump();
                Check(!bubble.IsExpanded && bubble.Width == 54 && bubble.Position == anchor, "Chat-open bubble did not return to its circle anchor");
                bubble.SetState(WorkAssistantBubbleState.Working, new string('x', 2000)); Pump();
                Check(!bubble.IsExpanded && bubble.Height == 54, "Progress reopened the bar over a visible chat");
                bubble.SetChatVisible(false); Pump();
                Check(bubble.IsExpanded && bubble.Height == 54, "Hiding a busy chat did not restore the single-line ticker");
                bubble.SetState(WorkAssistantBubbleState.Attention, "Cần xác nhận", taskActive: true); Pump();
                Check(bubble.IsExpanded, "Pending approval has no visible cue while chat hidden");
                foreach (var state in new[] { WorkAssistantBubbleState.Completed, WorkAssistantBubbleState.Attention, WorkAssistantBubbleState.Idle })
                { bubble.SetState(state); Pump(); Check(!bubble.IsExpanded && bubble.Width == 54, "Finished task left the bar expanded"); }
            }
            finally { bubble.Close(); }
        });
        test("Work Assistant bubble opens once on click and does not activate on drag or cancelled press", () =>
        {
            var opened = 0;
            var bubble = new WorkAssistantBubbleWindow(new() { Enabled = true }, () => { }, () => opened++);
            bubble.Show(); Pump(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            try
            {
                var center = new Point(37, 37);
                bubble.MouseDown(center, MouseButton.Left); Pump();
                Check(opened == 0, "Bubble activated on press before click/drag was known.");
                bubble.MouseMove(center + new Vector(1, 1), RawInputModifiers.LeftMouseButton);
                bubble.MouseUp(center + new Vector(1, 1), MouseButton.Left); Pump();
                Check(opened == 1, "A click with small hand movement did not open exactly once.");

                var originalPosition = bubble.Position;
                bubble.MouseDown(center, MouseButton.Left);
                bubble.MouseMove(center + new Vector(14, 0), RawInputModifiers.LeftMouseButton);
                bubble.MouseUp(center + new Vector(14, 0), MouseButton.Left); Pump();
                Check(opened == 1, "Dragging also opened the assistant.");
                Check(bubble.Position != originalPosition, "Drag did not move the bubble.");

                bubble.MouseDown(center, MouseButton.Left);
                bubble.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                bubble.MouseUp(center, MouseButton.Left); Pump();
                Check(opened == 1, "Cancelled press opened the assistant.");
                bubble.MouseDown(center, MouseButton.Right); bubble.MouseUp(center, MouseButton.Right); Pump();
                Check(opened == 1, "Right click opened the assistant.");
                // The compact circle now exposes its controls through a right-click menu.
                // Dismiss that menu before testing the next independent left click.
                bubble.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "WorkAssistantBubbleShell").ContextMenu?.Close(); Pump();

                bubble.MouseDown(center, MouseButton.Left); bubble.MouseUp(center, MouseButton.Left); Pump();
                Check(opened == 2, "Click stopped working after a drag/cancellation.");
            }
            finally { bubble.Close(); }
        });
        test("Work Assistant placement restores preferred monitor in DIPs and clamps to work area", () =>
        {
            var assembly = typeof(WorkAssistantBubbleWindow).Assembly;
            var placementType = assembly.GetType("H2Notes.Avalonia.WorkAssistantPlacement")
                ?? throw new Exception("WorkAssistantPlacement type missing.");
            var snapshotType = assembly.GetType("H2Notes.Avalonia.WorkAssistantScreenSnapshot")
                ?? throw new Exception("WorkAssistantScreenSnapshot type missing.");

            object Snapshot(string id, PixelRect area, double scale, bool primary)
                => Activator.CreateInstance(snapshotType, id, area, scale, primary)
                   ?? throw new Exception("Could not create screen snapshot.");

            var screens = Array.CreateInstance(snapshotType, 2);
            screens.SetValue(Snapshot("primary", new PixelRect(0, 0, 1920, 1080), 1d, true), 0);
            screens.SetValue(Snapshot("secondary", new PixelRect(1920, 0, 2560, 1440), 1.5d, false), 1);

            var settings = new WorkAssistantSettings
            {
                Enabled = true,
                PreferredMonitor = "secondary",
                BubblePosition = new WorkAssistantBubblePosition(100, 50)
            };

            var resolve = placementType.GetMethod(
                "Resolve",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new Exception("WorkAssistantPlacement.Resolve missing.");

            var restored = (PixelPoint)(resolve.Invoke(
                null,
                [settings, screens, 74d, 74d])
                ?? throw new Exception("Resolve returned null."));

            Check(restored.X == 2070 && restored.Y == 75,
                $"DPI restore is wrong: {restored}.");

            settings.BubblePosition = new WorkAssistantBubblePosition(5000, 5000);
            var clamped = (PixelPoint)(resolve.Invoke(
                null,
                [settings, screens, 74d, 74d])
                ?? throw new Exception("Resolve returned null."));

            // 74 DIP * 1.5 = 111 px. Placement must remain inside secondary 2560x1440 work area.
            Check(clamped.X >= 1924 && clamped.X <= 1920 + 2560 - 111 - 4,
                "Bubble X escaped preferred monitor work area.");
            Check(clamped.Y >= 4 && clamped.Y <= 1440 - 111 - 4,
                "Bubble Y escaped preferred monitor work area.");
        });

        test("Work Assistant bubble exposes idle working attention and completed states", () =>
        {
            var settings = new WorkAssistantSettings { Enabled = true, AlwaysOnTop = true };
            var bubble = new WorkAssistantBubbleWindow(settings, () => { });
            try
            {
                Check(bubble.State == WorkAssistantBubbleState.Idle,
                    "Bubble did not start idle.");

                bubble.SetState(WorkAssistantBubbleState.Working);
                Check(bubble.State == WorkAssistantBubbleState.Working,
                    "Working state was not applied.");

                bubble.SetState(WorkAssistantBubbleState.Attention, "Cần xác nhận");
                Check(bubble.State == WorkAssistantBubbleState.Attention,
                    "Attention state was not applied.");

                bubble.SetState(WorkAssistantBubbleState.Completed);
                Check(bubble.State == WorkAssistantBubbleState.Completed,
                    "Completed state was not applied.");

                bubble.SetState(WorkAssistantBubbleState.Idle);
                Check(bubble.State == WorkAssistantBubbleState.Idle,
                    "Idle state could not be restored.");
                Check(bubble.Topmost, "Always-on-top setting was not applied.");
            }
            finally
            {
                bubble.Close();
            }
        });

        test("Work Assistant bubble can live without MainWindow and disabled setting keeps it hidden", () =>
        {
            var app = new App();
            app.LocalSettings.WorkAssistant.Enabled = true;
            app.LocalSettings.WorkAssistant.StartWithH2 = false;

            var mainField = typeof(App).GetField("_main", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new Exception("App._main field missing.");
            Check(mainField.GetValue(app) is null,
                "Test precondition failed: MainWindow already exists.");

            app.ShowWorkAssistantBubble();
            Pump();
            Check(app.IsWorkAssistantBubbleVisible,
                "Bubble required MainWindow to become visible.");

            app.SetWorkAssistantBubbleState(WorkAssistantBubbleState.Working, "Đang làm");
            var bubble = Bubble(app);
            Check(bubble.State == WorkAssistantBubbleState.Working,
                "App did not project state to bubble.");

            app.ToggleWorkAssistantBubble();
            Pump();
            Check(!app.IsWorkAssistantBubbleVisible,
                "Tray-style toggle did not hide bubble.");

            app.ToggleWorkAssistantBubble();
            Pump();
            Check(app.IsWorkAssistantBubbleVisible,
                "Tray-style toggle did not show bubble.");

            app.LocalSettings.WorkAssistant.Enabled = false;
            app.HideWorkAssistantBubble();
            app.ShowWorkAssistantBubble();
            Pump();
            Check(!app.IsWorkAssistantBubbleVisible,
                "Disabled setting did not fail closed.");

            bubble.Close();
        });

        test("Work Assistant shell is wired to startup tray drag and local-only lifecycle", () =>
        {
            var repo = FindRepoRoot();
            var appSource = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "App.axaml.cs"));
            var bubbleSource = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "WorkAssistantBubbleWindow.cs"));

            Check(appSource.Contains("InitializeWorkAssistantBubble();", StringComparison.Ordinal),
                "App startup does not initialize Work Assistant shell.");
            Check(appSource.Contains("_local.WorkAssistant.StartWithH2", StringComparison.Ordinal),
                "StartWithH2 is not honored.");
            Check(appSource.Contains("Add(\"Work Assistant · hiện / ẩn\", ToggleWorkAssistantBubble);", StringComparison.Ordinal),
                "Tray has no Work Assistant show/hide action.");
            Check(appSource.Contains("if (!_local.WorkAssistant.Enabled)", StringComparison.Ordinal),
                "Enabled=false fail-closed guard missing.");
            Check(!appSource.Contains("TrackWindow(_workAssistantBubble", StringComparison.Ordinal),
                "Bubble leaked into shared/desktop-session tracked windows.");

            Check(bubbleSource.Contains("DesktopWindowChrome.AttachClickable(this, _shell, _activate);", StringComparison.Ordinal),
                "Bubble shell does not distinguish click activation from dragging.");
            Check(bubbleSource.Contains("WorkAssistantPlacement.Clamp", StringComparison.Ordinal)
                && bubbleSource.Contains("Screens.All", StringComparison.Ordinal)
                && bubbleSource.Contains("screen.Scaling", StringComparison.Ordinal),
                "Edge-safe multi-monitor/DPI placement wiring is incomplete.");

            foreach (var forbidden in new[]
            {
                "ProjectRecord", "ProjectLayout", "SheetState", "WorkspaceIndex",
                "ProjectWorkspaceStore.Save"
            })
                Check(!bubbleSource.Contains(forbidden, StringComparison.Ordinal),
                    "Bubble shell leaked local UI state into shared project storage: " + forbidden);
        });
    }

    private static WorkAssistantBubbleWindow Bubble(App app)
    {
        var field = typeof(App).GetField(
            "_workAssistantBubble",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("App._workAssistantBubble field missing.");
        return (WorkAssistantBubbleWindow)(field.GetValue(app)
            ?? throw new Exception("Work Assistant bubble was not created."));
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
            Dispatcher.UIThread.RunJobs();
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

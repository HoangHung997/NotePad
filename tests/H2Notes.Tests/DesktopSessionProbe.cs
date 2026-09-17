using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;
using H2Notes.Core;

internal static class DesktopSessionProbe
{
    // Runs in a separate headless process so the real app startup/exit hooks execute.
    public static int Run(string path, bool startup)
    {
        var before = SheetStorage.Read(path);
        var plan = DesktopRestorePlan.Create(before, startup);
        var expected = plan.OpenWindows.Select(n => n.Id).Concat(plan.ProjectAiWindowId is { } ai ? new[] { ai } : [])
            .OrderBy(id => before.DesktopSession?.OpenWindowIds.IndexOf(id) ?? 0).ToArray();
        var arguments = startup ? new[] { "--data", path, "--startup" } : new[] { "--data", path };
        AppBuilder.Configure<H2Notes.Avalonia.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithClassicDesktopLifetime(arguments);
        var app = (H2Notes.Avalonia.App)Application.Current!;
        var desktop = (ClassicDesktopStyleApplicationLifetime)app.ApplicationLifetime!;
        var failed = false;
        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                var windows = app.OpenWindows.Where(w => w.IsVisible).ToList();
                if (windows.Any(w => w.ShowInTaskbar || w.CanMinimize || w.CanMaximize))
                    throw new Exception("Unexpected taskbar entry or minimize/maximize support.");
                app.SaveNow();
                var saved = SheetStorage.Read(path);
                if (!expected.SequenceEqual(saved.DesktopSession!.OpenWindowIds))
                    throw new Exception("Visible windows or their order changed during startup.");
                if (windows.Count != expected.Length) throw new Exception("Unexpected auto-shown window.");
                if (windows.OfType<H2Notes.Avalonia.ProjectAiWindow>().FirstOrDefault() is { } projectAi
                    && projectAi.ProjectId != plan.Board.SelectedProjectId) throw new Exception("Desktop AI restored wrong project.");
                // This path fires ShutdownRequested and Closing(ApplicationShutdown), unlike force-exit.
                if (!desktop.TryShutdown()) throw new Exception("Window close incorrectly cancelled application shutdown.");
            }
            catch (Exception ex) { Console.WriteLine(ex); failed = true; desktop.Shutdown(); }
        }, TimeSpan.FromMilliseconds(150));
        desktop.Start(arguments);
        var after = SheetStorage.Read(path);
        if (!expected.SequenceEqual(after.DesktopSession!.OpenWindowIds)) failed = true;
        Console.WriteLine($"DESKTOP SESSION PROBE: {(failed ? "FAIL" : "PASS")}; startup={startup}; restored={expected.Length}; graceful shutdown preserved session.");
        return failed ? 1 : 0;
    }
}

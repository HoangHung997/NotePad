using System.Windows;

namespace Nodepad.Desktop;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var launchedFromSystemStartup = e.Args.Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));
        var mainWindow = new MainWindow(launchedFromSystemStartup);
        MainWindow = mainWindow;

        base.OnStartup(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is MainWindow mainWindow)
        {
            mainWindow.PrepareForAppExit();
        }

        base.OnSessionEnding(e);
    }
}

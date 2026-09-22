using Avalonia;

namespace H2Notes.Avalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--verify-portable")
        {
            Environment.ExitCode = PortablePackageDiagnostics.VerifyAsync(Path.GetFullPath(args[1])).GetAwaiter().GetResult();
            return;
        }
        if (args.Length == 2 && args[0] == "--verify-agent-helpers")
        {
            Environment.ExitCode = H2AgentLab.Integration.H2ProductionDiagnostics.VerifyHelpersAsync(Path.GetFullPath(args[1])).GetAwaiter().GetResult();
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

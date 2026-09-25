using Microsoft.Win32;

namespace H2AgentLab.DesktopHost;

public sealed record ResolvedDesktopApplication(
    string ApplicationId,
    string ExecutablePath,
    string ProcessName);

public static class DesktopApplicationResolver
{
    private static readonly IReadOnlyDictionary<string,string> Aliases =
        new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        {
            ["explorer"] = "explorer.exe",
            ["file explorer"] = "explorer.exe",
            ["windows explorer"] = "explorer.exe",
            ["notepad"] = "notepad.exe",
            ["word"] = "winword.exe",
            ["microsoft word"] = "winword.exe",
            ["winword"] = "winword.exe",
            ["excel"] = "excel.exe",
            ["microsoft excel"] = "excel.exe",
            ["autocad"] = "acad.exe",
            ["acad"] = "acad.exe",
            ["calculator"] = "calc.exe",
            ["calc"] = "calc.exe",
            ["paint"] = "mspaint.exe",
            ["mspaint"] = "mspaint.exe",
            ["edge"] = "msedge.exe",
            ["microsoft edge"] = "msedge.exe",
            ["msedge"] = "msedge.exe",
            ["chrome"] = "chrome.exe",
            ["google chrome"] = "chrome.exe"
        };

    public static string ProcessNameFor(string application)
    {
        var executable = ExecutableName(application);
        var process = Path.GetFileNameWithoutExtension(executable);
        DesktopSafetyPolicy.RequireLaunchProcessAllowed(process);
        return process;
    }

    public static ResolvedDesktopApplication ResolveForLaunch(string application)
    {
        if (!OperatingSystem.IsWindows())
            throw new DesktopHostFaultException("unsupported_operation", "Desktop application launch requires Windows.");
        var executable = ExecutableName(application);
        var process = Path.GetFileNameWithoutExtension(executable);
        DesktopSafetyPolicy.RequireLaunchProcessAllowed(process);
        var path = FindRegisteredExecutable(executable)
            ?? throw new DesktopHostFaultException(
                "app_not_found",
                "The requested application is not registered in an approved Windows application location.");
        return new(
            Path.GetFileNameWithoutExtension(executable).ToLowerInvariant(),
            path,
            process);
    }

    private static string ExecutableName(string application)
    {
        application = (application ?? "").Trim();
        if (application.Length is < 1 or > 128 || application.Any(char.IsControl)
            || application.IndexOfAny(['\\','/',':','"','\'',';','|','&','>','<']) >= 0
            || application.Contains("..", StringComparison.Ordinal))
            throw new DesktopHostFaultException(
                "invalid_application",
                "Use an application name, not an executable path or shell command.");

        if (Aliases.TryGetValue(application, out var alias))
            return alias;

        var candidate = application.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? application
            : application + ".exe";
        if (candidate.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
            throw new DesktopHostFaultException(
                "app_not_found",
                "Use a known application alias or an installed App Paths executable name.");
        return candidate;
    }

    private static string? FindRegisteredExecutable(string executable)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + executable,
                    writable: false);
                if (key?.GetValue(null) is string registered)
                {
                    registered = Environment.ExpandEnvironmentVariables(registered.Trim().Trim('"'));
                    if (File.Exists(registered)) return Path.GetFullPath(registered);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
            }
        }

        var system = Path.Combine(Environment.SystemDirectory, executable);
        if (File.Exists(system)) return Path.GetFullPath(system);
        var windows = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            executable);
        return File.Exists(windows) ? Path.GetFullPath(windows) : null;
    }
}

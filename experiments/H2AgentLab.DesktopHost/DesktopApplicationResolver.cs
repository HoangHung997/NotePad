using System.Diagnostics;
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
            ["explore"] = "explorer.exe",
            ["file explorer"] = "explorer.exe",
            ["file explore"] = "explorer.exe",
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
        => ResolveForLaunch(application).ProcessName;

    public static ResolvedDesktopApplication ResolveForLaunch(string application)
    {
        if (!OperatingSystem.IsWindows())
            throw new DesktopHostFaultException("unsupported_operation", "Desktop application launch requires Windows.");

        application = ValidateApplicationText(application);
        if (Aliases.TryGetValue(application, out var alias))
            return ResolveExecutable(alias)
                ?? throw new DesktopHostFaultException(
                    "app_not_found",
                    "The requested application is not registered in an approved Windows application location.");

        if (TryExecutableName(application, out var executable)
            && ResolveExecutable(executable) is { } exact)
            return exact;

        var normalized = NormalizeFriendlyName(application);
        var matches = RegisteredApplications()
            .Where(candidate => candidate.Names.Any(name =>
                string.Equals(NormalizeFriendlyName(name), normalized, StringComparison.Ordinal)))
            .Select(candidate => candidate.Application)
            .GroupBy(x => x.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(2)
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            > 1 => throw new DesktopHostFaultException(
                "ambiguous_target",
                "More than one registered Windows application exactly matches this friendly name."),
            _ => throw new DesktopHostFaultException(
                "app_not_found",
                "No exact registered Windows application matches this name.")
        };
    }

    private static string ValidateApplicationText(string application)
    {
        application = (application ?? "").Trim();
        if (application.Length is < 1 or > 128 || application.Any(char.IsControl)
            || application.IndexOfAny(['\\','/',':','"','\'',';','|','&','>','<']) >= 0
            || application.Contains("..", StringComparison.Ordinal))
            throw new DesktopHostFaultException(
                "invalid_application",
                "Use an application name, not an executable path or shell command.");
        return application;
    }

    private static bool TryExecutableName(string application, out string executable)
    {
        executable = application.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? application
            : application + ".exe";
        return executable.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    }

    private static ResolvedDesktopApplication? ResolveExecutable(string executable)
    {
        var path = FindRegisteredExecutable(executable);
        if (path is null) return null;
        if (!RegisteredExecutableIdentityMatches(executable, path))
            throw new DesktopHostFaultException(
                "invalid_application",
                "Registered application identity does not match its executable target.");

        var process = Path.GetFileNameWithoutExtension(path);
        DesktopSafetyPolicy.RequireLaunchProcessAllowed(process);
        return new(
            Path.GetFileNameWithoutExtension(executable).ToLowerInvariant(),
            path,
            process);
    }

    internal static bool RegisteredExecutableIdentityMatches(string executable, string resolvedPath)
    {
        if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(resolvedPath))
            return false;
        var expected = Path.GetFileNameWithoutExtension(executable.Trim());
        var actual = Path.GetFileNameWithoutExtension(resolvedPath.Trim());
        return expected.Length > 0
            && actual.Length > 0
            && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<RegisteredApplicationCandidate> RegisteredApplications()
    {
        const int maxEntries = 512;
        var result = new List<RegisteredApplicationCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            if (result.Count >= maxEntries) break;
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var appPaths = root.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
                    writable: false);
                if (appPaths is null) continue;

                string[] subKeys;
                try { subKeys = appPaths.GetSubKeyNames(); }
                catch (Exception ex) when (RegistryReadFailure(ex)) { continue; }

                foreach (var subKeyName in subKeys)
                {
                    if (result.Count >= maxEntries) break;
                    try
                    {
                        using var key = appPaths.OpenSubKey(subKeyName, writable: false);
                        if (key?.GetValue(null) is not string registered) continue;
                        registered = Environment.ExpandEnvironmentVariables(registered.Trim().Trim('"'));
                        if (!File.Exists(registered)) continue;
                        registered = Path.GetFullPath(registered);
                        if (!RegisteredExecutableIdentityMatches(subKeyName, registered)) continue;
                        if (!seen.Add(registered)) continue;

                        var process = Path.GetFileNameWithoutExtension(registered);
                        if (!DesktopSafetyPolicy.IsProcessAllowedForLaunch(process)) continue;

                        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        {
                            Path.GetFileNameWithoutExtension(subKeyName),
                            process
                        };
                        try
                        {
                            var version = FileVersionInfo.GetVersionInfo(registered);
                            if (!string.IsNullOrWhiteSpace(version.ProductName)) names.Add(version.ProductName);
                            if (!string.IsNullOrWhiteSpace(version.FileDescription)) names.Add(version.FileDescription);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                            or System.ComponentModel.Win32Exception or ArgumentException)
                        {
                        }

                        result.Add(new(
                            new(
                                Path.GetFileNameWithoutExtension(subKeyName).ToLowerInvariant(),
                                registered,
                                process),
                            names.ToArray()));
                    }
                    catch (Exception ex) when (RegistryReadFailure(ex))
                    {
                    }
                }
            }
            catch (Exception ex) when (RegistryReadFailure(ex))
            {
            }
        }

        return result;
    }

    private static bool RegistryReadFailure(Exception ex)
        => ex is UnauthorizedAccessException
            or IOException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException;

    private static string NormalizeFriendlyName(string value)
        => new(value.Normalize()
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private sealed record RegisteredApplicationCandidate(
        ResolvedDesktopApplication Application,
        IReadOnlyList<string> Names);

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
            catch (Exception ex) when (RegistryReadFailure(ex))
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

using System.Reflection;
using Microsoft.Win32;

namespace Nodepad.Desktop.Services;

public sealed class WindowsStartupService
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EntryName = "Nodepad";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
        var value = key?.GetValue(EntryName) as string;
        return !string.IsNullOrWhiteSpace(value);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunRegistryPath, writable: true);
        if (key is null)
        {
            return;
        }

        if (!enabled)
        {
            key.DeleteValue(EntryName, throwOnMissingValue: false);
            return;
        }

        var launchCommand = BuildLaunchCommand();
        if (!string.IsNullOrWhiteSpace(launchCommand))
        {
            key.SetValue(EntryName, launchCommand);
        }
    }

    private static string BuildLaunchCommand()
    {
        var processPath = Environment.ProcessPath;
        var assemblyPath = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(assemblyPath)
            && processPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return $"\"{processPath}\" \"{assemblyPath}\" --startup";
        }

        if (!string.IsNullOrWhiteSpace(assemblyPath)
            && processPath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return $"\"{processPath}\" \"{assemblyPath}\" --startup";
        }

        return $"\"{processPath}\" --startup";
    }
}

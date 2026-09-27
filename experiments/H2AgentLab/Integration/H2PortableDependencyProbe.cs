using Microsoft.Win32;

namespace H2AgentLab.Integration;

public sealed record H2PortableDependencyStatus(
    string Id,
    string State,
    string Code,
    string Message);

/// <summary>
/// Offline dependency inventory for portable preflight. This never launches Office/AutoCAD,
/// attaches COM, reads user documents, contacts a provider, or reads credentials.
/// </summary>
public static class H2PortableDependencyProbe
{
    public static IReadOnlyList<H2PortableDependencyStatus> Capture()
    {
        var result=new List<H2PortableDependencyStatus>();

        AddPackagedHelper(result,"desktop.helper","H2AgentLab.DesktopHost");
        AddPackagedHelper(result,"office.helper","H2AgentLab.OfficeHost");

        if(!OperatingSystem.IsWindows())
        {
            result.Add(new("office.native","Unsupported","windows_required","Native Office detection requires Windows."));
            result.Add(new("autocad.closed_file","Unsupported","windows_required","AutoCAD detection requires Windows."));
            result.Add(new("autocad.live_drawing","Unsupported","windows_required","Live AutoCAD detection requires Windows."));
            return result;
        }

        var word=HasAppPath("WINWORD.EXE");
        var excel=HasAppPath("EXCEL.EXE");
        result.Add(word||excel
            ? new("office.native","Degraded","installed_not_live_probed",
                "Word/Excel installation metadata was found. Live native-object attach is not exercised by portable preflight.")
            : new("office.native","Unavailable","office_not_installed",
                "No Word/Excel installation metadata was found for this Windows user/machine."));

        var core=H2AutoCadFileTools.FindExecutable();
        result.Add(core is not null
            ? new("autocad.closed_file","Ready","core_console_found",
                "AutoCAD Core Console is installed; no drawing was opened by preflight.")
            : new("autocad.closed_file","NeedsConfiguration","autocad_core_console_not_found",
                "AutoCAD Core Console is not installed or was not discovered."));

        var cadInstalled=core is not null || HasRegistryKey(Registry.CurrentUser,@"Software\Autodesk\AutoCAD")
            || HasRegistryKey(Registry.LocalMachine,@"SOFTWARE\Autodesk\AutoCAD")
            || HasRegistryKey(Registry.LocalMachine,@"SOFTWARE\WOW6432Node\Autodesk\AutoCAD")
            || HasClass("AutoCAD.Application");
        result.Add(cadInstalled
            ? new("autocad.live_drawing","Degraded","installed_not_live_probed",
                "AutoCAD installation metadata was found. A running same-user AutoCAD session/selection is not probed here.")
            : new("autocad.live_drawing","Unavailable","autocad_not_installed",
                "No AutoCAD installation metadata was found."));

        return result.AsReadOnly();
    }

    private static void AddPackagedHelper(List<H2PortableDependencyStatus> result,string id,string helper)
        => result.Add(H2HelperLocator.IsPackaged(helper)
            ? new(id,"Ready","packaged","Required helper executable, managed assembly and runtimeconfig are present.")
            : new(id,"Unavailable","packaged_helper_missing","Required packaged helper files are missing."));

    private static bool HasAppPath(string executable)
    {
        var key=@"Software\Microsoft\Windows\CurrentVersion\App Paths\"+executable;
        return HasRegistryKey(Registry.CurrentUser,key)
            || HasRegistryKey(Registry.LocalMachine,@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\"+executable)
            || HasRegistryKey(Registry.LocalMachine,@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\"+executable);
    }

    private static bool HasClass(string progId)
    {
        try { using var key=Registry.ClassesRoot.OpenSubKey(progId,false); return key is not null; }
        catch(Exception ex) when(ex is UnauthorizedAccessException or System.Security.SecurityException or IOException){return false;}
    }

    private static bool HasRegistryKey(RegistryKey root,string path)
    {
        try { using var key=root.OpenSubKey(path,false); return key is not null; }
        catch(Exception ex) when(ex is UnauthorizedAccessException or System.Security.SecurityException or IOException){return false;}
    }
}

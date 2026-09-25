namespace H2AgentLab.DesktopHost;

public static class DesktopSafetyPolicy
{
    private static readonly HashSet<string> BlockedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "powershell_ise", "WindowsTerminal", "OpenConsole", "conhost",
        "wsl", "bash", "ssh", "mstsc", "Taskmgr", "regedit", "mmc", "consent", "CredentialUIBroker",
        "rundll32", "regsvr32", "mshta", "wscript", "cscript", "msiexec", "runas",
        "wmic", "diskpart", "bcdedit", "schtasks", "sc", "net", "net1", "taskkill",
        "certutil", "bitsadmin", "wevtutil", "takeown", "icacls", "fodhelper", "ComputerDefaults",
        "LogonUI", "winlogon", "lsass", "SecHealthUI", "SecurityHealthSystray", "SystemSettings",
        "1Password", "Bitwarden", "KeePass", "KeePassXC", "NordPass", "LastPass",
        "Codex", "ChatGPT", "Code", "devenv"
    };

    private static readonly string[] SensitiveTitleTerms =
    [
        "password", "credential", "security", "authentication", "sign in",
        "mật khẩu", "bảo mật", "xác thực", "quyền truy cập", "trình quản lý mật khẩu"
    ];

    public static bool IsWindowAllowed(string processName, string title)
    {
        if (string.IsNullOrWhiteSpace(processName) || string.IsNullOrWhiteSpace(title))
            return false;
        var normalizedProcess = processName.Trim();
        if (BlockedProcesses.Contains(normalizedProcess))
            return false;
        if (normalizedProcess.Equals("H2AgentLab", StringComparison.OrdinalIgnoreCase)
            && !title.Equals(
                "H2 Agent Lab · Vùng thử an toàn",
                StringComparison.Ordinal))
            return false;
        if (SensitiveTitleTerms.Any(term => title.Contains(term, StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }

    public static bool IsProcessAllowedForLaunch(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var normalized = Path.GetFileNameWithoutExtension(processName.Trim());
        return !BlockedProcesses.Contains(normalized)
            && !normalized.StartsWith("H2AgentLab", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("H2Notes", StringComparison.OrdinalIgnoreCase);
    }

    public static void RequireLaunchProcessAllowed(string processName)
    {
        if (!IsProcessAllowedForLaunch(processName))
            throw new DesktopHostFaultException(
                "permission_denied",
                "This application process is blocked by desktop safety policy.");
    }

    public static void RequirePermission(bool granted)
    {
        if (!granted)
            throw new DesktopHostFaultException("permission_denied", "Desktop action was not approved.");
    }

    public static void RequireAction(string action)
    {
        if (!H2AgentLab.DesktopProtocol.DesktopActionKinds.All.Contains(action))
            throw new DesktopHostFaultException("invalid_action", $"Desktop action '{action}' is not supported.");
    }
}

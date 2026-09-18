namespace H2AgentLab.DesktopHost;

public static class DesktopSafetyPolicy
{
    private static readonly HashSet<string> BlockedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "powershell_ise", "WindowsTerminal", "OpenConsole", "conhost",
        "wsl", "bash", "ssh", "mstsc", "Taskmgr", "regedit", "mmc", "consent", "CredentialUIBroker",
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
        if (BlockedProcesses.Contains(processName.Trim()))
            return false;
        if (SensitiveTitleTerms.Any(term => title.Contains(term, StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
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

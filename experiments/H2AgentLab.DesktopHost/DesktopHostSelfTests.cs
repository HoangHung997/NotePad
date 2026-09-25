namespace H2AgentLab.DesktopHost;

public static class DesktopHostSelfTests
{
    public static int Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);
        var lines = new List<string>();
        var failed = 0;

        void Test(string name, Action action)
        {
            try
            {
                action();
                lines.Add("PASS " + name);
            }
            catch (Exception ex)
            {
                failed++;
                lines.Add("FAIL " + name + ": " + ex.Message);
            }
        }

        Test("Sensitive system and password-manager processes are blocked", () =>
        {
            foreach (var process in new[]
            {
                "CredentialUIBroker", "consent", "SecHealthUI",
                "1Password", "Bitwarden", "KeePass", "pwsh", "WindowsTerminal"
            })
            {
                if (DesktopSafetyPolicy.IsWindowAllowed(process, "Normal"))
                    throw new InvalidOperationException("Blocked process was allowed: " + process);
            }
        });

        Test("Launcher blocks shell credential and developer processes", () =>
        {
            foreach (var process in new[]
            {
                "cmd", "powershell", "pwsh", "regedit",
                "CredentialUIBroker", "1Password", "Bitwarden",
                "ChatGPT", "Codex", "Code", "devenv"
            })
            {
                if (DesktopSafetyPolicy.IsProcessAllowedForLaunch(process))
                    throw new InvalidOperationException("Blocked process was launchable: " + process);
            }

            if (!DesktopSafetyPolicy.IsProcessAllowedForLaunch("notepad"))
                throw new InvalidOperationException("Ordinary safe application was blocked from launch.");
        });

        Test("App Paths executable identity cannot redirect to another process", () =>
        {
            if (!DesktopApplicationResolver.RegisteredExecutableIdentityMatches(
                    "winword.exe",
                    @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE"))
                throw new InvalidOperationException("Matching App Paths executable identity was rejected.");

            if (DesktopApplicationResolver.RegisteredExecutableIdentityMatches(
                    "winword.exe",
                    @"C:\Windows\System32\cmd.exe"))
                throw new InvalidOperationException("App Paths executable identity redirected Word to cmd.exe.");

            if (DesktopApplicationResolver.RegisteredExecutableIdentityMatches(
                    "notepad.exe",
                    @"C:\Tools\powershell.exe"))
                throw new InvalidOperationException("App Paths executable identity accepted another executable stem.");
        });

        Test("Sensitive title terms are blocked", () =>
        {
            foreach (var title in new[]
            {
                "Enter password",
                "Credential security",
                "Nhập mật khẩu",
                "Xác thực bảo mật"
            })
            {
                if (DesktopSafetyPolicy.IsWindowAllowed("notepad", title))
                    throw new InvalidOperationException("Sensitive title was allowed: " + title);
            }
        });

        Test("H2 Agent Lab approval UI is blocked except the dedicated safe fixture", () =>
        {
            if (DesktopSafetyPolicy.IsWindowAllowed("H2AgentLab", "H2 Agent Lab · Bản thử độc lập"))
                throw new InvalidOperationException("Main H2 Agent Lab window became a desktop target.");
            if (!DesktopSafetyPolicy.IsWindowAllowed("H2AgentLab", "H2 Agent Lab · Vùng thử an toàn"))
                throw new InvalidOperationException("Dedicated H2 Agent Lab safe fixture was blocked.");
        });

        Test("Ordinary user application window is allowed", () =>
        {
            if (!DesktopSafetyPolicy.IsWindowAllowed("notepad", "Fixture Notes"))
                throw new InvalidOperationException("Ordinary safe window was blocked.");
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        File.WriteAllLines(Path.Combine(root, "desktop-host-self-tests.txt"), lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }
}

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
                "rundll32", "regsvr32", "mshta", "wscript", "cscript", "msiexec",
                "runas", "wmic", "diskpart", "bcdedit", "schtasks", "taskkill",
                "certutil", "bitsadmin", "wevtutil", "takeown", "icacls", "fodhelper",
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

        Test("Office launch recognition ignores splash and dialog windows", () =>
        {
            if (!Win32DesktopBackend.IsApplicationLaunchWindowClass("WINWORD", "OpusApp"))
                throw new InvalidOperationException("Word main window class was rejected.");
            if (Win32DesktopBackend.IsApplicationLaunchWindowClass("WINWORD", "NUIDialog"))
                throw new InvalidOperationException("Word dialog/splash class was accepted as launch completion.");
            if (!Win32DesktopBackend.IsApplicationLaunchWindowClass("EXCEL", "XLMAIN"))
                throw new InvalidOperationException("Excel main window class was rejected.");
            if (Win32DesktopBackend.IsApplicationLaunchWindowClass("EXCEL", "bosa_sdm_XL9"))
                throw new InvalidOperationException("Excel dialog class was accepted as launch completion.");
            if (!Win32DesktopBackend.IsApplicationLaunchWindowClass("notepad", "Notepad"))
                throw new InvalidOperationException("Ordinary application launch window was rejected.");
        });

        Test("Office new-window mode uses only host-owned safe switches", () =>
        {
            if (DesktopApplicationResolver.NewWindowArgumentsForProcess("WINWORD") != "/w")
                throw new InvalidOperationException("Word new-window launch lost the documented /w switch.");
            if (DesktopApplicationResolver.NewWindowArgumentsForProcess("EXCEL") != "/x")
                throw new InvalidOperationException("Excel new-window launch lost the documented /x switch.");
            foreach (var process in new[] { "explorer", "acad", "notepad", "chrome" })
                if (DesktopApplicationResolver.NewWindowArgumentsForProcess(process).Length != 0)
                    throw new InvalidOperationException("Unexpected command-line switch for ordinary application: " + process);
        });

        Test("Friendly app identity preserves meaningful punctuation", () =>
        {
            if (!DesktopApplicationResolver.FriendlyNameMatches("Microsoft Word", " microsoft   word "))
                throw new InvalidOperationException("Whitespace/case normalization rejected an exact friendly name.");
            if (!DesktopApplicationResolver.FriendlyNameMatches("AutoCAD 2026", "autocad 2026"))
                throw new InvalidOperationException("Exact AutoCAD friendly name was rejected.");
            if (DesktopApplicationResolver.FriendlyNameMatches("Notepad++", "Notepad"))
                throw new InvalidOperationException("Friendly-name normalization collapsed Notepad++ into Notepad.");
            if (DesktopApplicationResolver.FriendlyNameMatches("App-X", "App X"))
                throw new InvalidOperationException("Friendly-name normalization discarded meaningful punctuation.");
        });

        Test("Launch never guesses among multiple newly observed windows", () =>
        {
            var before = new HashSet<string>(StringComparer.Ordinal) { "before-1" };
            var one = new DesktopWindowInfo(
                "new-1", 101, 11, 1011, "fixture", "One",
                new H2AgentLab.DesktopProtocol.DesktopBounds(0, 0, 640, 480), 96, true);
            var selected = Win32DesktopBackend.SelectUniqueCreatedWindow([one], before);
            if (selected?.SessionId != "new-1")
                throw new InvalidOperationException("Unique new application window was not selected.");

            var two = one with { SessionId = "new-2", Handle = 102 };
            try
            {
                _ = Win32DesktopBackend.SelectUniqueCreatedWindow([one, two], before);
                throw new InvalidOperationException("Multiple new windows were silently reduced to one target.");
            }
            catch (DesktopHostFaultException ex) when (ex.Code == "launch_ambiguous")
            {
            }
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

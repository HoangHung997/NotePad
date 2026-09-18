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

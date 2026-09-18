using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace H2Notes.Core;

/// <summary>
/// Makes an OCR runtime prepared with tools/ocr/install.py --bundle-python relocatable.
/// Python virtual environments store the original base interpreter path in pyvenv.cfg;
/// after the whole H2 Notes folder is copied to another PC we rebuild only the venv
/// launchers from the bundled Python. Installed packages and local model files stay in place.
/// </summary>
public static class PortableOcrRuntime
{
    private static readonly ConcurrentDictionary<string, byte> Checked = new(StringComparer.OrdinalIgnoreCase);

    public static string Resolve(string configuredRoot)
    {
        var bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "ocr-runtime"));
        var configured = string.IsNullOrWhiteSpace(configuredRoot) ? bundled : Path.GetFullPath(configuredRoot);
        var root = File.Exists(Path.Combine(configured, "runtime.json")) ? configured
            : File.Exists(Path.Combine(bundled, "runtime.json")) ? bundled : configured;
        EnsureRelocated(root);
        return root;
    }

    public static void Invalidate(string root) => Checked.TryRemove(Path.GetFullPath(root), out _);

    public static void EnsureRelocated(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        root = Path.GetFullPath(root);
        var manifestPath = Path.Combine(root, "runtime.json");
        // Do not cache a missing bundle. The user may package the runtime beside the app later
        // in this same process and it must then be inspected/repaired immediately.
        if (!File.Exists(manifestPath)) return;
        if (!Checked.TryAdd(root, 0)) return;
        try
        {
            if (new FileInfo(manifestPath).Length > 64 * 1024) return;
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            if (!document.RootElement.TryGetProperty("pythonBundled", out var bundledFlag) || bundledFlag.ValueKind != JsonValueKind.True) return;
            var bundledPython = Path.Combine(root, "python", "python.exe");
            var venv = Path.Combine(root, "venv");
            var venvPython = Path.Combine(venv, "Scripts", "python.exe");
            var config = Path.Combine(venv, "pyvenv.cfg");
            if (!File.Exists(bundledPython) || !Directory.Exists(venv) || !File.Exists(venvPython)) return;

            var desiredHome = Path.GetFullPath(Path.Combine(root, "python"));
            if (File.Exists(config))
            {
                var text = File.ReadAllText(config);
                var home = text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("home =", StringComparison.OrdinalIgnoreCase));
                if (home is not null)
                {
                    var current = home[(home.IndexOf('=') + 1)..].Trim();
                    if (Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar).Equals(desiredHome.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return;
                }
            }

            using var gate = new FileStream(Path.Combine(root, ".h2-portable-ocr.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            // Another process may have repaired it while this one waited.
            if (File.Exists(config) && File.ReadAllText(config).Contains(desiredHome, StringComparison.OrdinalIgnoreCase)) return;

            var start = new ProcessStartInfo(bundledPython)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-I");
            start.ArgumentList.Add("-m");
            start.ArgumentList.Add("venv");
            start.ArgumentList.Add("--upgrade");
            start.ArgumentList.Add("--without-pip");
            start.ArgumentList.Add(venv);
            start.Environment.Clear();
            foreach (var name in new[] { "SystemRoot", "WINDIR", "SystemDrive" })
                if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
            using var process = Process.Start(start) ?? throw new IOException("Không khởi động được Python đi kèm để sửa đường dẫn OCR portable.");
            if (!process.WaitForExit(45_000))
            {
                try { process.Kill(true); } catch { }
                throw new IOException("Quá thời gian sửa OCR portable sau khi di chuyển ứng dụng.");
            }
            if (process.ExitCode != 0 || !File.Exists(venvPython))
                throw new IOException("Bộ OCR portable chưa thích nghi được với đường dẫn máy mới. Hãy chép trọn thư mục ứng dụng vào nơi có quyền ghi.");
        }
        catch
        {
            Checked.TryRemove(root, out _);
            throw;
        }
    }
}

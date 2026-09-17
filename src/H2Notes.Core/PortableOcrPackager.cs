using System.Text.Json;

namespace H2Notes.Core;

public sealed record PortableOcrInspection(
    string Root,
    bool Ready,
    bool PythonBundled,
    long Bytes,
    IReadOnlyDictionary<string, bool> Engines,
    string Message);

/// <summary>
/// Copies a fully prepared OCR runtime beside H2Notes.Avalonia.exe so the whole app
/// folder can be moved to another Windows x64 PC without relying on host Python or
/// machine-specific model paths. Source data is never deleted.
/// </summary>
public static class PortableOcrPackager
{
    private static readonly string[] EngineNames = ["got-ocr", "docling", "mineru"];
    public static string AppRuntimeRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "ocr-runtime"));

    public static PortableOcrInspection Inspect(string root)
    {
        root = LocalRoot(root);
        var manifest = Path.Combine(root, "runtime.json");
        if (!File.Exists(manifest)) return new(root, false, false, 0, EmptyEngines(), "Thiếu runtime.json.");
        RejectLinks(root);
        if (new FileInfo(manifest).Length > 64 * 1024) return new(root, false, false, 0, EmptyEngines(), "runtime.json quá lớn.");

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(manifest), new JsonDocumentOptions { MaxDepth = 12 });
            var data = json.RootElement;
            if (!data.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1)
                return new(root, false, false, 0, EmptyEngines(), "Phiên bản runtime OCR không được hỗ trợ.");
            var bundled = data.TryGetProperty("pythonBundled", out var bundledNode) && bundledNode.ValueKind == JsonValueKind.True;
            var engines = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in EngineNames)
            {
                var ready = data.TryGetProperty("engines", out var all)
                    && all.TryGetProperty(name, out var info)
                    && info.TryGetProperty("ready", out var readyNode) && readyNode.ValueKind == JsonValueKind.True
                    && info.TryGetProperty("modelsPath", out var modelsNode) && modelsNode.ValueKind == JsonValueKind.String
                    && modelsNode.GetString()?.Replace('\\', '/') == "models/" + name;
                var modelFolder = Path.Combine(root, "models", name);
                ready &= Directory.Exists(modelFolder) && Directory.EnumerateFileSystemEntries(modelFolder).Any();
                engines[name] = ready;
            }
            var python = Path.Combine(root, "python", "python.exe");
            var venvPython = Path.Combine(root, "venv", "Scripts", "python.exe");
            var portable = bundled && File.Exists(python) && File.Exists(venvPython);
            var readyAll = portable && engines.Values.All(v => v);
            var bytes = DirectoryBytes(root);
            var message = readyAll
                ? "Đủ Python portable + GOT-OCR 2.0 + Docling + MinerU. Có thể đóng gói cùng ứng dụng."
                : !portable
                    ? "Runtime OCR đang chạy trên máy này nhưng chưa kèm Python portable. Hãy tạo runtime portable trước khi chép sang PC khác."
                    : "Python portable đã có nhưng một hoặc nhiều engine/model OCR chưa sẵn sàng.";
            return new(root, readyAll, portable, bytes, engines, message);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(root, false, false, 0, EmptyEngines(), "Không đọc được runtime OCR: " + ex.Message);
        }
    }

    public static PortableOcrInspection CopyIntoApp(string sourceRoot, bool replaceExisting, IProgress<string>? progress = null)
    {
        var source = LocalRoot(sourceRoot);
        var inspection = Inspect(source);
        if (!inspection.Ready) throw new InvalidOperationException(inspection.Message);
        var destination = AppRuntimeRoot;
        if (SamePath(source, destination))
        {
            PortableOcrRuntime.EnsureRelocated(destination);
            return Inspect(destination);
        }
        if (IsNested(source, destination) || IsNested(destination, source))
            throw new IOException("Không đóng gói OCR vào thư mục nằm bên trong chính runtime nguồn hoặc ngược lại.");
        if (Directory.Exists(destination) && !replaceExisting)
            throw new IOException("Thư mục OCR portable đã tồn tại cạnh ứng dụng. Xác nhận thay thế trước khi đóng gói lại.");

        var parent = Path.GetDirectoryName(destination) ?? throw new IOException("Không xác định được thư mục ứng dụng.");
        Directory.CreateDirectory(parent);
        var drive = new DriveInfo(Path.GetPathRoot(parent)!);
        var required = inspection.Bytes + Math.Max(512L * 1024 * 1024, inspection.Bytes / 20);
        if (drive.AvailableFreeSpace < required)
            throw new IOException($"Không đủ dung lượng để đóng gói OCR. Cần khoảng {required / 1024d / 1024d / 1024d:0.0} GiB trống.");

        var stage = destination + ".staging-" + Guid.NewGuid().ToString("N");
        var backup = destination + ".previous-" + Guid.NewGuid().ToString("N");
        progress?.Report("Đang sao chép runtime OCR portable. Không xóa runtime nguồn...");
        try
        {
            CopyTree(source, stage, progress);
            var staged = Inspect(stage);
            if (!staged.Ready) throw new InvalidDataException("Bản OCR vừa sao chép chưa vượt kiểm tra portability: " + staged.Message);

            var hadDestination = Directory.Exists(destination);
            if (hadDestination) Directory.Move(destination, backup);
            try
            {
                Directory.Move(stage, destination);
                PortableOcrRuntime.EnsureRelocated(destination);
                var final = Inspect(destination);
                if (!final.Ready) throw new InvalidDataException("OCR portable chưa sẵn sàng sau khi chuyển vào thư mục ứng dụng.");
                if (Directory.Exists(backup)) Directory.Delete(backup, true);
                progress?.Report("Đã đóng gói đủ 3 engine OCR cạnh ứng dụng.");
                return final;
            }
            catch
            {
                if (Directory.Exists(destination)) Directory.Delete(destination, true);
                if (Directory.Exists(backup)) Directory.Move(backup, destination);
                throw;
            }
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }

    private static void CopyTree(string source, string destination, IProgress<string>? progress)
    {
        RejectLinks(source);
        Directory.CreateDirectory(destination);
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToArray();
        var copied = 0;
        foreach (var file in files)
        {
            RejectLinks(file);
            var relative = Path.GetRelativePath(source, file);
            if (relative.Equals(".h2-portable-ocr.lock", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
            copied++;
            if (copied % 250 == 0) progress?.Report($"Đang sao chép OCR: {copied}/{files.Length} tệp...");
        }
    }

    private static Dictionary<string, bool> EmptyEngines() => EngineNames.ToDictionary(x => x, _ => false, StringComparer.OrdinalIgnoreCase);

    private static long DirectoryBytes(string root)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            RejectLinks(file);
            total = checked(total + new FileInfo(file).Length);
        }
        return total;
    }

    private static string LocalRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || root.StartsWith("\\\\", StringComparison.Ordinal)
            || root.StartsWith("//", StringComparison.Ordinal) || root.Any(char.IsControl))
            throw new InvalidOperationException("Runtime OCR phải là thư mục cục bộ tuyệt đối, không dùng đường dẫn mạng.");
        return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
    }

    private static bool SamePath(string a, string b) => Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar)
        .Equals(Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static bool IsNested(string parent, string child)
    {
        parent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        child = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Không đóng gói OCR qua symlink/junction: " + current);
    }
}

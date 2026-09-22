using System.Text.Json;
using System.Security.Cryptography;

namespace H2AgentLab;

public sealed record JournalEvent(DateTime At, string Kind, string Text);

/// <summary>
/// Durable user/task journal. Ephemeral UI progress and machine telemetry events are separate:
/// progress stays in AgentProgressEventStream, while telemetry is persisted by AgentTraceStore.
/// </summary>
public sealed class LabSession
{
    private static readonly HashSet<string> EphemeralUiKinds = new(StringComparer.Ordinal)
    {
        "status",
        "progress",
        "thinking",
        "thinking-clear",
        "delta"
    };

    public int Schema { get; set; } = 1;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Workspace { get; set; } = "";
    public List<JournalEvent> Events { get; set; } = [];

    public void Add(string kind, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (EphemeralUiKinds.Contains(kind))
            throw new InvalidOperationException(
                "Ephemeral UI progress belongs in AgentProgressEventStream, not the durable LabSession journal.");
        Events.Add(new(
            DateTime.UtcNow,
            kind.Trim(),
            text ?? ""));
    }

    public void AddTelemetryReference(string tracePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tracePath);
        Add(
            "telemetry-reference",
            "Turn telemetry: " + Path.GetFileName(tracePath));
    }
    public string Context()
    {
        var text = string.Join("\n", Events.Where(e => e.Kind != "script").TakeLast(40).Select(e => $"[{e.At:O}] {e.Kind}: {e.Text}"));
        return text.Length > 60000 ? "[Earlier recent activity omitted; ask the user or inspect files again.]\n" + text[^60000..] : text;
    }
    public void Save(string stateRoot)
    {
        Directory.CreateDirectory(stateRoot);
        var json = JsonSerializer.Serialize(this);
        if (json.Length > 12_000_000) throw new IOException("Lịch sử lớn; tạo cuộc trao đổi mới trước khi tiếp tục.");
        WriteAtomic(Path.Combine(stateRoot, Id.ToString("N") + ".json"), json);
        WriteAtomic(Path.Combine(stateRoot, "session.json"), json);
    }
    private static void WriteAtomic(string file, string json)
    {
        var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, json); File.Move(tmp, file, true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
    public static LabSession Load(string stateRoot)
    {
        var file = Path.Combine(stateRoot, "session.json");
        if (!File.Exists(file)) return new();
        if (new FileInfo(file).Length > 16 * 1024 * 1024) throw new IOException("Lịch sử lớn hơn giới hạn bản thử; tệp gốc được giữ nguyên.");
        var session = JsonSerializer.Deserialize<LabSession>(File.ReadAllText(file)) ?? throw new IOException("Lịch sử không hợp lệ.");
        if (session.Schema != 1) throw new IOException("Phiên bản lịch sử chưa hỗ trợ.");
        return session;
    }
}

public sealed class SafeWorkspace
{
    public string Root { get; }
    private readonly Func<bool>? _fullAccessAuthorized;
    private readonly Func<string, bool>? _additionalTarget;
    public static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".txt", ".md", ".csv", ".json", ".cs", ".csproj", ".slnx", ".xaml", ".axaml", ".py", ".js", ".ts", ".tsx", ".html", ".css", ".xml", ".yml", ".yaml" };
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".ssh", ".codex", ".aws", ".azure", "node_modules", "bin", "obj", ".vs", ".env", "credentials", "secrets.json", "appsettings.production.json" };
    public SafeWorkspace(string root, Func<bool>? fullAccessAuthorized = null, Func<string, bool>? additionalTarget = null)
    {
        _fullAccessAuthorized = fullAccessAuthorized;
        _additionalTarget = additionalTarget;
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(Root) || Root == Path.GetPathRoot(Root)) throw new IOException("Chọn một thư mục dự án cụ thể, không chọn cả ổ đĩa.");
        CheckLinks(Root);
    }
    private static void CheckLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new AgentFaultException("boundary", "Không theo symbolic link/junction trong bản thử.", false);
    }
    public string Resolve(string relative, bool directory = false)
    {
        if (_fullAccessAuthorized is null && _additionalTarget is not null && relative.IndexOfAny(['\0', '*', '?']) < 0)
        {
            var candidate = Path.GetFullPath(relative, Root);
            if (_additionalTarget(candidate))
            {
                var components = candidate.Replace('\\', '/').Split('/');
                if (components.Skip(1).Any(p => p.Contains(':') || p.EndsWith(' ') || p.EndsWith('.')
                    || System.Text.RegularExpressions.Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\.)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                    throw new AgentFaultException("boundary", "Đường dẫn tệp không hợp lệ.", false);
                CheckLinks(candidate); return candidate;
            }
        }
        if (_fullAccessAuthorized is not null)
        {
            if (!_fullAccessAuthorized()) throw new AgentFaultException("expired_permission", "Quyền toàn máy đã hết hạn hoặc bị thu hồi.", false);
            // Full access intentionally has no workspace or junction boundary. Windows ACLs
            // still apply. Reject device/stream syntax; these tools operate on ordinary files.
            if (relative.IndexOfAny(['\0', '*', '?']) >= 0 || relative.StartsWith(@"\\.\", StringComparison.Ordinal)
                || relative.Replace('\\', '/').Split('/').Any(p => p.Contains(':') && !(p.Length == 2 && char.IsLetter(p[0]))))
                throw new AgentFaultException("boundary", "Đường dẫn tệp không hợp lệ.", false);
            return Path.GetFullPath(relative, Root);
        }
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.IndexOfAny(['\0', '*', '?']) >= 0)
            throw new AgentFaultException("boundary", "Chỉ dùng đường dẫn tương đối trong thư mục được chọn.", false);
        var parts = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => System.Text.RegularExpressions.Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\.)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            throw new AgentFaultException("boundary", "Không truy cập tên thiết bị Windows.", false);
        if (parts.Any(p => p == ".." || p.EndsWith(' ') || p.EndsWith('.') && p != "." || Excluded.Contains(p) || p.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".key", StringComparison.OrdinalIgnoreCase)))
            throw new AgentFaultException("boundary", "Đường dẫn bị chặn hoặc có thể chứa bí mật.", false);
        var path = Path.GetFullPath(Path.Combine(Root, relative));
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !(directory && path == Root))
            throw new AgentFaultException("boundary", "Ngoài phạm vi được chọn.", false);
        CheckLinks(path);
        return path;
    }
    public IReadOnlyList<string> Files(string relative = ".")
        => Scan(relative).Files;
    public FileScan Scan(string relative = ".", int limit = 200, CancellationToken cancellationToken = default)
    {
        var path = Resolve(relative, true); var found = new List<string>();
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Folder not found: " + relative);
        var queue = new Queue<string>(); queue.Enqueue(path); var visited = 0; var unreadable = 0; var truncated = false;
        while (queue.Count > 0 && found.Count < limit && visited++ < 400)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] children;
            try { children = Directory.EnumerateFileSystemEntries(queue.Dequeue()).Take(1001).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable++; continue; }
            if (children.Length > 1000) truncated = true;
            foreach (var child in children.Take(1000))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetRelativePath(Root, child);
                try { Resolve(name, Directory.Exists(child)); } catch (IOException) { continue; }
                if (Directory.Exists(child)) queue.Enqueue(child);
                else if (TextExtensions.Contains(Path.GetExtension(child)) || Path.GetExtension(child).ToLowerInvariant() is ".docx" or ".xlsx" or ".pdf" or ".png" or ".jpg") found.Add(name);
                if (found.Count == limit) { truncated = true; break; }
            }
        }
        return new(found, truncated || queue.Count > 0, unreadable, visited);
    }
    public byte[] Read(string relative)
    {
        var path = Resolve(relative);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 8 * 1024 * 1024) throw new IOException("Tệp vượt 8 MB.");
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes); return bytes;
    }
    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    public string Write(string relative, byte[] bytes, string expectedHash, string stateRoot)
    {
        if (bytes.Length > 8 * 1024 * 1024) throw new IOException("Tệp vượt 8 MB.");
        var path = Resolve(relative); var existed = File.Exists(path);
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new IOException("Thư mục đích chưa tồn tại.");
        if (existed)
        {
            var old = Read(relative);
            if (expectedHash.Length == 0 || Hash(old) != expectedHash) throw new IOException("Tệp đã đổi hoặc chưa đọc trước khi sửa. Đọc lại và duyệt lại.");
            var backupRoot = Path.Combine(stateRoot, "backups"); Directory.CreateDirectory(backupRoot);
            File.WriteAllBytes(Path.Combine(backupRoot, Guid.NewGuid().ToString("N") + Path.GetExtension(path)), old);
        }
        else if (expectedHash.Length != 0) throw new IOException("Tệp gốc không còn tồn tại.");
        var temp = Path.Combine(Path.GetDirectoryName(path)!, ".h2-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
            Resolve(relative);
            // A second check catches changes made while the approval dialog was open.
            if (existed && Hash(Read(relative)) != expectedHash) throw new IOException("Tệp vừa bị sửa bên ngoài; chưa ghi đè.");
            File.Move(temp, path, existed);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return Hash(Read(relative));
    }
}

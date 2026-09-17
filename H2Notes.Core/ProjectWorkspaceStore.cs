using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace H2Notes.Core;

// The index is published last. A durable journal restores the previous generation
// after an interrupted multi-file save; unchanged project files are never rewritten.
public sealed class ProjectWorkspaceStore : INoteStorage
{
    public const int SchemaVersion = 4;
    private int _sourceVersion = SchemaVersion;
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private Dictionary<string, string> _known = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;
    private Dictionary<Guid, FileEntry> _entries = [];
    private readonly Action<int>? _checkpoint;
    public string Root { get; }
    public string FilePath => Path.Combine(Root, "workspace.h2index.json");
    private string JournalPath => Path.Combine(Root, ".h2-transaction.json");

    public ProjectWorkspaceStore(string root, Action<int>? checkpoint = null)
    {
        Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        _checkpoint = checkpoint;
    }

    public FileStream AcquireLock()
    {
        Directory.CreateDirectory(Root);
        return new FileStream(Path.Combine(Root, ".h2-workspace.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public SheetState LoadOrImport(string? legacyPath = null)
    {
        Recover();
        if (File.Exists(FilePath)) return Read();
        if (FindDataFiles().Any()) throw new InvalidDataException("Có tệp dự án nhưng thiếu chỉ mục kho. Không tạo kho trống đè lên dữ liệu.");
        _loaded = true; _known.Clear();
        if (legacyPath is null || !File.Exists(legacyPath)) return new SheetState();
        var state = SheetStorage.Read(legacyPath);
        Directory.CreateDirectory(Path.Combine(Root, "backups"));
        File.Copy(legacyPath, Path.Combine(Root, "backups", "migration-" + Guid.NewGuid().ToString("N") + ".json"));
        Save(state);
        return state;
    }

    public SheetState Read()
    {
        var indexBytes = File.ReadAllBytes(FilePath);
        var index = Decode<WorkspaceIndex>(indexBytes);
        if (index.SchemaVersion is not 2 and not 3 and not SchemaVersion) throw new InvalidDataException("Kho thuộc phiên bản không được hỗ trợ. Không ghi đè.");
        if (index.State is null || index.Files is null) throw new InvalidDataException("Chỉ mục kho không đầy đủ.");
        var state = index.State;
        var fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["workspace.h2index.json"] = Hash(indexBytes) };
        var ids = new HashSet<Guid>();
        foreach (var entry in index.Files)
        {
            if (!ids.Add(entry.Id)) throw new InvalidDataException("ID trùng trong chỉ mục kho.");
            var path = Resolve(entry.File);
            var bytes = File.ReadAllBytes(path);
            if (Hash(bytes) != entry.Hash) throw new InvalidDataException("Tệp đã thay đổi ngoài app hoặc bị lỗi: " + entry.File);
            fingerprints.Add(entry.File, entry.Hash);
            if (entry.Kind == "project")
            {
                var doc = Decode<ProjectFile>(bytes);
                if (doc.SchemaVersion != index.SchemaVersion || doc.Project?.Id != entry.Id || doc.BoardId != entry.BoardId)
                    throw new InvalidDataException("Tệp dự án không khớp chỉ mục: " + entry.File);
                var board = state.Notes.SingleOrDefault(n => n.Id == entry.BoardId)
                    ?? throw new InvalidDataException("Không tìm thấy bảng sở hữu dự án.");
                board.Projects.Add(doc.Project);
            }
            else if (entry.Kind == "note")
            {
                var doc = Decode<GeneralNoteFile>(bytes);
                if (doc.SchemaVersion != index.SchemaVersion || doc.Note?.Id != entry.Id || doc.Note.IsBoard)
                    throw new InvalidDataException("Tệp ghi chú không hợp lệ: " + entry.File);
                state.Notes.Add(doc.Note);
            }
            else throw new InvalidDataException("Loại tệp trong kho không được hỗ trợ.");
        }
        ValidateState(state);
        _known = fingerprints; _entries = index.Files.ToDictionary(e => e.Id); _loaded = true; _sourceVersion = index.SchemaVersion;
        return state;
    }

    public void Save(SheetState state)
        => SaveIncremental(state, null);

    public void SaveIncremental(SheetState state, IReadOnlySet<Guid>? changedProjects)
    {
        if (!_loaded) throw new InvalidOperationException("Đọc kho trước khi lưu để kiểm tra xung đột.");
        ValidateState(state);
        // Upgrade all files in the same backed-up transaction. Older clients must
        // reject v3 rather than sending new local-only markers as normal chat turns.
        if (_sourceVersion != SchemaVersion) changedProjects = null;
        AssertUnchanged();
        Directory.CreateDirectory(Root);
        var data = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        var index = new WorkspaceIndex { State = new SheetState { SheetSchemaVersion = state.SheetSchemaVersion,
            SheetPreferences = state.SheetPreferences, ImportHistory = state.ImportHistory, DesktopSession = state.DesktopSession, Extra = state.Extra } };
        foreach (var note in state.Notes)
        {
            if (note.IsBoard)
            {
                index.State.Notes.Add(note.IndexShell());
                foreach (var project in note.Projects)
                {
                    if (changedProjects is not null && !changedProjects.Contains(project.Id) && _entries.TryGetValue(project.Id, out var cached)
                        && cached.BoardId == note.Id && cached.Kind == "project")
                    { data.Add(cached.File, null); index.Files.Add(cached); continue; }
                    var name = "projects/" + SafeName(project.DisplayName, project.Id) + ".h2project.json";
                    var bytes = Encode(new ProjectFile { BoardId = note.Id, Project = project });
                    data.Add(name, bytes); index.Files.Add(new(project.Id, note.Id, "project", name, Hash(bytes)));
                }
            }
            else
            {
                var name = "notes/" + SafeName(note.Title, note.Id) + ".h2note.json";
                var bytes = Encode(new GeneralNoteFile { Note = note });
                data.Add(name, bytes); index.Files.Add(new(note.Id, null, "note", name, Hash(bytes)));
            }
        }
        data.Add("workspace.h2index.json", Encode(index));
        var changed = data.Where(p => p.Value is not null && (!_known.TryGetValue(p.Key, out var hash) || hash != Hash(p.Value))).Select(p => p.Key).ToList();
        var removed = _known.Keys.Where(k => !data.ContainsKey(k)).ToList();
        if (changed.Count == 0 && removed.Count == 0) return;
        var transactionId = Guid.NewGuid().ToString("N");
        var backup = "backups/save-" + transactionId;
        var entries = changed.Concat(removed).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k == "workspace.h2index.json" ? 1 : 0)
            .Select((file, i) => new JournalEntry(file, File.Exists(Resolve(file)), backup + "/" + i + ".bak")).ToList();
        foreach (var entry in entries)
        {
            var target = Resolve(entry.File);
            if (!_known.ContainsKey(entry.File) && File.Exists(target))
                throw new IOException("Không ghi đè tệp chưa thuộc chỉ mục: " + entry.File);
            if (entry.Existed)
            {
                var saved = ResolveBackup(entry.Backup);
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!); File.Copy(target, saved, false);
            }
        }
        var journal = new SaveJournal { Entries = entries };
        AssertUnchanged();
        AtomicWrite(JournalPath, Encode(journal));
        try
        {
            var count = 0;
            foreach (var entry in entries)
            {
                var target = Resolve(entry.File);
                if (data.TryGetValue(entry.File, out var bytes) && bytes is not null) AtomicWrite(target, bytes);
                else File.Delete(target);
                _checkpoint?.Invoke(++count);
            }
            File.Delete(JournalPath);
            _known = data.ToDictionary(p => p.Key, p => p.Value is null ? _known[p.Key] : Hash(p.Value), StringComparer.OrdinalIgnoreCase);
            _entries = index.Files.ToDictionary(e => e.Id);
            _sourceVersion = SchemaVersion;
        }
        catch
        {
            Recover();
            throw;
        }
    }

    private void AssertUnchanged()
    {
        foreach (var pair in _known)
        {
            var path = Resolve(pair.Key);
            if (!File.Exists(path) || Hash(File.ReadAllBytes(path)) != pair.Value)
                throw new IOException("Dữ liệu đã thay đổi bên ngoài app. Dừng lưu để tránh ghi đè: " + pair.Key);
        }
        if (_known.Count == 0 && File.Exists(FilePath)) throw new IOException("Kho mới được tạo bởi tiến trình khác. Hãy mở lại.");
    }

    public void Recover()
    {
        if (!File.Exists(JournalPath)) return;
        var journal = Decode<SaveJournal>(File.ReadAllBytes(JournalPath));
        // Validate the complete recovery plan before touching any target.
        foreach (var entry in journal.Entries)
        {
            _ = Resolve(entry.File);
            var backup = ResolveBackup(entry.Backup);
            if (entry.Existed && !File.Exists(backup)) throw new InvalidDataException("Thiếu bản sao phục hồi. Giữ nguyên kho để kiểm tra.");
        }
        foreach (var entry in journal.Entries.AsEnumerable().Reverse())
        {
            if (entry.Existed) AtomicWrite(Resolve(entry.File), File.ReadAllBytes(ResolveBackup(entry.Backup)));
            else File.Delete(Resolve(entry.File));
        }
        File.Delete(JournalPath);
    }

    public LegacyImportRecord ImportCopies(SheetState current, LegacyImportPreview preview)
    {
        if (LegacyImport.AlreadyImported(current, preview)) throw new InvalidDataException("File này đã được nhập.");
        var copies = LegacyImport.CreateCopies(current, preview);
        var backup = Path.Combine(Root, "backups", "import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        AtomicWrite(Path.Combine(backup, "before-import.json"), Encode(current));
        AtomicWrite(Path.Combine(backup, "source.json"), preview.SourceBytes);
        var record = new LegacyImportRecord(Path.GetFileName(preview.SourcePath), preview.Sha256, DateTime.UtcNow, copies.Select(n => n.Id).ToList(), backup);
        var next = Clone(current); next.Notes.AddRange(copies); next.ImportHistory.Add(record);
        Save(next);
        current.Notes.AddRange(copies); current.ImportHistory.Add(record);
        return record;
    }

    public string BackupSnapshot(SheetState state)
    {
        var target = Path.Combine(Root, "backups", "transfer-" + Guid.NewGuid().ToString("N") + ".json");
        AtomicWrite(target, Encode(state)); return target;
    }

    private IEnumerable<string> FindDataFiles()
    {
        foreach (var folder in new[] { "projects", "notes" })
        {
            var path = Path.Combine(Root, folder);
            if (!Directory.Exists(path)) continue;
            RejectReparse(path);
            foreach (var file in Directory.EnumerateFiles(path, "*.json", SearchOption.TopDirectoryOnly)) yield return file;
        }
    }

    private string Resolve(string relative)
    {
        if (relative != "workspace.h2index.json" && !(relative.StartsWith("projects/", StringComparison.Ordinal) && relative.EndsWith(".h2project.json", StringComparison.Ordinal))
            && !(relative.StartsWith("notes/", StringComparison.Ordinal) && relative.EndsWith(".h2note.json", StringComparison.Ordinal)))
            throw new InvalidDataException("Đường dẫn tệp kho không hợp lệ.");
        return ResolveSafe(relative);
    }
    private string ResolveBackup(string relative)
    {
        if (!relative.StartsWith("backups/", StringComparison.Ordinal)) throw new InvalidDataException("Đường dẫn backup không hợp lệ.");
        return ResolveSafe(relative);
    }
    private string ResolveSafe(string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':') || relative.Split('/').Any(p => p is ".." or "." or ""))
            throw new InvalidDataException("Đường dẫn vượt phạm vi kho.");
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Đường dẫn vượt phạm vi kho.");
        for (var check = full; check is not null && check.Length >= Root.Length; check = Path.GetDirectoryName(check)) RejectReparse(check);
        return full;
    }
    public static void RejectReparse(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Không dùng liên kết thư mục/tệp làm kho dữ liệu: " + path);
    }
    public static void ValidateDestination(string source, string target)
    {
        var a = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
        var b = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase) || (a + Path.DirectorySeparatorChar).StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || (b + Path.DirectorySeparatorChar).StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Không chuyển vào chính kho đang dùng hoặc thư mục lồng nhau.");
        for (var path = b; path is not null; path = Path.GetDirectoryName(path)) RejectReparse(path);
    }
    public static string SafeName(string name, Guid id)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToHashSet();
        var value = new string(name.Normalize(NormalizationForm.FormC).Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (value.Length > 72) value = value[..72].TrimEnd();
        if (value.Length > 0 && char.IsHighSurrogate(value[^1])) value = value[..^1];
        if (string.IsNullOrWhiteSpace(value)) value = "Du an";
        return value + "--" + id.ToString("N");
    }
    public static void ValidateState(SheetState state)
    {
        if (state.Notes is null || state.Notes.Any(n => n.Projects is null || n.Content is null)) throw new InvalidDataException("Dữ liệu ghi chú không đầy đủ.");
        var ids = new HashSet<Guid>();
        void ValidateConversations(List<AiConversation> conversations)
        {
            if (conversations is null) throw new InvalidDataException("Thiếu lịch sử AI.");
            foreach (var c in conversations)
            {
                if (c.Id == Guid.Empty || !ids.Add(c.Id) || c.Messages is null) throw new InvalidDataException("Hội thoại không hợp lệ.");
                foreach (var m in c.Messages) if (m.Id == Guid.Empty || !ids.Add(m.Id)) throw new InvalidDataException("ID tin nhắn bị trùng.");
                if (c.DraftAttachments is null || c.Messages.Any(m => m.Attachments is null || m.SavedFiles is null)) throw new InvalidDataException("Thiếu danh sách đính kèm.");
                foreach (var a in c.DraftAttachments.Concat(c.Messages.SelectMany(m => m.Attachments)))
                    if (a is null || a.Id == Guid.Empty || !ids.Add(a.Id) || a.Data is null || a.Text is null || a.Name is null || a.MimeType is null || a.Notice is null || a.Data.Length > AiDocuments.MaxFileBytes)
                        throw new InvalidDataException("Tệp đính kèm không hợp lệ hoặc quá lớn.");
            }
        }
        foreach (var n in state.Notes)
        {
            if (n.Id == Guid.Empty || !ids.Add(n.Id)) throw new InvalidDataException("ID ghi chú bị trùng hoặc rỗng.");
            ValidateConversations(n.AiConversations);
            foreach (var p in n.Projects)
            {
                if (p.Id == Guid.Empty || !ids.Add(p.Id) || p.ChecklistItems is null || p.Conversations is null || p.Links is null || p.Layout is null)
                    throw new InvalidDataException("Dữ liệu dự án hoặc ID không hợp lệ.");
                foreach (var t in p.ChecklistItems) if (t.Id == Guid.Empty || !ids.Add(t.Id)) throw new InvalidDataException("ID công việc bị trùng.");
                ValidateConversations(p.Conversations);
            }
        }
    }
    public static T Clone<T>(T value) => Decode<T>(Encode(value));
    internal static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    internal static T Decode<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("File JSON rỗng.");
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static void AtomicWrite(string target, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private sealed class WorkspaceIndex
    {
        public int SchemaVersion { get; set; } = ProjectWorkspaceStore.SchemaVersion;
        public SheetState State { get; set; } = new();
        public List<FileEntry> Files { get; set; } = [];
    }
    private sealed record FileEntry(Guid Id, Guid? BoardId, string Kind, string File, string Hash);
    private sealed class ProjectFile { public int SchemaVersion { get; set; } = ProjectWorkspaceStore.SchemaVersion; public Guid BoardId { get; set; } public ProjectRecord Project { get; set; } = null!; }
    private sealed class GeneralNoteFile { public int SchemaVersion { get; set; } = ProjectWorkspaceStore.SchemaVersion; public NoteRecord Note { get; set; } = null!; }
    private sealed class SaveJournal { public List<JournalEntry> Entries { get; set; } = []; }
    private sealed record JournalEntry(string File, bool Existed, string Backup);
}

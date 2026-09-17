using System.Text.Json;

namespace H2Notes.Core;

public sealed class SheetStorage(string path) : INoteStorage
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string FilePath => path;

    public SheetState LoadOrImport(string? legacyPath = null)
    {
        if (File.Exists(path)) return Read(path);
        if (legacyPath is not null && File.Exists(legacyPath))
        {
            var imported = Read(legacyPath);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            if (!File.Exists(path + ".legacy-backup")) File.Copy(legacyPath, path + ".legacy-backup", overwrite: false);
            Save(imported);
            return imported;
        }
        return new SheetState();
    }

    public static SheetState Read(string source)
    {
        var state = JsonSerializer.Deserialize<SheetState>(File.ReadAllText(source), Options)
            ?? throw new InvalidDataException("Không đọc được dữ liệu ghi chú.");
        if (state.SheetSchemaVersion > 1) throw new InvalidDataException("Dữ liệu thuộc phiên bản mới hơn. Không ghi đè.");
        state.Notes ??= [];
        state.SheetPreferences ??= new();
        state.ImportHistory ??= [];
        foreach (var note in state.Notes)
        {
            note.Projects ??= [];
            foreach (var project in note.Projects) project.ChecklistItems ??= [];
        }
        return state;
    }

    public void Save(SheetState state)
    {
        var json = JsonSerializer.Serialize(state, Options);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write(json); writer.Flush(); stream.Flush(true);
        }
        if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
        else File.Move(temporary, path);
    }

    public LegacyImportRecord ImportCopies(SheetState current, LegacyImportPreview preview)
    {
        if (string.Equals(Path.GetFullPath(path), preview.SourcePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Đây là file dữ liệu đang dùng. Hãy chọn file JSON từ app cũ.");
        if (LegacyImport.AlreadyImported(current, preview))
            throw new InvalidDataException("File này đã được nhập. Không tạo thêm bản trùng.");
        var notes = LegacyImport.CreateCopies(current, preview);
        var backup = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "import-backups",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        new SheetStorage(Path.Combine(backup, "before-import.json")).Save(current);
        using (var source = new FileStream(Path.Combine(backup, "source.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { source.Write(preview.SourceBytes); source.Flush(true); }
        var record = new LegacyImportRecord(Path.GetFileName(preview.SourcePath), preview.Sha256, DateTime.UtcNow, notes.Select(n => n.Id).ToList(), backup);
        var combined = new SheetState
        {
            SheetSchemaVersion = current.SheetSchemaVersion, SheetPreferences = current.SheetPreferences,
            DesktopSession = current.DesktopSession,
            Extra = current.Extra, Notes = [.. current.Notes, .. notes], ImportHistory = [.. current.ImportHistory, record]
        };
        // Publish in memory only after the atomic save succeeds; keep current editor references.
        Save(combined);
        current.Notes.AddRange(notes); current.ImportHistory.Add(record);
        return record;
    }

    public static SheetState Demo()
    {
        var names = new[] { "Đường Gom CT_TA_171", "Đường tỉnh 156 – Lào Cai", "Điện Hạt Nhân I – Ninh Thuận", "Đường gom QL18", "Cầu Vạn – Kinh Môn" };
        var comments = new[] { "Chờ xác nhận khối lượng", "Thi công từ 03/05", "HS gửi chủ đầu tư", "Chờ bản vẽ", "Đối chiếu nghiệm thu" };
        var board = new NoteRecord { Title = "Các dự án", Projects = names.Select((n, i) => new ProjectRecord
        {
            Name = n, Notes = comments[i], IsExpanded = i == 2,
            ChecklistItems = [new() { Text = "Chia lại khối lượng", IsCompleted = true, Comment = "Đã gửi 09/06" },
                new() { Text = "Dựng HSHC", IsCompleted = i == 2, Comment = "Đã xong" },
                new() { Text = "Gửi hồ sơ bổ sung", Comment = "Chờ duyệt" }]
        }).ToList() };
        board.Projects[2].Notes = "Tạm hoàn thành HSHC, chờ chủ đầu tư chốt.\nCập nhật khối lượng khi có xác nhận mới.";
        return new SheetState { Notes = [board] };
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace H2Notes.Core;

public sealed class LegacyImportPreview
{
    internal byte[] SourceBytes { get; }
    internal List<NoteRecord> ConvertedNotes { get; }
    public string SourcePath { get; }
    public string Sha256 { get; }
    public int Boards => ConvertedNotes.Count(n => n.IsBoard);
    public int GeneralNotes => ConvertedNotes.Count(n => !n.IsBoard);
    public int Projects => ConvertedNotes.Sum(n => n.Projects.Count);
    public int Tasks => ConvertedNotes.Sum(n => n.Projects.Sum(p => p.ChecklistItems.Count));
    public int Archived => ConvertedNotes.Count(n => n.IsArchived);

    internal LegacyImportPreview(string path, byte[] bytes, List<NoteRecord> notes)
    {
        SourcePath = path; SourceBytes = bytes; ConvertedNotes = notes;
        Sha256 = Convert.ToHexString(SHA256.HashData(bytes));
    }
}

public static class LegacyImport
{
    public const int MaximumBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static LegacyImportPreview Prepare(string sourcePath)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("File vượt quá giới hạn nhập 32 MB.");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Property(root, "Notes") is not { ValueKind: JsonValueKind.Array } notes)
            throw new InvalidDataException("Không đúng file dữ liệu H2 Notes cũ: cần có danh sách Notes trong JSON.");
        if (Property(root, "SheetSchemaVersion") is { } schema && (schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version > 1))
            throw new InvalidDataException("File thuộc phiên bản không được hỗ trợ.");
        if (notes.GetArrayLength() == 0) throw new InvalidDataException("File không có ghi chú để nhập.");

        List<NoteRecord> converted = [];
        foreach (var element in notes.EnumerateArray())
        {
            RequireRecord(element, "ghi chú", "Title", "Content", "Projects", "ProjectName");
            ValidateChildren(element, "Projects", "dự án", "Name", "Notes", "ChecklistItems");
            foreach (var project in Children(element, "Projects"))
                ValidateChildren(project, "ChecklistItems", "công việc", "Text", "TextRich");
            ValidateChildren(element, "ChecklistItems", "công việc", "Text", "TextRich");
            var note = element.Deserialize<NoteRecord>(Options)!;
            note.Projects ??= [];
            note.Title = string.IsNullOrWhiteSpace(note.Title) ? "Ghi chú đã nhập" : note.Title;
            note.Content ??= "";
            note.NoteKind = Property(element, "NoteKind")?.GetString() ?? "general";
            note.ContentRich = ConvertDocument(note.ContentRich, note.Content);
            if (string.Equals(note.NoteKind, "project", StringComparison.OrdinalIgnoreCase))
            {
                var projectName = Property(element, "ProjectName")?.GetString();
                note.Projects.Insert(0, new ProjectRecord
                {
                    Name = string.IsNullOrWhiteSpace(projectName) ? note.Title : projectName,
                    Notes = note.Content, NotesRich = note.ContentRich.Clone(),
                    ChecklistItems = Children(element, "ChecklistItems").Select(t => t.Deserialize<TaskRecord>(Options)!).ToList()
                });
            }
            if (note.Projects.Count > 0 || string.Equals(note.NoteKind, "project-hub", StringComparison.OrdinalIgnoreCase))
                note.NoteKind = "project-hub";
            else note.NoteKind = "general";

            foreach (var project in note.Projects)
            {
                project.Name ??= ""; project.Notes ??= ""; project.ChecklistItems ??= [];
                project.NameRich = ConvertDocument(project.NameRich, project.Name);
                project.NotesRich = ConvertDocument(project.NotesRich, project.Notes);
                foreach (var task in project.ChecklistItems)
                {
                    task.Text ??= ""; task.Comment ??= "";
                    task.TextRich = ConvertDocument(task.TextRich, task.Text);
                    task.CommentRich = ConvertDocument(task.CommentRich, task.Comment);
                }
            }
            // Import never opens dozens of windows or applies old startup preferences.
            note.IsVisibleOnDesktop = false;
            converted.Add(note);
        }
        return new LegacyImportPreview(fullPath, bytes, converted);
    }

    public static bool AlreadyImported(SheetState current, LegacyImportPreview preview) =>
        current.ImportHistory.Any(h => h.Sha256 == preview.Sha256 && h.NoteIds.Any(id => current.Notes.Any(n => n.Id == id)));

    internal static List<NoteRecord> CreateCopies(SheetState current, LegacyImportPreview preview)
    {
        var copies = JsonSerializer.Deserialize<List<NoteRecord>>(JsonSerializer.Serialize(preview.ConvertedNotes), Options)!;
        var titles = current.Notes.Select(n => n.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var note in copies)
        {
            note.Id = Guid.NewGuid();
            note.SelectedAiConversationId = AiHistory.RenewIds(note.AiConversations, note.SelectedAiConversationId);
            var originalTitle = note.Title; var suffix = 1;
            while (!titles.Add(note.Title)) note.Title = originalTitle + (suffix++ == 1 ? " (nhập từ app cũ)" : $" (nhập từ app cũ {suffix - 1})");
            foreach (var project in note.Projects)
            {
                project.Id = Guid.NewGuid();
                project.SelectedAiConversationId = AiHistory.RenewIds(project.Conversations, project.SelectedAiConversationId);
                foreach (var task in project.ChecklistItems) task.Id = Guid.NewGuid();
            }
        }
        return copies;
    }

    private static RichDocument ConvertDocument(RichDocument? rich, string plain)
    {
        var doc = rich ?? RichDocument.FromLegacy(plain);
        if (doc.Runs is null || doc.Runs.Any(r => r is null || r.Text is null || r.Style is null
            || string.IsNullOrWhiteSpace(r.Style.Font) || !double.IsFinite(r.Style.Size) || r.Style.Size is < 1 or > 512))
            throw new InvalidDataException("Có đoạn chữ chứa định dạng không hợp lệ. Dữ liệu hiện tại chưa bị thay đổi.");
        return doc;
    }
    private static JsonElement? Property(JsonElement element, string name) => element.EnumerateObject()
        .Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Select(p => (JsonElement?)p.Value).FirstOrDefault();
    private static IEnumerable<JsonElement> Children(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];
    private static void RequireRecord(JsonElement element, string label, params string[] knownFields)
    {
        if (element.ValueKind != JsonValueKind.Object || !knownFields.Any(f => Property(element, f) is not null))
            throw new InvalidDataException($"Có {label} không đúng cấu trúc trong file JSON.");
    }
    private static void ValidateChildren(JsonElement element, string field, string label, params string[] knownFields)
    {
        if (Property(element, field) is not { } value || value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{field} phải là một danh sách.");
        foreach (var child in value.EnumerateArray()) RequireRecord(child, label, knownFields);
    }
}

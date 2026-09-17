using System.Text.Json;
using System.Text.Json.Serialization;

namespace H2Notes.Core;

public sealed class SheetState
{
    public int SheetSchemaVersion { get; set; } = 1;
    public SheetPreferences SheetPreferences { get; set; } = new();
    public List<NoteRecord> Notes { get; set; } = [];
    public List<LegacyImportRecord> ImportHistory { get; set; } = [];
    public DesktopSessionState? DesktopSession { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed record LegacyImportRecord(string SourceName, string Sha256, DateTime ImportedAt,
    List<Guid> NoteIds, string BackupDirectory);

public sealed class SheetPreferences
{
    public bool RestoreVisibleNotes { get; set; } = true;
    public bool RunOnSystemStart { get; set; }
    public double InactiveOpacity { get; set; } = 1;
    public bool SnapWindows { get; set; } = true;
    public bool AutoHeightTitle { get; set; } = true;
    public bool AutoHeightProgress { get; set; } = true;
    public bool AutoHeightComment { get; set; }
}

public sealed class NoteRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long Revision { get; set; }
    // Nullable on purpose: legacy data did not record time. Never invent a historical timestamp on load.
    public DateTime? CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string Title { get; set; } = "Các dự án";
    public string NoteKind { get; set; } = "project-hub";
    public string Content { get; set; } = "";
    public RichDocument? ContentRich { get; set; }
    public List<ProjectRecord> Projects { get; set; } = [];
    public List<AiConversation> AiConversations { get; set; } = [];
    public Guid? SelectedAiConversationId { get; set; }
    public bool IsArchived { get; set; }
    public bool IsVisibleOnDesktop { get; set; } = true;
    public bool IsPinned { get; set; }
    public Guid? SelectedProjectId { get; set; }
    public double NoteOpacity { get; set; } = 1;
    public double Left { get; set; } = 120;
    public double Top { get; set; } = 100;
    public double Width { get; set; } = 1100;
    public double Height { get; set; } = 740;
    public double? SheetWidth { get; set; }
    public double? SheetHeight { get; set; }
    public int? SheetLeft { get; set; }
    public int? SheetTop { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    [JsonIgnore] public bool IsBoard => NoteKind == "project-hub" || Projects.Count > 0;
    [JsonIgnore] public bool IsChat => NoteKind == "ai-chat" && !IsBoard;
    public RichDocument ReadContent() => (ContentRich ??= RichDocument.FromLegacy(Content)).Clone();
    public NoteRecord IndexShell()
    {
        var shell = (NoteRecord)MemberwiseClone(); shell.Projects = []; return shell;
    }
}

public sealed class ProjectRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long Revision { get; set; }
    public DateTime? CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string Name { get; set; } = "Dự án mới";
    public string Notes { get; set; } = "";
    public RichDocument? NameRich { get; set; }
    public RichDocument? NotesRich { get; set; }
    public bool IsExpanded { get; set; } = true;
    public List<TaskRecord> ChecklistItems { get; set; } = [];
    public List<AiConversation> Conversations { get; set; } = [];
    public Guid? SelectedAiConversationId { get; set; }
    public List<ProjectLink> Links { get; set; } = [];
    public ProjectLayout Layout { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    [JsonIgnore] public string DisplayName => (NameRich ??= RichDocument.FromLegacy(Name)).Text;
    [JsonIgnore] public string NotesText => (NotesRich ??= RichDocument.FromLegacy(Notes)).Text;
    [JsonIgnore] public TaskRecord? Next => ChecklistItems.FirstOrDefault(t => !t.IsCompleted && !string.IsNullOrWhiteSpace(t.DisplayText));
    [JsonIgnore] public string Progress => $"{ChecklistItems.Count(t => t.IsCompleted)}/{ChecklistItems.Count}";
    public RichDocument ReadName() => (NameRich ??= RichDocument.FromLegacy(Name)).Clone();
    public RichDocument ReadNotes() => (NotesRich ??= RichDocument.FromLegacy(Notes)).Clone();
}

public sealed class TaskRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long Revision { get; set; }
    public DateTime? CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string Text { get; set; } = "Công việc mới";
    public RichDocument? TextRich { get; set; }
    public string Comment { get; set; } = "";
    public RichDocument? CommentRich { get; set; }
    public bool IsCompleted { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    [JsonIgnore] public string DisplayText => (TextRich ??= RichDocument.FromLegacy(Text)).Text;
    public RichDocument ReadText() => (TextRich ??= RichDocument.FromLegacy(Text)).Clone();
    [JsonIgnore] public string CommentText => CommentRich?.Text ?? Comment;
    public RichDocument ReadComment() => (CommentRich ?? RichDocument.Plain(Comment)).Clone();
}

public sealed record SheetRow(ProjectRecord Project, TaskRecord? Task, int Ordinal)
{
    public Guid Id => Task?.Id ?? Project.Id;
    public bool IsProject => Task is null;
    public string Title => Task?.DisplayText ?? Project.DisplayName;
    public string Comment => Task?.CommentText ?? Project.NotesText;
}

public static class SheetOperations
{
    public static List<SheetRow> Rows(NoteRecord board, string filter)
    {
        List<SheetRow> rows = [];
        for (var i = 0; i < board.Projects.Count; i++)
        {
            var p = board.Projects[i];
            var projectMatch = p.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || p.NotesText.Contains(filter, StringComparison.OrdinalIgnoreCase);
            var tasks = p.ChecklistItems.Where(t => projectMatch || t.DisplayText.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || t.CommentText.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!projectMatch && tasks.Count == 0) continue;
            rows.Add(new(p, null, i + 1));
            if (p.IsExpanded || filter.Length > 0) rows.AddRange(tasks.Select(t => new SheetRow(p, t, i + 1)));
        }
        return rows;
    }

    public static bool MoveProject(NoteRecord board, Guid source, Guid target, bool after)
        => Move(board.Projects, source, target, after, p => p.Id);
    public static bool MoveTask(ProjectRecord project, Guid source, Guid target, bool after)
        => Move(project.ChecklistItems, source, target, after, t => t.Id);
    private static bool Move<T>(List<T> list, Guid source, Guid target, bool after, Func<T, Guid> id)
    {
        var from = list.FindIndex(x => id(x) == source);
        var to = list.FindIndex(x => id(x) == target);
        if (from < 0 || to < 0 || source == target) return false;
        var item = list[from];
        list.RemoveAt(from);
        to = list.FindIndex(x => id(x) == target) + (after ? 1 : 0);
        list.Insert(to, item);
        return from != to;
    }
}
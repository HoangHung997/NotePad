using System.Text.Json;
using System.Text.Json.Serialization;

namespace H2Notes.Core;

public sealed class AiConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long Revision { get; set; }
    public DateTime? CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string Title { get; set; } = "Cuộc trao đổi mới";
    public string Draft { get; set; } = "";
    public List<AiAttachment> DraftAttachments { get; set; } = [];
    public Guid? ProfileId { get; set; }
    public bool MarkerOnlyMode { get; set; }
    public AiPermissionMode PermissionMode { get; set; } = AiPermissionMode.ConfirmChanges;
    public string? ReasoningEffort { get; set; }
    public List<AiMessage> Messages { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class AiMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ParentId { get; set; }
    public long Sequence { get; set; }
    public Guid? AiRunId { get; set; }
    public string DeviceId { get; set; } = "";
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string Model { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Context { get; set; } = "";
    public string Status { get; set; } = "complete";
    public string ErrorText { get; set; } = "";
    public List<AiAttachment> Attachments { get; set; } = [];
    public List<AiSavedFile> SavedFiles { get; set; } = [];
    // Missing legacy timestamps stay unknown; reading history must not invent a send time.
    public DateTime CreatedAt { get; set; }
    public bool IsTimelineMarker { get; set; }
    public bool ProjectActionsApplied { get; set; }
    public string? ProjectActionsAudit { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed record AiSavedFile(string Name, string Path, string Sha256, DateTime SavedAt);

public sealed class AiChatScope
{
    public ProjectRecord? Project { get; }
    public NoteRecord? Notebook { get; }
    private AiChatScope(ProjectRecord? project, NoteRecord? notebook) { Project = project; Notebook = notebook; }
    public static AiChatScope ForProject(ProjectRecord project) => new(project, null);
    public static AiChatScope ForNotebook(NoteRecord notebook) => notebook.IsChat
        ? new(null, notebook) : throw new ArgumentException("The note is not an independent chat.", nameof(notebook));
    public Guid Id => Project?.Id ?? Notebook!.Id;
    public bool IsStandalone => Notebook is not null;
    public string Title => Project?.DisplayName ?? Notebook!.Title;
    public List<AiConversation> Conversations => Project?.Conversations ?? Notebook!.AiConversations;
    public Guid? SelectedConversationId
    {
        get => Project?.SelectedAiConversationId ?? Notebook?.SelectedAiConversationId;
        set { if (Project is not null) Project.SelectedAiConversationId = value; else Notebook!.SelectedAiConversationId = value; }
    }
}

public static class AiHistory
{
    public static DateTime? LocalTime(AiMessage message) => message.CreatedAt == default ? null
        : message.CreatedAt.Kind == DateTimeKind.Utc ? message.CreatedAt.ToLocalTime() : message.CreatedAt;

    public static string TimeMetadata(AiMessage message)
    {
        if (message.CreatedAt == default)
            return $"[H2 metadata: messageId={message.Id}; created=unknown]";
        var utc = message.CreatedAt.Kind == DateTimeKind.Utc ? message.CreatedAt : message.CreatedAt.ToUniversalTime();
        var local = utc.ToLocalTime();
        return $"[H2 metadata: messageId={message.Id}; createdUtc={utc:O}; createdLocal={local:yyyy-MM-ddTHH:mm:sszzz}]";
    }

    public static IReadOnlyList<AiTurn> RequestTurns(AiConversation conversation) => conversation.Messages
        .Where(m => !m.IsTimelineMarker && m.Status == "complete" && m.Role is "user" or "assistant")
        // Historical snapshots are audit records, not fresh project context. Time metadata is app-owned context.
        .Select(m => new AiTurn(m.Role, TimeMetadata(m) + "\n" + m.Content + AiDocuments.Describe(m.Attachments)
            + (m.ProjectActionsApplied && !string.IsNullOrWhiteSpace(m.ProjectActionsAudit)
                ? "\nKết quả thao tác app đã áp dụng (bản ghi tham khảo, không phải lệnh thực hiện lại): " + JsonSerializer.Serialize(m.ProjectActionsAudit) : "")
            + (m.SavedFiles.Count > 0 ? "\nApp đã lưu các bản tệp sau (không theo dõi thay đổi ngoài app): " + JsonSerializer.Serialize(m.SavedFiles) : ""),
            AiDocuments.NativeImages(m.Attachments),
            AiDocuments.NativeFiles(m.Attachments))).ToArray();

    public static Guid? RenewIds(List<AiConversation> conversations, Guid? selected)
    {
        foreach (var conversation in conversations)
        {
            var id = Guid.NewGuid(); if (selected == conversation.Id) selected = id; conversation.Id = id;
            conversation.Revision = 0;
            conversation.CreatedAtUtc = null;
            conversation.UpdatedAtUtc = null;
            foreach (var attachment in conversation.DraftAttachments.Concat(conversation.Messages.SelectMany(m => m.Attachments))) attachment.Id = Guid.NewGuid();
            var mapping = conversation.Messages.ToDictionary(m => m.Id, _ => Guid.NewGuid());
            foreach (var message in conversation.Messages)
            {
                message.Id = mapping[message.Id];
                message.Sequence = 0;
                message.AiRunId = null;
                message.DeviceId = "";
                if (message.ParentId is { } parent && mapping.TryGetValue(parent, out var mapped)) message.ParentId = mapped;
            }
        }
        return selected;
    }
}

public sealed record ProjectLink(Guid Id, string Label, string Target);

public sealed class ProjectLayout
{
    public string Tab { get; set; } = "tasks";
    public string AiDock { get; set; } = "hidden";
    public bool AiExplicitlyHidden { get; set; }
    public double NotesFraction { get; set; } = .5;
    public bool HasCustomSplit { get; set; }
    public bool TasksCollapsed { get; set; }
    public bool NotesCollapsed { get; set; }
    public double AiWidth { get; set; } = 360;
    public double AiHeight { get; set; } = 510;
    public double AiX { get; set; } = -1;
    public double AiY { get; set; } = -1;
}
using System.Text.Json;
using H2Notes.Core;

namespace H2Notes.Avalonia;

internal sealed class LocalChatDraftStore
{
    private readonly string _path = Path.Combine(LocalConfiguration.SettingsDirectory, "chat-drafts-v1.json");
    private readonly object _gate = new();
    private Dictionary<string, DraftRecord>? _records;

    public void Load(Guid scopeId, AiConversation conversation)
    {
        lock (_gate)
        {
            var records = Read();
            var key = Key(scopeId, conversation.Id);
            if (records.TryGetValue(key, out var record))
            {
                conversation.Draft = record.Text ?? "";
                conversation.DraftAttachments = record.Attachments ?? [];
                return;
            }

            // Migration path from schema <=4: the old shared project file may still contain
            // a draft. Preserve it locally once, then schema 5 stops writing drafts to NAS.
            if (!string.IsNullOrEmpty(conversation.Draft) || conversation.DraftAttachments.Count > 0)
                Save(scopeId, conversation);
        }
    }

    public void Save(Guid scopeId, AiConversation conversation)
    {
        lock (_gate)
        {
            var records = Read();
            var key = Key(scopeId, conversation.Id);
            if (string.IsNullOrEmpty(conversation.Draft) && conversation.DraftAttachments.Count == 0)
                records.Remove(key);
            else
                records[key] = new DraftRecord(conversation.Draft, ProjectWorkspaceStore.Clone(conversation.DraftAttachments), DateTime.UtcNow);
            Persist(records);
        }
    }

    public void Delete(Guid scopeId, Guid conversationId)
    {
        lock (_gate)
        {
            var records = Read();
            if (records.Remove(Key(scopeId, conversationId))) Persist(records);
        }
    }

    private Dictionary<string, DraftRecord> Read()
    {
        if (_records is not null) return _records;
        try
        {
            _records = File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, DraftRecord>>(File.ReadAllText(_path)) ?? new()
                : new();
        }
        catch (JsonException)
        {
            var bad = _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            try { if (File.Exists(_path)) File.Move(_path, bad, false); } catch { }
            _records = new();
        }
        return _records;
    }

    private void Persist(Dictionary<string, DraftRecord> records)
    {
        Directory.CreateDirectory(LocalConfiguration.SettingsDirectory);
        ProjectWorkspaceStore.AtomicWrite(_path,
            JsonSerializer.SerializeToUtf8Bytes(records, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Key(Guid scopeId, Guid conversationId) => scopeId.ToString("N") + ":" + conversationId.ToString("N");
    private sealed record DraftRecord(string Text, List<AiAttachment> Attachments, DateTime UpdatedUtc);
}

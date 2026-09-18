using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace H2Notes.Core;

public sealed class AiMemoryRecord
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Id { get; set; }
    public string Scope { get; set; } = "workspace";
    public Guid? ProjectId { get; set; }
    public string ProjectName { get; set; } = "";
    public string Kind { get; set; } = "event";
    public string SourceType { get; set; } = "app";
    public Guid SourceId { get; set; }
    public string FactKey { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime? CreatedUtc { get; set; }
    public DateTime? UpdatedUtc { get; set; }
    public bool IsCurrent { get; set; }
    public int Priority { get; set; }
    public string WriterId { get; set; } = "";
}

public sealed record AiMemoryContext(IReadOnlyList<AiMemoryRecord> Records, bool WorkspaceScope, DateTimeOffset? From = null, DateTimeOffset? To = null)
{
    public static readonly AiMemoryContext Empty = new(Array.Empty<AiMemoryRecord>(), false);
}

/// <summary>
/// Durable model-independent memory for H2 Notes. Canonical memory records live inside the
/// shared workspace/NAS; each PC keeps only a disposable local cache used to avoid rescanning
/// the share on every prompt. The cache can always be rebuilt from the shared records.
/// </summary>
public sealed class AiMemoryStore
{
    public const int SchemaVersion = 1;
    private const int MaxRecordText = 16_000;
    private const int NoteChunk = 3_500;
    private readonly string _workspaceRoot;
    private readonly string _root;
    private readonly string _writerId;
    private readonly string _revisionPath;
    private readonly string _lockPath;
    private readonly string _cachePath;

    public AiMemoryStore(string workspaceRoot, string writerId)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar);
        _root = Path.Combine(_workspaceRoot, "memory");
        _writerId = string.IsNullOrWhiteSpace(writerId) ? Environment.MachineName : writerId.Trim();
        _revisionPath = Path.Combine(_root, "revision.json");
        _lockPath = Path.Combine(_root, ".h2-memory.lock");
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "H2Notes", "memory-index");
        Directory.CreateDirectory(local);
        _cachePath = Path.Combine(local, HashText(_workspaceRoot)[..32] + ".json");
    }

    public string Root => _root;

    public void SyncFromState(SheetState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Directory.CreateDirectory(_root);
        using var gate = AcquireLock();
        var desiredCurrent = new HashSet<Guid>();
        var changed = false;
        var now = DateTime.UtcNow;

        foreach (var note in state.Notes)
        {
            if (note.IsBoard)
            {
                foreach (var project in note.Projects)
                {
                    var stamp = Utc(project.UpdatedAtUtc ?? project.CreatedAtUtc) ?? now;
                    var summary = new StringBuilder();
                    summary.Append("Dự án: ").Append(project.DisplayName)
                        .Append("\nTiến độ: ").Append(project.Progress);
                    if (project.Next is { } next) summary.Append("\nViệc tiếp theo: ").Append(next.DisplayText);
                    summary.Append("\nSố công việc: ").Append(project.ChecklistItems.Count)
                        .Append("; hoàn thành: ").Append(project.ChecklistItems.Count(t => t.IsCompleted));
                    changed |= Upsert(Current("project", project.Id, project.Id, project.DisplayName,
                        $"project:{project.Id:N}:summary", summary.ToString(), project.CreatedAtUtc, stamp, 100), desiredCurrent);

                    var notes = project.NotesText ?? "";
                    foreach (var (chunk, index) in Chunk(notes, NoteChunk).Select((value, index) => (value, index)))
                        changed |= Upsert(Current("project-note", StableId($"project-note:{project.Id:N}:{index}"), project.Id,
                            project.DisplayName, $"project:{project.Id:N}:note:{index}", chunk, project.CreatedAtUtc, stamp, 95), desiredCurrent);

                    foreach (var task in project.ChecklistItems)
                    {
                        var text = "Công việc: " + task.DisplayText + "\nTrạng thái: " + (task.IsCompleted ? "Hoàn thành" : "Chưa hoàn thành/đang thực hiện")
                            + (string.IsNullOrWhiteSpace(task.CommentText) ? "" : "\nGhi chú: " + task.CommentText);
                        changed |= Upsert(Current("task", task.Id, project.Id, project.DisplayName,
                            $"task:{task.Id:N}", text, task.CreatedAtUtc, Utc(task.UpdatedAtUtc ?? task.CompletedAtUtc ?? project.UpdatedAtUtc) ?? stamp, 100), desiredCurrent);
                    }

                    changed |= SyncConversationEvents(project.Conversations, project.Id, project.DisplayName);
                }
            }
            else if (note.IsChat)
            {
                changed |= SyncConversationEvents(note.AiConversations, null, note.Title);
            }
            else
            {
                var stamp = Utc(note.UpdatedAtUtc ?? note.CreatedAtUtc) ?? now;
                var text = note.ContentRich?.Text ?? note.Content ?? "";
                foreach (var (chunk, index) in Chunk(text, NoteChunk).Select((value, index) => (value, index)))
                    changed |= Upsert(Current("general-note", StableId($"note:{note.Id:N}:{index}"), null, "",
                        $"note:{note.Id:N}:{index}", $"Ghi chú {note.Title}:\n{chunk}", note.CreatedAtUtc, stamp, 85), desiredCurrent);
            }
        }

        // Current structured records are rebuildable. Remove only stale current records; immutable
        // episodic/message records remain as history.
        foreach (var path in EnumerateRecordFiles())
        {
            AiMemoryRecord? record = null;
            try { record = ReadRecord(path); } catch (InvalidDataException) { }
            if (record is null || !record.IsCurrent || desiredCurrent.Contains(record.Id)) continue;
            File.Delete(path); changed = true;
        }

        if (changed)
        {
            var revision = ReadRevisionUnsafe() + 1;
            ProjectWorkspaceStore.AtomicWrite(_revisionPath, JsonSerializer.SerializeToUtf8Bytes(new MemoryRevision
            {
                SchemaVersion = AiMemoryStore.SchemaVersion,
                Revision = revision,
                UpdatedUtc = now,
                WriterId = _writerId
            }, ProjectWorkspaceStore.Json));
        }
    }

    public AiMemoryContext Query(string query, Guid? currentProjectId, bool workspaceScope, int maxRecords = 20, DateTimeOffset? now = null)
    {
        var records = LoadCachedRecords();
        if (records.Count == 0) return new([], workspaceScope);
        now ??= DateTimeOffset.Now;
        var range = ParseTimeRange(query, now.Value);
        var normalized = Normalize(query);
        var tokens = Tokens(normalized);

        var candidates = records.Where(r => workspaceScope || r.ProjectId is null || r.ProjectId == currentProjectId);
        if (range.From is { } from)
            candidates = candidates.Where(r => Timestamp(r) is { } t && t >= from.UtcDateTime && t < range.To!.Value.UtcDateTime);

        var ranked = candidates.Select(r => new { Record = r, Score = Score(r, normalized, tokens, currentProjectId, range.From is not null, now.Value.UtcDateTime) })
            .Where(x => x.Score > 0 || tokens.Count == 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => Timestamp(x.Record) ?? DateTime.MinValue)
            .Take(Math.Clamp(maxRecords, 1, 50))
            .Select(x => x.Record)
            .ToArray();
        return new(ranked, workspaceScope, range.From, range.To);
    }

    private bool SyncConversationEvents(IEnumerable<AiConversation> conversations, Guid? projectId, string projectName)
    {
        var changed = false;
        foreach (var conversation in conversations)
        foreach (var message in conversation.Messages)
        {
            if (message.Id == Guid.Empty || message.Status is not ("complete" or "interrupted") || message.IsTimelineMarker && message.Role != "user") continue;
            // User statements are authoritative memory. AI prose becomes memory only when H2 Notes
            // actually applied its project action; ordinary model speculation is not promoted to fact.
            if (message.Role != "user" && !message.ProjectActionsApplied) continue;
            var text = new StringBuilder(message.Content ?? "");
            foreach (var attachment in message.Attachments.Where(a => !string.IsNullOrWhiteSpace(a.Text)))
                text.Append("\nTệp ").Append(attachment.Name).Append(":\n").Append(Trim(attachment.Text, 12_000));
            if (message.ProjectActionsApplied && !string.IsNullOrWhiteSpace(message.ProjectActionsAudit))
                text.Append("\nThao tác H2 Notes đã áp dụng: ").Append(message.ProjectActionsAudit);
            if (text.Length == 0) continue;
            var record = new AiMemoryRecord
            {
                Id = message.Id,
                Scope = projectId is null ? "workspace" : "project",
                ProjectId = projectId,
                ProjectName = projectName,
                Kind = message.Role == "user" ? "message" : "applied-action",
                SourceType = message.Role == "user" ? "user" : "app-applied-ai",
                SourceId = message.Id,
                FactKey = "message:" + message.Id.ToString("N"),
                Text = Trim(text.ToString(), MaxRecordText),
                CreatedUtc = message.CreatedAt == default ? null : Utc(message.CreatedAt),
                UpdatedUtc = message.CreatedAt == default ? null : Utc(message.CreatedAt),
                IsCurrent = false,
                Priority = message.Role == "user" ? 90 : 80,
                WriterId = string.IsNullOrWhiteSpace(message.DeviceId) ? _writerId : message.DeviceId
            };
            changed |= Upsert(record, null);
        }
        return changed;
    }

    private AiMemoryRecord Current(string kind, Guid id, Guid? projectId, string projectName, string factKey, string text, DateTime? created, DateTime updated, int priority)
        => new()
        {
            Id = id,
            Scope = projectId is null ? "workspace" : "project",
            ProjectId = projectId,
            ProjectName = projectName,
            Kind = kind,
            SourceType = "structured-state",
            SourceId = id,
            FactKey = factKey,
            Text = Trim(text, MaxRecordText),
            CreatedUtc = Utc(created),
            UpdatedUtc = Utc(updated),
            IsCurrent = true,
            Priority = priority,
            // Structured truth is identical on every device after the workspace merge. Do not rewrite
            // a record merely because another PC generated the same index entry.
            WriterId = "structured-state"
        };

    private bool Upsert(AiMemoryRecord record, HashSet<Guid>? desiredCurrent)
    {
        desiredCurrent?.Add(record.Id);
        if (string.IsNullOrWhiteSpace(record.Text)) return false;
        var path = RecordPath(record);
        if (File.Exists(path))
        {
            var existing = ReadRecord(path);
            var oldStamp = Timestamp(existing) ?? DateTime.MinValue;
            var newStamp = Timestamp(record) ?? DateTime.MinValue;
            if (oldStamp > newStamp) return false;
            if (Equivalent(existing, record)) return false;
            ProjectWorkspaceStore.AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(record, ProjectWorkspaceStore.Json));
            return true;
        }
        ProjectWorkspaceStore.AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(record, ProjectWorkspaceStore.Json));
        return true;
    }

    private static bool Equivalent(AiMemoryRecord a, AiMemoryRecord b) => a.SchemaVersion == b.SchemaVersion && a.Id == b.Id
        && a.Scope == b.Scope && a.ProjectId == b.ProjectId && a.ProjectName == b.ProjectName && a.Kind == b.Kind
        && a.SourceType == b.SourceType && a.SourceId == b.SourceId && a.FactKey == b.FactKey && a.Text == b.Text
        && Utc(a.CreatedUtc) == Utc(b.CreatedUtc) && Utc(a.UpdatedUtc) == Utc(b.UpdatedUtc)
        && a.IsCurrent == b.IsCurrent && a.Priority == b.Priority;

    private IReadOnlyList<AiMemoryRecord> LoadCachedRecords()
    {
        Directory.CreateDirectory(_root);
        var revision = ReadRevision();
        try
        {
            if (File.Exists(_cachePath))
            {
                var cache = JsonSerializer.Deserialize<LocalCache>(File.ReadAllBytes(_cachePath), ProjectWorkspaceStore.Json);
                if (cache?.SchemaVersion == AiMemoryStore.SchemaVersion && cache.Revision == revision && cache.Records is not null) return cache.Records;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }

        var records = new List<AiMemoryRecord>();
        foreach (var path in EnumerateRecordFiles()) records.Add(ReadRecord(path));
        try
        {
            ProjectWorkspaceStore.AtomicWrite(_cachePath, JsonSerializer.SerializeToUtf8Bytes(new LocalCache
            {
                SchemaVersion = AiMemoryStore.SchemaVersion,
                Revision = revision,
                Records = records
            }, ProjectWorkspaceStore.Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return records;
    }

    private IEnumerable<string> EnumerateRecordFiles()
    {
        if (!Directory.Exists(_root)) yield break;
        ProjectWorkspaceStore.RejectReparse(_root);
        foreach (var file in Directory.EnumerateFiles(_root, "*.h2memory.json", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(_root, file);
            if (relative.StartsWith("..") || Path.IsPathRooted(relative)) continue;
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(p => p is ".." or "." or "")) continue;
            ProjectWorkspaceStore.RejectReparse(file);
            yield return file;
        }
    }

    private AiMemoryRecord ReadRecord(string path)
    {
        if (new FileInfo(path).Length > 128 * 1024) throw new InvalidDataException("Bản ghi AI memory vượt giới hạn.");
        var record = JsonSerializer.Deserialize<AiMemoryRecord>(File.ReadAllBytes(path), ProjectWorkspaceStore.Json)
            ?? throw new InvalidDataException("Bản ghi AI memory rỗng.");
        if (record.SchemaVersion != AiMemoryStore.SchemaVersion || record.Id == Guid.Empty || record.SourceId == Guid.Empty || record.Text.Length > MaxRecordText
            || record.Scope is not ("workspace" or "project")) throw new InvalidDataException("Bản ghi AI memory không hợp lệ.");
        return record;
    }

    private string RecordPath(AiMemoryRecord record)
    {
        var folder = record.ProjectId is { } project
            ? Path.Combine(_root, "projects", project.ToString("N"))
            : Path.Combine(_root, "workspace");
        Directory.CreateDirectory(folder);
        ProjectWorkspaceStore.RejectReparse(folder);
        return Path.Combine(folder, record.Id.ToString("N") + ".h2memory.json");
    }

    private FileStream AcquireLock()
    {
        Directory.CreateDirectory(_root);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        IOException? last = null;
        do
        {
            try { return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) { last = ex; Thread.Sleep(60); }
        } while (DateTime.UtcNow < deadline);
        throw new IOException("Bộ nhớ AI trên NAS đang được thiết bị khác cập nhật. H2 Notes sẽ thử lại ở lần lưu sau.", last);
    }

    private long ReadRevision()
    {
        using var gate = AcquireLock();
        return ReadRevisionUnsafe();
    }

    private long ReadRevisionUnsafe()
    {
        if (!File.Exists(_revisionPath)) return 0;
        try
        {
            var revision = JsonSerializer.Deserialize<MemoryRevision>(File.ReadAllBytes(_revisionPath), ProjectWorkspaceStore.Json);
            return revision?.SchemaVersion == AiMemoryStore.SchemaVersion ? revision.Revision : 0;
        }
        catch (JsonException) { return 0; }
    }

    private static (DateTimeOffset? From, DateTimeOffset? To) ParseTimeRange(string query, DateTimeOffset now)
    {
        var q = Normalize(query);
        var yesterday = q.Contains("hom qua", StringComparison.Ordinal);
        var hasDay = q.Contains("hom nay", StringComparison.Ordinal) || yesterday;
        var morning = q.Contains("sang nay", StringComparison.Ordinal) || q.Contains("sang hom qua", StringComparison.Ordinal);
        var afternoon = q.Contains("chieu nay", StringComparison.Ordinal) || q.Contains("chieu hom qua", StringComparison.Ordinal);
        var evening = q.Contains("toi nay", StringComparison.Ordinal) || q.Contains("toi hom qua", StringComparison.Ordinal);
        var night = q.Contains("dem nay", StringComparison.Ordinal) || q.Contains("dem hom qua", StringComparison.Ordinal);
        if (morning || afternoon || evening || night) hasDay = true;
        if (!hasDay) return (null, null);
        var day = yesterday ? now.AddDays(-1).Date : now.Date;
        TimeSpan from = TimeSpan.Zero, to = TimeSpan.FromDays(1);
        if (morning) { from = TimeSpan.FromHours(5); to = TimeSpan.FromHours(12); }
        else if (afternoon) { from = TimeSpan.FromHours(12); to = TimeSpan.FromHours(18); }
        else if (evening) { from = TimeSpan.FromHours(18); to = TimeSpan.FromHours(23); }
        else if (night) { from = TimeSpan.FromHours(23); to = TimeSpan.FromDays(1); }
        return (new DateTimeOffset(day + from, now.Offset), new DateTimeOffset(day + to, now.Offset));
    }

    private static int Score(AiMemoryRecord record, string normalizedQuery, HashSet<string> queryTokens, Guid? currentProjectId, bool timeFiltered, DateTime nowUtc)
    {
        var text = Normalize(record.ProjectName + " " + record.Text + " " + record.Kind);
        var score = record.Priority / 10;
        if (record.ProjectId == currentProjectId) score += 12;
        if (record.IsCurrent) score += 8;
        if (normalizedQuery.Length >= 4 && text.Contains(normalizedQuery, StringComparison.Ordinal)) score += 30;
        foreach (var token in queryTokens) if (text.Contains(token, StringComparison.Ordinal)) score += token.Length >= 5 ? 5 : 3;
        if (timeFiltered) score += 12;
        if (Timestamp(record) is { } stamp)
        {
            var days = Math.Max(0, (nowUtc - stamp).TotalDays);
            score += days < 1 ? 8 : days < 7 ? 4 : days < 31 ? 2 : 0;
        }
        return score;
    }

    private static HashSet<string> Tokens(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(t => t.Length >= 2 && t is not ("la" or "va" or "cho" or "toi" or "cua" or "nhung" or "cac" or "mot" or "nay" or "do"))
        .ToHashSet(StringComparer.Ordinal);

    private static string Normalize(string text)
    {
        var decomposed = (text ?? "").Normalize(NormalizationForm.FormD).ToLowerInvariant();
        var output = new StringBuilder(decomposed.Length);
        var space = false;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            var c = ch == 'đ' ? 'd' : ch;
            if (char.IsLetterOrDigit(c)) { output.Append(c); space = false; }
            else if (!space) { output.Append(' '); space = true; }
        }
        return output.ToString().Trim();
    }

    private static IEnumerable<string> Chunk(string text, int size)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        for (var offset = 0; offset < text.Length;)
        {
            var take = Math.Min(size, text.Length - offset);
            if (offset + take < text.Length)
            {
                var newline = text.LastIndexOf('\n', offset + take - 1, take);
                if (newline > offset + size / 2) take = newline - offset + 1;
            }
            var chunk = text.Substring(offset, take).Trim();
            if (chunk.Length > 0) yield return chunk;
            offset += take;
        }
    }

    private static string Trim(string? text, int max) => string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max] + "\n[… phần còn lại vẫn ở nguồn gốc H2 Notes …]";
    private static DateTime? Timestamp(AiMemoryRecord r) => Utc(r.UpdatedUtc ?? r.CreatedUtc);
    private static DateTime? Utc(DateTime? value) => value is null ? null : value.Value.Kind == DateTimeKind.Utc ? value.Value : value.Value.ToUniversalTime();
    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    private static Guid StableId(string key) { var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key)); return new Guid(hash.AsSpan(0, 16)); }
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class MemoryRevision
    {
        public int SchemaVersion { get; set; } = AiMemoryStore.SchemaVersion;
        public long Revision { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public string WriterId { get; set; } = "";
    }

    private sealed class LocalCache
    {
        public int SchemaVersion { get; set; } = AiMemoryStore.SchemaVersion;
        public long Revision { get; set; }
        public List<AiMemoryRecord> Records { get; set; } = [];
    }
}

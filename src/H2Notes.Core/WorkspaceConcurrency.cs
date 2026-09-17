using System.Text.Json;

namespace H2Notes.Core;

public sealed record WorkspaceMergeConflict(
    DateTime CreatedAtUtc,
    string Path,
    string Resolution,
    string BaseJson,
    string LocalJson,
    string RemoteJson);

internal static class WorkspaceConcurrency
{
    public static List<WorkspaceMergeConflict> MergeIntoLocal(
        SheetState baseline,
        SheetState local,
        SheetState remote,
        IReadOnlySet<Guid>? dirtyProjects,
        string writerId)
    {
        var conflicts = new List<WorkspaceMergeConflict>();

        local.SheetPreferences = MergeValue(baseline.SheetPreferences, local.SheetPreferences, remote.SheetPreferences,
            "workspace.preferences", conflicts);
        local.ImportHistory = MergeImportHistory(baseline.ImportHistory, local.ImportHistory, remote.ImportHistory);
        local.Extra = MergeValue(baseline.Extra, local.Extra, remote.Extra, "workspace.extra", conflicts);

        MergeNotes(baseline, local, remote, dirtyProjects, writerId, conflicts);
        return conflicts;
    }

    private static void MergeNotes(
        SheetState baseline,
        SheetState local,
        SheetState remote,
        IReadOnlySet<Guid>? dirtyProjects,
        string writerId,
        List<WorkspaceMergeConflict> conflicts)
    {
        var baseById = baseline.Notes.ToDictionary(n => n.Id);
        var remoteById = remote.Notes.ToDictionary(n => n.Id);
        var localById = local.Notes.ToDictionary(n => n.Id);

        foreach (var remoteNote in remote.Notes)
        {
            if (localById.ContainsKey(remoteNote.Id)) continue;
            if (baseById.TryGetValue(remoteNote.Id, out var baseNote))
            {
                // A locally removed note stays removed if the remote copy was untouched.
                if (Same(baseNote, remoteNote)) continue;
                conflicts.Add(Conflict($"note/{remoteNote.Id}", baseNote, null, remoteNote,
                    "Remote changed after local deletion; kept remote copy."));
            }
            var clone = ProjectWorkspaceStore.Clone(remoteNote);
            local.Notes.Add(clone); localById[clone.Id] = clone;
        }

        foreach (var localNote in local.Notes.ToArray())
        {
            baseById.TryGetValue(localNote.Id, out var baseNote);
            if (!remoteById.TryGetValue(localNote.Id, out var remoteNote))
            {
                if (baseNote is null) continue; // locally created
                if (Same(localNote, baseNote)) local.Notes.Remove(localNote); // remote deletion wins if local was unchanged
                else conflicts.Add(Conflict($"note/{localNote.Id}", baseNote, localNote, null,
                    "Remote deleted while local changed; kept local copy."));
                continue;
            }

            MergeNote(baseNote, localNote, remoteNote, dirtyProjects, writerId, conflicts);
        }

        Reorder(local.Notes, baseline.Notes, remote.Notes, n => n.Id);
    }

    private static void MergeNote(
        NoteRecord? baseline,
        NoteRecord local,
        NoteRecord remote,
        IReadOnlySet<Guid>? dirtyProjects,
        string writerId,
        List<WorkspaceMergeConflict> conflicts)
    {
        var path = $"note/{local.Id}";
        local.CreatedAtUtc = Earliest(baseline?.CreatedAtUtc, local.CreatedAtUtc, remote.CreatedAtUtc);
        local.UpdatedAtUtc = Latest(baseline?.UpdatedAtUtc, local.UpdatedAtUtc, remote.UpdatedAtUtc);
        if (baseline is null)
        {
            if (local.IsBoard && remote.IsBoard) MergeProjects(null, local, remote, dirtyProjects, writerId, conflicts);
            MergeConversations([], local.AiConversations, remote.AiConversations, path + "/chat", writerId, conflicts);
            local.Revision = Math.Max(local.Revision, remote.Revision) + 1;
            return;
        }

        local.Title = MergeValue(baseline.Title, local.Title, remote.Title, path + "/title", conflicts);
        local.NoteKind = MergeValue(baseline.NoteKind, local.NoteKind, remote.NoteKind, path + "/kind", conflicts);
        local.Content = MergeValue(baseline.Content, local.Content, remote.Content, path + "/content", conflicts);
        local.ContentRich = MergeValue(baseline.ContentRich, local.ContentRich, remote.ContentRich, path + "/content-rich", conflicts);
        local.IsArchived = MergeValue(baseline.IsArchived, local.IsArchived, remote.IsArchived, path + "/archived", conflicts);
        local.Extra = MergeValue(baseline.Extra, local.Extra, remote.Extra, path + "/extra", conflicts);

        // Window placement, visibility, pin state and current selection are device-local UX state.
        MergeConversations(baseline.AiConversations, local.AiConversations, remote.AiConversations, path + "/chat", writerId, conflicts);
        if (local.IsBoard && remote.IsBoard) MergeProjects(baseline, local, remote, dirtyProjects, writerId, conflicts);
        local.Revision = Math.Max(local.Revision, remote.Revision) + 1;
    }

    private static void MergeProjects(
        NoteRecord? baselineBoard,
        NoteRecord localBoard,
        NoteRecord remoteBoard,
        IReadOnlySet<Guid>? dirtyProjects,
        string writerId,
        List<WorkspaceMergeConflict> conflicts)
    {
        var baseProjects = baselineBoard?.Projects ?? [];
        var baseById = baseProjects.ToDictionary(p => p.Id);
        var remoteById = remoteBoard.Projects.ToDictionary(p => p.Id);
        var localById = localBoard.Projects.ToDictionary(p => p.Id);

        foreach (var remoteProject in remoteBoard.Projects)
        {
            if (localById.ContainsKey(remoteProject.Id)) continue;
            if (baseById.TryGetValue(remoteProject.Id, out var baseProject))
            {
                if (Same(baseProject, remoteProject)) continue; // local deletion of untouched remote project
                conflicts.Add(Conflict($"project/{remoteProject.Id}", baseProject, null, remoteProject,
                    "Remote changed after local deletion; kept remote copy."));
            }
            var clone = ProjectWorkspaceStore.Clone(remoteProject);
            localBoard.Projects.Add(clone); localById[clone.Id] = clone;
        }

        foreach (var localProject in localBoard.Projects.ToArray())
        {
            baseById.TryGetValue(localProject.Id, out var baseProject);
            if (!remoteById.TryGetValue(localProject.Id, out var remoteProject))
            {
                if (baseProject is null) continue;
                if (SameProjectShared(localProject, baseProject)) localBoard.Projects.Remove(localProject);
                else conflicts.Add(Conflict($"project/{localProject.Id}", baseProject, localProject, null,
                    "Remote deleted while local changed; kept local copy."));
                continue;
            }

            var dirty = dirtyProjects is null || dirtyProjects.Contains(localProject.Id)
                || baseProject is null || !SameProjectShared(localProject, baseProject);
            MergeProject(baseProject, localProject, remoteProject, dirty, writerId, conflicts);
        }

        Reorder(localBoard.Projects, baseProjects, remoteBoard.Projects, p => p.Id);
    }

    private static void MergeProject(
        ProjectRecord? baseline,
        ProjectRecord local,
        ProjectRecord remote,
        bool locallyDirty,
        string writerId,
        List<WorkspaceMergeConflict> conflicts)
    {
        var path = $"project/{local.Id}";
        if (!locallyDirty && baseline is not null)
        {
            CopyProjectShared(remote, local);
            return;
        }

        local.CreatedAtUtc = Earliest(baseline?.CreatedAtUtc, local.CreatedAtUtc, remote.CreatedAtUtc);
        local.UpdatedAtUtc = Latest(baseline?.UpdatedAtUtc, local.UpdatedAtUtc, remote.UpdatedAtUtc);
        if (baseline is not null)
        {
            local.Name = MergeValue(baseline.Name, local.Name, remote.Name, path + "/name", conflicts);
            local.NameRich = MergeValue(baseline.NameRich, local.NameRich, remote.NameRich, path + "/name-rich", conflicts);
            local.Notes = MergeValue(baseline.Notes, local.Notes, remote.Notes, path + "/notes", conflicts);
            local.NotesRich = MergeValue(baseline.NotesRich, local.NotesRich, remote.NotesRich, path + "/notes-rich", conflicts);
            local.Links = MergeLinks(baseline.Links, local.Links, remote.Links, path + "/links", conflicts);
            local.Extra = MergeValue(baseline.Extra, local.Extra, remote.Extra, path + "/extra", conflicts);
        }

        MergeTasks(baseline?.ChecklistItems ?? [], local.ChecklistItems, remote.ChecklistItems, path + "/tasks", conflicts);
        MergeConversations(baseline?.Conversations ?? [], local.Conversations, remote.Conversations, path + "/chat", writerId, conflicts);
        local.Revision = Math.Max(Math.Max(local.Revision, remote.Revision), baseline?.Revision ?? 0) + 1;
    }

    private static void CopyProjectShared(ProjectRecord source, ProjectRecord target)
    {
        target.CreatedAtUtc = source.CreatedAtUtc;
        target.UpdatedAtUtc = source.UpdatedAtUtc;
        target.Name = source.Name;
        target.NameRich = ProjectWorkspaceStore.Clone(source.NameRich);
        target.Notes = source.Notes;
        target.NotesRich = ProjectWorkspaceStore.Clone(source.NotesRich);
        target.Links = ProjectWorkspaceStore.Clone(source.Links);
        target.ChecklistItems = ProjectWorkspaceStore.Clone(source.ChecklistItems);
        target.Conversations = ProjectWorkspaceStore.Clone(source.Conversations);
        target.Extra = ProjectWorkspaceStore.Clone(source.Extra);
        target.Revision = source.Revision;
        // Keep IsExpanded, SelectedAiConversationId and Layout local to this device.
    }

    private static bool SameProjectShared(ProjectRecord a, ProjectRecord b)
        => Same(a.CreatedAtUtc, b.CreatedAtUtc) && Same(a.UpdatedAtUtc, b.UpdatedAtUtc)
           && Same(a.Name, b.Name) && Same(a.NameRich, b.NameRich)
           && Same(a.Notes, b.Notes) && Same(a.NotesRich, b.NotesRich)
           && Same(a.ChecklistItems, b.ChecklistItems) && Same(a.Conversations, b.Conversations)
           && Same(a.Links, b.Links) && Same(a.Extra, b.Extra);

    private static void MergeTasks(
        List<TaskRecord> baseline,
        List<TaskRecord> local,
        List<TaskRecord> remote,
        string path,
        List<WorkspaceMergeConflict> conflicts)
    {
        var baseById = baseline.ToDictionary(t => t.Id);
        var remoteById = remote.ToDictionary(t => t.Id);
        var localById = local.ToDictionary(t => t.Id);

        foreach (var remoteTask in remote)
        {
            if (localById.ContainsKey(remoteTask.Id)) continue;
            if (baseById.TryGetValue(remoteTask.Id, out var baseTask))
            {
                if (Same(baseTask, remoteTask)) continue;
                conflicts.Add(Conflict($"{path}/{remoteTask.Id}", baseTask, null, remoteTask,
                    "Remote changed after local deletion; kept remote task."));
            }
            var clone = ProjectWorkspaceStore.Clone(remoteTask); local.Add(clone); localById[clone.Id] = clone;
        }

        foreach (var localTask in local.ToArray())
        {
            baseById.TryGetValue(localTask.Id, out var baseTask);
            if (!remoteById.TryGetValue(localTask.Id, out var remoteTask))
            {
                if (baseTask is null) continue;
                if (Same(localTask, baseTask)) local.Remove(localTask);
                else conflicts.Add(Conflict($"{path}/{localTask.Id}", baseTask, localTask, null,
                    "Remote deleted while local changed; kept local task."));
                continue;
            }
            MergeTask(baseTask, localTask, remoteTask, $"{path}/{localTask.Id}", conflicts);
        }
        Reorder(local, baseline, remote, t => t.Id);
    }

    private static void MergeTask(TaskRecord? baseline, TaskRecord local, TaskRecord remote, string path, List<WorkspaceMergeConflict> conflicts)
    {
        local.CreatedAtUtc = Earliest(baseline?.CreatedAtUtc, local.CreatedAtUtc, remote.CreatedAtUtc);
        local.UpdatedAtUtc = Latest(baseline?.UpdatedAtUtc, local.UpdatedAtUtc, remote.UpdatedAtUtc);
        if (baseline is not null)
        {
            local.Text = MergeValue(baseline.Text, local.Text, remote.Text, path + "/text", conflicts);
            local.TextRich = MergeValue(baseline.TextRich, local.TextRich, remote.TextRich, path + "/text-rich", conflicts);
            local.Comment = MergeValue(baseline.Comment, local.Comment, remote.Comment, path + "/comment", conflicts);
            local.CommentRich = MergeValue(baseline.CommentRich, local.CommentRich, remote.CommentRich, path + "/comment-rich", conflicts);
            local.IsCompleted = MergeValue(baseline.IsCompleted, local.IsCompleted, remote.IsCompleted, path + "/completed", conflicts);
            local.Extra = MergeValue(baseline.Extra, local.Extra, remote.Extra, path + "/extra", conflicts);
            local.CompletedAtUtc = local.IsCompleted
                ? MergeValue(baseline.CompletedAtUtc, local.CompletedAtUtc, remote.CompletedAtUtc, path + "/completed-at", conflicts)
                : null;
        }
        else
        {
            local.CompletedAtUtc = local.IsCompleted ? Earliest(local.CompletedAtUtc, remote.CompletedAtUtc) : null;
        }
        local.Revision = Math.Max(Math.Max(local.Revision, remote.Revision), baseline?.Revision ?? 0) + 1;
    }

    private static List<ProjectLink> MergeLinks(List<ProjectLink> baseline, List<ProjectLink> local, List<ProjectLink> remote,
        string path, List<WorkspaceMergeConflict> conflicts)
    {
        if (Same(local, baseline)) return ProjectWorkspaceStore.Clone(remote);
        if (Same(remote, baseline) || Same(local, remote)) return local;
        conflicts.Add(Conflict(path, baseline, local, remote, "Both devices changed links; kept local list and preserved remote in conflict audit."));
        var result = local.ToDictionary(x => x.Id);
        foreach (var item in remote) result.TryAdd(item.Id, item);
        return result.Values.ToList();
    }

    private static void MergeConversations(
        List<AiConversation> baseline,
        List<AiConversation> local,
        List<AiConversation> remote,
        string path,
        string writerId,
        List<WorkspaceMergeConflict> conflicts)
    {
        var baseById = baseline.ToDictionary(c => c.Id);
        var remoteById = remote.ToDictionary(c => c.Id);
        var localById = local.ToDictionary(c => c.Id);

        foreach (var remoteConversation in remote)
        {
            if (localById.ContainsKey(remoteConversation.Id)) continue;
            if (baseById.TryGetValue(remoteConversation.Id, out var baseConversation))
            {
                if (SameConversationShared(baseConversation, remoteConversation)) continue;
                conflicts.Add(Conflict($"{path}/{remoteConversation.Id}", baseConversation, null, remoteConversation,
                    "Remote changed after local deletion; kept remote conversation."));
            }
            var clone = ProjectWorkspaceStore.Clone(remoteConversation);
            clone.Draft = ""; clone.DraftAttachments.Clear();
            local.Add(clone); localById[clone.Id] = clone;
        }

        foreach (var conversation in local.ToArray())
        {
            baseById.TryGetValue(conversation.Id, out var baseConversation);
            if (!remoteById.TryGetValue(conversation.Id, out var remoteConversation))
            {
                if (baseConversation is null) { AssignSequences(conversation, writerId); continue; }
                if (SameConversationShared(conversation, baseConversation)) local.Remove(conversation);
                else conflicts.Add(Conflict($"{path}/{conversation.Id}", baseConversation, conversation, null,
                    "Remote deleted while local changed; kept local conversation."));
                continue;
            }
            MergeConversation(baseConversation, conversation, remoteConversation, $"{path}/{conversation.Id}", writerId, conflicts);
        }
        Reorder(local, baseline, remote, c => c.Id);
    }

    private static bool SameConversationShared(AiConversation a, AiConversation b)
        => Same(a.CreatedAtUtc, b.CreatedAtUtc) && Same(a.UpdatedAtUtc, b.UpdatedAtUtc)
           && Same(a.Title, b.Title) && Same(a.Messages, b.Messages);

    private static void MergeConversation(
        AiConversation? baseline,
        AiConversation local,
        AiConversation remote,
        string path,
        string writerId,
        List<WorkspaceMergeConflict> conflicts)
    {
        local.CreatedAtUtc = Earliest(baseline?.CreatedAtUtc, local.CreatedAtUtc, remote.CreatedAtUtc);
        local.UpdatedAtUtc = Latest(baseline?.UpdatedAtUtc, local.UpdatedAtUtc, remote.UpdatedAtUtc);
        if (baseline is not null)
            local.Title = MergeValue(baseline.Title, local.Title, remote.Title, path + "/title", conflicts);

        // Draft text, draft attachments, selected profile, permissions and reasoning options are intentionally device-local.
        MergeMessages(baseline?.Messages ?? [], local.Messages, remote.Messages, path + "/messages", writerId, conflicts);
        local.Revision = Math.Max(Math.Max(local.Revision, remote.Revision), baseline?.Revision ?? 0) + 1;
    }

    private static void MergeMessages(
        List<AiMessage> baseline,
        List<AiMessage> local,
        List<AiMessage> remote,
        string path,
        string writerId,
        List<WorkspaceMergeConflict> conflicts)
    {
        var baseById = baseline.ToDictionary(m => m.Id);
        var remoteById = remote.ToDictionary(m => m.Id);
        var localById = local.ToDictionary(m => m.Id);

        foreach (var remoteMessage in remote)
        {
            if (localById.ContainsKey(remoteMessage.Id)) continue;
            var clone = ProjectWorkspaceStore.Clone(remoteMessage); local.Add(clone); localById[clone.Id] = clone;
        }

        foreach (var message in local)
        {
            if (!remoteById.TryGetValue(message.Id, out var remoteMessage)) continue;
            baseById.TryGetValue(message.Id, out var baseMessage);
            MergeMessage(baseMessage, message, remoteMessage, $"{path}/{message.Id}", conflicts);
        }

        AssignSequences(local, writerId);
        local.Sort((a, b) =>
        {
            var seq = a.Sequence.CompareTo(b.Sequence); if (seq != 0) return seq;
            var time = a.CreatedAt.CompareTo(b.CreatedAt); if (time != 0) return time;
            return a.Id.CompareTo(b.Id);
        });
    }

    private static void MergeMessage(AiMessage? baseline, AiMessage local, AiMessage remote, string path, List<WorkspaceMergeConflict> conflicts)
    {
        if (local.CreatedAt == default) local.CreatedAt = remote.CreatedAt;
        if (baseline is null)
        {
            if (local.Status == "streaming" && remote.Status != "streaming") CopyMessageMutable(remote, local);
            return;
        }
        local.ParentId = MergeValue(baseline.ParentId, local.ParentId, remote.ParentId, path + "/parent", conflicts);
        local.Role = MergeValue(baseline.Role, local.Role, remote.Role, path + "/role", conflicts);
        local.Content = MergeMessageText(baseline.Content, local.Content, remote.Content, path + "/content", conflicts);
        local.Model = MergeValue(baseline.Model, local.Model, remote.Model, path + "/model", conflicts);
        local.Provider = MergeValue(baseline.Provider, local.Provider, remote.Provider, path + "/provider", conflicts);
        local.Context = MergeValue(baseline.Context, local.Context, remote.Context, path + "/context", conflicts);
        local.Status = MergeMessageStatus(baseline.Status, local.Status, remote.Status);
        local.ErrorText = MergeValue(baseline.ErrorText, local.ErrorText, remote.ErrorText, path + "/error", conflicts);
        local.Attachments = MergeValue(baseline.Attachments, local.Attachments, remote.Attachments, path + "/attachments", conflicts);
        local.SavedFiles = MergeSavedFiles(local.SavedFiles, remote.SavedFiles);
        local.IsTimelineMarker = MergeValue(baseline.IsTimelineMarker, local.IsTimelineMarker, remote.IsTimelineMarker, path + "/marker", conflicts);
        local.ProjectActionsApplied = MergeValue(baseline.ProjectActionsApplied, local.ProjectActionsApplied, remote.ProjectActionsApplied, path + "/actions-applied", conflicts);
        local.ProjectActionsAudit = MergeValue(baseline.ProjectActionsAudit, local.ProjectActionsAudit, remote.ProjectActionsAudit, path + "/actions-audit", conflicts);
        local.Sequence = local.Sequence != 0 ? local.Sequence : remote.Sequence;
        local.AiRunId ??= remote.AiRunId;
        if (string.IsNullOrWhiteSpace(local.DeviceId)) local.DeviceId = remote.DeviceId;
    }

    private static string MergeMessageText(string baseline, string local, string remote, string path, List<WorkspaceMergeConflict> conflicts)
    {
        if (local == baseline) return remote;
        if (remote == baseline || local == remote) return local;
        // Streaming answers are monotonic in normal operation. Prefer the longer prefix when possible.
        if (local.StartsWith(remote, StringComparison.Ordinal)) return local;
        if (remote.StartsWith(local, StringComparison.Ordinal)) return remote;
        conflicts.Add(Conflict(path, baseline, local, remote, "Both devices changed one message; kept local text and preserved remote in conflict audit."));
        return local;
    }

    private static string MergeMessageStatus(string baseline, string local, string remote)
    {
        if (local == baseline) return remote;
        if (remote == baseline || local == remote) return local;
        static int Rank(string s) => s switch { "complete" => 4, "error" => 3, "interrupted" => 2, "streaming" => 1, _ => 0 };
        return Rank(remote) > Rank(local) ? remote : local;
    }

    private static List<AiSavedFile> MergeSavedFiles(List<AiSavedFile> local, List<AiSavedFile> remote)
    {
        var result = local.ToList();
        foreach (var item in remote)
            if (!result.Any(x => x.Sha256 == item.Sha256 && x.Path == item.Path && x.Name == item.Name)) result.Add(item);
        return result;
    }

    private static void CopyMessageMutable(AiMessage source, AiMessage target)
    {
        if (target.CreatedAt == default) target.CreatedAt = source.CreatedAt;
        target.Content = source.Content; target.Status = source.Status; target.ErrorText = source.ErrorText;
        target.SavedFiles = ProjectWorkspaceStore.Clone(source.SavedFiles);
        target.ProjectActionsApplied = source.ProjectActionsApplied; target.ProjectActionsAudit = source.ProjectActionsAudit;
        target.Sequence = source.Sequence; target.AiRunId = source.AiRunId; target.DeviceId = source.DeviceId;
    }

    private static void AssignSequences(AiConversation conversation, string writerId) => AssignSequences(conversation.Messages, writerId);

    private static void AssignSequences(List<AiMessage> messages, string writerId)
    {
        var max = messages.Where(m => m.Sequence > 0).Select(m => m.Sequence).DefaultIfEmpty().Max();
        foreach (var message in messages.Where(m => m.Sequence == 0).OrderBy(m => m.CreatedAt).ThenBy(m => m.Id))
        {
            message.Sequence = ++max;
            if (string.IsNullOrWhiteSpace(message.DeviceId)) message.DeviceId = writerId;
        }
    }

    private static List<LegacyImportRecord> MergeImportHistory(List<LegacyImportRecord> baseline, List<LegacyImportRecord> local, List<LegacyImportRecord> remote)
    {
        var result = local.ToList();
        foreach (var record in remote)
            if (!result.Any(x => x.SourceName == record.SourceName && x.Sha256 == record.Sha256 && x.ImportedAt == record.ImportedAt)) result.Add(record);
        return result.OrderBy(x => x.ImportedAt).ToList();
    }

    private static DateTime? Earliest(params DateTime?[] values)
        => values.Where(v => v.HasValue).Select(v => v!.Value).Cast<DateTime?>().OrderBy(v => v).FirstOrDefault();

    private static DateTime? Latest(params DateTime?[] values)
        => values.Where(v => v.HasValue).Select(v => v!.Value).Cast<DateTime?>().OrderByDescending(v => v).FirstOrDefault();

    private static T MergeValue<T>(T baseline, T local, T remote, string path, List<WorkspaceMergeConflict> conflicts)
    {
        var localChanged = !Same(local, baseline);
        var remoteChanged = !Same(remote, baseline);
        if (!localChanged) return CloneValue(remote);
        if (!remoteChanged || Same(local, remote)) return local;
        conflicts.Add(Conflict(path, baseline, local, remote, "Both devices changed the same field; kept local value and preserved remote in conflict audit."));
        return local;
    }

    private static T CloneValue<T>(T value)
    {
        if (value is null) return value!;
        var type = typeof(T);
        if (type.IsValueType || value is string) return value;
        return ProjectWorkspaceStore.Clone(value);
    }

    private static bool Same<T>(T a, T b)
        => JsonSerializer.Serialize(a, ProjectWorkspaceStore.Json) == JsonSerializer.Serialize(b, ProjectWorkspaceStore.Json);

    private static WorkspaceMergeConflict Conflict<T>(string path, T baseline, T local, T remote, string resolution)
        => new(DateTime.UtcNow, path, resolution,
            JsonSerializer.Serialize(baseline, ProjectWorkspaceStore.Json),
            JsonSerializer.Serialize(local, ProjectWorkspaceStore.Json),
            JsonSerializer.Serialize(remote, ProjectWorkspaceStore.Json));

    private static void Reorder<T>(List<T> local, List<T> baseline, List<T> remote, Func<T, Guid> id)
    {
        var baseOrder = baseline.Select(id).ToArray();
        var localOrder = local.Where(x => baseOrder.Contains(id(x))).Select(id).ToArray();
        var remoteOrder = remote.Where(x => baseOrder.Contains(id(x))).Select(id).ToArray();
        var localReordered = !localOrder.SequenceEqual(baseOrder.Where(x => localOrder.Contains(x)));
        if (localReordered) return;

        var byId = local.ToDictionary(id);
        var ordered = new List<T>();
        foreach (var item in remote)
            if (byId.Remove(id(item), out var existing)) ordered.Add(existing);
        ordered.AddRange(local.Where(x => byId.ContainsKey(id(x))));
        local.Clear(); local.AddRange(ordered);
    }
}
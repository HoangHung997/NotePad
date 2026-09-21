using System.Text.Json;

namespace H2Notes.Core;

public enum H2ProjectSyncStatus
{
    LocalOnly = 0,
    Synced = 1,
    Pending = 2
}

public sealed record H2ProjectReplicaSnapshot(
    Guid WorkspaceId,
    Guid ProjectId,
    Guid DeviceId,
    long AppliedServerSequence,
    long NextDeviceSequence,
    ProjectRecord? AuthoritativeProject,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<H2ProjectRevisionEntry>? Revisions = null);

public sealed record H2ProjectSyncResult(
    H2ProjectSyncStatus Status,
    long AppliedServerSequence,
    int PendingCount,
    int PulledEventCount,
    int AcknowledgedEventCount);

/// <summary>
/// Machine-local durable cache/outbox for Coordinator mode. This state is never shared through
/// WebDAV/SMB and therefore must not be used as cross-device authority.
/// </summary>
public sealed class H2CoordinatorClientStateStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _root;
    private readonly string _outboxRoot;
    private readonly string _replicaPath;

    public H2CoordinatorClientStateStore(
        string localRoot,
        Guid workspaceId,
        Guid projectId,
        Guid deviceId)
    {
        if (string.IsNullOrWhiteSpace(localRoot))
            throw new ArgumentException("Local Coordinator client state root is required.", nameof(localRoot));
        WorkspaceId = RequireId(workspaceId, nameof(workspaceId));
        ProjectId = RequireId(projectId, nameof(projectId));
        DeviceId = RequireId(deviceId, nameof(deviceId));

        var full = Path.GetFullPath(localRoot.Trim());
        ValidateLocalRoot(full);
        _root = Path.Combine(
            full,
            WorkspaceId.ToString("N"),
            ProjectId.ToString("N"),
            DeviceId.ToString("N"));
        _outboxRoot = Path.Combine(_root, "outbox");
        _replicaPath = Path.Combine(_root, "replica.json");
    }

    public Guid WorkspaceId { get; }
    public Guid ProjectId { get; }
    public Guid DeviceId { get; }
    public string Root => _root;

    public H2ProjectReplicaSnapshot LoadReplica()
    {
        if (!File.Exists(_replicaPath))
            return new H2ProjectReplicaSnapshot(
                WorkspaceId,
                ProjectId,
                DeviceId,
                0,
                1,
                null,
                DateTimeOffset.UtcNow,
                Array.Empty<H2ProjectRevisionEntry>());

        var snapshot = JsonSerializer.Deserialize<H2ProjectReplicaSnapshot>(
            File.ReadAllText(_replicaPath),
            Json)
            ?? throw new InvalidDataException("Local Coordinator replica state is unreadable.");

        if (snapshot.WorkspaceId != WorkspaceId
            || snapshot.ProjectId != ProjectId
            || snapshot.DeviceId != DeviceId
            || snapshot.AppliedServerSequence < 0
            || snapshot.NextDeviceSequence <= 0)
            throw new InvalidDataException("Local Coordinator replica identity/state is invalid.");

        return snapshot with
        {
            AuthoritativeProject = Clone(snapshot.AuthoritativeProject),
            Revisions = (snapshot.Revisions ?? Array.Empty<H2ProjectRevisionEntry>()).ToArray()
        };
    }

    public void SaveReplica(H2ProjectReplicaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.WorkspaceId != WorkspaceId
            || snapshot.ProjectId != ProjectId
            || snapshot.DeviceId != DeviceId)
            throw new InvalidOperationException("Replica identity does not match local state store.");
        if (snapshot.AppliedServerSequence < 0 || snapshot.NextDeviceSequence <= 0)
            throw new InvalidOperationException("Replica sequence values are invalid.");

        AtomicWrite(_replicaPath, JsonSerializer.SerializeToUtf8Bytes(
            snapshot with { AuthoritativeProject = Clone(snapshot.AuthoritativeProject) },
            Json));
    }

    public void Enqueue(H2ProjectEventDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.WorkspaceId != WorkspaceId
            || draft.ProjectId != ProjectId
            || draft.DeviceId != DeviceId)
            throw new InvalidOperationException("Outbox event identity does not match local replica.");

        Directory.CreateDirectory(_outboxRoot);
        var path = EventPath(draft.DeviceSequence, draft.EventId);
        if (File.Exists(path))
        {
            var existing = ReadDraft(path);
            if (existing.EventId != draft.EventId
                || existing.ClientOperationId != draft.ClientOperationId
                || !string.Equals(existing.PayloadSha256, draft.PayloadSha256, StringComparison.Ordinal))
                throw new InvalidDataException("Outbox event path already contains incompatible content.");
            return;
        }

        AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(draft, Json));
    }

    public IReadOnlyList<H2ProjectEventDraft> Pending()
    {
        if (!Directory.Exists(_outboxRoot)) return Array.Empty<H2ProjectEventDraft>();

        var result = new List<H2ProjectEventDraft>();
        foreach (var file in Directory.EnumerateFiles(_outboxRoot, "*.pending.json")
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var draft = ReadDraft(file);
            if (draft.WorkspaceId != WorkspaceId
                || draft.ProjectId != ProjectId
                || draft.DeviceId != DeviceId)
                throw new InvalidDataException("Outbox contains an event for another replica identity.");
            result.Add(draft);
        }

        var duplicateOperation = result
            .GroupBy(item => item.ClientOperationId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateOperation is not null)
            throw new InvalidDataException("Outbox contains duplicate ClientOperationId entries.");

        return result.OrderBy(item => item.DeviceSequence).ThenBy(item => item.EventId).ToArray();
    }

    public void Acknowledge(IEnumerable<H2ProjectEventAcknowledgement> acknowledgements)
    {
        ArgumentNullException.ThrowIfNull(acknowledgements);
        var byEvent = acknowledgements.ToDictionary(item => item.EventId);

        if (!Directory.Exists(_outboxRoot) || byEvent.Count == 0) return;

        foreach (var file in Directory.EnumerateFiles(_outboxRoot, "*.pending.json"))
        {
            var draft = ReadDraft(file);
            if (!byEvent.TryGetValue(draft.EventId, out var ack)) continue;
            if (ack.ClientOperationId != draft.ClientOperationId)
                throw new InvalidDataException("Coordinator acknowledgement operation identity does not match outbox event.");
            File.Delete(file);
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(_outboxRoot).Any())
                Directory.Delete(_outboxRoot);
        }
        catch (IOException) { }
    }

    private string EventPath(long deviceSequence, Guid eventId)
        => Path.Combine(_outboxRoot, $"{deviceSequence:D20}-{eventId:N}.pending.json");

    private static H2ProjectEventDraft ReadDraft(string path)
        => JsonSerializer.Deserialize<H2ProjectEventDraft>(File.ReadAllText(path), Json)
           ?? throw new InvalidDataException("Local Coordinator outbox event is unreadable.");

    private static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(
                   temp,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   32 * 1024,
                   FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }

        File.Move(temp, path, true);
    }

    private static void ValidateLocalRoot(string full)
    {
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("Coordinator client cache/outbox must be machine-local.");

        if (!OperatingSystem.IsWindows()) return;
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Coordinator client state path has no local drive root.");
        try
        {
            if (new DriveInfo(root).DriveType == DriveType.Network)
                throw new ArgumentException("Coordinator client cache/outbox must not use a mapped network drive.");
        }
        catch (DriveNotFoundException)
        {
            // The caller may be preparing a local path before the drive/folder is created.
        }
    }

    private static Guid RequireId(Guid value, string name)
        => value != Guid.Empty ? value : throw new ArgumentException(name + " is required.", name);

    private static ProjectRecord? Clone(ProjectRecord? project)
    {
        if (project is null) return null;
        return JsonSerializer.Deserialize<ProjectRecord>(
                   JsonSerializer.Serialize(project))
               ?? throw new InvalidDataException("Project replica clone failed.");
    }
}

public static class H2ProjectEventPayload
{
    private static readonly JsonSerializerOptions Json = new();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, Json)
           ?? throw new InvalidDataException("Project event payload could not be decoded.");
}

/// <summary>
/// Applies accepted/pending project events to the existing H2 ProjectRecord/TaskRecord/ProjectLink
/// product model. Conflict/revision arbitration is Coordinator task H2M-133E; this class applies an
/// already-ordered event stream deterministically.
/// </summary>
public static class H2ProjectEventApplier
{
    public static ProjectRecord? Apply(ProjectRecord? current, H2ProjectEventDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var project = Clone(current);

        return draft.Kind switch
        {
            H2ProjectEventKind.CreateEntity => ApplyCreate(project, draft),
            H2ProjectEventKind.SetField => ApplySetField(project, draft),
            H2ProjectEventKind.DeleteEntity => ApplyDelete(project, draft),
            _ => throw new NotSupportedException(
                $"Project event kind {draft.Kind} is not part of the initial Coordinator client projection.")
        };
    }

    private static ProjectRecord? ApplyCreate(ProjectRecord? project, H2ProjectEventDraft draft)
    {
        switch (draft.Target.EntityKind)
        {
            case H2ProjectEntityKind.Project:
            {
                if (project is not null)
                    throw new InvalidDataException("Project already exists in replica.");
                var created = H2ProjectEventPayload.Deserialize<ProjectRecord>(draft.PayloadJson);
                if (created.Id != draft.Target.EntityId || created.Id != draft.ProjectId)
                    throw new InvalidDataException("Created project identity does not match event target.");
                created.ChecklistItems ??= [];
                created.Links ??= [];
                return created;
            }
            case H2ProjectEntityKind.Task:
            {
                project = RequireProject(project, draft);
                var task = H2ProjectEventPayload.Deserialize<TaskRecord>(draft.PayloadJson);
                if (task.Id != draft.Target.EntityId)
                    throw new InvalidDataException("Created task identity does not match event target.");
                if (project.ChecklistItems.Any(item => item.Id == task.Id))
                    return project; // replay/idempotent projection
                project.ChecklistItems.Add(task);
                project.Revision++;
                return project;
            }
            case H2ProjectEntityKind.Link:
            {
                project = RequireProject(project, draft);
                var link = H2ProjectEventPayload.Deserialize<ProjectLink>(draft.PayloadJson);
                if (link.Id != draft.Target.EntityId)
                    throw new InvalidDataException("Created link identity does not match event target.");
                if (project.Links.Any(item => item.Id == link.Id))
                    return project;
                project.Links.Add(link);
                project.Revision++;
                return project;
            }
            default:
                throw new NotSupportedException(
                    $"CreateEntity for {draft.Target.EntityKind} is not implemented in initial project replica.");
        }
    }

    private static ProjectRecord ApplySetField(ProjectRecord? project, H2ProjectEventDraft draft)
    {
        project = RequireProject(project, draft);
        var field = draft.Target.FieldKey
            ?? throw new InvalidDataException("SetField event is missing FieldKey.");

        switch (draft.Target.EntityKind)
        {
            case H2ProjectEntityKind.Project:
                if (draft.Target.EntityId != project.Id)
                    throw new InvalidDataException("Project field target does not match project identity.");
                switch (field)
                {
                    case "NameRich":
                        project.NameRich = H2ProjectEventPayload.Deserialize<RichDocument>(draft.PayloadJson);
                        project.Name = project.NameRich.Text;
                        break;
                    case "NotesRich":
                        project.NotesRich = H2ProjectEventPayload.Deserialize<RichDocument>(draft.PayloadJson);
                        project.Notes = project.NotesRich.Text;
                        break;
                    default:
                        throw new NotSupportedException("Unsupported project field: " + field);
                }
                project.Revision++;
                return project;

            case H2ProjectEntityKind.Task:
            {
                var task = project.ChecklistItems.SingleOrDefault(item => item.Id == draft.Target.EntityId)
                    ?? throw new InvalidDataException("Task field target does not exist.");
                switch (field)
                {
                    case "TextRich":
                        task.TextRich = H2ProjectEventPayload.Deserialize<RichDocument>(draft.PayloadJson);
                        task.Text = task.TextRich.Text;
                        break;
                    case "CommentRich":
                        task.CommentRich = H2ProjectEventPayload.Deserialize<RichDocument>(draft.PayloadJson);
                        task.Comment = task.CommentRich.Text;
                        break;
                    case "IsCompleted":
                        task.IsCompleted = H2ProjectEventPayload.Deserialize<bool>(draft.PayloadJson);
                        break;
                    case "CompletedAtUtc":
                        task.CompletedAtUtc = H2ProjectEventPayload.Deserialize<DateTime?>(draft.PayloadJson);
                        break;
                    default:
                        throw new NotSupportedException("Unsupported task field: " + field);
                }
                task.Revision++;
                project.Revision++;
                return project;
            }

            case H2ProjectEntityKind.Link:
            {
                var index = project.Links.FindIndex(item => item.Id == draft.Target.EntityId);
                if (index < 0) throw new InvalidDataException("Link field target does not exist.");
                var current = project.Links[index];
                project.Links[index] = field switch
                {
                    "Label" => current with { Label = H2ProjectEventPayload.Deserialize<string>(draft.PayloadJson) },
                    "Target" => current with { Target = H2ProjectEventPayload.Deserialize<string>(draft.PayloadJson) },
                    _ => throw new NotSupportedException("Unsupported link field: " + field)
                };
                project.Revision++;
                return project;
            }

            default:
                throw new NotSupportedException(
                    $"SetField for {draft.Target.EntityKind} is not implemented in initial project replica.");
        }
    }

    private static ProjectRecord? ApplyDelete(ProjectRecord? project, H2ProjectEventDraft draft)
    {
        project = RequireProject(project, draft);
        switch (draft.Target.EntityKind)
        {
            case H2ProjectEntityKind.Project:
                if (draft.Target.EntityId != project.Id)
                    throw new InvalidDataException("Deleted project identity does not match replica.");
                return null;
            case H2ProjectEntityKind.Task:
                project.ChecklistItems.RemoveAll(item => item.Id == draft.Target.EntityId);
                project.Revision++;
                return project;
            case H2ProjectEntityKind.Link:
                project.Links.RemoveAll(item => item.Id == draft.Target.EntityId);
                project.Revision++;
                return project;
            default:
                throw new NotSupportedException(
                    $"DeleteEntity for {draft.Target.EntityKind} is not implemented in initial project replica.");
        }
    }

    private static ProjectRecord RequireProject(ProjectRecord? project, H2ProjectEventDraft draft)
    {
        if (project is null)
            throw new InvalidDataException("Project event cannot be applied before project creation/baseline.");
        if (project.Id != draft.ProjectId)
            throw new InvalidDataException("Project event ProjectId does not match replica.");
        return project;
    }

    private static ProjectRecord? Clone(ProjectRecord? project)
    {
        if (project is null) return null;
        return JsonSerializer.Deserialize<ProjectRecord>(JsonSerializer.Serialize(project))
               ?? throw new InvalidDataException("Project event projection clone failed.");
    }
}

/// <summary>
/// One-project client replica. Remote accepted state is kept separately from optimistic local pending
/// state so reconnect can pull remote work first and then safely reapply durable local outbox events.
/// </summary>
public sealed class H2ProjectSyncClient
{
    private readonly IH2SyncCoordinator _coordinator;
    private readonly H2CoordinatorClientStateStore _local;
    private H2ProjectReplicaSnapshot _replica;

    public H2ProjectSyncClient(
        Guid workspaceId,
        Guid projectId,
        H2CoordinatorDeviceIdentity device,
        IH2SyncCoordinator coordinator,
        H2CoordinatorClientStateStore localState)
    {
        WorkspaceId = workspaceId != Guid.Empty ? workspaceId : throw new ArgumentException("WorkspaceId required.", nameof(workspaceId));
        ProjectId = projectId != Guid.Empty ? projectId : throw new ArgumentException("ProjectId required.", nameof(projectId));
        Device = device ?? throw new ArgumentNullException(nameof(device));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _local = localState ?? throw new ArgumentNullException(nameof(localState));
        if (_local.WorkspaceId != WorkspaceId || _local.ProjectId != ProjectId || _local.DeviceId != Device.DeviceId)
            throw new InvalidOperationException("Local replica identity does not match sync client.");

        _replica = _local.LoadReplica();
        var pending = _local.Pending();
        if (pending.Count > 0)
        {
            var minimumNext = pending.Max(item => item.DeviceSequence) + 1;
            if (_replica.NextDeviceSequence < minimumNext)
            {
                _replica = _replica with
                {
                    NextDeviceSequence = minimumNext,
                    UpdatedUtc = DateTimeOffset.UtcNow
                };
                PersistReplica();
            }
        }
        RebuildCurrent();
    }

    public Guid WorkspaceId { get; }
    public Guid ProjectId { get; }
    public H2CoordinatorDeviceIdentity Device { get; }
    public ProjectRecord? AuthoritativeProject { get; private set; }
    public ProjectRecord? CurrentProject { get; private set; }
    public long AppliedServerSequence => _replica.AppliedServerSequence;
    public int PendingCount => _local.Pending().Count;

    public H2ProjectEventDraft QueueCreateProject(ProjectRecord project, DateTimeOffset? clientCreatedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Id != ProjectId)
            throw new ArgumentException("Project identity does not match sync client.", nameof(project));
        if (CurrentProject is not null)
            throw new InvalidOperationException("Project already exists in current replica.");
        return Queue(
            H2ProjectEventKind.CreateEntity,
            new H2ProjectMutationTarget(H2ProjectEntityKind.Project, project.Id, expectedRevision: 0),
            H2ProjectEventPayload.Serialize(project),
            clientCreatedUtc);
    }

    public H2ProjectEventDraft QueueSetProjectName(RichDocument name, DateTimeOffset? clientCreatedUtc = null)
        => QueueSetField(
            H2ProjectEntityKind.Project,
            ProjectId,
            "NameRich",
            ExpectedRevision(H2ProjectEntityKind.Project, ProjectId, "NameRich"),
            name ?? throw new ArgumentNullException(nameof(name)),
            clientCreatedUtc);

    public H2ProjectEventDraft QueueSetProjectNotes(RichDocument notes, DateTimeOffset? clientCreatedUtc = null)
        => QueueSetField(
            H2ProjectEntityKind.Project,
            ProjectId,
            "NotesRich",
            ExpectedRevision(H2ProjectEntityKind.Project, ProjectId, "NotesRich"),
            notes ?? throw new ArgumentNullException(nameof(notes)),
            clientCreatedUtc);

    public H2ProjectEventDraft QueueCreateTask(TaskRecord task, DateTimeOffset? clientCreatedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        RequireCurrentProject();
        return Queue(
            H2ProjectEventKind.CreateEntity,
            new H2ProjectMutationTarget(H2ProjectEntityKind.Task, task.Id, expectedRevision: 0),
            H2ProjectEventPayload.Serialize(task),
            clientCreatedUtc);
    }

    public H2ProjectEventDraft QueueSetTaskText(Guid taskId, RichDocument text, DateTimeOffset? clientCreatedUtc = null)
        => QueueSetTaskField(taskId, "TextRich", text, clientCreatedUtc);

    public H2ProjectEventDraft QueueSetTaskComment(Guid taskId, RichDocument comment, DateTimeOffset? clientCreatedUtc = null)
        => QueueSetTaskField(taskId, "CommentRich", comment, clientCreatedUtc);

    public H2ProjectEventDraft QueueSetTaskCompleted(Guid taskId, bool completed, DateTimeOffset? clientCreatedUtc = null)
        => QueueSetTaskField(taskId, "IsCompleted", completed, clientCreatedUtc);

    public H2ProjectEventDraft QueueDeleteTask(Guid taskId, DateTimeOffset? clientCreatedUtc = null)
    {
        var task = RequireTask(taskId);
        return Queue(
            H2ProjectEventKind.DeleteEntity,
            new H2ProjectMutationTarget(
                H2ProjectEntityKind.Task,
                task.Id,
                expectedRevision: ExpectedRevision(H2ProjectEntityKind.Task, task.Id, null)),
            "{}",
            clientCreatedUtc);
    }

    public H2ProjectEventDraft QueueAddLink(ProjectLink link, DateTimeOffset? clientCreatedUtc = null)
    {
        RequireCurrentProject();
        return Queue(
            H2ProjectEventKind.CreateEntity,
            new H2ProjectMutationTarget(H2ProjectEntityKind.Link, link.Id, expectedRevision: 0),
            H2ProjectEventPayload.Serialize(link),
            clientCreatedUtc);
    }

    public H2ProjectEventDraft QueueRemoveLink(Guid linkId, DateTimeOffset? clientCreatedUtc = null)
    {
        var project = RequireCurrentProject();
        if (!project.Links.Any(link => link.Id == linkId))
            throw new KeyNotFoundException("Project link does not exist.");
        return Queue(
            H2ProjectEventKind.DeleteEntity,
            new H2ProjectMutationTarget(H2ProjectEntityKind.Link, linkId),
            "{}",
            clientCreatedUtc);
    }

    public async Task<H2ProjectSyncResult> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _coordinator.RegisterDeviceAsync(WorkspaceId, Device, cancellationToken).ConfigureAwait(false);

        var pulled = await PullRemoteAsync(cancellationToken).ConfigureAwait(false);
        var pending = _local.Pending();
        var acknowledged = 0;

        if (pending.Count > 0)
        {
            var result = await _coordinator.SubmitProjectEventsAsync(
                WorkspaceId,
                Device.DeviceId,
                pending,
                cancellationToken).ConfigureAwait(false);

            // Keep durable outbox items until the accepted server sequence has been ingested into
            // the local authoritative projection. If the process/network fails after Submit, retrying
            // the same ClientOperationId is idempotent and the optimistic edit remains durable/visible.
            pulled += await PullRemoteAsync(cancellationToken).ConfigureAwait(false);
            if (result.Acknowledgements.Count > 0)
            {
                var requiredWatermark = result.Acknowledgements.Max(item => item.ServerSequence);
                if (_replica.AppliedServerSequence < requiredWatermark)
                    throw new InvalidDataException(
                        $"Coordinator acknowledgement watermark {requiredWatermark} was not observable after submit; local outbox is retained.");
            }

            _local.Acknowledge(result.Acknowledgements);
            acknowledged = result.Acknowledgements.Count;
        }

        PersistReplica();
        RebuildCurrent();
        var remaining = _local.Pending().Count;
        return new H2ProjectSyncResult(
            remaining == 0 ? H2ProjectSyncStatus.Synced : H2ProjectSyncStatus.Pending,
            _replica.AppliedServerSequence,
            remaining,
            pulled,
            acknowledged);
    }

    private async Task<int> PullRemoteAsync(CancellationToken cancellationToken)
    {
        var pulled = 0;

        if (_replica.AuthoritativeProject is null && _replica.AppliedServerSequence == 0)
        {
            var snapshot = await _coordinator.GetProjectSnapshotAsync(
                WorkspaceId,
                ProjectId,
                null,
                cancellationToken).ConfigureAwait(false);
            if (snapshot is not null)
            {
                var project = H2ProjectEventPayload.Deserialize<ProjectRecord>(snapshot.StateJson);
                if (project.Id != ProjectId)
                    throw new InvalidDataException("Coordinator snapshot ProjectId does not match client replica.");
                _replica = _replica with
                {
                    AuthoritativeProject = project,
                    AppliedServerSequence = snapshot.ThroughServerSequence,
                    Revisions = snapshot.Revisions.ToArray(),
                    UpdatedUtc = DateTimeOffset.UtcNow
                };
            }
        }

        while (true)
        {
            var batch = await _coordinator.GetProjectEventsAsync(
                WorkspaceId,
                ProjectId,
                _replica.AppliedServerSequence,
                500,
                cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0) break;

            var ordered = batch.OrderBy(item => item.ServerSequence).ToArray();
            foreach (var accepted in ordered)
            {
                if (accepted.ServerSequence <= _replica.AppliedServerSequence)
                    continue;
                if (accepted.ServerSequence != _replica.AppliedServerSequence + 1)
                    throw new InvalidDataException(
                        $"Coordinator project event sequence gap: expected {_replica.AppliedServerSequence + 1}, got {accepted.ServerSequence}.");
                if (accepted.Draft.WorkspaceId != WorkspaceId || accepted.Draft.ProjectId != ProjectId)
                    throw new InvalidDataException("Coordinator returned event for another replica.");

                var authoritative = _replica.AuthoritativeProject;
                var revisions = (_replica.Revisions ?? Array.Empty<H2ProjectRevisionEntry>()).ToList();

                if (accepted.Disposition == H2ProjectEventDisposition.Applied)
                {
                    authoritative = H2ProjectEventApplier.Apply(authoritative, accepted.Draft);
                    ApplyAcceptedRevision(revisions, accepted);
                }

                _replica = _replica with
                {
                    AuthoritativeProject = authoritative,
                    AppliedServerSequence = accepted.ServerSequence,
                    Revisions = revisions,
                    UpdatedUtc = DateTimeOffset.UtcNow
                };

                // An earlier submit may have succeeded even if the client crashed/lost the response.
                // Observing our exact immutable event in authoritative server order is sufficient
                // acknowledgement to remove the matching local outbox copy without applying it twice.
                if (accepted.Draft.DeviceId == Device.DeviceId)
                {
                    _local.Acknowledge(
                    [
                        new H2ProjectEventAcknowledgement(
                            accepted.Draft.EventId,
                            accepted.Draft.ClientOperationId,
                            accepted.ServerSequence,
                            accepted.AcceptedUtc)
                    ]);
                }

                pulled++;
            }

            PersistReplica();
            if (batch.Count < 500) break;
        }

        RebuildCurrent();
        return pulled;
    }

    private long ExpectedRevision(
        H2ProjectEntityKind entityKind,
        Guid entityId,
        string? fieldKey)
    {
        var revisions = (_replica.Revisions ?? Array.Empty<H2ProjectRevisionEntry>())
            .ToDictionary(item => item.StableKey, StringComparer.Ordinal);

        foreach (var pending in _local.Pending())
            ApplyPendingRevision(revisions, pending);

        var key = RevisionKey(entityKind, entityId, fieldKey);
        return revisions.TryGetValue(key, out var entry) ? entry.Revision : 0;
    }

    private static void ApplyPendingRevision(
        Dictionary<string, H2ProjectRevisionEntry> revisions,
        H2ProjectEventDraft draft)
    {
        var entityKey = RevisionKey(draft.Target.EntityKind, draft.Target.EntityId, null);
        revisions.TryGetValue(entityKey, out var entity);

        switch (draft.Kind)
        {
            case H2ProjectEventKind.CreateEntity:
                revisions[entityKey] = new H2ProjectRevisionEntry(
                    draft.Target.EntityKind,
                    draft.Target.EntityId,
                    null,
                    (entity?.Revision ?? 0) + 1,
                    false);
                break;

            case H2ProjectEventKind.SetField:
            {
                var fieldKey = RevisionKey(
                    draft.Target.EntityKind,
                    draft.Target.EntityId,
                    draft.Target.FieldKey);
                revisions.TryGetValue(fieldKey, out var field);
                revisions[fieldKey] = new H2ProjectRevisionEntry(
                    draft.Target.EntityKind,
                    draft.Target.EntityId,
                    draft.Target.FieldKey,
                    (field?.Revision ?? 0) + 1,
                    false);
                revisions[entityKey] = new H2ProjectRevisionEntry(
                    draft.Target.EntityKind,
                    draft.Target.EntityId,
                    null,
                    (entity?.Revision ?? 0) + 1,
                    false);
                break;
            }

            case H2ProjectEventKind.DeleteEntity:
                revisions[entityKey] = new H2ProjectRevisionEntry(
                    draft.Target.EntityKind,
                    draft.Target.EntityId,
                    null,
                    (entity?.Revision ?? 0) + 1,
                    true);
                break;
        }
    }

    private static void ApplyAcceptedRevision(
        List<H2ProjectRevisionEntry> revisions,
        H2AcceptedProjectEvent accepted)
    {
        var draft = accepted.Draft;
        if (draft.Kind == H2ProjectEventKind.SetField && accepted.ResultingRevision.HasValue)
            UpsertReplicaRevision(
                revisions,
                new H2ProjectRevisionEntry(
                    draft.Target.EntityKind,
                    draft.Target.EntityId,
                    draft.Target.FieldKey,
                    accepted.ResultingRevision.Value,
                    false));

        if (accepted.ResultingEntityRevision.HasValue)
            UpsertReplicaRevision(
                revisions,
                new H2ProjectRevisionEntry(
                    draft.Target.EntityKind,
                    draft.Target.EntityId,
                    null,
                    accepted.ResultingEntityRevision.Value,
                    draft.Kind == H2ProjectEventKind.DeleteEntity));
    }

    private static void UpsertReplicaRevision(
        List<H2ProjectRevisionEntry> revisions,
        H2ProjectRevisionEntry next)
    {
        var index = revisions.FindIndex(item =>
            string.Equals(item.StableKey, next.StableKey, StringComparison.Ordinal));
        if (index >= 0) revisions[index] = next;
        else revisions.Add(next);
    }

    private static string RevisionKey(
        H2ProjectEntityKind entityKind,
        Guid entityId,
        string? fieldKey)
        => $"{(int)entityKind}:{entityId:N}:{fieldKey ?? "$entity"}";

    private H2ProjectEventDraft QueueSetTaskField<T>(
        Guid taskId,
        string field,
        T value,
        DateTimeOffset? clientCreatedUtc)
    {
        var task = RequireTask(taskId);
        return QueueSetField(
            H2ProjectEntityKind.Task,
            task.Id,
            field,
            ExpectedRevision(H2ProjectEntityKind.Task, task.Id, field),
            value,
            clientCreatedUtc);
    }

    private H2ProjectEventDraft QueueSetField<T>(
        H2ProjectEntityKind entityKind,
        Guid entityId,
        string field,
        long? expectedRevision,
        T value,
        DateTimeOffset? clientCreatedUtc)
        => Queue(
            H2ProjectEventKind.SetField,
            new H2ProjectMutationTarget(entityKind, entityId, field, expectedRevision),
            H2ProjectEventPayload.Serialize(value),
            clientCreatedUtc);

    private H2ProjectEventDraft Queue(
        H2ProjectEventKind kind,
        H2ProjectMutationTarget target,
        string payload,
        DateTimeOffset? clientCreatedUtc)
    {
        var deviceSequence = _replica.NextDeviceSequence;
        var draft = new H2ProjectEventDraft(
            Guid.NewGuid(),
            WorkspaceId,
            ProjectId,
            Device.DeviceId,
            deviceSequence,
            Guid.NewGuid(),
            kind,
            target,
            payload,
            H2ProjectEventDraft.ComputePayloadSha256(payload),
            clientCreatedUtc ?? DateTimeOffset.UtcNow);

        // Durable outbox precedes optimistic projection. A crash cannot expose an edit that has no
        // replayable local operation.
        _local.Enqueue(draft);
        _replica = _replica with
        {
            NextDeviceSequence = deviceSequence + 1,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        PersistReplica();
        RebuildCurrent();
        return draft;
    }

    private void RebuildCurrent()
    {
        AuthoritativeProject = Clone(_replica.AuthoritativeProject);
        var optimistic = Clone(_replica.AuthoritativeProject);
        foreach (var pending in _local.Pending())
            optimistic = H2ProjectEventApplier.Apply(optimistic, pending);
        CurrentProject = optimistic;
    }

    private void PersistReplica()
        => _local.SaveReplica(_replica with
        {
            AuthoritativeProject = Clone(_replica.AuthoritativeProject),
            UpdatedUtc = DateTimeOffset.UtcNow
        });

    private ProjectRecord RequireCurrentProject()
        => CurrentProject ?? throw new InvalidOperationException("Project replica has no project baseline.");

    private TaskRecord RequireTask(Guid taskId)
    {
        if (taskId == Guid.Empty) throw new ArgumentException("TaskId required.", nameof(taskId));
        return RequireCurrentProject().ChecklistItems.SingleOrDefault(task => task.Id == taskId)
               ?? throw new KeyNotFoundException("Project task does not exist.");
    }

    private static ProjectRecord? Clone(ProjectRecord? project)
    {
        if (project is null) return null;
        return JsonSerializer.Deserialize<ProjectRecord>(JsonSerializer.Serialize(project))
               ?? throw new InvalidDataException("Project replica clone failed.");
    }
}

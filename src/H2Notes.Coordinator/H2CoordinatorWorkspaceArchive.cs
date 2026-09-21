using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using H2Notes.Core;

namespace H2Notes.Coordinator;

public sealed record H2CoordinatorArchiveDevice(
    Guid DeviceId,
    string DisplayName,
    DateTimeOffset RegisteredUtc,
    DateTimeOffset LastSeenUtc);

public sealed record H2CoordinatorArchiveProjectRevisions(
    Guid ProjectId,
    IReadOnlyList<H2ProjectRevisionEntry> Revisions);

public sealed record H2CoordinatorWorkspaceArchive(
    int SchemaVersion,
    Guid WorkspaceId,
    DateTimeOffset ExportedUtc,
    IReadOnlyList<H2CoordinatorArchiveDevice> Devices,
    IReadOnlyList<H2AcceptedProjectEvent> Events,
    IReadOnlyList<H2ProjectSnapshot> Snapshots,
    IReadOnlyList<H2ProjectConflict> Conflicts,
    IReadOnlyList<H2CoordinatorArchiveProjectRevisions> ProjectRevisions);

public sealed record H2CoordinatorWorkspaceArchiveEnvelope(
    int SchemaVersion,
    string PayloadSha256,
    H2CoordinatorWorkspaceArchive Payload);

public sealed partial class H2CoordinatorSqliteStore
{
    private const int WorkspaceArchiveSchemaVersion = 1;
    private static readonly JsonSerializerOptions ArchiveJson = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public H2CoordinatorWorkspaceArchive ExportWorkspace(Guid workspaceId)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("WorkspaceId is required.", nameof(workspaceId));

        lock (_gate)
        {
            using var connection = Open();

            using (var exists = connection.CreateCommand())
            {
                exists.CommandText = "SELECT 1 FROM workspaces WHERE workspace_id = $workspace;";
                exists.Parameters.AddWithValue("$workspace", Id(workspaceId));
                if (exists.ExecuteScalar() is null)
                    throw new KeyNotFoundException("Workspace is not registered in this Coordinator.");
            }

            var devices = new List<H2CoordinatorArchiveDevice>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT device_id, display_name, registered_utc, last_seen_utc
                    FROM devices
                    WHERE workspace_id = $workspace
                    ORDER BY device_id;
                    """;
                command.Parameters.AddWithValue("$workspace", Id(workspaceId));
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    devices.Add(new H2CoordinatorArchiveDevice(
                        Guid.Parse(reader.GetString(0)),
                        reader.GetString(1),
                        ParseStamp(reader.GetString(2)),
                        ParseStamp(reader.GetString(3))));
                }
            }

            var events = new List<H2AcceptedProjectEvent>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT server_sequence, draft_json, accepted_utc,
                           disposition, resulting_revision, resulting_entity_revision, conflict_id
                    FROM (
                        SELECT workspace_id, project_id, server_sequence, draft_json, accepted_utc,
                               disposition, resulting_revision, resulting_entity_revision, conflict_id
                        FROM project_event_archive
                        UNION ALL
                        SELECT workspace_id, project_id, server_sequence, draft_json, accepted_utc,
                               disposition, resulting_revision, resulting_entity_revision, conflict_id
                        FROM project_events
                    ) e
                    WHERE workspace_id = $workspace
                    ORDER BY project_id, server_sequence;
                    """;
                command.Parameters.AddWithValue("$workspace", Id(workspaceId));
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    events.Add(new H2AcceptedProjectEvent(
                        Deserialize<H2ProjectEventDraft>(reader.GetString(1)),
                        reader.GetInt64(0),
                        ParseStamp(reader.GetString(2)),
                        (H2ProjectEventDisposition)reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetInt64(4),
                        reader.IsDBNull(5) ? null : reader.GetInt64(5),
                        reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6))));
                }
            }

            var snapshots = new List<H2ProjectSnapshot>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT snapshot_json
                    FROM project_snapshots
                    WHERE workspace_id = $workspace
                    ORDER BY project_id, through_sequence;
                    """;
                command.Parameters.AddWithValue("$workspace", Id(workspaceId));
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    snapshots.Add(Deserialize<H2ProjectSnapshot>(reader.GetString(0)));
            }

            var conflicts = new List<H2ProjectConflict>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT conflict_id, project_id,
                           entity_kind, entity_id, field_key, expected_revision,
                           event_ids_json, created_utc, state, resolved_event_id
                    FROM project_conflicts
                    WHERE workspace_id = $workspace
                    ORDER BY project_id, created_utc, conflict_id;
                    """;
                command.Parameters.AddWithValue("$workspace", Id(workspaceId));
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var target = new H2ProjectMutationTarget(
                        (H2ProjectEntityKind)reader.GetInt32(2),
                        Guid.Parse(reader.GetString(3)),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetInt64(5));
                    var ids = JsonSerializer.Deserialize<Guid[]>(reader.GetString(6), Json)
                        ?? throw new InvalidDataException("Conflict event IDs are missing.");
                    conflicts.Add(new H2ProjectConflict(
                        Guid.Parse(reader.GetString(0)),
                        workspaceId,
                        Guid.Parse(reader.GetString(1)),
                        target,
                        ids,
                        ParseStamp(reader.GetString(7)),
                        (H2ProjectConflictState)reader.GetInt32(8),
                        reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9))));
                }
            }

            var revisions = new List<H2CoordinatorArchiveProjectRevisions>();
            using (var projects = connection.CreateCommand())
            {
                projects.CommandText =
                    """
                    SELECT DISTINCT project_id
                    FROM project_heads
                    WHERE workspace_id = $workspace
                    ORDER BY project_id;
                    """;
                projects.Parameters.AddWithValue("$workspace", Id(workspaceId));
                using var reader = projects.ExecuteReader();
                var projectIds = new List<Guid>();
                while (reader.Read()) projectIds.Add(Guid.Parse(reader.GetString(0)));
                reader.Close();

                foreach (var projectId in projectIds)
                    revisions.Add(new H2CoordinatorArchiveProjectRevisions(
                        projectId,
                        ReadProjectRevisions(connection, null, workspaceId, projectId)));
            }

            var archive = new H2CoordinatorWorkspaceArchive(
                WorkspaceArchiveSchemaVersion,
                workspaceId,
                DateTimeOffset.UtcNow,
                devices,
                events,
                snapshots,
                conflicts,
                revisions);
            ValidateWorkspaceArchive(archive);
            return archive;
        }
    }

    public void ExportWorkspaceToFile(Guid workspaceId, string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            throw new ArgumentException("Archive path is required.", nameof(archivePath));

        var archive = ExportWorkspace(workspaceId);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(archive, ArchiveJson);
        var envelope = new H2CoordinatorWorkspaceArchiveEnvelope(
            WorkspaceArchiveSchemaVersion,
            Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant(),
            archive);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ArchiveJson);

        var full = Path.GetFullPath(archivePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(
                   temp,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   64 * 1024,
                   FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        File.Move(temp, full, true);
    }

    public static H2CoordinatorWorkspaceArchive ReadWorkspaceArchiveFile(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            throw new ArgumentException("Archive path is required.", nameof(archivePath));

        var envelope = JsonSerializer.Deserialize<H2CoordinatorWorkspaceArchiveEnvelope>(
            File.ReadAllBytes(Path.GetFullPath(archivePath)),
            ArchiveJson)
            ?? throw new InvalidDataException("Coordinator workspace archive could not be decoded.");
        if (envelope.SchemaVersion != WorkspaceArchiveSchemaVersion)
            throw new InvalidDataException("Unsupported Coordinator workspace archive schema.");

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(envelope.Payload, ArchiveJson);
        var hash = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant();
        if (!string.Equals(hash, envelope.PayloadSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Coordinator workspace archive payload hash mismatch.");

        ValidateWorkspaceArchive(envelope.Payload);
        return envelope.Payload;
    }

    public static H2CoordinatorSqliteStore RestoreWorkspaceArchive(
        string databasePath,
        string archivePath)
    {
        var archive = ReadWorkspaceArchiveFile(archivePath);
        var store = new H2CoordinatorSqliteStore(databasePath);
        store.ImportWorkspaceArchive(archive);
        return store;
    }

    public void ImportWorkspaceArchive(H2CoordinatorWorkspaceArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ValidateWorkspaceArchive(archive);

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();

            using (var exists = connection.CreateCommand())
            {
                exists.Transaction = tx;
                exists.CommandText = "SELECT 1 FROM workspaces WHERE workspace_id = $workspace;";
                exists.Parameters.AddWithValue("$workspace", Id(archive.WorkspaceId));
                if (exists.ExecuteScalar() is not null)
                    throw new InvalidOperationException("Workspace already exists in target Coordinator.");
            }

            Execute(connection, tx,
                "INSERT INTO workspaces(workspace_id, created_utc) VALUES($workspace, $created);",
                ("$workspace", Id(archive.WorkspaceId)),
                ("$created", Stamp(archive.ExportedUtc)));

            foreach (var device in archive.Devices)
            {
                Execute(connection, tx,
                    """
                    INSERT INTO devices(
                        workspace_id, device_id, display_name, registered_utc, last_seen_utc)
                    VALUES($workspace, $device, $name, $registered, $lastSeen);
                    """,
                    ("$workspace", Id(archive.WorkspaceId)),
                    ("$device", Id(device.DeviceId)),
                    ("$name", device.DisplayName),
                    ("$registered", Stamp(device.RegisteredUtc)),
                    ("$lastSeen", Stamp(device.LastSeenUtc)));
            }

            foreach (var accepted in archive.Events.OrderBy(item => item.Draft.ProjectId).ThenBy(item => item.ServerSequence))
            {
                var draft = accepted.Draft;
                Execute(connection, tx,
                    """
                    INSERT INTO project_events(
                        workspace_id, project_id, server_sequence,
                        event_id, device_id, device_sequence, client_operation_id,
                        payload_sha256, draft_json, accepted_utc,
                        disposition, resulting_revision, resulting_entity_revision, conflict_id)
                    VALUES(
                        $workspace, $project, $sequence,
                        $event, $device, $deviceSequence, $operation,
                        $payloadHash, $draft, $accepted,
                        $disposition, $revision, $entityRevision, $conflict);
                    """,
                    ("$workspace", Id(archive.WorkspaceId)),
                    ("$project", Id(draft.ProjectId)),
                    ("$sequence", accepted.ServerSequence),
                    ("$event", Id(draft.EventId)),
                    ("$device", Id(draft.DeviceId)),
                    ("$deviceSequence", draft.DeviceSequence),
                    ("$operation", Id(draft.ClientOperationId)),
                    ("$payloadHash", draft.PayloadSha256),
                    ("$draft", JsonSerializer.Serialize(draft, Json)),
                    ("$accepted", Stamp(accepted.AcceptedUtc)),
                    ("$disposition", (int)accepted.Disposition),
                    ("$revision", accepted.ResultingRevision),
                    ("$entityRevision", accepted.ResultingEntityRevision),
                    ("$conflict", accepted.ConflictId is null ? null : Id(accepted.ConflictId.Value)));
            }

            foreach (var snapshot in archive.Snapshots)
            {
                Execute(connection, tx,
                    """
                    INSERT INTO project_snapshots(
                        workspace_id, project_id, through_sequence,
                        state_sha256, snapshot_json, created_utc)
                    VALUES($workspace, $project, $sequence, $hash, $snapshot, $created);
                    """,
                    ("$workspace", Id(archive.WorkspaceId)),
                    ("$project", Id(snapshot.ProjectId)),
                    ("$sequence", snapshot.ThroughServerSequence),
                    ("$hash", snapshot.StateSha256),
                    ("$snapshot", JsonSerializer.Serialize(snapshot, Json)),
                    ("$created", Stamp(snapshot.CreatedUtc)));
            }

            foreach (var conflict in archive.Conflicts)
                InsertConflict(connection, tx, conflict);

            foreach (var project in archive.ProjectRevisions)
            {
                foreach (var revision in project.Revisions)
                {
                    UpsertRevision(
                        connection,
                        tx,
                        archive.WorkspaceId,
                        project.ProjectId,
                        revision.EntityKind,
                        revision.EntityId,
                        revision.FieldKey,
                        revision.Revision,
                        revision.IsDeleted,
                        LastRevisionEventId(archive, project.ProjectId, revision));
                }
            }

            var projectIds = archive.Events.Select(item => item.Draft.ProjectId)
                .Concat(archive.Snapshots.Select(item => item.ProjectId))
                .Concat(archive.Conflicts.Select(item => item.ProjectId))
                .Concat(archive.ProjectRevisions.Select(item => item.ProjectId))
                .Distinct()
                .ToArray();

            foreach (var projectId in projectIds)
            {
                var head = archive.Events
                    .Where(item => item.Draft.ProjectId == projectId)
                    .Select(item => item.ServerSequence)
                    .DefaultIfEmpty(0)
                    .Max();
                var snapshotHead = archive.Snapshots
                    .Where(item => item.ProjectId == projectId)
                    .Select(item => item.ThroughServerSequence)
                    .DefaultIfEmpty(0)
                    .Max();

                Execute(connection, tx,
                    """
                    INSERT INTO project_heads(
                        workspace_id, project_id, server_sequence, snapshot_through_sequence)
                    VALUES($workspace, $project, $head, $snapshot);
                    """,
                    ("$workspace", Id(archive.WorkspaceId)),
                    ("$project", Id(projectId)),
                    ("$head", head),
                    ("$snapshot", snapshotHead));
            }

            tx.Commit();
        }
    }

    private static Guid LastRevisionEventId(
        H2CoordinatorWorkspaceArchive archive,
        Guid projectId,
        H2ProjectRevisionEntry revision)
    {
        var candidates = archive.Events
            .Where(item =>
                item.Draft.ProjectId == projectId
                && item.Disposition == H2ProjectEventDisposition.Applied
                && item.Draft.Target.EntityKind == revision.EntityKind
                && item.Draft.Target.EntityId == revision.EntityId)
            .OrderBy(item => item.ServerSequence)
            .ToArray();

        if (revision.FieldKey is not null)
            candidates = candidates
                .Where(item =>
                    string.Equals(item.Draft.Target.FieldKey, revision.FieldKey, StringComparison.Ordinal))
                .ToArray();

        var last = candidates.LastOrDefault()
            ?? throw new InvalidDataException("Archive revision metadata has no matching accepted event.");
        return last.Draft.EventId;
    }

    private static void ValidateWorkspaceArchive(H2CoordinatorWorkspaceArchive archive)
    {
        if (archive.SchemaVersion != WorkspaceArchiveSchemaVersion)
            throw new InvalidDataException("Unsupported Coordinator workspace archive schema.");
        if (archive.WorkspaceId == Guid.Empty)
            throw new InvalidDataException("Coordinator workspace archive has no WorkspaceId.");

        if (archive.Devices.Any(device => device.DeviceId == Guid.Empty)
            || archive.Devices.GroupBy(device => device.DeviceId).Any(group => group.Count() > 1))
            throw new InvalidDataException("Coordinator workspace archive contains invalid/duplicate devices.");

        var eventIds = new HashSet<Guid>();
        var operations = new HashSet<Guid>();
        var deviceSequences = new HashSet<string>(StringComparer.Ordinal);
        foreach (var accepted in archive.Events)
        {
            if (accepted.Draft.WorkspaceId != archive.WorkspaceId)
                throw new InvalidDataException("Archive event belongs to another workspace.");
            if (!eventIds.Add(accepted.Draft.EventId))
                throw new InvalidDataException("Archive contains duplicate EventId.");
            if (!operations.Add(accepted.Draft.ClientOperationId))
                throw new InvalidDataException("Archive contains duplicate ClientOperationId.");
            if (!deviceSequences.Add($"{accepted.Draft.DeviceId:N}:{accepted.Draft.DeviceSequence}"))
                throw new InvalidDataException("Archive contains duplicate per-device sequence.");
        }

        foreach (var projectGroup in archive.Events.GroupBy(item => item.Draft.ProjectId))
        {
            var ordered = projectGroup.OrderBy(item => item.ServerSequence).ToArray();
            for (var i = 0; i < ordered.Length; i++)
                if (ordered[i].ServerSequence != i + 1)
                    throw new InvalidDataException("Archive project event sequence is not contiguous from 1.");
        }

        var knownEvents = archive.Events.ToDictionary(item => item.Draft.EventId);
        foreach (var conflict in archive.Conflicts)
        {
            if (conflict.WorkspaceId != archive.WorkspaceId)
                throw new InvalidDataException("Archive conflict belongs to another workspace.");
            foreach (var eventId in conflict.EventIds)
                if (!knownEvents.ContainsKey(eventId))
                    throw new InvalidDataException("Archive conflict references a missing event.");
            if (conflict.ResolvedByEventId is { } resolved && !knownEvents.ContainsKey(resolved))
                throw new InvalidDataException("Resolved archive conflict references a missing resolution event.");
        }

        foreach (var accepted in archive.Events.Where(item => item.Disposition == H2ProjectEventDisposition.Conflict))
        {
            if (accepted.ConflictId is null
                || !archive.Conflicts.Any(conflict =>
                    conflict.ConflictId == accepted.ConflictId.Value
                    && conflict.EventIds.Contains(accepted.Draft.EventId)))
                throw new InvalidDataException("Conflicted archive event has no matching conflict evidence.");
        }

        foreach (var snapshot in archive.Snapshots)
        {
            if (snapshot.WorkspaceId != archive.WorkspaceId)
                throw new InvalidDataException("Archive snapshot belongs to another workspace.");

            var events = archive.Events
                .Where(item =>
                    item.Draft.ProjectId == snapshot.ProjectId
                    && item.ServerSequence <= snapshot.ThroughServerSequence)
                .OrderBy(item => item.ServerSequence)
                .ToArray();
            ProjectRecord? project = null;
            foreach (var accepted in events)
                if (accepted.Disposition == H2ProjectEventDisposition.Applied)
                    project = H2ProjectEventApplier.Apply(project, accepted.Draft);

            var expectedNode = JsonNode.Parse(JsonSerializer.Serialize(project, Json));
            var actualNode = JsonNode.Parse(snapshot.StateJson);
            if (!JsonNode.DeepEquals(expectedNode, actualNode))
                throw new InvalidDataException("Archive snapshot does not match accepted event replay.");

            var derived = DeriveRevisions(events)
                .OrderBy(item => item.StableKey, StringComparer.Ordinal)
                .ToArray();
            var recorded = snapshot.Revisions
                .OrderBy(item => item.StableKey, StringComparer.Ordinal)
                .ToArray();
            if (!SameRevisions(derived, recorded))
                throw new InvalidDataException("Archive snapshot revision metadata is inconsistent.");
        }

        foreach (var project in archive.ProjectRevisions)
        {
            var events = archive.Events
                .Where(item => item.Draft.ProjectId == project.ProjectId)
                .OrderBy(item => item.ServerSequence)
                .ToArray();
            var derived = DeriveRevisions(events)
                .OrderBy(item => item.StableKey, StringComparer.Ordinal)
                .ToArray();
            var recorded = project.Revisions
                .OrderBy(item => item.StableKey, StringComparer.Ordinal)
                .ToArray();
            if (!SameRevisions(derived, recorded))
                throw new InvalidDataException("Archive current revision metadata is inconsistent.");
        }
    }

    private static IReadOnlyList<H2ProjectRevisionEntry> DeriveRevisions(
        IReadOnlyList<H2AcceptedProjectEvent> events)
    {
        var revisions = new Dictionary<string, H2ProjectRevisionEntry>(StringComparer.Ordinal);

        foreach (var accepted in events)
        {
            if (accepted.Disposition != H2ProjectEventDisposition.Applied) continue;

            var draft = accepted.Draft;
            var resolutionAction = draft.Kind == H2ProjectEventKind.ResolveConflict
                ? H2ProjectEventPayload.Deserialize<H2ConflictResolutionPayload>(draft.PayloadJson).Action
                : (H2ConflictResolutionAction?)null;

            if ((draft.Kind == H2ProjectEventKind.SetField
                 || resolutionAction == H2ConflictResolutionAction.SetField)
                && accepted.ResultingRevision.HasValue)
            {
                var field = new H2ProjectRevisionEntry(
                    draft.Target.EntityKind,
                    draft.Target.EntityId,
                    draft.Target.FieldKey,
                    accepted.ResultingRevision.Value,
                    false);
                revisions[field.StableKey] = field;
            }

            if (accepted.ResultingEntityRevision.HasValue)
            {
                var entity = new H2ProjectRevisionEntry(
                    draft.Target.EntityKind,
                    draft.Target.EntityId,
                    null,
                    accepted.ResultingEntityRevision.Value,
                    draft.Kind == H2ProjectEventKind.DeleteEntity
                    || resolutionAction == H2ConflictResolutionAction.DeleteEntity);
                revisions[entity.StableKey] = entity;
            }
        }

        return revisions.Values.ToArray();
    }

    private static bool SameRevisions(
        IReadOnlyList<H2ProjectRevisionEntry> left,
        IReadOnlyList<H2ProjectRevisionEntry> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].StableKey, right[i].StableKey, StringComparison.Ordinal)
                || left[i].Revision != right[i].Revision
                || left[i].IsDeleted != right[i].IsDeleted)
                return false;
        }
        return true;
    }
}

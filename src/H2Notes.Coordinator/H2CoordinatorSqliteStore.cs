using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using H2Notes.Core;
using Microsoft.Data.Sqlite;

namespace H2Notes.Coordinator;

/// <summary>
/// Durable single-writer store for one H2 Sync Coordinator process.
/// The database path must be local to the Coordinator host. Clients must never open
/// this SQLite database directly through SMB/WebDAV/mapped network paths.
/// </summary>
public sealed partial class H2CoordinatorSqliteStore
{
    private const int SchemaVersion = 2;
    private static readonly TimeSpan ProjectAiLeaseDuration = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly string _connectionString;
    private readonly Func<DateTimeOffset> _utcNow;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public H2CoordinatorSqliteStore(
        string databasePath,
        Func<DateTimeOffset>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        DatabasePath = ValidateLocalDatabasePath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        };
        _connectionString = builder.ToString();

        lock (_gate)
            Initialize();
    }

    public string DatabasePath { get; }

    public static string ValidateLocalDatabasePath(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Coordinator database path is required.", nameof(databasePath));

        var full = Path.GetFullPath(databasePath.Trim());
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("Coordinator SQLite database must not be stored on a UNC/network path.", nameof(databasePath));

        if (OperatingSystem.IsWindows())
        {
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrWhiteSpace(root))
                throw new ArgumentException("Coordinator database path has no local drive root.", nameof(databasePath));

            DriveType driveType;
            try { driveType = new DriveInfo(root).DriveType; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new ArgumentException("Coordinator database drive could not be validated as local.", nameof(databasePath), ex);
            }

            if (driveType == DriveType.Network)
                throw new ArgumentException("Coordinator SQLite database must not be stored on a mapped network drive.", nameof(databasePath));
        }

        return full;
    }

    public void RegisterDevice(Guid workspaceId, H2CoordinatorDeviceIdentity device)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("WorkspaceId is required.", nameof(workspaceId));
        ArgumentNullException.ThrowIfNull(device);

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            var now = DateTimeOffset.UtcNow;

            Execute(connection, tx,
                """
                INSERT OR IGNORE INTO workspaces(workspace_id, created_utc)
                VALUES($workspace, $created);
                """,
                ("$workspace", Id(workspaceId)),
                ("$created", Stamp(now)));

            Execute(connection, tx,
                """
                INSERT INTO devices(workspace_id, device_id, display_name, registered_utc, last_seen_utc)
                VALUES($workspace, $device, $name, $now, $now)
                ON CONFLICT(workspace_id, device_id) DO UPDATE SET
                    display_name = excluded.display_name,
                    last_seen_utc = excluded.last_seen_utc;
                """,
                ("$workspace", Id(workspaceId)),
                ("$device", Id(device.DeviceId)),
                ("$name", device.DisplayName),
                ("$now", Stamp(now)));

            tx.Commit();
        }
    }

    public H2ProjectEventSubmissionResult SubmitProjectEvents(
        Guid workspaceId,
        Guid deviceId,
        IReadOnlyList<H2ProjectEventDraft> events)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("WorkspaceId is required.", nameof(workspaceId));
        if (deviceId == Guid.Empty) throw new ArgumentException("DeviceId is required.", nameof(deviceId));
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count > 256) throw new ArgumentException("Coordinator event batch is bounded to 256 items.", nameof(events));
        if (events.Count == 0) return new H2ProjectEventSubmissionResult([], []);

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            RequireRegisteredDevice(connection, tx, workspaceId, deviceId);

            var heads = new Dictionary<Guid, long>();
            var acknowledgements = new List<H2ProjectEventAcknowledgement>(events.Count);
            var conflicts = new List<H2ProjectConflict>();

            foreach (var draft in events)
            {
                if (draft.WorkspaceId != workspaceId)
                    throw new InvalidOperationException("Event WorkspaceId does not match submission workspace.");
                if (draft.DeviceId != deviceId)
                    throw new InvalidOperationException("Event DeviceId does not match submitting device.");
                if (draft.Kind == H2ProjectEventKind.ResolveConflict)
                    throw new InvalidOperationException("Conflict resolution events must use ResolveConflict so the target conflict is explicit.");

                var existing = FindEventByOperation(connection, tx, workspaceId, draft.ClientOperationId);
                if (existing is not null)
                {
                    if (existing.EventId != draft.EventId
                        || existing.ProjectId != draft.ProjectId
                        || existing.DeviceId != draft.DeviceId
                        || !string.Equals(existing.PayloadSha256, draft.PayloadSha256, StringComparison.Ordinal))
                        throw new InvalidOperationException("ClientOperationId was reused for incompatible event content.");

                    acknowledgements.Add(new H2ProjectEventAcknowledgement(
                        existing.EventId,
                        draft.ClientOperationId,
                        existing.ServerSequence,
                        existing.AcceptedUtc));
                    if (existing.ConflictId is { } existingConflictId)
                        conflicts.Add(ReadConflict(connection, tx, existingConflictId));
                    continue;
                }

                var byEvent = FindEventByEventId(connection, tx, draft.EventId);
                if (byEvent is not null)
                    throw new InvalidOperationException("EventId already exists with another client operation.");

                var byDeviceSequence = FindEventByDeviceSequence(
                    connection, tx, workspaceId, draft.DeviceId, draft.DeviceSequence);
                if (byDeviceSequence is not null)
                    throw new InvalidOperationException("DeviceSequence was reused by another accepted project event.");

                if (!heads.TryGetValue(draft.ProjectId, out var sequence))
                {
                    EnsureProjectHead(connection, tx, workspaceId, draft.ProjectId);
                    sequence = ReadProjectSequence(connection, tx, workspaceId, draft.ProjectId);
                }

                sequence++;
                heads[draft.ProjectId] = sequence;
                var acceptedUtc = DateTimeOffset.UtcNow;
                ValidateAiEventCorrelation(connection, tx, draft, _utcNow());
                var arbitration = ArbitrateMutation(connection, tx, draft);
                var draftJson = JsonSerializer.Serialize(draft, Json);

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
                    ("$workspace", Id(workspaceId)),
                    ("$project", Id(draft.ProjectId)),
                    ("$sequence", sequence),
                    ("$event", Id(draft.EventId)),
                    ("$device", Id(draft.DeviceId)),
                    ("$deviceSequence", draft.DeviceSequence),
                    ("$operation", Id(draft.ClientOperationId)),
                    ("$payloadHash", draft.PayloadSha256),
                    ("$draft", draftJson),
                    ("$accepted", Stamp(acceptedUtc)),
                    ("$disposition", (int)arbitration.Disposition),
                    ("$revision", arbitration.ResultingRevision),
                    ("$entityRevision", arbitration.ResultingEntityRevision),
                    ("$conflict", arbitration.Conflict is null ? null : Id(arbitration.Conflict.ConflictId)));

                if (arbitration.Disposition == H2ProjectEventDisposition.Applied)
                    ApplyRevisionMutation(connection, tx, draft, arbitration);
                else if (arbitration.Conflict is { } conflict)
                {
                    InsertConflict(connection, tx, conflict);
                    conflicts.Add(conflict);
                }

                acknowledgements.Add(new H2ProjectEventAcknowledgement(
                    draft.EventId,
                    draft.ClientOperationId,
                    sequence,
                    acceptedUtc));
            }

            foreach (var pair in heads)
            {
                Execute(connection, tx,
                    """
                    UPDATE project_heads
                    SET server_sequence = $sequence
                    WHERE workspace_id = $workspace AND project_id = $project;
                    """,
                    ("$sequence", pair.Value),
                    ("$workspace", Id(workspaceId)),
                    ("$project", Id(pair.Key)));
            }

            tx.Commit();
            return new H2ProjectEventSubmissionResult(acknowledgements, conflicts);
        }
    }

    public H2ProjectEventAcknowledgement ResolveConflict(
        Guid workspaceId,
        Guid conflictId,
        H2ProjectEventDraft resolutionEvent)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("WorkspaceId is required.", nameof(workspaceId));
        if (conflictId == Guid.Empty) throw new ArgumentException("ConflictId is required.", nameof(conflictId));
        ArgumentNullException.ThrowIfNull(resolutionEvent);
        if (resolutionEvent.WorkspaceId != workspaceId)
            throw new InvalidOperationException("Resolution event WorkspaceId does not match target workspace.");
        if (resolutionEvent.Kind != H2ProjectEventKind.ResolveConflict)
            throw new InvalidOperationException("Conflict resolution must use ResolveConflict event kind.");

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            RequireRegisteredDevice(connection, tx, workspaceId, resolutionEvent.DeviceId);

            var conflict = ReadConflict(connection, tx, conflictId);
            if (conflict.WorkspaceId != workspaceId
                || conflict.ProjectId != resolutionEvent.ProjectId
                || conflict.Target.EntityKind != resolutionEvent.Target.EntityKind
                || conflict.Target.EntityId != resolutionEvent.Target.EntityId)
                throw new InvalidOperationException("Resolution event does not target the selected conflict.");

            if (conflict.State == H2ProjectConflictState.Resolved)
            {
                if (conflict.ResolvedByEventId != resolutionEvent.EventId)
                    throw new InvalidOperationException("Conflict was already resolved by another event.");

                var prior = FindEventByEventId(connection, tx, resolutionEvent.EventId)
                    ?? throw new InvalidDataException("Resolved conflict references a missing resolution event.");
                if (prior.ClientOperationId != resolutionEvent.ClientOperationId
                    || prior.DeviceId != resolutionEvent.DeviceId
                    || !string.Equals(prior.PayloadSha256, resolutionEvent.PayloadSha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("Resolution retry identity/content does not match the accepted resolution event.");
                tx.Commit();
                return new H2ProjectEventAcknowledgement(
                    prior.EventId,
                    prior.ClientOperationId,
                    prior.ServerSequence,
                    prior.AcceptedUtc);
            }

            var existing = FindEventByOperation(connection, tx, workspaceId, resolutionEvent.ClientOperationId);
            if (existing is not null)
                throw new InvalidOperationException("Resolution ClientOperationId already belongs to another accepted operation.");
            if (FindEventByEventId(connection, tx, resolutionEvent.EventId) is not null)
                throw new InvalidOperationException("Resolution EventId already exists.");
            if (FindEventByDeviceSequence(
                    connection, tx, workspaceId, resolutionEvent.DeviceId, resolutionEvent.DeviceSequence) is not null)
                throw new InvalidOperationException("Resolution DeviceSequence was already used by another event.");

            var payload = Deserialize<H2ConflictResolutionPayload>(resolutionEvent.PayloadJson);
            var arbitration = ArbitrateResolution(connection, tx, conflict, resolutionEvent, payload);

            EnsureProjectHead(connection, tx, workspaceId, resolutionEvent.ProjectId);
            var sequence = ReadProjectSequence(connection, tx, workspaceId, resolutionEvent.ProjectId) + 1;
            var acceptedUtc = DateTimeOffset.UtcNow;

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
                    $disposition, $revision, $entityRevision, NULL);
                """,
                ("$workspace", Id(workspaceId)),
                ("$project", Id(resolutionEvent.ProjectId)),
                ("$sequence", sequence),
                ("$event", Id(resolutionEvent.EventId)),
                ("$device", Id(resolutionEvent.DeviceId)),
                ("$deviceSequence", resolutionEvent.DeviceSequence),
                ("$operation", Id(resolutionEvent.ClientOperationId)),
                ("$payloadHash", resolutionEvent.PayloadSha256),
                ("$draft", JsonSerializer.Serialize(resolutionEvent, Json)),
                ("$accepted", Stamp(acceptedUtc)),
                ("$disposition", (int)H2ProjectEventDisposition.Applied),
                ("$revision", arbitration.ResultingRevision),
                ("$entityRevision", arbitration.ResultingEntityRevision));

            ApplyResolutionRevision(connection, tx, resolutionEvent, payload, arbitration);

            Execute(connection, tx,
                """
                UPDATE project_conflicts
                SET state = $resolved, resolved_event_id = $event
                WHERE conflict_id = $conflict AND state = $open;
                """,
                ("$resolved", (int)H2ProjectConflictState.Resolved),
                ("$event", Id(resolutionEvent.EventId)),
                ("$conflict", Id(conflictId)),
                ("$open", (int)H2ProjectConflictState.Open));

            Execute(connection, tx,
                """
                UPDATE project_heads
                SET server_sequence = $sequence
                WHERE workspace_id = $workspace AND project_id = $project;
                """,
                ("$sequence", sequence),
                ("$workspace", Id(workspaceId)),
                ("$project", Id(resolutionEvent.ProjectId)));

            tx.Commit();
            return new H2ProjectEventAcknowledgement(
                resolutionEvent.EventId,
                resolutionEvent.ClientOperationId,
                sequence,
                acceptedUtc);
        }
    }

    public H2ProjectHead GetProjectHead(Guid workspaceId, Guid projectId)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("WorkspaceId is required.", nameof(workspaceId));
        if (projectId == Guid.Empty) throw new ArgumentException("ProjectId is required.", nameof(projectId));

        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    COALESCE(h.server_sequence, 0),
                    COALESCE(h.snapshot_through_sequence, 0),
                    (SELECT COUNT(*) FROM project_conflicts c
                     WHERE c.workspace_id = $workspace
                       AND c.project_id = $project
                       AND c.state = $open)
                FROM (SELECT 1) seed
                LEFT JOIN project_heads h
                  ON h.workspace_id = $workspace AND h.project_id = $project;
                """;
            command.Parameters.AddWithValue("$workspace", Id(workspaceId));
            command.Parameters.AddWithValue("$project", Id(projectId));
            command.Parameters.AddWithValue("$open", (int)H2ProjectConflictState.Open);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("Could not read project head.");
            return new H2ProjectHead(
                workspaceId,
                projectId,
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt32(2));
        }
    }

    public IReadOnlyList<H2AcceptedProjectEvent> GetProjectEvents(
        Guid workspaceId,
        Guid projectId,
        long afterServerSequence,
        int limit = 500)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("WorkspaceId is required.", nameof(workspaceId));
        if (projectId == Guid.Empty) throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (afterServerSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterServerSequence));
        if (limit is < 1 or > 2_000) throw new ArgumentOutOfRangeException(nameof(limit));

        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    server_sequence, draft_json, accepted_utc,
                    disposition, resulting_revision, resulting_entity_revision, conflict_id
                FROM (
                    SELECT
                        workspace_id, project_id, server_sequence, draft_json, accepted_utc,
                        disposition, resulting_revision, resulting_entity_revision, conflict_id
                    FROM project_event_archive
                    UNION ALL
                    SELECT
                        workspace_id, project_id, server_sequence, draft_json, accepted_utc,
                        disposition, resulting_revision, resulting_entity_revision, conflict_id
                    FROM project_events
                ) e
                WHERE workspace_id = $workspace
                  AND project_id = $project
                  AND server_sequence > $after
                ORDER BY server_sequence
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$workspace", Id(workspaceId));
            command.Parameters.AddWithValue("$project", Id(projectId));
            command.Parameters.AddWithValue("$after", afterServerSequence);
            command.Parameters.AddWithValue("$limit", limit);

            var result = new List<H2AcceptedProjectEvent>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var draft = Deserialize<H2ProjectEventDraft>(reader.GetString(1));
                result.Add(new H2AcceptedProjectEvent(
                    draft,
                    reader.GetInt64(0),
                    ParseStamp(reader.GetString(2)),
                    (H2ProjectEventDisposition)reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6))));
            }
            return result;
        }
    }

    public void SaveSnapshot(H2ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            EnsureProjectHead(connection, tx, snapshot.WorkspaceId, snapshot.ProjectId);
            var head = ReadProjectSequence(connection, tx, snapshot.WorkspaceId, snapshot.ProjectId);
            if (snapshot.ThroughServerSequence != head)
                throw new InvalidOperationException("Verified snapshot must be created exactly at the current accepted project head.");

            VerifySnapshotAgainstEventLog(connection, tx, snapshot);

            using (var check = connection.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText =
                    """
                    SELECT state_sha256
                    FROM project_snapshots
                    WHERE workspace_id = $workspace
                      AND project_id = $project
                      AND through_sequence = $sequence;
                    """;
                check.Parameters.AddWithValue("$workspace", Id(snapshot.WorkspaceId));
                check.Parameters.AddWithValue("$project", Id(snapshot.ProjectId));
                check.Parameters.AddWithValue("$sequence", snapshot.ThroughServerSequence);
                var existing = check.ExecuteScalar() as string;
                if (existing is not null && !string.Equals(existing, snapshot.StateSha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("Snapshot sequence already exists with different state hash.");
            }

            Execute(connection, tx,
                """
                INSERT OR IGNORE INTO project_snapshots(
                    workspace_id, project_id, through_sequence,
                    state_sha256, snapshot_json, created_utc)
                VALUES($workspace, $project, $sequence, $hash, $snapshot, $created);
                """,
                ("$workspace", Id(snapshot.WorkspaceId)),
                ("$project", Id(snapshot.ProjectId)),
                ("$sequence", snapshot.ThroughServerSequence),
                ("$hash", snapshot.StateSha256),
                ("$snapshot", JsonSerializer.Serialize(snapshot, Json)),
                ("$created", Stamp(snapshot.CreatedUtc)));

            Execute(connection, tx,
                """
                UPDATE project_heads
                SET snapshot_through_sequence =
                    CASE
                        WHEN snapshot_through_sequence < $sequence THEN $sequence
                        ELSE snapshot_through_sequence
                    END
                WHERE workspace_id = $workspace AND project_id = $project;
                """,
                ("$sequence", snapshot.ThroughServerSequence),
                ("$workspace", Id(snapshot.WorkspaceId)),
                ("$project", Id(snapshot.ProjectId)));

            tx.Commit();
        }
    }

    public (int Hot, int Archived) GetProjectEventStorageCounts(Guid workspaceId, Guid projectId)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("WorkspaceId is required.", nameof(workspaceId));
        if (projectId == Guid.Empty) throw new ArgumentException("ProjectId is required.", nameof(projectId));

        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    (SELECT COUNT(*) FROM project_events
                     WHERE workspace_id = $workspace AND project_id = $project),
                    (SELECT COUNT(*) FROM project_event_archive
                     WHERE workspace_id = $workspace AND project_id = $project);
                """;
            command.Parameters.AddWithValue("$workspace", Id(workspaceId));
            command.Parameters.AddWithValue("$project", Id(projectId));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("Could not read project event storage counts.");
            return (reader.GetInt32(0), reader.GetInt32(1));
        }
    }

    public int CompactProjectEventsThrough(
        Guid workspaceId,
        Guid projectId,
        long throughServerSequence)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("WorkspaceId is required.", nameof(workspaceId));
        if (projectId == Guid.Empty) throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (throughServerSequence <= 0) throw new ArgumentOutOfRangeException(nameof(throughServerSequence));

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();

            using (var snapshot = connection.CreateCommand())
            {
                snapshot.Transaction = tx;
                snapshot.CommandText =
                    """
                    SELECT MAX(through_sequence)
                    FROM project_snapshots
                    WHERE workspace_id = $workspace AND project_id = $project;
                    """;
                snapshot.Parameters.AddWithValue("$workspace", Id(workspaceId));
                snapshot.Parameters.AddWithValue("$project", Id(projectId));
                var value = snapshot.ExecuteScalar();
                var snapshotThrough = value is null or DBNull
                    ? 0
                    : Convert.ToInt64(value, CultureInfo.InvariantCulture);
                if (snapshotThrough < throughServerSequence)
                    throw new InvalidOperationException(
                        "Project events cannot be compacted beyond the latest verified snapshot.");
            }

            var moved = Convert.ToInt32(ScalarInt64(
                connection,
                tx,
                """
                SELECT COUNT(*)
                FROM project_events
                WHERE workspace_id = $workspace
                  AND project_id = $project
                  AND server_sequence <= $sequence;
                """,
                ("$workspace", Id(workspaceId)),
                ("$project", Id(projectId)),
                ("$sequence", throughServerSequence)),
                CultureInfo.InvariantCulture);

            Execute(connection, tx,
                """
                INSERT OR IGNORE INTO project_event_archive(
                    workspace_id, project_id, server_sequence,
                    event_id, device_id, device_sequence, client_operation_id,
                    payload_sha256, draft_json, accepted_utc,
                    disposition, resulting_revision, resulting_entity_revision, conflict_id)
                SELECT
                    workspace_id, project_id, server_sequence,
                    event_id, device_id, device_sequence, client_operation_id,
                    payload_sha256, draft_json, accepted_utc,
                    disposition, resulting_revision, resulting_entity_revision, conflict_id
                FROM project_events
                WHERE workspace_id = $workspace
                  AND project_id = $project
                  AND server_sequence <= $sequence;
                """,
                ("$workspace", Id(workspaceId)),
                ("$project", Id(projectId)),
                ("$sequence", throughServerSequence));

            Execute(connection, tx,
                """
                DELETE FROM project_events
                WHERE workspace_id = $workspace
                  AND project_id = $project
                  AND server_sequence <= $sequence;
                """,
                ("$workspace", Id(workspaceId)),
                ("$project", Id(projectId)),
                ("$sequence", throughServerSequence));

            tx.Commit();
            return moved;
        }
    }

    public H2ProjectSnapshot? GetLatestSnapshot(Guid workspaceId, Guid projectId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT snapshot_json
                FROM project_snapshots
                WHERE workspace_id = $workspace AND project_id = $project
                ORDER BY through_sequence DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$workspace", Id(workspaceId));
            command.Parameters.AddWithValue("$project", Id(projectId));
            var json = command.ExecuteScalar() as string;
            return json is null ? null : Deserialize<H2ProjectSnapshot>(json);
        }
    }

    public void SaveConflict(H2ProjectConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            EnsureProjectHead(connection, tx, conflict.WorkspaceId, conflict.ProjectId);

            using (var check = connection.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT conflict_id FROM project_conflicts WHERE conflict_id = $id;";
                check.Parameters.AddWithValue("$id", Id(conflict.ConflictId));
                if (check.ExecuteScalar() is not null)
                {
                    tx.Rollback();
                    return;
                }
            }

            Execute(connection, tx,
                """
                INSERT INTO project_conflicts(
                    conflict_id, workspace_id, project_id,
                    entity_kind, entity_id, field_key, expected_revision,
                    event_ids_json, created_utc, state, resolved_event_id)
                VALUES(
                    $conflict, $workspace, $project,
                    $entityKind, $entity, $field, $revision,
                    $events, $created, $state, $resolved);
                """,
                ("$conflict", Id(conflict.ConflictId)),
                ("$workspace", Id(conflict.WorkspaceId)),
                ("$project", Id(conflict.ProjectId)),
                ("$entityKind", (int)conflict.Target.EntityKind),
                ("$entity", Id(conflict.Target.EntityId)),
                ("$field", conflict.Target.FieldKey),
                ("$revision", conflict.Target.ExpectedRevision),
                ("$events", JsonSerializer.Serialize(conflict.EventIds, Json)),
                ("$created", Stamp(conflict.CreatedUtc)),
                ("$state", (int)conflict.State),
                ("$resolved", conflict.ResolvedByEventId is null ? null : Id(conflict.ResolvedByEventId.Value)));

            tx.Commit();
        }
    }

    public IReadOnlyList<H2ProjectConflict> GetConflicts(Guid workspaceId, Guid projectId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    conflict_id, entity_kind, entity_id, field_key, expected_revision,
                    event_ids_json, created_utc, state, resolved_event_id
                FROM project_conflicts
                WHERE workspace_id = $workspace AND project_id = $project
                ORDER BY created_utc, conflict_id;
                """;
            command.Parameters.AddWithValue("$workspace", Id(workspaceId));
            command.Parameters.AddWithValue("$project", Id(projectId));

            var result = new List<H2ProjectConflict>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var target = new H2ProjectMutationTarget(
                    (H2ProjectEntityKind)reader.GetInt32(1),
                    Guid.Parse(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4));
                var eventIds = JsonSerializer.Deserialize<Guid[]>(reader.GetString(5), Json)
                    ?? throw new InvalidDataException("Conflict event identity list is missing.");
                result.Add(new H2ProjectConflict(
                    Guid.Parse(reader.GetString(0)),
                    workspaceId,
                    projectId,
                    target,
                    eventIds,
                    ParseStamp(reader.GetString(6)),
                    (H2ProjectConflictState)reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8))));
            }
            return result;
        }
    }

    public H2QueuedProjectAiRequest EnqueueProjectAi(H2ProjectAiRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            RequireRegisteredDevice(connection, tx, request.WorkspaceId, request.OwnerDeviceId);
            EnsureProjectHead(connection, tx, request.WorkspaceId, request.ProjectId);

            using (var existingCommand = connection.CreateCommand())
            {
                existingCommand.Transaction = tx;
                existingCommand.CommandText =
                    """
                    SELECT request_id, queue_sequence, request_json, state, accepted_utc
                    FROM ai_queue
                    WHERE workspace_id = $workspace AND client_request_id = $clientRequest;
                    """;
                existingCommand.Parameters.AddWithValue("$workspace", Id(request.WorkspaceId));
                existingCommand.Parameters.AddWithValue("$clientRequest", Id(request.ClientRequestId));
                using var reader = existingCommand.ExecuteReader();
                if (reader.Read())
                {
                    var existingRequestId = Guid.Parse(reader.GetString(0));
                    var existing = Deserialize<H2ProjectAiRequest>(reader.GetString(2));
                    if (existingRequestId != request.RequestId
                        || existing.ProjectId != request.ProjectId
                        || existing.OwnerDeviceId != request.OwnerDeviceId)
                        throw new InvalidOperationException("ClientRequestId was reused for incompatible AI request content.");

                    var queued = new H2QueuedProjectAiRequest(
                        existing,
                        reader.GetInt64(1),
                        (H2ProjectAiQueueState)reader.GetInt32(3),
                        ParseStamp(reader.GetString(4)));
                    tx.Commit();
                    return queued;
                }
            }

            var queueSequence = ScalarInt64(connection, tx,
                """
                SELECT COALESCE(MAX(queue_sequence), 0) + 1
                FROM ai_queue
                WHERE workspace_id = $workspace AND project_id = $project;
                """,
                ("$workspace", Id(request.WorkspaceId)),
                ("$project", Id(request.ProjectId)));
            var acceptedUtc = DateTimeOffset.UtcNow;

            Execute(connection, tx,
                """
                INSERT INTO ai_queue(
                    workspace_id, project_id, queue_sequence,
                    request_id, client_request_id, owner_device_id,
                    request_json, state, accepted_utc, lease_json, completion_json)
                VALUES(
                    $workspace, $project, $sequence,
                    $request, $clientRequest, $device,
                    $json, $state, $accepted, NULL, NULL);
                """,
                ("$workspace", Id(request.WorkspaceId)),
                ("$project", Id(request.ProjectId)),
                ("$sequence", queueSequence),
                ("$request", Id(request.RequestId)),
                ("$clientRequest", Id(request.ClientRequestId)),
                ("$device", Id(request.OwnerDeviceId)),
                ("$json", JsonSerializer.Serialize(request, Json)),
                ("$state", (int)H2ProjectAiQueueState.Waiting),
                ("$accepted", Stamp(acceptedUtc)));

            tx.Commit();
            return new H2QueuedProjectAiRequest(
                request,
                queueSequence,
                H2ProjectAiQueueState.Waiting,
                acceptedUtc);
        }
    }

    public IReadOnlyList<H2QueuedProjectAiRequest> GetProjectAiQueue(
        Guid workspaceId,
        Guid projectId,
        int limit = 100)
    {
        if (limit is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(limit));

        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT queue_sequence, request_json, state, accepted_utc
                FROM ai_queue
                WHERE workspace_id = $workspace AND project_id = $project
                ORDER BY queue_sequence
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$workspace", Id(workspaceId));
            command.Parameters.AddWithValue("$project", Id(projectId));
            command.Parameters.AddWithValue("$limit", limit);

            var result = new List<H2QueuedProjectAiRequest>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new H2QueuedProjectAiRequest(
                    Deserialize<H2ProjectAiRequest>(reader.GetString(1)),
                    reader.GetInt64(0),
                    (H2ProjectAiQueueState)reader.GetInt32(2),
                    ParseStamp(reader.GetString(3))));
            }
            return result;
        }
    }

    public H2ProjectAiLease? TryAcquireProjectAiLease(
        Guid workspaceId,
        Guid projectId,
        Guid deviceId)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("WorkspaceId is required.", nameof(workspaceId));
        if (projectId == Guid.Empty) throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (deviceId == Guid.Empty) throw new ArgumentException("DeviceId is required.", nameof(deviceId));

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            RequireRegisteredDevice(connection, tx, workspaceId, deviceId);
            EnsureProjectHead(connection, tx, workspaceId, projectId);
            var now = ServerNow();
            RecoverExpiredProjectAiUnderLock(connection, tx, workspaceId, projectId, now);

            using (var blocking = connection.CreateCommand())
            {
                blocking.Transaction = tx;
                blocking.CommandText =
                    """
                    SELECT 1
                    FROM ai_queue
                    WHERE workspace_id = $workspace
                      AND project_id = $project
                      AND state IN ($waitingSync, $running, $repair, $review)
                    LIMIT 1;
                    """;
                blocking.Parameters.AddWithValue("$workspace", Id(workspaceId));
                blocking.Parameters.AddWithValue("$project", Id(projectId));
                blocking.Parameters.AddWithValue("$waitingSync", (int)H2ProjectAiQueueState.WaitingForSync);
                blocking.Parameters.AddWithValue("$running", (int)H2ProjectAiQueueState.Running);
                blocking.Parameters.AddWithValue("$repair", (int)H2ProjectAiQueueState.WaitingForRepair);
                blocking.Parameters.AddWithValue("$review", (int)H2ProjectAiQueueState.NeedsUserReview);
                if (blocking.ExecuteScalar() is not null)
                {
                    tx.Commit();
                    return null;
                }
            }

            H2ProjectAiRequest? request = null;
            long queueSequence = 0;
            using (var first = connection.CreateCommand())
            {
                first.Transaction = tx;
                first.CommandText =
                    """
                    SELECT queue_sequence, request_json
                    FROM ai_queue
                    WHERE workspace_id = $workspace
                      AND project_id = $project
                      AND state = $waiting
                    ORDER BY queue_sequence
                    LIMIT 1;
                    """;
                first.Parameters.AddWithValue("$workspace", Id(workspaceId));
                first.Parameters.AddWithValue("$project", Id(projectId));
                first.Parameters.AddWithValue("$waiting", (int)H2ProjectAiQueueState.Waiting);
                using var reader = first.ExecuteReader();
                if (reader.Read())
                {
                    queueSequence = reader.GetInt64(0);
                    request = Deserialize<H2ProjectAiRequest>(reader.GetString(1));
                }
            }

            if (request is null || request.OwnerDeviceId != deviceId)
            {
                tx.Commit();
                return null;
            }

            var barrier = new H2ProjectSyncBarrier(
                projectId,
                ReadProjectSequence(connection, tx, workspaceId, projectId));
            var lease = new H2ProjectAiLease(
                Guid.NewGuid(),
                request.RequestId,
                projectId,
                deviceId,
                queueSequence,
                barrier,
                now,
                now,
                now + ProjectAiLeaseDuration);

            var changed = Execute(connection, tx,
                """
                UPDATE ai_queue
                SET state = $state, lease_json = $lease
                WHERE workspace_id = $workspace
                  AND project_id = $project
                  AND queue_sequence = $queue
                  AND state = $waiting;
                """,
                ("$state", (int)H2ProjectAiQueueState.WaitingForSync),
                ("$lease", JsonSerializer.Serialize(lease, Json)),
                ("$workspace", Id(workspaceId)),
                ("$project", Id(projectId)),
                ("$queue", queueSequence),
                ("$waiting", (int)H2ProjectAiQueueState.Waiting));
            if (changed != 1)
                throw new InvalidOperationException("AI queue head changed while acquiring lease.");

            tx.Commit();
            return lease;
        }
    }

    public bool ConfirmProjectAiBarrier(
        Guid leaseId,
        Guid deviceId,
        long observedProjectSequence)
    {
        if (leaseId == Guid.Empty) throw new ArgumentException("LeaseId is required.", nameof(leaseId));
        if (deviceId == Guid.Empty) throw new ArgumentException("DeviceId is required.", nameof(deviceId));
        if (observedProjectSequence < 0) throw new ArgumentOutOfRangeException(nameof(observedProjectSequence));

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            var row = FindAiQueueByLease(connection, tx, leaseId);
            if (row is null || row.Lease.OwnerDeviceId != deviceId)
            {
                tx.Commit();
                return false;
            }

            var now = ServerNow();
            RecoverExpiredProjectAiUnderLock(
                connection, tx, row.WorkspaceId, row.ProjectId, now);
            row = FindAiQueueByLease(connection, tx, leaseId);
            if (row is null
                || row.State != H2ProjectAiQueueState.WaitingForSync
                || row.Lease.OwnerDeviceId != deviceId
                || observedProjectSequence < row.Lease.Barrier.RequiredProjectSequence)
            {
                tx.Commit();
                return false;
            }

            var refreshed = RefreshLease(row.Lease, now);
            var changed = Execute(connection, tx,
                """
                UPDATE ai_queue
                SET state = $running, lease_json = $lease
                WHERE request_id = $request AND state = $waitingSync;
                """,
                ("$running", (int)H2ProjectAiQueueState.Running),
                ("$lease", JsonSerializer.Serialize(refreshed, Json)),
                ("$request", Id(row.Request.RequestId)),
                ("$waitingSync", (int)H2ProjectAiQueueState.WaitingForSync));
            tx.Commit();
            return changed == 1;
        }
    }

    public bool HeartbeatProjectAiLease(
        Guid leaseId,
        Guid deviceId,
        DateTimeOffset clientHeartbeatUtc)
    {
        if (leaseId == Guid.Empty) throw new ArgumentException("LeaseId is required.", nameof(leaseId));
        if (deviceId == Guid.Empty) throw new ArgumentException("DeviceId is required.", nameof(deviceId));
        _ = clientHeartbeatUtc; // diagnostic-only client time; server clock is authoritative.

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            var row = FindAiQueueByLease(connection, tx, leaseId);
            if (row is null || row.Lease.OwnerDeviceId != deviceId)
            {
                tx.Commit();
                return false;
            }

            var now = ServerNow();
            RecoverExpiredProjectAiUnderLock(
                connection, tx, row.WorkspaceId, row.ProjectId, now);
            row = FindAiQueueByLease(connection, tx, leaseId);
            if (row is null
                || row.Lease.OwnerDeviceId != deviceId
                || (row.State != H2ProjectAiQueueState.WaitingForSync
                    && row.State != H2ProjectAiQueueState.Running))
            {
                tx.Commit();
                return false;
            }

            var refreshed = RefreshLease(row.Lease, now);
            var changed = Execute(connection, tx,
                """
                UPDATE ai_queue
                SET lease_json = $lease
                WHERE request_id = $request;
                """,
                ("$lease", JsonSerializer.Serialize(refreshed, Json)),
                ("$request", Id(row.Request.RequestId)));
            tx.Commit();
            return changed == 1;
        }
    }

    public bool ResolveInterruptedProjectAi(
        Guid leaseId,
        Guid deviceId,
        H2ProjectAiQueueState terminalState)
    {
        if (terminalState != H2ProjectAiQueueState.Abandoned
            && terminalState != H2ProjectAiQueueState.Failed
            && terminalState != H2ProjectAiQueueState.Cancelled)
            throw new ArgumentException(
                "Interrupted AI resolution must be Abandoned, Failed or Cancelled.",
                nameof(terminalState));

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            var row = FindAiQueueByLease(connection, tx, leaseId);
            if (row is null
                || row.Lease.OwnerDeviceId != deviceId
                || (row.State != H2ProjectAiQueueState.WaitingForRepair
                    && row.State != H2ProjectAiQueueState.NeedsUserReview))
            {
                tx.Commit();
                return false;
            }

            var now = ServerNow();
            var head = ReadProjectSequence(connection, tx, row.WorkspaceId, row.ProjectId);
            var completion = new H2ProjectAiCompletion(
                row.Lease.LeaseId,
                row.Request.RequestId,
                row.ProjectId,
                terminalState,
                head,
                null,
                now);

            var changed = Execute(connection, tx,
                """
                UPDATE ai_queue
                SET state = $state, lease_json = NULL, completion_json = $completion
                WHERE request_id = $request;
                """,
                ("$state", (int)terminalState),
                ("$completion", JsonSerializer.Serialize(completion, Json)),
                ("$request", Id(row.Request.RequestId)));
            tx.Commit();
            return changed == 1;
        }
    }

    private DateTimeOffset ServerNow()
        => _utcNow().ToUniversalTime();

    private static H2ProjectAiLease RefreshLease(
        H2ProjectAiLease lease,
        DateTimeOffset serverNow)
        => new(
            lease.LeaseId,
            lease.RequestId,
            lease.ProjectId,
            lease.OwnerDeviceId,
            lease.QueueSequence,
            lease.Barrier,
            lease.GrantedUtc,
            serverNow,
            serverNow + ProjectAiLeaseDuration);

    private static AiQueueLeaseRow? FindAiQueueByLease(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid leaseId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            SELECT workspace_id, project_id, request_json, state, lease_json
            FROM ai_queue
            WHERE lease_json IS NOT NULL
              AND state IN ($waitingSync, $running, $repair, $review)
            ORDER BY workspace_id, project_id, queue_sequence;
            """;
        command.Parameters.AddWithValue("$waitingSync", (int)H2ProjectAiQueueState.WaitingForSync);
        command.Parameters.AddWithValue("$running", (int)H2ProjectAiQueueState.Running);
        command.Parameters.AddWithValue("$repair", (int)H2ProjectAiQueueState.WaitingForRepair);
        command.Parameters.AddWithValue("$review", (int)H2ProjectAiQueueState.NeedsUserReview);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var lease = Deserialize<H2ProjectAiLease>(reader.GetString(4));
            if (lease.LeaseId != leaseId) continue;
            return new AiQueueLeaseRow(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                Deserialize<H2ProjectAiRequest>(reader.GetString(2)),
                (H2ProjectAiQueueState)reader.GetInt32(3),
                lease);
        }
        return null;
    }

    private static void RecoverExpiredProjectAiUnderLock(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid workspaceId,
        Guid projectId,
        DateTimeOffset serverNow)
    {
        var rows = new List<(H2ProjectAiRequest Request, H2ProjectAiQueueState State, H2ProjectAiLease Lease)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText =
                """
                SELECT request_json, state, lease_json
                FROM ai_queue
                WHERE workspace_id = $workspace
                  AND project_id = $project
                  AND state IN ($waitingSync, $running)
                  AND lease_json IS NOT NULL
                ORDER BY queue_sequence;
                """;
            command.Parameters.AddWithValue("$workspace", Id(workspaceId));
            command.Parameters.AddWithValue("$project", Id(projectId));
            command.Parameters.AddWithValue("$waitingSync", (int)H2ProjectAiQueueState.WaitingForSync);
            command.Parameters.AddWithValue("$running", (int)H2ProjectAiQueueState.Running);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add((
                    Deserialize<H2ProjectAiRequest>(reader.GetString(0)),
                    (H2ProjectAiQueueState)reader.GetInt32(1),
                    Deserialize<H2ProjectAiLease>(reader.GetString(2))));
        }

        foreach (var row in rows.Where(item => item.Lease.ExpiresUtc <= serverNow))
        {
            if (row.State == H2ProjectAiQueueState.WaitingForSync)
            {
                var head = ReadProjectSequence(connection, tx, workspaceId, projectId);
                var completion = new H2ProjectAiCompletion(
                    row.Lease.LeaseId,
                    row.Request.RequestId,
                    projectId,
                    H2ProjectAiQueueState.Abandoned,
                    head,
                    null,
                    serverNow);
                Execute(connection, tx,
                    """
                    UPDATE ai_queue
                    SET state = $abandoned, lease_json = NULL, completion_json = $completion
                    WHERE request_id = $request AND state = $waitingSync;
                    """,
                    ("$abandoned", (int)H2ProjectAiQueueState.Abandoned),
                    ("$completion", JsonSerializer.Serialize(completion, Json)),
                    ("$request", Id(row.Request.RequestId)),
                    ("$waitingSync", (int)H2ProjectAiQueueState.WaitingForSync));
            }
            else
            {
                Execute(connection, tx,
                    """
                    UPDATE ai_queue
                    SET state = $repair
                    WHERE request_id = $request AND state = $running;
                    """,
                    ("$repair", (int)H2ProjectAiQueueState.WaitingForRepair),
                    ("$request", Id(row.Request.RequestId)),
                    ("$running", (int)H2ProjectAiQueueState.Running));
            }
        }
    }

    public void SaveProjectAiLease(H2ProjectAiLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();

            var changed = Execute(connection, tx,
                """
                UPDATE ai_queue
                SET state = $running, lease_json = $lease
                WHERE request_id = $request
                  AND project_id = $project
                  AND owner_device_id = $device
                  AND queue_sequence = $queue;
                """,
                ("$running", (int)H2ProjectAiQueueState.Running),
                ("$lease", JsonSerializer.Serialize(lease, Json)),
                ("$request", Id(lease.RequestId)),
                ("$project", Id(lease.ProjectId)),
                ("$device", Id(lease.OwnerDeviceId)),
                ("$queue", lease.QueueSequence));

            if (changed != 1)
                throw new InvalidOperationException("AI lease does not match a durable queued request.");

            tx.Commit();
        }
    }

    public H2ProjectAiLease? GetRunningProjectAiLease(Guid workspaceId, Guid projectId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT lease_json
                FROM ai_queue
                WHERE workspace_id = $workspace
                  AND project_id = $project
                  AND state = $running
                  AND lease_json IS NOT NULL
                ORDER BY queue_sequence
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$workspace", Id(workspaceId));
            command.Parameters.AddWithValue("$project", Id(projectId));
            command.Parameters.AddWithValue("$running", (int)H2ProjectAiQueueState.Running);
            var json = command.ExecuteScalar() as string;
            return json is null ? null : Deserialize<H2ProjectAiLease>(json);
        }
    }

    public void CompleteProjectAiUncheckedLegacy(H2ProjectAiCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();

            using var select = connection.CreateCommand();
            select.Transaction = tx;
            select.CommandText =
                """
                SELECT lease_json
                FROM ai_queue
                WHERE request_id = $request AND project_id = $project;
                """;
            select.Parameters.AddWithValue("$request", Id(completion.RequestId));
            select.Parameters.AddWithValue("$project", Id(completion.ProjectId));
            var leaseJson = select.ExecuteScalar() as string
                ?? throw new InvalidOperationException("AI completion has no active durable lease.");
            var lease = Deserialize<H2ProjectAiLease>(leaseJson);
            if (lease.LeaseId != completion.LeaseId)
                throw new InvalidOperationException("AI completion lease identity does not match durable lease.");

            var changed = Execute(connection, tx,
                """
                UPDATE ai_queue
                SET state = $state, lease_json = NULL, completion_json = $completion
                WHERE request_id = $request AND project_id = $project;
                """,
                ("$state", (int)completion.TerminalState),
                ("$completion", JsonSerializer.Serialize(completion, Json)),
                ("$request", Id(completion.RequestId)),
                ("$project", Id(completion.ProjectId)));
            if (changed != 1) throw new InvalidOperationException("AI completion target was not updated.");

            tx.Commit();
        }
    }

    public IReadOnlyList<H2ProjectRevisionEntry> GetProjectRevisions(Guid workspaceId, Guid projectId)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("WorkspaceId is required.", nameof(workspaceId));
        if (projectId == Guid.Empty) throw new ArgumentException("ProjectId is required.", nameof(projectId));

        lock (_gate)
        {
            using var connection = Open();
            return ReadProjectRevisions(connection, null, workspaceId, projectId);
        }
    }

    private static IReadOnlyList<H2ProjectRevisionEntry> ReadProjectRevisions(
        SqliteConnection connection,
        SqliteTransaction? tx,
        Guid workspaceId,
        Guid projectId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            SELECT entity_kind, entity_id, field_key, revision, is_deleted
            FROM project_revisions
            WHERE workspace_id = $workspace AND project_id = $project
            ORDER BY entity_kind, entity_id, field_key;
            """;
        command.Parameters.AddWithValue("$workspace", Id(workspaceId));
        command.Parameters.AddWithValue("$project", Id(projectId));

        var result = new List<H2ProjectRevisionEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new H2ProjectRevisionEntry(
                (H2ProjectEntityKind)reader.GetInt32(0),
                Guid.Parse(reader.GetString(1)),
                FromDbFieldKey(reader.GetString(2)),
                reader.GetInt64(3),
                reader.GetInt32(4) != 0));
        }
        return result;
    }

    private static void VerifySnapshotAgainstEventLog(
        SqliteConnection connection,
        SqliteTransaction tx,
        H2ProjectSnapshot snapshot)
    {
        ProjectRecord? rebuilt = null;

        using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText =
                """
                SELECT draft_json, disposition
                FROM (
                    SELECT workspace_id, project_id, server_sequence, draft_json, disposition
                    FROM project_event_archive
                    UNION ALL
                    SELECT workspace_id, project_id, server_sequence, draft_json, disposition
                    FROM project_events
                ) e
                WHERE workspace_id = $workspace
                  AND project_id = $project
                  AND server_sequence <= $sequence
                ORDER BY server_sequence;
                """;
            command.Parameters.AddWithValue("$workspace", Id(snapshot.WorkspaceId));
            command.Parameters.AddWithValue("$project", Id(snapshot.ProjectId));
            command.Parameters.AddWithValue("$sequence", snapshot.ThroughServerSequence);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var disposition = (H2ProjectEventDisposition)reader.GetInt32(1);
                if (disposition != H2ProjectEventDisposition.Applied) continue;
                var draft = Deserialize<H2ProjectEventDraft>(reader.GetString(0));
                rebuilt = H2ProjectEventApplier.Apply(rebuilt, draft);
            }
        }

        var expectedNode = JsonNode.Parse(JsonSerializer.Serialize(rebuilt, Json));
        var actualNode = JsonNode.Parse(snapshot.StateJson);
        if (!JsonNode.DeepEquals(expectedNode, actualNode))
            throw new InvalidDataException("Snapshot state does not equal replay of accepted applied project events.");

        var currentRevisions = ReadProjectRevisions(
            connection, tx, snapshot.WorkspaceId, snapshot.ProjectId)
            .OrderBy(item => item.StableKey, StringComparer.Ordinal)
            .ToArray();
        var snapshotRevisions = snapshot.Revisions
            .OrderBy(item => item.StableKey, StringComparer.Ordinal)
            .ToArray();

        if (currentRevisions.Length != snapshotRevisions.Length)
            throw new InvalidDataException("Snapshot revision metadata does not match current Coordinator revisions.");

        for (var i = 0; i < currentRevisions.Length; i++)
        {
            var expected = currentRevisions[i];
            var actual = snapshotRevisions[i];
            if (!string.Equals(expected.StableKey, actual.StableKey, StringComparison.Ordinal)
                || expected.Revision != actual.Revision
                || expected.IsDeleted != actual.IsDeleted)
                throw new InvalidDataException("Snapshot revision metadata does not match current Coordinator revisions.");
        }
    }

    private static MutationArbitration ArbitrateResolution(
        SqliteConnection connection,
        SqliteTransaction tx,
        H2ProjectConflict conflict,
        H2ProjectEventDraft resolutionEvent,
        H2ConflictResolutionPayload payload)
    {
        if (!Enum.IsDefined(payload.Action))
            throw new InvalidDataException("Conflict resolution action is invalid.");

        var entity = ReadRevision(
            connection, tx,
            resolutionEvent.WorkspaceId,
            resolutionEvent.ProjectId,
            resolutionEvent.Target.EntityKind,
            resolutionEvent.Target.EntityId,
            null)
            ?? throw new InvalidOperationException("Resolution target entity does not exist.");

        switch (payload.Action)
        {
            case H2ConflictResolutionAction.SetField:
            {
                if (string.IsNullOrWhiteSpace(resolutionEvent.Target.FieldKey)
                    || !string.Equals(
                        resolutionEvent.Target.FieldKey,
                        conflict.Target.FieldKey,
                        StringComparison.Ordinal))
                    throw new InvalidOperationException("SetField resolution must target the conflicting field.");
                if (payload.ValueJson is null)
                    throw new InvalidDataException("SetField resolution requires ValueJson.");
                if (entity.IsDeleted)
                    throw new InvalidOperationException("Cannot set a field on a deleted conflict target.");

                var field = ReadRevision(
                    connection, tx,
                    resolutionEvent.WorkspaceId,
                    resolutionEvent.ProjectId,
                    resolutionEvent.Target.EntityKind,
                    resolutionEvent.Target.EntityId,
                    resolutionEvent.Target.FieldKey);
                var current = field?.Revision ?? 0;
                if (resolutionEvent.Target.ExpectedRevision != current)
                    throw new InvalidOperationException("Conflict resolution field revision is stale.");

                return MutationArbitration.Applied(current + 1, entity.Revision + 1);
            }

            case H2ConflictResolutionAction.DeleteEntity:
            {
                if (resolutionEvent.Target.FieldKey is not null)
                    throw new InvalidOperationException("DeleteEntity resolution must target the whole entity.");
                if (entity.IsDeleted)
                    throw new InvalidOperationException("Conflict target is already deleted.");
                if (resolutionEvent.Target.ExpectedRevision != entity.Revision)
                    throw new InvalidOperationException("Conflict resolution entity revision is stale.");

                return MutationArbitration.Applied(
                    entity.Revision + 1,
                    entity.Revision + 1,
                    deleteEntity: true);
            }

            case H2ConflictResolutionAction.RestoreEntity:
            {
                if (resolutionEvent.Target.FieldKey is not null)
                    throw new InvalidOperationException("RestoreEntity resolution must target the whole entity.");
                if (!entity.IsDeleted)
                    throw new InvalidOperationException("RestoreEntity requires a deleted target.");
                if (payload.ValueJson is null)
                    throw new InvalidDataException("RestoreEntity resolution requires ValueJson.");
                if (resolutionEvent.Target.ExpectedRevision != entity.Revision)
                    throw new InvalidOperationException("Conflict resolution entity revision is stale.");

                return MutationArbitration.Applied(
                    entity.Revision + 1,
                    entity.Revision + 1);
            }

            default:
                throw new NotSupportedException("Unsupported conflict resolution action.");
        }
    }

    private static void ApplyResolutionRevision(
        SqliteConnection connection,
        SqliteTransaction tx,
        H2ProjectEventDraft resolutionEvent,
        H2ConflictResolutionPayload payload,
        MutationArbitration arbitration)
    {
        if (payload.Action == H2ConflictResolutionAction.SetField)
        {
            UpsertRevision(
                connection, tx,
                resolutionEvent.WorkspaceId,
                resolutionEvent.ProjectId,
                resolutionEvent.Target.EntityKind,
                resolutionEvent.Target.EntityId,
                resolutionEvent.Target.FieldKey,
                arbitration.ResultingRevision!.Value,
                false,
                resolutionEvent.EventId);
        }
        else if (payload.Action == H2ConflictResolutionAction.RestoreEntity)
        {
            Execute(connection, tx,
                """
                DELETE FROM project_revisions
                WHERE workspace_id = $workspace
                  AND project_id = $project
                  AND entity_kind = $kind
                  AND entity_id = $entity
                  AND field_key <> $entityKey;
                """,
                ("$workspace", Id(resolutionEvent.WorkspaceId)),
                ("$project", Id(resolutionEvent.ProjectId)),
                ("$kind", (int)resolutionEvent.Target.EntityKind),
                ("$entity", Id(resolutionEvent.Target.EntityId)),
                ("$entityKey", ToDbFieldKey(null)));
        }

        UpsertRevision(
            connection, tx,
            resolutionEvent.WorkspaceId,
            resolutionEvent.ProjectId,
            resolutionEvent.Target.EntityKind,
            resolutionEvent.Target.EntityId,
            null,
            arbitration.ResultingEntityRevision!.Value,
            payload.Action == H2ConflictResolutionAction.DeleteEntity,
            resolutionEvent.EventId);
    }

    private static void ValidateAiEventCorrelation(
        SqliteConnection connection,
        SqliteTransaction tx,
        H2ProjectEventDraft draft,
        DateTimeOffset serverNow)
    {
        if (!draft.AiRequestId.HasValue && !draft.AiLeaseId.HasValue) return;
        if (!draft.AiRequestId.HasValue || !draft.AiLeaseId.HasValue)
            throw new InvalidOperationException("AI event correlation is incomplete.");

        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            SELECT request_json, state, lease_json
            FROM ai_queue
            WHERE workspace_id = $workspace
              AND project_id = $project
              AND request_id = $request;
            """;
        command.Parameters.AddWithValue("$workspace", Id(draft.WorkspaceId));
        command.Parameters.AddWithValue("$project", Id(draft.ProjectId));
        command.Parameters.AddWithValue("$request", Id(draft.AiRequestId.Value));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("AI-correlated project event has no matching queued request.");

        var request = Deserialize<H2ProjectAiRequest>(reader.GetString(0));
        var state = (H2ProjectAiQueueState)reader.GetInt32(1);
        if (state != H2ProjectAiQueueState.Running || reader.IsDBNull(2))
            throw new InvalidOperationException("AI-correlated project event requires a RUNNING project AI lease.");
        var lease = Deserialize<H2ProjectAiLease>(reader.GetString(2));

        if (request.RequestId != draft.AiRequestId.Value
            || request.ProjectId != draft.ProjectId
            || request.OwnerDeviceId != draft.DeviceId
            || lease.LeaseId != draft.AiLeaseId.Value
            || lease.RequestId != draft.AiRequestId.Value
            || lease.ProjectId != draft.ProjectId
            || lease.OwnerDeviceId != draft.DeviceId)
            throw new InvalidOperationException("AI-correlated project event does not match the active request/lease/device.");
        if (lease.ExpiresUtc <= serverNow)
            throw new InvalidOperationException("AI-correlated project event lease has expired.");
    }

    private static MutationArbitration ArbitrateMutation(
        SqliteConnection connection,
        SqliteTransaction tx,
        H2ProjectEventDraft draft)
    {
        var entity = ReadRevision(
            connection, tx, draft.WorkspaceId, draft.ProjectId,
            draft.Target.EntityKind, draft.Target.EntityId, null);

        switch (draft.Kind)
        {
            case H2ProjectEventKind.CreateEntity:
            {
                if (draft.Target.EntityKind != H2ProjectEntityKind.Project)
                {
                    var projectEntity = ReadRevision(
                        connection, tx, draft.WorkspaceId, draft.ProjectId,
                        H2ProjectEntityKind.Project, draft.ProjectId, null);
                    if (projectEntity is null || projectEntity.IsDeleted)
                        throw new InvalidOperationException("Child entity cannot be created before an active project baseline.");
                }

                var expected = draft.Target.ExpectedRevision ?? 0;
                if (entity is null)
                {
                    if (expected != 0)
                        throw new InvalidOperationException("CreateEntity expected revision must be zero for a new entity.");
                    return MutationArbitration.Applied(resultingRevision: 1, resultingEntityRevision: 1);
                }

                return MutationArbitration.FromConflict(CreateConflict(draft, entity.LastEventId));
            }

            case H2ProjectEventKind.SetField:
            {
                if (entity is null)
                    throw new InvalidOperationException("SetField target entity does not exist.");
                if (entity.IsDeleted)
                    return MutationArbitration.FromConflict(CreateConflict(draft, entity.LastEventId));

                var expected = draft.Target.ExpectedRevision
                    ?? throw new InvalidOperationException("SetField requires ExpectedRevision.");
                var field = ReadRevision(
                    connection, tx, draft.WorkspaceId, draft.ProjectId,
                    draft.Target.EntityKind, draft.Target.EntityId, draft.Target.FieldKey);
                var currentFieldRevision = field?.Revision ?? 0;

                if (expected != currentFieldRevision)
                {
                    var priorEventId = field?.LastEventId ?? entity.LastEventId;
                    return MutationArbitration.FromConflict(CreateConflict(draft, priorEventId));
                }

                return MutationArbitration.Applied(
                    currentFieldRevision + 1,
                    entity.Revision + 1);
            }

            case H2ProjectEventKind.AppendMessage:
            {
                if (draft.Target.EntityKind != H2ProjectEntityKind.Message)
                    throw new InvalidOperationException("AppendMessage must target Message.");
                var projectEntity = ReadRevision(
                    connection, tx, draft.WorkspaceId, draft.ProjectId,
                    H2ProjectEntityKind.Project, draft.ProjectId, null);
                if (projectEntity is null || projectEntity.IsDeleted)
                    throw new InvalidOperationException("Message cannot be appended before an active project baseline.");

                var append = Deserialize<H2ProjectMessageAppend>(draft.PayloadJson);
                if (append.Message.Id != draft.Target.EntityId)
                    throw new InvalidDataException("AppendMessage payload identity does not match target.");
                var expected = draft.Target.ExpectedRevision ?? 0;
                if (entity is null)
                {
                    if (expected != 0)
                        throw new InvalidOperationException("AppendMessage expected revision must be zero for a new message.");
                    return MutationArbitration.Applied(1, 1);
                }
                return MutationArbitration.FromConflict(CreateConflict(draft, entity.LastEventId));
            }

            case H2ProjectEventKind.DeleteEntity:
            {
                if (entity is null)
                    throw new InvalidOperationException("DeleteEntity target does not exist.");

                var expected = draft.Target.ExpectedRevision
                    ?? throw new InvalidOperationException("DeleteEntity requires ExpectedRevision.");
                if (entity.IsDeleted || expected != entity.Revision)
                    return MutationArbitration.FromConflict(CreateConflict(draft, entity.LastEventId));

                return MutationArbitration.Applied(
                    entity.Revision + 1,
                    entity.Revision + 1,
                    deleteEntity: true);
            }

            default:
                throw new NotSupportedException(
                    $"Coordinator revision arbitration does not support event kind {draft.Kind} in H2M-133E.");
        }
    }

    private static void ApplyRevisionMutation(
        SqliteConnection connection,
        SqliteTransaction tx,
        H2ProjectEventDraft draft,
        MutationArbitration arbitration)
    {
        if (arbitration.Disposition != H2ProjectEventDisposition.Applied)
            throw new InvalidOperationException("Only applied mutations can advance revision state.");

        switch (draft.Kind)
        {
            case H2ProjectEventKind.CreateEntity:
            case H2ProjectEventKind.AppendMessage:
                UpsertRevision(
                    connection, tx, draft.WorkspaceId, draft.ProjectId,
                    draft.Target.EntityKind, draft.Target.EntityId, null,
                    arbitration.ResultingEntityRevision!.Value,
                    isDeleted: false,
                    draft.EventId);
                break;

            case H2ProjectEventKind.SetField:
                UpsertRevision(
                    connection, tx, draft.WorkspaceId, draft.ProjectId,
                    draft.Target.EntityKind, draft.Target.EntityId, draft.Target.FieldKey,
                    arbitration.ResultingRevision!.Value,
                    isDeleted: false,
                    draft.EventId);
                UpsertRevision(
                    connection, tx, draft.WorkspaceId, draft.ProjectId,
                    draft.Target.EntityKind, draft.Target.EntityId, null,
                    arbitration.ResultingEntityRevision!.Value,
                    isDeleted: false,
                    draft.EventId);
                break;

            case H2ProjectEventKind.DeleteEntity:
                UpsertRevision(
                    connection, tx, draft.WorkspaceId, draft.ProjectId,
                    draft.Target.EntityKind, draft.Target.EntityId, null,
                    arbitration.ResultingEntityRevision!.Value,
                    isDeleted: true,
                    draft.EventId);
                break;

            default:
                throw new NotSupportedException("Unsupported applied revision mutation.");
        }
    }

    private static RevisionRow? ReadRevision(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid workspaceId,
        Guid projectId,
        H2ProjectEntityKind entityKind,
        Guid entityId,
        string? fieldKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            SELECT revision, is_deleted, last_event_id
            FROM project_revisions
            WHERE workspace_id = $workspace
              AND project_id = $project
              AND entity_kind = $kind
              AND entity_id = $entity
              AND field_key = $field;
            """;
        command.Parameters.AddWithValue("$workspace", Id(workspaceId));
        command.Parameters.AddWithValue("$project", Id(projectId));
        command.Parameters.AddWithValue("$kind", (int)entityKind);
        command.Parameters.AddWithValue("$entity", Id(entityId));
        command.Parameters.AddWithValue("$field", ToDbFieldKey(fieldKey));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new RevisionRow(
            reader.GetInt64(0),
            reader.GetInt32(1) != 0,
            Guid.Parse(reader.GetString(2)));
    }

    private static void UpsertRevision(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid workspaceId,
        Guid projectId,
        H2ProjectEntityKind entityKind,
        Guid entityId,
        string? fieldKey,
        long revision,
        bool isDeleted,
        Guid lastEventId)
    {
        Execute(connection, tx,
            """
            INSERT INTO project_revisions(
                workspace_id, project_id, entity_kind, entity_id, field_key,
                revision, is_deleted, last_event_id)
            VALUES(
                $workspace, $project, $kind, $entity, $field,
                $revision, $deleted, $event)
            ON CONFLICT(workspace_id, project_id, entity_kind, entity_id, field_key)
            DO UPDATE SET
                revision = excluded.revision,
                is_deleted = excluded.is_deleted,
                last_event_id = excluded.last_event_id;
            """,
            ("$workspace", Id(workspaceId)),
            ("$project", Id(projectId)),
            ("$kind", (int)entityKind),
            ("$entity", Id(entityId)),
            ("$field", ToDbFieldKey(fieldKey)),
            ("$revision", revision),
            ("$deleted", isDeleted ? 1 : 0),
            ("$event", Id(lastEventId)));
    }

    private static H2ProjectConflict CreateConflict(H2ProjectEventDraft draft, Guid priorEventId)
    {
        if (priorEventId == Guid.Empty)
            throw new InvalidOperationException("Conflict cannot be anchored without prior accepted event identity.");
        return new H2ProjectConflict(
            Guid.NewGuid(),
            draft.WorkspaceId,
            draft.ProjectId,
            draft.Target,
            new[] { priorEventId, draft.EventId },
            DateTimeOffset.UtcNow);
    }

    private static void InsertConflict(
        SqliteConnection connection,
        SqliteTransaction tx,
        H2ProjectConflict conflict)
    {
        Execute(connection, tx,
            """
            INSERT INTO project_conflicts(
                conflict_id, workspace_id, project_id,
                entity_kind, entity_id, field_key, expected_revision,
                event_ids_json, created_utc, state, resolved_event_id)
            VALUES(
                $conflict, $workspace, $project,
                $entityKind, $entity, $field, $revision,
                $events, $created, $state, $resolved);
            """,
            ("$conflict", Id(conflict.ConflictId)),
            ("$workspace", Id(conflict.WorkspaceId)),
            ("$project", Id(conflict.ProjectId)),
            ("$entityKind", (int)conflict.Target.EntityKind),
            ("$entity", Id(conflict.Target.EntityId)),
            ("$field", conflict.Target.FieldKey),
            ("$revision", conflict.Target.ExpectedRevision),
            ("$events", JsonSerializer.Serialize(conflict.EventIds, Json)),
            ("$created", Stamp(conflict.CreatedUtc)),
            ("$state", (int)conflict.State),
            ("$resolved", conflict.ResolvedByEventId is null ? null : Id(conflict.ResolvedByEventId.Value)));
    }

    private static H2ProjectConflict ReadConflict(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid conflictId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            SELECT
                workspace_id, project_id,
                entity_kind, entity_id, field_key, expected_revision,
                event_ids_json, created_utc, state, resolved_event_id
            FROM project_conflicts
            WHERE conflict_id = $conflict;
            """;
        command.Parameters.AddWithValue("$conflict", Id(conflictId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("Conflict record is missing.");

        var target = new H2ProjectMutationTarget(
            (H2ProjectEntityKind)reader.GetInt32(2),
            Guid.Parse(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5));
        var eventIds = JsonSerializer.Deserialize<Guid[]>(reader.GetString(6), Json)
            ?? throw new InvalidDataException("Conflict event identity list is missing.");
        return new H2ProjectConflict(
            conflictId,
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            target,
            eventIds,
            ParseStamp(reader.GetString(7)),
            (H2ProjectConflictState)reader.GetInt32(8),
            reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)));
    }

    private static string ToDbFieldKey(string? fieldKey) => fieldKey ?? "$entity";
    private static string? FromDbFieldKey(string fieldKey)
        => string.Equals(fieldKey, "$entity", StringComparison.Ordinal) ? null : fieldKey;

    private void Initialize()
    {
        using var connection = Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText =
                """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=FULL;
                PRAGMA foreign_keys=ON;
                PRAGMA busy_timeout=5000;
                """;
            pragma.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS coordinator_meta(
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS workspaces(
                workspace_id TEXT PRIMARY KEY,
                created_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS devices(
                workspace_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                display_name TEXT NOT NULL,
                registered_utc TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL,
                PRIMARY KEY(workspace_id, device_id),
                FOREIGN KEY(workspace_id) REFERENCES workspaces(workspace_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS project_heads(
                workspace_id TEXT NOT NULL,
                project_id TEXT NOT NULL,
                server_sequence INTEGER NOT NULL DEFAULT 0,
                snapshot_through_sequence INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(workspace_id, project_id),
                FOREIGN KEY(workspace_id) REFERENCES workspaces(workspace_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS project_events(
                workspace_id TEXT NOT NULL,
                project_id TEXT NOT NULL,
                server_sequence INTEGER NOT NULL,
                event_id TEXT NOT NULL UNIQUE,
                device_id TEXT NOT NULL,
                device_sequence INTEGER NOT NULL,
                client_operation_id TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL,
                draft_json TEXT NOT NULL,
                accepted_utc TEXT NOT NULL,
                disposition INTEGER NOT NULL DEFAULT 1,
                resulting_revision INTEGER NULL,
                resulting_entity_revision INTEGER NULL,
                conflict_id TEXT NULL,
                PRIMARY KEY(workspace_id, project_id, server_sequence),
                UNIQUE(workspace_id, client_operation_id),
                UNIQUE(workspace_id, device_id, device_sequence)
            );

            CREATE INDEX IF NOT EXISTS ix_project_events_after
                ON project_events(workspace_id, project_id, server_sequence);

            CREATE TABLE IF NOT EXISTS project_event_archive(
                workspace_id TEXT NOT NULL,
                project_id TEXT NOT NULL,
                server_sequence INTEGER NOT NULL,
                event_id TEXT NOT NULL UNIQUE,
                device_id TEXT NOT NULL,
                device_sequence INTEGER NOT NULL,
                client_operation_id TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL,
                draft_json TEXT NOT NULL,
                accepted_utc TEXT NOT NULL,
                disposition INTEGER NOT NULL,
                resulting_revision INTEGER NULL,
                resulting_entity_revision INTEGER NULL,
                conflict_id TEXT NULL,
                PRIMARY KEY(workspace_id, project_id, server_sequence),
                UNIQUE(workspace_id, client_operation_id),
                UNIQUE(workspace_id, device_id, device_sequence)
            );

            CREATE INDEX IF NOT EXISTS ix_project_event_archive_after
                ON project_event_archive(workspace_id, project_id, server_sequence);

            CREATE TABLE IF NOT EXISTS project_revisions(
                workspace_id TEXT NOT NULL,
                project_id TEXT NOT NULL,
                entity_kind INTEGER NOT NULL,
                entity_id TEXT NOT NULL,
                field_key TEXT NOT NULL,
                revision INTEGER NOT NULL,
                is_deleted INTEGER NOT NULL DEFAULT 0,
                last_event_id TEXT NOT NULL,
                PRIMARY KEY(workspace_id, project_id, entity_kind, entity_id, field_key)
            );

            CREATE INDEX IF NOT EXISTS ix_project_revisions_project
                ON project_revisions(workspace_id, project_id);

            CREATE TABLE IF NOT EXISTS project_snapshots(
                workspace_id TEXT NOT NULL,
                project_id TEXT NOT NULL,
                through_sequence INTEGER NOT NULL,
                state_sha256 TEXT NOT NULL,
                snapshot_json TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                PRIMARY KEY(workspace_id, project_id, through_sequence)
            );

            CREATE TABLE IF NOT EXISTS project_conflicts(
                conflict_id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                project_id TEXT NOT NULL,
                entity_kind INTEGER NOT NULL,
                entity_id TEXT NOT NULL,
                field_key TEXT NULL,
                expected_revision INTEGER NULL,
                event_ids_json TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                state INTEGER NOT NULL,
                resolved_event_id TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_project_conflicts_project
                ON project_conflicts(workspace_id, project_id, state);

            CREATE TABLE IF NOT EXISTS ai_queue(
                workspace_id TEXT NOT NULL,
                project_id TEXT NOT NULL,
                queue_sequence INTEGER NOT NULL,
                request_id TEXT NOT NULL UNIQUE,
                client_request_id TEXT NOT NULL,
                owner_device_id TEXT NOT NULL,
                request_json TEXT NOT NULL,
                state INTEGER NOT NULL,
                accepted_utc TEXT NOT NULL,
                lease_json TEXT NULL,
                completion_json TEXT NULL,
                PRIMARY KEY(workspace_id, project_id, queue_sequence),
                UNIQUE(workspace_id, client_request_id)
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_ai_one_running_per_project
                ON ai_queue(workspace_id, project_id)
                WHERE state = 3;

            INSERT INTO coordinator_meta(key, value)
            VALUES('schema_version', '2')
            ON CONFLICT(key) DO NOTHING;
            """;
        command.ExecuteNonQuery();

        using var version = connection.CreateCommand();
        version.CommandText = "SELECT value FROM coordinator_meta WHERE key='schema_version';";
        var value = version.ExecuteScalar()?.ToString();
        if (string.Equals(value, "1", StringComparison.Ordinal))
        {
            using var migration = connection.CreateCommand();
            migration.CommandText =
                """
                ALTER TABLE project_events ADD COLUMN disposition INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE project_events ADD COLUMN resulting_revision INTEGER NULL;
                ALTER TABLE project_events ADD COLUMN resulting_entity_revision INTEGER NULL;
                ALTER TABLE project_events ADD COLUMN conflict_id TEXT NULL;

                CREATE TABLE IF NOT EXISTS project_revisions(
                    workspace_id TEXT NOT NULL,
                    project_id TEXT NOT NULL,
                    entity_kind INTEGER NOT NULL,
                    entity_id TEXT NOT NULL,
                    field_key TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    is_deleted INTEGER NOT NULL DEFAULT 0,
                    last_event_id TEXT NOT NULL,
                    PRIMARY KEY(workspace_id, project_id, entity_kind, entity_id, field_key)
                );

                CREATE INDEX IF NOT EXISTS ix_project_revisions_project
                    ON project_revisions(workspace_id, project_id);

                UPDATE coordinator_meta SET value='2' WHERE key='schema_version';
                """;
            migration.ExecuteNonQuery();
            value = "2";
        }

        if (!string.Equals(value, SchemaVersion.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported Coordinator DB schema {value ?? "<missing>"}.");
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static void RequireRegisteredDevice(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid workspaceId,
        Guid deviceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            SELECT 1
            FROM devices
            WHERE workspace_id = $workspace AND device_id = $device;
            """;
        command.Parameters.AddWithValue("$workspace", Id(workspaceId));
        command.Parameters.AddWithValue("$device", Id(deviceId));
        if (command.ExecuteScalar() is null)
            throw new InvalidOperationException("Submitting device is not registered for this workspace.");
    }

    private static void EnsureProjectHead(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid workspaceId,
        Guid projectId)
    {
        Execute(connection, tx,
            """
            INSERT OR IGNORE INTO project_heads(
                workspace_id, project_id, server_sequence, snapshot_through_sequence)
            VALUES($workspace, $project, 0, 0);
            """,
            ("$workspace", Id(workspaceId)),
            ("$project", Id(projectId)));
    }

    private static long ReadProjectSequence(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid workspaceId,
        Guid projectId)
        => ScalarInt64(connection, tx,
            """
            SELECT server_sequence
            FROM project_heads
            WHERE workspace_id = $workspace AND project_id = $project;
            """,
            ("$workspace", Id(workspaceId)),
            ("$project", Id(projectId)));

    private static ExistingEvent? FindEventByOperation(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid workspaceId,
        Guid clientOperationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            SELECT event_id, project_id, device_id, client_operation_id, payload_sha256, server_sequence, accepted_utc, conflict_id
            FROM (
                SELECT workspace_id, event_id, project_id, device_id, client_operation_id,
                       payload_sha256, server_sequence, accepted_utc, conflict_id
                FROM project_event_archive
                UNION ALL
                SELECT workspace_id, event_id, project_id, device_id, client_operation_id,
                       payload_sha256, server_sequence, accepted_utc, conflict_id
                FROM project_events
            ) e
            WHERE workspace_id = $workspace AND client_operation_id = $operation
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$workspace", Id(workspaceId));
        command.Parameters.AddWithValue("$operation", Id(clientOperationId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new ExistingEvent(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            Guid.Parse(reader.GetString(3)),
            reader.GetString(4),
            reader.GetInt64(5),
            ParseStamp(reader.GetString(6)),
            reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)));
    }

    private static ExistingEvent? FindEventByEventId(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid eventId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            SELECT event_id, project_id, device_id, client_operation_id, payload_sha256, server_sequence, accepted_utc, conflict_id
            FROM (
                SELECT event_id, project_id, device_id, client_operation_id,
                       payload_sha256, server_sequence, accepted_utc, conflict_id
                FROM project_event_archive
                UNION ALL
                SELECT event_id, project_id, device_id, client_operation_id,
                       payload_sha256, server_sequence, accepted_utc, conflict_id
                FROM project_events
            ) e
            WHERE event_id = $event
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$event", Id(eventId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new ExistingEvent(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            Guid.Parse(reader.GetString(3)),
            reader.GetString(4),
            reader.GetInt64(5),
            ParseStamp(reader.GetString(6)),
            reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)));
    }

    private static ExistingEvent? FindEventByDeviceSequence(
        SqliteConnection connection,
        SqliteTransaction tx,
        Guid workspaceId,
        Guid deviceId,
        long deviceSequence)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            SELECT event_id, project_id, device_id, client_operation_id,
                   payload_sha256, server_sequence, accepted_utc, conflict_id
            FROM (
                SELECT workspace_id, event_id, project_id, device_id, device_sequence,
                       client_operation_id, payload_sha256, server_sequence, accepted_utc, conflict_id
                FROM project_event_archive
                UNION ALL
                SELECT workspace_id, event_id, project_id, device_id, device_sequence,
                       client_operation_id, payload_sha256, server_sequence, accepted_utc, conflict_id
                FROM project_events
            ) e
            WHERE workspace_id = $workspace
              AND device_id = $device
              AND device_sequence = $deviceSequence
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$workspace", Id(workspaceId));
        command.Parameters.AddWithValue("$device", Id(deviceId));
        command.Parameters.AddWithValue("$deviceSequence", deviceSequence);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new ExistingEvent(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            Guid.Parse(reader.GetString(3)),
            reader.GetString(4),
            reader.GetInt64(5),
            ParseStamp(reader.GetString(6)),
            reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)));
    }

    private static int Execute(
        SqliteConnection connection,
        SqliteTransaction tx,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    private static long ScalarInt64(
        SqliteConnection connection,
        SqliteTransaction tx,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, Json)
           ?? throw new InvalidDataException("Coordinator durable JSON record could not be decoded.");

    private static string Id(Guid value) => value.ToString("N");
    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseStamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record RevisionRow(long Revision, bool IsDeleted, Guid LastEventId);

    private sealed record AiQueueLeaseRow(
        Guid WorkspaceId,
        Guid ProjectId,
        H2ProjectAiRequest Request,
        H2ProjectAiQueueState State,
        H2ProjectAiLease Lease);

    private sealed record MutationArbitration(
        H2ProjectEventDisposition Disposition,
        long? ResultingRevision,
        long? ResultingEntityRevision,
        bool DeleteEntity,
        H2ProjectConflict? Conflict)
    {
        public static MutationArbitration Applied(
            long resultingRevision,
            long resultingEntityRevision,
            bool deleteEntity = false)
            => new(
                H2ProjectEventDisposition.Applied,
                resultingRevision,
                resultingEntityRevision,
                deleteEntity,
                null);

        public static MutationArbitration FromConflict(H2ProjectConflict conflict)
            => new(H2ProjectEventDisposition.Conflict, null, null, false, conflict);
    }

    private sealed record ExistingEvent(
        Guid EventId,
        Guid ProjectId,
        Guid DeviceId,
        Guid ClientOperationId,
        string PayloadSha256,
        long ServerSequence,
        DateTimeOffset AcceptedUtc,
        Guid? ConflictId);
}

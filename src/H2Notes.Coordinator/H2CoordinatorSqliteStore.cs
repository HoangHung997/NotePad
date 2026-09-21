using System.Globalization;
using System.Text.Json;
using H2Notes.Core;
using Microsoft.Data.Sqlite;

namespace H2Notes.Coordinator;

/// <summary>
/// Durable single-writer store for one H2 Sync Coordinator process.
/// The database path must be local to the Coordinator host. Clients must never open
/// this SQLite database directly through SMB/WebDAV/mapped network paths.
/// </summary>
public sealed class H2CoordinatorSqliteStore
{
    private const int SchemaVersion = 1;
    private readonly object _gate = new();
    private readonly string _connectionString;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public H2CoordinatorSqliteStore(string databasePath)
    {
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

            foreach (var draft in events)
            {
                if (draft.WorkspaceId != workspaceId)
                    throw new InvalidOperationException("Event WorkspaceId does not match submission workspace.");
                if (draft.DeviceId != deviceId)
                    throw new InvalidOperationException("Event DeviceId does not match submitting device.");

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
                    continue;
                }

                var byEvent = FindEventByEventId(connection, tx, draft.EventId);
                if (byEvent is not null)
                    throw new InvalidOperationException("EventId already exists with another client operation.");

                if (!heads.TryGetValue(draft.ProjectId, out var sequence))
                {
                    EnsureProjectHead(connection, tx, workspaceId, draft.ProjectId);
                    sequence = ReadProjectSequence(connection, tx, workspaceId, draft.ProjectId);
                }

                sequence++;
                heads[draft.ProjectId] = sequence;
                var acceptedUtc = DateTimeOffset.UtcNow;
                var draftJson = JsonSerializer.Serialize(draft, Json);

                Execute(connection, tx,
                    """
                    INSERT INTO project_events(
                        workspace_id, project_id, server_sequence,
                        event_id, device_id, device_sequence, client_operation_id,
                        payload_sha256, draft_json, accepted_utc)
                    VALUES(
                        $workspace, $project, $sequence,
                        $event, $device, $deviceSequence, $operation,
                        $payloadHash, $draft, $accepted);
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
                    ("$accepted", Stamp(acceptedUtc)));

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
            return new H2ProjectEventSubmissionResult(acknowledgements, []);
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
                SELECT server_sequence, draft_json, accepted_utc
                FROM project_events
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
                    ParseStamp(reader.GetString(2))));
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
            if (snapshot.ThroughServerSequence > head)
                throw new InvalidOperationException("Snapshot cannot advance beyond accepted project head.");

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

    public void CompleteProjectAi(H2ProjectAiCompletion completion)
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
                PRIMARY KEY(workspace_id, project_id, server_sequence),
                UNIQUE(workspace_id, client_operation_id),
                UNIQUE(workspace_id, device_id, device_sequence)
            );

            CREATE INDEX IF NOT EXISTS ix_project_events_after
                ON project_events(workspace_id, project_id, server_sequence);

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
            VALUES('schema_version', '1')
            ON CONFLICT(key) DO NOTHING;
            """;
        command.ExecuteNonQuery();

        using var version = connection.CreateCommand();
        version.CommandText = "SELECT value FROM coordinator_meta WHERE key='schema_version';";
        var value = version.ExecuteScalar()?.ToString();
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
            SELECT event_id, project_id, device_id, payload_sha256, server_sequence, accepted_utc
            FROM project_events
            WHERE workspace_id = $workspace AND client_operation_id = $operation;
            """;
        command.Parameters.AddWithValue("$workspace", Id(workspaceId));
        command.Parameters.AddWithValue("$operation", Id(clientOperationId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new ExistingEvent(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            reader.GetString(3),
            reader.GetInt64(4),
            ParseStamp(reader.GetString(5)));
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
            SELECT event_id, project_id, device_id, payload_sha256, server_sequence, accepted_utc
            FROM project_events
            WHERE event_id = $event;
            """;
        command.Parameters.AddWithValue("$event", Id(eventId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new ExistingEvent(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            reader.GetString(3),
            reader.GetInt64(4),
            ParseStamp(reader.GetString(5)));
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

    private sealed record ExistingEvent(
        Guid EventId,
        Guid ProjectId,
        Guid DeviceId,
        string PayloadSha256,
        long ServerSequence,
        DateTimeOffset AcceptedUtc);
}

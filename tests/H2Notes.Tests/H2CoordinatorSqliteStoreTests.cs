using H2Notes.Coordinator;
using H2Notes.Core;

internal static class H2CoordinatorSqliteStoreTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Coordinator SQLite store rejects network database paths", () =>
        {
            if (OperatingSystem.IsWindows())
                Throws<ArgumentException>(() => new H2CoordinatorSqliteStore(@"\\server\share\h2-coordinator.db"));
        });

        test("Coordinator assigns durable server sequence and idempotent retry survives restart", () =>
        {
            var folder = Folder();
            var db = Path.Combine(folder, "coordinator.db");
            var workspace = Guid.NewGuid();
            var project = Guid.NewGuid();
            var device = H2CoordinatorDeviceIdentity.CreateNew("PC1");

            var first = Draft(
                workspace, project, device.DeviceId, deviceSequence: 1,
                clientOperationId: Guid.NewGuid(),
                field: "Name",
                value: "First",
                clientTime: new DateTimeOffset(2026, 9, 21, 10, 5, 0, TimeSpan.FromHours(7)));
            var second = Draft(
                workspace, project, device.DeviceId, deviceSequence: 2,
                clientOperationId: Guid.NewGuid(),
                field: "Deadline",
                value: "Second",
                clientTime: new DateTimeOffset(2026, 9, 21, 9, 55, 0, TimeSpan.FromHours(7)));

            var store = new H2CoordinatorSqliteStore(db);
            store.RegisterDevice(workspace, device);
            var accepted = store.SubmitProjectEvents(workspace, device.DeviceId, new[] { first, second });

            Equal(1L, accepted.Acknowledgements[0].ServerSequence);
            Equal(2L, accepted.Acknowledgements[1].ServerSequence);
            Equal(2L, store.GetProjectHead(workspace, project).ServerSequence);

            var retry = store.SubmitProjectEvents(workspace, device.DeviceId, new[] { first });
            Equal(1L, retry.Acknowledgements.Single().ServerSequence);
            Equal(2, store.GetProjectEvents(workspace, project, 0).Count);

            var restarted = new H2CoordinatorSqliteStore(db);
            Equal(2L, restarted.GetProjectHead(workspace, project).ServerSequence);
            var replay = restarted.GetProjectEvents(workspace, project, 0);
            Equal(2, replay.Count);
            Equal(first.EventId, replay[0].Draft.EventId);
            Equal(second.EventId, replay[1].Draft.EventId);

            // Device registration is durable: after restart the same device can submit without re-registering.
            var third = Draft(
                workspace, project, device.DeviceId, 3, Guid.NewGuid(),
                "Notes", "After restart", DateTimeOffset.UtcNow);
            var afterRestart = restarted.SubmitProjectEvents(workspace, device.DeviceId, new[] { third });
            Equal(3L, afterRestart.Acknowledgements.Single().ServerSequence);
        });

        test("Coordinator rejects ClientOperationId collision instead of duplicating or overwriting", () =>
        {
            var db = Path.Combine(Folder(), "coordinator.db");
            var workspace = Guid.NewGuid();
            var project = Guid.NewGuid();
            var device = H2CoordinatorDeviceIdentity.CreateNew("PC1");
            var operation = Guid.NewGuid();
            var store = new H2CoordinatorSqliteStore(db);
            store.RegisterDevice(workspace, device);

            var first = Draft(workspace, project, device.DeviceId, 1, operation, "Name", "A", DateTimeOffset.UtcNow);
            store.SubmitProjectEvents(workspace, device.DeviceId, new[] { first });

            var incompatible = Draft(workspace, project, device.DeviceId, 2, operation, "Name", "B", DateTimeOffset.UtcNow);
            Throws<InvalidOperationException>(() =>
                store.SubmitProjectEvents(workspace, device.DeviceId, new[] { incompatible }));

            Equal(1, store.GetProjectEvents(workspace, project, 0).Count);
        });

        test("Coordinator persists verified snapshot and structured conflict across restart", () =>
        {
            var db = Path.Combine(Folder(), "coordinator.db");
            var workspace = Guid.NewGuid();
            var project = Guid.NewGuid();
            var device = H2CoordinatorDeviceIdentity.CreateNew("PC1");
            var store = new H2CoordinatorSqliteStore(db);
            store.RegisterDevice(workspace, device);

            var a = Draft(workspace, project, device.DeviceId, 1, Guid.NewGuid(), "Name", "A", DateTimeOffset.UtcNow);
            var b = Draft(workspace, project, device.DeviceId, 2, Guid.NewGuid(), "Deadline", "B", DateTimeOffset.UtcNow);
            store.SubmitProjectEvents(workspace, device.DeviceId, new[] { a, b });

            var state = "{\"project\":\"snapshot\"}";
            var snapshot = new H2ProjectSnapshot(
                workspace,
                project,
                2,
                state,
                H2ProjectEventDraft.ComputePayloadSha256(state),
                DateTimeOffset.UtcNow);
            store.SaveSnapshot(snapshot);

            var conflict = new H2ProjectConflict(
                Guid.NewGuid(),
                workspace,
                project,
                new H2ProjectMutationTarget(H2ProjectEntityKind.Task, Guid.NewGuid(), "Name", 7),
                new[] { a.EventId, b.EventId },
                DateTimeOffset.UtcNow);
            store.SaveConflict(conflict);

            var restarted = new H2CoordinatorSqliteStore(db);
            Equal(2L, restarted.GetLatestSnapshot(workspace, project)!.ThroughServerSequence);
            var conflicts = restarted.GetConflicts(workspace, project);
            Equal(1, conflicts.Count);
            Equal(conflict.ConflictId, conflicts[0].ConflictId);
            Equal(1, restarted.GetProjectHead(workspace, project).OpenConflictCount);
        });

        test("Coordinator persists FIFO AI queue and active lease across restart", () =>
        {
            var db = Path.Combine(Folder(), "coordinator.db");
            var workspace = Guid.NewGuid();
            var project = Guid.NewGuid();
            var pc1 = H2CoordinatorDeviceIdentity.CreateNew("PC1");
            var pc2 = H2CoordinatorDeviceIdentity.CreateNew("PC2");
            var store = new H2CoordinatorSqliteStore(db);
            store.RegisterDevice(workspace, pc1);
            store.RegisterDevice(workspace, pc2);

            var r1 = Request(workspace, project, pc1.DeviceId);
            var r2 = Request(workspace, project, pc2.DeviceId);
            var q1 = store.EnqueueProjectAi(r1);
            var q2 = store.EnqueueProjectAi(r2);
            Equal(1L, q1.QueueSequence);
            Equal(2L, q2.QueueSequence);

            var now = DateTimeOffset.UtcNow;
            var lease1 = new H2ProjectAiLease(
                Guid.NewGuid(),
                r1.RequestId,
                project,
                pc1.DeviceId,
                q1.QueueSequence,
                new H2ProjectSyncBarrier(project, 0),
                now,
                now,
                now.AddMinutes(1));
            store.SaveProjectAiLease(lease1);

            var restarted = new H2CoordinatorSqliteStore(db);
            Equal(lease1.LeaseId, restarted.GetRunningProjectAiLease(workspace, project)!.LeaseId);
            Equal(2, restarted.GetProjectAiQueue(workspace, project).Count);

            restarted.CompleteProjectAi(new H2ProjectAiCompletion(
                lease1.LeaseId,
                r1.RequestId,
                project,
                H2ProjectAiQueueState.Completed,
                0,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow));

            var lease2 = new H2ProjectAiLease(
                Guid.NewGuid(),
                r2.RequestId,
                project,
                pc2.DeviceId,
                q2.QueueSequence,
                new H2ProjectSyncBarrier(project, 0),
                now.AddSeconds(1),
                now.AddSeconds(1),
                now.AddMinutes(1));
            restarted.SaveProjectAiLease(lease2);

            var queue = restarted.GetProjectAiQueue(workspace, project);
            Equal(H2ProjectAiQueueState.Completed, queue[0].State);
            Equal(H2ProjectAiQueueState.Running, queue[1].State);
            Equal(lease2.LeaseId, restarted.GetRunningProjectAiLease(workspace, project)!.LeaseId);
        });
    }

    private static H2ProjectEventDraft Draft(
        Guid workspace,
        Guid project,
        Guid device,
        long deviceSequence,
        Guid clientOperationId,
        string field,
        string value,
        DateTimeOffset clientTime)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new { value });
        return new H2ProjectEventDraft(
            Guid.NewGuid(),
            workspace,
            project,
            device,
            deviceSequence,
            clientOperationId,
            H2ProjectEventKind.SetField,
            new H2ProjectMutationTarget(H2ProjectEntityKind.Project, project, field, expectedRevision: 0),
            payload,
            H2ProjectEventDraft.ComputePayloadSha256(payload),
            clientTime);
    }

    private static H2ProjectAiRequest Request(Guid workspace, Guid project, Guid device)
        => new(
            Guid.NewGuid(),
            workspace,
            project,
            device,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow);

    private static string Folder()
    {
        var path = Path.Combine(Path.GetTempPath(), "H2CoordinatorTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected {expected}; actual {actual}");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}

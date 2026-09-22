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

            var baseline = CreateProjectDraft(workspace, project, device.DeviceId, 1);
            var first = Draft(
                workspace, project, device.DeviceId, deviceSequence: 2,
                clientOperationId: Guid.NewGuid(),
                field: "NameRich",
                value: "First",
                clientTime: new DateTimeOffset(2026, 9, 21, 10, 5, 0, TimeSpan.FromHours(7)));
            var second = Draft(
                workspace, project, device.DeviceId, deviceSequence: 3,
                clientOperationId: Guid.NewGuid(),
                field: "NotesRich",
                value: "Second",
                clientTime: new DateTimeOffset(2026, 9, 21, 9, 55, 0, TimeSpan.FromHours(7)));

            var store = new H2CoordinatorSqliteStore(db);
            store.RegisterDevice(workspace, device);
            store.SubmitProjectEvents(workspace, device.DeviceId, new[] { baseline });
            var accepted = store.SubmitProjectEvents(workspace, device.DeviceId, new[] { first, second });

            Equal(2L, accepted.Acknowledgements[0].ServerSequence);
            Equal(3L, accepted.Acknowledgements[1].ServerSequence);
            Equal(3L, store.GetProjectHead(workspace, project).ServerSequence);

            var retry = store.SubmitProjectEvents(workspace, device.DeviceId, new[] { first });
            Equal(2L, retry.Acknowledgements.Single().ServerSequence);
            Equal(3, store.GetProjectEvents(workspace, project, 0).Count);

            var restarted = new H2CoordinatorSqliteStore(db);
            Equal(3L, restarted.GetProjectHead(workspace, project).ServerSequence);
            var replay = restarted.GetProjectEvents(workspace, project, 0);
            Equal(3, replay.Count);
            Equal(baseline.EventId, replay[0].Draft.EventId);
            Equal(first.EventId, replay[1].Draft.EventId);
            Equal(second.EventId, replay[2].Draft.EventId);

            // Device registration is durable: after restart the same device can submit without re-registering.
            var third = Draft(
                workspace, project, device.DeviceId, 4, Guid.NewGuid(),
                "CommentRich", "After restart", DateTimeOffset.UtcNow);
            var afterRestart = restarted.SubmitProjectEvents(workspace, device.DeviceId, new[] { third });
            Equal(4L, afterRestart.Acknowledgements.Single().ServerSequence);
        });

        test("Coordinator serializes concurrent client submissions into unique server order", () =>
        {
            var db = Path.Combine(Folder(), "coordinator.db");
            var workspace = Guid.NewGuid();
            var project = Guid.NewGuid();
            var pc1 = H2CoordinatorDeviceIdentity.CreateNew("PC1");
            var pc2 = H2CoordinatorDeviceIdentity.CreateNew("PC2");
            var store = new H2CoordinatorSqliteStore(db);
            store.RegisterDevice(workspace, pc1);
            store.RegisterDevice(workspace, pc2);

            var baseline = CreateProjectDraft(workspace, project, pc1.DeviceId, 1);
            store.SubmitProjectEvents(workspace, pc1.DeviceId, new[] { baseline });

            var a = Draft(workspace, project, pc1.DeviceId, 2, Guid.NewGuid(), "NameRich", "A", DateTimeOffset.UtcNow.AddMinutes(5));
            var b = Draft(workspace, project, pc2.DeviceId, 1, Guid.NewGuid(), "NotesRich", "B", DateTimeOffset.UtcNow.AddMinutes(-5));
            H2ProjectEventSubmissionResult? resultA = null;
            H2ProjectEventSubmissionResult? resultB = null;

            Parallel.Invoke(
                () => resultA = store.SubmitProjectEvents(workspace, pc1.DeviceId, new[] { a }),
                () => resultB = store.SubmitProjectEvents(workspace, pc2.DeviceId, new[] { b }));

            var sequences = new[]
            {
                resultA!.Acknowledgements.Single().ServerSequence,
                resultB!.Acknowledgements.Single().ServerSequence
            }.OrderBy(value => value).ToArray();

            Equal(2L, sequences[0]);
            Equal(3L, sequences[1]);
            Equal(3L, store.GetProjectHead(workspace, project).ServerSequence);
            Equal(3, store.GetProjectEvents(workspace, project, 0).Count);
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

            var baseline = CreateProjectDraft(workspace, project, device.DeviceId, 1);
            store.SubmitProjectEvents(workspace, device.DeviceId, new[] { baseline });

            var first = Draft(workspace, project, device.DeviceId, 2, operation, "NameRich", "A", DateTimeOffset.UtcNow);
            store.SubmitProjectEvents(workspace, device.DeviceId, new[] { first });

            var incompatible = Draft(workspace, project, device.DeviceId, 3, operation, "NameRich", "B", DateTimeOffset.UtcNow);
            Throws<InvalidOperationException>(() =>
                store.SubmitProjectEvents(workspace, device.DeviceId, new[] { incompatible }));

            Equal(2, store.GetProjectEvents(workspace, project, 0).Count);
        });

        test("Coordinator persists verified snapshot and structured conflict across restart", () =>
        {
            var db = Path.Combine(Folder(), "coordinator.db");
            var workspace = Guid.NewGuid();
            var project = Guid.NewGuid();
            var device = H2CoordinatorDeviceIdentity.CreateNew("PC1");
            var store = new H2CoordinatorSqliteStore(db);
            store.RegisterDevice(workspace, device);

            var baseline = CreateProjectDraft(workspace, project, device.DeviceId, 1);
            var a = Draft(workspace, project, device.DeviceId, 2, Guid.NewGuid(), "NameRich", "A", DateTimeOffset.UtcNow);
            var b = Draft(workspace, project, device.DeviceId, 3, Guid.NewGuid(), "NotesRich", "B", DateTimeOffset.UtcNow);
            store.SubmitProjectEvents(workspace, device.DeviceId, new[] { baseline, a, b });

            ProjectRecord? projected = null;
            foreach (var accepted in store.GetProjectEvents(workspace, project, 0)
                         .Where(item => item.Disposition == H2ProjectEventDisposition.Applied))
                projected = H2ProjectEventApplier.Apply(projected, accepted.Draft);

            var state = System.Text.Json.JsonSerializer.Serialize(projected);
            var snapshot = new H2ProjectSnapshot(
                workspace,
                project,
                3,
                state,
                H2ProjectEventDraft.ComputePayloadSha256(state),
                DateTimeOffset.UtcNow,
                store.GetProjectRevisions(workspace, project));
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
            Equal(3L, restarted.GetLatestSnapshot(workspace, project)!.ThroughServerSequence);
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
            var now = new DateTimeOffset(2026, 9, 21, 6, 0, 0, TimeSpan.Zero);
            var store = new H2CoordinatorSqliteStore(db, () => now);
            store.RegisterDevice(workspace, pc1);
            store.RegisterDevice(workspace, pc2);
            store.SubmitProjectEvents(workspace, pc1.DeviceId,
                new[] { CreateProjectDraft(workspace, project, pc1.DeviceId, 1) });

            var r1 = Request(workspace, project, pc1.DeviceId);
            var r2 = Request(workspace, project, pc2.DeviceId);
            var q1 = store.EnqueueProjectAi(r1);
            var q2 = store.EnqueueProjectAi(r2);
            Equal(1L, q1.QueueSequence);
            Equal(2L, q2.QueueSequence);

            Equal<H2ProjectAiLease?>(null,
                store.TryAcquireProjectAiLease(workspace, project, pc2.DeviceId));
            var lease1 = store.TryAcquireProjectAiLease(workspace, project, pc1.DeviceId)
                ?? throw new Exception("PC1 should acquire the FIFO head.");
            Equal(1L, lease1.Barrier.RequiredProjectSequence);
            Equal(true, store.ConfirmProjectAiBarrier(lease1.LeaseId, pc1.DeviceId, 1));

            var restarted = new H2CoordinatorSqliteStore(db, () => now);
            Equal(lease1.LeaseId, restarted.GetRunningProjectAiLease(workspace, project)!.LeaseId);
            Equal(2, restarted.GetProjectAiQueue(workspace, project).Count);
            Equal<H2ProjectAiLease?>(null,
                restarted.TryAcquireProjectAiLease(workspace, project, pc2.DeviceId));

            var assistant = new AiMessage
            {
                Id = Guid.NewGuid(),
                AiRunId = r1.RequestId,
                ParentId = r1.UserMessageId,
                Role = "assistant",
                Content = "Verified answer after restart",
                CreatedAt = now.UtcDateTime
            };
            var payload = H2ProjectEventPayload.Serialize(
                new H2ProjectMessageAppend(r1.ConversationId!.Value, "AI", assistant));
            var assistantEvent = new H2ProjectEventDraft(
                Guid.NewGuid(), workspace, project, pc1.DeviceId, 2, Guid.NewGuid(),
                H2ProjectEventKind.AppendMessage,
                new H2ProjectMutationTarget(H2ProjectEntityKind.Message, assistant.Id, expectedRevision: 0),
                payload, H2ProjectEventDraft.ComputePayloadSha256(payload), now,
                r1.RequestId, lease1.LeaseId);
            restarted.SubmitProjectEvents(workspace, pc1.DeviceId, new[] { assistantEvent });
            restarted.CompleteProjectAi(new H2ProjectAiCompletion(
                lease1.LeaseId,
                r1.RequestId,
                project,
                H2ProjectAiQueueState.Completed,
                2,
                assistantEvent.EventId,
                now));

            var lease2 = restarted.TryAcquireProjectAiLease(workspace, project, pc2.DeviceId)
                ?? throw new Exception("PC2 should acquire after PC1's committed completion.");
            Equal(r2.RequestId, lease2.RequestId);
            Equal(2L, lease2.Barrier.RequiredProjectSequence);
            Equal(false, restarted.ConfirmProjectAiBarrier(lease2.LeaseId, pc2.DeviceId, 1));
            Equal(H2ProjectAiQueueState.WaitingForSync,
                restarted.GetProjectAiQueue(workspace, project)[1].State);
            Equal(true, restarted.ConfirmProjectAiBarrier(lease2.LeaseId, pc2.DeviceId, 2));

            var queue = restarted.GetProjectAiQueue(workspace, project);
            Equal(H2ProjectAiQueueState.Completed, queue[0].State);
            Equal(H2ProjectAiQueueState.Running, queue[1].State);
            Equal(lease2.LeaseId, restarted.GetRunningProjectAiLease(workspace, project)!.LeaseId);
        });

        test("Coordinator AI enqueue retries preserve exact content and server acceptance time", () =>
        {
            var db = Path.Combine(Folder(), "coordinator.db");
            var workspace = Guid.NewGuid();
            var project = Guid.NewGuid();
            var device = H2CoordinatorDeviceIdentity.CreateNew("PC1");
            var serverNow = new DateTimeOffset(2026, 9, 21, 13, 0, 0, TimeSpan.FromHours(7));
            var store = new H2CoordinatorSqliteStore(db, () => serverNow);
            store.RegisterDevice(workspace, device);
            var request = Request(workspace, project, device.DeviceId);
            var queued = store.EnqueueProjectAi(request);
            Equal(serverNow.ToUniversalTime(), queued.AcceptedUtc);
            Equal(TimeSpan.Zero, queued.AcceptedUtc.Offset);

            serverNow = serverNow.AddMinutes(5);
            var restarted = new H2CoordinatorSqliteStore(db, () => serverNow);
            var retry = restarted.EnqueueProjectAi(request);
            Equal(queued, retry);

            H2ProjectAiRequest Changed(Guid? conversationId, Guid? userMessageId, DateTimeOffset clientTime)
                => new(request.RequestId, request.WorkspaceId, request.ProjectId,
                    request.OwnerDeviceId, request.ClientRequestId, conversationId,
                    userMessageId, clientTime);

            foreach (var incompatible in new[]
            {
                Changed(Guid.NewGuid(), request.UserMessageId, request.ClientCreatedUtc),
                Changed(null, request.UserMessageId, request.ClientCreatedUtc),
                Changed(request.ConversationId, Guid.NewGuid(), request.ClientCreatedUtc),
                Changed(request.ConversationId, null, request.ClientCreatedUtc),
                Changed(request.ConversationId, request.UserMessageId, request.ClientCreatedUtc.AddSeconds(1))
            })
                Throws<InvalidOperationException>(() => restarted.EnqueueProjectAi(incompatible));

            Equal(queued, restarted.GetProjectAiQueue(workspace, project).Single());
            var next = restarted.EnqueueProjectAi(Request(workspace, project, device.DeviceId));
            Equal(2L, next.QueueSequence);
            Equal(serverNow.ToUniversalTime(), next.AcceptedUtc);
        });
    }

    private static H2ProjectEventDraft CreateProjectDraft(
        Guid workspace,
        Guid project,
        Guid device,
        long deviceSequence)
    {
        var model = new ProjectRecord
        {
            Id = project,
            Name = "Baseline",
            NameRich = RichDocument.Plain("Baseline"),
            Notes = "",
            NotesRich = RichDocument.Plain("")
        };
        var payload = H2ProjectEventPayload.Serialize(model);
        return new H2ProjectEventDraft(
            Guid.NewGuid(),
            workspace,
            project,
            device,
            deviceSequence,
            Guid.NewGuid(),
            H2ProjectEventKind.CreateEntity,
            new H2ProjectMutationTarget(
                H2ProjectEntityKind.Project,
                project,
                expectedRevision: 0),
            payload,
            H2ProjectEventDraft.ComputePayloadSha256(payload),
            DateTimeOffset.UtcNow);
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

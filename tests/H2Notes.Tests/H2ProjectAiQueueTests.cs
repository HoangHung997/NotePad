using H2Notes.Coordinator;
using H2Notes.Core;

internal static class H2ProjectAiQueueTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Project AI grant and barrier confirmation recover lost responses across Coordinator restart", () =>
        {
            var fixture = Fixture();
            var client = Client(fixture, fixture.ProjectA, fixture.Pc1, "pc1");
            client.QueueCreateProject(Project(fixture.ProjectA, "Project A"));
            client.SynchronizeAsync().GetAwaiter().GetResult();
            fixture.Store.RegisterDevice(fixture.Workspace, fixture.Pc2);
            var request = Request(Guid.NewGuid(), fixture, fixture.ProjectA,
                fixture.Pc1, Guid.NewGuid(), null);
            fixture.Store.EnqueueProjectAi(request);
            var lease = fixture.Store.TryAcquireProjectAiLease(
                fixture.Workspace, fixture.ProjectA, fixture.Pc1.DeviceId)!;

            // Simulate a committed grant whose response never reached PC1.
            var restarted = Restart(fixture);
            Equal(lease, restarted.TryAcquireProjectAiLease(
                fixture.Workspace, fixture.ProjectA, fixture.Pc1.DeviceId));
            True(restarted.TryAcquireProjectAiLease(
                fixture.Workspace, fixture.ProjectA, fixture.Pc2.DeviceId) is null);
            True(!restarted.ConfirmProjectAiBarrier(lease.LeaseId,
                fixture.Pc1.DeviceId, lease.Barrier.RequiredProjectSequence - 1));
            True(!restarted.ConfirmProjectAiBarrier(lease.LeaseId,
                fixture.Pc1.DeviceId, long.MaxValue));

            client.SynchronizeThroughAsync(lease.Barrier).GetAwaiter().GetResult();
            True(restarted.ConfirmProjectAiBarrier(lease.LeaseId,
                fixture.Pc1.DeviceId, client.AppliedServerSequence));
            var runningLease = restarted.GetRunningProjectAiLease(fixture.Workspace, fixture.ProjectA)!;
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            restarted = Restart(fixture);
            True(restarted.ConfirmProjectAiBarrier(lease.LeaseId,
                fixture.Pc1.DeviceId, client.AppliedServerSequence));
            Equal(runningLease, restarted.GetRunningProjectAiLease(fixture.Workspace, fixture.ProjectA));
            True(!restarted.ConfirmProjectAiBarrier(lease.LeaseId,
                fixture.Pc2.DeviceId, client.AppliedServerSequence));
            True(restarted.TryAcquireProjectAiLease(
                fixture.Workspace, fixture.ProjectA, fixture.Pc1.DeviceId) is null);

            fixture.Clock.Advance(TimeSpan.FromSeconds(30));
            True(!restarted.ConfirmProjectAiBarrier(lease.LeaseId,
                fixture.Pc1.DeviceId, client.AppliedServerSequence));
            Equal(H2ProjectAiQueueState.WaitingForRepair,
                restarted.GetProjectAiQueue(fixture.Workspace, fixture.ProjectA).Single().State);
        });

        test("Project AI completion receipt survives lost response restart and later project turns", () =>
        {
            var fixture = Fixture();
            var client = Client(fixture, fixture.ProjectA, fixture.Pc1, "pc1");
            client.QueueCreateProject(Project(fixture.ProjectA, "Project A"));
            client.SynchronizeAsync().GetAwaiter().GetResult();
            fixture.Store.RegisterDevice(fixture.Workspace, fixture.Pc2);
            var conversation = Guid.NewGuid();
            var request = Request(Guid.NewGuid(), fixture, fixture.ProjectA,
                fixture.Pc1, conversation, null);
            fixture.Store.EnqueueProjectAi(request);
            var lease = fixture.Store.TryAcquireProjectAiLease(
                fixture.Workspace, fixture.ProjectA, fixture.Pc1.DeviceId)!;
            True(fixture.Store.ConfirmProjectAiBarrier(lease.LeaseId,
                fixture.Pc1.DeviceId, client.AppliedServerSequence));
            var message = client.QueueAppendMessage(conversation, "AI",
                Message("assistant", "Committed answer", request.RequestId),
                request.RequestId, lease.LeaseId);
            client.SynchronizeAsync().GetAwaiter().GetResult();
            var completion = new H2ProjectAiCompletion(lease.LeaseId, request.RequestId,
                fixture.ProjectA, H2ProjectAiQueueState.Completed,
                client.AppliedServerSequence, message.EventId, fixture.Clock.UtcNow);
            fixture.Store.CompleteProjectAi(completion);

            var restarted = Restart(fixture);
            restarted.CompleteProjectAi(completion);
            var next = Request(Guid.NewGuid(), fixture, fixture.ProjectA,
                fixture.Pc2, conversation, null);
            restarted.EnqueueProjectAi(next);
            var nextLease = restarted.TryAcquireProjectAiLease(
                fixture.Workspace, fixture.ProjectA, fixture.Pc2.DeviceId)!;
            True(restarted.ConfirmProjectAiBarrier(nextLease.LeaseId,
                fixture.Pc2.DeviceId, client.AppliedServerSequence));

            // A late ACK retry must neither release the next owner nor duplicate messages.
            restarted.CompleteProjectAi(completion);
            Equal(nextLease.LeaseId,
                restarted.GetRunningProjectAiLease(fixture.Workspace, fixture.ProjectA)!.LeaseId);
            Equal(2, restarted.GetProjectEvents(fixture.Workspace, fixture.ProjectA, 0).Count);
            Throws<InvalidOperationException>(() => restarted.CompleteProjectAi(
                new H2ProjectAiCompletion(lease.LeaseId, request.RequestId,
                    fixture.ProjectA, H2ProjectAiQueueState.Failed,
                    completion.CommittedThroughProjectSequence, message.EventId, completion.CompletedUtc)));
            Throws<InvalidOperationException>(() => restarted.CompleteProjectAi(
                new H2ProjectAiCompletion(Guid.NewGuid(), request.RequestId,
                    fixture.ProjectA, H2ProjectAiQueueState.Completed,
                    completion.CommittedThroughProjectSequence, message.EventId, completion.CompletedUtc)));
            Equal(H2ProjectAiQueueState.Completed,
                restarted.GetProjectAiQueue(fixture.Workspace, fixture.ProjectA)[0].State);
        });

        test("Project AI queue is FIFO while user messages remain shared and different projects run concurrently", () =>
        {
            var fixture = Fixture();
            var conversation = Guid.NewGuid();
            var pc1Client = Client(fixture, fixture.ProjectA, fixture.Pc1, "pc1-a");
            var pc2Client = Client(fixture, fixture.ProjectA, fixture.Pc2, "pc2-a");

            pc1Client.QueueCreateProject(Project(fixture.ProjectA, "Project A"));
            pc1Client.SynchronizeAsync().GetAwaiter().GetResult();
            pc2Client.SynchronizeAsync().GetAwaiter().GetResult();

            var request1Id = Guid.NewGuid();
            var user1 = Message("user", "PC1 asks first", request1Id);
            pc1Client.QueueAppendMessage(conversation, "Shared AI", user1);
            pc1Client.SynchronizeAsync().GetAwaiter().GetResult();
            var request1 = Request(
                request1Id, fixture, fixture.ProjectA, fixture.Pc1, conversation, user1.Id);
            var queued1 = fixture.Service.EnqueueProjectAiAsync(request1).GetAwaiter().GetResult();
            Equal(1L, queued1.QueueSequence);

            var request2Id = Guid.NewGuid();
            var user2 = Message("user", "PC2 asks second", request2Id);
            pc2Client.QueueAppendMessage(conversation, "Shared AI", user2);
            pc2Client.SynchronizeAsync().GetAwaiter().GetResult();
            var request2 = Request(
                request2Id, fixture, fixture.ProjectA, fixture.Pc2, conversation, user2.Id);
            var queued2 = fixture.Service.EnqueueProjectAiAsync(request2).GetAwaiter().GetResult();
            Equal(2L, queued2.QueueSequence);

            var lease1 = fixture.Service.TryAcquireProjectAiLeaseAsync(
                fixture.Workspace, fixture.ProjectA, fixture.Pc1.DeviceId)
                .GetAwaiter().GetResult()
                ?? throw new Exception("PC1 should acquire first project-A lease.");
            Equal(3L, lease1.Barrier.RequiredProjectSequence);

            // PC2's user message is already durable/visible, but PC2 cannot run AI yet.
            True(fixture.Service.TryAcquireProjectAiLeaseAsync(
                fixture.Workspace, fixture.ProjectA, fixture.Pc2.DeviceId)
                .GetAwaiter().GetResult() is null);
            var queueBeforeRun = fixture.Service.GetProjectAiQueueAsync(
                fixture.Workspace, fixture.ProjectA).GetAwaiter().GetResult();
            Equal(H2ProjectAiQueueState.WaitingForSync, queueBeforeRun[0].State);
            Equal(H2ProjectAiQueueState.Waiting, queueBeforeRun[1].State);

            pc1Client.SynchronizeThroughAsync(lease1.Barrier).GetAwaiter().GetResult();
            True(fixture.Service.ConfirmProjectAiBarrierAsync(
                lease1.LeaseId,
                fixture.Pc1.DeviceId,
                pc1Client.AppliedServerSequence).GetAwaiter().GetResult());

            // Project B is independent and may run while Project A is running on PC1.
            var pc2ProjectB = Client(fixture, fixture.ProjectB, fixture.Pc2, "pc2-b");
            pc2ProjectB.QueueCreateProject(Project(fixture.ProjectB, "Project B"));
            pc2ProjectB.SynchronizeAsync().GetAwaiter().GetResult();
            var requestB = Request(
                Guid.NewGuid(), fixture, fixture.ProjectB, fixture.Pc2, Guid.NewGuid(), null);
            fixture.Service.EnqueueProjectAiAsync(requestB).GetAwaiter().GetResult();
            var leaseB = fixture.Service.TryAcquireProjectAiLeaseAsync(
                fixture.Workspace, fixture.ProjectB, fixture.Pc2.DeviceId)
                .GetAwaiter().GetResult()
                ?? throw new Exception("Project B should acquire independently.");
            pc2ProjectB.SynchronizeThroughAsync(leaseB.Barrier).GetAwaiter().GetResult();
            True(fixture.Service.ConfirmProjectAiBarrierAsync(
                leaseB.LeaseId,
                fixture.Pc2.DeviceId,
                pc2ProjectB.AppliedServerSequence).GetAwaiter().GetResult());

            var assistant1 = Message("assistant", "PC1 answer", request1Id);
            var assistantEvent = pc1Client.QueueAppendMessage(
                conversation,
                "Shared AI",
                assistant1,
                request1Id,
                lease1.LeaseId);
            pc1Client.SynchronizeAsync().GetAwaiter().GetResult();

            fixture.Service.CompleteProjectAiAsync(new H2ProjectAiCompletion(
                lease1.LeaseId,
                request1Id,
                fixture.ProjectA,
                H2ProjectAiQueueState.Completed,
                pc1Client.AppliedServerSequence,
                assistantEvent.EventId,
                fixture.Clock.UtcNow)).GetAwaiter().GetResult();

            var lease2 = fixture.Service.TryAcquireProjectAiLeaseAsync(
                fixture.Workspace, fixture.ProjectA, fixture.Pc2.DeviceId)
                .GetAwaiter().GetResult()
                ?? throw new Exception("PC2 should acquire after PC1 completed.");
            Equal(pc1Client.AppliedServerSequence, lease2.Barrier.RequiredProjectSequence);

            pc2Client.SynchronizeThroughAsync(lease2.Barrier).GetAwaiter().GetResult();
            True(fixture.Service.ConfirmProjectAiBarrierAsync(
                lease2.LeaseId,
                fixture.Pc2.DeviceId,
                pc2Client.AppliedServerSequence).GetAwaiter().GetResult());

            var messages = pc2Client.CurrentProject!.Conversations
                .Single(item => item.Id == conversation)
                .Messages;
            Equal(3, messages.Count);
            Equal("PC1 asks first", messages[0].Content);
            Equal("PC2 asks second", messages[1].Content);
            Equal("PC1 answer", messages[2].Content);
            True(messages[0].Sequence < messages[1].Sequence
                 && messages[1].Sequence < messages[2].Sequence);

            var finalQueue = fixture.Service.GetProjectAiQueueAsync(
                fixture.Workspace, fixture.ProjectA).GetAwaiter().GetResult();
            Equal(H2ProjectAiQueueState.Completed, finalQueue[0].State);
            Equal(H2ProjectAiQueueState.Running, finalQueue[1].State);
        });

        test("AI project events are rejected before sync barrier and completion requires committed assistant message", () =>
        {
            var fixture = Fixture();
            var client = Client(fixture, fixture.ProjectA, fixture.Pc1, "pc1");
            client.QueueCreateProject(Project(fixture.ProjectA, "Project A"));
            client.SynchronizeAsync().GetAwaiter().GetResult();

            var requestId = Guid.NewGuid();
            var request = Request(
                requestId, fixture, fixture.ProjectA, fixture.Pc1, Guid.NewGuid(), null);
            fixture.Service.EnqueueProjectAiAsync(request).GetAwaiter().GetResult();
            var lease = fixture.Service.TryAcquireProjectAiLeaseAsync(
                fixture.Workspace, fixture.ProjectA, fixture.Pc1.DeviceId)
                .GetAwaiter().GetResult()
                ?? throw new Exception("Lease missing.");

            var assistant = Message("assistant", "Too early", requestId);
            var tooEarly = AppendDraft(
                fixture,
                fixture.ProjectA,
                fixture.Pc1,
                deviceSequence: 2,
                conversationId: Guid.NewGuid(),
                assistant,
                requestId,
                lease.LeaseId);

            Throws<InvalidOperationException>(() =>
                fixture.Store.SubmitProjectEvents(
                    fixture.Workspace, fixture.Pc1.DeviceId, new[] { tooEarly }));

            True(fixture.Service.ConfirmProjectAiBarrierAsync(
                lease.LeaseId,
                fixture.Pc1.DeviceId,
                client.AppliedServerSequence).GetAwaiter().GetResult());

            Throws<InvalidOperationException>(() =>
                fixture.Service.CompleteProjectAiAsync(new H2ProjectAiCompletion(
                    lease.LeaseId,
                    requestId,
                    fixture.ProjectA,
                    H2ProjectAiQueueState.Completed,
                    client.AppliedServerSequence,
                    null,
                    fixture.Clock.UtcNow)).GetAwaiter().GetResult());

            var accepted = fixture.Store.SubmitProjectEvents(
                fixture.Workspace, fixture.Pc1.DeviceId, new[] { tooEarly });
            Equal(2L, accepted.Acknowledgements.Single().ServerSequence);

            fixture.Service.CompleteProjectAiAsync(new H2ProjectAiCompletion(
                lease.LeaseId,
                requestId,
                fixture.ProjectA,
                H2ProjectAiQueueState.Completed,
                2,
                tooEarly.EventId,
                fixture.Clock.UtcNow)).GetAwaiter().GetResult();

            Equal(H2ProjectAiQueueState.Completed,
                fixture.Service.GetProjectAiQueueAsync(
                    fixture.Workspace, fixture.ProjectA)
                    .GetAwaiter().GetResult().Single().State);
        });

        test("Running AI lease expiry enters repair and blocks FIFO until explicit recovery", () =>
        {
            var fixture = Fixture();
            var client = Client(fixture, fixture.ProjectA, fixture.Pc1, "pc1");
            client.QueueCreateProject(Project(fixture.ProjectA, "Project A"));
            client.SynchronizeAsync().GetAwaiter().GetResult();
            fixture.Store.RegisterDevice(fixture.Workspace, fixture.Pc2);

            var r1 = Request(
                Guid.NewGuid(), fixture, fixture.ProjectA, fixture.Pc1, Guid.NewGuid(), null);
            var r2 = Request(
                Guid.NewGuid(), fixture, fixture.ProjectA, fixture.Pc2, Guid.NewGuid(), null);
            fixture.Service.EnqueueProjectAiAsync(r1).GetAwaiter().GetResult();
            fixture.Service.EnqueueProjectAiAsync(r2).GetAwaiter().GetResult();

            var lease1 = fixture.Service.TryAcquireProjectAiLeaseAsync(
                fixture.Workspace, fixture.ProjectA, fixture.Pc1.DeviceId)
                .GetAwaiter().GetResult()
                ?? throw new Exception("First lease missing.");
            True(fixture.Service.ConfirmProjectAiBarrierAsync(
                lease1.LeaseId,
                fixture.Pc1.DeviceId,
                client.AppliedServerSequence).GetAwaiter().GetResult());

            // Client heartbeat time is deliberately absurd; server time remains authoritative.
            True(fixture.Service.HeartbeatProjectAiLeaseAsync(
                lease1.LeaseId,
                fixture.Pc1.DeviceId,
                fixture.Clock.UtcNow.AddYears(20)).GetAwaiter().GetResult());
            var refreshed = fixture.Store.GetRunningProjectAiLease(
                fixture.Workspace, fixture.ProjectA)
                ?? throw new Exception("Refreshed lease missing.");
            Equal(fixture.Clock.UtcNow.AddSeconds(30), refreshed.ExpiresUtc);

            fixture.Clock.Advance(TimeSpan.FromSeconds(31));
            True(fixture.Service.TryAcquireProjectAiLeaseAsync(
                fixture.Workspace, fixture.ProjectA, fixture.Pc2.DeviceId)
                .GetAwaiter().GetResult() is null);

            var blocked = fixture.Service.GetProjectAiQueueAsync(
                fixture.Workspace, fixture.ProjectA).GetAwaiter().GetResult();
            Equal(H2ProjectAiQueueState.WaitingForRepair, blocked[0].State);
            Equal(H2ProjectAiQueueState.Waiting, blocked[1].State);

            True(fixture.Service.ResolveInterruptedProjectAiAsync(
                lease1.LeaseId,
                fixture.Pc1.DeviceId,
                H2ProjectAiQueueState.Abandoned).GetAwaiter().GetResult());

            var lease2 = fixture.Service.TryAcquireProjectAiLeaseAsync(
                fixture.Workspace, fixture.ProjectA, fixture.Pc2.DeviceId)
                .GetAwaiter().GetResult()
                ?? throw new Exception("Second lease should acquire after explicit recovery.");
            Equal(2L, lease2.QueueSequence);

            // If the new owner never confirms the barrier, expiry is safe to auto-abandon.
            var r3 = Request(
                Guid.NewGuid(), fixture, fixture.ProjectA, fixture.Pc1, Guid.NewGuid(), null);
            fixture.Service.EnqueueProjectAiAsync(r3).GetAwaiter().GetResult();
            fixture.Clock.Advance(TimeSpan.FromSeconds(31));
            var lease3 = fixture.Service.TryAcquireProjectAiLeaseAsync(
                fixture.Workspace, fixture.ProjectA, fixture.Pc1.DeviceId)
                .GetAwaiter().GetResult()
                ?? throw new Exception("Unstarted expired lease should auto-abandon.");
            Equal(3L, lease3.QueueSequence);

            var queue = fixture.Service.GetProjectAiQueueAsync(
                fixture.Workspace, fixture.ProjectA).GetAwaiter().GetResult();
            Equal(H2ProjectAiQueueState.Abandoned, queue[0].State);
            Equal(H2ProjectAiQueueState.Abandoned, queue[1].State);
            Equal(H2ProjectAiQueueState.WaitingForSync, queue[2].State);
        });

        test("AI conflicting mutation prevents Completed and keeps next request blocked until review is resolved", () =>
        {
            var fixture = Fixture();
            var client1 = Client(fixture, fixture.ProjectA, fixture.Pc1, "pc1");
            var client2 = Client(fixture, fixture.ProjectA, fixture.Pc2, "pc2");
            client1.QueueCreateProject(Project(fixture.ProjectA, "Project A"));
            client1.SynchronizeAsync().GetAwaiter().GetResult();
            client2.SynchronizeAsync().GetAwaiter().GetResult();

            var request = Request(
                Guid.NewGuid(), fixture, fixture.ProjectA, fixture.Pc1, Guid.NewGuid(), null);
            var next = Request(
                Guid.NewGuid(), fixture, fixture.ProjectA, fixture.Pc2, Guid.NewGuid(), null);
            fixture.Service.EnqueueProjectAiAsync(request).GetAwaiter().GetResult();
            fixture.Service.EnqueueProjectAiAsync(next).GetAwaiter().GetResult();

            var lease = fixture.Service.TryAcquireProjectAiLeaseAsync(
                fixture.Workspace, fixture.ProjectA, fixture.Pc1.DeviceId)
                .GetAwaiter().GetResult()
                ?? throw new Exception("Lease missing.");
            True(fixture.Service.ConfirmProjectAiBarrierAsync(
                lease.LeaseId,
                fixture.Pc1.DeviceId,
                client1.AppliedServerSequence).GetAwaiter().GetResult());

            // Human PC2 advances NameRich first.
            client2.QueueSetProjectName(RichDocument.Plain("Human value"));
            client2.SynchronizeAsync().GetAwaiter().GetResult();

            var aiPayload = H2ProjectEventPayload.Serialize(
                RichDocument.Plain("AI stale value"));
            var aiConflict = new H2ProjectEventDraft(
                Guid.NewGuid(),
                fixture.Workspace,
                fixture.ProjectA,
                fixture.Pc1.DeviceId,
                2,
                Guid.NewGuid(),
                H2ProjectEventKind.SetField,
                new H2ProjectMutationTarget(
                    H2ProjectEntityKind.Project,
                    fixture.ProjectA,
                    "NameRich",
                    expectedRevision: 0),
                aiPayload,
                H2ProjectEventDraft.ComputePayloadSha256(aiPayload),
                fixture.Clock.UtcNow,
                request.RequestId,
                lease.LeaseId);
            var conflictResult = fixture.Store.SubmitProjectEvents(
                fixture.Workspace, fixture.Pc1.DeviceId, new[] { aiConflict });
            Equal(1, conflictResult.Conflicts.Count);

            var assistant = Message("assistant", "Needs review", request.RequestId);
            var assistantDraft = AppendDraft(
                fixture,
                fixture.ProjectA,
                fixture.Pc1,
                3,
                Guid.NewGuid(),
                assistant,
                request.RequestId,
                lease.LeaseId);
            fixture.Store.SubmitProjectEvents(
                fixture.Workspace, fixture.Pc1.DeviceId, new[] { assistantDraft });

            var head = fixture.Store.GetProjectHead(
                fixture.Workspace, fixture.ProjectA).ServerSequence;
            Throws<InvalidOperationException>(() =>
                fixture.Service.CompleteProjectAiAsync(new H2ProjectAiCompletion(
                    lease.LeaseId,
                    request.RequestId,
                    fixture.ProjectA,
                    H2ProjectAiQueueState.Completed,
                    head,
                    assistantDraft.EventId,
                    fixture.Clock.UtcNow)).GetAwaiter().GetResult());

            fixture.Service.CompleteProjectAiAsync(new H2ProjectAiCompletion(
                lease.LeaseId,
                request.RequestId,
                fixture.ProjectA,
                H2ProjectAiQueueState.NeedsUserReview,
                head,
                assistantDraft.EventId,
                fixture.Clock.UtcNow)).GetAwaiter().GetResult();

            True(fixture.Service.TryAcquireProjectAiLeaseAsync(
                fixture.Workspace, fixture.ProjectA, fixture.Pc2.DeviceId)
                .GetAwaiter().GetResult() is null);

            True(fixture.Service.ResolveInterruptedProjectAiAsync(
                lease.LeaseId,
                fixture.Pc1.DeviceId,
                H2ProjectAiQueueState.Failed).GetAwaiter().GetResult());

            True(fixture.Service.TryAcquireProjectAiLeaseAsync(
                fixture.Workspace, fixture.ProjectA, fixture.Pc2.DeviceId)
                .GetAwaiter().GetResult() is not null);
        });
    }

    private static H2CoordinatorSqliteStore Restart(FixtureState fixture)
        => new(Path.Combine(fixture.Root, "coordinator.db"), () => fixture.Clock.UtcNow);

    private static FixtureState Fixture()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "H2ProjectAiQueueTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var clock = new MutableClock(new DateTimeOffset(
            2026, 9, 21, 6, 0, 0, TimeSpan.Zero));
        var store = new H2CoordinatorSqliteStore(
            Path.Combine(root, "coordinator.db"),
            () => clock.UtcNow);
        var service = new H2SyncCoordinatorService(store);
        return new FixtureState(
            root,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            H2CoordinatorDeviceIdentity.CreateNew("PC1"),
            H2CoordinatorDeviceIdentity.CreateNew("PC2"),
            clock,
            store,
            service);
    }

    private static H2ProjectSyncClient Client(
        FixtureState fixture,
        Guid projectId,
        H2CoordinatorDeviceIdentity device,
        string folder)
        => new(
            fixture.Workspace,
            projectId,
            device,
            fixture.Service,
            new H2CoordinatorClientStateStore(
                Path.Combine(fixture.Root, folder),
                fixture.Workspace,
                projectId,
                device.DeviceId));

    private static ProjectRecord Project(Guid id, string name)
        => new()
        {
            Id = id,
            Name = name,
            NameRich = RichDocument.Plain(name),
            Notes = "",
            NotesRich = RichDocument.Plain("")
        };

    private static H2ProjectAiRequest Request(
        Guid requestId,
        FixtureState fixture,
        Guid projectId,
        H2CoordinatorDeviceIdentity device,
        Guid conversationId,
        Guid? userMessageId)
        => new(
            requestId,
            fixture.Workspace,
            projectId,
            device.DeviceId,
            Guid.NewGuid(),
            conversationId,
            userMessageId,
            fixture.Clock.UtcNow);

    private static AiMessage Message(string role, string content, Guid requestId)
        => new()
        {
            Id = Guid.NewGuid(),
            AiRunId = requestId,
            Role = role,
            Content = content,
            CreatedAt = DateTime.UtcNow
        };

    private static H2ProjectEventDraft AppendDraft(
        FixtureState fixture,
        Guid projectId,
        H2CoordinatorDeviceIdentity device,
        long deviceSequence,
        Guid conversationId,
        AiMessage message,
        Guid requestId,
        Guid leaseId)
    {
        var payload = H2ProjectEventPayload.Serialize(
            new H2ProjectMessageAppend(conversationId, "AI", message));
        return new H2ProjectEventDraft(
            Guid.NewGuid(),
            fixture.Workspace,
            projectId,
            device.DeviceId,
            deviceSequence,
            Guid.NewGuid(),
            H2ProjectEventKind.AppendMessage,
            new H2ProjectMutationTarget(
                H2ProjectEntityKind.Message,
                message.Id,
                expectedRevision: 0),
            payload,
            H2ProjectEventDraft.ComputePayloadSha256(payload),
            fixture.Clock.UtcNow,
            requestId,
            leaseId);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected {expected}; actual {actual}");
    }

    private static void True(bool value)
    {
        if (!value) throw new Exception("Expected true");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class MutableClock(DateTimeOffset utcNow)
    {
        public DateTimeOffset UtcNow { get; private set; } = utcNow;
        public void Advance(TimeSpan value) => UtcNow = UtcNow.Add(value);
    }

    private sealed record FixtureState(
        string Root,
        Guid Workspace,
        Guid ProjectA,
        Guid ProjectB,
        H2CoordinatorDeviceIdentity Pc1,
        H2CoordinatorDeviceIdentity Pc2,
        MutableClock Clock,
        H2CoordinatorSqliteStore Store,
        H2SyncCoordinatorService Service);
}

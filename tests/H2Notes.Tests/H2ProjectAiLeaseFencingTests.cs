using H2Notes.Coordinator;
using H2Notes.Core;

internal static class H2ProjectAiLeaseFencingTests
{
    public static void Run(Action<string, Action> test)
    {
        test("AI conflict resolution rejects expired, completed, wrong-device and nonexistent leases without changing project truth", () =>
        {
            foreach (var invalidLease in new[] { "expired", "completed", "wrong-device", "nonexistent" })
            {
                var fixture = new Fixture();
                var running = Start(fixture, fixture.Pc1);
                var device = invalidLease == "wrong-device" ? fixture.Pc2 : fixture.Pc1;
                var leaseId = invalidLease == "nonexistent" ? Guid.NewGuid() : running.Lease.LeaseId;

                if (invalidLease == "expired")
                    fixture.Clock.Advance(TimeSpan.FromSeconds(31));
                else if (invalidLease == "completed")
                    CompleteWithAssistant(fixture, running);

                var resolution = Resolution(fixture, device, running.Request.RequestId, leaseId);
                var before = State(fixture);
                Throws<InvalidOperationException>(() =>
                    fixture.Service.ResolveConflictAsync(
                        fixture.Workspace, fixture.ConflictId, resolution).GetAwaiter().GetResult());
                Equal(before, State(fixture));
                Equal(H2ProjectConflictState.Open, State(fixture).ConflictState);
                AssertClientsSee(fixture, "Human winner");
            }
        });

        test("Running AI can resolve a conflict and its exact resolution retry remains safe after completion", () =>
        {
            var fixture = new Fixture();
            var running = Start(fixture, fixture.Pc1);
            var resolution = Resolution(
                fixture, fixture.Pc1, running.Request.RequestId, running.Lease.LeaseId);
            var before = State(fixture);
            var acknowledgement = fixture.Service.ResolveConflictAsync(
                fixture.Workspace, fixture.ConflictId, resolution).GetAwaiter().GetResult();

            var resolved = State(fixture);
            Equal(before.Sequence + 1, acknowledgement.ServerSequence);
            Equal(acknowledgement.ServerSequence, resolved.Sequence);
            Equal(before.EventCount + 1, resolved.EventCount);
            Equal("AI resolution", resolved.Name);
            Equal(H2ProjectConflictState.Resolved, resolved.ConflictState);
            Equal<Guid?>(resolution.EventId, resolved.ResolvedByEventId);
            Equal(0, resolved.OpenConflictCount);

            CompleteWithAssistant(fixture, running);
            fixture.Clock.Advance(TimeSpan.FromSeconds(31));
            var completed = State(fixture);
            var retry = fixture.Service.ResolveConflictAsync(
                fixture.Workspace, fixture.ConflictId, resolution).GetAwaiter().GetResult();
            Equal(acknowledgement, retry);
            Equal(completed, State(fixture));
            Equal(H2ProjectAiQueueState.Completed, fixture.Service.GetProjectAiQueueAsync(
                fixture.Workspace, fixture.Project).GetAwaiter().GetResult().Single().State);
            AssertClientsSee(fixture, "AI resolution");
        });

        test("Restarted Coordinator blocks expired Running lease until recovery and fences old owner after next run starts", () =>
        {
            var fixture = new Fixture();
            var first = Start(fixture, fixture.Pc1);
            var nextRequest = Request(fixture, fixture.Pc2);
            fixture.Service.EnqueueProjectAiAsync(nextRequest).GetAwaiter().GetResult();

            fixture.Restart();
            fixture.Clock.Advance(TimeSpan.FromSeconds(31));
            True(fixture.Service.TryAcquireProjectAiLeaseAsync(
                fixture.Workspace, fixture.Project, fixture.Pc2.DeviceId).GetAwaiter().GetResult() is null);
            var blocked = fixture.Service.GetProjectAiQueueAsync(
                fixture.Workspace, fixture.Project).GetAwaiter().GetResult();
            Equal(H2ProjectAiQueueState.WaitingForRepair, blocked[0].State);
            Equal(H2ProjectAiQueueState.Waiting, blocked[1].State);
            True(!fixture.Service.HeartbeatProjectAiLeaseAsync(
                first.Lease.LeaseId, fixture.Pc1.DeviceId, fixture.Clock.UtcNow).GetAwaiter().GetResult());
            True(fixture.Service.ResolveInterruptedProjectAiAsync(
                first.Lease.LeaseId, fixture.Pc1.DeviceId, H2ProjectAiQueueState.Abandoned)
                .GetAwaiter().GetResult());

            var next = Grant(fixture, nextRequest);
            Equal(2L, next.Lease.QueueSequence);
            Equal(H2ProjectAiQueueState.Running, fixture.Service.GetProjectAiQueueAsync(
                fixture.Workspace, fixture.Project).GetAwaiter().GetResult()[1].State);

            var before = State(fixture);
            var staleResolution = Resolution(
                fixture, fixture.Pc1, first.Request.RequestId, first.Lease.LeaseId);
            Throws<InvalidOperationException>(() =>
                fixture.Service.ResolveConflictAsync(
                    fixture.Workspace, fixture.ConflictId, staleResolution).GetAwaiter().GetResult());
            Equal(before, State(fixture));

            var staleMutation = Draft(
                fixture, fixture.Pc1,
                H2ProjectEventKind.SetField,
                new H2ProjectMutationTarget(H2ProjectEntityKind.Project, fixture.Project, "NotesRich", 0),
                H2ProjectEventPayload.Serialize(RichDocument.Plain("Late old owner write")),
                first.Request.RequestId, first.Lease.LeaseId);
            Throws<InvalidOperationException>(() =>
                fixture.Service.SubmitProjectEventsAsync(
                    fixture.Workspace, fixture.Pc1.DeviceId, new[] { staleMutation }).GetAwaiter().GetResult());
            Equal(before, State(fixture));
            True(!fixture.Service.ConfirmProjectAiBarrierAsync(
                first.Lease.LeaseId, fixture.Pc1.DeviceId, before.Sequence).GetAwaiter().GetResult());
            True(!fixture.Service.HeartbeatProjectAiLeaseAsync(
                first.Lease.LeaseId, fixture.Pc1.DeviceId, fixture.Clock.UtcNow).GetAwaiter().GetResult());

            var validResolution = Resolution(
                fixture, fixture.Pc2, next.Request.RequestId, next.Lease.LeaseId);
            fixture.Service.ResolveConflictAsync(
                fixture.Workspace, fixture.ConflictId, validResolution).GetAwaiter().GetResult();
            Equal(before.Sequence + 1, State(fixture).Sequence);
            AssertClientsSee(fixture, "AI resolution");
        });
    }

    private static RunningRequest Start(Fixture fixture, H2CoordinatorDeviceIdentity device)
    {
        var request = Request(fixture, device);
        fixture.Service.EnqueueProjectAiAsync(request).GetAwaiter().GetResult();
        return Grant(fixture, request);
    }

    private static RunningRequest Grant(Fixture fixture, H2ProjectAiRequest request)
    {
        var lease = fixture.Service.TryAcquireProjectAiLeaseAsync(
            fixture.Workspace, fixture.Project, request.OwnerDeviceId).GetAwaiter().GetResult()
            ?? throw new Exception("Expected the FIFO head to receive a lease.");
        var client = request.OwnerDeviceId == fixture.Pc1.DeviceId ? fixture.Client1 : fixture.Client2;
        client.SynchronizeThroughAsync(lease.Barrier).GetAwaiter().GetResult();
        True(fixture.Service.ConfirmProjectAiBarrierAsync(
            lease.LeaseId, request.OwnerDeviceId, client.AppliedServerSequence).GetAwaiter().GetResult());
        return new RunningRequest(request, lease);
    }

    private static H2ProjectAiRequest Request(Fixture fixture, H2CoordinatorDeviceIdentity device)
        => new(Guid.NewGuid(), fixture.Workspace, fixture.Project, device.DeviceId,
            Guid.NewGuid(), Guid.NewGuid(), null, fixture.Clock.UtcNow);

    private static H2ProjectEventDraft Resolution(
        Fixture fixture,
        H2CoordinatorDeviceIdentity device,
        Guid requestId,
        Guid leaseId)
        => Draft(
            fixture, device,
            H2ProjectEventKind.ResolveConflict,
            new H2ProjectMutationTarget(H2ProjectEntityKind.Project, fixture.Project, "NameRich", 1),
            H2ProjectEventPayload.Serialize(new H2ConflictResolutionPayload(
                H2ConflictResolutionAction.SetField,
                H2ProjectEventPayload.Serialize(RichDocument.Plain("AI resolution")))),
            requestId, leaseId);

    private static H2ProjectEventDraft Draft(
        Fixture fixture,
        H2CoordinatorDeviceIdentity device,
        H2ProjectEventKind kind,
        H2ProjectMutationTarget target,
        string payload,
        Guid requestId,
        Guid leaseId)
    {
        var sequence = fixture.Store.GetProjectEvents(fixture.Workspace, fixture.Project, 0)
            .Where(item => item.Draft.DeviceId == device.DeviceId)
            .Max(item => item.Draft.DeviceSequence) + 1;
        return new H2ProjectEventDraft(
            Guid.NewGuid(), fixture.Workspace, fixture.Project, device.DeviceId, sequence,
            Guid.NewGuid(), kind, target, payload, H2ProjectEventDraft.ComputePayloadSha256(payload),
            fixture.Clock.UtcNow, requestId, leaseId);
    }

    private static void CompleteWithAssistant(Fixture fixture, RunningRequest running)
    {
        var message = new AiMessage
        {
            Id = Guid.NewGuid(),
            AiRunId = running.Request.RequestId,
            Role = "assistant",
            Content = "Committed answer",
            CreatedAt = fixture.Clock.UtcNow.UtcDateTime
        };
        var device = running.Request.OwnerDeviceId == fixture.Pc1.DeviceId ? fixture.Pc1 : fixture.Pc2;
        var assistant = Draft(
            fixture, device, H2ProjectEventKind.AppendMessage,
            new H2ProjectMutationTarget(H2ProjectEntityKind.Message, message.Id, expectedRevision: 0),
            H2ProjectEventPayload.Serialize(new H2ProjectMessageAppend(
                running.Request.ConversationId!.Value, "AI fencing", message)),
            running.Request.RequestId, running.Lease.LeaseId);
        var accepted = fixture.Service.SubmitProjectEventsAsync(
            fixture.Workspace, device.DeviceId, new[] { assistant }).GetAwaiter().GetResult();
        Equal(0, accepted.Conflicts.Count);
        fixture.Service.CompleteProjectAiAsync(new H2ProjectAiCompletion(
            running.Lease.LeaseId, running.Request.RequestId, fixture.Project,
            H2ProjectAiQueueState.Completed, accepted.Acknowledgements.Single().ServerSequence,
            assistant.EventId, fixture.Clock.UtcNow)).GetAwaiter().GetResult();
    }

    private static ProjectState State(Fixture fixture)
    {
        var head = fixture.Store.GetProjectHead(fixture.Workspace, fixture.Project);
        var events = fixture.Store.GetProjectEvents(fixture.Workspace, fixture.Project, 0);
        ProjectRecord? project = null;
        foreach (var accepted in events.Where(item => item.Disposition == H2ProjectEventDisposition.Applied))
            project = H2ProjectEventApplier.Apply(project, accepted);
        var conflict = fixture.Store.GetConflicts(fixture.Workspace, fixture.Project).Single();
        return new ProjectState(
            head.ServerSequence, events.Count, project!.DisplayName, project.NotesText,
            head.OpenConflictCount, conflict.State, conflict.ResolvedByEventId);
    }

    private static void AssertClientsSee(Fixture fixture, string name)
    {
        fixture.Client1.SynchronizeAsync().GetAwaiter().GetResult();
        fixture.Client2.SynchronizeAsync().GetAwaiter().GetResult();
        Equal(name, fixture.Client1.CurrentProject!.DisplayName);
        Equal(name, fixture.Client2.CurrentProject!.DisplayName);
        Equal("", fixture.Client1.CurrentProject.NotesText);
        Equal("", fixture.Client2.CurrentProject.NotesText);
        Equal(State(fixture).Sequence, fixture.Client1.AppliedServerSequence);
        Equal(State(fixture).Sequence, fixture.Client2.AppliedServerSequence);
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

    private sealed record RunningRequest(H2ProjectAiRequest Request, H2ProjectAiLease Lease);
    private sealed record ProjectState(
        long Sequence, int EventCount, string Name, string Notes, int OpenConflictCount,
        H2ProjectConflictState ConflictState, Guid? ResolvedByEventId);

    private sealed class MutableClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 9, 21, 6, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan elapsed) => UtcNow = UtcNow.Add(elapsed);
    }

    private sealed class Fixture
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "H2ProjectAiLeaseFencingTests-" + Guid.NewGuid().ToString("N"));
        public Guid Workspace { get; } = Guid.NewGuid();
        public Guid Project { get; } = Guid.NewGuid();
        public H2CoordinatorDeviceIdentity Pc1 { get; } = H2CoordinatorDeviceIdentity.CreateNew("PC1");
        public H2CoordinatorDeviceIdentity Pc2 { get; } = H2CoordinatorDeviceIdentity.CreateNew("PC2");
        public MutableClock Clock { get; } = new();
        public H2CoordinatorSqliteStore Store { get; private set; } = null!;
        public H2SyncCoordinatorService Service { get; private set; } = null!;
        public H2ProjectSyncClient Client1 { get; private set; } = null!;
        public H2ProjectSyncClient Client2 { get; private set; } = null!;
        public Guid ConflictId { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Restart();
            Client1.QueueCreateProject(new ProjectRecord
            {
                Id = Project, Name = "Original", NameRich = RichDocument.Plain("Original"),
                Notes = "", NotesRich = RichDocument.Plain("")
            });
            Client1.SynchronizeAsync().GetAwaiter().GetResult();
            Client2.SynchronizeAsync().GetAwaiter().GetResult();
            Client1.QueueSetProjectName(RichDocument.Plain("Human winner"));
            Client2.QueueSetProjectName(RichDocument.Plain("Human conflict"));
            Client1.SynchronizeAsync().GetAwaiter().GetResult();
            Client2.SynchronizeAsync().GetAwaiter().GetResult();
            ConflictId = Store.GetConflicts(Workspace, Project).Single().ConflictId;
            AssertClientsSee(this, "Human winner");
        }

        public void Restart()
        {
            Store = new H2CoordinatorSqliteStore(Path.Combine(Root, "coordinator.db"), () => Clock.UtcNow);
            Service = new H2SyncCoordinatorService(Store);
            Client1 = Client(Pc1, "pc1");
            Client2 = Client(Pc2, "pc2");
        }

        private H2ProjectSyncClient Client(H2CoordinatorDeviceIdentity device, string folder)
            => new(Workspace, Project, device, Service, new H2CoordinatorClientStateStore(
                Path.Combine(Root, folder), Workspace, Project, device.DeviceId));
    }
}

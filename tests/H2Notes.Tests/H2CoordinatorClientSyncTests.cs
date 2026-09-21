using H2Notes.Coordinator;
using H2Notes.Core;

internal static class H2CoordinatorClientSyncTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Coordinator client outbox survives restart and merges remote progress before pending local events", () =>
        {
            var root = Folder();
            var db = Path.Combine(root, "coordinator.db");
            var workspace = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var store = new H2CoordinatorSqliteStore(db);
            var service = new StoreCoordinator(store);
            var pc1 = H2CoordinatorDeviceIdentity.CreateNew("PC1");
            var pc2 = H2CoordinatorDeviceIdentity.CreateNew("PC2");

            var pc1Local = new H2CoordinatorClientStateStore(
                Path.Combine(root, "pc1-local"), workspace, projectId, pc1.DeviceId);
            var pc2Root = Path.Combine(root, "pc2-local");
            var pc2Local = new H2CoordinatorClientStateStore(
                pc2Root, workspace, projectId, pc2.DeviceId);

            var client1 = new H2ProjectSyncClient(workspace, projectId, pc1, service, pc1Local);
            var baseline = new ProjectRecord
            {
                Id = projectId,
                Name = "Initial",
                NameRich = RichDocument.Plain("Initial"),
                Notes = "Initial notes",
                NotesRich = RichDocument.Plain("Initial notes")
            };
            client1.QueueCreateProject(baseline);
            var initialSync = client1.SynchronizeAsync().GetAwaiter().GetResult();
            Equal(H2ProjectSyncStatus.Synced, initialSync.Status);
            Equal(1L, initialSync.AppliedServerSequence);

            var toggle = new ToggleCoordinator(service);
            var client2 = new H2ProjectSyncClient(workspace, projectId, pc2, toggle, pc2Local);
            var pc2Initial = client2.SynchronizeAsync().GetAwaiter().GetResult();
            Equal("Initial", client2.CurrentProject!.DisplayName);
            Equal(1L, pc2Initial.AppliedServerSequence);

            // PC2 goes offline and edits locally. The edit must be durable before any remote call.
            toggle.IsOnline = false;
            client2.QueueSetProjectNotes(RichDocument.Plain("PC2 offline notes"));
            Equal(1, client2.PendingCount);
            Equal("PC2 offline notes", client2.CurrentProject!.NotesText);

            Throws<IOException>(() => client2.SynchronizeAsync().GetAwaiter().GetResult());
            Equal(1, client2.PendingCount);

            // Restart PC2 while still offline. Optimistic state must rebuild from local replica + outbox.
            var pc2AfterRestart = new H2ProjectSyncClient(
                workspace,
                projectId,
                pc2,
                toggle,
                new H2CoordinatorClientStateStore(pc2Root, workspace, projectId, pc2.DeviceId));
            Equal(1, pc2AfterRestart.PendingCount);
            Equal("PC2 offline notes", pc2AfterRestart.CurrentProject!.NotesText);

            // PC1 advances another independent field while PC2 is offline.
            client1.QueueSetProjectName(RichDocument.Plain("PC1 online name"));
            client1.SynchronizeAsync().GetAwaiter().GetResult();
            Equal(2L, client1.AppliedServerSequence);

            // Reconnect PC2. It must first consume PC1's accepted event, then submit its durable event.
            toggle.IsOnline = true;
            var reconciled = pc2AfterRestart.SynchronizeAsync().GetAwaiter().GetResult();
            Equal(H2ProjectSyncStatus.Synced, reconciled.Status);
            Equal(0, reconciled.PendingCount);
            Equal(3L, reconciled.AppliedServerSequence);
            Equal("PC1 online name", pc2AfterRestart.CurrentProject!.DisplayName);
            Equal("PC2 offline notes", pc2AfterRestart.CurrentProject!.NotesText);

            // PC1 catches up and both replicas converge to the same existing ProjectRecord model.
            client1.SynchronizeAsync().GetAwaiter().GetResult();
            Equal(3L, client1.AppliedServerSequence);
            Equal("PC1 online name", client1.CurrentProject!.DisplayName);
            Equal("PC2 offline notes", client1.CurrentProject!.NotesText);
        });

        test("Coordinator client retains outbox when submit succeeds but acknowledgement pull is interrupted", () =>
        {
            var root = Folder();
            var workspace = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var device = H2CoordinatorDeviceIdentity.CreateNew("PC1");
            var store = new H2CoordinatorSqliteStore(Path.Combine(root, "coordinator.db"));
            var baseService = new StoreCoordinator(store);
            var localRoot = Path.Combine(root, "local");

            var client = new H2ProjectSyncClient(
                workspace,
                projectId,
                device,
                baseService,
                new H2CoordinatorClientStateStore(localRoot, workspace, projectId, device.DeviceId));
            client.QueueCreateProject(new ProjectRecord
            {
                Id = projectId,
                Name = "Initial",
                NameRich = RichDocument.Plain("Initial"),
                Notes = "",
                NotesRich = RichDocument.Plain("")
            });
            client.SynchronizeAsync().GetAwaiter().GetResult();
            Equal(1L, client.AppliedServerSequence);

            var interrupted = new FailFirstReadAfterSubmitCoordinator(baseService);
            var mutatingClient = new H2ProjectSyncClient(
                workspace,
                projectId,
                device,
                interrupted,
                new H2CoordinatorClientStateStore(localRoot, workspace, projectId, device.DeviceId));
            mutatingClient.QueueSetProjectNotes(RichDocument.Plain("accepted but pull interrupted"));

            Throws<IOException>(() => mutatingClient.SynchronizeAsync().GetAwaiter().GetResult());
            Equal(1, mutatingClient.PendingCount);
            Equal(2L, store.GetProjectHead(workspace, projectId).ServerSequence);
            Equal(2, store.GetProjectEvents(workspace, projectId, 0).Count);

            // Restart/retry sends the same ClientOperationId. Coordinator returns the original ack,
            // the event is not duplicated, and outbox clears only after sequence 2 is observed.
            var recovered = new H2ProjectSyncClient(
                workspace,
                projectId,
                device,
                baseService,
                new H2CoordinatorClientStateStore(localRoot, workspace, projectId, device.DeviceId));
            var result = recovered.SynchronizeAsync().GetAwaiter().GetResult();

            Equal(H2ProjectSyncStatus.Synced, result.Status);
            Equal(0, recovered.PendingCount);
            Equal(2L, recovered.AppliedServerSequence);
            Equal("accepted but pull interrupted", recovered.CurrentProject!.NotesText);
            Equal(2, store.GetProjectEvents(workspace, projectId, 0).Count);
        });

        test("Coordinator client durable outbox stores immutable events before optimistic projection", () =>
        {
            var root = Folder();
            var workspace = Guid.NewGuid();
            var project = Guid.NewGuid();
            var device = H2CoordinatorDeviceIdentity.CreateNew("PC");
            var local = new H2CoordinatorClientStateStore(
                Path.Combine(root, "local"), workspace, project, device.DeviceId);
            var offline = new AlwaysOfflineCoordinator();
            var client = new H2ProjectSyncClient(workspace, project, device, offline, local);
            var baseline = new ProjectRecord
            {
                Id = project,
                NameRich = RichDocument.Plain("Local"),
                NotesRich = RichDocument.Plain("")
            };

            var draft = client.QueueCreateProject(baseline);
            Equal(1, local.Pending().Count);
            Equal(draft.EventId, local.Pending().Single().EventId);
            Equal("Local", client.CurrentProject!.DisplayName);

            var restarted = new H2ProjectSyncClient(
                workspace,
                project,
                device,
                offline,
                new H2CoordinatorClientStateStore(
                    Path.Combine(root, "local"), workspace, project, device.DeviceId));
            Equal(1, restarted.PendingCount);
            Equal("Local", restarted.CurrentProject!.DisplayName);
        });

        test("Project event applier mutates existing H2 task and link models without shared workspace files", () =>
        {
            var workspace = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var device = Guid.NewGuid();
            var project = new ProjectRecord
            {
                Id = projectId,
                NameRich = RichDocument.Plain("Project"),
                NotesRich = RichDocument.Plain("")
            };
            var create = Draft(
                workspace,
                projectId,
                device,
                1,
                H2ProjectEventKind.CreateEntity,
                new H2ProjectMutationTarget(H2ProjectEntityKind.Project, projectId, expectedRevision: 0),
                H2ProjectEventPayload.Serialize(project));
            var state = H2ProjectEventApplier.Apply(null, create)!;

            var task = new TaskRecord
            {
                Id = Guid.NewGuid(),
                TextRich = RichDocument.Plain("Task A"),
                CommentRich = RichDocument.Plain("")
            };
            var addTask = Draft(
                workspace,
                projectId,
                device,
                2,
                H2ProjectEventKind.CreateEntity,
                new H2ProjectMutationTarget(H2ProjectEntityKind.Task, task.Id, expectedRevision: 0),
                H2ProjectEventPayload.Serialize(task));
            state = H2ProjectEventApplier.Apply(state, addTask)!;

            var updateText = Draft(
                workspace,
                projectId,
                device,
                3,
                H2ProjectEventKind.SetField,
                new H2ProjectMutationTarget(H2ProjectEntityKind.Task, task.Id, "TextRich", 0),
                H2ProjectEventPayload.Serialize(RichDocument.Plain("Task A updated")));
            state = H2ProjectEventApplier.Apply(state, updateText)!;

            var link = new ProjectLink(Guid.NewGuid(), "Folder", @"C:\Project");
            var addLink = Draft(
                workspace,
                projectId,
                device,
                4,
                H2ProjectEventKind.CreateEntity,
                new H2ProjectMutationTarget(H2ProjectEntityKind.Link, link.Id, expectedRevision: 0),
                H2ProjectEventPayload.Serialize(link));
            state = H2ProjectEventApplier.Apply(state, addLink)!;

            Equal("Task A updated", state.ChecklistItems.Single().DisplayText);
            Equal(link, state.Links.Single());
        });

        test("Coordinator client sync source does not use legacy shared workspace transaction primitives", () =>
        {
            var repo = FindRepoRoot();
            var source = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Core", "H2CoordinatorClientSync.cs"));

            foreach (var forbidden in new[]
            {
                "ProjectWorkspaceStore",
                "WorkspaceCommitLease",
                ".h2-commit",
                "FileShare.None or",
                "workspace.h2index.json"
            })
                if (source.Contains(forbidden, StringComparison.Ordinal))
                    throw new Exception("Coordinator client sync leaked legacy shared-workspace primitive: " + forbidden);
        });
    }

    private static H2ProjectEventDraft Draft(
        Guid workspace,
        Guid project,
        Guid device,
        long deviceSequence,
        H2ProjectEventKind kind,
        H2ProjectMutationTarget target,
        string payload)
        => new(
            Guid.NewGuid(),
            workspace,
            project,
            device,
            deviceSequence,
            Guid.NewGuid(),
            kind,
            target,
            payload,
            H2ProjectEventDraft.ComputePayloadSha256(payload),
            DateTimeOffset.UtcNow);

    private static string Folder()
    {
        var path = Path.Combine(Path.GetTempPath(), "H2CoordinatorClientTests-" + Guid.NewGuid().ToString("N"));
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

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(current.FullName, "src")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed class StoreCoordinator(H2CoordinatorSqliteStore store) : IH2SyncCoordinator
    {
        public Task RegisterDeviceAsync(
            Guid workspaceId,
            H2CoordinatorDeviceIdentity device,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            store.RegisterDevice(workspaceId, device);
            return Task.CompletedTask;
        }

        public Task<H2ProjectHead> GetProjectHeadAsync(
            Guid workspaceId,
            Guid projectId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(store.GetProjectHead(workspaceId, projectId));
        }

        public Task<H2ProjectSnapshot?> GetProjectSnapshotAsync(
            Guid workspaceId,
            Guid projectId,
            long? atOrBeforeSequence = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = store.GetLatestSnapshot(workspaceId, projectId);
            if (snapshot is not null
                && atOrBeforeSequence.HasValue
                && snapshot.ThroughServerSequence > atOrBeforeSequence.Value)
                snapshot = null;
            return Task.FromResult(snapshot);
        }

        public Task<IReadOnlyList<H2AcceptedProjectEvent>> GetProjectEventsAsync(
            Guid workspaceId,
            Guid projectId,
            long afterServerSequence,
            int limit = 500,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(store.GetProjectEvents(
                workspaceId, projectId, afterServerSequence, limit));
        }

        public Task<H2ProjectEventSubmissionResult> SubmitProjectEventsAsync(
            Guid workspaceId,
            Guid deviceId,
            IReadOnlyList<H2ProjectEventDraft> events,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(store.SubmitProjectEvents(workspaceId, deviceId, events));
        }

        public Task<IReadOnlyList<H2ProjectConflict>> GetProjectConflictsAsync(
            Guid workspaceId,
            Guid projectId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(store.GetConflicts(workspaceId, projectId));
        }

        public Task<H2ProjectEventAcknowledgement> ResolveConflictAsync(
            Guid workspaceId,
            Guid conflictId,
            H2ProjectEventDraft resolutionEvent,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<H2QueuedProjectAiRequest> EnqueueProjectAiAsync(
            H2ProjectAiRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(store.EnqueueProjectAi(request));

        public Task<H2ProjectAiLease?> TryAcquireProjectAiLeaseAsync(
            Guid workspaceId,
            Guid projectId,
            Guid deviceId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> HeartbeatProjectAiLeaseAsync(
            Guid leaseId,
            Guid deviceId,
            DateTimeOffset heartbeatUtc,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CompleteProjectAiAsync(
            H2ProjectAiCompletion completion,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            store.CompleteProjectAi(completion);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<H2QueuedProjectAiRequest>> GetProjectAiQueueAsync(
            Guid workspaceId,
            Guid projectId,
            int limit = 100,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(store.GetProjectAiQueue(workspaceId, projectId, limit));
        }
    }

    private sealed class ToggleCoordinator(IH2SyncCoordinator inner) : IH2SyncCoordinator
    {
        public bool IsOnline { get; set; } = true;

        private void EnsureOnline()
        {
            if (!IsOnline) throw new IOException("Coordinator offline fixture.");
        }

        public Task RegisterDeviceAsync(Guid workspaceId, H2CoordinatorDeviceIdentity device, CancellationToken cancellationToken = default)
        { EnsureOnline(); return inner.RegisterDeviceAsync(workspaceId, device, cancellationToken); }

        public Task<H2ProjectHead> GetProjectHeadAsync(Guid workspaceId, Guid projectId, CancellationToken cancellationToken = default)
        { EnsureOnline(); return inner.GetProjectHeadAsync(workspaceId, projectId, cancellationToken); }

        public Task<H2ProjectSnapshot?> GetProjectSnapshotAsync(Guid workspaceId, Guid projectId, long? atOrBeforeSequence = null, CancellationToken cancellationToken = default)
        { EnsureOnline(); return inner.GetProjectSnapshotAsync(workspaceId, projectId, atOrBeforeSequence, cancellationToken); }

        public Task<IReadOnlyList<H2AcceptedProjectEvent>> GetProjectEventsAsync(Guid workspaceId, Guid projectId, long afterServerSequence, int limit = 500, CancellationToken cancellationToken = default)
        { EnsureOnline(); return inner.GetProjectEventsAsync(workspaceId, projectId, afterServerSequence, limit, cancellationToken); }

        public Task<H2ProjectEventSubmissionResult> SubmitProjectEventsAsync(Guid workspaceId, Guid deviceId, IReadOnlyList<H2ProjectEventDraft> events, CancellationToken cancellationToken = default)
        { EnsureOnline(); return inner.SubmitProjectEventsAsync(workspaceId, deviceId, events, cancellationToken); }

        public Task<IReadOnlyList<H2ProjectConflict>> GetProjectConflictsAsync(Guid workspaceId, Guid projectId, CancellationToken cancellationToken = default)
        { EnsureOnline(); return inner.GetProjectConflictsAsync(workspaceId, projectId, cancellationToken); }

        public Task<H2ProjectEventAcknowledgement> ResolveConflictAsync(Guid workspaceId, Guid conflictId, H2ProjectEventDraft resolutionEvent, CancellationToken cancellationToken = default)
        { EnsureOnline(); return inner.ResolveConflictAsync(workspaceId, conflictId, resolutionEvent, cancellationToken); }

        public Task<H2QueuedProjectAiRequest> EnqueueProjectAiAsync(H2ProjectAiRequest request, CancellationToken cancellationToken = default)
        { EnsureOnline(); return inner.EnqueueProjectAiAsync(request, cancellationToken); }

        public Task<H2ProjectAiLease?> TryAcquireProjectAiLeaseAsync(Guid workspaceId, Guid projectId, Guid deviceId, CancellationToken cancellationToken = default)
        { EnsureOnline(); return inner.TryAcquireProjectAiLeaseAsync(workspaceId, projectId, deviceId, cancellationToken); }

        public Task<bool> HeartbeatProjectAiLeaseAsync(Guid leaseId, Guid deviceId, DateTimeOffset heartbeatUtc, CancellationToken cancellationToken = default)
        { EnsureOnline(); return inner.HeartbeatProjectAiLeaseAsync(leaseId, deviceId, heartbeatUtc, cancellationToken); }

        public Task CompleteProjectAiAsync(H2ProjectAiCompletion completion, CancellationToken cancellationToken = default)
        { EnsureOnline(); return inner.CompleteProjectAiAsync(completion, cancellationToken); }

        public Task<IReadOnlyList<H2QueuedProjectAiRequest>> GetProjectAiQueueAsync(Guid workspaceId, Guid projectId, int limit = 100, CancellationToken cancellationToken = default)
        { EnsureOnline(); return inner.GetProjectAiQueueAsync(workspaceId, projectId, limit, cancellationToken); }
    }

    private sealed class FailFirstReadAfterSubmitCoordinator(IH2SyncCoordinator inner) : IH2SyncCoordinator
    {
        private bool _submitted;
        private bool _failedRead;

        public Task RegisterDeviceAsync(Guid workspaceId, H2CoordinatorDeviceIdentity device, CancellationToken cancellationToken = default)
            => inner.RegisterDeviceAsync(workspaceId, device, cancellationToken);

        public Task<H2ProjectHead> GetProjectHeadAsync(Guid workspaceId, Guid projectId, CancellationToken cancellationToken = default)
            => inner.GetProjectHeadAsync(workspaceId, projectId, cancellationToken);

        public Task<H2ProjectSnapshot?> GetProjectSnapshotAsync(Guid workspaceId, Guid projectId, long? atOrBeforeSequence = null, CancellationToken cancellationToken = default)
            => inner.GetProjectSnapshotAsync(workspaceId, projectId, atOrBeforeSequence, cancellationToken);

        public Task<IReadOnlyList<H2AcceptedProjectEvent>> GetProjectEventsAsync(Guid workspaceId, Guid projectId, long afterServerSequence, int limit = 500, CancellationToken cancellationToken = default)
        {
            if (_submitted && !_failedRead)
            {
                _failedRead = true;
                throw new IOException("Interrupted after Coordinator accepted submit.");
            }
            return inner.GetProjectEventsAsync(workspaceId, projectId, afterServerSequence, limit, cancellationToken);
        }

        public async Task<H2ProjectEventSubmissionResult> SubmitProjectEventsAsync(Guid workspaceId, Guid deviceId, IReadOnlyList<H2ProjectEventDraft> events, CancellationToken cancellationToken = default)
        {
            var result = await inner.SubmitProjectEventsAsync(workspaceId, deviceId, events, cancellationToken);
            _submitted = true;
            return result;
        }

        public Task<IReadOnlyList<H2ProjectConflict>> GetProjectConflictsAsync(Guid workspaceId, Guid projectId, CancellationToken cancellationToken = default)
            => inner.GetProjectConflictsAsync(workspaceId, projectId, cancellationToken);

        public Task<H2ProjectEventAcknowledgement> ResolveConflictAsync(Guid workspaceId, Guid conflictId, H2ProjectEventDraft resolutionEvent, CancellationToken cancellationToken = default)
            => inner.ResolveConflictAsync(workspaceId, conflictId, resolutionEvent, cancellationToken);

        public Task<H2QueuedProjectAiRequest> EnqueueProjectAiAsync(H2ProjectAiRequest request, CancellationToken cancellationToken = default)
            => inner.EnqueueProjectAiAsync(request, cancellationToken);

        public Task<H2ProjectAiLease?> TryAcquireProjectAiLeaseAsync(Guid workspaceId, Guid projectId, Guid deviceId, CancellationToken cancellationToken = default)
            => inner.TryAcquireProjectAiLeaseAsync(workspaceId, projectId, deviceId, cancellationToken);

        public Task<bool> HeartbeatProjectAiLeaseAsync(Guid leaseId, Guid deviceId, DateTimeOffset heartbeatUtc, CancellationToken cancellationToken = default)
            => inner.HeartbeatProjectAiLeaseAsync(leaseId, deviceId, heartbeatUtc, cancellationToken);

        public Task CompleteProjectAiAsync(H2ProjectAiCompletion completion, CancellationToken cancellationToken = default)
            => inner.CompleteProjectAiAsync(completion, cancellationToken);

        public Task<IReadOnlyList<H2QueuedProjectAiRequest>> GetProjectAiQueueAsync(Guid workspaceId, Guid projectId, int limit = 100, CancellationToken cancellationToken = default)
            => inner.GetProjectAiQueueAsync(workspaceId, projectId, limit, cancellationToken);
    }

    private sealed class AlwaysOfflineCoordinator : IH2SyncCoordinator
    {
        private static IOException Offline() => new("Coordinator offline fixture.");
        public Task RegisterDeviceAsync(Guid workspaceId, H2CoordinatorDeviceIdentity device, CancellationToken cancellationToken = default) => throw Offline();
        public Task<H2ProjectHead> GetProjectHeadAsync(Guid workspaceId, Guid projectId, CancellationToken cancellationToken = default) => throw Offline();
        public Task<H2ProjectSnapshot?> GetProjectSnapshotAsync(Guid workspaceId, Guid projectId, long? atOrBeforeSequence = null, CancellationToken cancellationToken = default) => throw Offline();
        public Task<IReadOnlyList<H2AcceptedProjectEvent>> GetProjectEventsAsync(Guid workspaceId, Guid projectId, long afterServerSequence, int limit = 500, CancellationToken cancellationToken = default) => throw Offline();
        public Task<H2ProjectEventSubmissionResult> SubmitProjectEventsAsync(Guid workspaceId, Guid deviceId, IReadOnlyList<H2ProjectEventDraft> events, CancellationToken cancellationToken = default) => throw Offline();
        public Task<IReadOnlyList<H2ProjectConflict>> GetProjectConflictsAsync(Guid workspaceId, Guid projectId, CancellationToken cancellationToken = default) => throw Offline();
        public Task<H2ProjectEventAcknowledgement> ResolveConflictAsync(Guid workspaceId, Guid conflictId, H2ProjectEventDraft resolutionEvent, CancellationToken cancellationToken = default) => throw Offline();
        public Task<H2QueuedProjectAiRequest> EnqueueProjectAiAsync(H2ProjectAiRequest request, CancellationToken cancellationToken = default) => throw Offline();
        public Task<H2ProjectAiLease?> TryAcquireProjectAiLeaseAsync(Guid workspaceId, Guid projectId, Guid deviceId, CancellationToken cancellationToken = default) => throw Offline();
        public Task<bool> HeartbeatProjectAiLeaseAsync(Guid leaseId, Guid deviceId, DateTimeOffset heartbeatUtc, CancellationToken cancellationToken = default) => throw Offline();
        public Task CompleteProjectAiAsync(H2ProjectAiCompletion completion, CancellationToken cancellationToken = default) => throw Offline();
        public Task<IReadOnlyList<H2QueuedProjectAiRequest>> GetProjectAiQueueAsync(Guid workspaceId, Guid projectId, int limit = 100, CancellationToken cancellationToken = default) => throw Offline();
    }
}

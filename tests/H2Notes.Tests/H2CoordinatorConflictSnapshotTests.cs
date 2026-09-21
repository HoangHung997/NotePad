using H2Notes.Coordinator;
using H2Notes.Core;

internal static class H2CoordinatorConflictSnapshotTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Coordinator merges independent project fields with field-scoped revisions", () =>
        {
            var fixture = Fixture();
            var baseline = CreateProject(fixture, fixture.Pc1, 1);
            fixture.Store.SubmitProjectEvents(fixture.Workspace, fixture.Pc1.DeviceId, new[] { baseline });

            var name = SetProjectField(
                fixture, fixture.Pc1, 2, "NameRich", 0,
                H2ProjectEventPayload.Serialize(RichDocument.Plain("PC1 name")));
            var notes = SetProjectField(
                fixture, fixture.Pc2, 1, "NotesRich", 0,
                H2ProjectEventPayload.Serialize(RichDocument.Plain("PC2 notes")));

            var first = fixture.Store.SubmitProjectEvents(
                fixture.Workspace, fixture.Pc1.DeviceId, new[] { name });
            var second = fixture.Store.SubmitProjectEvents(
                fixture.Workspace, fixture.Pc2.DeviceId, new[] { notes });

            Equal(0, first.Conflicts.Count);
            Equal(0, second.Conflicts.Count);

            var events = fixture.Store.GetProjectEvents(fixture.Workspace, fixture.Project, 0);
            Equal(3, events.Count);
            True(events.All(item => item.Disposition == H2ProjectEventDisposition.Applied));

            var revisions = fixture.Store.GetProjectRevisions(fixture.Workspace, fixture.Project);
            Equal(1L, Revision(revisions, H2ProjectEntityKind.Project, fixture.Project, "NameRich").Revision);
            Equal(1L, Revision(revisions, H2ProjectEntityKind.Project, fixture.Project, "NotesRich").Revision);
            Equal(3L, Revision(revisions, H2ProjectEntityKind.Project, fixture.Project, null).Revision);

            var projected = Replay(events);
            Equal("PC1 name", projected!.DisplayName);
            Equal("PC2 notes", projected.NotesText);
        });

        test("Coordinator records same-field conflict without changing authoritative projection and resolves by event", () =>
        {
            var fixture = Fixture();
            var baseline = CreateProject(fixture, fixture.Pc1, 1);
            fixture.Store.SubmitProjectEvents(fixture.Workspace, fixture.Pc1.DeviceId, new[] { baseline });

            var pc1Edit = SetProjectField(
                fixture, fixture.Pc1, 2, "NameRich", 0,
                H2ProjectEventPayload.Serialize(RichDocument.Plain("PC1 value")));
            var pc2Edit = SetProjectField(
                fixture, fixture.Pc2, 1, "NameRich", 0,
                H2ProjectEventPayload.Serialize(RichDocument.Plain("PC2 value")));

            fixture.Store.SubmitProjectEvents(
                fixture.Workspace, fixture.Pc1.DeviceId, new[] { pc1Edit });
            var conflicted = fixture.Store.SubmitProjectEvents(
                fixture.Workspace, fixture.Pc2.DeviceId, new[] { pc2Edit });

            Equal(1, conflicted.Conflicts.Count);
            var conflict = conflicted.Conflicts.Single();
            var events = fixture.Store.GetProjectEvents(fixture.Workspace, fixture.Project, 0);
            Equal(H2ProjectEventDisposition.Conflict, events.Single(item => item.Draft.EventId == pc2Edit.EventId).Disposition);
            Equal(conflict.ConflictId, events.Single(item => item.Draft.EventId == pc2Edit.EventId).ConflictId);

            var projected = Replay(events);
            Equal("PC1 value", projected!.DisplayName);
            Equal(1, fixture.Store.GetProjectHead(fixture.Workspace, fixture.Project).OpenConflictCount);

            var retry = fixture.Store.SubmitProjectEvents(
                fixture.Workspace, fixture.Pc2.DeviceId, new[] { pc2Edit });
            Equal(events.Single(item => item.Draft.EventId == pc2Edit.EventId).ServerSequence,
                retry.Acknowledgements.Single().ServerSequence);
            Equal(conflict.ConflictId, retry.Conflicts.Single().ConflictId);
            Equal(1, fixture.Store.GetConflicts(fixture.Workspace, fixture.Project).Count);

            var resolvedValueJson = H2ProjectEventPayload.Serialize(RichDocument.Plain("Resolved value"));
            var resolutionPayload = H2ProjectEventPayload.Serialize(
                new H2ConflictResolutionPayload(
                    H2ConflictResolutionAction.SetField,
                    resolvedValueJson));
            var resolution = new H2ProjectEventDraft(
                Guid.NewGuid(),
                fixture.Workspace,
                fixture.Project,
                fixture.Pc2.DeviceId,
                2,
                Guid.NewGuid(),
                H2ProjectEventKind.ResolveConflict,
                new H2ProjectMutationTarget(
                    H2ProjectEntityKind.Project,
                    fixture.Project,
                    "NameRich",
                    expectedRevision: 1),
                resolutionPayload,
                H2ProjectEventDraft.ComputePayloadSha256(resolutionPayload),
                DateTimeOffset.UtcNow);

            var ack = fixture.Store.ResolveConflict(
                fixture.Workspace, conflict.ConflictId, resolution);
            Equal(4L, ack.ServerSequence);

            var after = fixture.Store.GetConflicts(fixture.Workspace, fixture.Project).Single();
            Equal(H2ProjectConflictState.Resolved, after.State);
            Equal(resolution.EventId, after.ResolvedByEventId);
            Equal(0, fixture.Store.GetProjectHead(fixture.Workspace, fixture.Project).OpenConflictCount);

            events = fixture.Store.GetProjectEvents(fixture.Workspace, fixture.Project, 0);
            Equal(4, events.Count);
            Equal(H2ProjectEventDisposition.Applied, events[^1].Disposition);
            Equal(2L, events[^1].ResultingRevision);
            Equal("Resolved value", Replay(events)!.DisplayName);

            var retryAck = fixture.Store.ResolveConflict(
                fixture.Workspace, conflict.ConflictId, resolution);
            Equal(ack.ServerSequence, retryAck.ServerSequence);
            Equal(4, fixture.Store.GetProjectEvents(fixture.Workspace, fixture.Project, 0).Count);
        });

        test("Coordinator turns delete-vs-update into conflict and preserves deletion", () =>
        {
            var fixture = Fixture();
            var baseline = CreateProject(fixture, fixture.Pc1, 1);
            fixture.Store.SubmitProjectEvents(fixture.Workspace, fixture.Pc1.DeviceId, new[] { baseline });

            var task = new TaskRecord
            {
                Id = Guid.NewGuid(),
                Text = "Task",
                TextRich = RichDocument.Plain("Task"),
                Comment = "",
                CommentRich = RichDocument.Plain("")
            };
            var taskPayload = H2ProjectEventPayload.Serialize(task);
            var createTask = Draft(
                fixture, fixture.Pc1, 2,
                H2ProjectEventKind.CreateEntity,
                new H2ProjectMutationTarget(H2ProjectEntityKind.Task, task.Id, expectedRevision: 0),
                taskPayload);
            fixture.Store.SubmitProjectEvents(
                fixture.Workspace, fixture.Pc1.DeviceId, new[] { createTask });

            var delete = Draft(
                fixture, fixture.Pc2, 1,
                H2ProjectEventKind.DeleteEntity,
                new H2ProjectMutationTarget(H2ProjectEntityKind.Task, task.Id, expectedRevision: 1),
                "{}");
            fixture.Store.SubmitProjectEvents(
                fixture.Workspace, fixture.Pc2.DeviceId, new[] { delete });

            var updatePayload = H2ProjectEventPayload.Serialize(RichDocument.Plain("stale update"));
            var update = Draft(
                fixture, fixture.Pc1, 3,
                H2ProjectEventKind.SetField,
                new H2ProjectMutationTarget(H2ProjectEntityKind.Task, task.Id, "CommentRich", expectedRevision: 0),
                updatePayload);
            var result = fixture.Store.SubmitProjectEvents(
                fixture.Workspace, fixture.Pc1.DeviceId, new[] { update });

            Equal(1, result.Conflicts.Count);
            var stream = fixture.Store.GetProjectEvents(fixture.Workspace, fixture.Project, 0);
            Equal(H2ProjectEventDisposition.Conflict, stream[^1].Disposition);
            Equal(0, Replay(stream)!.ChecklistItems.Count);

            var entityRevision = Revision(
                fixture.Store.GetProjectRevisions(fixture.Workspace, fixture.Project),
                H2ProjectEntityKind.Task,
                task.Id,
                null);
            True(entityRevision.IsDeleted);
            Equal(2L, entityRevision.Revision);
        });

        test("Coordinator compacts hot events only behind a verified snapshot and keeps archived events queryable", () =>
        {
            var fixture = Fixture();
            var baseline = CreateProject(fixture, fixture.Pc1, 1);
            fixture.Store.SubmitProjectEvents(fixture.Workspace, fixture.Pc1.DeviceId, new[] { baseline });

            var name = SetProjectField(
                fixture, fixture.Pc1, 2, "NameRich", 0,
                H2ProjectEventPayload.Serialize(RichDocument.Plain("Before compaction")));
            fixture.Store.SubmitProjectEvents(
                fixture.Workspace, fixture.Pc1.DeviceId, new[] { name });

            var stream = fixture.Store.GetProjectEvents(fixture.Workspace, fixture.Project, 0);
            var state = Replay(stream);
            var stateJson = System.Text.Json.JsonSerializer.Serialize(state);
            var snapshot = new H2ProjectSnapshot(
                fixture.Workspace,
                fixture.Project,
                2,
                stateJson,
                H2ProjectEventDraft.ComputePayloadSha256(stateJson),
                DateTimeOffset.UtcNow,
                fixture.Store.GetProjectRevisions(fixture.Workspace, fixture.Project));
            fixture.Store.SaveSnapshot(snapshot);

            Equal((2, 0), fixture.Store.GetProjectEventStorageCounts(fixture.Workspace, fixture.Project));
            Equal(2, fixture.Store.CompactProjectEventsThrough(fixture.Workspace, fixture.Project, 2));
            Equal((0, 2), fixture.Store.GetProjectEventStorageCounts(fixture.Workspace, fixture.Project));

            var archivedStream = fixture.Store.GetProjectEvents(fixture.Workspace, fixture.Project, 0);
            Equal(2, archivedStream.Count);
            Equal(name.EventId, archivedStream[1].Draft.EventId);

            // Retry of an archived event remains idempotent and does not recreate it in the hot log.
            var retry = fixture.Store.SubmitProjectEvents(
                fixture.Workspace, fixture.Pc1.DeviceId, new[] { name });
            Equal(2L, retry.Acknowledgements.Single().ServerSequence);
            Equal((0, 2), fixture.Store.GetProjectEventStorageCounts(fixture.Workspace, fixture.Project));

            var notes = SetProjectField(
                fixture, fixture.Pc1, 3, "NotesRich", 0,
                H2ProjectEventPayload.Serialize(RichDocument.Plain("After compaction")));
            fixture.Store.SubmitProjectEvents(
                fixture.Workspace, fixture.Pc1.DeviceId, new[] { notes });

            Equal((1, 2), fixture.Store.GetProjectEventStorageCounts(fixture.Workspace, fixture.Project));
            Equal(3, fixture.Store.GetProjectEvents(fixture.Workspace, fixture.Project, 0).Count);
            Equal(0, fixture.Store.CompactProjectEventsThrough(fixture.Workspace, fixture.Project, 2));
            Throws<InvalidOperationException>(() =>
                fixture.Store.CompactProjectEventsThrough(fixture.Workspace, fixture.Project, 3));
        });

        test("Coordinator logical archive restores compacted project truth and continues sequencing", () =>
        {
            var fixture = Fixture();
            var baseline = CreateProject(fixture, fixture.Pc1, 1);
            fixture.Store.SubmitProjectEvents(fixture.Workspace, fixture.Pc1.DeviceId, new[] { baseline });

            var first = SetProjectField(
                fixture, fixture.Pc1, 2, "NameRich", 0,
                H2ProjectEventPayload.Serialize(RichDocument.Plain("PC1")));
            var stale = SetProjectField(
                fixture, fixture.Pc2, 1, "NameRich", 0,
                H2ProjectEventPayload.Serialize(RichDocument.Plain("PC2")));
            fixture.Store.SubmitProjectEvents(fixture.Workspace, fixture.Pc1.DeviceId, new[] { first });
            var conflict = fixture.Store.SubmitProjectEvents(
                fixture.Workspace, fixture.Pc2.DeviceId, new[] { stale }).Conflicts.Single();

            var valueJson = H2ProjectEventPayload.Serialize(RichDocument.Plain("Resolved archive"));
            var payload = H2ProjectEventPayload.Serialize(
                new H2ConflictResolutionPayload(H2ConflictResolutionAction.SetField, valueJson));
            var resolution = new H2ProjectEventDraft(
                Guid.NewGuid(),
                fixture.Workspace,
                fixture.Project,
                fixture.Pc2.DeviceId,
                2,
                Guid.NewGuid(),
                H2ProjectEventKind.ResolveConflict,
                new H2ProjectMutationTarget(
                    H2ProjectEntityKind.Project,
                    fixture.Project,
                    "NameRich",
                    expectedRevision: 1),
                payload,
                H2ProjectEventDraft.ComputePayloadSha256(payload),
                DateTimeOffset.UtcNow);
            fixture.Store.ResolveConflict(fixture.Workspace, conflict.ConflictId, resolution);

            var events = fixture.Store.GetProjectEvents(fixture.Workspace, fixture.Project, 0);
            var state = Replay(events);
            var stateJson = System.Text.Json.JsonSerializer.Serialize(state);
            fixture.Store.SaveSnapshot(new H2ProjectSnapshot(
                fixture.Workspace,
                fixture.Project,
                4,
                stateJson,
                H2ProjectEventDraft.ComputePayloadSha256(stateJson),
                DateTimeOffset.UtcNow,
                fixture.Store.GetProjectRevisions(fixture.Workspace, fixture.Project)));
            Equal(4, fixture.Store.CompactProjectEventsThrough(
                fixture.Workspace, fixture.Project, 4));
            Equal((0, 4), fixture.Store.GetProjectEventStorageCounts(
                fixture.Workspace, fixture.Project));

            var folder = Path.GetDirectoryName(fixture.DatabasePath)!;
            var archivePath = Path.Combine(folder, "workspace.h2coord.json");
            fixture.Store.ExportWorkspaceToFile(fixture.Workspace, archivePath);
            var archive = H2CoordinatorSqliteStore.ReadWorkspaceArchiveFile(archivePath);
            Equal(fixture.Workspace, archive.WorkspaceId);
            Equal(4, archive.Events.Count);

            var restoredPath = Path.Combine(folder, "restored.db");
            var restored = H2CoordinatorSqliteStore.RestoreWorkspaceArchive(
                restoredPath, archivePath);
            var restoredEvents = restored.GetProjectEvents(
                fixture.Workspace, fixture.Project, 0);
            Equal(4, restoredEvents.Count);
            Equal("Resolved archive", Replay(restoredEvents)!.DisplayName);
            Equal(4L, restored.GetProjectHead(fixture.Workspace, fixture.Project).ServerSequence);
            Equal(0, restored.GetProjectHead(fixture.Workspace, fixture.Project).OpenConflictCount);
            Equal(H2ProjectConflictState.Resolved,
                restored.GetConflicts(fixture.Workspace, fixture.Project).Single().State);
            Equal(
                fixture.Store.GetLatestSnapshot(fixture.Workspace, fixture.Project)!.StateSha256,
                restored.GetLatestSnapshot(fixture.Workspace, fixture.Project)!.StateSha256);

            var sourceRevisions = fixture.Store.GetProjectRevisions(
                fixture.Workspace, fixture.Project)
                .OrderBy(item => item.StableKey, StringComparer.Ordinal)
                .ToArray();
            var restoredRevisions = restored.GetProjectRevisions(
                fixture.Workspace, fixture.Project)
                .OrderBy(item => item.StableKey, StringComparer.Ordinal)
                .ToArray();
            Equal(sourceRevisions.Length, restoredRevisions.Length);
            for (var i = 0; i < sourceRevisions.Length; i++)
            {
                Equal(sourceRevisions[i].StableKey, restoredRevisions[i].StableKey);
                Equal(sourceRevisions[i].Revision, restoredRevisions[i].Revision);
                Equal(sourceRevisions[i].IsDeleted, restoredRevisions[i].IsDeleted);
            }

            // Imported device audit identity permits the restored Coordinator to continue
            // accepting that device's next immutable operation without sequence reset.
            var notes = SetProjectField(
                fixture, fixture.Pc1, 3, "NotesRich", 0,
                H2ProjectEventPayload.Serialize(RichDocument.Plain("After restore")));
            var continued = restored.SubmitProjectEvents(
                fixture.Workspace, fixture.Pc1.DeviceId, new[] { notes });
            Equal(5L, continued.Acknowledgements.Single().ServerSequence);
            Equal("After restore",
                Replay(restored.GetProjectEvents(fixture.Workspace, fixture.Project, 0))!.NotesText);

            // Top-level archive hash detects tampering before restore.
            var tamperedPath = Path.Combine(folder, "tampered.h2coord.json");
            var archiveText = File.ReadAllText(archivePath);
            var tampered = archiveText.Replace("PC1", "PX1", StringComparison.Ordinal);
            if (tampered == archiveText) throw new Exception("Archive tamper fixture did not change payload.");
            File.WriteAllText(tamperedPath, tampered);
            Throws<InvalidDataException>(() =>
                H2CoordinatorSqliteStore.ReadWorkspaceArchiveFile(tamperedPath));
        });

        test("Coordinator accepts only snapshots that equal applied replay and current revision metadata", () =>
        {
            var fixture = Fixture();
            var baseline = CreateProject(fixture, fixture.Pc1, 1);
            fixture.Store.SubmitProjectEvents(fixture.Workspace, fixture.Pc1.DeviceId, new[] { baseline });

            var name = SetProjectField(
                fixture, fixture.Pc1, 2, "NameRich", 0,
                H2ProjectEventPayload.Serialize(RichDocument.Plain("Snapshot name")));
            fixture.Store.SubmitProjectEvents(
                fixture.Workspace, fixture.Pc1.DeviceId, new[] { name });

            var stream = fixture.Store.GetProjectEvents(fixture.Workspace, fixture.Project, 0);
            var projected = Replay(stream);
            var stateJson = System.Text.Json.JsonSerializer.Serialize(projected);
            var revisions = fixture.Store.GetProjectRevisions(fixture.Workspace, fixture.Project);

            var snapshot = new H2ProjectSnapshot(
                fixture.Workspace,
                fixture.Project,
                2,
                stateJson,
                H2ProjectEventDraft.ComputePayloadSha256(stateJson),
                DateTimeOffset.UtcNow,
                revisions);
            fixture.Store.SaveSnapshot(snapshot);

            var restored = new H2CoordinatorSqliteStore(fixture.DatabasePath)
                .GetLatestSnapshot(fixture.Workspace, fixture.Project)!;
            Equal(2L, restored.ThroughServerSequence);
            Equal(snapshot.StateSha256, restored.StateSha256);
            Equal(revisions.Count, restored.Revisions.Count);

            var wrongProject = new ProjectRecord
            {
                Id = fixture.Project,
                Name = "Tampered",
                NameRich = RichDocument.Plain("Tampered"),
                Notes = "",
                NotesRich = RichDocument.Plain("")
            };
            var wrongJson = System.Text.Json.JsonSerializer.Serialize(wrongProject);
            var wrongSnapshot = new H2ProjectSnapshot(
                fixture.Workspace,
                fixture.Project,
                2,
                wrongJson,
                H2ProjectEventDraft.ComputePayloadSha256(wrongJson),
                DateTimeOffset.UtcNow,
                revisions);
            Throws<InvalidDataException>(() => fixture.Store.SaveSnapshot(wrongSnapshot));

            var wrongRevisions = revisions
                .Select(item => item.FieldKey == "NameRich"
                    ? new H2ProjectRevisionEntry(
                        item.EntityKind, item.EntityId, item.FieldKey, item.Revision + 1, item.IsDeleted)
                    : item)
                .ToArray();
            var wrongRevisionSnapshot = new H2ProjectSnapshot(
                fixture.Workspace,
                fixture.Project,
                2,
                stateJson,
                H2ProjectEventDraft.ComputePayloadSha256(stateJson),
                DateTimeOffset.UtcNow,
                wrongRevisions);
            Throws<InvalidDataException>(() => fixture.Store.SaveSnapshot(wrongRevisionSnapshot));
        });
    }

    private static FixtureState Fixture()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "H2CoordinatorConflictTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        var workspace = Guid.NewGuid();
        var project = Guid.NewGuid();
        var pc1 = H2CoordinatorDeviceIdentity.CreateNew("PC1");
        var pc2 = H2CoordinatorDeviceIdentity.CreateNew("PC2");
        var db = Path.Combine(folder, "coordinator.db");
        var store = new H2CoordinatorSqliteStore(db);
        store.RegisterDevice(workspace, pc1);
        store.RegisterDevice(workspace, pc2);
        return new FixtureState(db, workspace, project, pc1, pc2, store);
    }

    private static H2ProjectEventDraft CreateProject(
        FixtureState fixture,
        H2CoordinatorDeviceIdentity device,
        long deviceSequence)
    {
        var project = new ProjectRecord
        {
            Id = fixture.Project,
            Name = "Baseline",
            NameRich = RichDocument.Plain("Baseline"),
            Notes = "",
            NotesRich = RichDocument.Plain("")
        };
        var payload = H2ProjectEventPayload.Serialize(project);
        return Draft(
            fixture,
            device,
            deviceSequence,
            H2ProjectEventKind.CreateEntity,
            new H2ProjectMutationTarget(
                H2ProjectEntityKind.Project,
                fixture.Project,
                expectedRevision: 0),
            payload);
    }

    private static H2ProjectEventDraft SetProjectField(
        FixtureState fixture,
        H2CoordinatorDeviceIdentity device,
        long deviceSequence,
        string field,
        long expectedRevision,
        string payload)
        => Draft(
            fixture,
            device,
            deviceSequence,
            H2ProjectEventKind.SetField,
            new H2ProjectMutationTarget(
                H2ProjectEntityKind.Project,
                fixture.Project,
                field,
                expectedRevision),
            payload);

    private static H2ProjectEventDraft Draft(
        FixtureState fixture,
        H2CoordinatorDeviceIdentity device,
        long deviceSequence,
        H2ProjectEventKind kind,
        H2ProjectMutationTarget target,
        string payload)
        => new(
            Guid.NewGuid(),
            fixture.Workspace,
            fixture.Project,
            device.DeviceId,
            deviceSequence,
            Guid.NewGuid(),
            kind,
            target,
            payload,
            H2ProjectEventDraft.ComputePayloadSha256(payload),
            DateTimeOffset.UtcNow);

    private static ProjectRecord? Replay(IReadOnlyList<H2AcceptedProjectEvent> events)
    {
        ProjectRecord? project = null;
        foreach (var accepted in events.OrderBy(item => item.ServerSequence))
        {
            if (accepted.Disposition != H2ProjectEventDisposition.Applied) continue;
            project = H2ProjectEventApplier.Apply(project, accepted.Draft);
        }
        return project;
    }

    private static H2ProjectRevisionEntry Revision(
        IReadOnlyList<H2ProjectRevisionEntry> revisions,
        H2ProjectEntityKind kind,
        Guid entityId,
        string? fieldKey)
        => revisions.Single(item =>
            item.EntityKind == kind
            && item.EntityId == entityId
            && string.Equals(item.FieldKey, fieldKey, StringComparison.Ordinal));

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

    private sealed record FixtureState(
        string DatabasePath,
        Guid Workspace,
        Guid Project,
        H2CoordinatorDeviceIdentity Pc1,
        H2CoordinatorDeviceIdentity Pc2,
        H2CoordinatorSqliteStore Store);
}

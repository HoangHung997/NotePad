using H2Notes.Core;

internal static class H2SyncCoordinatorContractTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Coordinator device identity is stable protocol identity, not a machine-name key", () =>
        {
            var a = H2CoordinatorDeviceIdentity.CreateNew("PC1");
            var b = H2CoordinatorDeviceIdentity.CreateNew("PC1");
            if (a.DeviceId == Guid.Empty || b.DeviceId == Guid.Empty || a.DeviceId == b.DeviceId)
                throw new Exception("Device identity must be non-empty and independently generated.");
            if (a.DisplayName != "PC1" || b.DisplayName != "PC1")
                throw new Exception("Display name should remain diagnostic metadata.");
            Throws<ArgumentException>(() => new H2CoordinatorDeviceIdentity(Guid.Empty, "PC1"));
        });

        test("Project event draft preserves idempotency identity and verifies payload hash", () =>
        {
            var payload = "{"value":"Final dossier"}";
            var hash = H2ProjectEventDraft.ComputePayloadSha256(payload);
            var eventId = Guid.NewGuid();
            var operationId = Guid.NewGuid();
            var draft = new H2ProjectEventDraft(
                eventId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                7,
                operationId,
                H2ProjectEventKind.SetField,
                new H2ProjectMutationTarget(H2ProjectEntityKind.Task, Guid.NewGuid(), "Name", expectedRevision: 3),
                payload,
                hash,
                DateTimeOffset.UtcNow);

            if (draft.EventId != eventId || draft.ClientOperationId != operationId || draft.DeviceSequence != 7)
                throw new Exception("Idempotency/sequence identity was not retained.");
            if (draft.Target.ExpectedRevision != 3 || draft.Target.FieldKey != "Name")
                throw new Exception("Mutation target revision identity was not retained.");

            Throws<ArgumentException>(() => new H2ProjectEventDraft(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                Guid.NewGuid(),
                H2ProjectEventKind.SetField,
                new H2ProjectMutationTarget(H2ProjectEntityKind.Task, Guid.NewGuid(), "Name", 0),
                payload,
                new string('0', 64),
                DateTimeOffset.UtcNow));

            Throws<ArgumentException>(() => new H2ProjectEventDraft(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                Guid.NewGuid(),
                H2ProjectEventKind.SetField,
                new H2ProjectMutationTarget(H2ProjectEntityKind.Task, Guid.NewGuid(), fieldKey: null, expectedRevision: 0),
                "{}",
                H2ProjectEventDraft.ComputePayloadSha256("{}"),
                DateTimeOffset.UtcNow));
        });

        test("Server sequence is authoritative independently of client clock order", () =>
        {
            var workspace = Guid.NewGuid();
            var project = Guid.NewGuid();
            var deviceA = Guid.NewGuid();
            var deviceB = Guid.NewGuid();
            var targetA = new H2ProjectMutationTarget(H2ProjectEntityKind.Task, Guid.NewGuid(), "Name", 0);
            var targetB = new H2ProjectMutationTarget(H2ProjectEntityKind.Task, Guid.NewGuid(), "Deadline", 0);
            var firstPayload = "{"value":"A"}";
            var secondPayload = "{"value":"B"}";

            var clientClockLaterButAcceptedFirst = new H2ProjectEventDraft(
                Guid.NewGuid(), workspace, project, deviceA, 1, Guid.NewGuid(),
                H2ProjectEventKind.SetField, targetA,
                firstPayload, H2ProjectEventDraft.ComputePayloadSha256(firstPayload),
                new DateTimeOffset(2026, 9, 21, 10, 10, 0, TimeSpan.FromHours(7)));

            var clientClockEarlierButAcceptedSecond = new H2ProjectEventDraft(
                Guid.NewGuid(), workspace, project, deviceB, 1, Guid.NewGuid(),
                H2ProjectEventKind.SetField, targetB,
                secondPayload, H2ProjectEventDraft.ComputePayloadSha256(secondPayload),
                new DateTimeOffset(2026, 9, 21, 9, 59, 0, TimeSpan.FromHours(7)));

            var accepted1 = new H2AcceptedProjectEvent(clientClockLaterButAcceptedFirst, 40, DateTimeOffset.UtcNow);
            var accepted2 = new H2AcceptedProjectEvent(clientClockEarlierButAcceptedSecond, 41, DateTimeOffset.UtcNow);

            if (accepted1.ServerSequence >= accepted2.ServerSequence)
                throw new Exception("Server order was not retained.");
            if (accepted1.Draft.ClientCreatedUtc <= accepted2.Draft.ClientCreatedUtc)
                throw new Exception("Fixture did not prove client-clock order can disagree with server order.");
        });

        test("Conflict evidence is immutable and rejects ambiguous duplicate event identity", () =>
        {
            var eventA = Guid.NewGuid();
            var eventB = Guid.NewGuid();
            var input = new[] { eventA, eventB };
            var conflict = new H2ProjectConflict(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                new H2ProjectMutationTarget(H2ProjectEntityKind.Task, Guid.NewGuid(), "Name", 7),
                input,
                DateTimeOffset.UtcNow);

            input[0] = Guid.NewGuid();
            if (conflict.EventIds[0] != eventA || conflict.EventIds[1] != eventB)
                throw new Exception("Conflict evidence changed after caller collection mutation.");

            Throws<ArgumentException>(() => new H2ProjectConflict(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                new H2ProjectMutationTarget(H2ProjectEntityKind.Task, Guid.NewGuid(), "Name", 7),
                new[] { eventA, eventA },
                DateTimeOffset.UtcNow));
        });

        test("Snapshot is hash-verified and bounded to an accepted server watermark", () =>
        {
            var state = "{"project":"A"}";
            var hash = H2ProjectEventDraft.ComputePayloadSha256(state);
            var snapshot = new H2ProjectSnapshot(
                Guid.NewGuid(), Guid.NewGuid(), 125, state, hash, DateTimeOffset.UtcNow);
            if (snapshot.ThroughServerSequence != 125 || snapshot.StateSha256 != hash)
                throw new Exception("Snapshot watermark/hash was not retained.");

            Throws<ArgumentException>(() => new H2ProjectSnapshot(
                Guid.NewGuid(), Guid.NewGuid(), 1, state, new string('f', 64), DateTimeOffset.UtcNow));
        });

        test("AI lease binds one project request to a sync barrier and bounded heartbeat timeline", () =>
        {
            var project = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var barrier = new H2ProjectSyncBarrier(project, 88);
            var lease = new H2ProjectAiLease(
                Guid.NewGuid(),
                Guid.NewGuid(),
                project,
                Guid.NewGuid(),
                12,
                barrier,
                now,
                now.AddSeconds(5),
                now.AddSeconds(30));

            if (lease.Barrier.RequiredProjectSequence != 88 || lease.QueueSequence != 12)
                throw new Exception("AI queue/barrier identity was not retained.");

            Throws<ArgumentException>(() => new H2ProjectAiLease(
                Guid.NewGuid(),
                Guid.NewGuid(),
                project,
                Guid.NewGuid(),
                1,
                new H2ProjectSyncBarrier(Guid.NewGuid(), 88),
                now,
                now,
                now.AddSeconds(30)));

            Throws<ArgumentException>(() => new H2ProjectAiLease(
                Guid.NewGuid(),
                Guid.NewGuid(),
                project,
                Guid.NewGuid(),
                1,
                barrier,
                now,
                now.AddSeconds(10),
                now.AddSeconds(5)));
        });

        test("AI completion rejects non-terminal queue states", () =>
        {
            Throws<ArgumentException>(() => new H2ProjectAiCompletion(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                H2ProjectAiQueueState.Running,
                10,
                null,
                DateTimeOffset.UtcNow));

            var completion = new H2ProjectAiCompletion(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                H2ProjectAiQueueState.Completed,
                10,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow);
            if (completion.TerminalState != H2ProjectAiQueueState.Completed)
                throw new Exception("Terminal AI completion was not retained.");
        });

        test("Coordinator service boundary is infrastructure and Agent-runtime neutral", () =>
        {
            var forbidden = new[]
            {
                "HttpClient", "HttpRequestMessage", "HttpResponseMessage",
                "Sqlite", "SQLite", "FileStream", "FileMode", "WorkspaceCommitLease",
                "AgentRuntime", "AgentOrchestrator", "ToolRegistry", "IAgentTransport"
            };

            foreach (var method in typeof(IH2SyncCoordinator).GetMethods())
            {
                var signature = method.ReturnType.FullName + " "
                    + string.Join(" ", method.GetParameters().Select(parameter => parameter.ParameterType.FullName));
                foreach (var marker in forbidden)
                    if (signature.Contains(marker, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("Coordinator boundary leaked implementation marker: " + marker);
            }

            var repo = FindRepoRoot();
            var source = File.ReadAllText(Path.Combine(repo, "src", "H2Notes.Core", "H2SyncCoordinatorContracts.cs"));
            foreach (var marker in new[]
            {
                "Microsoft.Data.Sqlite", "System.Net.Http", "ProjectWorkspaceStore",
                "WorkspaceCommitLease", "AgentRuntime", "ToolRegistry"
            })
                if (source.Contains(marker, StringComparison.Ordinal))
                    throw new Exception("Coordinator contract source contains implementation dependency: " + marker);
        });
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
}

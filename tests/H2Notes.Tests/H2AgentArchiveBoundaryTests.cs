using System.Security.Cryptography;
using System.Text.Json;
using H2AgentLab;
using H2AgentLab.Integration;
using H2AgentLab.Tools;
using H2Notes.Core;

internal static partial class H2AgentArchiveJournalTests
{
    private static void RunBoundaryTests(Action<string, Action> test)
    {
        test("AR-031 boundary capacity is diagnosed before admitting new work and survives reload", () => Fixture(root =>
        {
            var task = Summary();
            using (var archive = new AgentIntegrationTaskArchive(root, new(MaxEvents: 1)))
            {
                archive.Upsert(task);
                Check(!archive.Status.CanWrite && archive.Status.State == "CapacityRequired", "Full archive still admits new work.");
                Check(archive.Get(task.TaskId)!.Status == H2AgentTaskStatus.Completed && !archive.Get(task.TaskId)!.Recovery!.ReconcileRequired,
                    "Capacity limit incorrectly invalidated already retained results.");
            }
            using var reopened = new AgentIntegrationTaskArchive(root, new(MaxEvents: 1));
            Check(!reopened.Status.CanWrite && reopened.Status.State == "CapacityRequired", "Reload forgot the capacity limit.");
            Check(reopened.Get(task.TaskId)!.FinalText == task.FinalText, "Capacity diagnosis dropped data.");
        }));
        test("AR-031 boundary a completed prefix is not current completion after journal loss", () => Fixture(root =>
        {
            var task = Summary();
            using (var archive = new AgentIntegrationTaskArchive(root))
            { archive.Upsert(task); archive.Upsert(task with { Status = H2AgentTaskStatus.Running, FinalText = "new attempt" }); }
            File.WriteAllText(Events(root)[1], "{torn");
            using var reopened = new AgentIntegrationTaskArchive(root);
            var recovered = reopened.Get(task.TaskId)!;
            Check(recovered.Status == H2AgentTaskStatus.Blocked && recovered.Recovery!.ReconcileRequired,
                "A completed prefix was presented as current completion after source loss.");
            Check(recovered.FinalText == task.FinalText && !reopened.Status.CanWrite, "Verified prefix or corruption diagnosis was lost.");
        }));
        test("AR-031 boundary duplicate event identity is rejected even when hashes are internally consistent", () => Fixture(root =>
        {
            using (var archive = new AgentIntegrationTaskArchive(root)) { archive.Upsert(Summary()); archive.Upsert(Summary()); }
            var files = Events(root);
            var first = JsonSerializer.Deserialize<AgentIntegrationTaskArchive.JournalEntry>(File.ReadAllBytes(files[0]))!;
            var second = JsonSerializer.Deserialize<AgentIntegrationTaskArchive.JournalEntry>(File.ReadAllBytes(files[1]))!;
            var changed = second with { EventId = first.EventId, Sha256 = "" };
            changed = changed with { Sha256 = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(changed))).ToLowerInvariant() };
            File.WriteAllBytes(files[1], JsonSerializer.SerializeToUtf8Bytes(changed));
            File.WriteAllBytes(Path.Combine(root, "journal-v2", "head.json"), JsonSerializer.SerializeToUtf8Bytes(new
                { Schema = 2, changed.StoreId, Sequence = 2, HeadHash = changed.Sha256 }));
            using var reopened = new AgentIntegrationTaskArchive(root);
            Check(!reopened.Status.CanWrite && reopened.Status.LastSequence == 1
                && reopened.Status.Diagnostics.Contains("journal-prefix-only:journal-event-identity"),
                "A duplicate source event identity survived validated replay.");
            Check(reopened.Get(second.StreamId) is null, "Rejected event was applied to the index.");
        }));
        test("AR-031 boundary legacy byte budget stops before creating a partial migration", () => Fixture(root =>
        {
            var records = Path.Combine(root, "task-records"); Directory.CreateDirectory(records);
            var task = Summary() with { FinalText = new string('X', 8_000) };
            var path = Path.Combine(records, task.TaskId.ToString("N") + ".json");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(task); File.WriteAllBytes(path, bytes);
            using var archive = new AgentIntegrationTaskArchive(root, new(MaxJournalBytes: 4096));
            Check(!archive.Status.CanWrite && !Directory.EnumerateDirectories(root, "migration-*").Any(),
                "Oversized legacy sources were retained into a partial migration before quota preflight.");
            Check(File.ReadAllBytes(path).SequenceEqual(bytes), "Quota rejection changed legacy rollback bytes.");
        }));
        test("AR-031 boundary journal admission failure prevents concrete executor effects", () => Fixture(root =>
        {
            var count = 0; var descriptor = ArchiveFixtureTool(root, () => count++);
            using var scheduler = new ToolExecutionScheduler();
            var request = new ToolExecutionRequest(descriptor, new ToolCall("denied", descriptor.Name, JsonSerializer.SerializeToElement(new { })), "fixture")
                { BeforeExecute = _ => throw new IOException("controlled admission failure") };
            Reject<IOException>(() => scheduler.ExecuteBatchAsync([request], CancellationToken.None).GetAwaiter().GetResult());
            Check(count == 0 && !File.Exists(Path.Combine(root, "scheduler-effect.txt")), "Executor ran before durable admission succeeded.");
        }));
        test("AR-031 boundary result journal failure fences a queued mutation under its resource gate", () => Fixture(root =>
        {
            var count = 0; var descriptor = ArchiveFixtureTool(root, () => count++);
            using var scheduler = new ToolExecutionScheduler();
            ToolExecutionRequest Request(string id) => new(descriptor, new ToolCall(id, descriptor.Name, JsonSerializer.SerializeToElement(new { })), "same");
            var first = Request("first") with { AfterExecute = (_, _) => throw new IOException("controlled result failure") };
            Reject<IOException>(() => scheduler.ExecuteBatchAsync([first, Request("queued")], CancellationToken.None).GetAwaiter().GetResult());
            var future = scheduler.ExecuteBatchAsync([Request("later")], CancellationToken.None).GetAwaiter().GetResult();
            Check(count == 1 && File.ReadAllText(Path.Combine(root, "scheduler-effect.txt")) == "ONE"
                && future.Single().Outcome?.Effect == ToolMutationEffect.None, "Result-loss fence allowed a duplicate effect.");
        }));
    }
    private static ToolDescriptor ArchiveFixtureTool(string root, Action observe)
        => new("fixture.archive_write", new("fixture", "Dedicated journal boundary fixture"), "Write a disposable fixture",
            AgentToolRisk.Medium, AgentToolAccess.Mutating, false, "v1",
            JsonSerializer.SerializeToElement(new { type = "function", function = new { name = "fixture.archive_write",
                description = "Controlled local test", parameters = new { type = "object", properties = new { } } } }),
            new DelegatingToolExecutor("fixture", (call, ct) =>
            {
                ct.ThrowIfCancellationRequested(); observe(); File.AppendAllText(Path.Combine(root, "scheduler-effect.txt"), "ONE");
                return ValueTask.FromResult("{\"ok\":true}");
            }), resourceScope: new("fixture-resource", "dedicated temporary file"), resultFormat: ToolResultFormat.Json);
}

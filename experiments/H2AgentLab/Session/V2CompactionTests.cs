using System.Security.Cryptography;
using H2AgentLab.Context;

namespace H2AgentLab.Session;

public static class V2CompactionTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root)) throw new IOException("Use a new v2 compaction test directory.");
        Directory.CreateDirectory(root);
        var lines = new List<string>();
        var failed = 0;

        async Task Test(string name, Func<Task> action)
        {
            try
            {
                await action();
                lines.Add("PASS " + name);
            }
            catch (Exception ex)
            {
                failed++;
                lines.Add("FAIL " + name + ": " + ex.Message);
            }
        }

        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        await Test("Checkpoint replaces old active history with summary plus durable source references without changing raw journal", () =>
        {
            var state = Path.Combine(root, "preservation");
            var session = new LabSession();
            var oldIndex = session.Events.Count;
            session.Add("user", "OLD_RAW_SENTINEL_MUST_REMAIN_ONLY_ON_DISK");
            for (var i = 0; i < 20; i++)
            {
                session.Add("assistant", "older assistant " + i);
                session.Add("user", "older user " + i);
            }
            session.Add("assistant", "RECENT_VERIFIED_TURN");
            session.Save(state);
            var sessionPath = Path.Combine(state, "session.json");
            var rawBefore = File.ReadAllBytes(sessionPath);
            var rawHashBefore = Convert.ToHexString(SHA256.HashData(rawBefore));

            var artifact = new ArtifactStore(state).StoreText(
                AgentArtifactKind.DocumentExtract,
                sourceId: "document:older-extract",
                toolName: "read_document",
                content: "full older document evidence",
                summary: "Older document evidence stored by handle.",
                sequence: 12);

            var compaction = new CompactionManager(state);
            var checkpoint = compaction.CreateCheckpoint(
                "Older verified activity was compacted; exact evidence remains in the durable references below.",
                [
                    new AgentCompactionSourceReference(
                        AgentCompactionSourceKind.JournalEvent,
                        $"session:{session.Id:N}:event:{oldIndex}",
                        Sequence: oldIndex),
                    new AgentCompactionSourceReference(
                        AgentCompactionSourceKind.Artifact,
                        artifact.Handle.Id,
                        artifact.Handle.Sha256,
                        Sequence: 12)
                ],
                coveredThroughSequence: 40);

            var rendered = compaction.RenderContext(checkpoint);
            Check(rendered.Length <= CompactionManager.MaxContextCharacters, "Rendered checkpoint exceeded compacted-history budget.");
            Check(rendered.Contains(checkpoint.Id, StringComparison.Ordinal), "Checkpoint ID missing from compacted context.");
            Check(rendered.Contains($"session:{session.Id:N}:event:{oldIndex}", StringComparison.Ordinal), "Journal source reference missing.");
            Check(rendered.Contains(artifact.Handle.Id, StringComparison.Ordinal), "Artifact source handle missing.");
            Check(!rendered.Contains("OLD_RAW_SENTINEL", StringComparison.Ordinal), "Raw old journal text leaked into checkpoint context.");

            var snapshot = new LabSessionContextAdapter(new AgentContextManager(new AgentContextBudget
            {
                MaxTotalCharacters = 5_000,
                MaxTaskContractCharacters = 0,
                MaxCurrentStateCharacters = 0,
                MaxRecentTurnsCharacters = 500,
                MaxToolSummariesCharacters = 0,
                MaxCompactedHistoryCharacters = 4_000,
                MaxCharactersPerItem = 300,
                MaxRecentTurns = 1,
                MaxToolSummaries = 0
            })).Build(session, compactedHistory: rendered);
            var active = snapshot.RuntimeContext.WorkingState ?? "";
            Check(active.Contains("RECENT_VERIFIED_TURN", StringComparison.Ordinal), "Recent verified turn was lost after compaction.");
            Check(active.Contains(checkpoint.Id, StringComparison.Ordinal), "Compaction checkpoint was not injected as bounded historical context.");
            Check(!active.Contains("OLD_RAW_SENTINEL", StringComparison.Ordinal), "Old raw history was re-injected after compaction.");

            var rawAfter = File.ReadAllBytes(sessionPath);
            Check(rawHashBefore == Convert.ToHexString(SHA256.HashData(rawAfter)), "Creating/using a checkpoint modified durable raw session history.");
            Check(session.Events.Count == LabSession.Load(state).Events.Count, "Compaction changed persisted journal event count.");
            return Task.CompletedTask;
        });

        await Test("Checkpoint chains and source references round-trip without becoming source data", () =>
        {
            var state = Path.Combine(root, "chain");
            var manager = new CompactionManager(state);
            var first = manager.CreateCheckpoint(
                "First historical checkpoint.",
                [new AgentCompactionSourceReference(AgentCompactionSourceKind.ToolResult, "tool-result:1", Sequence: 1)],
                coveredThroughSequence: 1);
            var second = manager.CreateCheckpoint(
                "Second checkpoint builds on the first while retaining the earlier durable reference.",
                [new AgentCompactionSourceReference(AgentCompactionSourceKind.Checkpoint, first.Id, Sequence: 1)],
                coveredThroughSequence: 2,
                previousCheckpointId: first.Id);

            var loaded = manager.Load(second.Id);
            Check(loaded.Id == second.Id && loaded.Summary == second.Summary, "Checkpoint fields did not round-trip.");
            Check(loaded.PreviousCheckpointId == first.Id, "Checkpoint chain link was lost.");
            Check(loaded.Sources.Count == 1 && loaded.Sources[0].SourceId == first.Id, "Checkpoint source reference was lost.");
            var rendered = manager.RenderContext(loaded);
            Check(rendered.Contains("Previous checkpoint: " + first.Id, StringComparison.Ordinal), "Rendered context lost previous checkpoint reference.");
            return Task.CompletedTask;
        });

        await Test("Checkpoint integrity and source requirements reject summary-only or tampered history", () =>
        {
            var state = Path.Combine(root, "integrity");
            var manager = new CompactionManager(state);

            var emptyRejected = false;
            try
            {
                _ = manager.CreateCheckpoint("summary without evidence", [], 0);
            }
            catch (ArgumentOutOfRangeException)
            {
                emptyRejected = true;
            }
            Check(emptyRejected, "Summary-only checkpoint without durable sources was accepted.");

            var unsafeRejected = false;
            try
            {
                _ = manager.CreateCheckpoint(
                    "summary",
                    [new AgentCompactionSourceReference(AgentCompactionSourceKind.Snapshot, "unsafe\nsource")],
                    0);
            }
            catch (ArgumentException)
            {
                unsafeRejected = true;
            }
            Check(unsafeRejected, "Unsafe source ID was accepted.");

            var checkpoint = manager.CreateCheckpoint(
                "summary-original",
                [new AgentCompactionSourceReference(AgentCompactionSourceKind.Snapshot, "snapshot:1")],
                1);
            var path = Path.Combine(state, "checkpoints", checkpoint.Id + ".json");
            var json = File.ReadAllText(path).Replace("summary-original", "summary-tampered", StringComparison.Ordinal);
            File.WriteAllText(path, json);
            var tamperRejected = false;
            try
            {
                _ = manager.Load(checkpoint.Id);
            }
            catch (IOException)
            {
                tamperRejected = true;
            }
            Check(tamperRejected, "Tampered checkpoint summary was accepted.");

            var previousRejected = false;
            try
            {
                _ = manager.CreateCheckpoint(
                    "summary",
                    [new AgentCompactionSourceReference(AgentCompactionSourceKind.Snapshot, "snapshot:2")],
                    2,
                    previousCheckpointId: "h2cp1_" + new string('a', 32));
            }
            catch (FileNotFoundException)
            {
                previousRejected = true;
            }
            Check(previousRejected, "Missing previous checkpoint was accepted as a durable chain link.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "v2-compaction-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join("\n", lines));
        return failed == 0 ? 0 : 1;
    }
}

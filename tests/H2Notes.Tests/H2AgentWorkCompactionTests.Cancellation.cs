using System.Text.Json;
using H2AgentLab.Integration;
using H2AgentLab.Session;
using H2AgentLab.Transport;

internal static partial class H2AgentWorkCompactionTests
{
    private static void RunSourceCancellationTests(Action<string, Action> test)
    {
        foreach (var previous in new[] { false, true })
        foreach (var behavior in new[] { "normal", "throws", "invalid" })
            test($"AR-051 cancellation source commit prevents summarizer {behavior} previous={previous}",
                () => RunAsync(() => SourceCommitCancellation(previous, behavior)));
    }

    // E2: real durable source/activation journal and existing concrete Chat serializer. The
    // HTTP handler and optional summarizer are deterministic fixtures; no live model call.
    private static async Task SourceCommitCancellation(bool previous, string behavior)
    {
        using var f = new CaseRoot("source-cancel");
        using var wire = new H2AgentRequestBudgetTests.WireFixture("chat", Profile("chat"));
        await using var transport = wire.Create();
        var start = Start(f.Task, f.Turn); var state = Contract(f.Task);
        var archive = new AgentIntegrationTaskArchive(f.ArchiveRoot);
        using (archive)
        using (var cts = new CancellationTokenSource())
        {
            archive.Upsert(Summary(state, f.Turn));
            var sources = new List<AgentContextSourceRecord>();
            var active = new List<AgentContextCompactionRecord>();
            var cancelCycle = previous ? 2 : 1;
            var summaries = 0;
            var turn = new RuntimeCompactionCoordinator(f.TaskRoot, new()).CreateWorkTurn(start,
                record => { archive.RecordContextCompaction(record); active.Add(record); },
                record =>
                {
                    archive.RecordContextSource(record);
                    sources.Add(record);
                    // The cancellation arrives while durable source admission is in flight.
                    if (record.Cycle == cancelCycle) cts.Cancel();
                }, new(2, 2), (source, hash) =>
                {
                    summaries++;
                    if (cts.IsCancellationRequested)
                    {
                        if (behavior == "throws") throw new IOException("cancelled-summarizer-must-not-start");
                        if (behavior == "invalid") return new(hash, [new(0, 7, "INVALID")]);
                    }
                    return RuntimeContextCompactionTurn.ExtractSummary(source, hash);
                });
            var events = await Collect(transport.StartAsync(start));
            Exception? rejected = null;
            var totalBatches = 2 * cancelCycle;
            for (var i = 1; i <= totalBatches; i++)
            {
                var call = Call(events); var ack = Ack(start, call, $"source-{i} {Formula}", []);
                try
                {
                    var candidate = turn.Prepare(transport, i + 1, "observed", [call], ack,
                        state.Goals!.RevisionId, Anchors(state, f.Turn), cts.Token);
                    if (i == totalBatches) break; // A missing cancellation is diagnosed below.
                    events = candidate is { } p
                        ? await Collect(((IAgentContextRebaseTransport)transport).RebaseContextAsync(p.Context, ack, p.BodySha256))
                        : await Collect(transport.ContinueAsync(ack));
                }
                catch (Exception ex) { rejected = ex; break; }
            }
            // Save before assertions so the old-code control retains the actual failure.
            Save($"source-cancel-{previous}-{behavior}", new
            {
                previous, behavior, cancelCycle, summaries, sends = wire.Bodies.Count,
                rejection = rejected?.GetType().Name,
                cancellationTokenMatches = rejected is OperationCanceledException oce && oce.CancellationToken == cts.Token,
                sources, active
            });
            Check(summaries == cancelCycle - 1, "Cancelled source boundary invoked the summarizer.");
            Check(rejected is OperationCanceledException cancelled && cancelled.CancellationToken == cts.Token,
                "Source cancellation was replaced with a summary error or wrong cancellation token.");
            Check(active.Count == cancelCycle - 1 && sources.Count == cancelCycle && wire.Bodies.Count == totalBatches,
                "Cancelled candidate activated, lost source, or sent another request.");
            var last = sources[^1]; var artifacts = new ArtifactStore(f.TaskRoot);
            Check(artifacts.LoadHandle(last.Source.Id) == last.Source && artifacts.LoadHandle(last.Anchors.Id) == last.Anchors,
                "Source cancellation changed durable source identities.");
            using (var json = JsonDocument.Parse(artifacts.ReadText(last.Source.Id)))
                Check(json.RootElement.GetProperty("Batches")[0].GetProperty("Results")[0].GetProperty("Content").GetString()
                    == $"source-{totalBatches - 1} {Formula}", "Cancelled source lost exact formula.");
            if (previous)
                Check(new CompactionManager(f.TaskRoot).Fingerprint(active[0].CheckpointId) == active[0].CheckpointSha256,
                    "Cancellation changed the previously activated checkpoint.");
            var journalKinds = archive.ReadJournal(f.Task).Select(e => e.Kind).ToArray();
            var sent = wire.Bodies.Count;
            archive.Dispose();
            using var reopened = new AgentIntegrationTaskArchive(f.ArchiveRoot);
            var restored = reopened.ReadJournal(f.Task).ToArray();
            Check(restored.Select(e => e.Kind).SequenceEqual(journalKinds)
                && restored.Count(e => e.Kind == "context-source") == cancelCycle
                && restored.Count(e => e.Kind == "context-compaction") == cancelCycle - 1
                && wire.Bodies.Count == sent, "Reload activated cancelled work or replayed provider traffic.");
        }
    }
}

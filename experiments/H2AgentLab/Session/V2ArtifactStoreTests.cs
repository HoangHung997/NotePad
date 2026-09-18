using H2AgentLab.Context;

namespace H2AgentLab.Session;

public static class V2ArtifactStoreTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root)) throw new IOException("Use a new v2 artifact-store test directory.");
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

        await Test("Large tool output stays out of active context and is retrievable by opaque handle", () =>
        {
            var state = Path.Combine(root, "large-output");
            var store = new ArtifactStore(state);
            const string tail = "TAIL_SENTINEL_MUST_NOT_ENTER_ACTIVE_CONTEXT";
            var full = "stdout header\n" + new string('x', 120_000) + "\n" + tail;
            var projection = store.StoreText(
                AgentArtifactKind.Stdout,
                sourceId: "tool-run:42:stdout",
                toolName: "run_python",
                content: full,
                summary: "Python completed; stdout was large and is stored outside active context.",
                sequence: 42);

            Check(projection.Handle.Id.StartsWith("h2a1_", StringComparison.Ordinal), "Artifact handle scheme is missing.");
            Check(projection.Handle.Bytes == System.Text.Encoding.UTF8.GetByteCount(full), "Artifact byte count is wrong.");
            Check(projection.Handle.Sha256.Length == 64, "Artifact SHA-256 is missing.");
            Check(projection.ContextSummary.Summary.Contains(projection.Handle.Id, StringComparison.Ordinal), "Context summary does not contain the retrievable handle.");
            Check(projection.ContextSummary.Summary.Length <= ArtifactStore.MaxContextHandleCharacters, "Artifact handle projection is not bounded.");
            Check(!projection.ContextSummary.Summary.Contains(tail, StringComparison.Ordinal), "Full large output leaked into its context summary.");
            Check(!projection.ContextSummary.Summary.Contains(Path.GetFullPath(state), StringComparison.OrdinalIgnoreCase), "Local state path leaked into model context.");

            var snapshot = new AgentContextManager(new AgentContextBudget
            {
                MaxTotalCharacters = 1_400,
                MaxTaskContractCharacters = 0,
                MaxCurrentStateCharacters = 0,
                MaxRecentTurnsCharacters = 0,
                MaxToolSummariesCharacters = 1_200,
                MaxCompactedHistoryCharacters = 0,
                MaxCharactersPerItem = 1_000,
                MaxRecentTurns = 0,
                MaxToolSummaries = 2
            }).Build(new AgentContextInput(ToolSummaries: [projection.ContextSummary]));
            var active = snapshot.RuntimeContext.WorkingState ?? "";
            Check(active.Contains(projection.Handle.Id, StringComparison.Ordinal), "Active context lost the artifact handle.");
            Check(!active.Contains(tail, StringComparison.Ordinal), "Active context contains full large tool output.");
            Check(store.ReadText(projection.Handle.Id) == full, "Explicit artifact read did not return exact original text.");
            Check(store.LoadHandle(projection.Handle.Id) == projection.Handle, "Stored handle metadata did not round-trip.");
            return Task.CompletedTask;
        });

        await Test("Artifact content tampering is rejected before retrieval", () =>
        {
            var state = Path.Combine(root, "tamper");
            var store = new ArtifactStore(state);
            var projection = store.StoreText(
                AgentArtifactKind.DocumentExtract,
                sourceId: "document:extract:1",
                toolName: "read_document",
                content: "original document extract",
                summary: "Document extract is available by handle.",
                sequence: 1);

            var contentPath = Path.Combine(state, "artifacts", "context", projection.Handle.Id + ".txt");
            File.WriteAllText(contentPath, "tampered document extract");

            var rejected = false;
            try
            {
                _ = store.ReadText(projection.Handle.Id);
            }
            catch (IOException)
            {
                rejected = true;
            }
            Check(rejected, "Tampered artifact content was returned as trusted data.");
            return Task.CompletedTask;
        });

        await Test("Artifact summaries and handles are bounded and validated", () =>
        {
            var state = Path.Combine(root, "bounds");
            var store = new ArtifactStore(state);
            var projection = store.StoreText(
                AgentArtifactKind.Stderr,
                sourceId: "tool-run:7:stderr",
                toolName: "run_python",
                content: "diagnostic",
                summary: new string('s', 5_000),
                sequence: 7);

            Check(projection.ContextSummary.Summary.Length <= ArtifactStore.MaxContextHandleCharacters, "Long summary escaped the context projection bound.");
            Check(projection.ContextSummary.Summary.Contains("summary truncated", StringComparison.Ordinal), "Long summary was not explicitly marked truncated.");
            Check(projection.ContextSummary.Summary.Contains(projection.Handle.Id, StringComparison.Ordinal), "Truncated summary lost its artifact handle.");

            var invalidRejected = false;
            try
            {
                _ = store.ReadText("../outside");
            }
            catch (IOException)
            {
                invalidRejected = true;
            }
            Check(invalidRejected, "Path-like artifact handle was accepted.");

            var manifest = File.ReadAllText(Path.Combine(state, "artifacts", "context", projection.Handle.Id + ".json"));
            Check(!manifest.Contains("diagnostic", StringComparison.Ordinal), "Artifact manifest persisted full output text.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "v2-artifact-store-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join("\n", lines));
        return failed == 0 ? 0 : 1;
    }
}

using H2AgentLab.Prompting;
using H2AgentLab.Session;
using H2AgentLab.Transport;
using H2Notes.Core;

namespace H2AgentLab.Context;

public static class V2ContextCacheTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root)) throw new IOException("Use a new v2 context/cache test directory.");
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

        await Test("Stable prefix and cache identity stay equal across changing bounded runtime context", () =>
        {
            var stable = new AgentPromptStablePrefix(
                AgentVersions.Current,
                "BASE_STABLE",
                "SECURITY_STABLE",
                "MODEL_POLICY_STABLE",
                "TOOL_NAMESPACE_STABLE");
            var profile = new AiProfile
            {
                Protocol = AiProtocol.OpenAiResponses,
                BaseUrl = "https://api.openai.com/v1",
                Model = "gpt-5"
            };

            var first = AgentPromptLayout.Create(
                stable,
                new AgentPromptRuntimeContext(
                    "task-one",
                    "SESSION_A\nWORKSPACE_A",
                    "TIME_A"),
                "USER_A");
            var secondRuntime = new AgentPromptRuntimeContext(
                "task-two",
                "SESSION_B\nWORKSPACE_B",
                "TIME_B");
            var second = AgentPromptLayout.Create(
                stable,
                secondRuntime,
                "USER_B");

            Check(first.CacheBoundaryIndex == first.StablePrefix.Count
                && second.CacheBoundaryIndex == second.StablePrefix.Count,
                "Prompt cache boundary no longer matches the stable-prefix boundary.");
            Check(first.StablePrefix.SequenceEqual(second.StablePrefix),
                "Runtime context changed stable-prefix messages.");

            var firstKey = AgentPromptCacheIdentityBuilder.Build(first, profile).Key;
            var secondKey = AgentPromptCacheIdentityBuilder.Build(second, profile).Key;
            Check(firstKey == secondKey,
                "Runtime task/session/workspace/time/user changes invalidated stable prompt cache identity.");

            var changedStable = AgentPromptLayout.Create(
                stable with { SecurityPolicy = "SECURITY_CHANGED" },
                secondRuntime,
                "USER_B");
            var changedKey = AgentPromptCacheIdentityBuilder.Build(changedStable, profile).Key;
            Check(changedKey != firstKey,
                "A stable security-policy change failed to invalidate cache identity.");
            return Task.CompletedTask;
        });

        await Test("Active context remains hard-bounded and does not grow linearly from 20 to 1000 turns", () =>
        {
            var budget = new AgentContextBudget
            {
                MaxTotalCharacters = 2_200,
                MaxTaskContractCharacters = 120,
                MaxCurrentStateCharacters = 120,
                MaxRecentTurnsCharacters = 1_500,
                MaxToolSummariesCharacters = 200,
                MaxCompactedHistoryCharacters = 200,
                MaxCharactersPerItem = 180,
                MaxRecentTurns = 6,
                MaxToolSummaries = 1
            };
            var manager = new AgentContextManager(budget);

            static AgentContextTurn[] Turns(int count)
                => Enumerable.Range(0, count)
                    .Select(i => new AgentContextTurn(
                        sourceId: $"thread-turn-{i}",
                        role: i % 2 == 0 ? AgentTransportMessageRole.User : AgentTransportMessageRole.Assistant,
                        content: $"TURN_{i:D4}_" + new string('x', 220),
                        sequence: i,
                        relevance: 1))
                    .ToArray();

            var shortThread = manager.Build(new AgentContextInput(RecentTurns: Turns(20)));
            var mediumThread = manager.Build(new AgentContextInput(RecentTurns: Turns(200)));
            var longThread = manager.Build(new AgentContextInput(RecentTurns: Turns(1000)));

            foreach (var snapshot in new[] { shortThread, mediumThread, longThread })
                Check(snapshot.Usage.TotalCharacters <= budget.MaxTotalCharacters,
                    "Synthetic thread exceeded active context hard budget.");

            Check(mediumThread.Pressure.CandidateCharacters > shortThread.Pressure.CandidateCharacters
                && longThread.Pressure.CandidateCharacters > mediumThread.Pressure.CandidateCharacters,
                "Candidate context pressure did not grow with source thread size.");
            Check(longThread.Usage.TotalCharacters <= mediumThread.Usage.TotalCharacters + 120,
                "Active context grew materially with thread age instead of remaining bounded.");
            Check(longThread.Usage.SelectedRecentTurns == 6
                && longThread.Usage.DroppedRecentTurns == 994,
                "Long-thread selection/drop counts are wrong.");
            Check(longThread.RecentTurnSourceIds.SequenceEqual(
                    Enumerable.Range(994, 6).Select(i => $"thread-turn-{i}")),
                "Long-thread context did not retain the newest six equally relevant turns.");
            Check(longThread.Pressure.RequiresCompaction
                && longThread.Pressure.Reasons.Contains("recent-turns-dropped", StringComparer.Ordinal),
                "Long-thread source pressure did not request compaction.");
            Check(!(longThread.RuntimeContext.WorkingState ?? "").Contains("TURN_0000_", StringComparison.Ordinal),
                "Old raw thread content leaked into bounded active context.");
            return Task.CompletedTask;
        });

        await Test("Compacted context preserves durable journal and artifact sources without replaying old raw evidence", () =>
        {
            var state = Path.Combine(root, "source-preservation");
            var session = new LabSession();
            var oldEventIndex = session.Events.Count;
            session.Add("user", "OLD_DURABLE_JOURNAL_SENTINEL");
            for (var i = 0; i < 150; i++)
            {
                session.Add("assistant", $"older assistant {i}");
                session.Add("user", $"older user {i}");
            }
            session.Add("assistant", "RECENT_CONTEXT_SENTINEL");
            session.Save(state);

            var artifactStore = new ArtifactStore(state);
            const string fullArtifact = "FULL_ARTIFACT_SENTINEL_" + "document evidence body";
            var artifact = artifactStore.StoreText(
                AgentArtifactKind.DocumentExtract,
                sourceId: "document:phase03:fixture",
                toolName: "read_document",
                content: fullArtifact,
                summary: "Older document evidence is stored outside active context.",
                sequence: 50);

            var compaction = new CompactionManager(state);
            var checkpoint = compaction.CreateCheckpoint(
                "Older verified history was compacted. Use the durable references for exact evidence.",
                [
                    new AgentCompactionSourceReference(
                        AgentCompactionSourceKind.JournalEvent,
                        $"session:{session.Id:N}:event:{oldEventIndex}",
                        Sequence: oldEventIndex),
                    new AgentCompactionSourceReference(
                        AgentCompactionSourceKind.Artifact,
                        artifact.Handle.Id,
                        artifact.Handle.Sha256,
                        Sequence: 50)
                ],
                coveredThroughSequence: 300);

            var budget = new AgentContextBudget
            {
                MaxTotalCharacters = 7_000,
                MaxTaskContractCharacters = 300,
                MaxCurrentStateCharacters = 300,
                MaxRecentTurnsCharacters = 1_400,
                MaxToolSummariesCharacters = 1_200,
                MaxCompactedHistoryCharacters = 4_000,
                MaxCharactersPerItem = 700,
                MaxRecentTurns = 4,
                MaxToolSummaries = 2
            };
            var snapshot = new LabSessionContextAdapter(new AgentContextManager(budget)).Build(
                session,
                taskContract: "preserve durable evidence",
                currentState: "current state",
                toolSummaries: [artifact.ContextSummary],
                compactedHistory: compaction.RenderContext(checkpoint));
            var active = snapshot.RuntimeContext.WorkingState ?? "";

            Check(active.Contains("RECENT_CONTEXT_SENTINEL", StringComparison.Ordinal),
                "Recent verified turn disappeared after compaction.");
            Check(active.Contains(checkpoint.Id, StringComparison.Ordinal)
                && active.Contains(artifact.Handle.Id, StringComparison.Ordinal),
                "Compacted active context lost durable checkpoint/artifact references.");
            Check(active.Contains($"session:{session.Id:N}:event:{oldEventIndex}", StringComparison.Ordinal),
                "Compacted active context lost durable journal source reference.");
            Check(!active.Contains("OLD_DURABLE_JOURNAL_SENTINEL", StringComparison.Ordinal),
                "Old raw journal text was replayed after compaction.");
            Check(!active.Contains(fullArtifact, StringComparison.Ordinal),
                "Full artifact content was replayed into active context.");

            var persisted = LabSession.Load(state);
            Check(persisted.Events.Count == session.Events.Count
                && persisted.Events[oldEventIndex].Text == "OLD_DURABLE_JOURNAL_SENTINEL",
                "Compaction/context selection changed durable raw journal evidence.");
            Check(artifactStore.ReadText(artifact.Handle.Id) == fullArtifact,
                "Full artifact evidence was not exactly retrievable by durable handle.");
            return Task.CompletedTask;
        });

        await Test("Cache identity stays stable while thread pressure and compaction checkpoints change", () =>
        {
            var stable = new AgentPromptStablePrefix(
                AgentVersions.Current,
                "BASE",
                "SECURITY",
                "MODEL",
                "TOOLS");
            var profile = new AiProfile
            {
                Protocol = AiProtocol.OpenAiResponses,
                BaseUrl = "https://api.openai.com/v1",
                Model = "gpt-5"
            };
            var budget = new AgentContextBudget
            {
                MaxTotalCharacters = 1_500,
                MaxTaskContractCharacters = 100,
                MaxCurrentStateCharacters = 100,
                MaxRecentTurnsCharacters = 800,
                MaxToolSummariesCharacters = 100,
                MaxCompactedHistoryCharacters = 300,
                MaxCharactersPerItem = 120,
                MaxRecentTurns = 4,
                MaxToolSummaries = 1
            };
            var manager = new AgentContextManager(budget);
            var small = manager.Build(new AgentContextInput(
                RecentTurns:
                [
                    new AgentContextTurn("turn-a", AgentTransportMessageRole.User, "small", 1)
                ]));
            var manyTurns = Enumerable.Range(0, 400)
                .Select(i => new AgentContextTurn(
                    sourceId: $"turn-{i}",
                    role: i % 2 == 0 ? AgentTransportMessageRole.User : AgentTransportMessageRole.Assistant,
                    content: new string('z', 200),
                    sequence: i))
                .ToArray();
            var stressed = manager.Build(new AgentContextInput(
                RecentTurns: manyTurns,
                CompactedHistory: "checkpoint h2cp1_00000000000000000000000000000000"));

            var smallLayout = AgentPromptLayout.Create(stable, small.RuntimeContext, "question-small");
            var stressedLayout = AgentPromptLayout.Create(stable, stressed.RuntimeContext, "question-stressed");
            Check(smallLayout.StablePrefix.SequenceEqual(stressedLayout.StablePrefix),
                "Thread pressure changed stable prefix.");
            Check(AgentPromptCacheIdentityBuilder.Build(smallLayout, profile).Key
                == AgentPromptCacheIdentityBuilder.Build(stressedLayout, profile).Key,
                "Thread/compaction runtime changes invalidated stable cache identity.");
            Check(stressed.Pressure.RequiresCompaction,
                "Stressed runtime fixture did not expose compaction pressure.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "v2-context-cache-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join("\n", lines));
        return failed == 0 ? 0 : 1;
    }
}

using H2AgentLab.Prompting;
using H2AgentLab.Transport;

namespace H2AgentLab.Context;

public static class V2SessionContextTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root)) throw new IOException("Use a new v2 session-context test directory.");
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

        await Test("Raw LabSession stays durable while v2 active context is filtered and bounded", () =>
        {
            var state = Path.Combine(root, "durable-session");
            var session = new LabSession { Workspace = @"C:\fixture-workspace" };
            session.Add("user", "OLD_USER_SENTINEL_SHOULD_STAY_ON_DISK");
            session.Add("assistant", "OLD_ASSISTANT_SENTINEL_SHOULD_STAY_ON_DISK");
            for (var i = 0; i < 12; i++)
            {
                session.Add("user", $"older user {i}");
                session.Add("assistant", $"older assistant {i}");
            }
            session.Add("script", "SCRIPT_SENTINEL_RAW_ONLY");
            session.Add("recovery", "RECOVERY_SENTINEL_RAW_ONLY");
            session.Add("unverified-draft", "UNVERIFIED_SENTINEL_RAW_ONLY");
            var recentUserIndex = session.Events.Count;
            session.Add("user", "RECENT_USER_VERIFIED");
            var recentAssistantIndex = session.Events.Count;
            session.Add("assistant", "RECENT_ASSISTANT_VERIFIED");
            session.Save(state);

            var budget = new AgentContextBudget
            {
                MaxTotalCharacters = 700,
                MaxTaskContractCharacters = 80,
                MaxCurrentStateCharacters = 100,
                MaxRecentTurnsCharacters = 300,
                MaxToolSummariesCharacters = 0,
                MaxCompactedHistoryCharacters = 0,
                MaxCharactersPerItem = 120,
                MaxRecentTurns = 2,
                MaxToolSummaries = 0
            };
            var snapshot = new LabSessionContextAdapter(new AgentContextManager(budget)).Build(
                session,
                taskContract: "TASK_CONTRACT_SENTINEL",
                currentState: "CURRENT_STATE_SENTINEL");
            var active = (snapshot.RuntimeContext.TaskContract ?? "") + "\n" + (snapshot.RuntimeContext.WorkingState ?? "");

            Check(snapshot.Usage.TotalCharacters <= budget.MaxTotalCharacters, "Active v2 context exceeded its total budget.");
            Check(active.Contains("RECENT_USER_VERIFIED", StringComparison.Ordinal), "Recent verified user turn missing from v2 context.");
            Check(active.Contains("RECENT_ASSISTANT_VERIFIED", StringComparison.Ordinal), "Recent verified assistant turn missing from v2 context.");
            Check(!active.Contains("OLD_USER_SENTINEL", StringComparison.Ordinal), "Old raw user history was blindly injected into active v2 context.");
            Check(!active.Contains("OLD_ASSISTANT_SENTINEL", StringComparison.Ordinal), "Old raw assistant history was blindly injected into active v2 context.");
            Check(!active.Contains("SCRIPT_SENTINEL", StringComparison.Ordinal), "Script journal event leaked into active v2 context.");
            Check(!active.Contains("RECOVERY_SENTINEL", StringComparison.Ordinal), "Recovery journal event leaked into active v2 context without an explicit context source.");
            Check(!active.Contains("UNVERIFIED_SENTINEL", StringComparison.Ordinal), "Unverified draft leaked into active v2 context.");
            Check(snapshot.RecentTurnSourceIds.SequenceEqual(new[]
            {
                $"session:{session.Id:N}:event:{recentUserIndex}",
                $"session:{session.Id:N}:event:{recentAssistantIndex}"
            }), "Session adapter did not preserve exact selected journal source IDs.");

            var rawJson = File.ReadAllText(Path.Combine(state, "session.json"));
            Check(rawJson.Contains("OLD_USER_SENTINEL_SHOULD_STAY_ON_DISK", StringComparison.Ordinal), "Raw old user journal event was lost from disk.");
            Check(rawJson.Contains("SCRIPT_SENTINEL_RAW_ONLY", StringComparison.Ordinal), "Raw script journal event was lost from disk.");
            Check(rawJson.Contains("RECOVERY_SENTINEL_RAW_ONLY", StringComparison.Ordinal), "Raw recovery journal event was lost from disk.");
            Check(rawJson.Contains("UNVERIFIED_SENTINEL_RAW_ONLY", StringComparison.Ordinal), "Raw unverified draft was lost from disk.");
            var reloaded = LabSession.Load(state) ?? throw new InvalidOperationException("Persisted LabSession could not be reloaded.");
            Check(reloaded.Events.Count == session.Events.Count, "Bounded active context mutated or truncated the durable session journal.");
            return Task.CompletedTask;
        });

        await Test("V2 prompt consumes adapter snapshot instead of raw LabSession.Context output", () =>
        {
            var state = Path.Combine(root, "prompt-session");
            var session = new LabSession();
            session.Add("user", "RAW_OLD_SENTINEL");
            for (var i = 0; i < 10; i++)
            {
                session.Add("assistant", "older assistant " + i);
                session.Add("user", "older user " + i);
            }
            session.Add("assistant", "RECENT_ASSISTANT_FOR_PROMPT");
            session.Save(state);

            var manager = new AgentContextManager(new AgentContextBudget
            {
                MaxTotalCharacters = 500,
                MaxTaskContractCharacters = 80,
                MaxCurrentStateCharacters = 80,
                MaxRecentTurnsCharacters = 180,
                MaxToolSummariesCharacters = 0,
                MaxCompactedHistoryCharacters = 0,
                MaxCharactersPerItem = 100,
                MaxRecentTurns = 1,
                MaxToolSummaries = 0
            });
            var snapshot = new LabSessionContextAdapter(manager).Build(session, taskContract: "task", currentState: "state");
            var layout = AgentPromptLayout.Create(
                new AgentPromptStablePrefix(AgentVersions.Current, "base", "security", "model", "tools"),
                snapshot.RuntimeContext,
                "CURRENT_USER_INPUT");
            var prompt = string.Join("\n", layout.Messages.Select(x => x.Content));

            Check(prompt.Contains("RECENT_ASSISTANT_FOR_PROMPT", StringComparison.Ordinal), "V2 prompt did not consume the bounded session snapshot.");
            Check(prompt.Contains("CURRENT_USER_INPUT", StringComparison.Ordinal), "Current user input missing from v2 prompt layout.");
            Check(!prompt.Contains("RAW_OLD_SENTINEL", StringComparison.Ordinal), "V2 prompt still contains raw old journal history.");
            Check(session.Context().Contains("RAW_OLD_SENTINEL", StringComparison.Ordinal), "Fixture no longer proves legacy session.Context() contains the raw old journal sentinel.");
            Check(File.ReadAllText(Path.Combine(state, "session.json")).Contains("RAW_OLD_SENTINEL", StringComparison.Ordinal), "Raw journal evidence disappeared from disk.");
            return Task.CompletedTask;
        });

        await Test("Non-conversation journal kinds never become recent turns implicitly", () =>
        {
            var session = new LabSession();
            session.Add("script", "script");
            session.Add("recovery", "recovery");
            session.Add("unverified-draft", "draft");
            session.Add("future-kind", "future");
            var snapshot = new LabSessionContextAdapter(new AgentContextManager(new AgentContextBudget
            {
                MaxTotalCharacters = 300,
                MaxTaskContractCharacters = 0,
                MaxCurrentStateCharacters = 0,
                MaxRecentTurnsCharacters = 200,
                MaxToolSummariesCharacters = 0,
                MaxCompactedHistoryCharacters = 0,
                MaxCharactersPerItem = 100,
                MaxRecentTurns = 4,
                MaxToolSummaries = 0
            })).Build(session);

            Check(snapshot.RecentTurnSourceIds.Count == 0, "Non-conversation journal events were promoted to recent turns.");
            Check(string.IsNullOrEmpty(snapshot.RuntimeContext.WorkingState), "Filtered journal events still produced active v2 working context.");
            Check(session.Events.Count == 4, "Filtering active context mutated raw journal events.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "v2-session-context-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join("\n", lines));
        return failed == 0 ? 0 : 1;
    }
}

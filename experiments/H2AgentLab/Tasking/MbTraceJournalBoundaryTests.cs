using System.Text.Json;
using H2AgentLab.Metrics;

namespace H2AgentLab.Tasking;

public static class MbTraceJournalBoundaryTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-95 test directory.");
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
                lines.Add("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        await Test("MB-95 typed progress is bounded ephemeral UI state", () =>
        {
            var progress = new AgentProgressEventStream();
            var first = progress.Add(
                AgentProgressEventKind.Phase,
                "grounded",
                new string('x', 3_000));
            var second = progress.Add(
                AgentProgressEventKind.Verification,
                "verified",
                "Verifier completed.");

            Check(first.Sequence == 0 && second.Sequence == 1,
                "Progress sequence is not deterministic.");
            Check(first.Message.Length == 2_000,
                "Progress message bound regressed.");
            Check(progress.Events.Select(x => x.Kind).SequenceEqual(new[]
            {
                AgentProgressEventKind.Phase,
                AgentProgressEventKind.Verification
            }), "Typed progress kind ordering changed.");
            return Task.CompletedTask;
        });

        await Test("MB-95 LabSession journal rejects ephemeral UI progress and stores telemetry by reference only", () =>
        {
            var session = new global::H2AgentLab.LabSession();
            foreach (var kind in new[]
            {
                "status",
                "progress",
                "thinking",
                "thinking-clear",
                "delta"
            })
            {
                try
                {
                    session.Add(kind, "must-not-persist");
                    throw new InvalidOperationException(
                        "Ephemeral UI kind entered durable journal: " + kind);
                }
                catch (InvalidOperationException ex) when (
                    ex.Message.Contains("Ephemeral UI progress", StringComparison.Ordinal))
                {
                }
            }

            session.Add("user", "durable-user");
            session.Add("assistant", "durable-assistant");
            var privateTrace = Path.Combine(
                root,
                "private",
                "traces",
                "turn-secret-path.json");
            session.AddTelemetryReference(privateTrace);

            Check(session.Events.Count == 3,
                "Durable journal event count is wrong.");
            var telemetry = session.Events.Single(x =>
                x.Kind == "telemetry-reference");
            Check(telemetry.Text.Contains("turn-secret-path.json", StringComparison.Ordinal)
                  && !telemetry.Text.Contains(
                      Path.Combine(root, "private"),
                      StringComparison.Ordinal),
                "Telemetry journal entry stored a host path instead of a safe reference.");
            return Task.CompletedTask;
        });

        await Test("MB-95 durable journal survives reload while progress stays outside persisted conversation", () =>
        {
            var state = Path.Combine(root, "journal-state");
            var session = new global::H2AgentLab.LabSession
            {
                Workspace = root
            };
            session.Add("user", "journal-user");
            session.Add("assistant", "journal-assistant");
            session.Save(state);

            var loaded = global::H2AgentLab.LabSession.Load(state);
            Check(loaded.Events.Select(x => x.Kind).SequenceEqual(
                    new[] { "user", "assistant" }),
                "Durable conversation journal did not round-trip.");
            Check(!File.ReadAllText(Path.Combine(state, "session.json"))
                    .Contains("progress", StringComparison.Ordinal),
                "Ephemeral progress leaked into persisted journal.");
            return Task.CompletedTask;
        });

        await Test("MB-95 machine telemetry persists independently from progress and journal text", () =>
        {
            var telemetryRoot = Path.Combine(root, "telemetry");
            var telemetry = new AgentRunTelemetry();
            telemetry.Trace.Mark(
                AgentTraceKind.RequestStart,
                "provider-request",
                "PRIVATE-DETAIL-NOT-PERSISTED");
            telemetry.Metrics.IncrementModelCalls();

            var progress = new AgentProgressEventStream();
            progress.Add(
                AgentProgressEventKind.Phase,
                "progress-secret-code",
                "PROGRESS-MESSAGE-MUST-NOT-BE-TELEMETRY");
            var session = new global::H2AgentLab.LabSession();
            session.Add("user", "JOURNAL-MESSAGE-MUST-NOT-BE-TELEMETRY");

            var path = AgentTraceStore.Save(
                telemetryRoot,
                telemetry.Trace,
                telemetry.Metrics);
            var json = File.ReadAllText(path);

            Check(json.Contains("provider-request", StringComparison.Ordinal),
                "Machine telemetry event was not persisted.");
            Check(!json.Contains("PRIVATE-DETAIL-NOT-PERSISTED", StringComparison.Ordinal)
                  && !json.Contains("PROGRESS-MESSAGE-MUST-NOT-BE-TELEMETRY", StringComparison.Ordinal)
                  && !json.Contains("JOURNAL-MESSAGE-MUST-NOT-BE-TELEMETRY", StringComparison.Ordinal),
                "Telemetry store copied detail/progress/journal content.");
            return Task.CompletedTask;
        });

        await Test("MB-95 repository names and channels enforce the three-source boundary", () =>
        {
            var repo = FindRepoRoot();
            var tasking = File.ReadAllText(Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "Tasking",
                "AgentOrchestratedRun.cs"));
            var labWindow = File.ReadAllText(Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "LabWindow.cs"));
            var labSession = File.ReadAllText(Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "LabSession.cs"));
            var metrics = File.ReadAllText(Path.Combine(
                repo,
                "experiments",
                "H2AgentLab",
                "Metrics",
                "AgentTrace.cs"));

            Check(tasking.Contains("AgentProgressEventStream", StringComparison.Ordinal)
                  && tasking.Contains("ProgressEvents", StringComparison.Ordinal)
                  && tasking.Contains("output(\"progress\"", StringComparison.Ordinal)
                  && !tasking.Contains("AgentTraceEventStream", StringComparison.Ordinal)
                  && !tasking.Contains("AgentTraceEventKind", StringComparison.Ordinal)
                  && !tasking.Contains("TraceEvents", StringComparison.Ordinal),
                "Tasking still uses telemetry trace naming for user-visible progress.");
            Check(labWindow.Contains("kind == \"progress\"", StringComparison.Ordinal)
                  && !labWindow.Contains("kind == \"trace\"", StringComparison.Ordinal)
                  && labWindow.Contains("AddTelemetryReference", StringComparison.Ordinal),
                "Lab UI still conflates progress with machine trace or duplicates telemetry.");
            Check(labSession.Contains("EphemeralUiKinds", StringComparison.Ordinal)
                  && labSession.Contains("telemetry-reference", StringComparison.Ordinal),
                "Durable journal boundary is not explicit.");
            Check(metrics.Contains("public sealed class AgentTrace", StringComparison.Ordinal)
                  && metrics.Contains("AgentTraceEvent", StringComparison.Ordinal),
                "Machine telemetry trace was accidentally removed instead of separated.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-trace-journal-boundary-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(current.FullName, "experiments")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate repository root.");
    }
}

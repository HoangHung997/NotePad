using System.Text.Json;
using H2AgentLab.Runtime;

namespace H2AgentLab.Acceptance;

public static class MbCoreCorrectnessGateTests
{
    private const double MinimumCorrectnessPercent = 90d;

    private sealed record CoreCase(
        string Id,
        string Requirement,
        string Evidence,
        bool Passed,
        string? Failure);

    private sealed record FalseCompletionCase(
        string Id,
        string Requirement,
        bool Passed);

    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-105 correctness gate directory.");
        Directory.CreateDirectory(root);

        var corpus = new List<CoreCase>();

        var mbaRoot = Path.Combine(root, "minimum-bootable-agent");
        _ = await MbMinimumBootableAgentAcceptanceTests.Run(mbaRoot)
            .ConfigureAwait(false);
        using (var mbaJson = JsonDocument.Parse(
                   await File.ReadAllTextAsync(
                       Path.Combine(
                           mbaRoot,
                           "mb-minimum-bootable-agent-acceptance.json"))
                       .ConfigureAwait(false)))
        {
            foreach (var item in mbaJson.RootElement
                         .GetProperty("cases")
                         .EnumerateArray())
            {
                corpus.Add(new CoreCase(
                    item.GetProperty("Id").GetString()
                        ?? throw new InvalidDataException("MB-100 case id is missing."),
                    item.GetProperty("Requirement").GetString()
                        ?? throw new InvalidDataException("MB-100 requirement is missing."),
                    item.GetProperty("Evidence").GetString()
                        ?? "MB-100 acceptance evidence",
                    item.GetProperty("Passed").GetBoolean(),
                    item.TryGetProperty("Failure", out var failure)
                        && failure.ValueKind == JsonValueKind.String
                            ? failure.GetString()
                            : null));
            }
        }

        var uiRoot = Path.Combine(root, "normal-ui-v2");
        _ = await MbNormalUiV2PathTests.Run(uiRoot).ConfigureAwait(false);
        var uiLines = await File.ReadAllLinesAsync(
            Path.Combine(uiRoot, "mb-normal-ui-v2-path-tests.txt"))
            .ConfigureAwait(false);

        AddTextCase(
            corpus,
            "CORE-UI-READ",
            "normal read-only UI task completes correctly through AgentRuntime V2",
            "MB-101 exact AgentOrchestratedRun read-only UI fixture",
            uiLines,
            "MB-101 normal read-only UI facade creates AgentRuntime over IAgentTransport and bounded V2 context");

        AddTextCase(
            corpus,
            "CORE-UI-MUTATE",
            "normal mutating UI task repairs verifier failure and completes only after PASS",
            "MB-101 exact AgentOrchestratedRun mutating verifier/repair fixture",
            uiLines,
            "MB-101 normal mutating UI facade performs deferred loading verifier repair and host-owned completion");

        var completionRoot = Path.Combine(root, "completion-invariants");
        var completionExit = await MbCompletionGateTests.Run(completionRoot)
            .ConfigureAwait(false);
        var completionLines = await File.ReadAllLinesAsync(
            Path.Combine(completionRoot, "mb-completion-gate-tests.txt"))
            .ConfigureAwait(false);

        var falseCompletionCases = new[]
        {
            Negative(
                "NEG-01",
                "model phrase done cannot mark a mutating task Completed",
                completionLines,
                "MB-44 model done cannot complete a mutating host task"),
            Negative(
                "NEG-02",
                "required verifier failure cannot mark a task Completed",
                completionLines,
                "MB-44 required verifier failure keeps task non-complete")
        };

        var supportedTotal = corpus.Count;
        var supportedPassed = corpus.Count(x => x.Passed);
        var supportedFailed = supportedTotal - supportedPassed;
        var correctnessPercent = supportedTotal == 0
            ? 0d
            : supportedPassed * 100d / supportedTotal;
        var falseCompletionViolations = falseCompletionCases.Count(x => !x.Passed);
        var gatePassed =
            correctnessPercent >= MinimumCorrectnessPercent
            && falseCompletionViolations == 0
            && completionExit == 0;

        var lines = corpus.Select(x =>
                (x.Passed ? "PASS " : "FAIL ")
                + x.Id + " " + x.Requirement
                + " | evidence=" + x.Evidence
                + (x.Failure is null ? "" : " | " + x.Failure))
            .ToList();
        lines.AddRange(falseCompletionCases.Select(x =>
            (x.Passed ? "PASS " : "FAIL ")
            + x.Id + " " + x.Requirement));
        lines.Add(
            $"CORRECTNESS: {supportedPassed}/{supportedTotal} = {correctnessPercent:F2}% (required >= {MinimumCorrectnessPercent:F2}%).");
        lines.Add(
            $"FALSE_COMPLETION_VIOLATIONS: {falseCompletionViolations} (required 0).");
        lines.Add(
            $"COMPLETION_POLICY_SUITE_EXIT: {completionExit}.");
        lines.Add(
            $"RESULT: {(gatePassed ? "PASS" : "FAIL")} MB-105 core correctness gate.");

        await File.WriteAllLinesAsync(
            Path.Combine(root, "mb-core-correctness-gate-tests.txt"),
            lines).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(root, "mb-core-correctness-gate-tests.json"),
            JsonSerializer.Serialize(
                new
                {
                    gate = "MB-105",
                    minimumCorrectnessPercent = MinimumCorrectnessPercent,
                    supportedTotal,
                    supportedPassed,
                    supportedFailed,
                    correctnessPercent,
                    falseCompletionViolations,
                    completionPolicySuiteExit = completionExit,
                    passed = gatePassed,
                    corpus,
                    falseCompletionCases
                },
                new JsonSerializerOptions { WriteIndented = true }))
            .ConfigureAwait(false);

        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return gatePassed ? 0 : 1;
    }

    private static void AddTextCase(
        ICollection<CoreCase> corpus,
        string id,
        string requirement,
        string evidence,
        IEnumerable<string> lines,
        string expectedName)
    {
        var all = lines.ToArray();
        var pass = all.Any(x =>
            x.StartsWith("PASS " + expectedName, StringComparison.Ordinal));
        var failure = all.FirstOrDefault(x =>
            x.StartsWith("FAIL " + expectedName, StringComparison.Ordinal));

        corpus.Add(new CoreCase(
            id,
            requirement,
            evidence,
            pass,
            pass
                ? null
                : failure ?? "Expected accepted UI task case was not present."));
    }

    private static FalseCompletionCase Negative(
        string id,
        string requirement,
        IEnumerable<string> lines,
        string expectedName)
        => new(
            id,
            requirement,
            lines.Any(x =>
                x.StartsWith("PASS " + expectedName, StringComparison.Ordinal)));
}

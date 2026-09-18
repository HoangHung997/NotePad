using System.Text.Json;
using H2AgentLab.OfficeProtocol;

namespace H2AgentLab.Verification;

public sealed record LiveWordExpectedParagraph(
    int Index,
    WordParagraphState Expected);

public sealed record LiveWordVerificationExpectation(
    IReadOnlyList<LiveWordExpectedParagraph> ExpectedParagraphs,
    bool PreserveOtherParagraphs = true,
    bool PreserveStructure = true);

public static class LiveWordVerifier
{
    public const string VerifierId = "word-live";
    public const string TargetCriterionId = "word-live.targets";
    public const string PreserveCriterionId = "word-live.preserve";

    public static VerificationReport Verify(
        WordLiveSnapshot before,
        WordLiveSnapshot after,
        LiveWordVerificationExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(expectation);

        var failures = new List<string>();
        var evidence = new List<string>();
        foreach (var expected in expectation.ExpectedParagraphs)
        {
            if (expected.Index < 0 || expected.Index >= after.Paragraphs.Count)
            {
                failures.Add($"paragraph:{expected.Index}: missing");
                continue;
            }
            var actual = after.Paragraphs[expected.Index];
            evidence.Add($"word-live:paragraph:{expected.Index}");
            if (!Same(actual, expected.Expected))
                failures.Add($"paragraph:{expected.Index}: target state mismatch");
        }

        var target = Result(
            TargetCriterionId,
            failures.Count == 0,
            evidence,
            failures.Count == 0 ? null : string.Join("; ", failures));

        var preserveFailures = new List<string>();
        if (expectation.PreserveOtherParagraphs)
        {
            var targets = expectation.ExpectedParagraphs.Select(x => x.Index).ToHashSet();
            var max = Math.Max(before.Paragraphs.Count, after.Paragraphs.Count);
            for (var i = 0; i < max; i++)
            {
                if (targets.Contains(i)) continue;
                var left = i < before.Paragraphs.Count ? before.Paragraphs[i] : null;
                var right = i < after.Paragraphs.Count ? after.Paragraphs[i] : null;
                if (!SameNullable(left, right))
                    preserveFailures.Add($"paragraph:{i}: changed outside target set");
            }
        }

        if (expectation.PreserveStructure)
        {
            if (!Same(before.Tables, after.Tables)) preserveFailures.Add("Tables changed.");
            if (!Same(before.Sections, after.Sections)) preserveFailures.Add("Sections changed.");
            if (!Same(before.Headers, after.Headers)) preserveFailures.Add("Headers changed.");
            if (!Same(before.Footers, after.Footers)) preserveFailures.Add("Footers changed.");
        }

        var preserve = Result(
            PreserveCriterionId,
            preserveFailures.Count == 0,
            [],
            preserveFailures.Count == 0 ? null : string.Join("; ", preserveFailures.Take(20)));

        return new VerificationReport(
            VerifierId,
            [target, preserve],
            ["before-state:" + before.StateToken, "after-state:" + after.StateToken]);
    }

    private static bool Same<T>(T left, T right)
        => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    private static bool SameNullable(WordParagraphState? left, WordParagraphState? right)
        => left is null ? right is null : right is not null && Same(left, right);

    private static VerificationCriterionResult Result(
        string id,
        bool passed,
        IEnumerable<string> evidence,
        string? failure)
        => passed
            ? new(id, VerificationCriterionStatus.Passed, evidence)
            : new(
                id,
                VerificationCriterionStatus.Failed,
                evidence,
                new VerificationFailure(id, failure ?? "Live Word verification failed.", evidence));
}

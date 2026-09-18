using System.Text.Json;
using H2AgentLab.Documents;

namespace H2AgentLab.Verification;

public sealed record WordExpectedBodyParagraph(
    int Index,
    ClosedWordParagraphSnapshot Expected);

public sealed record WordExpectedPartParagraph(
    string RelationshipId,
    int Index,
    ClosedWordParagraphSnapshot Expected);

public sealed record WordVerificationExpectation
{
    public WordVerificationExpectation(
        IEnumerable<WordExpectedBodyParagraph>? expectedBodyParagraphs = null,
        IEnumerable<WordExpectedPartParagraph>? expectedHeaderParagraphs = null,
        IEnumerable<WordExpectedPartParagraph>? expectedFooterParagraphs = null,
        bool preserveOtherBodyParagraphs = true,
        bool preserveTables = true,
        bool preserveSections = true,
        bool preserveOtherHeaders = true,
        bool preserveOtherFooters = true)
    {
        ExpectedBodyParagraphs = SnapshotBody(expectedBodyParagraphs);
        ExpectedHeaderParagraphs = SnapshotParts(expectedHeaderParagraphs, nameof(expectedHeaderParagraphs));
        ExpectedFooterParagraphs = SnapshotParts(expectedFooterParagraphs, nameof(expectedFooterParagraphs));
        PreserveOtherBodyParagraphs = preserveOtherBodyParagraphs;
        PreserveTables = preserveTables;
        PreserveSections = preserveSections;
        PreserveOtherHeaders = preserveOtherHeaders;
        PreserveOtherFooters = preserveOtherFooters;
    }

    public IReadOnlyList<WordExpectedBodyParagraph> ExpectedBodyParagraphs { get; }
    public IReadOnlyList<WordExpectedPartParagraph> ExpectedHeaderParagraphs { get; }
    public IReadOnlyList<WordExpectedPartParagraph> ExpectedFooterParagraphs { get; }
    public bool PreserveOtherBodyParagraphs { get; }
    public bool PreserveTables { get; }
    public bool PreserveSections { get; }
    public bool PreserveOtherHeaders { get; }
    public bool PreserveOtherFooters { get; }

    private static IReadOnlyList<WordExpectedBodyParagraph> SnapshotBody(
        IEnumerable<WordExpectedBodyParagraph>? values)
    {
        var result = (values ?? Array.Empty<WordExpectedBodyParagraph>()).ToArray();
        if (result.Any(x => x is null || x.Index < 0 || x.Expected is null))
            throw new ArgumentException("Expected body paragraphs contain an invalid entry.", nameof(values));
        var duplicate = result.GroupBy(x => x.Index).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Duplicate expected body paragraph index {duplicate.Key}.", nameof(values));
        return Array.AsReadOnly(result);
    }

    private static IReadOnlyList<WordExpectedPartParagraph> SnapshotParts(
        IEnumerable<WordExpectedPartParagraph>? values,
        string parameterName)
    {
        var result = (values ?? Array.Empty<WordExpectedPartParagraph>()).ToArray();
        if (result.Any(x => x is null || x.Index < 0 || x.Expected is null || string.IsNullOrWhiteSpace(x.RelationshipId)))
            throw new ArgumentException("Expected Word part paragraphs contain an invalid entry.", parameterName);
        var duplicate = result
            .GroupBy(x => x.RelationshipId.Trim() + ":" + x.Index, StringComparer.Ordinal)
            .FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Duplicate expected Word part paragraph '{duplicate.Key}'.", parameterName);
        return Array.AsReadOnly(result);
    }
}

public static class WordVerifier
{
    public const string VerifierId = "word-closed";
    public const string TargetParagraphsCriterionId = "word.targets";
    public const string PreserveBodyCriterionId = "word.preserve-body";
    public const string StructureCriterionId = "word.structure";

    public static VerificationReport Verify(
        ClosedWordSnapshot before,
        ClosedWordSnapshot after,
        WordVerificationExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(expectation);

        var targetFailures = new List<string>();
        var targetEvidence = new List<string>();

        foreach (var expected in expectation.ExpectedBodyParagraphs.OrderBy(x => x.Index))
        {
            if (expected.Index >= after.BodyParagraphs.Count)
            {
                targetFailures.Add($"body:{expected.Index}: paragraph missing");
                continue;
            }

            var actual = after.BodyParagraphs[expected.Index];
            targetEvidence.Add($"word:body:{expected.Index}");
            if (!Same(actual, expected.Expected))
                targetFailures.Add($"body:{expected.Index}: expected {Describe(expected.Expected)}, actual {Describe(actual)}");
        }

        VerifyExpectedPartParagraphs(
            "header",
            after.Headers,
            expectation.ExpectedHeaderParagraphs,
            targetFailures,
            targetEvidence);
        VerifyExpectedPartParagraphs(
            "footer",
            after.Footers,
            expectation.ExpectedFooterParagraphs,
            targetFailures,
            targetEvidence);

        var targetResult = Result(
            TargetParagraphsCriterionId,
            targetFailures.Count == 0,
            targetEvidence,
            targetFailures.Count == 0 ? null : string.Join("; ", targetFailures.Take(20)));

        var expectedBodyIndexes = expectation.ExpectedBodyParagraphs
            .Select(x => x.Index)
            .ToHashSet();
        var bodyFailures = new List<string>();
        var bodyEvidence = new List<string>();
        if (expectation.PreserveOtherBodyParagraphs)
        {
            var max = Math.Max(before.BodyParagraphs.Count, after.BodyParagraphs.Count);
            for (var index = 0; index < max; index++)
            {
                if (expectedBodyIndexes.Contains(index)) continue;
                var left = index < before.BodyParagraphs.Count ? before.BodyParagraphs[index] : null;
                var right = index < after.BodyParagraphs.Count ? after.BodyParagraphs[index] : null;
                if (!SameNullable(left, right))
                    bodyFailures.Add($"body:{index}: changed outside target set");
                else if (left is not null)
                    bodyEvidence.Add($"preserved:body:{index}");
            }
        }

        var bodyResult = Result(
            PreserveBodyCriterionId,
            bodyFailures.Count == 0,
            bodyEvidence.Take(64),
            bodyFailures.Count == 0 ? null : string.Join("; ", bodyFailures.Take(20)));

        var structureFailures = new List<string>();
        var structureEvidence = new List<string>();

        if (expectation.PreserveTables && !Same(before.Tables, after.Tables))
            structureFailures.Add("Tables changed outside allowed targets.");
        else if (expectation.PreserveTables)
            structureEvidence.Add("preserved:tables");

        if (expectation.PreserveSections && !Same(before.Sections, after.Sections))
            structureFailures.Add("Sections/page margins/header-footer references changed.");
        else if (expectation.PreserveSections)
            structureEvidence.Add("preserved:sections");

        if (expectation.PreserveOtherHeaders)
            VerifyPreservedParts(
                "header",
                before.Headers,
                after.Headers,
                expectation.ExpectedHeaderParagraphs,
                structureFailures,
                structureEvidence);

        if (expectation.PreserveOtherFooters)
            VerifyPreservedParts(
                "footer",
                before.Footers,
                after.Footers,
                expectation.ExpectedFooterParagraphs,
                structureFailures,
                structureEvidence);

        var structureResult = Result(
            StructureCriterionId,
            structureFailures.Count == 0,
            structureEvidence.Take(64),
            structureFailures.Count == 0 ? null : string.Join("; ", structureFailures.Take(20)));

        return new VerificationReport(
            VerifierId,
            [targetResult, bodyResult, structureResult],
            reportEvidenceIds:
            [
                "before-sha256:" + before.Sha256,
                "after-sha256:" + after.Sha256
            ]);
    }

    private static void VerifyExpectedPartParagraphs(
        string kind,
        IReadOnlyList<ClosedWordPartSnapshot> parts,
        IReadOnlyList<WordExpectedPartParagraph> expected,
        List<string> failures,
        List<string> evidence)
    {
        var byId = parts.ToDictionary(x => x.RelationshipId, StringComparer.Ordinal);
        foreach (var target in expected.OrderBy(x => x.RelationshipId, StringComparer.Ordinal).ThenBy(x => x.Index))
        {
            if (!byId.TryGetValue(target.RelationshipId, out var part))
            {
                failures.Add($"{kind}:{target.RelationshipId}: part missing");
                continue;
            }
            if (target.Index >= part.Paragraphs.Count)
            {
                failures.Add($"{kind}:{target.RelationshipId}:{target.Index}: paragraph missing");
                continue;
            }

            var actual = part.Paragraphs[target.Index];
            evidence.Add($"word:{kind}:{target.RelationshipId}:{target.Index}");
            if (!Same(actual, target.Expected))
                failures.Add($"{kind}:{target.RelationshipId}:{target.Index}: target state mismatch");
        }
    }

    private static void VerifyPreservedParts(
        string kind,
        IReadOnlyList<ClosedWordPartSnapshot> before,
        IReadOnlyList<ClosedWordPartSnapshot> after,
        IReadOnlyList<WordExpectedPartParagraph> expected,
        List<string> failures,
        List<string> evidence)
    {
        var expectedKeys = expected
            .Select(x => x.RelationshipId.Trim() + ":" + x.Index)
            .ToHashSet(StringComparer.Ordinal);
        var beforeMap = before.ToDictionary(x => x.RelationshipId, StringComparer.Ordinal);
        var afterMap = after.ToDictionary(x => x.RelationshipId, StringComparer.Ordinal);

        foreach (var id in beforeMap.Keys.Union(afterMap.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            beforeMap.TryGetValue(id, out var leftPart);
            afterMap.TryGetValue(id, out var rightPart);
            if (leftPart is null || rightPart is null)
            {
                failures.Add($"{kind}:{id}: part added/removed");
                continue;
            }

            var max = Math.Max(leftPart.Paragraphs.Count, rightPart.Paragraphs.Count);
            for (var index = 0; index < max; index++)
            {
                if (expectedKeys.Contains(id + ":" + index)) continue;
                var left = index < leftPart.Paragraphs.Count ? leftPart.Paragraphs[index] : null;
                var right = index < rightPart.Paragraphs.Count ? rightPart.Paragraphs[index] : null;
                if (!SameNullable(left, right))
                    failures.Add($"{kind}:{id}:{index}: changed outside target set");
                else if (left is not null)
                    evidence.Add($"preserved:{kind}:{id}:{index}");
            }
        }
    }

    private static VerificationCriterionResult Result(
        string id,
        bool passed,
        IEnumerable<string> evidence,
        string? failure)
        => passed
            ? new VerificationCriterionResult(id, VerificationCriterionStatus.Passed, evidence)
            : new VerificationCriterionResult(
                id,
                VerificationCriterionStatus.Failed,
                evidence,
                new VerificationFailure(id, failure ?? "Word verification failed.", evidence));

    private static bool Same<T>(T left, T right)
        => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    private static bool SameNullable<T>(T? left, T? right) where T : class
        => left is null ? right is null : right is not null && Same(left, right);

    private static string Describe(ClosedWordParagraphSnapshot paragraph)
        => $"text={JsonSerializer.Serialize(paragraph.Text)}, style={paragraph.StyleId ?? "<none>"}, runs={paragraph.Runs.Count}";
}

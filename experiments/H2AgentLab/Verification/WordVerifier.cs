using System.Text.Json;
using H2AgentLab.Documents;

namespace H2AgentLab.Verification;

public sealed record WordExpectedBodyParagraph(
    int Index,
    ClosedWordParagraphSnapshot Expected);

public sealed record WordExpectedPartParagraph(
    string PartKey,
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
        if (result.Any(x => x is null || x.Index < 0 || x.Expected is null || string.IsNullOrWhiteSpace(x.PartKey)))
            throw new ArgumentException("Expected Word part paragraphs contain an invalid entry.", parameterName);
        var duplicate = result
            .GroupBy(x => x.PartKey.Trim() + ":" + x.Index, StringComparer.Ordinal)
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

        var beforeHeaders = LogicalParts(before, header: true);
        var afterHeaders = LogicalParts(after, header: true);
        var beforeFooters = LogicalParts(before, header: false);
        var afterFooters = LogicalParts(after, header: false);

        VerifyExpectedPartParagraphs(
            "header",
            afterHeaders,
            expectation.ExpectedHeaderParagraphs,
            targetFailures,
            targetEvidence);
        VerifyExpectedPartParagraphs(
            "footer",
            afterFooters,
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

        if (expectation.PreserveSections && !SameSectionStructure(before.Sections, after.Sections))
            structureFailures.Add("Sections/page margins/header-footer logical slots changed.");
        else if (expectation.PreserveSections)
            structureEvidence.Add("preserved:sections");

        if (expectation.PreserveOtherHeaders)
            VerifyPreservedParts(
                "header",
                beforeHeaders,
                afterHeaders,
                expectation.ExpectedHeaderParagraphs,
                structureFailures,
                structureEvidence);

        if (expectation.PreserveOtherFooters)
            VerifyPreservedParts(
                "footer",
                beforeFooters,
                afterFooters,
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
        IReadOnlyDictionary<string, ClosedWordPartSnapshot> parts,
        IReadOnlyList<WordExpectedPartParagraph> expected,
        List<string> failures,
        List<string> evidence)
    {
        foreach (var target in expected.OrderBy(x => x.PartKey, StringComparer.Ordinal).ThenBy(x => x.Index))
        {
            if (!parts.TryGetValue(target.PartKey, out var part))
            {
                failures.Add($"{kind}:{target.PartKey}: part missing");
                continue;
            }
            if (target.Index >= part.Paragraphs.Count)
            {
                failures.Add($"{kind}:{target.PartKey}:{target.Index}: paragraph missing");
                continue;
            }

            var actual = part.Paragraphs[target.Index];
            evidence.Add($"word:{kind}:{target.PartKey}:{target.Index}");
            if (!Same(actual, target.Expected))
                failures.Add($"{kind}:{target.PartKey}:{target.Index}: target state mismatch");
        }
    }

    private static void VerifyPreservedParts(
        string kind,
        IReadOnlyDictionary<string, ClosedWordPartSnapshot> before,
        IReadOnlyDictionary<string, ClosedWordPartSnapshot> after,
        IReadOnlyList<WordExpectedPartParagraph> expected,
        List<string> failures,
        List<string> evidence)
    {
        var expectedKeys = expected
            .Select(x => x.PartKey.Trim() + ":" + x.Index)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var id in before.Keys.Union(after.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            before.TryGetValue(id, out var leftPart);
            after.TryGetValue(id, out var rightPart);
            if (leftPart is null || rightPart is null)
            {
                failures.Add($"{kind}:{id}: logical part added/removed");
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

    private static IReadOnlyDictionary<string, ClosedWordPartSnapshot> LogicalParts(
        ClosedWordSnapshot snapshot,
        bool header)
    {
        var physical = (header ? snapshot.Headers : snapshot.Footers)
            .ToDictionary(x => x.RelationshipId, StringComparer.Ordinal);
        var logical = new Dictionary<string, ClosedWordPartSnapshot>(StringComparer.Ordinal);

        foreach (var section in snapshot.Sections.OrderBy(x => x.Index))
        {
            var references = header ? section.HeaderReferences : section.FooterReferences;
            foreach (var reference in references)
            {
                if (!physical.TryGetValue(reference.RelationshipId, out var part))
                    continue;
                var key = $"section:{section.Index}:{(header ? "header" : "footer")}:{reference.Type}";
                logical[key] = part;
            }
        }
        return logical;
    }

    private static bool SameSectionStructure(
        IReadOnlyList<ClosedWordSectionSnapshot> before,
        IReadOnlyList<ClosedWordSectionSnapshot> after)
    {
        if (before.Count != after.Count) return false;
        for (var index = 0; index < before.Count; index++)
        {
            var left = before[index];
            var right = after[index];
            if (left.Index != right.Index
                || left.PageWidthTwips != right.PageWidthTwips
                || left.PageHeightTwips != right.PageHeightTwips
                || left.MarginTopTwips != right.MarginTopTwips
                || left.MarginRightTwips != right.MarginRightTwips
                || left.MarginBottomTwips != right.MarginBottomTwips
                || left.MarginLeftTwips != right.MarginLeftTwips
                || !left.HeaderReferences.Select(x => x.Type)
                    .SequenceEqual(right.HeaderReferences.Select(x => x.Type), StringComparer.Ordinal)
                || !left.FooterReferences.Select(x => x.Type)
                    .SequenceEqual(right.FooterReferences.Select(x => x.Type), StringComparer.Ordinal))
                return false;
        }
        return true;
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

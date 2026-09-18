namespace H2AgentLab.Verification;

public sealed record FileVerificationEntry
{
    public FileVerificationEntry(string path, string sha256)
    {
        Path = NormalizePath(path);
        Sha256 = NormalizeSha256(sha256);
    }

    public string Path { get; }
    public string Sha256 { get; }

    internal static string NormalizePath(string? path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        if (normalized.Length == 0
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Contains("../", StringComparison.Ordinal)
            || normalized.Contains("/..", StringComparison.Ordinal)
            || normalized.Any(char.IsControl))
            throw new ArgumentException("Verification path must be a safe relative path.", nameof(path));
        return normalized;
    }

    internal static string NormalizeSha256(string? sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        var normalized = sha256.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("SHA-256 must contain exactly 64 hexadecimal characters.", nameof(sha256));
        return normalized;
    }
}

public sealed record FileVerificationExpectation
{
    public FileVerificationExpectation(
        IEnumerable<string>? expectedChangedPaths = null,
        IReadOnlyDictionary<string, string>? expectedHashes = null,
        IEnumerable<string>? allowedOutputPaths = null)
    {
        ExpectedChangedPaths = NormalizePaths(expectedChangedPaths);
        AllowedOutputPaths = NormalizePaths(allowedOutputPaths);

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (expectedHashes is not null)
        {
            foreach (var pair in expectedHashes)
            {
                var path = FileVerificationEntry.NormalizePath(pair.Key);
                var hash = FileVerificationEntry.NormalizeSha256(pair.Value);
                if (!hashes.TryAdd(path, hash))
                    throw new ArgumentException($"Duplicate expected hash path '{path}'.", nameof(expectedHashes));
            }
        }
        ExpectedHashes = hashes;
    }

    public IReadOnlySet<string> ExpectedChangedPaths { get; }
    public IReadOnlyDictionary<string, string> ExpectedHashes { get; }
    public IReadOnlySet<string> AllowedOutputPaths { get; }

    private static IReadOnlySet<string> NormalizePaths(IEnumerable<string>? values)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values ?? Array.Empty<string>())
            set.Add(FileVerificationEntry.NormalizePath(value));
        return set;
    }
}

public static class FileScopeVerifier
{
    public const string VerifierId = "file-hash-scope";
    public const string ExactChangesCriterionId = "file-scope.exact-changes";
    public const string ExpectedHashesCriterionId = "file-scope.expected-hashes";
    public const string UnintendedOutputCriterionId = "file-scope.unintended-output";

    public static VerificationReport Verify(
        IEnumerable<FileVerificationEntry> before,
        IEnumerable<FileVerificationEntry> after,
        FileVerificationExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(expectation);

        var beforeMap = Snapshot(before, nameof(before));
        var afterMap = Snapshot(after, nameof(after));

        var changed = beforeMap.Keys.Union(afterMap.Keys, StringComparer.Ordinal)
            .Where(path =>
                !beforeMap.TryGetValue(path, out var beforeHash)
                || !afterMap.TryGetValue(path, out var afterHash)
                || !string.Equals(beforeHash, afterHash, StringComparison.Ordinal))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var exactChangesPass = changed.SequenceEqual(
            expectation.ExpectedChangedPaths.OrderBy(x => x, StringComparer.Ordinal),
            StringComparer.Ordinal);
        var exactEvidence = changed.Select(path => $"changed:{path}").ToArray();
        var exactResult = Result(
            ExactChangesCriterionId,
            exactChangesPass,
            exactEvidence,
            exactChangesPass
                ? null
                : $"Changed paths [{string.Join(", ", changed)}] did not exactly match expected [{string.Join(", ", expectation.ExpectedChangedPaths.OrderBy(x => x, StringComparer.Ordinal))}].");

        var hashFailures = new List<string>();
        var hashEvidence = new List<string>();
        foreach (var pair in expectation.ExpectedHashes.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (!afterMap.TryGetValue(pair.Key, out var actual))
            {
                hashFailures.Add($"{pair.Key}: missing");
                continue;
            }
            hashEvidence.Add($"after:{pair.Key}:sha256:{actual}");
            if (!string.Equals(actual, pair.Value, StringComparison.Ordinal))
                hashFailures.Add($"{pair.Key}: expected {pair.Value}, actual {actual}");
        }
        var hashResult = Result(
            ExpectedHashesCriterionId,
            hashFailures.Count == 0,
            hashEvidence,
            hashFailures.Count == 0 ? null : string.Join("; ", hashFailures));

        var created = afterMap.Keys.Except(beforeMap.Keys, StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        var allowedCreated = expectation.AllowedOutputPaths
            .Union(expectation.ExpectedChangedPaths, StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var unintended = created.Where(path => !allowedCreated.Contains(path)).ToArray();
        var unintendedResult = Result(
            UnintendedOutputCriterionId,
            unintended.Length == 0,
            created.Select(path => $"created:{path}"),
            unintended.Length == 0
                ? null
                : "Unexpected output path(s): " + string.Join(", ", unintended));

        return new VerificationReport(
            VerifierId,
            [exactResult, hashResult, unintendedResult],
            reportEvidenceIds: changed.Select(path => $"scope:{path}"));
    }

    private static VerificationCriterionResult Result(
        string criterionId,
        bool passed,
        IEnumerable<string> evidenceIds,
        string? failureMessage)
        => passed
            ? new VerificationCriterionResult(
                criterionId,
                VerificationCriterionStatus.Passed,
                evidenceIds)
            : new VerificationCriterionResult(
                criterionId,
                VerificationCriterionStatus.Failed,
                evidenceIds,
                new VerificationFailure(
                    criterionId,
                    failureMessage ?? "Verification failed.",
                    evidenceIds));

    private static Dictionary<string, string> Snapshot(
        IEnumerable<FileVerificationEntry> entries,
        string parameterName)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (!map.TryAdd(entry.Path, entry.Sha256))
                throw new ArgumentException($"Duplicate file snapshot path '{entry.Path}'.", parameterName);
        }
        return map;
    }
}

namespace H2AgentLab.Verification;

public enum VerificationCriterionStatus
{
    Passed = 0,
    Failed = 1,
    NotVerified = 2
}

public sealed record VerificationFailure
{
    public VerificationFailure(
        string criterionId,
        string message,
        IEnumerable<string>? evidenceIds = null)
    {
        CriterionId = NormalizeId(criterionId, nameof(criterionId));
        Message = NormalizeText(message, nameof(message), 4_000);
        EvidenceIds = NormalizeEvidence(evidenceIds);
    }

    public string CriterionId { get; }
    public string Message { get; }
    public IReadOnlyList<string> EvidenceIds { get; }

    internal static string NormalizeId(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > 128 || normalized.Any(char.IsControl))
            throw new ArgumentException("Identifier must be <=128 characters and contain no control characters.", parameterName);
        return normalized;
    }

    internal static string NormalizeText(string? value, string parameterName, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maxLength)
            throw new ArgumentException($"Text exceeds {maxLength} characters.", parameterName);
        return normalized;
    }

    internal static IReadOnlyList<string> NormalizeEvidence(IEnumerable<string>? values)
    {
        if (values is null) return Array.Empty<string>();
        var result = values
            .Select(x => x?.Trim() ?? "")
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (result.Any(x => x.Length > 256 || x.Any(char.IsControl)))
            throw new ArgumentException("Evidence IDs must be <=256 characters and contain no control characters.", nameof(values));
        return Array.AsReadOnly(result);
    }
}

public sealed record VerificationCriterionResult
{
    public VerificationCriterionResult(
        string criterionId,
        VerificationCriterionStatus status,
        IEnumerable<string>? evidenceIds = null,
        VerificationFailure? failure = null)
    {
        CriterionId = VerificationFailure.NormalizeId(criterionId, nameof(criterionId));
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (failure is not null && !string.Equals(failure.CriterionId, CriterionId, StringComparison.Ordinal))
            throw new ArgumentException("Failure criterion ID must match result criterion ID.", nameof(failure));
        if (status == VerificationCriterionStatus.Failed && failure is null)
            throw new ArgumentException("Failed criterion requires VerificationFailure.", nameof(failure));
        if (status != VerificationCriterionStatus.Failed && failure is not null)
            throw new ArgumentException("Only failed criteria may carry VerificationFailure.", nameof(failure));

        Status = status;
        EvidenceIds = VerificationFailure.NormalizeEvidence(evidenceIds);
        Failure = failure;
    }

    public string CriterionId { get; }
    public VerificationCriterionStatus Status { get; }
    public IReadOnlyList<string> EvidenceIds { get; }
    public VerificationFailure? Failure { get; }
}

public sealed record VerificationReport
{
    public VerificationReport(
        string verifierId,
        IEnumerable<VerificationCriterionResult> criteria,
        IEnumerable<string>? reportEvidenceIds = null)
    {
        VerifierId = VerificationFailure.NormalizeId(verifierId, nameof(verifierId));
        ArgumentNullException.ThrowIfNull(criteria);

        var snapshot = criteria.ToArray();
        if (snapshot.Any(x => x is null))
            throw new ArgumentException("Verification criteria cannot contain null entries.", nameof(criteria));
        var duplicate = snapshot.GroupBy(x => x.CriterionId, StringComparer.Ordinal)
            .FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Duplicate verification criterion '{duplicate.Key}'.", nameof(criteria));

        Criteria = Array.AsReadOnly(snapshot);
        ReportEvidenceIds = VerificationFailure.NormalizeEvidence(reportEvidenceIds);
    }

    public string VerifierId { get; }
    public IReadOnlyList<VerificationCriterionResult> Criteria { get; }
    public IReadOnlyList<string> ReportEvidenceIds { get; }

    public bool Passed => Criteria.Count > 0
        && Criteria.All(x => x.Status == VerificationCriterionStatus.Passed);

    public IReadOnlyList<VerificationFailure> Failures
        => Criteria.Where(x => x.Failure is not null).Select(x => x.Failure!).ToArray();

    public bool Covers(IEnumerable<string> criterionIds)
    {
        ArgumentNullException.ThrowIfNull(criterionIds);
        var available = Criteria.Select(x => x.CriterionId).ToHashSet(StringComparer.Ordinal);
        return criterionIds.All(available.Contains);
    }
}

namespace H2AgentLab.Tasking;

public enum AgentEvidenceKind
{
    SourceSnapshot = 0,
    ToolResult = 1,
    VerifierReport = 2,
    ScreenshotState = 3,
    ArtifactHash = 4,
    TestBuildResult = 5
}

/// <summary>
/// Typed durable reference to evidence used to satisfy one acceptance criterion.
/// Large/raw evidence remains in its owning store; the contract keeps only the reference.
/// </summary>
public sealed record AgentEvidenceReference
{
    public AgentEvidenceReference(
        AgentEvidenceKind kind,
        string referenceId,
        string? sha256 = null,
        string? summary = null)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), "Unknown evidence kind.");

        Kind = kind;
        ReferenceId = NormalizeRequired(referenceId, nameof(referenceId), 256);
        Sha256 = NormalizeSha256(sha256);
        Summary = NormalizeOptional(summary, nameof(summary), 2_000);
    }

    public AgentEvidenceKind Kind { get; }
    public string ReferenceId { get; }
    public string? Sha256 { get; }
    public string? Summary { get; }

    private static string NormalizeRequired(string? value, string parameterName, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maxLength || normalized.Any(char.IsControl))
            throw new ArgumentException($"Value must be at most {maxLength} characters and contain no control characters.", parameterName);
        return normalized;
    }

    private static string? NormalizeOptional(string? value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Length > maxLength)
            throw new ArgumentException($"Value must be at most {maxLength} characters.", parameterName);
        return normalized;
    }

    private static string? NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Evidence SHA-256 must contain exactly 64 hexadecimal characters.", nameof(value));
        return normalized;
    }
}

/// <summary>
/// Immutable acceptance requirement with a stable criterion ID. Evidence may be appended, but the
/// criterion identity/requirement is never rewritten by evidence recording.
/// </summary>
public sealed record AgentAcceptanceCriterion
{
    public AgentAcceptanceCriterion(
        string criterionId,
        string requirement,
        IEnumerable<AgentEvidenceReference>? evidence = null)
    {
        CriterionId = NormalizeId(criterionId);
        Requirement = NormalizeRequirement(requirement);
        Evidence = Array.AsReadOnly((evidence ?? Array.Empty<AgentEvidenceReference>())
            .Where(x => x is not null)
            .Distinct()
            .ToArray());
    }

    public string CriterionId { get; }
    public string Requirement { get; }
    public IReadOnlyList<AgentEvidenceReference> Evidence { get; }

    public AgentAcceptanceCriterion WithEvidence(AgentEvidenceReference evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (Evidence.Contains(evidence)) return this;
        return new AgentAcceptanceCriterion(CriterionId, Requirement, Evidence.Append(evidence));
    }

    private static string NormalizeId(string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.Length > 128 || normalized.Any(char.IsControl))
            throw new ArgumentException("Criterion ID must be at most 128 characters and contain no control characters.", nameof(value));
        return normalized;
    }

    private static string NormalizeRequirement(string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.Length > 8_000)
            throw new ArgumentException("Acceptance requirement exceeds 8,000 characters.", nameof(value));
        return normalized;
    }
}

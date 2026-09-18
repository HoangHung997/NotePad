using H2AgentLab.Tasking;

namespace H2AgentLab.Verification;

public sealed record ArtifactVerificationTarget
{
    public ArtifactVerificationTarget(
        string artifactId,
        string mediaType,
        string? sha256 = null,
        string? sourceReference = null)
    {
        ArtifactId = VerificationFailure.NormalizeId(artifactId, nameof(artifactId));
        MediaType = VerificationFailure.NormalizeText(mediaType, nameof(mediaType), 256);
        Sha256 = NormalizeSha256(sha256);
        SourceReference = NormalizeOptional(sourceReference, nameof(sourceReference), 512);
    }

    public string ArtifactId { get; }
    public string MediaType { get; }
    public string? Sha256 { get; }
    public string? SourceReference { get; }

    private static string? NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Artifact SHA-256 must contain exactly 64 hexadecimal characters.", nameof(value));
        return normalized;
    }

    private static string? NormalizeOptional(string? value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Length > maxLength || normalized.Any(char.IsControl))
            throw new ArgumentException($"Value must be <={maxLength} characters and contain no control characters.", parameterName);
        return normalized;
    }
}

public sealed record ArtifactVerificationRequest
{
    public ArtifactVerificationRequest(
        AgentTaskContract contract,
        ArtifactVerificationTarget target,
        IEnumerable<string>? criterionIds = null)
    {
        Contract = contract ?? throw new ArgumentNullException(nameof(contract));
        Target = target ?? throw new ArgumentNullException(nameof(target));

        var requested = (criterionIds ?? contract.AcceptanceCriteria.Select(x => x.CriterionId))
            .Select(x => VerificationFailure.NormalizeId(x, nameof(criterionIds)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var known = contract.AcceptanceCriteria.Select(x => x.CriterionId).ToHashSet(StringComparer.Ordinal);
        var unknown = requested.Where(x => !known.Contains(x)).ToArray();
        if (unknown.Length > 0)
            throw new ArgumentException(
                "Artifact verification requested unknown criterion(s): " + string.Join(", ", unknown),
                nameof(criterionIds));

        CriterionIds = Array.AsReadOnly(requested);
    }

    public AgentTaskContract Contract { get; }
    public ArtifactVerificationTarget Target { get; }
    public IReadOnlyList<string> CriterionIds { get; }
}

public interface IArtifactVerifier
{
    string VerifierId { get; }

    bool CanVerify(ArtifactVerificationTarget target);

    ValueTask<VerificationReport> VerifyAsync(
        ArtifactVerificationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Provider/domain-neutral dispatcher. The orchestrator can depend on this abstraction without
/// referencing Excel, Word, PDF, Desktop or any future verifier implementation.
/// </summary>
public sealed class ArtifactVerifierRegistry
{
    private readonly Dictionary<string, IArtifactVerifier> _verifiers = new(StringComparer.Ordinal);

    public IReadOnlyList<string> VerifierIds
        => _verifiers.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    public void Register(IArtifactVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        var id = VerificationFailure.NormalizeId(verifier.VerifierId, nameof(verifier));
        if (!_verifiers.TryAdd(id, verifier))
            throw new InvalidOperationException($"Artifact verifier '{id}' is already registered.");
    }

    public IReadOnlyList<IArtifactVerifier> Resolve(ArtifactVerificationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return _verifiers.Values
            .Where(x => x.CanVerify(target))
            .OrderBy(x => x.VerifierId, StringComparer.Ordinal)
            .ToArray();
    }
}

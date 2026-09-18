using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using H2AgentLab.Verification;

namespace H2AgentLab.Web;

public enum WebSourceType
{
    Official = 0,
    Primary = 1,
    Secondary = 2,
    SearchResult = 3,
    Unknown = 4
}

public sealed record WebEvidence(
    string EvidenceId,
    string Url,
    string Title,
    string Publisher,
    DateTime? PublishedAt,
    DateTime? EffectiveAt,
    DateTime FetchedAt,
    WebSourceType SourceType,
    string ContentHash,
    string RelevantExcerpt,
    string? ArtifactHandle = null);

public sealed class WebEvidenceStore
{
    public const int MaxInlineExcerptCharacters = 2_000;
    public const int MaxBodyBytes = 32 * 1024 * 1024;
    private readonly string _root;

    public WebEvidenceStore(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        _root = Path.Combine(Path.GetFullPath(stateRoot), "web-evidence");
        Directory.CreateDirectory(_root);
    }

    public WebEvidence Store(
        WebFetchedDocument document,
        WebSourceType sourceType,
        string relevantExcerpt)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Url);
        if (document.Bytes.Length > MaxBodyBytes)
            throw new IOException("Web evidence body exceeds 32 MB.");

        var sha = global::H2AgentLab.SafeWorkspace.Hash(document.Bytes).ToLowerInvariant();
        var id = "evidence:web:" + Guid.NewGuid().ToString("N");
        var artifactId = "webbody-" + Guid.NewGuid().ToString("N");
        var bodyPath = Path.Combine(_root, artifactId + ".bin");
        WriteAtomic(bodyPath, document.Bytes);

        var excerpt = NormalizeExcerpt(relevantExcerpt);
        var evidence = new WebEvidence(
            id,
            document.Url.Trim(),
            Bound(document.Title ?? "", 500),
            Bound(document.Publisher ?? "", 300),
            document.PublishedAt?.ToUniversalTime(),
            document.EffectiveAt?.ToUniversalTime(),
            DateTime.UtcNow,
            sourceType,
            sha,
            excerpt,
            artifactId);
        WriteAtomic(
            Path.Combine(_root, id.Replace(':', '_') + ".json"),
            JsonSerializer.SerializeToUtf8Bytes(evidence));
        return evidence;
    }

    public byte[] ReadBody(WebEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (string.IsNullOrWhiteSpace(evidence.ArtifactHandle))
            throw new InvalidOperationException("Web evidence has no body artifact.");
        var path = Path.Combine(_root, evidence.ArtifactHandle + ".bin");
        if (!File.Exists(path))
            throw new FileNotFoundException("Web evidence body is missing.", evidence.ArtifactHandle);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > MaxBodyBytes)
            throw new IOException("Stored web body exceeds safety limit.");
        var hash = global::H2AgentLab.SafeWorkspace.Hash(bytes).ToLowerInvariant();
        if (!string.Equals(hash, evidence.ContentHash, StringComparison.Ordinal))
            throw new IOException("Stored web body hash no longer matches evidence.");
        return bytes;
    }

    public static string ContextProjection(WebEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var projection =
            $"[{evidence.EvidenceId}] {evidence.Title} · {evidence.Publisher}\n"
            + $"URL: {evidence.Url}\nFetched: {evidence.FetchedAt:O}\n"
            + $"Excerpt: {evidence.RelevantExcerpt}\n"
            + $"sha256={evidence.ContentHash}; artifact={evidence.ArtifactHandle}";
        return projection.Length <= 3_000
            ? projection
            : projection[..3_000];
    }

    private static string NormalizeExcerpt(string value)
    {
        value ??= "";
        value = System.Text.RegularExpressions.Regex.Replace(value, "\s+", " ").Trim();
        return value.Length <= MaxInlineExcerptCharacters
            ? value
            : value[..MaxInlineExcerptCharacters] + "…";
    }

    private static string Bound(string value, int max)
    {
        value = value.Replace((char)13, ' ').Replace((char)10, ' ').Trim();
        return value.Length <= max ? value : value[..max];
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}

public enum FreshnessRequirementKind
{
    None = 0,
    CurrentWeb = 1,
    CurrentAuthoritative = 2
}

public sealed record FreshnessRequirement(
    FreshnessRequirementKind Kind,
    string Reason,
    TimeSpan MaxEvidenceAge);

public static class FreshnessPolicy
{
    private static readonly string[] CurrentTerms =
    [
        "today", "current", "currently", "latest", "newest", "this week", "this month",
        "hôm nay", "hiện tại", "mới nhất", "gần đây", "giá hiện tại", "thời tiết", "tin mới",
        "current price", "weather", "news", "current documentation"
    ];

    private static readonly string[] LegalCurrentTerms =
    [
        "still effective", "currently valid", "replaced", "amended", "repealed", "superseded",
        "còn hiệu lực", "thay thế", "sửa đổi", "bổ sung", "bãi bỏ", "hết hiệu lực",
        "new law", "new regulation", "văn bản mới", "luật mới", "nghị định mới"
    ];

    public static FreshnessRequirement Infer(string intent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intent);
        if (LegalCurrentTerms.Any(x => intent.Contains(x, StringComparison.OrdinalIgnoreCase)))
            return new(
                FreshnessRequirementKind.CurrentAuthoritative,
                "Intent asks for current legal/regulatory/effective status.",
                TimeSpan.FromDays(7));
        if (CurrentTerms.Any(x => intent.Contains(x, StringComparison.OrdinalIgnoreCase)))
            return new(
                FreshnessRequirementKind.CurrentWeb,
                "Intent contains current/latest/time-sensitive language.",
                TimeSpan.FromDays(2));
        return new(
            FreshnessRequirementKind.None,
            "No explicit freshness-sensitive intent was detected.",
            TimeSpan.MaxValue);
    }
}

public static class FreshnessCompletionGate
{
    public static void EnsureSatisfied(
        FreshnessRequirement requirement,
        IReadOnlyList<WebEvidence> evidence,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(evidence);
        utcNow = utcNow.ToUniversalTime();

        if (requirement.Kind == FreshnessRequirementKind.None)
            return;

        var acceptable = evidence.Where(x =>
            x.FetchedAt.Kind == DateTimeKind.Utc
            && utcNow >= x.FetchedAt
            && utcNow - x.FetchedAt <= requirement.MaxEvidenceAge);

        if (requirement.Kind == FreshnessRequirementKind.CurrentAuthoritative)
            acceptable = acceptable.Where(x =>
                x.SourceType is WebSourceType.Official or WebSourceType.Primary);

        if (!acceptable.Any())
            throw new InvalidOperationException(
                "Freshness-required task cannot complete without sufficiently current approved web evidence.");
    }
}

public enum LegalRelationType
{
    REPLACED = 0,
    AMENDED = 1,
    SUPPLEMENTED = 2,
    PARTIALLY_REPEALED = 3,
    REPEALED = 4
}

public enum LegalDocumentEffectiveStatus
{
    REPLACED = 0,
    AMENDED = 1,
    SUPPLEMENTED = 2,
    PARTIALLY_REPEALED = 3,
    REPEALED = 4,
    STILL_EFFECTIVE = 5,
    NOT_YET_EFFECTIVE = 6,
    UNKNOWN = 7
}

public sealed record LegalDocumentRelationship(
    LegalRelationType RelationType,
    string RelatedDocumentId,
    DateTime? EffectiveDate,
    IReadOnlyList<string> EvidenceIds);

public sealed record LegalDocumentStatus(
    string DocumentId,
    string Jurisdiction,
    string Issuer,
    DateTime? IssueDate,
    DateTime? EffectiveDate,
    LegalDocumentEffectiveStatus Status,
    IReadOnlyList<LegalDocumentRelationship> Relationships,
    IReadOnlyList<string> EvidenceIds);

public static class LegalStatusVerifier
{
    private static readonly LegalDocumentEffectiveStatus[] EffectClaims =
    [
        LegalDocumentEffectiveStatus.REPLACED,
        LegalDocumentEffectiveStatus.AMENDED,
        LegalDocumentEffectiveStatus.SUPPLEMENTED,
        LegalDocumentEffectiveStatus.PARTIALLY_REPEALED,
        LegalDocumentEffectiveStatus.REPEALED,
        LegalDocumentEffectiveStatus.STILL_EFFECTIVE,
        LegalDocumentEffectiveStatus.NOT_YET_EFFECTIVE
    ];

    public static VerificationReport Verify(
        LegalDocumentStatus status,
        IReadOnlyList<WebEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(evidence);
        var byId = evidence.ToDictionary(x => x.EvidenceId, StringComparer.Ordinal);
        var failures = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(status.DocumentId)
            || string.IsNullOrWhiteSpace(status.Jurisdiction)
            || string.IsNullOrWhiteSpace(status.Issuer))
            failures.Add("Legal document identity/jurisdiction/issuer is incomplete.");

        foreach (var id in status.EvidenceIds)
        {
            if (!byId.ContainsKey(id)) failures.Add("Unknown legal evidence ID: " + id);
            else used.Add(id);
        }

        foreach (var relationship in status.Relationships)
        {
            if (string.IsNullOrWhiteSpace(relationship.RelatedDocumentId))
                failures.Add("Legal relationship is missing related document ID.");
            if (relationship.EvidenceIds.Count == 0)
                failures.Add("Legal relationship has no evidence IDs.");
            foreach (var id in relationship.EvidenceIds)
            {
                if (!byId.ContainsKey(id))
                    failures.Add("Unknown relationship evidence ID: " + id);
                else
                    used.Add(id);
            }
        }

        if (EffectClaims.Contains(status.Status))
        {
            if (used.Count == 0)
                failures.Add("Legal effect/status claim has no evidence.");
            if (!used.Select(id => byId[id]).Any(x =>
                    x.SourceType is WebSourceType.Official or WebSourceType.Primary))
                failures.Add("Legal effect/status claim lacks authoritative/primary evidence.");
        }

        if (status.Status is
                LegalDocumentEffectiveStatus.REPLACED
                or LegalDocumentEffectiveStatus.AMENDED
                or LegalDocumentEffectiveStatus.SUPPLEMENTED
                or LegalDocumentEffectiveStatus.PARTIALLY_REPEALED
                or LegalDocumentEffectiveStatus.REPEALED
            && status.Relationships.Count == 0)
            failures.Add("A newer document alone is insufficient: typed legal relationship evidence is required.");

        var result = failures.Count == 0
            ? new VerificationCriterionResult(
                "legal.status",
                VerificationCriterionStatus.Passed,
                used)
            : new VerificationCriterionResult(
                "legal.status",
                VerificationCriterionStatus.Failed,
                used,
                new VerificationFailure(
                    "legal.status",
                    string.Join("; ", failures),
                    used));

        return new VerificationReport(
            "legal-status",
            [result],
            used);
    }
}

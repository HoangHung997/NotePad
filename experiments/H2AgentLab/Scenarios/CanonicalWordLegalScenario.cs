using H2AgentLab.Office;
using H2AgentLab.Web;
using H2AgentLab.Verification;

namespace H2AgentLab.Scenarios;

public sealed record CanonicalWordState(
    string SessionId,
    bool Saved,
    string Text,
    string StateToken,
    string PreservationToken);

public sealed record CanonicalWordPatch(
    string ExpectedStateToken,
    IReadOnlyList<WordSpellingCandidate> SpellingFixes,
    IReadOnlyList<CanonicalLegalReplacement> LegalReplacements);

public sealed record CanonicalLegalReplacement(
    string OriginalCitation,
    string ReplacementText,
    IReadOnlyList<string> EvidenceIds);

public sealed record CanonicalWordPatchResult(
    CanonicalWordState Before,
    CanonicalWordState After,
    IReadOnlyList<string> ChangedEvidenceIds);

public interface ICanonicalWordSession
{
    int DesktopCallCount { get; }
    Task<CanonicalWordState> ReadActiveAsync(CancellationToken cancellationToken);
    Task<CanonicalWordPatchResult> ApplyPatchAsync(
        CanonicalWordPatch patch,
        CancellationToken cancellationToken);
    Task<bool> VerifyPreservationAsync(
        CanonicalWordState before,
        CanonicalWordState after,
        CancellationToken cancellationToken);
}

public interface ICanonicalLegalStatusResolver
{
    Task<LegalDocumentStatus> ResolveAsync(
        WordLegalCitation citation,
        IReadOnlyList<WebEvidence> evidence,
        CancellationToken cancellationToken);
}

public sealed record CanonicalWordLegalScenarioResult(
    CanonicalWordState Before,
    CanonicalWordState After,
    IReadOnlyList<WordSpellingCandidate> SpellingCandidates,
    IReadOnlyList<WordLegalCitation> LegalCitations,
    IReadOnlyList<LegalDocumentStatus> LegalStatuses,
    IReadOnlyList<WebEvidence> WebEvidence,
    bool PreservationVerified,
    int RepairCount,
    int DesktopPixelCalls,
    IReadOnlyList<string> EvidenceIds);

public sealed class CanonicalWordLegalScenario
{
    private readonly ICanonicalWordSession _word;
    private readonly IWordLanguageEvidenceProvider _language;
    private readonly WebResearchHost _web;
    private readonly WebEvidenceStore _webEvidence;
    private readonly ICanonicalLegalStatusResolver _legal;

    public CanonicalWordLegalScenario(
        ICanonicalWordSession word,
        IWordLanguageEvidenceProvider language,
        WebResearchHost web,
        WebEvidenceStore webEvidence,
        ICanonicalLegalStatusResolver legal)
    {
        _word = word ?? throw new ArgumentNullException(nameof(word));
        _language = language ?? throw new ArgumentNullException(nameof(language));
        _web = web ?? throw new ArgumentNullException(nameof(web));
        _webEvidence = webEvidence ?? throw new ArgumentNullException(nameof(webEvidence));
        _legal = legal ?? throw new ArgumentNullException(nameof(legal));
    }

    public async Task<CanonicalWordLegalScenarioResult> RunAsync(
        CancellationToken cancellationToken)
    {
        if (_web.Health.Status != Providers.ProviderHealthStatus.Ready)
            await _web.ConnectAsync(cancellationToken).ConfigureAwait(false);

        var before = await _word.ReadActiveAsync(cancellationToken).ConfigureAwait(false);
        if (before.Saved)
            throw new InvalidOperationException(
                "Canonical acceptance fixture must preserve and observe an unsaved Word state.");

        var spelling = await _language.GetSpellingErrorsAsync(
            before.Text,
            cancellationToken).ConfigureAwait(false);
        var citations = await _language.ExtractLegalCitationsAsync(
            before.Text,
            cancellationToken).ConfigureAwait(false);

        var freshness = FreshnessPolicy.Infer(
            "Check whether the legal documents are still effective, replaced, amended or supplemented with current information.");
        if (freshness.Kind != FreshnessRequirementKind.CurrentAuthoritative)
            throw new InvalidOperationException("Canonical legal scenario did not require authoritative freshness.");

        var allEvidence = new List<WebEvidence>();
        var statuses = new List<LegalDocumentStatus>();
        foreach (var citation in citations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hits = await _web.SearchAsync(
                citation.DocumentId + " hiệu lực thay thế sửa đổi bổ sung",
                8,
                cancellationToken).ConfigureAwait(false);
            if (hits.Count == 0)
                throw new InvalidOperationException(
                    "Current web research returned no source for legal citation " + citation.DocumentId + ".");

            // Prefer an official-looking result first; resolver still verifies exact evidence status.
            var ordered = hits
                .OrderByDescending(hit => LooksOfficial(hit.Url, hit.Publisher))
                .ThenByDescending(hit => hit.PublishedAt)
                .ToArray();

            var citationEvidence = new List<WebEvidence>();
            foreach (var hit in ordered.Take(3))
            {
                var fetched = await _web.FetchAsync(hit.Url, cancellationToken).ConfigureAwait(false);
                var excerpt = WebResearchHost.ExtractText(
                    fetched.ContentType,
                    fetched.Bytes,
                    WebEvidenceStore.MaxInlineExcerptCharacters);
                var sourceType = LooksOfficial(hit.Url, hit.Publisher)
                    ? WebSourceType.Official
                    : WebSourceType.Secondary;
                citationEvidence.Add(_webEvidence.Store(
                    fetched with
                    {
                        Title = string.IsNullOrWhiteSpace(fetched.Title) ? hit.Title : fetched.Title,
                        Publisher = string.IsNullOrWhiteSpace(fetched.Publisher) ? hit.Publisher : fetched.Publisher,
                        PublishedAt = fetched.PublishedAt ?? hit.PublishedAt
                    },
                    sourceType,
                    excerpt.Length > 0 ? excerpt : hit.Snippet));
            }

            FreshnessCompletionGate.EnsureSatisfied(
                freshness,
                citationEvidence,
                DateTime.UtcNow);
            var status = await _legal.ResolveAsync(
                citation,
                citationEvidence,
                cancellationToken).ConfigureAwait(false);
            var legalReport = LegalStatusVerifier.Verify(status, citationEvidence);
            if (!legalReport.Passed)
                throw new InvalidOperationException(
                    "Legal relationship/status could not be verified: "
                    + string.Join("; ", legalReport.Failures.Select(x => x.Message)));

            allEvidence.AddRange(citationEvidence);
            statuses.Add(status);
        }

        var replacements = statuses
            .SelectMany(status => ReplacementFor(status, citations))
            .ToArray();
        var patch = new CanonicalWordPatch(
            before.StateToken,
            spelling,
            replacements);

        var patched = await _word.ApplyPatchAsync(
            patch,
            cancellationToken).ConfigureAwait(false);
        var reread = await _word.ReadActiveAsync(cancellationToken).ConfigureAwait(false);

        if (!string.Equals(
                patched.After.StateToken,
                reread.StateToken,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Canonical Word mutation was not re-observed from the live structured provider.");

        var preservation = await _word.VerifyPreservationAsync(
            before,
            reread,
            cancellationToken).ConfigureAwait(false);
        if (!preservation)
            throw new InvalidOperationException(
                "Canonical Word preservation verification failed.");

        if (_word.DesktopCallCount != 0)
            throw new InvalidOperationException(
                "Canonical Word + legal scenario used Desktop pixel/computer-use despite sufficient structured providers.");

        var ids = spelling.Select(x => x.EvidenceId)
            .Concat(citations.Select(x => x.EvidenceId))
            .Concat(allEvidence.Select(x => x.EvidenceId))
            .Concat(patched.ChangedEvidenceIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new CanonicalWordLegalScenarioResult(
            before,
            reread,
            spelling,
            citations,
            statuses,
            allEvidence,
            preservation,
            RepairCount: 0,
            DesktopPixelCalls: _word.DesktopCallCount,
            EvidenceIds: ids);
    }

    private static IEnumerable<CanonicalLegalReplacement> ReplacementFor(
        LegalDocumentStatus status,
        IReadOnlyList<WordLegalCitation> citations)
    {
        var citation = citations.FirstOrDefault(x =>
            string.Equals(x.DocumentId, status.DocumentId, StringComparison.OrdinalIgnoreCase));
        if (citation is null)
            yield break;

        if (status.Status is LegalDocumentEffectiveStatus.UNKNOWN
            or LegalDocumentEffectiveStatus.STILL_EFFECTIVE)
            yield break;

        var relationships = status.Relationships
            .Select(x => x.RelationType + " by " + x.RelatedDocumentId)
            .ToArray();
        yield return new CanonicalLegalReplacement(
            citation.CitationText,
            citation.CitationText + " [" + string.Join("; ", relationships) + "]",
            status.EvidenceIds
                .Concat(status.Relationships.SelectMany(x => x.EvidenceIds))
                .Distinct(StringComparer.Ordinal)
                .ToArray());
    }

    private static bool LooksOfficial(string url, string publisher)
    {
        if (publisher.Contains("Chính phủ", StringComparison.OrdinalIgnoreCase)
            || publisher.Contains("Quốc hội", StringComparison.OrdinalIgnoreCase)
            || publisher.Contains("Bộ ", StringComparison.OrdinalIgnoreCase)
            || publisher.Contains("UBND", StringComparison.OrdinalIgnoreCase))
            return true;

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Host.EndsWith(".gov.vn", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".chinhphu.vn", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".quochoi.vn", StringComparison.OrdinalIgnoreCase));
    }
}

using System.Text;
using H2AgentLab.Verification;

namespace H2AgentLab.Web;

public static class MbWebFreshnessLegalAcceptanceTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-111 test directory.");
        Directory.CreateDirectory(root);

        var lines = new List<string>();
        var failed = 0;

        async Task Test(string name, Func<Task> action)
        {
            try
            {
                await action();
                lines.Add("PASS " + name);
            }
            catch (Exception ex)
            {
                failed++;
                lines.Add("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        await Test("MB-111 current lookup is required for freshness-sensitive and legal-current intent", async () =>
        {
            var current = FreshnessPolicy.Infer("latest current documentation today");
            var legal = FreshnessPolicy.Infer(
                "văn bản này còn hiệu lực hay đã thay thế, sửa đổi, bổ sung?");
            Check(current.Kind == FreshnessRequirementKind.CurrentWeb,
                "Current/latest intent did not require current web evidence.");
            Check(legal.Kind == FreshnessRequirementKind.CurrentAuthoritative,
                "Current legal/effective-status intent did not require authoritative current evidence.");

            ExpectFreshnessFailure(current, []);
            ExpectFreshnessFailure(legal, []);

            var backend = new FixtureBackend();
            await using var host = new WebResearchHost(backend);
            await host.ConnectAsync(CancellationToken.None);
            var hits = await host.SearchAsync(
                "Nghị định 100/2025/NĐ-CP hiệu lực thay thế sửa đổi",
                5,
                CancellationToken.None);
            Check(hits.Count == 2 && backend.SearchCalls == 1,
                "Structured current lookup was not performed exactly once.");

            var fetched = await host.FetchAsync(hits[0].Url, CancellationToken.None);
            var store = new WebEvidenceStore(Path.Combine(root, "freshness-evidence"));
            var evidence = store.Store(
                fetched,
                WebSourceType.Official,
                "Official status page states the document remains effective.");
            FreshnessCompletionGate.EnsureSatisfied(
                legal,
                [evidence],
                DateTime.UtcNow.AddMinutes(1));
        });

        await Test("MB-111 legal effect requires authoritative or primary evidence", () =>
        {
            var secondary = Evidence(
                "secondary",
                WebSourceType.Secondary,
                "https://news.example.test/legal-analysis");
            var official = Evidence(
                "official",
                WebSourceType.Official,
                "https://vbpl.gov.vn/fixture");

            var status = new LegalDocumentStatus(
                "22/2023/QH15",
                "VN",
                "Quốc hội",
                new DateTime(2023, 6, 23),
                new DateTime(2024, 1, 1),
                LegalDocumentEffectiveStatus.STILL_EFFECTIVE,
                [],
                [secondary.EvidenceId]);

            var secondaryOnly = LegalStatusVerifier.Verify(status, [secondary]);
            Check(!secondaryOnly.Passed
                  && secondaryOnly.Failures.Any(x =>
                      x.Message.Contains("authoritative", StringComparison.OrdinalIgnoreCase)
                      || x.Message.Contains("primary", StringComparison.OrdinalIgnoreCase)),
                "Secondary-only legal effect claim incorrectly passed.");

            var authoritative = status with
            {
                EvidenceIds = [official.EvidenceId]
            };
            var officialReport = LegalStatusVerifier.Verify(
                authoritative,
                [official]);
            Check(officialReport.Passed,
                "Official legal evidence did not satisfy authoritative status verification.");
            return Task.CompletedTask;
        });

        await Test("MB-111 web evidence body stays durable while context projection is bounded", () =>
        {
            var state = Path.Combine(root, "bounded-web-evidence");
            var store = new WebEvidenceStore(state);
            var bodyText = string.Join(
                " ",
                Enumerable.Range(0, 12_000).Select(i => "legal-body-" + i));
            var bytes = Encoding.UTF8.GetBytes(bodyText);
            var excerpt = string.Join(
                " ",
                Enumerable.Repeat(
                    "This is a deliberately long legal excerpt used to prove bounded projection.",
                    200));

            var evidence = store.Store(
                new WebFetchedDocument(
                    "https://official.example.test/document",
                    "text/html",
                    bytes,
                    "Official document",
                    "Official Publisher",
                    DateTime.UtcNow.AddDays(-1),
                    DateTime.UtcNow.AddDays(-1)),
                WebSourceType.Official,
                excerpt);

            Check(evidence.RelevantExcerpt.Length
                    <= WebEvidenceStore.MaxInlineExcerptCharacters + 1,
                "Inline web excerpt exceeded bounded projection.");
            var projection = WebEvidenceStore.ContextProjection(evidence);
            Check(projection.Length <= 3_000
                  && projection.Contains(evidence.ContentHash, StringComparison.Ordinal)
                  && projection.Contains(evidence.ArtifactHandle!, StringComparison.Ordinal),
                "Bounded context projection lost evidence hash/artifact handle.");
            Check(store.ReadBody(evidence).SequenceEqual(bytes),
                "Durable web evidence body did not round-trip exactly.");
            return Task.CompletedTask;
        });

        await Test("MB-111 legal replacement is accepted only with typed relationship evidence", () =>
        {
            var official = Evidence(
                "replacement",
                WebSourceType.Primary,
                "https://official.example.test/replacement");
            var relationship = new LegalDocumentRelationship(
                LegalRelationType.REPLACED,
                "100/2026/NĐ-CP",
                new DateTime(2026, 10, 1),
                [official.EvidenceId]);
            var status = new LegalDocumentStatus(
                "50/2024/NĐ-CP",
                "VN",
                "Chính phủ",
                new DateTime(2024, 5, 1),
                new DateTime(2024, 6, 1),
                LegalDocumentEffectiveStatus.REPLACED,
                [relationship],
                [official.EvidenceId]);

            var report = LegalStatusVerifier.Verify(status, [official]);
            Check(report.Passed,
                "Typed REPLACED relationship with primary evidence did not verify.");
            Check(status.Relationships.Single().RelationType
                    == LegalRelationType.REPLACED
                  && status.Relationships.Single().RelatedDocumentId
                    == "100/2026/NĐ-CP",
                "Legal relationship identity/type was not preserved.");
            return Task.CompletedTask;
        });

        await Test("MB-111 newer document alone never implies replacement", () =>
        {
            var newerOfficial = Evidence(
                "newer",
                WebSourceType.Official,
                "https://official.example.test/newer");
            var unsupportedReplacement = new LegalDocumentStatus(
                "50/2024/NĐ-CP",
                "VN",
                "Chính phủ",
                new DateTime(2024, 5, 1),
                new DateTime(2024, 6, 1),
                LegalDocumentEffectiveStatus.REPLACED,
                Relationships: [],
                EvidenceIds: [newerOfficial.EvidenceId]);

            var rejected = LegalStatusVerifier.Verify(
                unsupportedReplacement,
                [newerOfficial]);
            Check(!rejected.Passed
                  && rejected.Failures.Any(x =>
                      x.Message.Contains(
                          "newer document alone",
                          StringComparison.OrdinalIgnoreCase)
                      || x.Message.Contains(
                          "typed legal relationship",
                          StringComparison.OrdinalIgnoreCase)),
                "A newer official document was incorrectly treated as proof of replacement.");

            var unknown = unsupportedReplacement with
            {
                Status = LegalDocumentEffectiveStatus.UNKNOWN
            };
            var unknownReport = LegalStatusVerifier.Verify(
                unknown,
                [newerOfficial]);
            Check(unknownReport.Passed,
                "UNKNOWN status should remain permissible when no typed legal relationship has been established.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-web-freshness-legal-acceptance-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static void ExpectFreshnessFailure(
        FreshnessRequirement requirement,
        IReadOnlyList<WebEvidence> evidence)
    {
        try
        {
            FreshnessCompletionGate.EnsureSatisfied(
                requirement,
                evidence,
                DateTime.UtcNow);
            throw new InvalidOperationException(
                "Freshness gate unexpectedly accepted missing evidence.");
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains(
                "Freshness-required task",
                StringComparison.Ordinal))
        {
        }
    }

    private static WebEvidence Evidence(
        string id,
        WebSourceType sourceType,
        string url)
        => new(
            "evidence:web:" + id,
            url,
            "Fixture legal source",
            sourceType is WebSourceType.Official or WebSourceType.Primary
                ? "Official Authority"
                : "Secondary Publisher",
            DateTime.UtcNow.AddDays(-1),
            DateTime.UtcNow.AddDays(-1),
            DateTime.UtcNow.AddMinutes(-5),
            sourceType,
            new string('a', 64),
            "Fixture evidence excerpt.",
            "artifact-" + id);

    private sealed class FixtureBackend : IWebResearchBackend
    {
        public int SearchCalls { get; private set; }

        public Task<IReadOnlyList<WebSearchHit>> SearchAsync(
            string query,
            int maxResults,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SearchCalls++;
            return Task.FromResult<IReadOnlyList<WebSearchHit>>(
            [
                new WebSearchHit(
                    "https://vbpl.gov.vn/fixture-document",
                    "Official legal status",
                    "Cơ sở dữ liệu quốc gia về pháp luật",
                    "Official status fixture.",
                    DateTime.UtcNow.AddHours(-2)),
                new WebSearchHit(
                    "https://news.example.test/legal",
                    "Secondary legal commentary",
                    "Fixture News",
                    "Secondary commentary fixture.",
                    DateTime.UtcNow.AddHours(-1))
            ]);
        }

        public Task<WebFetchedDocument> FetchAsync(
            string url,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new WebFetchedDocument(
                url,
                "text/html",
                Encoding.UTF8.GetBytes(
                    "<html><body>Official status: the document remains effective.</body></html>"),
                "Official legal status",
                "Cơ sở dữ liệu quốc gia về pháp luật",
                DateTime.UtcNow.AddHours(-2),
                DateTime.UtcNow.AddHours(-2)));
        }

        public Task<string> OpenBrowserFallbackAsync(
            string url,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(url);
        }
    }
}

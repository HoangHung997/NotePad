using System.Text;
using System.Text.Json;
using H2AgentLab.Cad;
using H2AgentLab.Context;
using H2AgentLab.Office;
using H2AgentLab.Scenarios;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2AgentLab.Verification;
using H2AgentLab.Web;

namespace H2AgentLab.Phase11;

public static class V2Phase11Tests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new Phase 11 test directory.");
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

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        await Test("1101 Lab UI send path is routed through AgentOrchestratedRun with no legacy runner seam", () =>
        {
            var repo = FindRepoRoot();
            var source = File.ReadAllText(Path.Combine(repo, "experiments", "H2AgentLab", "LabWindow.cs"));
            Check(source.Contains("new AgentOrchestratedRun()", StringComparison.Ordinal),
                "Lab UI does not construct AgentOrchestratedRun.");
            Check(source.Contains("orchestrated.RunAsync", StringComparison.Ordinal),
                "Lab UI send path does not invoke AgentOrchestratedRun.");
            Check(!source.Contains("AgentRunner", StringComparison.Ordinal)
                    && !source.Contains("CreateCompatibilityRunner", StringComparison.Ordinal),
                "Lab UI still references legacy runner execution.");

            var orchestratorSource = File.ReadAllText(Path.Combine(repo, "experiments", "H2AgentLab", "Tasking", "AgentOrchestrator.cs"));
            Check(orchestratorSource.Contains("RunRuntimeAsync", StringComparison.Ordinal)
                    && !orchestratorSource.Contains("AgentRunner", StringComparison.Ordinal)
                    && !orchestratorSource.Contains("CreateCompatibilityRunner", StringComparison.Ordinal),
                "Production orchestrator still exposes a legacy runner seam.");
            return Task.CompletedTask;
        });

        await Test("1102 typed UI trace events expose phase tool verifier evidence progress without chain-of-thought", () =>
        {
            var trace = new AgentProgressEventStream();
            trace.Add(AgentProgressEventKind.Phase, "grounded", "Scope grounded.");
            trace.Add(AgentProgressEventKind.Tool, "tool", "web.search completed.");
            trace.Add(AgentProgressEventKind.Verification, "verify", "Criterion C1 passed.");
            trace.Add(AgentProgressEventKind.Evidence, "evidence", "evidence:web:1");
            Check(trace.Events.Select(x => x.Kind).SequenceEqual(new[]
            {
                AgentProgressEventKind.Phase,
                AgentProgressEventKind.Tool,
                AgentProgressEventKind.Verification,
                AgentProgressEventKind.Evidence
            }), "Typed trace event order/kinds are wrong.");
            Check(trace.Events.All(x => !x.Code.Contains("thinking", StringComparison.OrdinalIgnoreCase)),
                "Typed progress trace exposes thinking/chain-of-thought code.");
            return Task.CompletedTask;
        });

        await Test("1103 task criterion evidence inspection model preserves requirements and evidence counts", () =>
        {
            var criteria = new[]
            {
                new AgentAcceptanceCriterion(
                    "c1",
                    "Output exists.",
                    [new AgentEvidenceReference(AgentEvidenceKind.ArtifactHash, "artifact:1", new string('a', 64))]),
                new AgentAcceptanceCriterion(
                    "c2",
                    "Legal status verified.")
            };
            var diagnostics = new AgentDiagnosticsSnapshot(
                100, 200, false, [], 2, 0, 1, 0, false, false, false);
            var snapshot = new AgentInspectionSnapshot(
                Guid.NewGuid(),
                "fixture",
                AgentTaskState.Verifying,
                AgentTaskRouteClass.ComplexAgent,
                criteria,
                [],
                100,
                false,
                diagnostics);
            Check(snapshot.Criteria.Count == 2
                && snapshot.Criteria[0].Evidence.Count == 1
                && snapshot.Criteria[1].Evidence.Count == 0,
                "Inspection snapshot lost criterion/evidence detail.");
            return Task.CompletedTask;
        });

        await Test("1104 cancellation propagates to linked transport tool token and registered helper abort", async () =>
        {
            using var coordinator = new AgentCancellationCoordinator();
            var helperAborted = false;
            using var registration = coordinator.RegisterHelperAbort(() => helperAborted = true);
            using var linked = coordinator.CreateLinked();

            var observedCancel = false;
            var worker = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), linked.Token);
                }
                catch (OperationCanceledException)
                {
                    observedCancel = true;
                }
            });

            coordinator.Cancel();
            await worker;
            Check(helperAborted && observedCancel && coordinator.IsCancellationRequested,
                "Cancellation did not reach helper abort and linked tool/transport token.");
        });

        await Test("1105 durable restart resume never blindly repeats uncertain mutation", () =>
        {
            var store = new DurableAgentTaskStore(Path.Combine(root, "1105-state"));
            var taskId = Guid.NewGuid();
            var durable = new DurableAgentTaskState(
                taskId,
                AgentTaskState.Executing,
                "modify fixture",
                "workspace:/fixture",
                DurableMutationState.StartedUncertain,
                "document:1",
                null,
                ["c1"],
                DateTime.UtcNow);
            store.Save(durable);
            var loaded = store.Load(taskId);
            var plan = AgentResumePlanner.Plan(loaded);

            Check(plan.Action == AgentResumeAction.ReobserveBeforeContinuing
                && !plan.MayRepeatMutation,
                "Uncertain mutation resume plan allows blind repeat.");
            return Task.CompletedTask;
        });

        await Test("1106 context diagnostics expose pressure compaction and dropped-item counters", () =>
        {
            var manager = new AgentContextManager(new AgentContextBudget
            {
                MaxTotalCharacters = 300,
                MaxTaskContractCharacters = 80,
                MaxCurrentStateCharacters = 60,
                MaxRecentTurnsCharacters = 80,
                MaxToolSummariesCharacters = 50,
                MaxCompactedHistoryCharacters = 30,
                MaxCharactersPerItem = 40,
                MaxRecentTurns = 2,
                MaxToolSummaries = 1
            });
            var snapshot = manager.Build(new AgentContextInput(
                TaskContract: new string('T', 200),
                CurrentState: new string('S', 200),
                RecentTurns:
                [
                    new AgentContextTurn("t1", AgentTransportMessageRole.User, new string('a', 100), 1),
                    new AgentContextTurn("t2", AgentTransportMessageRole.Assistant, new string('b', 100), 2),
                    new AgentContextTurn("t3", AgentTransportMessageRole.User, new string('c', 100), 3)
                ],
                ToolSummaries:
                [
                    new AgentContextToolSummary("tool1", "x", new string('x', 100), 1),
                    new AgentContextToolSummary("tool2", "y", new string('y', 100), 2)
                ],
                CompactedHistory: new string('H', 100)));
            var diagnostics = AgentDiagnostics.FromContext(snapshot);
            Check(diagnostics.RequiresCompaction
                && diagnostics.CandidateContextCharacters > diagnostics.ActiveContextCharacters
                && diagnostics.RecentTurnsDropped > 0
                && diagnostics.ToolSummariesDropped > 0
                && diagnostics.CompactionReasons.Count > 0,
                "Compaction diagnostics did not expose bounded-context pressure.");
            return Task.CompletedTask;
        });

        await Test("1107 CI workflow contains complete deterministic v1 v2 shared regression chain", () =>
        {
            var repo = FindRepoRoot();
            var workflow = File.ReadAllText(Path.Combine(repo, ".github", "workflows", "avalonia-ci.yml"));
            foreach (var required in new[]
            {
                "Run H2 Notes tests",
                "Run H2 Agent Lab v1 self-test",
                "Run H2 Agent Lab v2 architecture guard",
                "Run H2 Agent Lab v2 verification gate tests",
                "Run H2 Agent Lab v2 Phase 10 acceptance tests",
                "Run H2 Agent Lab v2 DesktopHost acceptance tests",
                "Run H2 Agent Lab v2 OfficeHost acceptance tests"
            })
                Check(workflow.Contains(required, StringComparison.Ordinal),
                    "CI regression chain is missing: " + required);
            return Task.CompletedTask;
        });

        await Test("1108 WebResearchHost exposes structured search fetch download extract metadata browser fallback lazily", async () =>
        {
            await using var host = new WebResearchHost(new FixtureWebBackend());
            await host.ConnectAsync(CancellationToken.None);
            var summaries = await host.ListToolSummariesAsync(CancellationToken.None);
            var expected = new[]
            {
                "web.search", "web.fetch", "web.download",
                "web.extract", "web.read_feed", "web.get_metadata", "web.open_browser"
            };
            Check(summaries.Select(x => x.Name).SequenceEqual(expected),
                "WebResearchHost capability surface is incomplete or unordered.");

            var definitions = await host.LoadToolDefinitionsAsync(["web.search"], CancellationToken.None);
            Check(definitions.Count == 1
                && definitions[0].Summary.Name == "web.search",
                "WebResearchHost did not lazily load selected schema only.");

            // AR-001 feed execution contract: keep exact inventory/order and prove the added
            // callable uses the existing backend/parser rather than weakening the guard.
            var feedDefinitions = await host.LoadToolDefinitionsAsync(["web.read_feed"], CancellationToken.None);
            Check(feedDefinitions.Count == 1 && feedDefinitions[0].Summary.Name == "web.read_feed"
                && feedDefinitions[0].Summary.Access == AgentToolAccess.ReadOnly
                && feedDefinitions[0].Summary.SupportsParallel
                && feedDefinitions[0].Summary.ResourceScope == "web:public",
                "Feed schema was eagerly mixed with other tools or changed permission metadata.");
            WebFeedPage? observedFeed = null;
            host.FeedObserved = page => observedFeed = page;
            using var feed = JsonDocument.Parse(await host.ExecuteToolAsync("web.read_feed",
                JsonSerializer.SerializeToElement(new { url = "https://feed.example.test/ar001.xml", max_items = 1 }),
                CancellationToken.None));
            Check(feed.RootElement.GetProperty("Items").GetArrayLength() == 1
                && feed.RootElement.GetProperty("Items")[0].GetProperty("Title").GetString() == "Tin thử AR-001"
                && feed.RootElement.GetProperty("Items")[0].GetProperty("Url").GetString() == "https://feed.example.test/item-1"
                && observedFeed is { TotalItems: 1 } && observedFeed.Items.Count == 1,
                "Feed execution lost source content, paging bound or host observation.");

            var search = await host.SearchAsync("Nghị định fixture", 5, CancellationToken.None);
            Check(search.Count >= 1 && search[0].Url.Contains("gov.vn", StringComparison.OrdinalIgnoreCase),
                "Structured WebResearchHost search did not return fixture authoritative source.");

            var fetched = await host.FetchAsync(search[0].Url, CancellationToken.None);
            Check(fetched.Bytes.Length > 0
                && WebResearchHost.ExtractText(fetched.ContentType, fetched.Bytes, 5_000)
                    .Contains("AMENDED", StringComparison.Ordinal),
                "Structured WebResearchHost fetch/extract failed.");
        });

        await Test("1109 FreshnessPolicy blocks model-memory-only completion for current and legal-status intents", () =>
        {
            var normal = FreshnessPolicy.Infer("summarize this local note");
            Check(normal.Kind == FreshnessRequirementKind.None,
                "Non-current local task incorrectly requires web freshness.");

            var current = FreshnessPolicy.Infer("latest current price today");
            Check(current.Kind == FreshnessRequirementKind.CurrentWeb,
                "Latest/current task did not require current web evidence.");

            var legal = FreshnessPolicy.Infer("Nghị định này còn hiệu lực hay đã được thay thế?");
            Check(legal.Kind == FreshnessRequirementKind.CurrentAuthoritative,
                "Legal effective-status task did not require authoritative freshness.");

            try
            {
                FreshnessCompletionGate.EnsureSatisfied(legal, [], DateTime.UtcNow);
                throw new InvalidOperationException("Freshness-required legal task completed from memory only.");
            }
            catch (InvalidOperationException)
            {
            }
            return Task.CompletedTask;
        });

        await Test("1110 WebEvidence persists provenance body hash and bounded context projection outside active context", () =>
        {
            var store = new WebEvidenceStore(Path.Combine(root, "1110-state"));
            var body = Encoding.UTF8.GetBytes("<html><body>" + new string('x', 10_000) + "</body></html>");
            var evidence = store.Store(
                new WebFetchedDocument(
                    "https://vanban.gov.vn/fixture",
                    "text/html",
                    body,
                    "Official fixture",
                    "Chính phủ",
                    DateTime.UtcNow.AddDays(-1),
                    DateTime.UtcNow.AddDays(-1)),
                WebSourceType.Official,
                new string('E', 5_000));

            Check(evidence.EvidenceId.StartsWith("evidence:web:", StringComparison.Ordinal)
                && evidence.ContentHash.Length == 64
                && evidence.RelevantExcerpt.Length <= WebEvidenceStore.MaxInlineExcerptCharacters + 1,
                "WebEvidence identity/hash/excerpt bounds are invalid.");
            Check(store.ReadBody(evidence).SequenceEqual(body),
                "WebEvidence body artifact cannot be retrieved/verified.");
            Check(WebEvidenceStore.ContextProjection(evidence).Length <= 3_000
                && !WebEvidenceStore.ContextProjection(evidence).Contains(new string('x', 5_000), StringComparison.Ordinal),
                "Full web body leaked into active context projection.");
            return Task.CompletedTask;
        });

        await Test("1111 legal verifier distinguishes typed relationships and rejects newer-equals-replaced shortcuts", () =>
        {
            var official = Evidence(
                "evidence:web:official",
                WebSourceType.Official,
                "https://vanban.gov.vn/official");
            var secondary = Evidence(
                "evidence:web:secondary",
                WebSourceType.Secondary,
                "https://example.com/article");

            var valid = new LegalDocumentStatus(
                "12/2024/ND-CP",
                "VN",
                "Chính phủ",
                DateTime.UtcNow.AddYears(-2),
                DateTime.UtcNow.AddYears(-2),
                LegalDocumentEffectiveStatus.AMENDED,
                [
                    new LegalDocumentRelationship(
                        LegalRelationType.AMENDED,
                        "99/2026/ND-CP",
                        DateTime.UtcNow.AddMonths(-1),
                        [official.EvidenceId])
                ],
                [official.EvidenceId]);
            Check(LegalStatusVerifier.Verify(valid, [official, secondary]).Passed,
                "Authoritative typed legal relationship did not verify.");

            var shortcut = valid with
            {
                Status = LegalDocumentEffectiveStatus.REPLACED,
                Relationships = [],
                EvidenceIds = [secondary.EvidenceId]
            };
            var fail = LegalStatusVerifier.Verify(shortcut, [official, secondary]);
            Check(!fail.Passed
                && fail.Failures.Single().Message.Contains("typed legal relationship", StringComparison.OrdinalIgnoreCase),
                "Legal verifier allowed newer-document shortcut without replacement relationship evidence.");
            return Task.CompletedTask;
        });

        await Test("1112 ToolRegistry exposes complete structured Word Excel namespaces including spelling grammar citations", () =>
        {
            var registry = new ToolRegistry();
            var executor = new DelegatingToolExecutor(
                "office-structured-fixture",
                (call, ct) => ValueTask.FromResult("{}"));
            _ = StructuredOfficeCapabilityCatalog.RegisterInto(registry, executor);

            foreach (var name in new[]
            {
                "word.get_active_document",
                "word.read_range",
                "word.read_runs",
                "word.read_headers_footers",
                "word.replace_range",
                "word.verify_range",
                "word.get_spelling_errors",
                "word.get_grammar_candidates",
                "word.extract_legal_citations",
                "excel.get_active_workbook",
                "excel.read_range",
                "excel.read_formulas",
                "excel.read_styles",
                "excel.read_merges",
                "excel.read_hidden_state",
                "excel.write_range",
                "excel.set_formula",
                "excel.verify_range"
            })
                Check(registry.TryGet(name, out _), "Missing structured Office tool: " + name);

            var language = new DefaultWordLanguageEvidenceProvider();
            var citations = language.ExtractLegalCitationsAsync(
                "Tham chiếu Nghị định 12/2024/NĐ-CP và Luật 22/2023/QH15.",
                CancellationToken.None).GetAwaiter().GetResult();
            Check(citations.Count >= 2,
                "Structured Word legal-citation extractor did not identify fixture citations.");
            return Task.CompletedTask;
        });

        await Test("1113 AutoCAD provider contract requires typed native plugin mutation with state tokens and rejects command/script fields", () =>
        {
            var valid = new AutoCadMutationRequest(
                AutoCadOperationKind.UpdateAttribute,
                "doc-1",
                "doc-state",
                "1A2B",
                "entity-state",
                JsonSerializer.SerializeToElement(new
                {
                    attributeTag = "NAME",
                    value = "Updated"
                }),
                true);
            AutoCadProviderPolicy.ValidateMutation(valid);

            try
            {
                AutoCadProviderPolicy.ValidateMutation(valid with
                {
                    Parameters = JsonSerializer.SerializeToElement(new
                    {
                        command = "_.ERASE ALL"
                    })
                });
                throw new InvalidOperationException("AutoCAD arbitrary command field was accepted.");
            }
            catch (InvalidOperationException)
            {
            }

            var descriptors = AutoCadProviderPolicy.BuildDescriptors(
                new DelegatingToolExecutor(
                    "cad-fixture",
                    (call, ct) => ValueTask.FromResult("{}")));
            Check(descriptors.Any(x => x.Name == "autocad.update_attribute")
                && descriptors.Any(x => x.Name == "autocad.plot")
                && descriptors.Any(x => x.Name == "autocad.verify_entity")
                && descriptors.All(x => x.Provenance?.ProviderId == "autocad-native-plugin"),
                "AutoCAD structured native-plugin provider surface is incomplete.");
            return Task.CompletedTask;
        });

        await Test("1114 AgentOrchestrator refreshes provider plugin capabilities only at controlled task boundaries", async () =>
        {
            var registry = new ToolRegistry();
            var coordinator = new AgentCapabilityRefreshCoordinator(registry);
            var source = new FixtureRefreshSource("plugins", 1);
            coordinator.RegisterSource(source);
            var orchestrator = new AgentOrchestrator(capabilityRefresh: coordinator);

            var first = await orchestrator.RefreshCapabilitiesAtTaskBoundaryAsync();
            Check(first.RefreshedSources.SequenceEqual(new[] { "plugins" }),
                "First capability refresh did not execute source.");

            var unchanged = await orchestrator.RefreshCapabilitiesAtTaskBoundaryAsync();
            Check(unchanged.RefreshedSources.Count == 0,
                "Unchanged capability source was refreshed repeatedly.");

            source.Version = 2;
            using (orchestrator.EnterCapabilityToolCallBoundary())
            {
                try
                {
                    _ = await orchestrator.RefreshCapabilitiesAtTaskBoundaryAsync();
                    throw new InvalidOperationException("Capability surface mutated during in-flight tool call.");
                }
                catch (InvalidOperationException ex) when (
                    ex.Message.Contains("in flight", StringComparison.OrdinalIgnoreCase))
                {
                }
            }

            var refreshed = await orchestrator.RefreshCapabilitiesAtTaskBoundaryAsync();
            Check(refreshed.RefreshedSources.SequenceEqual(new[] { "plugins" })
                && source.RefreshCount == 2,
                "Changed provider/plugin version did not refresh after safe boundary.");
        });

        await Test("1115 canonical unsaved Word legal scenario uses structured Office WebResearch verification and zero Desktop calls", async () =>
        {
            var stateRoot = Path.Combine(root, "1115-state");
            Directory.CreateDirectory(stateRoot);
            var word = new FixtureCanonicalWordSession();
            var language = new FixtureWordLanguageProvider();
            await using var web = new WebResearchHost(new FixtureWebBackend());
            var scenario = new CanonicalWordLegalScenario(
                word,
                language,
                web,
                new WebEvidenceStore(stateRoot),
                new FixtureLegalResolver());

            var result = await scenario.RunAsync(CancellationToken.None);
            Check(!result.Before.Saved
                && result.SpellingCandidates.Count == 1
                && result.LegalCitations.Count == 1
                && result.LegalStatuses.Single().Status == LegalDocumentEffectiveStatus.AMENDED,
                "Canonical scenario missed unsaved/spelling/legal structured state.");
            Check(result.After.Text.Contains("the", StringComparison.Ordinal)
                && result.After.Text.Contains("AMENDED", StringComparison.OrdinalIgnoreCase),
                "Canonical scenario did not apply spelling/legal patch.");
            Check(result.PreservationVerified
                && result.DesktopPixelCalls == 0
                && result.WebEvidence.Any(x => x.SourceType == WebSourceType.Official)
                && result.EvidenceIds.Count >= 4,
                "Canonical scenario failed preservation/evidence/zero-desktop acceptance.");
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "v2-phase11-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static WebEvidence Evidence(
        string id,
        WebSourceType sourceType,
        string url)
        => new(
            id,
            url,
            "Fixture",
            sourceType == WebSourceType.Official ? "Chính phủ" : "Secondary",
            DateTime.UtcNow.AddDays(-1),
            DateTime.UtcNow.AddDays(-1),
            DateTime.UtcNow,
            sourceType,
            new string('a', 64),
            "Fixture legal evidence.");

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(current.FullName, "experiments")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed class FixtureWebBackend : IWebResearchBackend
    {
        public Task<IReadOnlyList<WebSearchHit>> SearchAsync(
            string query,
            int maxResults,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WebSearchHit>>(
            [
                new(
                    "https://vanban.gov.vn/legal/99-2026",
                    "Official amendment",
                    "Chính phủ",
                    "Nghị định 99/2026/NĐ-CP AMENDED Nghị định 12/2024/NĐ-CP.",
                    DateTime.UtcNow.AddDays(-1)),
                new(
                    "https://example.com/summary",
                    "Secondary summary",
                    "Example News",
                    "Summary only.",
                    DateTime.UtcNow.AddDays(-1))
            ]);
        }

        public Task<WebFetchedDocument> FetchAsync(
            string url,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (url == "https://feed.example.test/ar001.xml")
                return Task.FromResult(new WebFetchedDocument(url, "application/rss+xml",
                    Encoding.UTF8.GetBytes("<rss version=\"2.0\"><channel><title>Fixture</title><item><title>Tin thử AR-001</title><link>https://feed.example.test/item-1</link><description>Observed fixture only</description></item></channel></rss>"),
                    "Fixture feed", "Fixture", null, null));
            var official = url.Contains("gov.vn", StringComparison.OrdinalIgnoreCase);
            var body = official
                ? "Official legal text: Nghị định 99/2026/NĐ-CP AMENDED Nghị định 12/2024/NĐ-CP effective 2026-08-01."
                : "Secondary summary only.";
            return Task.FromResult(new WebFetchedDocument(
                url,
                "text/html",
                Encoding.UTF8.GetBytes("<html><body>" + body + "</body></html>"),
                official ? "Official amendment" : "Secondary summary",
                official ? "Chính phủ" : "Example News",
                DateTime.UtcNow.AddDays(-1),
                official ? DateTime.UtcNow.AddMonths(-1) : null));
        }

        public Task<string> OpenBrowserFallbackAsync(
            string url,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(url);
        }
    }

    private sealed class FixtureRefreshSource : IAgentCapabilityRefreshSource
    {
        public FixtureRefreshSource(string sourceId, long version)
        {
            SourceId = sourceId;
            Version = version;
        }

        public string SourceId { get; }
        public long Version { get; set; }
        public int RefreshCount { get; private set; }

        public Task RefreshAsync(
            ToolRegistry registry,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FixtureWordLanguageProvider : IWordLanguageEvidenceProvider
    {
        public Task<IReadOnlyList<WordSpellingCandidate>> GetSpellingErrorsAsync(
            string text,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = text.IndexOf("teh", StringComparison.Ordinal);
            return Task.FromResult<IReadOnlyList<WordSpellingCandidate>>(
                start < 0
                    ? []
                    :
                    [
                        new(
                            start,
                            3,
                            "teh",
                            ["the"],
                            "evidence:word-spelling:1",
                            NativeEvidence: true)
                    ]);
        }

        public Task<IReadOnlyList<WordGrammarCandidate>> GetGrammarCandidatesAsync(
            string text,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WordGrammarCandidate>>([]);
        }

        public Task<IReadOnlyList<WordLegalCitation>> ExtractLegalCitationsAsync(
            string text,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var citation = "Nghị định 12/2024/NĐ-CP";
            var start = text.IndexOf(citation, StringComparison.Ordinal);
            return Task.FromResult<IReadOnlyList<WordLegalCitation>>(
                start < 0
                    ? []
                    :
                    [
                        new(
                            citation,
                            "12/2024/NĐ-CP",
                            start,
                            citation.Length,
                            "evidence:word-citation:1")
                    ]);
        }
    }

    private sealed class FixtureLegalResolver : ICanonicalLegalStatusResolver
    {
        public Task<LegalDocumentStatus> ResolveAsync(
            WordLegalCitation citation,
            IReadOnlyList<WebEvidence> evidence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var official = evidence.First(x => x.SourceType == WebSourceType.Official);
            return Task.FromResult(new LegalDocumentStatus(
                citation.DocumentId,
                "VN",
                "Chính phủ",
                DateTime.UtcNow.AddYears(-2),
                DateTime.UtcNow.AddYears(-2),
                LegalDocumentEffectiveStatus.AMENDED,
                [
                    new LegalDocumentRelationship(
                        LegalRelationType.AMENDED,
                        "99/2026/NĐ-CP",
                        DateTime.UtcNow.AddMonths(-1),
                        [official.EvidenceId])
                ],
                [official.EvidenceId]));
        }
    }

    private sealed class FixtureCanonicalWordSession : ICanonicalWordSession
    {
        private CanonicalWordState _state = new(
            "word-fixture",
            Saved: false,
            "This is teh paragraph. Tham chiếu Nghị định 12/2024/NĐ-CP.",
            "state-1",
            "preserve-table-style-header-footer");
        private int _stateSequence = 1;

        public int DesktopCallCount => 0;

        public Task<CanonicalWordState> ReadActiveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_state);
        }

        public Task<CanonicalWordPatchResult> ApplyPatchAsync(
            CanonicalWordPatch patch,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (patch.ExpectedStateToken != _state.StateToken)
                throw new InvalidOperationException("Fixture Word state became stale.");

            var before = _state;
            var text = before.Text;
            foreach (var spelling in patch.SpellingFixes.OrderByDescending(x => x.Start))
            {
                var replacement = spelling.Suggestions.FirstOrDefault() ?? spelling.Text;
                text = text.Remove(spelling.Start, spelling.Length).Insert(spelling.Start, replacement);
            }
            foreach (var legal in patch.LegalReplacements)
                text = text.Replace(legal.OriginalCitation, legal.ReplacementText, StringComparison.Ordinal);

            _stateSequence++;
            _state = before with
            {
                Text = text,
                StateToken = "state-" + _stateSequence
            };
            return Task.FromResult(new CanonicalWordPatchResult(
                before,
                _state,
                patch.SpellingFixes.Select(x => x.EvidenceId)
                    .Concat(patch.LegalReplacements.SelectMany(x => x.EvidenceIds))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()));
        }

        public Task<bool> VerifyPreservationAsync(
            CanonicalWordState before,
            CanonicalWordState after,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                before.PreservationToken == after.PreservationToken
                && !after.Saved);
        }
    }
}

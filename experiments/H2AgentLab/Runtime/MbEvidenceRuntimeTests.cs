using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using H2AgentLab.Context;
using H2AgentLab.Prompting;
using H2AgentLab.Session;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2AgentLab.Verification;

namespace H2AgentLab.Runtime;

public static class MbEvidenceRuntimeTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-41 test directory.");
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

        await Test("MB-41 large tool output becomes bounded durable evidence", async () =>
        {
            var state = Path.Combine(root, "large-output");
            var store = new ArtifactStore(state);
            var projector = new AgentRuntimeEvidenceProjector(store);
            const string tail = "TAIL_SENTINEL_MUST_NOT_REENTER_MODEL_CONTEXT";
            var full = "large observed output\n" + new string('x', 120_000) + "\n" + tail;

            var registry = new ToolRegistry();
            registry.Register(ReadTool(
                "fixture.large_output",
                "Return a large fixture output.",
                evidenceCapable: false,
                (call, ct) => ValueTask.FromResult(full)));

            var transport = new LargeOutputTransport();
            await using var runtime = new AgentRuntime(
                transport,
                new AgentContextManager(),
                registry,
                evidenceProjector: projector);

            var result = await runtime.RunAsync(
                Request("Read large fixture output.", mutating: false, requireVerification: false),
                CancellationToken.None);

            var evidence = result.Evidence.Single();
            Check(transport.ObservedEvidenceId == evidence.ReferenceId,
                "Model continuation did not receive the same durable evidence handle.");
            Check(!transport.ObservedToolContent.Contains(tail, StringComparison.Ordinal),
                "Large raw tail leaked back into model continuation.");
            Check(transport.ObservedToolContent.Length <= ArtifactStore.MaxContextHandleCharacters,
                "Large model projection exceeded ArtifactStore context-handle bound.");
            Check(store.ReadText(evidence.ReferenceId) == full,
                "Durable evidence did not preserve exact raw tool output.");

            var expectedHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(full))).ToLowerInvariant();
            Check(evidence.Sha256 == expectedHash,
                "Evidence SHA-256 does not match raw output.");

            var second = store.StoreText(
                AgentArtifactKind.ToolOutput,
                "tool-result:stable-hash",
                "fixture.large_output",
                full,
                "same output stored again",
                999);
            Check(second.Handle.Sha256 == evidence.Sha256,
                "Same raw output produced a different evidence hash.");
            Check(result.FinalText.Contains(evidence.ReferenceId, StringComparison.Ordinal),
                "Final answer could not cite the observed evidence handle.");
        });

        await Test("MB-41 important small tool result keeps inline observation plus evidence handle", async () =>
        {
            var state = Path.Combine(root, "small-evidence");
            var store = new ArtifactStore(state);
            var projector = new AgentRuntimeEvidenceProjector(store);
            const string observed = "{\"value\":42,\"source\":\"fixture\"}";

            var registry = new ToolRegistry();
            registry.Register(ReadTool(
                "fixture.evidence_read",
                "Return a small verification-capable observation.",
                evidenceCapable: true,
                (call, ct) => ValueTask.FromResult(observed)));

            var transport = new SmallEvidenceTransport();
            await using var runtime = new AgentRuntime(
                transport,
                new AgentContextManager(),
                registry,
                evidenceProjector: projector);

            var result = await runtime.RunAsync(
                Request("Read important fixture observation.", mutating: false, requireVerification: false),
                CancellationToken.None);

            var evidence = result.Evidence.Single();
            Check(transport.ObservedToolContent.Contains(observed, StringComparison.Ordinal),
                "Small important observation was unnecessarily removed from model context.");
            Check(transport.ObservedToolContent.Contains("[evidence:" + evidence.ReferenceId + "]", StringComparison.Ordinal),
                "Small important observation lacks an evidence handle.");
            Check(store.ReadText(evidence.ReferenceId) == observed,
                "Small evidence raw output did not round-trip.");
        });

        await Test("MB-41 verifier receives evidence IDs from real mutation tool results", async () =>
        {
            var state = Path.Combine(root, "verifier-evidence");
            var store = new ArtifactStore(state);
            var projector = new AgentRuntimeEvidenceProjector(store);
            var verifier = new EvidenceVerifier();

            var registry = new ToolRegistry();
            registry.Register(MutationTool(
                "fixture.mutate",
                "Mutate fixture and return observed result.",
                "fixture.mutation",
                (call, ct) => ValueTask.FromResult(
                    "{\"ok\":true,\"changed\":\"fixture.txt\"}")));

            var transport = new VerifiedMutationTransport();
            await using var runtime = new AgentRuntime(
                transport,
                new AgentContextManager(),
                registry,
                verifier: verifier,
                evidenceProjector: projector);

            var result = await runtime.RunAsync(
                Request("Mutate and verify fixture.", mutating: true, requireVerification: true),
                CancellationToken.None);

            var evidence = result.Evidence.Single();
            Check(verifier.ObservedEvidenceId == evidence.ReferenceId,
                "Verifier did not receive runtime tool evidence ID.");
            var report = result.VerificationHistory.Single();
            Check(report.Passed, "Verifier report did not pass.");
            Check(report.ReportEvidenceIds.Contains(evidence.ReferenceId, StringComparer.Ordinal),
                "Verifier report does not reference durable runtime evidence.");
            Check(report.Criteria.Single().EvidenceIds.Contains(evidence.ReferenceId, StringComparer.Ordinal),
                "Verification criterion does not reference durable runtime evidence.");
            Check(result.FinalText.Contains(evidence.ReferenceId, StringComparison.Ordinal),
                "Verified final answer did not retain observed evidence reference.");
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        await File.WriteAllLinesAsync(
            Path.Combine(root, "mb-evidence-runtime-tests.txt"),
            lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static AgentRuntimeRequest Request(
        string userInput,
        bool mutating,
        bool requireVerification)
    {
        var criteria = requireVerification
            ? new[]
            {
                new AgentAcceptanceCriterion(
                    "mutation-observed",
                    "Mutation result must be backed by durable observed evidence.")
            }
            : Array.Empty<AgentAcceptanceCriterion>();

        var contract = new AgentTaskContract(
            Guid.NewGuid(),
            userInput,
            "fixture",
            null,
            mutating ? ["mutate fixture"] : null,
            ["preserve unrelated state"],
            ["finish fixture"],
            criteria,
            mutating ? AgentTaskRiskClass.Medium : AgentTaskRiskClass.ReadOnly,
            new AgentVerificationPolicy(requireVerification));

        return new AgentRuntimeRequest(
            contract,
            userInput,
            new AgentPromptStablePrefix(
                AgentVersions.Current,
                "BASE POLICY",
                "SECURITY POLICY",
                "MODEL POLICY",
                ""),
            new AgentContextInput(
                TaskContract: contract.UserGoal,
                CurrentState: "MB-41 evidence fixture"),
            PromptCacheKey: "mb41",
            MaxToolRounds: 6);
    }

    private static ToolDescriptor ReadTool(
        string name,
        string description,
        bool evidenceCapable,
        Func<global::H2AgentLab.ToolCall, CancellationToken, ValueTask<string>> execute)
        => Tool(
            name,
            description,
            AgentToolAccess.ReadOnly,
            AgentToolRisk.Low,
            resourceScope: null,
            evidenceCapable,
            execute);

    private static ToolDescriptor MutationTool(
        string name,
        string description,
        string scope,
        Func<global::H2AgentLab.ToolCall, CancellationToken, ValueTask<string>> execute)
        => Tool(
            name,
            description,
            AgentToolAccess.Mutating,
            AgentToolRisk.Medium,
            new ToolResourceScope(scope, scope),
            evidenceCapable: true,
            execute);

    private static ToolDescriptor Tool(
        string name,
        string description,
        AgentToolAccess access,
        AgentToolRisk risk,
        ToolResourceScope? resourceScope,
        bool evidenceCapable,
        Func<global::H2AgentLab.ToolCall, CancellationToken, ValueTask<string>> execute)
        => new(
            name,
            new ToolNamespace("fixture", "MB-41 evidence fixture namespace."),
            description,
            risk,
            access,
            supportsParallel: access == AgentToolAccess.ReadOnly,
            schemaVersion: "v1",
            callableSchema: JsonSerializer.SerializeToElement(new
            {
                type = "function",
                function = new
                {
                    name,
                    description,
                    parameters = new
                    {
                        type = "object",
                        properties = new { },
                        additionalProperties = false
                    }
                }
            }),
            executor: new DelegatingToolExecutor("mb41-fixture", execute),
            provenance: new ToolProvenance("mb41", "1.0.0", "fixture", "1.0.0"),
            resourceScope: resourceScope,
            serializationKey: access == AgentToolAccess.Mutating ? "mb41-mutation" : "mb41-read",
            canProvideVerificationEvidence: evidenceCapable);

    private sealed class EvidenceVerifier : IAgentRuntimeVerifier
    {
        public string? ObservedEvidenceId { get; private set; }

        public Task<VerificationReport?> VerifyAsync(
            AgentRuntimeVerificationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evidence = context.Evidence.Single();
            ObservedEvidenceId = evidence.ReferenceId;
            VerificationReport report = new(
                "mb41-evidence-verifier",
                [
                    new VerificationCriterionResult(
                        "mutation-observed",
                        VerificationCriterionStatus.Passed,
                        [evidence.ReferenceId])
                ],
                [evidence.ReferenceId]);
            return Task.FromResult<VerificationReport?>(report);
        }
    }

    private abstract class FixtureTransport : IAgentTransport
    {
        protected int Continuations;
        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.Minimal;
        public bool Cancelled { get; private set; }

        public abstract IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            CancellationToken cancellationToken = default);

        public abstract IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            CancellationToken cancellationToken = default);

        public void Cancel() => Cancelled = true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        protected static async IAsyncEnumerable<AgentTransportEvent> Search(
            string id,
            string query,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return AgentTransportEvent.Tool(new(
                id,
                DeferredToolDiscovery.SearchToolName,
                JsonSerializer.Serialize(new { query })));
            await Task.Yield();
            yield return AgentTransportEvent.Complete(id + "-done", "tool_calls");
        }

        protected static string EvidenceId(string content)
        {
            foreach (var marker in new[] { "[artifact:", "[evidence:" })
            {
                var start = content.IndexOf(marker, StringComparison.Ordinal);
                if (start < 0) continue;
                start += marker.Length;
                var end = content.IndexOf(']', start);
                if (end > start) return content[start..end];
            }
            throw new InvalidOperationException("Tool result has no evidence/artifact handle.");
        }
    }

    private sealed class LargeOutputTransport : FixtureTransport
    {
        public string ObservedToolContent { get; private set; } = "";
        public string ObservedEvidenceId { get; private set; } = "";

        public override IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            CancellationToken cancellationToken = default)
            => Search("search-large", "large fixture output evidence", cancellationToken);

        public override async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (++Continuations == 1)
            {
                if (request.NewlyLoadedTools?.Single().Name != "fixture.large_output")
                    throw new InvalidOperationException("Large-output schema was not loaded.");
                yield return AgentTransportEvent.Tool(new("large-call", "fixture.large_output", "{}"));
                yield return AgentTransportEvent.Complete("large-call-done", "tool_calls");
                yield break;
            }

            if (Continuations == 2)
            {
                var tool = request.ToolResults.Single();
                ObservedToolContent = tool.Content;
                ObservedEvidenceId = EvidenceId(tool.Content);
                yield return AgentTransportEvent.TextDeltaEvent(
                    "Observed durable evidence " + ObservedEvidenceId);
                yield return AgentTransportEvent.Complete("large-final", "stop");
                yield break;
            }

            throw new InvalidOperationException("Unexpected large-output continuation.");
        }
    }

    private sealed class SmallEvidenceTransport : FixtureTransport
    {
        public string ObservedToolContent { get; private set; } = "";

        public override IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            CancellationToken cancellationToken = default)
            => Search("search-small", "small verification capable fixture observation", cancellationToken);

        public override async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (++Continuations == 1)
            {
                if (request.NewlyLoadedTools?.Single().Name != "fixture.evidence_read")
                    throw new InvalidOperationException("Small-evidence schema was not loaded.");
                yield return AgentTransportEvent.Tool(new("small-call", "fixture.evidence_read", "{}"));
                yield return AgentTransportEvent.Complete("small-call-done", "tool_calls");
                yield break;
            }

            if (Continuations == 2)
            {
                var tool = request.ToolResults.Single();
                ObservedToolContent = tool.Content;
                var evidenceId = EvidenceId(tool.Content);
                yield return AgentTransportEvent.TextDeltaEvent("Observed " + evidenceId);
                yield return AgentTransportEvent.Complete("small-final", "stop");
                yield break;
            }

            throw new InvalidOperationException("Unexpected small-evidence continuation.");
        }
    }

    private sealed class VerifiedMutationTransport : FixtureTransport
    {
        private string _evidenceId = "";

        public override IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            CancellationToken cancellationToken = default)
            => Search("search-mutation", "mutate fixture observed result", cancellationToken);

        public override async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (++Continuations == 1)
            {
                if (request.NewlyLoadedTools?.Single().Name != "fixture.mutate")
                    throw new InvalidOperationException("Mutation schema was not loaded.");
                yield return AgentTransportEvent.Tool(new("mutation-call", "fixture.mutate", "{}"));
                yield return AgentTransportEvent.Complete("mutation-call-done", "tool_calls");
                yield break;
            }

            if (Continuations == 2)
            {
                var tool = request.ToolResults.Single();
                if (tool.IsError)
                    throw new InvalidOperationException("Mutation tool unexpectedly failed.");
                _evidenceId = EvidenceId(tool.Content);
                yield return AgentTransportEvent.TextDeltaEvent(
                    "Verified observed mutation evidence " + _evidenceId);
                yield return AgentTransportEvent.Complete("mutation-final", "stop");
                yield break;
            }

            throw new InvalidOperationException("Unexpected mutation continuation.");
        }
    }
}

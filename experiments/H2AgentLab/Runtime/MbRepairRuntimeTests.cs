using System.Runtime.CompilerServices;
using System.Text.Json;
using H2AgentLab.Context;
using H2AgentLab.Prompting;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2AgentLab.Verification;

namespace H2AgentLab.Runtime;

public static class MbRepairRuntimeTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-43 test directory.");
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

        await Test("MB-43 failed-only repair context preserves prior passed criteria without transcript replay", async () =>
        {
            var executions = 0;
            var registry = Registry((call, ct) =>
            {
                Interlocked.Increment(ref executions);
                return ValueTask.FromResult(JsonSerializer.Serialize(new
                {
                    value = call.Arguments.GetProperty("value").GetString()
                }));
            });
            var transport = new PreserveCriteriaTransport();
            var verifier = new PreserveCriteriaVerifier();

            await using var runtime = new AgentRuntime(
                transport,
                new AgentContextManager(),
                registry,
                verifier: verifier);

            var result = await runtime.RunAsync(
                Request(
                    "Repair fixture FULL_TRANSCRIPT_SENTINEL_DO_NOT_REPLAY.",
                    [
                        new AgentAcceptanceCriterion("criterion-a", "Criterion A remains correct."),
                        new AgentAcceptanceCriterion("criterion-b", "Criterion B is corrected.")
                    ],
                    maxRepairRounds: 2),
                CancellationToken.None);

            Check(result.FinalText == "repair-complete",
                "Repair fixture did not reach final completion.");
            Check(executions == 2 && result.RepairRounds == 1,
                "Repair fixture did not execute exactly one corrective mutation.");
            Check(result.VerificationHistory.Count == 2,
                "Expected failed and repaired verification snapshots.");
            var latest = result.VerificationHistory[^1];
            Check(latest.Passed
                && latest.Criteria.Count == 2
                && latest.Criteria.Single(x => x.CriterionId == "criterion-a").Status == VerificationCriterionStatus.Passed
                && latest.Criteria.Single(x => x.CriterionId == "criterion-b").Status == VerificationCriterionStatus.Passed,
                "Previously passed criterion was lost across the repair round.");
            Check(transport.SawFailedOnlyContext,
                "Repair continuation did not receive failed-criteria-only host context.");
            Check(!transport.SawTranscriptSentinel,
                "Repair continuation replayed full user transcript content.");
        });

        await Test("MB-43 canonical duplicate mutation is blocked before a second executor call", async () =>
        {
            var executions = 0;
            var registry = Registry((call, ct) =>
            {
                Interlocked.Increment(ref executions);
                return ValueTask.FromResult(JsonSerializer.Serialize(new
                {
                    value = call.Arguments.GetProperty("value").GetString()
                }));
            });
            var transport = new DuplicateMutationTransport();
            var verifier = new SingleCriterionVerifier();

            await using var runtime = new AgentRuntime(
                transport,
                new AgentContextManager(),
                registry,
                verifier: verifier);

            var result = await runtime.RunAsync(
                Request(
                    "Repair a duplicate failing mutation.",
                    [new AgentAcceptanceCriterion("fixture.correct", "Fixture value is good.")],
                    maxRepairRounds: 3),
                CancellationToken.None);

            Check(result.FinalText == "duplicate-blocked-then-repaired",
                "Duplicate mutation fixture did not complete after a changed correction.");
            Check(executions == 2,
                $"Expected bad+good executor calls only; observed {executions}.");
            Check(transport.SawDuplicateBlock,
                "Identical failed mutation was not blocked by host before executor.");
            Check(verifier.Verifications == 2,
                "Verifier should run only for actually executed mutations.");
        });

        await Test("MB-43 repair retry budget is host-bounded", async () =>
        {
            var executions = 0;
            var registry = Registry((call, ct) =>
            {
                Interlocked.Increment(ref executions);
                return ValueTask.FromResult("{}");
            });
            var verifier = new AlwaysFailVerifier();

            await using var runtime = new AgentRuntime(
                new RepairBudgetTransport(),
                new AgentContextManager(),
                registry,
                verifier: verifier);

            try
            {
                _ = await runtime.RunAsync(
                    Request(
                        "Exercise repair budget.",
                        [new AgentAcceptanceCriterion("fixture.correct", "Fixture must verify.")],
                        maxRepairRounds: 1),
                    CancellationToken.None);
                throw new InvalidOperationException("Repair budget unexpectedly allowed completion.");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("repair budget", StringComparison.OrdinalIgnoreCase))
            {
            }

            Check(executions == 2,
                "Bounded repair budget did not stop after the configured second failure.");
            Check(verifier.Verifications == 2,
                "Verifier count does not match bounded repair attempts.");
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-repair-runtime-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static ToolRegistry Registry(
        Func<global::H2AgentLab.ToolCall, CancellationToken, ValueTask<string>> execute)
    {
        var registry = new ToolRegistry();
        registry.Register(new ToolDescriptor(
            "fixture.write",
            new ToolNamespace("fixture", "MB-43 bounded repair fixture."),
            "Write fixture state for bounded repair verification.",
            AgentToolRisk.Medium,
            AgentToolAccess.Mutating,
            supportsParallel: false,
            schemaVersion: "v1",
            callableSchema: JsonSerializer.SerializeToElement(new
            {
                type = "function",
                function = new
                {
                    name = "fixture.write",
                    description = "Write fixture state for bounded repair verification.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            value = new { type = "string" },
                            meta = new { type = "object" }
                        },
                        required = new[] { "value" },
                        additionalProperties = false
                    }
                }
            }),
            executor: new DelegatingToolExecutor("mb43-fixture", execute),
            provenance: new ToolProvenance("mb43", "1.0.0", "fixture", "1.0.0"),
            resourceScope: new ToolResourceScope("fixture:repair", "fixture:repair"),
            serializationKey: "mb43-fixture",
            canProvideVerificationEvidence: true));
        return registry;
    }

    private static AgentRuntimeRequest Request(
        string userInput,
        IReadOnlyList<AgentAcceptanceCriterion> criteria,
        int maxRepairRounds)
    {
        var contract = new AgentTaskContract(
            Guid.NewGuid(),
            userInput,
            "fixture:repair",
            null,
            ["repair fixture"],
            ["preserve already-passed criteria"],
            ["complete only after host verification"],
            criteria,
            AgentTaskRiskClass.Medium,
            new AgentVerificationPolicy(
                requireVerification: true,
                requiredVerifierIds: ["mb43-verifier"]));

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
                CurrentState: "bounded repair fixture"),
            PromptCacheKey: "mb43",
            MaxToolRounds: 8,
            MaxRepairRounds: maxRepairRounds);
    }

    private abstract class RepairTransportBase : IAgentTransport
    {
        protected int Continuations;
        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.Minimal;

        public virtual async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return AgentTransportEvent.Tool(new(
                "search-repair",
                DeferredToolDiscovery.SearchToolName,
                JsonSerializer.Serialize(new { query = "write fixture repair state" })));
            await Task.Yield();
            yield return AgentTransportEvent.Complete("search", "tool_calls");
        }

        protected static void RequireLoaded(AgentTransportContinuationRequest request)
        {
            if (request.NewlyLoadedTools?.Single().Name != "fixture.write")
                throw new InvalidOperationException("fixture.write was not loaded.");
        }

        public abstract IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            CancellationToken cancellationToken = default);

        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PreserveCriteriaTransport : RepairTransportBase
    {
        public bool SawFailedOnlyContext { get; private set; }
        public bool SawTranscriptSentinel { get; private set; }

        public override async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Continuations++;

            if (Continuations == 1)
            {
                RequireLoaded(request);
                yield return AgentTransportEvent.Tool(new(
                    "write-bad",
                    "fixture.write",
                    JsonSerializer.Serialize(new { value = "bad" })));
                yield return AgentTransportEvent.Complete("bad", "tool_calls");
                yield break;
            }

            if (Continuations == 2)
            {
                var failed = request.ToolResults.Single();
                var text = failed.Content;
                SawFailedOnlyContext =
                    failed.IsError
                    && text.Contains("[HOST VERIFICATION FAILED]", StringComparison.Ordinal)
                    && text.Contains("FAILED criterion-b", StringComparison.Ordinal)
                    && text.Contains("Preserve passed criteria: criterion-a", StringComparison.Ordinal)
                    && !text.Contains("FAILED criterion-a", StringComparison.Ordinal);
                SawTranscriptSentinel = text.Contains(
                    "FULL_TRANSCRIPT_SENTINEL_DO_NOT_REPLAY",
                    StringComparison.Ordinal);

                yield return AgentTransportEvent.Tool(new(
                    "write-good",
                    "fixture.write",
                    JsonSerializer.Serialize(new { value = "good" })));
                yield return AgentTransportEvent.Complete("good", "tool_calls");
                yield break;
            }

            if (Continuations == 3)
            {
                if (request.ToolResults.Single().IsError)
                    throw new InvalidOperationException("Corrective mutation remained an error.");
                yield return AgentTransportEvent.TextDeltaEvent("repair-complete");
                await Task.Yield();
                yield return AgentTransportEvent.Complete("final", "stop");
                yield break;
            }

            throw new InvalidOperationException("Unexpected preserve-criteria continuation.");
        }
    }

    private sealed class DuplicateMutationTransport : RepairTransportBase
    {
        public bool SawDuplicateBlock { get; private set; }

        public override async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Continuations++;

            if (Continuations == 1)
            {
                RequireLoaded(request);
                yield return AgentTransportEvent.Tool(new(
                    "write-bad",
                    "fixture.write",
                    "{\"value\":\"bad\",\"meta\":{\"b\":2,\"a\":1}}"));
                yield return AgentTransportEvent.Complete("bad", "tool_calls");
                yield break;
            }

            if (Continuations == 2)
            {
                if (!request.ToolResults.Single().IsError)
                    throw new InvalidOperationException("Initial failed mutation lacks repair feedback.");
                yield return AgentTransportEvent.Tool(new(
                    "write-repeat",
                    "fixture.write",
                    "{\"meta\":{\"a\":1,\"b\":2},\"value\":\"bad\"}"));
                yield return AgentTransportEvent.Complete("repeat", "tool_calls");
                yield break;
            }

            if (Continuations == 3)
            {
                var repeated = request.ToolResults.Single();
                SawDuplicateBlock = repeated.IsError
                    && repeated.Content.Contains("repeated_failed_mutation", StringComparison.Ordinal);
                yield return AgentTransportEvent.Tool(new(
                    "write-good",
                    "fixture.write",
                    "{\"meta\":{\"a\":1,\"b\":2},\"value\":\"good\"}"));
                yield return AgentTransportEvent.Complete("good", "tool_calls");
                yield break;
            }

            if (Continuations == 4)
            {
                if (request.ToolResults.Single().IsError)
                    throw new InvalidOperationException("Changed corrective mutation remained an error.");
                yield return AgentTransportEvent.TextDeltaEvent("duplicate-blocked-then-repaired");
                await Task.Yield();
                yield return AgentTransportEvent.Complete("final", "stop");
                yield break;
            }

            throw new InvalidOperationException("Unexpected duplicate-mutation continuation.");
        }
    }

    private sealed class RepairBudgetTransport : RepairTransportBase
    {
        public override async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Continuations++;

            if (Continuations == 1)
            {
                RequireLoaded(request);
                yield return AgentTransportEvent.Tool(new(
                    "write-bad-1",
                    "fixture.write",
                    JsonSerializer.Serialize(new { value = "bad-1" })));
                yield return AgentTransportEvent.Complete("bad-1", "tool_calls");
                yield break;
            }

            if (Continuations == 2)
            {
                if (!request.ToolResults.Single().IsError)
                    throw new InvalidOperationException("First failure lacks repair feedback.");
                yield return AgentTransportEvent.Tool(new(
                    "write-bad-2",
                    "fixture.write",
                    JsonSerializer.Serialize(new { value = "bad-2" })));
                yield return AgentTransportEvent.Complete("bad-2", "tool_calls");
                yield break;
            }

            throw new InvalidOperationException("Repair budget should stop before another continuation.");
        }
    }

    private static VerificationReport BindFixture(AgentRuntimeVerificationContext context, VerificationReport report)
        => report with { CallCoverage = context.Calls.Where(c => context.MutationCallIds.Contains(c.Id))
            .SelectMany(call => report.Criteria.Select(c => new VerificationCallCoverage(call.Invocation!.InvocationId,
                c.CriterionId, "fixture:repair", c.CriterionId, c.Status, c.EvidenceIds))).ToArray() };

    private sealed class PreserveCriteriaVerifier : IAgentRuntimeVerifier
    {
        private int _writes;

        public Task<VerificationReport?> VerifyAsync(
            AgentRuntimeVerificationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.RawToolOutputs.Count == 0)
                return Task.FromResult<VerificationReport?>(null);

            _writes++;
            if (_writes == 1)
            {
                return Task.FromResult<VerificationReport?>(BindFixture(context, new VerificationReport(
                    "mb43-verifier",
                    [
                        new VerificationCriterionResult(
                            "criterion-a",
                            VerificationCriterionStatus.Passed,
                            ["evidence:a:pass"]),
                        new VerificationCriterionResult(
                            "criterion-b",
                            VerificationCriterionStatus.Failed,
                            ["evidence:b:fail"],
                            new VerificationFailure(
                                "criterion-b",
                                "Criterion B still needs correction.",
                                ["evidence:b:fail"]))
                    ])));
            }

            return Task.FromResult<VerificationReport?>(BindFixture(context, new VerificationReport(
                "mb43-verifier",
                [
                    // The repair prompt remains failed-only; post-repair verification also
                    // reasserts preservation of A on this same affected fixture resource.
                    new VerificationCriterionResult("criterion-a", VerificationCriterionStatus.Passed, ["evidence:a:preserved"]),
                    new VerificationCriterionResult(
                        "criterion-b",
                        VerificationCriterionStatus.Passed,
                        ["evidence:b:pass"])
                ])));
        }
    }

    private sealed class SingleCriterionVerifier : IAgentRuntimeVerifier
    {
        public int Verifications { get; private set; }

        public Task<VerificationReport?> VerifyAsync(
            AgentRuntimeVerificationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.RawToolOutputs.Count == 0)
                return Task.FromResult<VerificationReport?>(null);

            Verifications++;
            var raw = context.RawToolOutputs.Values.Single();
            var good = raw.Contains("good", StringComparison.Ordinal);
            var result = good
                ? new VerificationCriterionResult(
                    "fixture.correct",
                    VerificationCriterionStatus.Passed,
                    ["evidence:good"])
                : new VerificationCriterionResult(
                    "fixture.correct",
                    VerificationCriterionStatus.Failed,
                    ["evidence:bad"],
                    new VerificationFailure(
                        "fixture.correct",
                        "Fixture is still bad.",
                        ["evidence:bad"]));
            return Task.FromResult<VerificationReport?>(
                BindFixture(context, new VerificationReport("mb43-verifier", [result])));
        }
    }

    private sealed class AlwaysFailVerifier : IAgentRuntimeVerifier
    {
        public int Verifications { get; private set; }

        public Task<VerificationReport?> VerifyAsync(
            AgentRuntimeVerificationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.RawToolOutputs.Count == 0)
                return Task.FromResult<VerificationReport?>(null);

            Verifications++;
            return Task.FromResult<VerificationReport?>(BindFixture(context, new VerificationReport(
                "mb43-verifier",
                [
                    new VerificationCriterionResult(
                        "fixture.correct",
                        VerificationCriterionStatus.Failed,
                        [$"evidence:failure:{Verifications}"],
                        new VerificationFailure(
                            "fixture.correct",
                            $"Attempt {Verifications} still fails.",
                            [$"evidence:failure:{Verifications}"]))
                ])));
        }
    }
}

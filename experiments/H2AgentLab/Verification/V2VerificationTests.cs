using System.Text.Json;
using H2AgentLab.Tasking;

namespace H2AgentLab.Verification;

public static class V2VerificationTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new v2 verification test directory.");
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
                lines.Add("FAIL " + name + ": " + ex.Message);
            }
        }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        static AgentTaskContract Contract()
            => new(
                Guid.NewGuid(),
                "Produce and verify fixture artifact",
                "workspace:/fixture",
                ["source.txt"],
                ["update result"],
                ["preserve source"],
                ["result.txt"],
                [
                    new AgentAcceptanceCriterion("content", "Result content is correct."),
                    new AgentAcceptanceCriterion("scope", "Only intended files changed.")
                ],
                AgentTaskRiskClass.Medium,
                new AgentVerificationPolicy(
                    requireVerification: true,
                    requiredVerifierIds: ["semantic-verifier", FileScopeVerifier.VerifierId]));

        static AgentOrchestrationSession ToVerifying(
            AgentOrchestrator orchestrator,
            AgentTaskContract contract)
        {
            var session = orchestrator.Receive(contract);
            orchestrator.Ground(session);
            orchestrator.Plan(session);
            orchestrator.Execute(session);
            orchestrator.Verify(session);
            return session;
        }

        await Test("Model completion claim cannot bypass failed semantic verifier", () =>
        {
            var orchestrator = new AgentOrchestrator();
            var contract = Contract();
            var session = ToVerifying(orchestrator, contract);
            const bool modelSaysDone = true;
            Check(modelSaysDone, "Fixture must represent a model completion claim.");

            var semantic = new VerificationReport(
                "semantic-verifier",
                [
                    new VerificationCriterionResult(
                        "content",
                        VerificationCriterionStatus.Failed,
                        ["semantic:evidence"],
                        new VerificationFailure("content", "Observed content mismatch.", ["semantic:evidence"]))
                ]);
            var scope = new VerificationReport(
                FileScopeVerifier.VerifierId,
                [new VerificationCriterionResult("scope", VerificationCriterionStatus.Passed, ["scope:evidence"])]);

            try
            {
                orchestrator.CompleteVerified(session, [semantic, scope], "model proposed completion");
                throw new InvalidOperationException("Failed verifier allowed model completion claim.");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("cannot complete without successful verification", StringComparison.Ordinal))
            {
            }

            Check(session.StateMachine.State == AgentTaskState.Verifying,
                "Failed verification changed task state away from Verifying.");
            return Task.CompletedTask;
        });

        await Test("Missing or NotVerified acceptance criterion cannot complete", () =>
        {
            var contract = Contract();

            var missing = VerificationCompletionGate.Evaluate(
                contract,
                [
                    new VerificationReport(
                        "semantic-verifier",
                        [new VerificationCriterionResult("content", VerificationCriterionStatus.Passed, ["e1"])])
                ]);
            Check(!missing.Passed, "Missing acceptance criterion produced passing outcome.");

            var notVerified = VerificationCompletionGate.Evaluate(
                contract,
                [
                    new VerificationReport(
                        "semantic-verifier",
                        [new VerificationCriterionResult("content", VerificationCriterionStatus.Passed, ["e1"])]),
                    new VerificationReport(
                        FileScopeVerifier.VerifierId,
                        [new VerificationCriterionResult("scope", VerificationCriterionStatus.NotVerified)])
                ]);
            Check(!notVerified.Passed, "NotVerified criterion produced passing outcome.");
            return Task.CompletedTask;
        });

        await Test("All required verifier reports complete mutating task", () =>
        {
            var orchestrator = new AgentOrchestrator();
            var contract = Contract();
            var session = ToVerifying(orchestrator, contract);

            orchestrator.CompleteVerified(
                session,
                [
                    new VerificationReport(
                        "semantic-verifier",
                        [new VerificationCriterionResult("content", VerificationCriterionStatus.Passed, ["semantic:ok"])]),
                    new VerificationReport(
                        FileScopeVerifier.VerifierId,
                        [new VerificationCriterionResult("scope", VerificationCriterionStatus.Passed, ["scope:ok"])])
                ],
                "all required verification passed");

            Check(session.StateMachine.State == AgentTaskState.Completed && session.StateMachine.IsTerminal,
                "All-passed required verifier reports did not complete task.");
            return Task.CompletedTask;
        });

        await Test("File/hash/scope failure remains machine-readable", () =>
        {
            static string Hash(char c) => new string(c, 64);
            var before = new[] { new FileVerificationEntry("source.txt", Hash('a')) };
            var after = new[]
            {
                new FileVerificationEntry("source.txt", Hash('b')),
                new FileVerificationEntry("rogue.txt", Hash('c'))
            };
            var report = FileScopeVerifier.Verify(
                before,
                after,
                new FileVerificationExpectation(
                    expectedChangedPaths: ["source.txt"],
                    expectedHashes: new Dictionary<string, string> { ["source.txt"] = Hash('b') }));

            Check(!report.Passed, "Unexpected output incorrectly passed file scope verification.");
            Check(report.Failures.Any(x => x.CriterionId == FileScopeVerifier.ExactChangesCriterionId)
                && report.Failures.Any(x => x.CriterionId == FileScopeVerifier.UnintendedOutputCriterionId),
                "File scope verifier did not expose failed criteria.");
            return Task.CompletedTask;
        });

        await Test("Repair context targets only failed criteria and protects passed criteria", () =>
        {
            var contract = Contract();
            var report = new VerificationReport(
                "semantic-verifier",
                [
                    new VerificationCriterionResult(
                        "content",
                        VerificationCriterionStatus.Failed,
                        ["content:bad"],
                        new VerificationFailure("content", "Expected HELLO, observed H3LLO.", ["content:bad"])),
                    new VerificationCriterionResult("scope", VerificationCriterionStatus.Passed, ["scope:ok"])
                ]);

            var repair = new AgentRepairController().Build(contract, report);
            Check(repair.FailedCriterionIds.SequenceEqual(new[] { "content" }),
                "Repair controller targeted wrong failed criteria.");
            Check(repair.PassedCriterionIds.SequenceEqual(new[] { "scope" }),
                "Repair controller lost passed criterion preservation.");
            Check(repair.PromptContext.Contains("Expected HELLO, observed H3LLO.", StringComparison.Ordinal)
                && repair.PromptContext.Contains("Preserve passed criteria: scope", StringComparison.Ordinal),
                "Repair context lacks failure/preservation evidence.");
            return Task.CompletedTask;
        });

        await Test("Runtime recovery cannot erase semantic verification failure", () =>
        {
            var contract = Contract();
            var report = new VerificationReport(
                "semantic-verifier",
                [
                    new VerificationCriterionResult(
                        "content",
                        VerificationCriterionStatus.Failed,
                        ["content:bad"],
                        new VerificationFailure("content", "Semantic mismatch.", ["content:bad"])),
                    new VerificationCriterionResult("scope", VerificationCriterionStatus.Passed, ["scope:ok"])
                ]);

            var coordinator = new VerificationRecoveryCoordinator();
            var call = new global::H2AgentLab.ToolCall(
                "runtime",
                "read_file",
                JsonSerializer.SerializeToElement(new { path = "missing.txt", offset = "0" }));
            coordinator.ObserveRuntimeCall(
                call,
                JsonSerializer.Serialize(new
                {
                    recovery = new
                    {
                        code = "not_found",
                        message = "missing",
                        recoverable = true,
                        next = "inspect"
                    }
                }));
            Check(coordinator.GetState(report).HasRuntimeRecoveryPending,
                "Runtime failure was not tracked.");

            coordinator.ObserveRuntimeCall(call, "{\"content\":\"runtime ok\"}");
            var state = coordinator.GetState(report);
            Check(!state.HasRuntimeRecoveryPending && state.HasSemanticVerificationFailures,
                "Runtime recovery cleared semantic failure.");
            Check(coordinator.BuildSemanticRepair(contract, report).FailedCriterionIds.Contains("content"),
                "Semantic repair disappeared after runtime recovery.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var reportPath = Path.Combine(root, "v2-verification-tests.txt");
        await File.WriteAllLinesAsync(reportPath, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }
}

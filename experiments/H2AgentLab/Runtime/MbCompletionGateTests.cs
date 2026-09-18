using System.Runtime.CompilerServices;
using H2AgentLab.Context;
using H2AgentLab.Prompting;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2AgentLab.Verification;

namespace H2AgentLab.Runtime;

public static class MbCompletionGateTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-44 test directory.");
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

        await Test("MB-44 model done cannot complete a mutating host task", async () =>
        {
            var contract = MutationContract(
                requireVerification: false,
                allowNotMechanicallyVerifiable: false);
            var orchestrator = new AgentOrchestrator();
            var session = orchestrator.Receive(contract);
            orchestrator.Ground(session);
            orchestrator.Plan(session);

            await using var runtime = new AgentRuntime(
                new DirectFinalTransport("done"),
                new AgentContextManager(),
                new ToolRegistry());

            try
            {
                _ = await orchestrator.RunRuntimeAsync(
                    session,
                    runtime,
                    Request(contract),
                    CancellationToken.None);
                throw new InvalidOperationException(
                    "Model final text unexpectedly completed a mutating task.");
            }
            catch (AgentVerificationRequiredException)
            {
            }

            Check(session.StateMachine.State == AgentTaskState.Blocked,
                "Host did not block mutation after model-only completion claim.");
            Check(session.StateMachine.State != AgentTaskState.Completed,
                "Model phrase 'done' marked mutation Completed.");
        });

        await Test("MB-44 required verifier failure keeps task non-complete", () =>
        {
            var contract = MutationContract(
                requireVerification: true,
                allowNotMechanicallyVerifiable: false,
                verifierIds: ["required-verifier"]);
            var orchestrator = new AgentOrchestrator();
            var session = ToVerifying(orchestrator, contract);

            var failedReport = new VerificationReport(
                "required-verifier",
                [
                    new VerificationCriterionResult(
                        "result",
                        VerificationCriterionStatus.Failed,
                        ["evidence:failed"],
                        new VerificationFailure(
                            "result",
                            "Observed state does not satisfy the required mutation.",
                            ["evidence:failed"]))
                ]);

            try
            {
                orchestrator.CompleteVerified(
                    session,
                    [failedReport],
                    "model proposed completion after failed verification");
                throw new InvalidOperationException(
                    "Failed required verifier unexpectedly completed the task.");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("cannot complete without successful verification", StringComparison.Ordinal))
            {
            }

            Check(session.StateMachine.State == AgentTaskState.Verifying,
                "Failed verifier changed host state to a terminal completion state.");
        });

        await Test("MB-44 non-mechanical completion requires explicit host classification evidence", () =>
        {
            var contract = MutationContract(
                requireVerification: true,
                allowNotMechanicallyVerifiable: true);
            var orchestrator = new AgentOrchestrator();
            var session = ToVerifying(orchestrator, contract);

            try
            {
                orchestrator.CompleteNotMechanicallyVerifiable(
                    session,
                    Array.Empty<AgentEvidenceReference>(),
                    "host classified task without evidence");
                throw new InvalidOperationException(
                    "Not-mechanically-verifiable task completed without classification evidence.");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("classification evidence", StringComparison.OrdinalIgnoreCase))
            {
            }

            Check(session.StateMachine.State == AgentTaskState.Verifying,
                "Evidence-free non-mechanical classification changed host completion state.");

            var classification = new AgentEvidenceReference(
                AgentEvidenceKind.HostClassification,
                "host-classification:mb44:manual-review",
                summary:
                    "Host explicitly classified this acceptance path as not mechanically verifiable after reviewing bounded task evidence.");

            orchestrator.CompleteNotMechanicallyVerifiable(
                session,
                [classification],
                "explicit host classification with evidence");

            Check(session.StateMachine.State == AgentTaskState.Completed
                && session.StateMachine.IsTerminal,
                "Explicit host classification evidence did not complete the allowed non-mechanical task.");
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "mb-completion-gate-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static AgentTaskContract MutationContract(
        bool requireVerification,
        bool allowNotMechanicallyVerifiable,
        IEnumerable<string>? verifierIds = null)
        => new(
            Guid.NewGuid(),
            "Mutate the MB-44 fixture and satisfy host completion policy.",
            "fixture:mb44",
            null,
            ["mutate fixture"],
            ["preserve unrelated state"],
            ["report the host-owned final state"],
            [new AgentAcceptanceCriterion("result", "Requested mutation satisfies the task contract.")],
            AgentTaskRiskClass.Medium,
            new AgentVerificationPolicy(
                requireVerification,
                allowNotMechanicallyVerifiable,
                verifierIds));

    private static AgentRuntimeRequest Request(AgentTaskContract contract)
        => new(
            contract,
            contract.UserGoal,
            new AgentPromptStablePrefix(
                AgentVersions.Current,
                "BASE POLICY",
                "SECURITY POLICY",
                "MODEL POLICY",
                ""),
            new AgentContextInput(
                TaskContract: contract.UserGoal,
                CurrentState: "MB-44 completion gate fixture"),
            PromptCacheKey: "mb44",
            MaxToolRounds: 2,
            MaxRepairRounds: 1);

    private static AgentOrchestrationSession ToVerifying(
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

    private sealed class DirectFinalTransport : IAgentTransport
    {
        private readonly string _finalText;

        public DirectFinalTransport(string finalText)
        {
            _finalText = finalText;
        }

        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return AgentTransportEvent.Started("mb44");
            yield return AgentTransportEvent.TextDeltaEvent(_finalText);
            await Task.Yield();
            yield return AgentTransportEvent.Complete("mb44", "stop");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            throw new InvalidOperationException(
                "MB-44 direct-final fixture must not enter a continuation round.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public void Cancel()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

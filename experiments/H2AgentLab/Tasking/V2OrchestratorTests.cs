namespace H2AgentLab.Tasking;

public static class V2OrchestratorTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new v2 orchestrator test directory.");
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

        static AgentTaskContract Contract(
            string goal,
            IEnumerable<string>? inputs = null,
            IEnumerable<string>? changes = null,
            AgentTaskRiskClass risk = AgentTaskRiskClass.ReadOnly,
            bool requireVerification = false,
            IEnumerable<string>? verifierIds = null)
            => new(
                Guid.NewGuid(),
                goal,
                "workspace:/fixture",
                inputs,
                changes,
                ["preserve unrelated state"],
                ["requested result"],
                [new AgentAcceptanceCriterion("result", "Requested result satisfies the task contract.")],
                risk,
                new AgentVerificationPolicy(
                    requireVerification: requireVerification,
                    requiredVerifierIds: verifierIds));

        await Test("Direct route completes through host state machine without heavy tools", () =>
        {
            var orchestrator = new AgentOrchestrator();
            var session = orchestrator.Receive(Contract("Answer grounded question"));

            Check(session.Route.RouteClass == AgentTaskRouteClass.Direct, "Direct task was not routed Direct.");
            Check(session.Route.InitialToolNamespaces.Count == 0, "Direct route preloaded tool namespaces.");

            orchestrator.Ground(session);
            orchestrator.Plan(session);
            orchestrator.Execute(session);
            orchestrator.Verify(session);
            orchestrator.Complete(session, new AgentVerificationOutcome(passed: false));

            Check(session.StateMachine.State == AgentTaskState.Completed, "Direct task did not complete.");
            return Task.CompletedTask;
        });

        await Test("Retrieval route stays retrieval-only and reaches verified completion", () =>
        {
            var orchestrator = new AgentOrchestrator();
            var session = orchestrator.Receive(
                Contract("Read source", inputs: ["artifact-123"]),
                new AgentTaskRoutingSignals(NeedsExternalRetrieval: true));

            Check(session.Route.RouteClass == AgentTaskRouteClass.Retrieval, "Retrieval task was misrouted.");
            Check(session.Route.InitialToolNamespaces.SequenceEqual(new[] { "files" }),
                "Retrieval route loaded namespaces beyond files.");

            orchestrator.Ground(session);
            orchestrator.Plan(session);
            orchestrator.Execute(session);
            orchestrator.Verify(session);
            orchestrator.Complete(session, new AgentVerificationOutcome(passed: false));

            Check(session.StateMachine.State == AgentTaskState.Completed, "Retrieval task did not complete.");
            return Task.CompletedTask;
        });

        await Test("Action route requires verifier before mutating completion", () =>
        {
            var orchestrator = new AgentOrchestrator();
            var contract = Contract(
                "Modify target",
                inputs: ["target.txt"],
                changes: ["update target"],
                risk: AgentTaskRiskClass.Medium,
                requireVerification: true,
                verifierIds: ["mutation-verify"]);
            var session = orchestrator.Receive(contract);

            Check(session.Route.RouteClass == AgentTaskRouteClass.Action, "Mutating task was not routed Action.");

            orchestrator.Ground(session);
            orchestrator.Plan(session);
            orchestrator.Execute(session);
            orchestrator.Verify(session);

            try
            {
                orchestrator.Complete(session, new AgentVerificationOutcome(passed: true));
                throw new InvalidOperationException("Action completed without required verifier.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("missing required verifier", StringComparison.Ordinal))
            {
            }

            orchestrator.Complete(
                session,
                new AgentVerificationOutcome(passed: true, verifierIds: ["mutation-verify"]));

            Check(session.StateMachine.State == AgentTaskState.Completed, "Verified action did not complete.");
            return Task.CompletedTask;
        });

        await Test("Repair loop returns Verifying failure to Executing and completes after re-verification", () =>
        {
            var orchestrator = new AgentOrchestrator();
            var session = orchestrator.Receive(Contract(
                "Repair target",
                changes: ["repair target"],
                risk: AgentTaskRiskClass.Low,
                requireVerification: true,
                verifierIds: ["repair-verify"]));

            orchestrator.Ground(session);
            orchestrator.Plan(session);
            orchestrator.Execute(session);
            orchestrator.Verify(session);
            orchestrator.Repair(session, "first verification failed");
            orchestrator.Execute(session, "repair applied");
            orchestrator.Verify(session, "re-verify");
            orchestrator.Complete(
                session,
                new AgentVerificationOutcome(passed: true, verifierIds: ["repair-verify"]));

            var path = session.StateMachine.History.Select(x => x.To).ToArray();
            var expected = new[]
            {
                AgentTaskState.Grounded,
                AgentTaskState.Planned,
                AgentTaskState.Executing,
                AgentTaskState.Verifying,
                AgentTaskState.Repairing,
                AgentTaskState.Executing,
                AgentTaskState.Verifying,
                AgentTaskState.Completed
            };
            Check(path.SequenceEqual(expected), "Repair transition path is not deterministic.");
            return Task.CompletedTask;
        });

        await Test("Cancelled task becomes terminal and cannot resume", () =>
        {
            var orchestrator = new AgentOrchestrator();
            var session = orchestrator.Receive(Contract("Cancel fixture"));
            orchestrator.Ground(session);
            orchestrator.Cancel(session, "user cancelled");

            Check(session.StateMachine.State == AgentTaskState.Cancelled && session.StateMachine.IsTerminal,
                "Cancelled task is not terminal.");

            try
            {
                orchestrator.Plan(session);
                throw new InvalidOperationException("Cancelled task resumed.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Illegal task transition", StringComparison.Ordinal))
            {
            }

            return Task.CompletedTask;
        });

        await Test("Blocked task becomes terminal and cannot execute", () =>
        {
            var orchestrator = new AgentOrchestrator();
            var session = orchestrator.Receive(Contract("Blocked fixture"));
            orchestrator.Ground(session);
            orchestrator.Block(session, "required input unavailable");

            Check(session.StateMachine.State == AgentTaskState.Blocked && session.StateMachine.IsTerminal,
                "Blocked task is not terminal.");

            try
            {
                orchestrator.Execute(session);
                throw new InvalidOperationException("Blocked task executed.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Illegal task transition", StringComparison.Ordinal))
            {
            }

            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "v2-orchestrator-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join("\n", lines));
        return failed == 0 ? 0 : 1;
    }
}

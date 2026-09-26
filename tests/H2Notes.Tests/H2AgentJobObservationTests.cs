using System.Reflection;
using System.Text.Json;
using H2AgentLab;
using H2AgentLab.Runtime;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2AgentLab.Verification;
using H2Notes.Core;

/// <summary>E1 control policy. Synthetic host observations here are never native process evidence.</summary>
internal static class H2AgentJobObservationTests
{
    public static void Run(Action<string, Action> test)
    {
        foreach (var mismatch in new[] { "job", "invocation", "revision", "effect", "still-running", "new-revision" })
            test("AR-040 host terminal observation rejects mismatched " + mismatch, () =>
            {
                var f = Fixture();
                var output = new ToolExecutionOutput("{}", f.Outcome with { Status = ToolOutcomeStatus.Succeeded, Completeness = new(true) });
                var update = new AgentRuntimeJobObservation(f.Call.Invocation!.InvocationId, "fixture-job", f.Contract.Goals!.RevisionId, output);
                if (mismatch == "job") update = update with { JobId = "foreign-job" };
                if (mismatch == "invocation") update = update with { InvocationId = Guid.NewGuid() };
                if (mismatch == "revision") update = update with { GoalRevisionId = "foreign-revision" };
                if (mismatch == "effect") update = update with { Output = output with { Outcome = output.Outcome with { Effect = ToolMutationEffect.None } } };
                if (mismatch == "still-running") update = update with { Output = output with { Outcome = f.Outcome } };
                var current = mismatch == "new-revision" ? f.Contract.WithUserInput(new(Guid.NewGuid(), "A further user correction")) : f.Contract;
                Reject(() => Invoke(f.Index, "Observe", new AgentRuntimeJobObservation[] { update }, current, f.Registry));
                var valid = new AgentRuntimeJobObservation(f.Call.Invocation.InvocationId, "fixture-job", f.Contract.Goals.RevisionId, output);
                // Rejected identity does not silently consume the pending invocation.
                _ = Invoke(f.Index, "Observe", new[] { valid }, f.Contract, f.Registry);
            });
        test("AR-040 host terminal observation is journaled once under original dispatch identity", () =>
        {
            var journal = new List<H2AgentOperationRecord>(); var f = Fixture(journal.Add);
            var original = journal.Last();
            var update = new AgentRuntimeJobObservation(f.Call.Invocation!.InvocationId, "fixture-job", f.Contract.Goals!.RevisionId,
                new("terminal", f.Outcome with { Status = ToolOutcomeStatus.Succeeded, Completeness = new(true) }));
            _ = Invoke(f.Index, "Observe", new[] { update }, f.Contract, f.Registry);
            Check(journal.Count == 2 && journal.Last().InvocationId == original.InvocationId
                && journal.Last().ArgumentsSha256 == original.ArgumentsSha256 && journal.Last().Status == "Succeeded"
                && journal.Last().OutputSha256 is { Length: 64 }, "Terminal journal rewrote original operation or did not hash output.");
            Reject(() => Invoke(f.Index, "Observe", new[] { update }, f.Contract, f.Registry));
            Check(journal.Count == 2, "Duplicate terminal observation was persisted twice.");
        });
        test("AR-040 job terminal status cannot self-award semantic verification", () =>
        {
            var f = Fixture(); var assessment = new AgentCompletionAssessment(true);
            var context = Context(f.Contract, f.Call, f.Outcome); assessment.Register(context);
            var terminal = context with { Results = [new AgentToolResult(f.Call.Id, f.Call.Name, "{}", false)
                { Outcome = f.Outcome with { Status = ToolOutcomeStatus.Succeeded, Completeness = new(true) } }] };
            assessment.ObserveJob(terminal);
            Check(assessment.UnverifiedMutations == 1 && !assessment.ProofIds.Any(), "Native exit status was treated as verifier proof.");
        });
        test("AR-040 durable receipt failure keeps pending observation and cannot drop its identity", () =>
        {
            var fail = false;
            var f = Fixture(_ => { if (fail) throw new IOException("controlled journal error"); });
            var update = new AgentRuntimeJobObservation(f.Call.Invocation!.InvocationId, "fixture-job", f.Contract.Goals!.RevisionId,
                new("{}", f.Outcome with { Status = ToolOutcomeStatus.Succeeded, Completeness = new(true) }));
            fail = true; Reject(() => Invoke(f.Index, "Observe", new[] { update }, f.Contract, f.Registry));
            fail = false; _ = Invoke(f.Index, "Observe", new[] { update }, f.Contract, f.Registry);
        });
        test("AR-040 scheduler only releases a bound successful job and retains failed journal fences", () =>
        {
            foreach (var loseReceipt in new[] { false, true })
            {
                var f = Fixture(); var count = 0;
                var descriptor = new ToolDescriptor(f.Call.Name, new("fixture", "Controlled"), "Controlled job fixture",
                    AgentToolRisk.High, AgentToolAccess.Mutating, false, "v1", JsonSerializer.SerializeToElement(new { }),
                    new DelegatingOutcomeToolExecutor("fixture", (call, _) =>
                    {
                        count++;
                        return ValueTask.FromResult(new ToolExecutionOutput("{}", f.Outcome with { Invocation = call.Invocation! }));
                    }), resourceScope: new("fixture", "fixed"));
                using var scheduler = new ToolExecutionScheduler();
                var request = new ToolExecutionRequest(descriptor, f.Call, "fixed")
                { AfterExecute = loseReceipt ? (_, _) => throw new IOException("receipt lost") : null };
                try { _ = scheduler.ExecuteBatchAsync([request], default).GetAwaiter().GetResult(); }
                catch (IOException) when (loseReceipt) { }
                var method = typeof(ToolExecutionScheduler).GetMethod("ObserveCompletedJob", BindingFlags.Instance | BindingFlags.NonPublic)!;
                method.Invoke(scheduler, [f.Outcome with { Status = ToolOutcomeStatus.Succeeded, Completeness = new(true) }]);
                var next = f.Call with { Id = "next", Invocation = ToolInvocation.Create(f.Contract.TaskId, f.Call.Name, f.Call.Arguments) };
                _ = scheduler.ExecuteBatchAsync([new(descriptor, next, "fixed")], default).GetAwaiter().GetResult();
                Check(count == (loseReceipt ? 1 : 2), "Lost receipt fence was released or a valid completed job remained permanently fenced.");
            }
        });
    }
    private static (object Index, AgentTaskContract Contract, ToolCall Call, ToolOutcome Outcome, ToolRegistry Registry) Fixture(Action<H2AgentOperationRecord>? journal = null)
    {
        var contract = new AgentTaskContract(Guid.NewGuid(), "Controlled job", "fixture", [], [], [], [],
            [new AgentAcceptanceCriterion("fixture-criterion", "Controlled requirement")], AgentTaskRiskClass.ReadOnly,
            new AgentVerificationPolicy(true), mutationAllowed: true).WithUserInput(new(Guid.NewGuid(), "Hello"));
        var call = new ToolCall("first", "fixture_job", JsonSerializer.SerializeToElement(new { value = 1 }));
        call = call with { Invocation = ToolInvocation.Create(contract.TaskId, call.Name, call.Arguments) };
        var outcome = ToolOutcome.Success(call, ToolMutationEffect.Applied, new(false)) with { Status = ToolOutcomeStatus.Running, Job = new("fixture-job") };
        var index = Activator.CreateInstance(typeof(AgentRuntime).Assembly.GetType("H2AgentLab.Runtime.AgentRuntimeJobs")!, [journal])!;
        var record = new H2AgentOperationRecord(call.Invocation!.InvocationId, call.Invocation.LogicalOperationId, Guid.NewGuid(),
            contract.Goals!.RevisionId, call.Id, call.Name, "Result", "Running", "Applied", new string('a', 64), null, null, null);
        _ = Invoke(index, "Record", record);
        _ = Invoke(index, "Register", Context(contract, call, outcome));
        var registry = new ToolRegistry();
        registry.Register(new ToolDescriptor(call.Name, new("fixture", "Controlled"), "Controlled job fixture", AgentToolRisk.High,
            AgentToolAccess.Mutating, false, "v1", JsonSerializer.SerializeToElement(new { }),
            new DelegatingToolExecutor("fixture", (_, _) => ValueTask.FromResult("{}"))));
        return (index, contract, call, outcome, registry);
    }
    private static AgentRuntimeVerificationContext Context(AgentTaskContract contract, ToolCall call, ToolOutcome outcome)
        => new(contract, 1, [call], [new AgentToolResult(call.Id, call.Name, "{}", false) { Outcome = outcome }]) { MutationCallIds = [call.Id] };
    private static object? Invoke(object target, string method, params object[] args)
    {
        try { return target.GetType().GetMethod(method)!.Invoke(target, args); }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is InvalidOperationException or IOException) { return; }
        throw new InvalidOperationException("Expected a rejected host observation.");
    }
    private static void Check(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
}

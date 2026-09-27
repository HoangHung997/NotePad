using System.Security.Cryptography;
using System.Text;
using H2AgentLab.Integration;
using H2Notes.Core;

internal static class H2AgentRestartReconcileTests
{
    public static void Run(Action<string,Action> test)
    {
        test("AR-041 restart dispatched mutation stays blocked until exact host observation verifies it", () =>
            Fixture(root =>
            {
                var seeded=SeedDispatched(root,"fixture-resource");
                using(var adapter=Adapter(root))
                {
                    var before=adapter.GetTaskSummary(seeded.TaskId);
                    Check(before.Status==H2AgentTaskStatus.Blocked
                        && before.Recovery is { Interrupted:true, ReconcileRequired:true }
                        && before.Recovery.Operations.Single().State=="Dispatched",
                        "Restart did not project dispatched mutation as interrupted/reconcile-required.");

                    var observed=new H2AgentReconcileObservation(
                        seeded.Operation.InvocationId,H2AgentReconcileDisposition.Verified,
                        "fixture-resource","v2","host observed exact postcondition",DateTime.UtcNow);
                    var result=adapter.ReconcileInterruptedTask(seeded.TaskId,[observed]);
                    Check(result.State=="ReadyForResume"&&!result.ReconcileRequired&&result.RequiresFreshPermission,
                        "Verified restart observation did not clear reconciliation while requiring fresh permission.");
                    var operation=result.Operations.Single();
                    Check(operation.ReconciliationState=="Verified"
                        && operation.ReconciliationEvidenceId is not null
                        && operation.ReconciledObservedVersion=="v2",
                        "Verified restart receipt was not persisted on the exact operation.");

                    var after=adapter.GetTaskSummary(seeded.TaskId);
                    Check(after.Status==H2AgentTaskStatus.Blocked
                        && after.Recovery is { Interrupted:true, ReconcileRequired:false },
                        "Reconciliation incorrectly auto-resumed the interrupted task.");
                    Check(after.Evidence.Any(e=>e.EvidenceId==operation.ReconciliationEvidenceId
                        && e.Provenance=="H2AgentLab.Integration.RestartReconcile"
                        && e.VerificationPassed==true),
                        "Host reconciliation evidence was not projected into the task.");

                    var seq=adapter.GetArchiveStatus().LastSequence;
                    var again=adapter.ReconcileInterruptedTask(seeded.TaskId,[observed]);
                    Check(again.State=="ReadyForResume"&&adapter.GetArchiveStatus().LastSequence==seq,
                        "Duplicate verified host observation was not idempotent.");
                    Check(!adapter.RespondToApproval(seeded.TaskId,Guid.NewGuid(),true)
                        && !adapter.SupplementTask(seeded.TaskId,Guid.NewGuid(),"continue"),
                        "Archived restart state accidentally restored old approval/input authority.");
                    adapter.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                using var reopened=Adapter(root);
                var durable=reopened.GetTaskSummary(seeded.TaskId);
                Check(durable.Recovery is { Interrupted:true, ReconcileRequired:false }
                    && durable.Recovery.Operations.Single().ReconciliationState=="Verified",
                    "Verified reconciliation did not survive another host restart.");
                reopened.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }));

        test("AR-041 wrong resource rebind cannot verify unknown mutation and later exact rebind is safe", () =>
            Fixture(root =>
            {
                var seeded=SeedDispatched(root,"resource-A");
                using var adapter=Adapter(root);
                var wrong=adapter.ReconcileInterruptedTask(seeded.TaskId,
                [
                    new(seeded.Operation.InvocationId,H2AgentReconcileDisposition.Verified,
                        "resource-B","wrong-v","wrong target",DateTime.UtcNow)
                ]);
                var blocked=wrong.Operations.Single();
                Check(wrong.State=="NeedsUser"&&wrong.ReconcileRequired
                    && blocked.ReconciliationState=="NeedsUserResourceMismatch"
                    && blocked.ErrorCode=="resource_rebind_mismatch"
                    && blocked.ReconciliationEvidenceId is null,
                    "Wrong resource identity was accepted as verified restart evidence.");

                var rebound=adapter.ReconcileInterruptedTask(seeded.TaskId,
                [
                    new(seeded.Operation.InvocationId,H2AgentReconcileDisposition.Verified,
                        "resource-A","exact-v","exact rebind",DateTime.UtcNow)
                ]);
                Check(rebound.State=="ReadyForResume"&&!rebound.ReconcileRequired
                    && rebound.RequiresFreshPermission
                    && rebound.Operations.Single().ReconciliationState=="Verified",
                    "Exact resource rebind could not replace a prior mismatch observation.");
                adapter.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }));

        test("AR-041 applied result after interrupted task remains AppliedUnverified until independent verification", () =>
            Fixture(root =>
            {
                var seeded=SeedApplied(root,"resource-applied");
                using var adapter=Adapter(root);
                var before=adapter.GetTaskSummary(seeded.TaskId);
                Check(before.Recovery?.ReconcileRequired==true,
                    "Interrupted applied operation was incorrectly treated as already verified.");

                var applied=adapter.ReconcileInterruptedTask(seeded.TaskId,
                [
                    new(seeded.Operation.InvocationId,H2AgentReconcileDisposition.AppliedUnverified,
                        "resource-applied","post-v1","effect exists but verifier not complete",DateTime.UtcNow)
                ]);
                Check(applied.State=="ReconcileRequired"&&applied.ReconcileRequired
                    && applied.Operations.Single().ReconciliationState=="AppliedUnverified",
                    "Applied-but-unverified observation incorrectly allowed resume.");

                var verified=adapter.ReconcileInterruptedTask(seeded.TaskId,
                [
                    new(seeded.Operation.InvocationId,H2AgentReconcileDisposition.Verified,
                        "resource-applied","post-v1","verifier passed",DateTime.UtcNow)
                ]);
                Check(verified.State=="ReadyForResume"&&!verified.ReconcileRequired
                    && verified.RequiresFreshPermission,
                    "Independent verification did not resolve AppliedUnverified state.");
                adapter.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }));

        test("AR-041 prepared-only durable intent proves no executor dispatch after restart", () =>
            Fixture(root =>
            {
                var seeded=SeedPreparedOnly(root,"resource-prepared");
                using var adapter=Adapter(root);
                var before=adapter.GetTaskSummary(seeded.TaskId);
                Check(before.Recovery is { Interrupted:true }
                    && before.Recovery.Operations.Single().State=="Prepared",
                    "Prepared-only crash receipt was not recovered from the journal.");

                var result=adapter.ReconcileInterruptedTask(seeded.TaskId,[]);
                var operation=result.Operations.Single();
                Check(result.State=="ReadyForResume"&&!result.ReconcileRequired&&result.RequiresFreshPermission
                    && operation.ReconciliationState=="NoEffect"
                    && operation.Status=="RejectedBeforeEffect"
                    && operation.Effect=="None"
                    && operation.ReconciledObservedVersion=="not-dispatched",
                    "Prepared-only intent was not safely finalized as no-effect.");
                adapter.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }));

        test("AR-041 persisted CancelOnHostExit running job is dead after restart and never adopted by PID", () =>
            Fixture(root =>
            {
                var seeded=SeedRunningJob(root,"resource-job");
                using var adapter=Adapter(root);
                var result=adapter.ReconcileInterruptedTask(seeded.TaskId,[]);
                var operation=result.Operations.Single();
                Check(result.State=="NeedsUser"&&result.ReconcileRequired&&result.RequiresFreshPermission
                    && operation.ReconciliationState=="NeedsUserWorkerDead"
                    && operation.ErrorCode=="worker_interrupted_on_host_exit"
                    && operation.Job is { Status:"Running", HostExitPolicy:"CancelOnHostExit" },
                    "Restart incorrectly adopted a stale running PID/job as live work.");
                adapter.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }));

        test("AR-041 user/repair dispositions remain durable blockers and never become replay permission", () =>
            Fixture(root =>
            {
                var seeded=SeedDispatched(root,"resource-repair");
                using var adapter=Adapter(root);
                var repair=adapter.ReconcileInterruptedTask(seeded.TaskId,
                [
                    new(seeded.Operation.InvocationId,H2AgentReconcileDisposition.RepairRequired,
                        "resource-repair","repair-v","partial postcondition",DateTime.UtcNow)
                ]);
                Check(repair.State=="RepairRequired"&&repair.ReconcileRequired&&repair.RequiresFreshPermission
                    && repair.Operations.Single().ReconciliationState=="RepairRequired",
                    "RepairRequired observation incorrectly became a retry instruction.");

                var needsUser=adapter.ReconcileInterruptedTask(seeded.TaskId,
                [
                    new(seeded.Operation.InvocationId,H2AgentReconcileDisposition.NeedsUser,
                        "resource-repair",null,"cannot establish postcondition",DateTime.UtcNow)
                ]);
                Check(needsUser.State=="NeedsUser"&&needsUser.ReconcileRequired&&needsUser.RequiresFreshPermission
                    && needsUser.Operations.Single().ReconciliationState=="NeedsUser",
                    "NeedsUser observation incorrectly cleared uncertainty.");
                adapter.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }));
    }

    private static Seeded SeedDispatched(string root,string resource)
    {
        var task=Summary();
        var operation=Operation(task,resource);
        using var archive=new AgentIntegrationTaskArchive(Path.Combine(root,"integration"));
        archive.Upsert(task);
        archive.RecordOperation(task.TaskId,operation);
        return new(task.TaskId,operation);
    }

    private static Seeded SeedApplied(string root,string resource)
    {
        var task=Summary();
        var operation=Operation(task,resource);
        using var archive=new AgentIntegrationTaskArchive(Path.Combine(root,"integration"));
        archive.Upsert(task);
        archive.RecordOperation(task.TaskId,operation);
        archive.RecordOperation(task.TaskId,operation with
        {
            State="Result",Status="Succeeded",Effect="Applied",OutputSha256=Hash("result")
        });
        return new(task.TaskId,operation);
    }

    private static Seeded SeedPreparedOnly(string root,string resource)
    {
        var task=Summary();
        var operation=Operation(task,resource);
        var armed=false;var faulted=false;
        using(var archive=new AgentIntegrationTaskArchive(Path.Combine(root,"integration"),fault:step=>
        {
            if(armed&&!faulted&&step=="journal-activated")
            {
                faulted=true;
                throw new IOException("controlled intent-only crash");
            }
        }))
        {
            archive.Upsert(task);
            armed=true;
            try{archive.RecordOperation(task.TaskId,operation);}
            catch(IOException){ }
        }
        Check(faulted,"Prepared-only fixture did not interrupt after durable intent.");
        return new(task.TaskId,operation);
    }

    private static Seeded SeedRunningJob(string root,string resource)
    {
        var task=Summary();
        var operation=Operation(task,resource);
        using var archive=new AgentIntegrationTaskArchive(Path.Combine(root,"integration"));
        archive.Upsert(task);
        archive.RecordOperation(task.TaskId,operation);
        var now=DateTime.UtcNow;
        archive.RecordJob(task.TaskId,operation.InvocationId,new H2AgentProcessJobInfo(
            "job-restart",task.TaskId,operation.GoalRevisionId,4242,
            now.AddSeconds(-5),now.AddMinutes(5),"Running",
            false,false,false,false,null,"CancelOnHostExit",[]));
        return new(task.TaskId,operation);
    }

    private static H2AgentTaskSummary Summary()
    {
        var now=DateTime.UtcNow;
        return new(Guid.NewGuid(),null,"AR-041 restart reconcile fixture",H2AgentTaskStatus.Running,
            null,[],null,null,now,now,Guid.NewGuid(),Guid.NewGuid());
    }

    private static H2AgentOperationRecord Operation(H2AgentTaskSummary task,string resource)
        => new(Guid.NewGuid(),"logical-"+Guid.NewGuid().ToString("N"),task.TurnId!.Value,
            "revision-ar041","call-ar041","fixture.write","Dispatched","NotKnown","Unknown",
            Hash("{}"),Hash(resource),null,null);

    private static H2ProductionAgentAdapter Adapter(string root)
        => new(root,()=>new(new AiProfile
        {
            Model="ar041-no-network",
            BaseUrl="https://example.test/v1",
            Protocol=AiProtocol.OpenAiChat
        },""));

    private static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static void Fixture(Action<string> action)
    {
        var root=Path.Combine(Path.GetTempPath(),"h2-ar041-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try{action(root);}
        finally{try{Directory.Delete(root,true);}catch{}}
    }

    private static void Check(bool value,string message)
    {
        if(!value)throw new InvalidOperationException(message);
    }

    private sealed record Seeded(Guid TaskId,H2AgentOperationRecord Operation);
}

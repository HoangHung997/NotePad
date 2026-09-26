# AR-041 native crash/restart acceptance — deferred

Use final portable artifact **10900553554**, source SHA `ca215bf616bca98f94fafa11f8ec0690bc99e961`.

This acceptance is deferred by the user. Do not record E3 PASS until it is run on an authorized Windows PC against real providers/resources using disposable documents only.

## Required cases

1. Start a bounded Word or Excel mutation against a disposable live document, then terminate H2 after dispatch but before a durable result receipt.
2. Restart H2. The task must appear Interrupted / ReconcileRequired. It must not auto-run the model/tool or replay the mutation.
3. Reopen/rebind the exact same resource and observe the postcondition. Verified observation may clear reconciliation, but the task remains archived/blocked and any later continuation requires a fresh permission scope.
4. Repeat with an effect that exists but has not been semantically verified. It must remain AppliedUnverified until independent verification.
5. Reopen another document / Save As / near-name resource. Its identity must not discharge the old operation; expect NeedsUser/resource mismatch.
6. Expire/revoke the prior permission before restart. Reconciliation may inspect safe postconditions but must not restore the old grant or perform a repair automatically.
7. For a long job whose host policy is CancelOnHostExit, restart must not adopt the persisted PID/job as live work. It must be NeedsUserWorkerDead (or equivalent fail-closed state).
8. For GUI/computer-use mutation, do not replay mouse/keyboard input after restart. Reobserve target/postcondition first.
9. If the postcondition cannot be established, remain NeedsUser/RepairRequired rather than inventing success/failure.
10. Verify a second H2 restart preserves the reconciliation receipt and does not duplicate evidence or execute effects.

## Evidence to capture

Capture build SHA, task/invocation ID, original tool name, resource identity, crash boundary, post-restart recovery projection, reconciliation disposition, observed version, fresh permission decision and before/after disposable resource state.

Do not claim generic exactly-once semantics for COM/GUI providers. AR-041 guarantees fail-closed durable reconciliation and no blind replay, not distributed transactional exactly-once execution.

# AR-042 native multi-task concurrency acceptance — deferred

Use final portable artifact **10903620313**, source SHA `51c74339c05602efc13180dcffbb41a9aed6277b`.

This acceptance is deferred by the user. Do not record E3 PASS until it is executed on an authorized Windows PC using disposable resources only.

## Same-resource native concurrency

1. Open one disposable Excel workbook or Word document.
2. Start two H2 tasks that both mutate the same exact live resource.
3. Hold the first mutation long enough for the second to queue.
4. Verify the second mutation does not overlap the first and that the final document reflects serialized, verified effects rather than lost updates.
5. In parallel, start a mutation against a different disposable resource; verify it is not globally blocked by the first resource's gate.

## Permission expiry/revocation boundary

1. Queue a mutating task behind another task holding the same resource.
2. Let its grant expire or revoke it while it waits.
3. When the resource gate becomes available, H2 must revalidate authorization before durable dispatch/effect.
4. Expected result: denied/no effect/no mutation dispatch receipt. Do not accept an approval that was valid only before the wait.

## Steering while a mutation is in flight

1. Start a task that has already dispatched a bounded mutation.
2. Submit a user steering correction with an explicit InputId while the mutation is still in flight.
3. The in-flight operation must retain its original GoalRevisionId.
4. The new requirement applies only at the next safe boundary; it must not rewrite the original operation receipt.
5. Submit the same InputId/text again: one ACK/one revision only.
6. Submit the same InputId with different text: reject it.

## Queue cancellation and reconnect

1. Start a long-running owner task, then queue another turn behind it.
2. Cancel only the queued task. The running owner must continue; no foreign process/resource may be cancelled.
3. Reconnect/re-submit the same UI turn with the same TurnId. It must return the existing TaskId and must not allocate another model/tool run.
4. Reuse the TurnId with a conflicting goal/thread/project and confirm fail-closed behavior.

## Uncertain effect fence

1. Cause a real provider/native mutation to lose its result after possible effect.
2. While its resource is uncertain, start another task targeting the same resource.
3. H2 must block the second mutation until AR-041 reconciliation or equivalent host observation clears the uncertainty.

## Evidence to capture

Capture build SHA, task IDs, TurnIds, steering InputIds, resource identities, permission issue/expiry/revoke timestamps, original and steered GoalRevisionIds, dispatch/result receipts, final native resource state and any reconciliation evidence.

The local mutation coordinator is not a distributed lock and does not replace Coordinator/NAS fencing. Multi-PC acceptance remains AR-083/E5.

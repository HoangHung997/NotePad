# AR-042 — Safe steering, cancellation and cross-task concurrency

Validated code: `51c74339c05602efc13180dcffbb41a9aed6277b`.

## Implemented behavior

- Production runtime instances created by one H2 adapter share a host-local mutation coordinator.
- Mutations to the same exact resource are serialized across tasks; distinct resources remain parallel.
- Ambiguous/unknown mutation effects establish a shared resource fence so another task cannot blindly write that same resource.
- Approval/user interaction occurs before entering the shared mutation gate.
- After a queued mutation acquires its resource gate, permission/scope is revalidated before durable dispatch and before executor effect.
- An already-dispatched mutation retains its original GoalRevisionId. Later steering is prospective and cannot rewrite dispatch history.
- TurnId is an idempotency key across live and durable tasks: reconnect/retry returns the existing TaskId; conflicting reuse fails closed.
- Steering InputId is journaled before runtime exposure. The durable steering receipt stores only InputId, SHA-256 and UTC, never raw steering text.
- Duplicate steering with identical InputId/text is acknowledged idempotently; identical InputId with changed text is rejected.
- Queued cancellation remains owner-scoped and cannot cancel or start another task.

## Concurrency boundary

The mutation coordinator is local to one H2 host/runtime-factory composition. It is not a distributed lock, NAS lease or Coordinator fence. Coordinator/multi-PC ownership remains separately governed and E5 remains deferred.

## Validation

Exact source: `51c74339c05602efc13180dcffbb41a9aed6277b`.

- AR-042 focused: 6/6.
- retained AR-041: 6/6.
- retained AR-040: 57/57.
- retained AR-031: 39/39.
- retained AR-030: 39/39.
- retained AR-024: 2/2.
- full H2: 1323/1323.
- required Agent suites: PASS.
- dedicated AR-042 run 36231762267 / job 108376125152: SUCCESS.
- full Avalonia CI 36231762357 / job 108376125362: SUCCESS.
- all 18 exact-SHA workflow identities: SUCCESS after same-SHA AR-021 rerun.
- self-contained Windows x64 publish and packaged DesktopHost/OfficeHost IPC: PASS.

## Acceptance boundary

E1/E2 are PASS. Real E3 native Office/GUI multi-task concurrency is DEFERRED_BY_USER / AWAITING_ENVIRONMENT until the user tests the final build. This implementation does not claim E3, E5, cross-machine serialization, or a distributed lock.

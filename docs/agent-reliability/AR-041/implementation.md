# AR-041 — Restart reconciliation for uncertain mutations

Validated code: `ca215bf616bca98f94fafa11f8ec0690bc99e961`.

## Architecture

AR-041 extends the existing AR-031 immutable local Agent journal. It does not add a recovery database, second Agent state store, auto-resume engine, or a generic exactly-once promise.

The H2 adapter exposes a typed restart-reconciliation operation. Reconciliation accepts only host-owned observations made after restart. It records a reconciliation receipt against the original durable operation identity; it never runs the tool/model and never treats the observation as permission to retry.

## State rules

- Prepared-only: executor was never dispatched; finalize as NoEffect.
- Dispatched / unknown effect: remain ReconcileRequired until the host reobserves the exact resource.
- Applied after interrupted task: AppliedUnverified until independent verification proves the postcondition.
- Wrong resource identity: NeedsUserResourceMismatch.
- RepairRequired: remains a blocker; it is not a retry command.
- NeedsUser: remains a blocker.
- CancelOnHostExit + persisted Running job: NeedsUserWorkerDead; do not adopt the saved PID/job as live.
- Verified / NoEffect receipt: idempotent only for the same resource/version; terminal receipt cannot be rebound or rewritten.
- Reconciliation always reports RequiresFreshPermission=true. The archived task remains Interrupted/Blocked; AR-052 may later resume from canonical state under a fresh permission/resource binding.

Only typed disposition, durable operation identity and bounded observed-version metadata are persisted. Free-form observation notes/raw document output are not persisted.

## Validation

Exact source `ca215bf616bca98f94fafa11f8ec0690bc99e961`.

- AR-041 focused: 6/6.
- retained AR-031: 39/39.
- retained AR-040: 57/57.
- retained AR-024: 2/2.
- retained AR-033: 42/42.
- retained AR-022: 8/8.
- full H2: 1317/1317.
- required Agent suites: 75/75.
- dedicated AR-041 run 36225962252 / job 108359938339: SUCCESS.
- full Avalonia CI 36225962431 / job 108359938825: SUCCESS.
- all 17 pull-request workflows on the exact SHA: SUCCESS.
- Windows x64 self-contained publish and packaged DesktopHost/OfficeHost IPC: PASS.

## Acceptance boundary

E1/E2 are PASS. Native E3 controlled crash/restart against real Office/GUI/live-resource providers is DEFERRED_BY_USER / AWAITING_ENVIRONMENT until final-build testing. No E3 PASS, generic exactly-once, blind replay, stale permission restoration or mouse-input replay claim is made.

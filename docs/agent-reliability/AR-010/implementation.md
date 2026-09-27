# AR-010 — shared production/Lab runtime boundaries

Status: IMPLEMENTED / E2_PASS / DONE on 14767f0fdaf3ead109867c41caf34111adb8c02a.

## Scope

Existing AgentRuntime, AgentRuntimeFactory, AgentOrchestrator, bounded AgentContextManager and production adapter remain the only normal path. Global/Project invocation identity is immutable per runtime turn and is metadata, not a permission grant. Lab retains its existing CompactionManager path. No ProjectRecord state, queue, NAS protocol, replacement engine, new provider credential or endpoint is added.

The shared concrete AgentRuntimeHooks implementation validates task/turn and call/result identity and records content-free events through existing AgentRunTelemetry. Every engine Start/Continue passes one awaited pre-request boundary, including supplemental messages and the completion-repair continuation. Tool observations are hooked before recovery bookkeeping can reject a repeated failure. BeforeCompletion is only a candidate hook; every existing verification/unresolved-call gate still runs afterwards. Hook cancellation/errors stop the current run.

Checkpoint hooks identify actual context-prepared, tool-batch-observed and completion-validated safe boundaries. Only the already-persisted Lab CompactionManager checkpoint carries a durable ID; ordinary production boundaries are explicitly IN_MEMORY. This task does not implement AR-031 durability, AR-050 serialized wire budgeting, AR-051 semantic compaction or AR-041 resume. B04–B10 and MB-124–127 are not closed here.

## Registered E1/E2 tests

H2AgentRuntimeHookTests is registered in H2Notes.Tests/Program.cs and supports the existing --filter AR-010 runner. RC-01 sends one identical goal through Global, Project and the actual Lab facade, reads distinct real temporary marker files through the concrete registry, checks all four hook types in execution order, actual provider sends/deferred schemas, task-local identities and telemetry redaction. Separate tests cover a real Lab compaction checkpoint, supplemental and completion-repair continuations, cancellation at each awaited hook, and fail-closed hook errors. All models are scripted/no-network; only the failing-command regression starts an owned PowerShell process with a five-second budget. No personal file or external service is used.

## Baseline and delivery

Main 1283bc13e07c3cd47d04886166de3dfc595422c0; accepted AR-001 source f3ebc4d336b8d6752436412840675fb2e7204e1d; existing PR #3 branch feature/h2-agent-reliability-ar-000. Current text snapshot e0a2935d565ca5fe83b68f743c0b3ab3ef9a0736 was hash-verified against the exact-source Actions artifact. All mandatory reference docs except the current tracker are unchanged from the previous task. User-PC working tree is inaccessible. Local container has no .NET/PowerShell or direct GitHub DNS, so no local C# PASS is claimed.

E3/E4 remain NOT_RUN / AWAITING_ENVIRONMENT; AR-083 remains DEFERRED_BY_USER.

## Review repair before acceptance

The initial exact-source build and three focused iterations passed. Further code review found that a throwing/null host-hook factory could leave a newly constructed transport without an owner. Hook construction/validation now precedes registry/provider/transport allocation. Two registered regressions require the original hook error and zero transport allocations for exception/null cases. This change needs its own exact-source test results; no earlier PASS is transferred to it.

## Accepted execution evidence

Full CI **35698496546** passed all required steps on `14767f0fdaf3ead109867c41caf34111adb8c02a`, including Windows publish and packaged-helper IPC. H2 tests: **614 passed, 0 failed**. Focused run **35698479792**: **11/11 AR-010 cases x3**, AR-001 **13/13**, Agent suites **74/74**. Read-only workflow run **35698742558** repeats those results on `d9c3831e65f2616dabbd9a69643bbfd4e2cf265a`. Both downloaded evidence archives had SHA256/ZIP CRC and identities/results/traces verified. Detailed exact steps, artifact hashes, commands and limits are in [acceptance.json](acceptance.json).

The reviewed factory resource-ownership repair is included in these results. No native Office/model/UI or physical two-PC acceptance is claimed. The temporary source-delivery payload/script have been removed; normal AR-010 CI is read-only. SESSION HANDOFF advances only to **AR-011 NOT_STARTED**, on the same branch/PR. No merge is authorized.

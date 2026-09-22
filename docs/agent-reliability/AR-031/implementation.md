# AR-031 — local Agent journal/checkpoint

Status: IMPLEMENTED / E2_PASS / DONE for AR-031 only. Exact evidence and retained limitations follow below.

Extends AgentIntegrationTaskArchive inside the existing Agent store. A v2 source journal uses immutable numbered, flushed, hash-linked events; task, goal revision, progress, verification and operation intent/dispatch/result remain Agent-owned. The existing scheduler calls the optional durable observer while holding the resource gate. Argument/result bodies and credentials are not copied into operation receipts. A result is not a verifier verdict.

A small high-water receipt and an atomic validated checkpoint/index are derived from the source. Startup checks schema, source order, hashes and index equivalence; it replays local records, never tools. Bad source exposes a read-only verified prefix plus recovery diagnostics and retained/quarantine copies. Corrupt/stale checkpoint with intact journal rebuilds. Quotas stop new append before data loss. Recent-index retention deletes no task, journal or referenced artifact. Startup cost is linear in retained journal; this slice does not promise indexed large-history search (AR-032).

Legacy recent/task/progress/thread JSON is read and validated once, then a staged local generation activates. Originals remain unchanged for rollback to the pre-upgrade data only; new v2 activity is not silently backported. Legacy/new writers must not run simultaneously. The v2 archive rejects detected network/reparse roots and holds a one-machine writer lease. This is not a security sandbox against an administrator replacing files, and is not an E5/SMB/WebDAV guarantee. Coordinator remains the only shared-project sequencing/lease authority.

Recovered running tasks show Interrupted; dispatched/no-result operations remain uncertain. No old approval or model credential is restored and no operation is automatically retried. Semantic task completion, durable job workers and uncertain-write resume remain AR-033/040/041.

Tests are registered in H2Notes.Tests. Planned coverage: migration/rollback-source equality, torn/modified/future journal/checkpoint, index reconstruction and bounded retention beyond 200 tasks, actual two-process writer exclusion, child termination at intent/dispatch/effect/result/checkpoint boundaries, exact Global/Project file execution and reload, and corruption blocking provider allocation. Until logs are read these are test definitions, not passing evidence. E3/E4 NOT_RUN; AR-020 native gate remains pending; AR-083 DEFERRED_BY_USER.


## Verified final checkpoint

Tested code `268079a566f3f6f10f31d99852c31bb050d39ec4`. [Exact acceptance manifest](acceptance.json). The Windows validation and full CI ran the actual saved source; no prior green result substitutes for the cancellation repair.

Cancellation now signals outside the task lock even if journal progress fails or the live task has already been marked Blocked. Original storage errors remain primary; a failed write never creates a fake durable cancel receipt. Four concrete cancellation cases pass in three repetitions. The unchanged tests on the prior adapter reproduce three failures, and exact current source is restored before positive tests.

All retained archive/migration/hash/quota/recovery tests, actual same-machine child crashes and writer exclusion, Global/Project fixture paths, AR regressions and 74 Agent suites pass. Full H2 runner: 797 passed, zero failed. Windows publish and packaged-helper startup/IPC pass. Build warnings are not claimed fixed. Fixture/model boundaries remain E1/E2, not E3/E4.

Startup replays validated data, not tool actions. Automatic uncertain-write resume remains AR-041. No Coordinator or ProjectRecord ownership change, parallel store, credential restore or personal-document test. AR-020 awaits native E3; E4 NOT_RUN; AR-083 DEFERRED_BY_USER. Old local cancellation patch packages are superseded; do not reapply.

On the same branch and PR #3, reconcile latest refs, working tree and SESSION HANDOFF. Start only AR-032: scoped TaskId/ThreadId history and evidence retrieval with source IDs, pagination and current unfinished work. Reuse the existing Agent archive and registry; do not add a parallel store or replay unknown writes. Keep AR-020 native E3 pending, AR-083 deferred and retained AR-031/030/020/012/011/010/001 regressions.

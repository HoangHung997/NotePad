# AR-031 — local Agent journal/checkpoint

Status: ACTIVE / NOT_RUN. Implementation must be built and validated on its actual commit before acceptance.

Extends AgentIntegrationTaskArchive inside the existing Agent store. A v2 source journal uses immutable numbered, flushed, hash-linked events; task, goal revision, progress, verification and operation intent/dispatch/result remain Agent-owned. The existing scheduler calls the optional durable observer while holding the resource gate. Argument/result bodies and credentials are not copied into operation receipts. A result is not a verifier verdict.

A small high-water receipt and an atomic validated checkpoint/index are derived from the source. Startup checks schema, source order, hashes and index equivalence; it replays local records, never tools. Bad source exposes a read-only verified prefix plus recovery diagnostics and retained/quarantine copies. Corrupt/stale checkpoint with intact journal rebuilds. Quotas stop new append before data loss. Recent-index retention deletes no task, journal or referenced artifact. Startup cost is linear in retained journal; this slice does not promise indexed large-history search (AR-032).

Legacy recent/task/progress/thread JSON is read and validated once, then a staged local generation activates. Originals remain unchanged for rollback to the pre-upgrade data only; new v2 activity is not silently backported. Legacy/new writers must not run simultaneously. The v2 archive rejects detected network/reparse roots and holds a one-machine writer lease. This is not a security sandbox against an administrator replacing files, and is not an E5/SMB/WebDAV guarantee. Coordinator remains the only shared-project sequencing/lease authority.

Recovered running tasks show Interrupted; dispatched/no-result operations remain uncertain. No old approval or model credential is restored and no operation is automatically retried. Semantic task completion, durable job workers and uncertain-write resume remain AR-033/040/041.

Tests are registered in H2Notes.Tests. Planned coverage: migration/rollback-source equality, torn/modified/future journal/checkpoint, index reconstruction and bounded retention beyond 200 tasks, actual two-process writer exclusion, child termination at intent/dispatch/effect/result/checkpoint boundaries, exact Global/Project file execution and reload, and corruption blocking provider allocation. Until logs are read these are test definitions, not passing evidence. E3/E4 NOT_RUN; AR-020 native gate remains pending; AR-083 DEFERRED_BY_USER.

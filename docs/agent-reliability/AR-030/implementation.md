# AR-030 — outcome obligations and user-sourced goal revisions

Status: ACTIVE / NOT_RUN. This is implementation scope, not acceptance. Uses the existing branch and PR #3. AR-020 remains IMPLEMENTED / AWAITING_ENVIRONMENT; AR-083 remains DEFERRED_BY_USER.

## Source and ownership

Resumed verified checkpoint `1e05be0d9a360975f960165ce6a0dc3c6101b4bb`. Main remains `1283bc13e07c3cd47d04886166de3dfc595422c0`. AR-030 requires accepted AR-010/011, not native Office. The local review snapshot comes from the validated `b270de5` source archive plus the three exact saved checkpoint documents; it is NOT a clone of remote history. No local SDK/PowerShell is available. Source is delivered on the existing branch and tested on Windows CI; no reset/merge/force-push or new provider/model endpoint is needed.

AgentGoalState extends AgentTaskContract with immutable bounded user-source revisions and outcome obligations. Existing IDs, risk promotion, permission checks, registry, verifier, runtime, integration archive, and H2AgentTaskSummary remain the owners. No engine, service, database, ProjectRecord field or H2 TaskRecord is added. GoalState on the public task summary is a read-only archive projection, not an alternate mutable authority.

## Behavior

Explicit compound/list work clauses retain exact requirements, IDs, source-message references, target scope, status and evidence references. Plain conversation keeps only its source/revision; it is not forced into a work checklist. Quoted payloads are not split. The recognizer preserves explicit newline/semicolon/plus clauses; it is a conservative deterministic extractor, not unrestricted semantic understanding. Full original user text remains in source history. Ambiguous replacements are retained as unresolved work, never silently substituted.

User input IDs survive the production supplemental queue. Exact replay is idempotent; same ID/different content is rejected. Quoted exact `Replace "old" with "new"` / `Thay "old" bằng "new"`, and explicit pending-outcome cancellation create source-backed revisions. Superseded/waived items and their evidence remain visible. Unchanged outcomes keep IDs/evidence; changed requirements get new pending IDs. Waiving prospective work never undoes an existing mutation. Scope and grants stay fixed; an explicit new destination in a goal is NOT permission to touch it. Rebinding/grant decisions remain in the existing tool path and AR-042.

A model proposal may append an exact quotation from an existing user source; it cannot remove requirements, waive them, supply verified status or cite another task's source. This contract API is not a new callable tool. Model/tool/document text never enters the trusted user-revision queue. Host verifier reports need a matching active criterion and actual observed evidence references to set Verified; the generic mutation verifier is not semantic coverage of all outcomes. No final model text changes this state.

Runtime observes revisions at the existing supplemental boundaries and records the dispatch revision for mutations. Pending user outcomes block early completion. This is a minimal AR-030 coverage guard, not AR-033's full alternate-approach/job-resolution implementation. Existing provider-specific verifiers still need to report matching outcome criteria for arbitrary semantic work; unsupported coverage remains pending/blocked, not a fabricated PASS. The E2 success fixture deliberately injects a criterion verifier that independently reads exact expected contents of three temporary text files, through the real production/file runtime.

The existing Agent archive stores the public source/revision/status projection, including blocked tasks; old records without it remain readable. This does NOT implement AR-031's journal, corruption recovery/migration, restart reconciliation, automatic resume, durable jobs or long-term retrieval. No UI layout was changed; existing goal progress is public source/status, not hidden reasoning.

## Registered evidence

H2AgentGoalRevisionTests runs from the existing H2 test runner. It covers RC-11/12, task/criterion isolation, input replay/conflict, source authority, exact supersession, pending waiver vs undo, bounded history, stale/no-evidence reports, concrete Global/Project file effects, untrusted retrieved cancel text, typed archive roundtrip and lightweight conversation. Its model transport is scripted and never connects to the configured placeholder address; a semantic criterion verifier is injected only in the explicitly labeled successful three-file fixture.

The read-only AR-030 workflow builds, runs an extraction-only mutation control (not an original-code checkout), restores exact checkout bytes, repeats the actual positive corpus three times, and retains AR-020/012/011/010/001 and all mandatory Agent suites. Full required Avalonia CI/publish/helper IPC remains separate and unchanged. Do not claim a count or PASS until the exact SHA's logs/artifacts have been read.

E3/E4 remain NOT_RUN; AR-083 DEFERRED_BY_USER. Only AR-030 is actively implemented. MB-124–127 are not closed by this slice.

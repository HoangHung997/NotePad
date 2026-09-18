# H2 Notes — Non-AI Bug Ledger

This is the canonical backlog for **H2 Notes defects, robustness gaps, and non-AI acceptance issues** discovered during real use and review.

## Scope

Include:
- workspace/NAS/multi-PC synchronization;
- persistence/recovery/data integrity;
- desktop/session behavior;
- UI/UX defects not caused by AI;
- portability/configuration defects;
- performance/resource issues not specific to AI;
- regression/test gaps that can hide a real H2 Notes defect.

Exclude:
- model quality;
- AI prompts/context/reasoning;
- local/API provider behavior;
- tool calling/Agent Lab internals;
- AI document/action protocol issues;
- anything tracked as an Agent Lab feature unless it causes a separate H2 Notes runtime defect.

## Mandatory workflow

1. Every newly confirmed H2 Notes non-AI defect gets a unique ID in this file.
2. Never delete an old entry. Change its status and append evidence.
3. Before Agent Lab is mapped back into production H2 Notes (Phase 13), the implementer/reviewer must read this **entire file**.
4. Every OPEN item must end as one of:
   - **FIXED** — exact source SHA + deterministic/real-device evidence;
   - **ACCEPTED_LIMITATION** — explicit user acceptance + documented boundary;
   - **NOT_REPRODUCIBLE** — only after a bounded reproduction attempt with evidence.
5. No H2 Notes production integration is considered complete while a HIGH/CRITICAL OPEN item in this ledger is silently ignored.
6. Real two-PC/NAS evidence must be used for issues whose root cause depends on network-filesystem semantics. Local temp-folder unit tests alone are not sufficient evidence.
7. AI-related defects must not be added here.

---

## Summary

| ID | Severity | Area | Status | Short description |
|---|---|---|---|---|
| H2-NONAI-001 | HIGH | NAS / multi-PC sync | OPEN | Project file and workspace index can be observed with mismatched hashes, blocking PC2 refresh |
| H2-NONAI-002 | HIGH | Persistence / recovery | OPEN | Save journal is deleted before final snapshot verification, weakening rollback after a bad committed generation |
| H2-NONAI-003 | MEDIUM | Diagnostics / sync UX | OPEN | Background NAS refresh swallows actionable exception detail and only shows a generic sync-failed status |
| H2-NONAI-004 | HIGH | NAS protocol compatibility | OPEN-RISK | Multi-PC protocol assumes locking/rename semantics without proving the selected shared filesystem supports them |
| H2-NONAI-005 | HIGH | Recovery / availability | OPEN | Persistent index/file mismatch has no automatic last-known-good or guided self-heal path |
| H2-NONAI-006 | HIGH | Test coverage | OPEN | Multi-PC tests use local filesystem fixtures and do not prove real SMB/NAS lock/visibility behavior |
| H2-NONAI-007 | MEDIUM | Offline durability | OPEN-KNOWN-GAP | No durable local pending-operation queue while NAS is unavailable; crash durability remains incomplete |

---

# H2-NONAI-001 — NAS project/index hash mismatch blocks second-PC refresh

**Severity:** HIGH  
**Status:** OPEN  
**First confirmed:** 2026-09-18  
**Area:** multi-PC NAS synchronization / data integrity

## Observed behavior

Two PCs are configured to use the same shared H2 Notes workspace on NAS.

PC1 saves updated project content. PC2 sees that something changed, but it does not refresh to the new project state. PC2 reports:

```text
Chưa đồng bộ được NAS · vẫn giữ bản đang soạn
```

and the detailed storage error identifies a project file similar to:

```text
Tệp đã thay đổi ngoài app hoặc bị lỗi:
projects/c86f409b7f214579bb498c0ff84ca6c5.h2project.json
```

## Current code path

`ProjectWorkspaceStore.ReadSnapshot()` reads `workspace.h2index.json`, then loads each referenced project/note file and verifies its SHA-256 against the hash stored in the index.

A mismatch causes an `InvalidDataException` before normal multi-PC merge logic runs.

Therefore the failure is **before merge**: PC2 is seeing a workspace generation in which the project file bytes and the index hash do not agree.

## Expected behavior

After PC1 finishes a valid commit, PC2 should observe one consistent committed generation and refresh/merge within the normal sync interval.

PC2 must never permanently observe:

```text
project file = generation N+1
workspace index = generation N
```

or the inverse.

## Root cause status

Exact production root cause is **not yet proven**.

Candidates to verify:
- one PC is running an older/incompatible H2 Notes build;
- another process modifies `.h2project.json` outside the H2 workspace transaction protocol;
- the mapped NAS path exposes delayed/non-atomic cross-file visibility;
- the selected network filesystem does not honor the lock/rename semantics assumed by H2 Notes;
- transaction/recovery ordering leaves a bad generation visible after a failed final validation.

## Required evidence before closure

- reproduce on two physical PCs against the intended NAS;
- record exact H2 Notes build/source SHA on both PCs;
- record actual/expected SHA-256 for the offending project file;
- record the corresponding hash in `workspace.h2index.json`;
- record file timestamps and whether `.h2-transaction.json` exists;
- prove the repaired protocol never exposes a persistent mixed generation.

---

# H2-NONAI-002 — Journal deleted before final snapshot verification

**Severity:** HIGH  
**Status:** OPEN  
**First confirmed:** 2026-09-18  
**Area:** persistence / rollback

## Current code behavior

The current `ProjectWorkspaceStore.SaveIncremental()` sequence is effectively:

```text
acquire commit lock
→ recover previous unfinished transaction
→ read current remote snapshot
→ build next package
→ write transaction journal
→ write changed project/note files
→ write workspace.h2index.json
→ delete transaction journal
→ ReadSnapshot() final validation
```

If final `ReadSnapshot()` throws **after the journal has already been deleted**, the catch path calls recovery, but the journal needed for rollback is no longer present.

## Risk

A final-generation consistency failure can leave the shared workspace in a state that cannot automatically roll back to the previously complete generation.

This ordering is especially risky on shared/network storage where visibility and replacement semantics may differ from a local NTFS test fixture.

## Required direction

Do not delete the transaction recovery record until the new generation has passed final validation.

Preferred designs include:
- validate full committed generation first, then retire journal;
- generation/commit IDs with one final manifest/index commit point;
- last-known-good manifest retained until replacement generation is proven;
- recovery that can deterministically choose old or new complete generation after a crash.

Exact design remains implementation work; do not paper over the issue by simply ignoring hash mismatches.

---

# H2-NONAI-003 — Background sync hides useful failure diagnostics

**Severity:** MEDIUM  
**Status:** OPEN  
**First confirmed:** 2026-09-18  
**Area:** diagnostics / multi-PC UX

## Current code behavior

`App.RefreshSharedWorkspace()` catches generic storage exceptions and reduces them to:

```text
Chưa đồng bộ được NAS · vẫn giữ bản đang soạn
```

The background refresh path does not retain a structured sync-error record containing the exact failing file, expected hash, actual hash, generation/writer identity, or whether the error is likely transient.

## Impact

Users can see that sync failed but cannot determine:
- whether another writer is still committing;
- whether one PC runs stale code;
- whether a single file was externally changed;
- whether the workspace is permanently inconsistent;
- which machine/generation caused the condition.

## Required direction

Keep the safe user-facing status, but also persist bounded diagnostics:
- error type;
- offending relative file;
- expected/actual hash when available;
- local device/writer ID;
- observed index/generation identity;
- timestamp;
- retry count / transient-vs-persistent classification.

Do not expose secrets or unrelated file contents.

---

# H2-NONAI-004 — Shared-filesystem capability assumptions are not verified

**Severity:** HIGH  
**Status:** OPEN-RISK  
**First recorded:** 2026-09-18  
**Area:** NAS protocol compatibility

## Current assumptions

The multi-PC protocol depends on:
- `.h2-commit.lock` opened with exclusive `FileShare.None`;
- temporary-file write + flush;
- replace/move behavior for atomic file publication;
- all participating H2 Notes instances respecting the same transaction protocol.

These semantics may hold on normal SMB shares but are not guaranteed for every mapped `X:` provider such as WebDAV, cloud-sync drives, virtual filesystems, or third-party sync clients.

## Required direction

Before declaring a folder multi-PC-safe, add a bounded capability/protocol probe or explicitly limit supported shared-storage types.

Real NAS acceptance must prove:
- exclusive lock visibility across PCs;
- replacement visibility/order;
- no client-side delayed publish that exposes mixed generations;
- crash/reconnect behavior.

If the share fails the capability probe, fail closed with a clear warning instead of silently enabling shared editing.

---

# H2-NONAI-005 — No self-healing path after a persistent mixed generation

**Severity:** HIGH  
**Status:** OPEN  
**First confirmed:** 2026-09-18  
**Area:** recovery / availability

## Current behavior

If project bytes and the index hash disagree, H2 Notes correctly refuses to consume the inconsistent snapshot and keeps the user's in-memory draft.

However, if the mismatch does not disappear on a later poll, the workspace has no deterministic automatic path to:
- restore a previous known-good generation;
- complete an interrupted generation;
- quarantine one corrupt file while preserving recoverable data;
- guide the user through a safe repair.

## Required direction

Maintain fail-closed data protection, but add deterministic recovery based on durable transaction/generation evidence.

Never “repair” by rewriting the index to whatever bytes happen to be visible without proving which generation is authoritative.

---

# H2-NONAI-006 — Real NAS semantics are not covered by the current automated multi-PC tests

**Severity:** HIGH  
**Status:** OPEN  
**First confirmed:** 2026-09-18  
**Area:** regression/acceptance testing

## Current coverage

The current workspace tests exercise two logical `ProjectWorkspaceStore` instances and merge behavior, including:
- edits from two writers;
- same-field conflict handling;
- remote refresh;
- incremental save;
- transaction rollback;
- external modification rejection.

These tests use a local temporary filesystem.

## Gap

A local temp directory does not prove:
- SMB cross-machine `FileShare.None` behavior;
- network caching/visibility;
- rename/replace guarantees;
- disconnect/reconnect behavior;
- real two-PC timing around index/project publication.

## Required gate

Before final H2 Notes integration acceptance, add a real-device test matrix using the intended NAS/share type.

At minimum:
1. PC1 and PC2 open the same workspace.
2. Alternate rapid saves on different projects.
3. Simultaneous edits to different fields of the same project.
4. Same-field conflict.
5. Kill one writer during transaction.
6. Disconnect NAS during save and reconnect.
7. Verify no persistent index/file hash mismatch.
8. Verify both clients converge without data loss.

---

# H2-NONAI-007 — No durable pending-operation queue while NAS is offline

**Severity:** MEDIUM  
**Status:** OPEN-KNOWN-GAP  
**First recorded:** existing project boundary, retained 2026-09-18  
**Area:** offline durability

## Current boundary

When the shared NAS is fully unavailable, H2 Notes can keep in-memory/local draft state, but there is no durable local pending-operation queue guaranteeing replay after a crash/restart.

## Risk

If the application or PC crashes while changes cannot be committed to NAS, unsynchronized edits may not have a durable operation log sufficient for deterministic replay.

## Required direction

Add an append-only local pending-operation/recovery record with:
- stable operation identity;
- target entity IDs;
- base/generation identity;
- durable local flush;
- observe-before-replay after reconnect;
- idempotent replay or explicit conflict handling.

Do not blindly replay mutations after reconnect without first reading the current shared state.

---

## Future entries template

```markdown
# H2-NONAI-XXX — Short title

**Severity:** CRITICAL | HIGH | MEDIUM | LOW
**Status:** OPEN
**First confirmed:** YYYY-MM-DD
**Area:** ...

## Observed behavior

...

## Expected behavior

...

## Evidence

- source SHA:
- exact file/path:
- screenshot/log/reproduction:
- affected environment:

## Root cause status

Confirmed / suspected / unknown.

## Required direction

...

## Closure evidence

- fix SHA:
- tests:
- real-device/manual evidence:
```

---

## Phase-13 integration lock

Before any accepted Agent Lab engine is connected back into production H2 Notes:

1. Read this complete ledger.
2. Re-audit every OPEN non-AI item against the latest H2 Notes source.
3. Convert each item into a bounded fix/acceptance task.
4. Do not let AI integration changes mask or overwrite these defects.
5. Run the H2 Notes full regression suite **and** the required real multi-PC/NAS acceptance checks.
6. Record exact evidence back in this ledger.

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

## H2M-010 resolution triage — 2026-09-19

No HIGH/CRITICAL issue is accepted as a limitation at this gate. No issue is marked fixed without implementation/real-device evidence.

| ID | Current severity/status | Target disposition | Owning task(s) | Required closure evidence |
|---|---|---|---|---|
| H2-NONAI-001 | HIGH / OPEN | **FIXED** | H2M-012 + H2M-013 | deterministic mixed-generation recovery tests + real two-PC/NAS convergence evidence |
| H2-NONAI-002 | HIGH / OPEN | **FIXED** | H2M-011 | fault-injection proving final-validation failure retains a recoverable transaction journal |
| H2-NONAI-003 | MEDIUM / OPEN | **FIXED** | H2M-012 follow-up | structured bounded sync diagnostic evidence; generic user status may remain |
| H2-NONAI-004 | HIGH / OPEN-RISK | **FIXED** or fail-closed unsupported-share classification | H2M-013 | real two-PC lock/rename/flush/read-after-commit capability matrix |
| H2-NONAI-005 | HIGH / OPEN | **FIXED** | H2M-012 | last-known-good/guided recovery path; no destructive hash rewrite |
| H2-NONAI-006 | HIGH / OPEN | **FIXED** | H2M-013 | real NAS/SMB acceptance evidence, not local temp-folder tests |
| H2-NONAI-007 | MEDIUM / OPEN-KNOWN-GAP | **FIXED unless later explicitly accepted by user** | H2M-015 | durable local pending-operation/draft recovery decision + tests |
| H2-NONAI-008 | HIGH / OPEN | **FIXED** | H2M-014 | Local/Mapped/UNC classification, logical workspace identity and safe endpoint behavior |
| H2-NONAI-009 | HIGH / OPEN | **FIXED** | H2M-014 | alias-safe WorkspaceId locking/transfer validation tests |

Triage result:

- **7 HIGH** issues all have an explicit FIX path.
- **0 CRITICAL** issues currently exist.
- **2 MEDIUM** issues also have explicit follow-up paths.
- No HIGH/CRITICAL item is silently deferred.
- No ACCEPTED_LIMITATION disposition is used without a later explicit user decision.
- Real NAS-dependent issues remain OPEN until real NAS evidence exists.

## H2M-013 probe readiness — 2026-09-19

A dedicated real-device acceptance harness now exists at `tools/H2Notes.NasAcceptance`, with execution instructions in `docs/H2_NAS_REAL_ACCEPTANCE.md`.

This changes neither H2-NONAI-004 nor H2-NONAI-006 to FIXED. Their statuses remain open until two physical PCs execute the same session against the intended NAS/share and preserve the coordinator/peer JSON evidence. GitHub-hosted/local self-tests prove harness behavior only, not SMB/NAS semantics.

## H2M-013 deferred real-NAS acceptance — 2026-09-20

The user explicitly approved continuing without the physical two-PC/NAS execution at this stage because the application is not yet complete enough for that test.

This is an **ACCEPTED_LIMITATION**, not a claim of FIXED or VERIFIED real NAS behavior.

Evidence available now:

- `tools/H2Notes.NasAcceptance` two-node acceptance harness;
- `docs/H2_NAS_REAL_ACCEPTANCE.md` physical-run procedure;
- source `ed0a02de50fdffc63ef4fa5bd8b89505c5c52092`;
- GitHub Actions run `35453854716` SUCCESS;
- H2 Notes **340/340**;
- NAS harness local protocol self-test PASS;
- self-contained Windows NAS probe artifact published.

Deferred evidence still mandatory before final production acceptance:

- two distinct physical PCs;
- the user's intended NAS/share;
- exclusive lock visibility;
- durable flush visibility;
- replacement/rename visibility;
- concurrent writers/read-after-commit;
- interrupted-save recovery;
- no persistent mixed generation;
- coordinator/peer JSON reports preserved.

Reopen this limitation no later than H2M-116 / H2M-133. Do not advertise real multi-PC NAS support as certified until that evidence exists.

## H2M-014 resolution evidence — 2026-09-20

Implemented:

- shared workspace schema 6 with stable `WorkspaceId`;
- backward read/migration support for schema 2/3/4/5;
- `WorkspaceLocationKind` classification for local fixed/removable, mapped network, UNC and unsupported/unknown providers;
- Windows mapped-drive resolution through `WNetGetConnection`;
- friendly mapped display path retained while canonical network identity uses the resolved endpoint;
- machine-local `WorkspaceLocationProfile` persistence without network credentials;
- same-machine lock keyed by WorkspaceId once known;
- transfer validation rejects matching WorkspaceIds even when path strings differ;
- recovery cache keyed by canonical endpoint;
- startup endpoint fallback uses only reachable aliases exposing the expected WorkspaceId;
- Storage settings show location kind, network target and WorkspaceId.

Deterministic regression evidence at source `15adb9f3f188943ad9397a349805fe010a590119`:

- mapped-network and UNC aliases normalize to one canonical endpoint;
- schema 5 migrates atomically to schema 6 without project-data loss;
- cloned alias paths with one WorkspaceId cannot obtain a second local instance lock;
- source/target aliases with one WorkspaceId are rejected as self-transfer;
- genuinely different WorkspaceIds remain valid transfer targets;
- mapped endpoint is preferred when reachable;
- resolved alias is selected only when it has the same WorkspaceId;
- mismatched endpoint identity is rejected;
- LocalConfiguration round-trips friendly path + resolved/canonical metadata.

CI:

- Actions run `35475729264`: SUCCESS;
- H2 Notes: **345 passed, 0 failed**;
- NAS harness self-test: PASS;
- full Agent/reference-extension/provider matrix: PASS;
- self-contained Windows app and NAS probe publish: PASS;
- publish commit: `b11ef7ffaddcae2861c6302ea3ed9ac8d56bdf98`;
- portable ZIP SHA256: `98f774d01ee4214ecd0fc9ce591d574655d787af3d9e165017c0e41a17f9fd08`.

Disposition:

- **H2-NONAI-009 = FIXED.**
- **H2-NONAI-008 = PARTIAL-FIX-H2M015-FOLLOWUP.** The core identity/classification and mapped↔UNC behavior are fixed. A broader configured secure remote/VPN endpoint/failover policy remains intentionally unresolved and is assigned to H2M-015 together with offline/reconnect durability. No raw SMB-over-public-Internet behavior is introduced.

## Summary

| ID | Severity | Area | Status | Short description |
|---|---|---|---|---|
| H2-NONAI-001 | HIGH | NAS / multi-PC sync | OPEN-REAL-NAS / H2M-133 BLOCKER | Code-side recovery is implemented; physical two-PC/NAS convergence proof is now required at the final data-integrity gate |
| H2-NONAI-002 | HIGH | Persistence / recovery | FIXED | Journal now survives through final snapshot validation; fault-injection proves rollback after a bad committed generation |
| H2-NONAI-003 | MEDIUM | Diagnostics / sync UX | FIXED | Structured bounded sync diagnostics are persisted locally and surfaced as transient/persistent/recovery status |
| H2-NONAI-004 | HIGH | NAS protocol compatibility | OPEN-REAL-NAS / H2M-133 BLOCKER | Harness exists and self-tests; real share locking/rename/flush semantics must now be certified on the intended share |
| H2-NONAI-005 | HIGH | Recovery / availability | FIXED | Validated last-known-good fallback + write lock + explicit quarantine/recovery path implemented and tested |
| H2-NONAI-006 | HIGH | Test coverage | OPEN-REAL-NAS / H2M-133 BLOCKER | Real two-PC harness is ready; final physical run is now required before production acceptance |
| H2-NONAI-007 | MEDIUM | Offline durability | FIXED | Durable machine-local pending snapshots survive restart, merge after reconnect, audit conflicts and clear only after shared commit |
| H2-NONAI-008 | HIGH | Storage location / network failover | PARTIAL-FIX / USER-POLICY-DECISION-REQUIRED | Local/mapped/UNC classification, canonical mapped→UNC resolution, WorkspaceId and identity-checked alias fallback are implemented; optional secure Remote/VPN failover still needs final product-policy decision |
| H2-NONAI-009 | HIGH | Workspace identity / locking | FIXED | Schema 6 WorkspaceId drives logical alias identity, same-machine locking and self-transfer rejection; mapped/UNC alias regressions are green |

---

# H2-NONAI-001 — NAS project/index hash mismatch blocks second-PC refresh

**Severity:** HIGH  
**Status:** OPEN-REAL-NAS / H2M-133 BLOCKER  
**First confirmed:** 2026-09-18  
**Area:** multi-PC NAS synchronization / data integrity

## Observed behavior

Two physical PCs are configured to use the same shared H2 Notes workspace on NAS.

Observed mapped paths:
- PC1: `X:\\.Note`
- PC2: `X:\\Dữ liệu Hưng\\.Note`

These are user-confirmed NAS paths. The two Windows drive mappings are not required to have identical local path strings; the acceptance question is whether they resolve to the same intended NAS workspace/root.


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


## H2M-012 mitigation evidence — 2026-09-19

The former permanent-PC2 trap is removed at the application layer: bounded reread distinguishes short visibility skew from persistent mismatch; a previously validated machine-local generation may be opened read-only; writes remain blocked; explicit recovery quarantines the invalid generation before restoring last-known-good; no-cache cases fail closed. Exact source `d3937e501b50220c6688d336a84e5b58ac1539a4`, Actions run `35452160225`, H2 Notes **340/340**.

This does **not** close H2-NONAI-001. The physical NAS root cause and proof that the repaired protocol converges across two actual PCs remain mandatory under H2M-013.

---

# H2-NONAI-002 — Journal deleted before final snapshot verification

**Severity:** HIGH  
**Status:** FIXED  
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


## Resolution evidence — 2026-09-19

Implementation:

- source commit `4deeb435442b38fda9ceb4e4763ffd95e79ef5c3` moves deletion of `.h2-transaction.json` until after the newly published generation passes full `ReadSnapshot()` hash/schema/state validation;
- regression commit `7d50fc7631bf3aab225d68faebb7ff0db7f4cbc5` adds deterministic fault injection that corrupts the changed project only after both project payload and workspace index were published;
- the injected final validation fails, the still-present journal restores the previous generation, the journal is then retired by recovery, and a fresh store can reopen the original valid project state.

Verification:

- GitHub Actions run `35450998329`: **SUCCESS**;
- H2 Notes: **337 passed, 0 failed**;
- dedicated regression: `PASS Workspace final validation failure retains recovery journal until rollback`;
- all Agent regression/acceptance, DesktopHost, OfficeHost, provider transport/resilience, self-contained Windows x64 publish and repository ZIP publication completed successfully;
- publish commit: `c1784dc2a592c7e28d48594858cf5fe02504f797`;
- portable ZIP SHA256: `ba1df4fa00dcf8d870963a20b8b028513972c2b8574608d1fc4ae865f3e484c3`.


---

# H2-NONAI-003 — Background sync hides useful failure diagnostics

**Severity:** MEDIUM  
**Status:** FIXED  
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


## Resolution evidence — 2026-09-19

- `WorkspaceSyncDiagnostic` persists a bounded local record under the machine-local workspace recovery cache.
- The record includes event/failure code, exception type, offending relative file, expected/actual SHA-256, observed index SHA-256, local writer/device ID, UTC timestamp, attempt count, transient/persistent classification, recovery availability, recovery result/snapshot ID and quarantine path where applicable.
- No project/note body, attachment bytes, API key or unrelated file contents are copied into the diagnostic record.
- `App.RefreshSharedWorkspace()` now distinguishes transient convergence, persistent generation failure with/without safe recovery, and read-only last-known-good fallback instead of collapsing all cases to one generic message.
- Exact source `d3937e501b50220c6688d336a84e5b58ac1539a4`, GitHub Actions run `35452160225`: H2 Notes **340 passed, 0 failed**, full Agent/reference-extension/transport/publish pipeline SUCCESS.

---

# H2-NONAI-004 — Shared-filesystem capability assumptions are not verified

**Severity:** HIGH  
**Status:** OPEN-REAL-NAS / H2M-133 BLOCKER  
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
**Status:** FIXED  
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


## Resolution evidence — 2026-09-19

The repaired path is fail-safe rather than hash-rewriting:

1. every fully validated shared generation is copied to a machine-local immutable last-known-good generation keyed by the validated workspace-index SHA-256;
2. an inconsistent read is retried a bounded three times, allowing short NAS visibility skew to converge without intervention;
3. if the mismatch remains persistent and a validated local snapshot exists, H2 opens that last-known-good snapshot so PC2 can still start/use the app, but `ProjectWorkspaceStore.IsRecoveryFallbackActive` blocks all writes;
4. background refresh and Save status explicitly tell the user the workspace is in safe read-only fallback;
5. Settings exposes **Phục hồi từ bản an toàn gần nhất…** only when persistent failure + safe recovery evidence exist;
6. explicit recovery first copies the complete currently referenced invalid generation into a local quarantine (including actual bytes and bounded hash diagnostics), then restores the verified last-known-good files with the index published last;
7. if no valid last-known-good exists, recovery is unavailable and the shared bytes remain untouched.

Deterministic regression cases in `WorkspaceTests.cs` prove:

- transient mixed generation converges on bounded reread;
- persistent mixed generation enters last-known-good fallback without changing corrupt NAS bytes;
- Save is rejected while fallback is active;
- explicit guided recovery quarantines the exact corrupt project bytes before restoring;
- persistent mismatch with no recovery cache fails closed and persists bounded diagnostics;
- externally modified bytes are preserved and never silently overwritten.

Exact functional source: `d3937e501b50220c6688d336a84e5b58ac1539a4`.  
GitHub Actions: `35452160225` — **SUCCESS**.  
H2 Notes: **340 passed, 0 failed**.  
Publish commit: `819ab68bcf73174282df98e68ef726d81b0ad82b`.  
Portable ZIP SHA256: `4a27bf96882a7518baa87e0feeb91a577415f3a957e85435210e9bd741acdbfd`.

---

# H2-NONAI-006 — Real NAS semantics are not covered by the current automated multi-PC tests

**Severity:** HIGH  
**Status:** OPEN-REAL-NAS / H2M-133 BLOCKER  
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
1. Reproduce with the user's real NAS mappings: PC1 `X:\\.Note`, PC2 `X:\\Dữ liệu Hưng\\.Note`, after confirming both resolve to the same intended shared workspace.
2. PC1 and PC2 open the same workspace.
3. Alternate rapid saves on different projects.
4. Simultaneous edits to different fields of the same project.
5. Same-field conflict.
6. Kill one writer during transaction.
7. Disconnect NAS during save and reconnect.
8. Verify no persistent index/file hash mismatch.
9. Verify both clients converge without data loss.

---

# H2-NONAI-007 — No durable pending-operation queue while NAS is offline

**Severity:** MEDIUM  
**Status:** FIXED  
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

## H2M-015 resolution evidence — 2026-09-20

H2-NONAI-007 is closed by a durable local pending-work path:

- every normal ProjectWorkspaceStore save persists a machine-local immutable pending snapshot before remote I/O;
- each pending record carries operation identity, WorkspaceId and base-generation identity;
- restart/reconnect observes remote first, then performs the existing three-way merge;
- local and remote non-conflicting work is preserved;
- same-field conflicts are retained in the existing conflict audit;
- replay of the same pending record is idempotent;
- successful remote commit acknowledges pending state;
- corrupt pending files are quarantined;
- pending history is bounded to eight snapshots.

Exact source: `cc2b03ddec6e9fd3524e376c7013426308b70ef4`.  
Actions: `35476297321` — SUCCESS.  
H2 Notes: **348 passed, 0 failed**.  
Portable source/publish: `38f0975622bf0191017356fbec41dde11e8d8f99`, SHA256 `5e2514dae1de9e478c80b05c486fb8badc6a090d54def8ff5ba1fad0107ad660`.

The remaining H2-NONAI-008 remote/VPN portion is intentionally not marked fixed. Current policy is: known friendly/resolved aliases may switch only after WorkspaceId verification; otherwise H2 enters durable pending/offline behavior. Automatic discovery/use of arbitrary Internet/VPN endpoints remains deferred and must be resolved or explicitly accepted at H2M-133 before final production claim.

---

# H2-NONAI-008 — Storage location is path-string only; mapped-network detection and endpoint failover are missing

**Severity:** HIGH  
**Status:** PARTIAL-FIX / USER-POLICY-DECISION-REQUIRED  
**First confirmed:** 2026-09-18  
**Area:** storage location / mapped network / LAN-remote failover

## Observed design gap

Windows shows the user's NAS shares as **Network locations** with mapped drive letters (for example X:, Y:, Z:). A mapped drive may look like an ordinary local path to application code, but it is not local storage.

Current H2 Notes behavior:
- `LocalConfiguration` stores only `string? DataFolder`;
- folder selection uses `TryGetLocalPath()` and then persists the returned path string;
- startup recreates `ProjectWorkspaceStore` from only that path;
- no storage-kind metadata is persisted;
- no mapped-drive root is resolved to its UNC/network target;
- no alternative LAN/remote endpoints are associated with one logical workspace;
- no automatic endpoint failover exists.

The current picker error text also says `Cần thư mục cục bộ đã tải về máy.`, which is conceptually wrong for a valid mapped NAS drive that exposes a Windows path such as `X:\...`.

## Required architecture

Introduce a host-side storage-location abstraction instead of treating every selected path as local.

A selected workspace location should be classified at minimum as:
- `LocalFixed`;
- `LocalRemovable`;
- `MappedNetwork`;
- `UncNetwork`;
- `UnsupportedOrUnknownNetworkProvider`.

On Windows, mapped drives should be detected using the drive/root type and resolved to the underlying network mapping (for example via the Windows network-drive mapping API) while preserving the user-friendly mapped path for display.

Persist a local workspace profile similar to:

```text
WorkspaceId
PreferredEndpointId
Endpoints[]
  EndpointId
  Kind = Local | LanNetwork | RemoteNetwork
  DisplayPath
  ResolvedNetworkPath
  Priority
  LastSuccessfulUtc
  CapabilityStatus
```

Do **not** store network credentials in the workspace profile.

## LAN / Internet behavior

Do not attempt to infer "same LAN" only from network-interface state.

Prefer endpoint reachability:
1. Try the configured LAN endpoint first with a short bounded probe.
2. If unavailable, try a configured secure remote/VPN endpoint.
3. Before switching, prove the candidate endpoint exposes the **same logical H2 workspace**.
4. Never change endpoint in the middle of a storage transaction.
5. If the fallback transport does not preserve the locking/atomic-replace semantics required by H2 Notes, fail closed or use a separate offline/pending-sync mode instead of pretending it is equivalent shared storage.

Raw SMB should not be exposed directly over the public Internet merely to satisfy this failover design; remote access should use an appropriate secure network/VPN/overlay or another transport whose filesystem semantics are explicitly supported.

## Required workspace identity

Path strings must not be the logical identity of a shared workspace.

Add/retain one stable workspace identity (GUID or equivalent) in the shared H2 workspace metadata. Every candidate endpoint must be verified against this ID before automatic failover.

Example:

```text
PC A mapped path  -> X:\.Note
PC B mapped path  -> X:\Dữ liệu Hưng\.Note
LAN UNC endpoint  -> \\server\share\...\.Note
Remote/VPN path   -> another reachable path
                     |
                     +--> all must expose the same WorkspaceId
```

Only after identity verification may H2 Notes treat these as aliases of one workspace.

## Required acceptance

- picker correctly identifies mapped-network paths as network storage;
- UI shows Local / Network (LAN) / Network (Remote/VPN) status;
- app preserves a friendly mapped path while retaining a canonical/network identity;
- disconnect LAN while app is idle and verify safe failover to a configured remote endpoint for the same WorkspaceId;
- reconnect LAN and verify safe preference switch back only while no transaction is in flight;
- wrong endpoint with a different WorkspaceId must be rejected;
- unsupported network provider semantics must not enable multi-writer mode silently.

## Current implementation / final product decision — 2026-09-21

The path-string-only portion of this entry is no longer current:

- Schema 6 stores a stable shared `WorkspaceId`;
- `WorkspaceLocation` classifies LocalFixed / LocalRemovable / MappedNetwork / UncNetwork / Unsupported;
- Windows mapped drives are resolved to their network target while the friendly mapped path is retained for display;
- logical identity, same-machine locking and self-transfer rejection use `WorkspaceId` after the workspace is known;
- the resolved mapped/UNC alias may be used only when reachable and the candidate exposes the same `WorkspaceId`;
- H2 never switches endpoint in the middle of a transaction;
- when the accepted endpoint is unavailable, durable machine-local pending work is retained and replayed after reconnect.

Exact implementation evidence: H2M-014 source `15adb9f3f188943ad9397a349805fe010a590119`, Actions `35475729264` SUCCESS; H2M-015 source `cc2b03ddec6e9fd3524e376c7013426308b70ef4`, Actions `35476297321` SUCCESS.

The remaining unresolved product-policy question is narrower: whether production H2 should also support an explicitly configured secure **Remote/VPN** alias when the preferred LAN endpoint is unavailable. Current behavior deliberately chooses fail-safe LAN/verified-alias + durable offline pending mode instead of inventing Internet/VPN failover semantics.

At H2M-133 the user must choose one:

1. accept the current fail-safe boundary as the production design; or
2. require implementation and real acceptance of secure Remote/VPN alias failover before production acceptance.

---

# H2-NONAI-009 — Path aliases can bypass same-workspace identity checks and local instance locking

**Severity:** HIGH  
**Status:** FIXED  
**First confirmed:** 2026-09-18  
**Area:** workspace identity / duplicate instance / folder validation

## Current code behavior

`ProjectWorkspaceStore` currently canonicalizes only with:

```text
Root = Path.GetFullPath(root)
```

The same-PC instance lock key is derived from the resulting `Root` string.

`ValidateDestination(source, target)` also compares only normalized path strings to decide whether two locations are the same or nested.

## Failure mode

The same physical NAS workspace may be reachable through multiple aliases:

```text
X:\Dữ liệu Hưng\.Note
Y:\.Note
\\DRPBM6\share\Dữ liệu Hưng\.Note
```

If these aliases resolve to the same underlying workspace but have different path strings:
- same-machine duplicate-instance protection can allocate different local lock keys;
- H2 Notes may allow two processes on one PC to open the same logical workspace through two aliases;
- source/destination validation can fail to recognize that a transfer target is actually the same workspace;
- future LAN/remote endpoint failover can accidentally be treated as a workspace change instead of an endpoint change.

## Required direction

Use a stable shared `WorkspaceId` as the primary identity after the workspace can be read.

The local duplicate-instance lock should ultimately be keyed by logical workspace identity, not by the presentation path.

For initial opening when the workspace ID is not yet available:
- use a temporary path-level/opening lock if necessary;
- read/validate the workspace identity;
- acquire the logical WorkspaceId lock;
- refuse the second instance if that logical ID is already open;
- then release any temporary alias-specific lock.

Transfer validation should compare logical workspace IDs when both source and destination already contain H2 workspaces.

Path comparison remains useful for local empty folders and traversal protection, but it is insufficient as shared-workspace identity.

## Required tests

- same NAS root via mapped drive and UNC alias on one PC -> second instance rejected;
- same NAS root via two mapped letters -> second instance rejected;
- source/target aliases of same WorkspaceId -> transfer rejected as self-transfer;
- two genuinely different workspaces with similar paths -> allowed;
- endpoint failover between aliases of the same WorkspaceId -> no data migration prompt.

## Resolution evidence — 2026-09-21

H2-NONAI-009 is fixed by the Schema-6 logical workspace identity work:

- stable `WorkspaceId` is persisted in shared workspace metadata;
- same-machine locks use logical workspace identity after validation;
- mapped-drive and UNC aliases are recognized as aliases of the same workspace;
- source/target aliases with the same WorkspaceId are rejected as self-transfer;
- different workspaces remain allowed;
- resolved endpoint aliases are accepted only after WorkspaceId verification.

Exact functional source: `15adb9f3f188943ad9397a349805fe010a590119`.  
GitHub Actions: `35475729264` — SUCCESS.  
H2 Notes: **345 passed, 0 failed**.  
NAS harness self-test and the complete Agent/provider/publish pipeline passed in that acceptance run.

Real two-PC NAS semantics remain tracked separately by H2-NONAI-001 / 004 / 006 and do not reopen this logical-identity defect.

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

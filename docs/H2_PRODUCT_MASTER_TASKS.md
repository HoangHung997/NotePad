# H2 Notes — AI-First Product Rebuild / Integration Task Tracker

Status: **CANONICAL FUTURE H2 NOTES TRACKER**  
Date: 2026-09-18  
Architecture source of truth: `docs/H2_PRODUCT_MASTER_SPEC.md`

> This tracker is for H2 Notes/product work.
>
> It does not replace the Agent tracker.
>
> Agent work must first complete the acceptance defined by:
>
> - `docs/H2_AGENT_MASTER_SPEC.md`
> - `docs/H2_AGENT_MASTER_TASKS.md`
>
> H2 Agent integration gate **was explicitly passed by the user on 2026-09-19 (MB-122)**. Product work may proceed, subject to this tracker's storage/data-integrity and migration gates.

---

# Execution rules

1. Read `docs/H2_PRODUCT_MASTER_SPEC.md` completely before implementation.
2. Read `docs/H2_NOTES_NON_AI_BUG_LEDGER.md` completely before changing shared storage/data models.
3. Preserve existing user projects/tasks/notes/chat history.
4. Do not create duplicate H2 stores for Agent tasks/evidence/verification.
5. Do not put machine-local UI state into shared NAS project data.
6. Do not delete a legacy AI path until Agent replacement parity is proven.
7. Every persistent schema change requires migration + rollback/read-old-data coverage.
8. H2 Notes tests must remain green.
9. Real NAS/multi-PC issues require real NAS evidence where specified by the bug ledger.
10. UI redesign must be measured against actual use, not only static mockups.
11. Do not reimplement Agent Core inside H2.
12. Do not turn every useful view into a separate subsystem.

---

# Stage H0 — Freeze current H2 baseline

## [x] H2M-000 — Record current implementation baseline

Goal:

Create a factual baseline before redesign.

Record:

- current main commit;
- current H2 Notes test count;
- current workspace schema;
- current project file format;
- current MainWindow information architecture;
- current AI chat execution path;
- current project/chat data model;
- current open non-AI bug ledger items;
- current UI baseline documents/images.

Deliverable:

`docs/H2_PRODUCT_CURRENT_BASELINE.md`

Acceptance:

- no runtime behavior changed;
- exact code paths documented;
- data compatibility risks listed.

Evidence: `docs/H2_PRODUCT_CURRENT_BASELINE.md` freezes main HEAD `8a45794b15a791f761d3f43372e6d397cdb6ab7b`, active-branch inspection HEAD `c22c0e5a5091d95e91f773ac25991a7cc771d29f`, last full functional CI source `0a7ea1e6586bc5e70e336536603a8044fab654d6` / Actions run `35448436223`, H2 Notes **336 passed, 0 failed**, Schema 5 storage/file layout, current Task+Notes+AI information architecture, legacy `AiChatPanel -> AiClient -> AiProjectContext/AiProjectActions` execution path, project/chat persisted models, UI evidence set, nine open non-AI defects/risks, and migration/data-compatibility constraints. H2M-000 changed documentation only.

---

## [x] H2M-001 — Mark future product documentation authority

Goal:

Make documentation authority unambiguous.

Required:

- `H2_PRODUCT_MASTER_SPEC.md` = future H2 product architecture authority;
- `H2_PRODUCT_MASTER_TASKS.md` = future H2 product task authority;
- `H2_NOTES_NON_AI_BUG_LEDGER.md` remains independent canonical defect ledger;
- old redesign/proposal files remain history/reference only.

Do not delete:

- `APPROVED_PRODUCT_SPEC.md`;
- `RESPONSIVE_IMPLEMENTATION.md`;
- `UI_ACCEPTANCE.md`;
- UI concept screenshots;
- old AI proposal documents.

Acceptance:

- every old future-redesign document links to the master spec;
- no conflicting “current future architecture” document remains.

Evidence: future authority is now explicit in `H2_PRODUCT_MASTER_SPEC.md` / `H2_PRODUCT_MASTER_TASKS.md`; `APPROVED_PRODUCT_SPEC.md`, `RESPONSIVE_IMPLEMENTATION.md`, `UI_ACCEPTANCE.md`, `AI_PROJECT_ASSISTANT_PROPOSAL.md` and `H2_AI_PROJECT_COMMAND_CENTER_REDESIGN.md` are retained but clearly marked historical/current-baseline evidence. `H2_AGENT_MASTER_SPEC.md` was corrected so it no longer treats the superseded Command Center draft as current H2 architecture. The independent bug ledger remains canonical and separate. Documentation-only change; no runtime/data behavior changed.

---

# Stage H1 — Storage/data integrity before new shared state

## [x] H2M-010 — Triage all non-AI storage bugs

Read every entry in:

`docs/H2_NOTES_NON_AI_BUG_LEDGER.md`

For every OPEN HIGH/CRITICAL item, choose:

- FIXED;
- ACCEPTED_LIMITATION with explicit user approval;
- NOT_REPRODUCIBLE with required evidence.

Do not silently defer a HIGH data-integrity issue.

Acceptance:

- all HIGH/CRITICAL entries have an explicit path;
- implementation evidence is linked.

Evidence: `docs/H2_NOTES_NON_AI_BUG_LEDGER.md` now carries the H2M-010 resolution table. All **7 HIGH** entries target FIXED with an owning implementation/real-device task (H2M-011/012/013/014), **0 CRITICAL** entries exist, and both MEDIUM entries have explicit follow-up paths. No HIGH/CRITICAL item was relabeled fixed, accepted, or not-reproducible without the required evidence. The next active task is H2M-011.

---

## [x] H2M-011 — Fix/close journal final-verification ordering risk

Target:

H2-NONAI-002.

Required direction:

- do not delete the recovery journal before final committed snapshot validation;
- prove failed final validation still has a recoverable transaction path;
- deterministic fault-injection test.

Acceptance:

- bad committed generation can be recovered without relying on already-deleted journal state.

Evidence: `ProjectWorkspaceStore.SaveIncremental` now retains the recovery journal until `ReadSnapshot()` validates the complete new generation. Deterministic fault injection corrupts the project after payload+index publication and proves the catch path restores the old generation. Source/test head `7d50fc7631bf3aab225d68faebb7ff0db7f4cbc5`, GitHub Actions run `35450998329` SUCCESS, H2 Notes **337/337**, full Agent/extension/transport/publish pipeline green. Publish commit `c1784dc2a592c7e28d48594858cf5fe02504f797`; ZIP SHA256 `ba1df4fa00dcf8d870963a20b8b028513972c2b8574608d1fc4ae865f3e484c3`. H2-NONAI-002 is FIXED.

---

## [x] H2M-012 — Resolve persistent mixed-generation recovery path

Target:

H2-NONAI-001 / H2-NONAI-005.

Required:

- distinguish transient read inconsistency from persistent invalid generation;
- preserve last-known-good data;
- avoid destructive overwrite;
- provide actionable recovery status.

Acceptance:

- persistent hash mismatch does not trap the second PC forever without a recovery path;
- no silent data loss.

Evidence: `ProjectWorkspaceStore` now performs bounded consistency rereads; persists machine-local validated last-known-good generations; opens a persistent mismatch in write-blocked last-good fallback when safe evidence exists; fails closed when it does not; persists structured bounded diagnostics; and exposes explicit guided recovery that quarantines the complete invalid referenced generation before restoring verified last-good bytes. `App` surfaces transient/persistent/fallback state and Settings exposes the confirmed recovery action. Regression coverage proves transient convergence, persistent fallback, write blocking, exact corrupt-byte quarantine, guided recovery, no-cache fail-closed behavior and preservation of externally modified bytes. Exact source `d3937e501b50220c6688d336a84e5b58ac1539a4`, Actions run `35452160225` SUCCESS, H2 Notes **340/340**, full Agent/reference-extension/provider/publish pipeline green. Publish `819ab68bcf73174282df98e68ef726d81b0ad82b`; ZIP SHA256 `4a27bf96882a7518baa87e0feeb91a577415f3a957e85435210e9bd741acdbfd`. H2-NONAI-005 and H2-NONAI-003 are FIXED; H2-NONAI-001 remains OPEN-REAL-NAS for H2M-013 physical two-PC proof.

---

## [x] H2M-013 — Real NAS/SMB capability acceptance

Targets:

- H2-NONAI-004;
- H2-NONAI-006.

Run real two-PC/NAS tests for:

- lock behavior;
- rename/replace visibility;
- flush visibility;
- concurrent writers;
- interrupted save;
- read-after-commit;
- recovery.

Acceptance:

- shared filesystem mode is either verified supported or explicitly blocked/limited.

H2M-013 closure: the real-device harness remains available at `tools/H2Notes.NasAcceptance` and `docs/H2_NAS_REAL_ACCEPTANCE.md`. Exact harness source `ed0a02de50fdffc63ef4fa5bd8b89505c5c52092`, Actions run `35453854716` SUCCESS: H2 Notes **340/340**, NAS harness self-test PASS, self-contained NAS probe publish PASS, full Agent/reference-extension/provider/publish pipeline green. **No claim is made that the user's real NAS/SMB share has been certified.** On 2026-09-20 the user explicitly accepted deferring the physical two-PC/NAS run until the application is more complete. Therefore H2-NONAI-001/004/006 are recorded as `ACCEPTED_LIMITATION-DEFERRED_REAL_NAS`, not FIXED. This acceptance is temporary and must be reopened at H2M-116/H2M-133/final production acceptance before claiming real multi-PC NAS support.

---

## [x] H2M-014 — Workspace identity and network location model

Targets:

- H2-NONAI-008;
- H2-NONAI-009.

Required:

- distinguish local / mapped-network / UNC;
- resolve mapped drive target where possible;
- stable workspace identity independent of X:/Y:/UNC aliases;
- local instance lock ultimately tied to workspace identity;
- destination validation compares logical workspace identity.

Acceptance:

- same physical NAS workspace via aliases is recognized as the same workspace.

H2M-014 evidence: Schema **6** adds one stable shared `WorkspaceId` while preserving read/migration support for schemas 2–5. `WorkspaceLocation` classifies LocalFixed/LocalRemovable/MappedNetwork/UncNetwork/Unsupported, resolves Windows mapped drives through `WNetGetConnection`, preserves the friendly mapped path for UI, and uses the resolved network path as canonical endpoint identity when available. Same-machine instance locks and transfer validation use `WorkspaceId` once known; recovery cache identity uses the canonical endpoint. `WorkspaceEndpointSelector` prefers the friendly endpoint, falls back to the resolved alias only when reachable **and** the candidate exposes the same WorkspaceId, and never changes endpoint mid-transaction. LocalConfiguration persists endpoint metadata without credentials; Settings displays storage kind/network target/WorkspaceId. Regression cases prove mapped↔UNC canonicalization, schema-5→6 atomic migration, duplicate alias lock/self-transfer rejection, distinct-workspace allowance, endpoint fallback identity checking, and local endpoint-profile persistence. Exact functional source `15adb9f3f188943ad9397a349805fe010a590119`, Actions `35475729264` SUCCESS, H2 Notes **345/345**, NAS harness self-test PASS, complete Agent/reference-extension/provider pipeline and both Windows publishes PASS. Publish commit `b11ef7ffaddcae2861c6302ea3ed9ac8d56bdf98`; ZIP SHA256 `98f774d01ee4214ecd0fc9ce591d574655d787af3d9e165017c0e41a17f9fd08`. H2-NONAI-009 is FIXED. The broader optional remote/VPN multi-endpoint policy from H2-NONAI-008 is not falsely claimed complete and moves to H2M-015 alongside offline/reconnect behavior.

---

## [x] H2M-015 — Offline durability decision

Target:

H2-NONAI-007.

Choose and implement or explicitly accept limitation for:

- durable local pending operations;
- draft durability while NAS unavailable;
- conflict behavior after reconnect;
- remaining H2-NONAI-008 endpoint policy: whether/how a configured secure remote/VPN alias may be used when the preferred LAN/mapped endpoint is unavailable, always requiring matching WorkspaceId and supported storage semantics.

Do not accidentally expand scope into full distributed database synchronization.

H2M-015 evidence: `ProjectWorkspaceStore` now writes an immutable machine-local pending snapshot **before** attempting the shared commit. Pending records carry a stable operation ID, WorkspaceId, base generation, local/base state and dirty project IDs; the cache is bounded to eight append-only records. On restart/reconnect H2 first reads the current remote generation, then replays the newest valid pending record through the existing three-way merge, preserving remote changes, auditing same-field conflicts and keeping replay idempotent. A successful remote commit acknowledges/removes pending snapshots; corrupt pending records are quarantined instead of replayed. `App.SaveNow` automatically retries while pending data exists and surfaces that the local pending copy is durable. Exact source `cc2b03ddec6e9fd3524e376c7013426308b70ef4`, Actions run `35476297321` SUCCESS, H2 Notes **348/348**, dedicated offline replay/conflict/bounded-pending tests PASS, full Agent/reference-extension/provider pipeline and both Windows publishes PASS. Publish commit `38f0975622bf0191017356fbec41dde11e8d8f99`; ZIP SHA256 `5e2514dae1de9e478c80b05c486fb8badc6a090d54def8ff5ba1fad0107ad660`. H2-NONAI-007 is FIXED.

Endpoint policy at this stage is deliberately fail-safe rather than distributed failover: H2 may select the friendly configured endpoint or its resolved alias only when reachable and, once known, the candidate exposes the same WorkspaceId. It does not discover arbitrary Internet endpoints, store network credentials, or switch endpoints in the middle of a transaction. If no accepted endpoint is reachable, H2 keeps durable pending work locally and retries after reconnect. The broader optional user-configured secure remote/VPN alias requirement from H2-NONAI-008 remains explicitly deferred to the final data-integrity/product acceptance gate rather than being falsely marked complete.

---

# Stage H2 — Wait for Agent acceptance boundary

## [x] H2M-020 — Verify Agent integration gate

Blocking dependency:

Agent master task `MB-122` must be explicitly accepted by the user.

Before H2 integration confirm Agent exposes a stable product-facing boundary for:

- start task;
- observe progress;
- cancel;
- approval;
- task summary;
- recent project runs;
- evidence;
- optional ProjectId association.

Acceptance:

- H2 integration does not need direct access to ToolRegistry/transport/MCP internals.

Evidence: `docs/H2_AGENT_INTEGRATION_GATE.md` maps every H2 lifecycle need to the frozen MB-121 seven-operation boundary and records the explicitly approved MB-122 gate. `InspectTask` supplies task summary/evidence projection; H2-specific recent-run enumeration, nullable ProjectId correlation and later task-to-project attachment are intentionally owned by H2AgentAdapter/H2M-031 rather than expanding Agent Core or exposing ToolRegistry/transport/MCP internals. Latest full pipeline evidence used: source `cc2b03ddec6e9fd3524e376c7013426308b70ef4`, Actions `35476297321` SUCCESS.

---

# Stage H3 — Add H2AgentAdapter, no UI redesign yet

## [x] H2M-030 — Define H2AgentAdapter

Create one H2-facing service boundary over the accepted Agent engine.

Conceptual operations:

```text
StartTask(projectId?, goal, context?)
ObserveTask(taskId)
CancelTask(taskId)
RespondToApproval(taskId, decision)
GetTaskSummary(taskId)
GetRecentTasks(projectId?)
GetEvidence(id)
AttachProject(taskId, projectId)
```

Exact types should reuse Agent public contracts where appropriate.

Acceptance:

- H2 code does not reference model-specific HTTP payloads;
- H2 code does not execute ToolRegistry directly;
- adapter can be fake-tested.

Evidence: `src/H2Notes.Core/H2AgentAdapter.cs` defines the provider-neutral `IH2AgentAdapter` plus bounded H2 task/progress/approval/evidence DTOs. The interface covers start/observe/cancel/approval/summary/recent/evidence/attach-project and explicitly supports `ProjectId = null`. `docs/H2_AGENT_ADAPTER_CONTRACT.md` freezes ownership boundaries. `H2AgentAdapterContractTests` proves a pure fake can exercise scoped/unscoped tasks, recent queries, evidence, attachment, approval and cancellation, and source/signature guards reject Agent runtime/transport/ToolRegistry/provider types. Exact functional source `84d7b23bf19ea2a96d1ca743d0ec1b1e7a034905`, Actions run `35477060220` SUCCESS, H2 Notes **352/352**, all four H2AgentAdapter contract tests PASS, NAS harness and full Agent/reference-extension/provider/publish pipeline PASS.

---

## [x] H2M-031 — ProjectId correlation without embedding Agent state

Goal:

Associate Agent tasks with H2 projects through an optional project ID/correlation field.

Do not add:

- AgentTask arrays into ProjectRecord;
- full evidence into project JSON;
- full trace into project JSON.

Acceptance:

- Agent run can be queried by ProjectId;
- Agent run with ProjectId = null is valid.

Evidence: `H2AgentTaskCorrelationIndex` stores only `TaskId ↔ ProjectId?` correlation and timestamps; it does not own Agent execution/evidence state and never mutates `ProjectRecord`. It supports project queries, unscoped quick-work queries, later project attachment, bounded recent correlation queries and fail-closed conflicting registration. Architecture tests serialize ProjectRecord before/after correlation operations and reflect its properties to prove no Agent task/verification/tool state was embedded. Exact functional source `dae9adf56a000aa09f0e0c6ff7807083fbaf2ceb`, Actions run `35477369245` SUCCESS, H2 Notes **356/356**, dedicated correlation tests PASS, full Agent/reference-extension/provider/publish pipeline PASS. Publish commit `548fd0827272855e870c7863d2de36403dd9bdb2`; portable ZIP SHA256 `d78048cab78ae8cad75cb165b3a9680989caadee1ccec0ee3d1d44b66da174b6`.

---

## [x] H2M-032 — Define H2 product projections

Add query/view-model layer, not new authoritative databases.

At minimum:

- `ProjectOverviewProjection`;
- `NeedsAttentionProjection`;
- `ProjectActivityProjection`.

They derive from:

- ProjectRecord/TaskRecord;
- AgentAdapter;
- sync/workspace health.

Acceptance:

- projections can be rebuilt from source state;
- deleting a projection cache does not lose project truth.

Evidence: `src/H2Notes.Core/H2ProductProjections.cs` defines rebuildable `ProjectOverviewProjection`, `NeedsAttentionProjection`, `ProjectActivityProjection` and `H2WorkspaceHealthSnapshot` over authoritative ProjectRecord/TaskRecord, `IH2AgentAdapter` and live ProjectWorkspaceStore health. The projection service has no durable/cache store and was corrected to avoid lazy-mutating `DisplayText/DisplayName` getters; legacy text is parsed into temporary objects only. Regression tests prove deterministic task progress/next task, actionable Agent/workspace attention, chronological project/Agent/evidence activity, direct workspace-health derivation, fresh-service rebuild equivalence, no ProjectRecord mutation and no persistence APIs in the projection source. Exact source `8944c06128b101fccc8ef35c08a21799399914cf`, Actions run `35479559324` SUCCESS, H2 Notes **362/362**, all six projection tests PASS, NAS harness + full Agent/reference-extension/provider pipeline PASS. Publish commit `15b0f9acee43fa9d059f41e033cc32b944fbb042`; portable ZIP SHA256 `4a40a8c148301975a75971ded350527e57c3ddc2f4be913fd5a24cec5a744216`.

---

# Stage H4 — Preserve project data model, avoid ProjectState

## [x] H2M-040 — Lock ProjectRecord / TaskRecord responsibilities

Document and guard:

`ProjectRecord`:

- project identity;
- name;
- notes;
- project links;
- project/user tasks;
- durable project content.

`TaskRecord`:

- user/project checklist item.

They must not become Agent execution-step stores.

Acceptance:

- architecture test prevents AgentTask/VerificationReport/ToolRun collections from being added casually to ProjectRecord.

Evidence: `docs/H2_PROJECT_DATA_BOUNDARY.md` freezes ProjectRecord as durable project/user truth and TaskRecord as a human/project checklist item. `H2ProjectDataBoundaryTests` requires existing durable fields, rejects AgentTask/AgentRun/VerificationReport/ToolRun/evidence/trace/progress/ProjectState persistence markers on both ProjectRecord and TaskRecord, rejects dependencies on H2/Agent execution DTOs, and proves external task correlation leaves ProjectRecord JSON unchanged. Exact functional source `5a2edd28d499a6de4bb8a68f4fa8db19554ef36d`, Actions run `35479914331` SUCCESS, H2 Notes full suite + project boundary guards + NAS/Agent/provider/publish pipeline PASS. Publish commit `492e424629ed4224fc3316e6250f85905735daa5`; ZIP SHA256 `f0ee9739ea8f68e2542dc09985bbc7cbe8bfceec5e584a7ca296e5a60474faad`.

---

## [x] H2M-041 — Remove future giant ProjectState requirement

No implementation should create a second durable object containing copies of:

- progress;
- next action;
- Agent run state;
- evidence;
- risks;
- decisions;
- files;

when those values are available from authoritative sources.

Acceptance:

- Command Center uses projection/query services.

Evidence: `H2CommandCenterQueryService` is the sole Command Center-facing query facade introduced by this stage and composes only `H2ProductProjectionService`; it owns no durable truth/cache. `docs/H2_PROJECT_STATE_BOUNDARY.md` freezes the projection-only architecture. `H2ProjectStateArchitectureTests` scan all runtime `src/**/*.cs` for a forbidden `class/record/struct ProjectState`, guard the query facade against hidden state/persistence, prove a fresh query/projection service rebuilds equivalent cards from authoritative inputs, and prove queries do not mutate ProjectRecord. Exact functional source `ce980fc63ca282f2da3cc8ffedb7c9b91579a9f4`, Actions run `35480922743` SUCCESS, H2 Notes **370/370**, ProjectState architecture guards PASS, NAS harness + full Agent/reference-extension/provider/publish pipeline PASS.

---

## [x] H2M-042 — Deterministic project progress only

Keep current deterministic task completion progress.

Do not accept free-form AI percentage writes.

Acceptance:

- project progress shown on board can be traced to a deterministic formula;
- Agent progress is separately shown as criteria/task execution state.

Evidence: `ProjectProgressCalculator` is now the single canonical formula: completed durable `TaskRecord` items / total durable `TaskRecord` items. `ProjectRecord.Progress`, product/Command Center projections and navigator percent all delegate to this calculator. `ProjectRecord.Progress` remains read-only and the model has no free-form AI/Agent percentage field. Agent execution remains a separate `H2AgentTaskStatus/H2AgentProgress` projection and cannot mutate project checklist completion. `docs/H2_DETERMINISTIC_PROJECT_PROGRESS.md` freezes the rule. Exact functional source `7f1f740655da985a06c4fb8fbae5cfadd5050e31`, Actions run `35481246677` SUCCESS, H2 Notes **375/375**, all five deterministic-progress guards PASS, NAS harness + full Agent/reference-extension/provider/publish pipeline PASS.

---

# Stage H5 — Build Project Command Center

## [x] H2M-050 — Create Command Center view

Build a new primary global project overview.

Each row/card must show only high-value fields:

- project name;
- project task completion;
- next project task;
- Agent state if active/recent;
- attention count;
- latest verified activity;
- sync health.

Acceptance:

- user can understand all major projects without entering each project.

Evidence: `MainWindow.CommandCenter.cs` + `MainWindow.axaml` make Command Center the default project surface, aggregate active projects across boards through `H2CommandCenterQueryService`, and show only high-value project name, deterministic task completion, next project task, latest Agent state, attention count, latest verified activity and sync health. Selecting a card reuses the existing project workspace; Projects navigation returns to the global overview. Dedicated H2CommandCenterUiTests pass, and legacy responsive/project-AI regressions were updated to navigate through Command Center before testing workspace behavior rather than weakening runtime semantics. Exact functional source `3581950046411a460d78511ac319cd5a6273ed57`, Actions run `35483265023` SUCCESS, H2 Notes **378/378**, NAS harness + full Agent/reference-extension/provider pipeline + both Windows publishes PASS. Publish commit `a8b1a5b542f88bdaabfaeccb8827ae14512e0316`; portable ZIP SHA256 `df10b9ca2c7c9f0c9b31e4da2e537ff449eb27a5105cf944e9a318eb7b6ef980`.

---

## [x] H2M-051 — Attention projection

Create a “Needs attention” section from real sources.

Sources may include:

- Agent WaitingForApproval;
- Agent Blocked;
- verification failed;
- sync failure;
- user review required.

Do not create an independent AI Inbox database.

Acceptance:

- every attention item deep-links to a real source;
- resolving the source removes/updates the projection automatically.

Evidence: Command Center now renders a dedicated `CommandCenterAttentionSection` from `H2CommandCenterQueryService.GetNeedsAttention(...)` only; no AI Inbox/attention database was added. Agent attention rows retain real `ProjectId` + `AgentTaskId` and deep-link to the owning project; workspace/sync attention links to storage settings. A one-second visible-Command-Center refresh plus normal save/sync refreshes rebuild the list from source state, so resolving a WaitingForApproval/Blocked/Failed source removes or updates the row automatically. Workspace health is one global attention row instead of being multiplied into every project card. Dedicated UI regression proves source IDs, deep-linking and stale-row removal after Agent resolution. Exact functional source `abcd5ac633a39f3a427fe45503d89e3cae645c19`, Actions run `35483760781` SUCCESS, H2 Notes **379/379**, NAS harness + full Agent/reference-extension/provider pipeline + both Windows publishes PASS. Publish commit `3c0b361c4d3bd5a25b1468183a87abfcc4cfa685`; portable ZIP SHA256 `32963b5b56bb513353862c9b84fa6e8d269d2b9b7e744ca0e061b9db7693e58e`.

---

## [x] H2M-052 — Command Center grouping/filtering

Initial groups:

- Needs Attention;
- Working;
- Waiting;
- Normal;
- Completed.

Grouping must be derived.

Acceptance:

- moving between groups does not require an unrelated persisted “board status” update.

Evidence: `H2CommandCenterQueryService.GroupFor(...)` derives the five required groups directly from attention count, Agent lifecycle state, and deterministic project-task completion: **Needs Attention / Working / Waiting / Normal / Completed**. `CommandCenterGroupFilter` offers All plus those five groups and filters disposable `CommandCenterProjectItem` projections only. No BoardStatus/CommandCenterGroup/GroupStatus/DashboardStatus field exists on ProjectRecord or TaskRecord. Dedicated regressions cover all five classifier outputs and prove switching UI filters leaves serialized ProjectRecord/TaskRecord truth byte-equivalent after window initialization. Exact functional source `a91ce7d83054a2e096997975382c7c514d8e36f8`, Actions run `35487219021` SUCCESS, H2 Notes **381/381**, NAS harness + full Agent/reference-extension/provider pipeline + both Windows publishes PASS. Publish commit `e9929c69fce588ca7fd377c41e172ad4e8be4d92`; portable ZIP SHA256 `6f148d2a64053e88d9bf79261d26d16c13bfdd6bbfb8347ee6b0e8e6f0d42e1b`.

---

## [x] H2M-053 — Sync health UI

Display storage health:

- Synced;
- Saving;
- Offline;
- Error/recovery required.

Data comes from storage/sync service.

Do not allow model text to control sync health.

Evidence: `App.CurrentWorkspaceHealth` derives storage health from `ProjectWorkspaceStore` diagnostics/pending/recovery state and overlays the host-owned `_saving` lifecycle as `Busy`; recovery/offline truth has priority over that transient state. Save failures are recorded through `ProjectWorkspaceStore.RecordSyncFailure(ex)`, so failed writes surface real Offline/Warning/RecoveryRequired state instead of merely showing a local-pending label. Command Center maps those enum values to explicit **Đã đồng bộ / Đang lưu / Chờ đồng bộ / Ngoại tuyến / Lỗi đồng bộ / Lỗi · cần phục hồi** labels and diagnostic tooltips. Dedicated regression proves an Agent/model completion cannot override an offline store and verifies Saving/Offline/Recovery/Error labels. Exact functional source `4f257e7d9671bf6fc2697fa0aee71fb9ebe0e8f1`, Actions run `35487603554` SUCCESS, H2 Notes **382/382**, NAS harness + full Agent/reference-extension/provider pipeline + both Windows publishes PASS. Publish commit `b54418f79b1b61daf0a5d015d36b88ff0cc07f50`; portable ZIP SHA256 `204dcb364549aff9cfed31e0b1986143fedbe0d60ac44058407e563705882063`.

---

# Stage H6 — Build AI-first Project Workspace

## [ ] H2M-060 — Reframe MainWindow/project view

Future default project screen:

- compact project status/details;
- Agent surface as primary work area;
- task/note/file/history details accessible secondarily.

Reuse current responsive infrastructure where useful.

Do not preserve current Task+Notes split as the dominant screen solely for backward compatibility.

Acceptance:

- opening project makes it immediately obvious where to ask Agent to work;
- tasks/notes remain one click away.

---

## [ ] H2M-061 — Project Tasks detail

Reuse `ProjectGrid`.

It becomes a project detail/editor panel.

Keep:

- editing;
- completion;
- drag/drop;
- comments;
- search;
- rich text where already supported.

Do not show Agent execution steps as TaskRecord rows.

---

## [ ] H2M-062 — Project Notes detail

Reuse current rich notes/editor.

Notes remain human/project knowledge.

Optional later:

- pin important knowledge;
- link note paragraph to evidence.

Do not build a knowledge graph now.

---

## [ ] H2M-063 — Files/resources detail

Initial scope:

- ProjectLink;
- known project files;
- recent Agent-observed resources;
- open/reveal actions where safe.

Agent handles actual file operations through providers/tools.

Do not mirror entire filesystem metadata into project JSON.

---

## [ ] H2M-064 — Project History projection

Build timeline from:

- project edits;
- Agent task lifecycle;
- verified mutations;
- meaningful evidence;
- sync events.

Do not duplicate all low-level Agent trace events.

Acceptance:

- user sees meaningful work history;
- detailed trace remains available through Agent inspection when needed.

---

## [ ] H2M-065 — Evidence/research inspector

Provide a view into Agent evidence for current project/run.

No separate H2 ResearchStore.

Acceptance:

- official web evidence and file evidence can be inspected;
- evidence identity/provenance remains Agent-owned.

---

# Stage H7 — Replace legacy project AI execution

## [ ] H2M-070 — Convert AiChatPanel into Agent presentation surface

Reuse useful UI:

- composer;
- attachments;
- model/profile chooser where still product-relevant;
- progress rendering;
- chat/history visuals;
- file/evidence cards.

Replace direct send execution with `H2AgentAdapter`.

Acceptance:

- normal new project AI request does not call legacy direct `AiClient` project-chat path.

---

## [ ] H2M-071 — Preserve legacy conversation history

Existing `AiConversation` / `AiMessage` are user data.

Required:

- old conversations remain readable;
- attachments/saved files preserved;
- old provider/model/time metadata preserved;
- no fabricated timestamps.

Choose migration presentation:

- historical read-only thread;
- imported conversation view;
- or safe conversion.

Acceptance:

- no chat data loss.

---

## [ ] H2M-072 — Retire AiProjectContext after parity

Current `AiProjectContext` serializes project/chat context for the legacy chat path.

After Agent Adapter provides project grounding:

- stop using `AiProjectContext.Prepare` for new Agent tasks;
- keep migration compatibility until tests pass;
- then retire.

Acceptance:

- new Agent context is bounded by Agent engine;
- H2 does not build giant history JSON prompt.

---

## [ ] H2M-073 — Retire AiProjectActions pseudo-protocol

Current model-generated:

```text
h2-actions
add_task
append_note
```

is a legacy text protocol.

New Agent integration should use typed H2 project tools/actions through Agent permissions.

Required typed H2 extension tools may initially include only:

- add project task;
- append/update project note;
- read project summary/details.

Do not expose unrestricted ProjectRecord mutation.

Acceptance:

- no parsing model prose/fenced pseudo-action JSON for new Agent operations;
- host validates all typed project mutations.

---

## [ ] H2M-074 — Retire legacy direct project AiClient path

Only after:

- Agent project conversation works;
- provider selection works;
- attachments work;
- cancel works;
- progress works;
- historical chat is preserved;
- project actions parity exists.

Then remove legacy direct execution from normal project flow.

---

# Stage H8 — Work Assistant

## [ ] H2M-080 — Add machine-local Work Assistant settings

Extend `LocalConfiguration` with local-only settings:

- enabled;
- start with H2;
- start collapsed;
- always on top;
- hotkey;
- bubble position;
- preferred monitor;
- notification preference.

Do not persist these in shared project files.

---

## [ ] H2M-081 — Add floating bubble shell

Implement:

- draggable bubble;
- edge-safe placement;
- multi-monitor restore;
- DPI handling;
- idle/working/attention/completed states;
- tray show/hide.

Acceptance:

- bubble does not require main H2 window visible;
- disabled setting fully hides it.

---

## [ ] H2M-082 — Add global hotkey

Hotkey opens compact assistant.

Acceptance:

- no mutation occurs just from hotkey;
- conflict with another system shortcut fails gracefully;
- configurable.

---

## [ ] H2M-083 — Add ActiveWorkContext capture

Capture bounded local context:

- process;
- window;
- application kind;
- document/session identity if provider can identify it;
- selection where available.

Do not persist as project truth.

Acceptance:

- stale target is revalidated before mutation.

---

## [ ] H2M-084 — Add context chips

Show current interpretation:

```text
[Excel] [DuToan.xlsx] [BAOCAOGS] [D51:F80]
```

User can see/remove/change scope before send where appropriate.

---

## [ ] H2M-085 — Quick task through the same Agent Adapter

A quick Work Assistant request starts a normal Agent task with:

```text
ProjectId = null
```

No `QuickWorkSession` model.

Acceptance:

- task runs while panel collapses;
- user can later link task to a project;
- no second Agent runtime.

---

## [ ] H2M-086 — Map Work Assistant permissions to Agent permissions

UI presets map to Agent scope.

No H2-specific execution permission engine.

Acceptance:

- “allow this workbook” does not mean “full PC access”;
- scope expires appropriately.

---

## [ ] H2M-087 — Work Assistant completion UX

Show:

- concise result;
- verified/attention state;
- view details/evidence;
- cancel/retry where applicable;
- link to project;
- open full Project Workspace.

Undo only when underlying provider/Agent action supports a real safe undo path.

Do not promise generic undo for arbitrary external applications.

---

# Stage H9 — Local/shared UI-state cleanup

## [ ] H2M-090 — Audit ProjectLayout storage

Review:

- AI dock mode;
- AI width/height;
- AI X/Y;
- task/note collapse state.

Classify each property as:

- project content;
- user preference;
- machine-local layout.

Move machine-specific geometry out of shared `ProjectRecord`.

Acceptance:

- monitor/window geometry no longer syncs through NAS unless explicitly justified.

---

## [ ] H2M-091 — Audit DesktopSessionState

Keep local desktop session restore logic, but confirm where it is persisted.

Machine-specific window state should live locally.

Do not let another PC inherit invalid monitor coordinates/session windows from NAS.

---

## [ ] H2M-092 — Preserve responsive behavior

Reuse useful current responsive rules:

- compact project picker;
- drawers;
- narrow-window modes;
- no tiny unreadable panes;
- DPI/multi-monitor support.

Adapt them to the new Command Center/Project Workspace instead of reproducing the old information hierarchy.

---

# Stage H10 — Remove over-designed product models

## [ ] H2M-100 — Explicitly reject giant ProjectState implementation

Add architecture guard/documentation.

No second state store duplicating project and Agent data.

---

## [ ] H2M-101 — No independent AI Inbox database

NeedsAttention remains a projection.

---

## [ ] H2M-102 — No independent Research database

Research remains an evidence view.

---

## [ ] H2M-103 — Defer formal ProjectDecision

Use notes/pinned knowledge initially.

Do not implement supersession/effective-date workflow unless separately approved.

---

## [ ] H2M-104 — No QuickWorkSession model

Quick work = AgentTask with no ProjectId.

---

## [ ] H2M-105 — No AI-generated project percentage

Guard UI/data model against arbitrary model-written progress percentage.

---

# Stage H11 — Acceptance scenarios

## [ ] H2M-110 — Command Center 5–10 second test

With realistic project data, user must be able to answer:

- what needs attention;
- what is working;
- what is next;
- whether Agent is waiting;
- whether sync is healthy.

Run actual UX test, not only unit test.

---

## [ ] H2M-111 — Project AI-first workflow

Scenario:

> "Kiểm tra toàn bộ hồ sơ dự án và hoàn thành mọi thứ có thể."

Expected:

- H2 sends project association/context through Agent Adapter;
- Agent creates its own execution plan;
- H2 project checklist is not polluted with every execution step;
- progress visible;
- evidence/verification available;
- final project overview updates through projection.

---

## [ ] H2M-112 — Excel Work Assistant scenario

Foreground Excel workbook.

Request:

> "Đổi toàn bộ công thức trong file đang mở thành tuyệt đối."

Acceptance:

- no need to open H2 main window;
- current workbook context visible;
- structured Excel tool preferred;
- verify result;
- bubble can collapse while running;
- ProjectId optional.

---

## [ ] H2M-113 — Word legal scenario

Foreground unsaved Word document.

Request:

> "Kiểm tra chính tả và rà văn bản pháp lý rồi cập nhật tài liệu."

Acceptance:

- live unsaved state;
- structured Word;
- current authoritative Web evidence;
- scoped changes;
- verify;
- zero pixel calls when structured providers suffice.

---

## [ ] H2M-114 — AutoCAD Work Assistant scenario

Foreground AutoCAD drawing/selection.

Request:

> "Kiểm tra block OTC đang chọn và sửa attribute sai."

Acceptance:

- structured AutoCAD provider;
- selection/document identity;
- bounded mutation;
- reread/verify;
- no unrelated entities changed.

---

## [ ] H2M-115 — Legacy data migration scenario

Open workspace containing:

- old project tasks;
- notes;
- multiple old AI conversations;
- attachments;
- saved files;
- old layout.

Acceptance:

- no data lost;
- historical AI remains viewable;
- new Agent tasks work.

---

## [ ] H2M-116 — Multi-PC projection scenario

Two PCs use same project workspace.

Acceptance after bug-ledger requirements:

- project/task/notes remain consistent;
- machine-local bubble/layout is different per PC;
- Agent task running on PC1 is not falsely represented as locally running on PC2 unless an explicit shared activity mechanism exists;
- completed shared project state does not corrupt workspace.

---

# Stage H12 — Legacy cleanup

## [ ] H2M-120 — Remove legacy AiProjectContext execution dependency

After parity only.

---

## [ ] H2M-121 — Remove legacy AiProjectActions execution dependency

After typed project tools parity only.

---

## [ ] H2M-122 — Remove direct legacy project AiClient runtime

After AgentAdapter parity only.

Keep lower-level AI/provider code only if still used elsewhere and architecturally appropriate.

---

## [ ] H2M-123 — Simplify ProjectAiWindow

Decide:

- reuse as detached Agent workspace;
- or replace with new Project Workspace/Work Assistant behavior.

Do not keep redundant windows merely for legacy.

---

## [ ] H2M-124 — Clean obsolete future-design docs

Do not delete historical evidence.

Add clear headers:

- historical;
- superseded for future architecture;
- current implementation evidence only.

The future H2 source of truth must remain two files:

- `H2_PRODUCT_MASTER_SPEC.md`;
- `H2_PRODUCT_MASTER_TASKS.md`;

plus the independent bug ledger.

---

# Stage H13 — Final product acceptance

## [ ] H2M-130 — Product correctness report

Report by surface:

- Command Center;
- Project Workspace;
- Work Assistant;
- persistence/NAS;
- Agent integration;
- legacy migration.

---

## [ ] H2M-131 — UX responsiveness report

Test:

- narrow;
- medium;
- wide;
- full screen;
- 100/125/150/200% DPI where feasible;
- multi-monitor;
- keyboard navigation;
- IME Vietnamese typing.

---

## [ ] H2M-132 — Resource/performance report

Measure:

- startup;
- project-switch latency;
- Command Center render;
- Agent progress updates;
- background bubble idle cost;
- workspace save;
- large project behavior.

Do not optimize based only on assumptions.

---

## [ ] H2M-133 — Data integrity gate

Require:

- no unresolved silent HIGH/CRITICAL data-integrity bug;
- real NAS evidence where required;
- migration rollback path;
- old data preserved.

---

## [ ] H2M-134 — User product acceptance

User explicitly approves:

- Command Center;
- Project Workspace;
- Work Assistant;
- legacy migration;
- Agent behavior;
- remaining limitations.

Only then treat the redesign as accepted production direction.

---

# KEEP / MODIFY / RETIRE checklist

## KEEP

```text
ProjectRecord
TaskRecord
NoteRecord
ProjectLink
RichDocument
ProjectWorkspaceStore   (after bug fixes)
WorkspaceTransfer
INoteStorage
ProjectGrid
RichEditor
SecretVault
LocalConfiguration
WindowPlacement helpers
responsive infrastructure
existing conversation data
```

## MODIFY

```text
MainWindow
AiChatPanel
ProjectAiWindow
ProjectLayout
DesktopSessionState placement ownership
App tray/navigation
AI settings integration
project summary UI
```

## RETIRE AFTER PARITY

```text
AiProjectContext new-task path
AiProjectActions pseudo-action protocol
legacy direct project AiClient execution
automatic model-text project-action parsing
legacy AI runtime-specific context construction
```

## DO NOT BUILD NOW

```text
Giant ProjectState
AIInboxStore
ResearchStore
formal ProjectDecision subsystem
QuickWorkSession
second evidence database
second Agent task database
second permission engine
AI-invented project percentage
distributed Agent cluster
knowledge graph
```

---

# Final execution order

```text
FIX STORAGE FOUNDATION
       ↓
WAIT FOR ACCEPTED AGENT CORE
       ↓
ADD H2AgentAdapter
       ↓
BUILD PROJECTIONS
       ↓
BUILD COMMAND CENTER
       ↓
BUILD AI-FIRST PROJECT WORKSPACE
       ↓
MIGRATE LEGACY AI UI TO AGENT
       ↓
ADD WORK ASSISTANT
       ↓
MOVE LOCAL UI STATE OUT OF SHARED PROJECT DATA
       ↓
CLEAN LEGACY
       ↓
REAL UX / NAS / AGENT ACCEPTANCE
```

Do not create more product subsystems before these three surfaces work well:

```text
Command Center
Project Workspace
Work Assistant
```

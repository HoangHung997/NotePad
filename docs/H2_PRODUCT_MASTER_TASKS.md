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
> H2 Agent integration remains blocked until the Agent user-acceptance gate is explicitly passed.

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

## [ ] H2M-000 — Record current implementation baseline

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

---

## [ ] H2M-001 — Mark future product documentation authority

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

---

# Stage H1 — Storage/data integrity before new shared state

## [ ] H2M-010 — Triage all non-AI storage bugs

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

---

## [ ] H2M-011 — Fix/close journal final-verification ordering risk

Target:

H2-NONAI-002.

Required direction:

- do not delete the recovery journal before final committed snapshot validation;
- prove failed final validation still has a recoverable transaction path;
- deterministic fault-injection test.

Acceptance:

- bad committed generation can be recovered without relying on already-deleted journal state.

---

## [ ] H2M-012 — Resolve persistent mixed-generation recovery path

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

---

## [ ] H2M-013 — Real NAS/SMB capability acceptance

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

---

## [ ] H2M-014 — Workspace identity and network location model

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

---

## [ ] H2M-015 — Offline durability decision

Target:

H2-NONAI-007.

Choose and implement or explicitly accept limitation for:

- durable local pending operations;
- draft durability while NAS unavailable;
- conflict behavior after reconnect.

Do not accidentally expand scope into full distributed database synchronization.

---

# Stage H2 — Wait for Agent acceptance boundary

## [ ] H2M-020 — Verify Agent integration gate

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

---

# Stage H3 — Add H2AgentAdapter, no UI redesign yet

## [ ] H2M-030 — Define H2AgentAdapter

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

---

## [ ] H2M-031 — ProjectId correlation without embedding Agent state

Goal:

Associate Agent tasks with H2 projects through an optional project ID/correlation field.

Do not add:

- AgentTask arrays into ProjectRecord;
- full evidence into project JSON;
- full trace into project JSON.

Acceptance:

- Agent run can be queried by ProjectId;
- Agent run with ProjectId = null is valid.

---

## [ ] H2M-032 — Define H2 product projections

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

---

# Stage H4 — Preserve project data model, avoid ProjectState

## [ ] H2M-040 — Lock ProjectRecord / TaskRecord responsibilities

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

---

## [ ] H2M-041 — Remove future giant ProjectState requirement

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

---

## [ ] H2M-042 — Deterministic project progress only

Keep current deterministic task completion progress.

Do not accept free-form AI percentage writes.

Acceptance:

- project progress shown on board can be traced to a deterministic formula;
- Agent progress is separately shown as criteria/task execution state.

---

# Stage H5 — Build Project Command Center

## [ ] H2M-050 — Create Command Center view

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

---

## [ ] H2M-051 — Attention projection

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

---

## [ ] H2M-052 — Command Center grouping/filtering

Initial groups:

- Needs Attention;
- Working;
- Waiting;
- Normal;
- Completed.

Grouping must be derived.

Acceptance:

- moving between groups does not require an unrelated persisted “board status” update.

---

## [ ] H2M-053 — Sync health UI

Display storage health:

- Synced;
- Saving;
- Offline;
- Error/recovery required.

Data comes from storage/sync service.

Do not allow model text to control sync health.

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

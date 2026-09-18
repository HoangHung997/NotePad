# H2 Notes — AI-First Product Master Specification

Status: **CANONICAL FUTURE H2 PRODUCT SPECIFICATION**  
Date: 2026-09-18  
Implementation timing: **After H2 Agent core passes its master acceptance gate and the user explicitly approves integration.**

> This document replaces `docs/H2_AI_PROJECT_COMMAND_CENTER_REDESIGN.md` as the normative future-product specification.
>
> Read together with:
>
> - `docs/H2_PRODUCT_MASTER_TASKS.md`
> - `docs/H2_AGENT_MASTER_SPEC.md`
> - `docs/H2_AGENT_MASTER_TASKS.md`
> - `docs/H2_NOTES_NON_AI_BUG_LEDGER.md`
>
> Historical/current-baseline UI documents remain useful for migration and visual comparison, but they do not override this future information architecture.

---

## 1. Product reset

H2 Notes should no longer evolve as:

```text
notes app
+ tasks
+ AI chat
+ AI inbox
+ research database
+ decision database
+ agent task database
+ evidence database
+ many more subsystems
```

The approved direction is simpler:

> **H2 is the project shell around the Agent.**

H2 owns:

- durable user project data;
- project navigation;
- project overview;
- project workspace UI;
- local desktop UX;
- workspace/NAS persistence;
- the adapter that connects a project to the Agent engine.

Agent Core owns:

- model/runtime loop;
- tool execution;
- task execution state;
- verification;
- repair;
- evidence/artifacts;
- tool/plugin/skill/provider behavior;
- permission enforcement for Agent actions;
- runtime trace/progress.

H2 must not reimplement those Agent responsibilities.

---

## 2. Product identity

Target product:

```text
H2 Project AI
=
Project data
+ Project Command Center
+ AI-first Project Workspace
+ Work Assistant
+ Agent integration
+ NAS/multi-device project storage
```

The user normally performs two actions:

1. **Look at H2** to understand project state.
2. **Ask the Agent** to perform work.

Tasks/notes/files remain available, but they are supporting project data and details rather than the primary interaction flow.

---

## 3. Minimum Useful AI Project Shell

H2 should have exactly three primary product surfaces:

```text
1. PROJECT COMMAND CENTER
   See all projects quickly.

2. PROJECT WORKSPACE
   Work inside one project; AI is the primary interaction surface.

3. WORK ASSISTANT
   Invoke the same Agent quickly from the current desktop application.
```

Other concepts such as:

- Tasks;
- Notes;
- Files;
- History;
- Evidence;
- Research;
- Approvals;
- Decisions;

are views/details supporting these three surfaces.

Do not turn each of them into a separate top-level application or independent state engine unless later evidence proves that is necessary.

---

## 4. Source-of-truth boundaries

The redesign must prevent duplicate truth.

### 4.1 H2 durable project truth

H2 remains authoritative for user-maintained project data:

```text
ProjectRecord
TaskRecord
Project notes
Project links/resources
User-authored project knowledge
Project ordering
Project archive state
```

### 4.2 Agent runtime truth

Agent engine remains authoritative for:

```text
Agent task/run state
Task contract
Execution criteria
Tool calls
Verification
Repair
Evidence
Artifacts
Agent trace/progress
Installed capabilities used by the task
```

### 4.3 H2 UI projections

Command Center, attention summaries and activity views are derived projections.

They are **not independent truth stores**.

### 4.4 Local desktop state

Machine-local UI state remains local:

- window placement;
- Work Assistant bubble position;
- global hotkey;
- active foreground app/document context;
- temporary panel state;
- monitor selection;
- per-machine permission/session grants where appropriate.

Do not sync those through project NAS data.

---

## 5. Do not create a second ProjectState database

The previous redesign proposed a large durable structure similar to:

```text
ProjectState
{
    Status
    Progress
    NextActions[]
    Blockers[]
    Risks[]
    Deadlines[]
    ActiveAgentRuns[]
    PendingDecisions[]
    LatestVerifiedResults[]
    ImportantFiles[]
    KnowledgeRefs[]
}
```

This is no longer the preferred design.

It duplicates information already held by:

```text
ProjectRecord / TaskRecord
+
Agent task state / verification / evidence
+
workspace/sync state
```

A duplicate ProjectState can drift from those sources.

Instead use a projection.

---

## 6. ProjectOverviewProjection

The board should render a computed projection.

Conceptually:

```text
ProjectOverviewProjection
{
    ProjectId
    Title

    UserTaskCompleted
    UserTaskTotal
    NextUserTask

    AgentState?
    AttentionCount
    AttentionSummary?

    LastVerifiedActivity?
    SyncHealth
}
```

This object may be a view model or query result.

It should not become another independently persisted source of truth.

Possible sources:

```text
ProjectRecord
+
AgentAdapter task/run query
+
workspace/sync health
        ↓
ProjectOverviewProjection
        ↓
Command Center UI
```

---

## 7. Do not let AI invent project progress percentages

H2 currently has a deterministic project progress concept:

```text
completed project tasks / total project tasks
```

Keep deterministic values where available.

Do not permit the model to invent:

```text
68%
72%
+8%
```

without a defined calculation.

Prefer:

```text
Project tasks: 3/5 complete
Agent run: 4/6 verification criteria passed
Status: working
Needs attention: 1
```

A future project-type plugin may define specialized progress formulas if needed.

---

## 8. Project tasks and Agent tasks are different

This distinction is mandatory.

### Project task

Long-lived user/project management item.

Examples:

- prepare quantity takeoff;
- complete estimate;
- submit dossier;
- wait for owner confirmation.

Existing `TaskRecord` represents this category.

### Agent task

Execution plan/internal task generated for one Agent run.

Examples:

- read Word;
- inspect Excel;
- search current legal source;
- patch document;
- verify formula;
- repair failed criterion.

Agent tasks belong to Agent engine state.

Do not copy every Agent execution step into `ProjectRecord.ChecklistItems`.

Relationship:

```text
Project task:
"Complete estimate dossier"
        |
        +-- Agent run
              |- read Word
              |- inspect Excel
              |- research law
              |- patch
              |- verify
```

H2 may display a compact Agent progress summary under the project task without converting those execution steps into project checklist rows.

---

## 9. Command Center

The Command Center is the primary monitoring surface.

The user should understand major project state in approximately 5–10 seconds.

A compact project card may show:

```text
┌──────────────────────────────────────┐
│ RPBMVN THẠCH BÍCH                    │
│                                      │
│ Công việc: 3/5                       │
│ Tiếp theo: Hoàn thiện dự toán        │
│                                      │
│ ● AI đang kiểm hồ sơ                 │
│ ⚠ 1 việc cần bạn xem                │
│                                      │
│ Vừa xong: Rà pháp lý · Verified ✓   │
└──────────────────────────────────────┘
```

Default groupings can be simple:

```text
NEEDS ATTENTION
WORKING
WAITING
NORMAL
COMPLETED
```

Grouping is a UI projection from real state.

Do not store a separate mutable board status merely to drive grouping when it can be derived.

---

## 10. Attention view replaces a separate AI Inbox subsystem

The UI concept “AI Inbox” is useful.

The implementation should be a projection:

```text
NeedsAttentionProjection
```

derived from events such as:

- Agent waiting for approval;
- Agent blocked;
- verification failed;
- workspace sync failed;
- current task requires user decision;
- resource unavailable;
- task ready for review.

Do not create a second durable AI Inbox queue unless later real requirements demand it.

UI examples:

```text
CẦN BẠN XỬ LÝ · 3

- Agent cần quyền sửa workbook.
- Verification còn 2 tiêu chí fail.
- NAS chưa đồng bộ.
```

Each row links to its real source.

---

## 11. Project Workspace

When opening a project, AI interaction is the main work surface.

Conceptual layout:

```text
┌──────────────────────┬──────────────────────────────┐
│ PROJECT              │ AGENT                        │
│                      │                              │
│ 3/5 công việc        │ > Hoàn thiện hồ sơ này...   │
│ Next: Dự toán        │                              │
│ ⚠ 1 cần xem          │ ✓ đọc Word                 │
│                      │ ✓ kiểm Excel               │
│ [Công việc]          │ ● tra pháp lý              │
│ [Ghi chú]            │                              │
│ [Tệp]                │                              │
│ [Lịch sử]            │                              │
└──────────────────────┴──────────────────────────────┘
```

AI is primary.

Project details remain accessible without dominating the default layout.

---

## 12. Detail drawers, not separate applications

Project Workspace may expose:

- Tasks;
- Notes;
- Files;
- History;
- Evidence;
- Research;
- Approvals.

These should initially be:

- drawers;
- tabs;
- inspectors;
- side panels;
- filtered projections.

Do not create a separate persistent subsystem for each concept unless later user workflows prove it is necessary.

---

## 13. Project notes become durable human/project knowledge

Keep the current notes system.

Notes remain:

- user-authored knowledge;
- project facts;
- meeting notes;
- constraints;
- requirements;
- decisions in plain form;
- references.

Do not require a new formal Knowledge database for the first redesign.

A future lightweight “pin as important knowledge” feature is allowed, but should reuse notes/metadata rather than introduce a complex knowledge graph immediately.

---

## 14. Formal ProjectDecision is deferred

The previous redesign proposed a rich `ProjectDecision` model with effective dates, superseding links and evidence.

That is useful for some workflows but not required for the first AI-first H2 product.

Initial approach:

- save important user decisions in project notes/knowledge;
- optionally mark/pin them;
- allow Agent to reference them.

Add a formal decision module later only if real projects need:

- approval workflow;
- supersession;
- effective dates;
- decision status;
- compliance audit.

---

## 15. Research is an Agent evidence view

Do not create a separate H2 Research database initially.

Agent Core already owns:

- WebEvidence;
- artifacts;
- source provenance;
- verification;
- task trace.

H2 may show a Research view by querying Agent data associated with the project/run.

Concept:

```text
Project
 -> Agent runs
      -> evidence
           -> WebEvidence
           -> files
           -> artifacts

H2 Research tab
 -> projection of those sources
```

Do not copy all evidence into ProjectRecord.

---

## 16. Activity / History is a projection

Do not create multiple independent truth logs.

Project History should merge/project meaningful events from:

- user project edits;
- Agent task lifecycle;
- verified mutations;
- important evidence;
- workspace/sync events.

Example:

```text
10:42  User asked Agent to review legal references
10:43  Agent run started
10:47  Official web evidence added
10:50  Word document updated
10:51  Verification PASS
```

The event sources remain authoritative.

H2 timeline is a presentation layer.

---

## 17. Files/resources

Files are project resources.

Current `ProjectLink` should be preserved and may be extended carefully later.

Initial project file/resource features may include:

- linked files/folders;
- recently used project files;
- Agent-observed files;
- verification status where available.

Do not embed file bytes or full external file state into ProjectRecord.

Agent accesses files through approved tool/provider scopes.

---

## 18. H2 Agent Adapter

H2 must integrate Agent through one public adapter/service boundary.

Concept:

```text
H2 UI
   |
   v
H2AgentAdapter
   |
   v
Accepted Agent Engine
```

H2 UI should not directly know:

- OpenAI/Ollama wire format;
- ToolRegistry internals;
- plugin internals;
- verifier internals;
- MCP JSON-RPC.

The adapter should expose product-level operations such as:

```text
StartTask
ObserveTask
CancelTask
Approve/deny pending action
GetTaskSummary
GetEvidence
GetRecentProjectRuns
Attach/detach ProjectId
```

Exact API follows the accepted Agent integration boundary from the Agent master tracker.

---

## 19. Project association with Agent runs

Do not embed the whole Agent task/run into `ProjectRecord`.

An Agent task may carry an optional external project correlation:

```text
ProjectId?
```

Agent store remains authoritative for the run.

H2 queries Agent runs by ProjectId.

This gives:

```text
ProjectRecord
  no Agent execution internals embedded

AgentTask
  ProjectId = project.Id
```

For ad-hoc work:

```text
ProjectId = null
```

This also eliminates the need for a separate `QuickWorkSession` model.

---

## 20. Quick work is just an Agent task without a project

Previous design introduced:

```text
QuickWorkSession
```

Do not add it.

Use:

```text
AgentTask
{
    ProjectId? = null
    ActiveWorkContext = current desktop context
}
```

Later the user may link the completed/current task to a project if appropriate.

No user request repetition is required.

---

## 21. Work Assistant remains a first-class feature

Keep the floating desktop assistant.

Its product purpose is:

> **Do not make the user open H2 just to ask H2 to work on the application already in front of them.**

The Work Assistant is a thin UI over the same Agent engine.

Concept:

```text
● H2 bubble
   |
   v
Quick Assistant panel
   |
   v
H2AgentAdapter
   |
   v
Agent Engine
```

No second AI engine.

No second plugin system.

No second evidence store.

No second permission engine.

---

## 22. Work Assistant UI states

Keep three states.

### Collapsed bubble

```text
● H2
```

Optional visual state:

```text
● H2       idle
● H2 ⟳     working
● H2 !     needs attention
● H2 ✓     completed
```

### Quick panel

Shows:

- prompt input;
- active context chips;
- current Agent state;
- cancel;
- open full project/workspace.

### Running/result panel

Shows typed Agent progress and concise result.

Clicking outside may collapse to bubble while Agent continues.

---

## 23. Global hotkey

Keep configurable global hotkey.

Example candidate:

```text
Ctrl + Shift + Space
```

Behavior:

```text
foreground app
 -> hotkey
 -> capture ActiveWorkContext
 -> open prompt panel
```

Hotkey never silently performs a mutation.

---

## 24. ActiveWorkContext is ephemeral local state

Keep concept:

```text
ActiveWorkContext
{
    ProcessId
    ApplicationKind
    WindowIdentity
    DocumentSessionId?
    DocumentPath?
    Selection?
    CapturedUtc
}
```

This belongs to the current machine/session.

Do not persist it as shared project truth.

Before mutation, Agent/provider must revalidate the target.

---

## 25. Context chips

Quick Assistant should show what it thinks the target is.

Examples:

```text
[Excel] [DuToan.xlsx] [BAOCAOGS] [D51:F80]
```

```text
[AutoCAD] [BanVe01.dwg] [14 selected entities]
```

This is UI confirmation of current Agent grounding.

---

## 26. Permission UI maps to Agent Core permissions

Keep user-facing presets such as:

- Observe only;
- Ask before changes;
- Allow changes in this document/session;
- Use project policy.

But H2 must map them into Agent Core permission scopes.

Do not build a new H2-specific execution-permission engine.

Example:

```text
UI:
"Allow changes in this workbook"

maps to Agent policy:
resource = workbook-session-X
mutation = allowed
lifetime = session/document
```

---

## 27. Structured adapter preference

Keep the generic preference:

```text
structured typed app/provider interface
 > app API / MCP / native plugin
 > direct file adapter
 > UI Automation
 > screenshot / mouse / keyboard
```

H2 Work Assistant only captures UI context.

Agent Core/provider selection determines the actual tool path.

---

## 28. Work Assistant examples

### Excel

User:

> "Đổi toàn bộ công thức trong file Excel đang mở thành tuyệt đối."

Flow:

```text
Work Assistant
 -> current Excel context
 -> Agent
 -> structured Excel provider
 -> read formulas
 -> patch
 -> recalc if needed
 -> reread
 -> verify
 -> result
```

Target pixel calls: 0 when structured Excel tools are available.

### Word

User:

> "Kiểm tra chính tả và rà các văn bản pháp lý trong file đang mở."

Flow:

```text
Word provider
 -> current unsaved document
 -> spelling/citation extraction
 -> Web provider
 -> evidence
 -> scoped patch
 -> reread
 -> verify
```

### AutoCAD

User:

> "Kiểm tra các block OTC đang chọn và sửa attribute sai."

Flow:

```text
AutoCAD provider
 -> current drawing/selection
 -> inspect
 -> bounded mutation
 -> reread
 -> verify
```

---

## 29. Existing H2 code audit

### 29.1 KEEP — durable project/user data

Keep:

```text
src/H2Notes.Core/SheetModels.cs

ProjectRecord
TaskRecord
NoteRecord
ProjectLink
RichDocument structures
SheetOperations
```

`TaskRecord` remains a project/user task, not Agent execution state.

---

### 29.2 KEEP — persistence foundation, but fix bug ledger first

Keep:

```text
ProjectWorkspaceStore
WorkspaceTransfer
INoteStorage
LocalConfiguration
SecretVault
```

But persistent schema expansion must not proceed while critical storage integrity risks are ignored.

Before new shared Agent/project metadata is added, read and resolve/accept the relevant items in:

`docs/H2_NOTES_NON_AI_BUG_LEDGER.md`

Especially:

- cross-PC hash mismatch;
- journal/final verification ordering;
- SMB/NAS filesystem assumptions;
- mixed-generation recovery;
- real NAS test coverage;
- network endpoint identity/failover;
- workspace identity/path aliases.

---

### 29.3 KEEP — current project editing controls

Keep/reuse:

```text
ProjectGrid
RichEditor
existing project/task editing
search
drag/drop
responsive shell pieces
window placement utilities
```

ProjectGrid becomes a detail/editor view rather than the future primary product surface.

---

### 29.4 MODIFY — MainWindow information architecture

Current `MainWindow.axaml` centers:

- project list;
- tasks;
- notes;
- docked AI.

Future layout should center:

- Command Center view; or
- AI-first Project Workspace.

Reuse responsive/window infrastructure where useful.

Do not preserve an old layout merely because it already exists.

---

### 29.5 MODIFY — AiChatPanel

Current `AiChatPanel` contains valuable presentation work:

- composer;
- attachments;
- model picker;
- reasoning/progress UI;
- file cards;
- chat history rendering;
- responsive behavior.

Reuse presentation components selectively.

But the execution path must move from direct legacy AI calls to `H2AgentAdapter`.

The panel should become an Agent task/conversation surface, not its own model runtime.

---

### 29.6 RETIRE AFTER AGENT INTEGRATION — legacy project AI execution

The following are legacy runtime paths after Agent integration:

```text
AiProjectContext
AiProjectActions
direct AiClient project-chat execution
pseudo-action parsing from model text
automatic application of h2-actions text blocks
```

Do not delete before:

- Agent Adapter exists;
- project chat parity exists;
- historical chat migration is safe;
- tests pass.

---

### 29.7 PRESERVE HISTORY — AiConversation / AiMessage

Existing conversations are user data.

Do not delete them during redesign.

Options:

- keep legacy history readable;
- migrate into an Agent-conversation presentation model;
- preserve old provider/model/file metadata.

Do not use legacy `AiConversation` as the authoritative store for new Agent execution state.

---

### 29.8 MODIFY — ProjectAiWindow

The existing detached Project AI window is useful evidence that users value an independent AI surface.

It may be adapted into:

- detached Project Workspace Agent panel; or
- transitional shell before Work Assistant.

Do not maintain a second Agent engine inside it.

---

### 29.9 REVIEW — ProjectLayout

Current `ProjectLayout` stores many UI properties in shared project data:

- AI dock mode;
- AI width/height/position;
- task/notes collapse state.

Review which settings are truly project content vs machine-local presentation.

Work Assistant position/hotkey is definitely local.

Window geometry and monitor-specific layout should normally remain local.

Avoid syncing machine-specific geometry through NAS project files.

---

### 29.10 MODIFY — LocalConfiguration

This is the correct place for machine-local settings.

Future examples:

```text
WorkAssistantEnabled
WorkAssistantStartup
BubblePosition
PreferredMonitor
GlobalHotkey
Local layout preferences
Agent connection/profile preferences
```

Do not put credentials into shared project files.

---

## 30. Remove or defer over-designed concepts

### Do not create now

- giant durable `ProjectState`;
- independent `AIInboxStore`;
- independent `ResearchStore`;
- formal `ProjectDecision` subsystem;
- `QuickWorkSession`;
- second H2 evidence database;
- second H2 Agent-task database;
- second H2 permission engine;
- AI-generated arbitrary project percentage;
- project-embedded full Agent runs;
- project-embedded tool traces.

### Defer until measured need

- formal decision supersession/effective-date workflow;
- project knowledge graph;
- cross-device live Agent-run replication;
- automatic project inference for every ad-hoc task;
- specialized progress algorithms;
- complicated AI risk scoring.

Keep extension points, not unused complexity.

---

## 31. Project/Agent storage boundary

Initial target:

```text
Shared project workspace
  -> ProjectRecord
  -> TaskRecord
  -> Notes
  -> Links
  -> minimal durable H2 project data

Agent state store
  -> Agent tasks/runs
  -> evidence
  -> artifacts
  -> verification
  -> trace

Local config/state
  -> window layout
  -> bubble
  -> hotkey
  -> active desktop context
```

Project association is by ID/reference, not by copying Agent state into the project JSON.

---

## 32. Cross-device implications

Do not assume Agent tasks running on PC1 automatically exist live on PC2.

First version may define:

- project data sync through existing H2 workspace;
- Agent task execution stays on the machine running it;
- completed verified Agent activity may later publish a compact project activity reference if needed.

Do not build distributed Agent execution before the NAS/project store itself is stable.

---

## 33. Sync health belongs on the board

Because H2 is multi-device/NAS-aware, Command Center should show project/workspace health.

Examples:

```text
✓ Synced
● Saving
⚠ NAS unavailable
⚠ Workspace conflict/recovery required
```

This state comes from storage/sync service, not the model.

---

## 34. Existing responsive UI remains visual/migration evidence

Historical files such as:

- `APPROVED_PRODUCT_SPEC.md`;
- `RESPONSIVE_IMPLEMENTATION.md`;
- `UI_ACCEPTANCE.md`;
- UI concept image sets;

remain valuable for:

- visual style;
- responsive thresholds;
- current behavior;
- regression/migration knowledge.

They are not the future product information-architecture authority after this master specification.

Do not delete them.

---

## 35. Migration principle

The redesign must preserve user data.

Preserve:

- projects;
- tasks;
- notes/rich text;
- links;
- conversations/history;
- attachments;
- saved files;
- layout where still relevant;
- workspace migration history.

Do not convert existing project tasks into Agent tasks.

Do not delete chat history simply because new Agent conversations use a different engine.

---

## 36. H2 Agent integration boundary

H2 should only consume the accepted Agent public surface.

Conceptual product API:

```text
StartTask(projectId?, goal, context?)
ObserveTask(taskId)
CancelTask(taskId)
RespondToApproval(taskId, decision)
GetTaskSummary(taskId)
GetRecentTasks(projectId?)
GetEvidence(taskId/evidenceId)
AttachProject(taskId, projectId)
```

Exact types follow the Agent master integration boundary.

H2 must not depend directly on:

- model-specific transports;
- plugin internals;
- ToolRegistry internals;
- MCP protocol internals.

---

## 37. Command Center acceptance

User opens H2 and within approximately 5–10 seconds can answer:

- which project needs attention;
- what project is currently active;
- what the next user project task is;
- whether Agent is working/waiting/blocked;
- whether workspace sync is healthy;
- what was most recently verified.

No project needs to be opened to answer those questions.

---

## 38. Project Workspace acceptance

User opens one project and can:

- immediately ask Agent to work;
- see current project tasks/next task;
- inspect notes/files/history only when needed;
- observe typed Agent progress;
- approve/deny scoped changes;
- inspect verification/evidence;
- continue working without manually maintaining Agent execution steps.

---

## 39. Work Assistant acceptance

User is in another application and can:

1. invoke H2 via bubble/hotkey;
2. see current context;
3. state a goal;
4. Agent works through structured providers/tools;
5. panel may collapse while task continues;
6. completion/attention state is shown;
7. user may link the task to a project;
8. full H2 window never had to be opened first.

---

## 40. Non-goals for first redesign

Do not attempt to build:

- autonomous project manager that invents business priorities;
- distributed multi-PC Agent cluster;
- universal knowledge graph;
- universal decision-management system;
- universal research database;
- autonomous unattended project progress scoring;
- all possible Work Assistant integrations;
- every future plugin UI.

Build the shell around the accepted Agent first.

---

## 41. Core product rule

The redesign should remain understandable in three sentences:

> **Agent is the primary way to do work.**  
> **Command Center is the primary way to understand projects.**  
> **H2 stores project truth and projects Agent truth into the UI without duplicating the Agent engine.**

Or more compactly:

```text
Ask Agent to work.
Use H2 to understand.
Open details only when needed.
```

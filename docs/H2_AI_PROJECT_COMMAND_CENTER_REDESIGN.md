# H2 Notes Redesign — AI Project Command Center

Status: **Approved direction / future H2 redesign specification**  
Date: 2026-09-18  
Scope: H2 Notes product UX + project-state architecture after Agent Lab acceptance  
Implementation timing: **Do not disrupt current Agent Lab work. Revisit during Phase 13 H2 Notes integration.**

---

## 1. Product direction

H2 Notes is no longer primarily a note/task application with an AI chat added on top.

The future product direction is:

> **AI is the primary way the user works.  
> The project board is the primary way the user understands status.  
> Tasks, notes, files, research, decisions and execution details become structured project state behind the AI.**

Target product identity:

```text
H2 Project AI
=
AI Agent
+ Project Command Center
+ Project Memory
+ Structured project state
+ Files / Word / Excel / AutoCAD
+ Web research
+ MCP / plugins / skills
+ Verification / evidence
+ Human decisions
```

H2 should feel less like "a notes app with AI" and more like an **AI project operating system / command center**.

---

## 2. UX principle

The user should normally do two things:

1. **Look at the board** and understand the project quickly.
2. **Tell the AI the desired outcome** instead of manually maintaining many tasks and notes.

Example:

Instead of the user manually creating:

```text
[ ] read Word
[ ] inspect Excel
[ ] search new legal documents
[ ] update report
[ ] check result
```

the user should be able to say:

> "Check the whole project package, verify the legal references, update the report and tell me what is still missing."

The agent then creates/maintains the internal execution state itself.

---

## 3. Primary information architecture

The future H2 UI should be organized conceptually as:

```text
H2 PROJECT AI
|
+-- 1. PROJECT BOARD / COMMAND CENTER
|      quick project understanding
|
+-- 2. AI WORKSPACE
|      primary user interaction
|
+-- 3. PROJECT STATE
|      tasks / notes / files / research / decisions / evidence
|
+-- 4. ACTIVITY / HISTORY
       what AI/user/tools changed and verified
```

Tasks and notes remain important, but they are no longer the primary top-level workflow.

---

## 4. Project Board / Command Center

The board must let the user understand a project in approximately 5–10 seconds.

A project card should emphasize decision-relevant state, not raw checklist volume.

Conceptual card:

```text
┌────────────────────────────────────────────┐
│ PROJECT: RPBMVN THACH BICH                 │
│ STATUS: IN PROGRESS                        │
│                                            │
│ Progress                  68%               │
│ Important items             3               │
│ Waiting / blocked            2               │
│ Warnings                     1               │
│                                            │
│ NEXT: Complete estimate + legal review     │
│ BLOCKER: Waiting on updated legal basis    │
│                                            │
│ AI: 11:42 · Reviewed 4 project files       │
└────────────────────────────────────────────┘
```

### Required board fields

At minimum, each project should be able to expose:

- project name;
- high-level status;
- progress;
- next important action;
- blockers;
- risks/warnings;
- pending decisions requiring the user;
- current/last agent activity;
- important deadlines;
- latest verified result;
- stale/out-of-date indicator where appropriate.

### Board grouping

The default board should prioritize attention, for example:

```text
NEEDS ATTENTION
IN PROGRESS
WAITING
STABLE
COMPLETED
```

This is preferred over requiring the user to manually maintain a traditional Kanban board.

---

## 5. AI Workspace becomes the primary project screen

When the user opens a project, AI interaction should be the main working surface.

Conceptual layout:

```text
┌────────────────────┬──────────────────────────────────┐
│ PROJECT STATUS     │ AI WORKSPACE                     │
│                    │                                  │
│ Progress 68%       │ What do you want done?           │
│ 3 need attention  │                                  │
│ 1 blocker         │ > Check the whole package...      │
│                    │                                  │
│ NEXT               │ Agent activity                   │
│ Estimate + legal   │ ✓ read Word                      │
│                    │ ✓ inspect Excel                  │
│ BLOCKER            │ ● research legal updates        │
│ New regulation    │ ○ update report                  │
│                    │                                  │
├────────────────────┤                                  │
│ FILES              │                                  │
│ DECISIONS          │                                  │
│ RESEARCH           │                                  │
│ HISTORY            │                                  │
└────────────────────┴──────────────────────────────────┘
```

The user should not have to navigate through Tasks -> Notes -> Chat just to ask the AI to work.

The hierarchy should instead feel like:

```text
Project
  -> AI
      -> Tasks
      -> Notes
      -> Files
      -> Research
      -> Decisions
      -> Evidence
```

---

## 6. Tasks remain, but become Agent execution state

Tasks must not be removed.

Their role changes.

Instead of requiring the user to manually maintain every execution step, the agent may create and update structured task state from the user's goal.

Example:

```text
Goal:
"Complete the project estimate package"

Internal agent plan:
  C1 Read source documents
  C2 Audit Excel estimate
  C3 Check current legal references
  C4 Update Word report
  C5 Verify final package
```

The board may show only:

```text
Complete project estimate package
In progress · 3/5 verified
```

Detailed criteria/tasks are expanded only when the user wants deeper inspection.

### Task principles

- stable IDs;
- status separate from prose;
- acceptance criteria;
- evidence references;
- blockers;
- dependency state;
- verified vs unverified completion;
- automatically maintained by Agent where appropriate;
- user can still manually create/edit/override tasks.

---

## 7. Notes become Project Knowledge

Notes remain supported, but their primary future role is **project knowledge**, not manual scratchpad maintenance.

Knowledge categories may include:

- project facts;
- requirements;
- decisions;
- meeting notes;
- constraints;
- design choices;
- research findings;
- user instructions;
- assumptions;
- important external references;
- lessons learned.

Example:

User says:

> "From now on this project uses cost norm X."

H2 should be able to preserve a structured project decision:

```text
Project Decision
Date: 2026-09-18
Decision: Use cost norm X for ...
Source: User
Status: Active
```

Future AI work can consume this state without relying on a long chat transcript.

---

## 8. AI Inbox / Human Decision Queue

Add a first-class area for items requiring human attention.

Conceptual example:

```text
AI INBOX

⚠ 3 decisions need attention

1. New legal document found
   Existing reference may require amendment.
   [Review] [Allow update]

2. Excel estimate
   4 formulas differ from project pattern.
   [Review]

3. AutoCAD
   17 OTC blocks have inconsistent attributes.
   [Allow AI to repair]
```

This allows the AI to surface important findings without forcing the user to manually ask for every status update.

### AI Inbox item types

Potential categories:

- decision required;
- permission required;
- blocker;
- risk;
- external change detected;
- verification failure;
- conflicting data;
- missing source;
- stale legal/technical reference;
- task ready for user review.

---

## 9. Board data must be structured state

The dashboard must **not** be generated only from an LLM-written summary.

Fields such as:

```text
progress
status
next action
blocker
risk
pending decision
latest verified result
```

must exist as structured state.

Conceptual model:

```text
ProjectState
{
    ProjectId
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
    LastUpdatedUtc
}
```

The AI may propose/update state, but the board renders the stored structure.

Benefits:

- survives model changes;
- survives app restarts;
- does not depend on reconstructing chat;
- supports deterministic sorting/filtering;
- enables multi-PC synchronization;
- allows verification/audit.

---

## 10. Agent activity must be visible without exposing chain-of-thought

The UI should show meaningful operational progress similar to a coding/work agent:

```text
Analyzing request...
Reading project files...
Reading active Word document...
Checking Excel workbook...
Searching current legal sources...
Found 2 potentially relevant amendments...
Updating report...
Verifying changed sections...
Completed.
```

Do not expose hidden chain-of-thought.

Expose typed activity/state such as:

- phase;
- tool name/category;
- target app/resource;
- progress;
- verifier state;
- repair attempt;
- waiting/blocker;
- completion evidence.

---

## 11. AI should update project state as part of work

A successful Agent task should not end only with a chat answer.

Where appropriate it should update:

- project progress;
- task state;
- next actions;
- blockers;
- project knowledge;
- research evidence;
- decisions;
- files changed;
- verification evidence;
- activity history.

Example:

```text
User:
"Review the estimate and legal references."

Agent performs:
  Word -> Excel -> Web -> verification

Resulting project state:
  Progress +8%
  Legal review = verified
  Excel issue #3 = resolved
  Blocker = none
  Next = submit for approval
  New evidence refs = ...
```

The board then updates automatically.

---

## 12. Files are project resources, not a separate workflow silo

Project Files should support:

- current important files;
- file role/type;
- last verified state;
- source/working/output relationships;
- application session where relevant;
- evidence hashes;
- external path/location;
- NAS identity;
- open-live-app state where available.

The user should be able to ask:

> "Review all current project files."

and let the agent choose Word/Excel/AutoCAD/Web tools as needed.

---

## 13. Research becomes first-class project state

Web/legal/technical research should not live only inside chat history.

Store:

- research query/goal;
- evidence references;
- source publisher;
- dates;
- status/freshness;
- conclusions;
- unresolved uncertainty;
- affected project decisions/files.

This supports later revalidation when sources change.

---

## 14. Decision model

Add a durable decision concept separate from ordinary notes.

Conceptual model:

```text
ProjectDecision
{
    Id
    Title
    Decision
    Status
    Source
    CreatedUtc
    UpdatedUtc
    EffectiveFrom
    EvidenceIds[]
    SupersedesDecisionId?
}
```

The user remains the authority for decisions requiring human judgment.

The agent may:
- propose;
- summarize;
- identify affected areas;
- execute authorized consequences.

---

## 15. Activity / audit history

The project should expose a timeline of important events:

```text
10:42 User asked AI to review legal references
10:43 Agent read active Word document
10:44 Agent found 6 legal references
10:46 Web research completed
10:48 1 amendment identified
10:50 Word updated
10:51 Verification PASS
10:51 Project state updated
```

History should reference structured evidence/tool runs instead of embedding huge raw outputs.

---

## 16. Human control

AI-primary does not mean user control disappears.

The user must always be able to:

- manually edit task/state;
- correct project knowledge;
- reject AI proposals;
- override priorities;
- mark a decision;
- disable automation;
- inspect evidence;
- inspect changes;
- undo/restore where supported;
- select project permissions.

The product should reduce user maintenance, not remove user authority.

---

## 17. Multi-PC / NAS implications

The redesigned project state must remain compatible with multi-PC H2 usage.

Structured board state, tasks, knowledge, decisions and verified results need stable IDs and deterministic merge rules.

Do not make the dashboard depend on:
- local-only window state;
- a single PC's path aliases;
- raw chat ordering alone;
- model-generated summaries without durable state.

Before implementing this redesign, also read:

`docs/H2_NOTES_NON_AI_BUG_LEDGER.md`

especially the NAS/workspace identity items.

---

## 18. Relationship to Agent Lab

Agent Lab is responsible for building and accepting the Agent engine:

- orchestration;
- bounded context;
- tool discovery;
- MCP/plugins/skills;
- Office/Web/AutoCAD/Desktop capability;
- verification;
- repair;
- task state;
- evidence;
- permissions.

H2 Notes should not rebuild those mechanisms.

Phase 13 should adapt the accepted engine into the H2 project model.

Conceptually:

```text
Accepted Agent Lab engine
        |
        v
H2 Agent Adapter
        |
        +-- ProjectState
        +-- ProjectKnowledge
        +-- Tasks
        +-- Decisions
        +-- Files
        +-- Research
        +-- Evidence
        +-- Activity
        |
        v
AI Project Command Center UI
```

---

## 19. Phase-13 mandatory reading

`docs/H2_AGENT_MCP_WEB_OFFICE_REQUIREMENTS.md` is **not deferred Phase-13 backlog**. It is being consumed during Agent Lab implementation now and its accepted requirements must be folded into the final Agent Lab spec/tracker and verified engine before H2 integration.

Therefore, after Agent Lab acceptance, the two additional H2-specific documents that must be read before redesign/integration are:

1. `docs/H2_AI_PROJECT_COMMAND_CENTER_REDESIGN.md`
2. `docs/H2_NOTES_NON_AI_BUG_LEDGER.md`

The final accepted Agent Lab spec/tracker remains the source of truth for Agent capability behavior; it should already include the MCP/Web/Office/plugin/tool requirements implemented during Agent Lab work.

These sources jointly define:
- future H2 product UX direction;
- known non-AI H2 product defects and robustness gaps;
- the already-accepted Agent engine behavior that H2 must integrate without downgrading.

---

## 20. Migration principle

Do not delete current Tasks/Notes data just because they become less visible in the new UX.

Migration should preserve existing data and map it into the new concepts.

Potential mapping:

```text
existing project tasks
    -> ProjectState / Tasks

existing notes
    -> ProjectKnowledge or Notes

existing chat history
    -> Activity/history references where appropriate

existing project files/links
    -> Project Resources

existing manually maintained status
    -> structured ProjectState
```

The redesign is a UX/information-architecture change, not permission to lose historical data.

---

## 21. Initial target screens

A future redesign should at minimum prototype:

### A. Global Project Command Center

Shows:
- all projects;
- attention state;
- progress;
- blocker/risk;
- next action;
- pending decisions;
- last AI activity.

### B. Project AI Workspace

Shows:
- project status summary;
- AI conversation/work area;
- live agent activity;
- quick access to Files / Decisions / Research / History;
- pending approvals.

### C. AI Inbox

Shows:
- decisions;
- blockers;
- risks;
- verification failures;
- permissions;
- external changes.

### D. Project Detail / State Inspector

For advanced/manual users:
- complete tasks;
- notes/knowledge;
- structured state;
- evidence;
- decisions;
- activity;
- raw IDs/diagnostics when needed.

---

## 22. UX acceptance scenarios

### Scenario 1 — quick overview

User opens H2.

Within 5–10 seconds they should be able to answer:

- Which project needs attention?
- What is currently being worked on?
- What is blocked?
- What should happen next?
- Is AI waiting for me?

without opening every task/note.

### Scenario 2 — goal-first work

User opens a project and says:

> "Check the full project package and finish everything you can."

The agent plans and executes.

The user does not manually create all sub-tasks.

### Scenario 3 — AI finds something important

Agent discovers a legal/reference issue while working.

It creates an AI Inbox item and updates project risk/blocker state.

### Scenario 4 — user manually corrects AI state

User changes a priority/decision manually.

The Agent must use the corrected structured state on future work.

### Scenario 5 — restart/multi-PC

Open the same project on another PC.

Board status and project state remain understandable without depending on the previous machine's UI/session.

---

## 23. Core product principle

The redesign is approved around this rule:

> **AI is the primary working interface.  
> The Board is the primary monitoring interface.  
> Tasks/Notes/Files/Research/Decisions are structured supporting state.**

Another concise expression:

```text
Ask AI to work.
Use the board to understand.
Open details only when needed.
```

This is the target future H2 Notes product direction.


---

## 24. H2 Work Assistant — floating desktop assistant

H2 should provide an optional **floating Work Assistant** that gives immediate access to the same Agent engine without requiring the user to open the full Project Command Center first.

This is intended to become the fastest day-to-day interaction path when the user is already working inside Word, Excel, AutoCAD, a browser, or another desktop application.

Core UX principle:

> **Stay in the application where the work is happening. Invoke H2 AI there.**

The floating assistant is not a second AI engine. It is a thin desktop surface over the same accepted:

- AgentOrchestrator;
- ToolRegistry;
- plugin/skill system;
- OfficeHost;
- AutoCADHost;
- WebResearchHost;
- DesktopHost;
- project memory/state;
- verification and repair loop.

---

## 25. Three UI states

The Work Assistant should have three primary presentation states.

### 25.1 Collapsed bubble

A small floating chat-head style bubble:

```text
● H2
```

Requirements:

- optional and user-toggleable;
- draggable;
- remembers position per machine;
- supports multi-monitor placement;
- can snap to screen edges;
- does not occupy normal application workspace unnecessarily;
- should not require the main H2 window to remain visible;
- should not steal focus unless intentionally opened;
- configurable always-on-top behavior;
- visual state can indicate idle / working / waiting / completed / blocked.

Suggested states:

```text
● H2        idle
● H2 ⟳      working
● H2 !      needs user attention
● H2 ✓      completed
```

### 25.2 Quick Assistant panel

Clicking the bubble or invoking the global hotkey opens a compact prompt panel.

Example:

```text
┌─────────────────────────────────────┐
│ H2 Work Assistant                   │
│ Excel · DuToan.xlsx                 │
├─────────────────────────────────────┤
│ Context                             │
│ [Workbook: DuToan.xlsx]             │
│ [Sheet: BAOCAOGS]                   │
│ [Selection: D51:F80]                │
│                                     │
│ > What should I do?                 │
│                                     │
│ [Attach] [Mic]              [Send]  │
└─────────────────────────────────────┘
```

The panel is optimized for a fast instruction, not for full project administration.

### 25.3 Running / result panel

When work is active, the compact panel shows typed operational progress.

Example:

```text
✓ Identified active workbook
✓ Found 3,482 formula cells
● Converting references
○ Verifying formulas
```

If the user clicks outside the panel, it may collapse back to the bubble while the task continues.

On completion:

```text
Completed

3,482 formulas updated
3 worksheets affected
Verification PASS

[View changes] [Undo if supported] [Open full project]
```

---

## 26. Enable / disable and startup behavior

The floating assistant must be configurable.

Settings should include at least:

- Enable H2 Work Assistant;
- Show on Windows startup;
- Start collapsed;
- Always on top;
- remember bubble position;
- preferred monitor;
- enable global hotkey;
- global hotkey binding;
- show completion notifications;
- automatically associate recognized files with known projects;
- default permission mode for quick sessions.

Disabling the floating assistant must not disable the Agent engine or Project Command Center.

The bubble/window placement is machine-local state and must not sync through the shared NAS project workspace.

---

## 27. Global hotkey

The user should be able to invoke the assistant without reaching for the mouse.

Example default candidate:

```text
Ctrl + Shift + Space
```

Exact shortcut remains configurable.

Behavior:

```text
foreground application
    -> hotkey
    -> capture current ActiveWorkContext
    -> show Quick Assistant
    -> focus prompt input
```

The global hotkey must not silently trigger a mutation.

It only opens the assistant and captures context.

---

## 28. ActiveWorkContext

When the assistant is invoked, H2 should build a structured snapshot describing the current working application.

Conceptual model:

```text
ActiveWorkContext
{
    CapturedUtc
    ProcessId
    ProcessIdentity
    ApplicationKind
    WindowId
    WindowTitle
    WindowStateId
    DocumentSessionId?
    DocumentPath?
    DocumentDisplayName?
    Selection?
    ProjectId?
    AdapterKind
}
```

This context must be treated as a starting observation, not permanent truth.

Before mutation, the selected structured adapter must revalidate process/document/session identity.

---

## 29. Context examples

### Excel

```text
Application: Excel
Workbook: DuToan.xlsx
Worksheet: BAOCAOGS
Selection: D51:F80
WorkbookSessionId: ...
UnsavedState: true
```

### Word

```text
Application: Word
Document: HoSoKhaoSat.docx
Selection: paragraph/range ...
DocumentSessionId: ...
UnsavedState: true
```

### AutoCAD

```text
Application: AutoCAD 2024
Drawing: BanVe01.dwg
Space: ModelSpace
Selection: 14 entities
DocumentSessionId: ...
```

### Browser / other application

Capture only the bounded metadata available from the approved adapter/DesktopHost.

Sensitive/system/password-manager windows must remain excluded by policy.

---

## 30. Context chips

The Quick Assistant must visibly show what H2 currently thinks the scope is.

Examples:

```text
[Excel] [DuToan.xlsx] [BAOCAOGS] [D51:F80]
```

or:

```text
[AutoCAD] [BanVe01.dwg] [14 selected entities]
```

This reduces wrong-target operations.

If the user says:

> "Change the whole workbook"

the assistant may expand scope to:

```text
[Workbook: DuToan.xlsx]
[Scope: all worksheets]
```

The scope change must be explicit in the task contract.

---

## 31. Quick Work Sessions

A Work Assistant task does **not** have to belong to an H2 project.

For ad-hoc work, create a temporary structured session:

```text
QuickWorkSession
{
    SessionId
    Goal
    ActiveWorkContext
    TaskContract
    EvidenceRefs
    ToolRuns
    Verification
    CreatedUtc
    CompletedUtc?
    LinkedProjectId?
}
```

Example:

> "Convert all formulas in the Excel workbook currently open to absolute references."

This should work even if the workbook has never been associated with an H2 project.

Afterward H2 may offer:

```text
This workbook appears related to project "RPBMVN Thach Bich".

[Link activity to project] [Keep as quick session]
```

Do not automatically attach uncertain work to a project.

---

## 32. Project-aware quick work

When H2 has strong evidence that the current document/resource belongs to a known project, the assistant may surface that relationship.

Example:

```text
Project: RPBMVN Thach Bich
File: DuToan.xlsx
```

If linked, completion can update:

- project activity;
- verified results;
- next actions;
- blockers;
- project knowledge;
- evidence;
- affected files.

This preserves the principle:

> Quick Assistant is the fast execution surface; Project Command Center remains the durable management surface.

---

## 33. Permission model for the floating assistant

Fast access must not mean unlimited authority.

The assistant should support scoped permission modes such as:

```text
Observe only
Ask before changes
Allow changes in current document
Allow changes in current application session
Use project permission policy
```

Examples:

```text
FULL CONTROL: DuToan.xlsx
```

must **not** mean:

```text
FULL CONTROL: entire PC
```

Permission scope should be bound to:

- app/process identity;
- document/session identity;
- project/resource scope;
- mutation class;
- lifetime.

A permission can expire when:

- document closes;
- application exits;
- task completes;
- user revokes permission;
- state identity becomes stale.

---

## 34. Structured adapter priority

The floating assistant must use the same structured-adapter priority as Agent Lab.

```text
OfficeHost / AutoCADHost / specialized plugin
    > file/API adapter
    > UI Automation
    > screenshot / mouse / keyboard
```

Examples:

### Excel

For:

> "Convert every formula in the currently open workbook to absolute references."

Prefer:

```text
ExcelHost
 -> enumerate formulas
 -> transform references
 -> write structured patches
 -> recalculate if required
 -> re-read formulas
 -> verify
```

Do not click thousands of cells.

### Word

For proofreading/legal updates:

```text
WordHost
 -> read live unsaved document
 -> spelling/citation extraction
 -> WebResearchHost
 -> structured Word patch
 -> re-read
 -> verify
```

### AutoCAD

For block audit:

```text
AutoCADHost/native plugin
 -> query selected/current drawing entities
 -> structured edits
 -> query again
 -> verify
```

DesktopHost is fallback when higher-level capabilities are insufficient.

---

## 35. Example acceptance scenario — Excel formula conversion

User is actively editing Excel and invokes the Work Assistant.

Request:

> "Change all formulas in the Excel file I currently have open to absolute references."

Required behavior:

1. Detect active Excel process/session.
2. Identify the exact active workbook.
3. Capture whether workbook has unsaved edits.
4. Show context chips.
5. Build task scope = active workbook / all worksheets.
6. Use ExcelHost, not pixel clicking.
7. Read formulas in bounded batches.
8. Convert only formula references that require the requested transformation.
9. Preserve constants, formatting, named ranges and unrelated workbook state.
10. Re-read changed formulas.
11. Verify all targeted formulas satisfy the requested absolute-reference rule.
12. Surface failures rather than claiming blanket success.
13. Keep workbook open.
14. Respect save/overwrite policy.
15. Show concise completion result.
16. If supported by Excel/host policy, provide undo/change-review path.

Target computer-use calls:

```text
0
```

unless a structured Excel capability is genuinely unavailable.

---

## 36. Example acceptance scenario — Word proofreading + legal research

User is working in an open Word document.

Request:

> "Check spelling and verify whether the legal documents referenced here have newer replacement/amendment/supplement documents, then update it."

Required behavior:

- capture active Word document/session;
- preserve unsaved state;
- use WordHost;
- find spelling candidates;
- extract legal citations;
- use current WebResearchHost;
- verify authoritative sources;
- distinguish replaced / amended / supplemented / repealed / still effective;
- patch only intended Word ranges;
- preserve unrelated formatting;
- re-read Word;
- verify;
- update project state only when linked/appropriate;
- report unresolved uncertainty.

Target pixel computer-use calls:

```text
0
```

when OfficeHost + WebResearchHost are sufficient.

---

## 37. Example acceptance scenario — AutoCAD block audit

User is actively working in AutoCAD.

Request:

> "Check the selected OTC blocks and fix any incorrect attributes."

Required behavior:

- identify active AutoCAD document;
- capture current selection when available;
- use AutoCAD structured host/plugin;
- query stable entity handles/IDs;
- inspect attributes/dynamic properties;
- apply bounded changes;
- re-query changed entities;
- verify;
- preserve unrelated drawing entities;
- keep the drawing session open.

Computer Use is fallback only.

---

## 38. Background execution

Closing/collapsing the Quick Assistant panel must not cancel an active task.

The task continues in AgentOrchestrator.

The bubble reflects current status.

Example:

```text
panel open
 -> send task
 -> agent starts
 -> user clicks outside
 -> panel collapses
 -> task continues
 -> bubble shows ⟳
 -> completion
 -> bubble shows ✓
```

The user must still have a clear Cancel/Stop action.

App shutdown must use existing task cancellation/safe-state rules.

---

## 39. Progress display

The assistant should show **typed operational progress**, not hidden chain-of-thought.

Allowed examples:

- Reading workbook...
- Found 3,482 formula cells...
- Updating 1,200 / 3,482...
- Running verification...
- 2 criteria failed; repairing...
- Verification PASS.

Do not display private reasoning text.

Progress events should come from the Agent trace/event model.

---

## 40. Completion and attention states

A completed task should expose concise structured result data.

Examples:

```text
Completed
3,482 formulas changed
3 worksheets affected
Verification PASS
```

or:

```text
Needs attention
2 legal references remain unresolved
No document changes made for those items
```

Possible actions:

- View changes;
- View evidence;
- Open full H2 project;
- Link session to project;
- Retry failed criteria;
- Undo when supported;
- Dismiss.

---

## 41. Multi-monitor behavior

The floating assistant must be designed for users working across multiple monitors.

Requirements:

- remember last monitor and position locally;
- keep bubble inside visible work area after monitor topology changes;
- support DPI changes;
- when invoked by hotkey, optionally appear near the foreground application rather than on an unrelated monitor;
- do not sync window placement across NAS/multi-PC;
- restore safely when a previously used monitor is disconnected.

---

## 42. Tray / taskbar behavior

Suggested behavior:

- bubble does not require a permanent taskbar button;
- main H2 Project Command Center remains a normal app window;
- tray menu can show/hide Work Assistant;
- settings can disable the bubble completely;
- exiting H2 stops/handles active work according to safe task-state policy.

Exact Windows shell integration may evolve during implementation.

---

## 43. Stale-state safety

Foreground context can change after the user sends a request.

Therefore:

```text
capture context
 -> plan
 -> before mutation revalidate target
```

If:

- workbook changed;
- active document changed;
- AutoCAD drawing changed;
- process restarted;
- selection token expired;
- file was replaced;
- app/session identity changed;

the mutation must fail closed or require re-grounding.

Never mutate a new foreground document merely because it happens to occupy the same screen position.

---

## 44. Quick Assistant and plugin/skill discovery

The floating assistant uses the same dynamic ToolRegistry and plugin/skill catalog as the full Agent engine.

Example:

```text
User asks specialized AutoCAD task
 -> installed capabilities insufficient
 -> Agent searches approved plugin/skill catalog
 -> host applies install policy
 -> capability becomes available
 -> original quick task resumes
```

The Quick Assistant must not maintain a separate plugin ecosystem.

---

## 45. Quick Assistant and Web

The assistant can invoke WebResearchHost even when the foreground application is not a browser.

Example:

```text
Word foreground
User asks about current legal status
 -> WordHost
 -> WebResearchHost
 -> WordHost
 -> verification
```

The current foreground application provides task context, not an exclusive capability boundary.

---

## 46. Accessibility and keyboard-first use

The Quick Assistant must be usable without requiring pointer interaction.

At minimum:

- global hotkey;
- keyboard focus in prompt;
- Escape/collapse behavior;
- keyboard-accessible cancel;
- keyboard-accessible context chips and permission prompt;
- screen-reader labels for status and progress;
- no status encoded only by color/icon.

---

## 47. Local-state vs shared-state boundary

The following should remain local-machine state:

- bubble enabled/disabled;
- bubble coordinates;
- monitor preference;
- global hotkey;
- panel size;
- temporary UI state.

The following may become durable project/activity state when the Quick Work Session is linked to a project:

- task contract;
- important evidence references;
- verified result;
- changes made;
- project state updates;
- project activity entry.

Do not sync ephemeral desktop positioning through the project NAS workspace.

---

## 48. Relationship to Project Command Center

The two primary H2 surfaces serve different purposes:

```text
H2 Work Assistant
  = fast, context-aware execution

H2 Project Command Center
  = durable project understanding and management
```

They must share the same underlying task/project/evidence data where appropriate.

A task started from the floating assistant may later be inspected in the full H2 UI.

A task started from the full H2 UI may expose minimal status in the floating assistant if relevant.

---

## 49. Future implementation timing

This feature is a **future H2 Notes redesign requirement**.

Do not interrupt current Agent Lab acceptance work to build the bubble prematurely.

Implementation belongs after the accepted Agent engine is ready to integrate into H2.

At that time:

1. implement the H2 Agent Adapter;
2. implement Project Command Center redesign;
3. implement Work Assistant as a thin surface over the same Agent engine;
4. run structured Word/Excel/AutoCAD/Web acceptance scenarios;
5. run the H2 non-AI bug ledger regression/acceptance requirements.

---

## 50. Work Assistant product rule

The approved product rule is:

> **Do not make the user open H2 just to ask H2 to help with the application already in front of them.**

The intended workflow is:

```text
work in Word / Excel / AutoCAD / browser / another app
        |
        v
invoke H2 Work Assistant
        |
        v
tell AI the desired outcome
        |
        v
Agent uses structured tools / plugins / web / computer control as needed
        |
        v
verify
        |
        v
user continues working in the same app
```

This complements, rather than replaces, the H2 Project Command Center.

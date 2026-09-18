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

Before redesigning H2 Notes after Agent Lab acceptance, the implementer/reviewer must read:

1. `docs/H2_AI_PROJECT_COMMAND_CENTER_REDESIGN.md`
2. `docs/H2_AGENT_MCP_WEB_OFFICE_REQUIREMENTS.md`
3. `docs/H2_NOTES_NON_AI_BUG_LEDGER.md`
4. final Agent Lab spec/tracker

These documents jointly define:
- product UX direction;
- Agent capability requirements;
- known non-AI product defects;
- accepted Agent engine behavior.

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

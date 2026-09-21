# H2 Agent Chat Surface — Canonical Interaction & Rendering Specification

Status: **CANONICAL / NORMATIVE FOR ALL NEW H2 AGENT CHAT SURFACES**  
Date: 2026-09-21  
Scope: H2 Project Agent + Global Work Assistant full chat surface  
Architecture sources:
- `docs/H2_AGENT_MASTER_SPEC.md`
- `docs/H2_PRODUCT_MASTER_SPEC.md`
- `docs/H2_PRODUCT_MASTER_TASKS.md`

> This specification defines how H2 Agent chat behaves, resolves context, renders content, exposes tools/progress/approvals/artifacts, and persists task threads.
>
> It is intentionally **Codex-style in interaction grammar and information layout**, while remaining H2-specific in domain, data model, providers and safety.
>
> It does **not** depend on undocumented/private Codex internals and does not require pixel-for-pixel copying. The requirement is behavioral and structural consistency: the user should experience a professional agent thread in which work, progress, tools, artifacts, approvals and final results live in one coherent conversation.

---

# 1. Product rule

H2 keeps the **Project Management / Command Center** as the primary application surface.

The Agent has two entry modes:

```text
A. GLOBAL ASSISTANT
   entry: floating H2 bubble / global hotkey
   implicit grounding: whole current computer context

B. PROJECT AGENT
   entry: Agent chat inside one H2 project
   implicit grounding: current project first
```

Both modes must use:

```text
same AgentRuntime
same model/provider layer
same ToolRegistry
same Skills
same Plugins
same permission engine
same verification engine
same evidence/artifact system
same chat renderer
same task/thread model
```

Do not create a second Agent for Work Assistant.

Do not create a second Agent for projects.

---

# 2. Core distinction: capability vs implicit target scope

The two Agent modes do **not** differ by what the Agent is technically capable of doing.

Both may use the full approved computer capability surface:

- files;
- Word;
- Excel;
- AutoCAD;
- browser/Web;
- shell/process where allowed;
- Desktop/UIA;
- MCP;
- plugins;
- skills;
- future providers.

The difference is only how the Agent resolves **implicit targets**.

```text
GLOBAL ASSISTANT
Whole-computer implicit grounding

PROJECT AGENT
Project-anchored implicit grounding
+ explicit user override outside project
```

This distinction is mandatory.

---

# 3. Invocation modes

## 3.1 Global Assistant Mode

Concept:

```text
Mode = GlobalAssistant
ProjectId = null
DefaultTargetPolicy = MachineFirst
```

Typical user request:

> "Đọc file Excel đang mở và xem công thức này sai ở đâu."

Expected behavior:

1. inspect current foreground application;
2. identify current active Excel workbook/session;
3. use current selection if relevant;
4. act on that workbook subject to permission/safety;
5. no H2 project is required.

The Agent may use the currently active document even if it is outside every H2 project.

---

## 3.2 Project Agent Mode

Concept:

```text
Mode = Project
ProjectId = current project
ProjectRoot = project workspace root
DefaultTargetPolicy = ProjectAnchored
```

Typical request:

> "Đọc file Excel đang mở."

Expected behavior:

1. resolve active/open Excel workbooks;
2. keep only workbooks inside the project scope or explicitly linked project resources;
3. if one project workbook is the foreground project workbook, use it;
4. ignore a foreground workbook outside the project;
5. if no suitable project workbook is open, do not silently use an external workbook.

Project mode still has whole-computer capabilities, but project scope controls **implicit resolution**.

---

# 4. Explicit external target override

Project scope is **not** a hard filesystem prison.

If the user explicitly names a target outside the project, the Agent may use that exact target subject to host safety.

Example:

> "Đọc file D:\DinhMuc\DM1776.xlsx để đối chiếu."

Resulting task scope:

```text
Project scope:
X:\DuAn\ThachBich\

Explicit external target:
D:\DinhMuc\DM1776.xlsx
```

The explicit target does not automatically grant:

```text
D:\DinhMuc\*
D:\*
whole computer write access
```

The expansion is exactly the scope the user specified.

---

# 5. Five mandatory scope laws

1. There is only **one Agent runtime**.
2. Global Assistant resolves unspecified targets from the current computer context.
3. Project Agent resolves unspecified targets from project root + approved/linked project resources.
4. An explicit user target may extend a project task outside the project exactly to the named scope.
5. Explicit scope never bypasses permission, security, stale-state checks or verification.

---

# 6. Target resolution policy

## 6.1 Global Assistant resolution order

When the user does not name a precise path/resource:

```text
1. explicit attached resource/path
2. foreground active document/session
3. current selection
4. open document in active application
5. attached context/resource chips
6. ask user if still ambiguous
```

Example:

> "Sửa công thức này."

Foreground:

```text
Excel
DuToan.xlsx
BAOCAOGS
D51
```

Target resolves to D51 in the active workbook.

---

## 6.2 Project Agent resolution order

When the user does not name a precise target:

```text
1. explicit user target/path
2. active foreground resource IF it belongs to ProjectScope
3. currently open project resources
4. explicitly linked project resources
5. relevant files inside ProjectRoot
6. ask user if ambiguous
```

A foreground resource outside project scope is **not** an implicit candidate.

---

# 7. ProjectScope

Conceptual model:

```text
ProjectScope
{
    ProjectId
    ProjectRoot
    LinkedResources[]
}
```

Example:

```text
ProjectRoot:
X:\DuAn\ThachBich\

LinkedResources:
D:\TaiLieuChung\DinhMuc.xlsx
C:\Reference\MauBaoCao.docx
```

Linked resources are explicit, narrow additions.

Do not treat a linked file as permission to scan the entire parent drive/folder unless the link itself is a folder resource.

---

# 8. Active work context

Conceptual local ephemeral context:

```text
ActiveWorkContext
{
    ProcessId
    ApplicationKind
    WindowIdentity
    DocumentSessionId?
    DocumentPath?
    Sheet/Page/Drawing?
    Selection?
    CapturedUtc
}
```

Rules:

- local machine/session only;
- not shared through NAS;
- never authoritative after stale-state invalidation;
- revalidate before mutation;
- used as grounding, not as permission.

---

# 9. Chat UI product principle

Once the user enters Agent chat, the interaction must behave like a modern Codex-style agent thread.

The thread is not:

```text
user text
assistant text
user text
assistant text
```

The thread is a structured execution surface:

```text
User request
Agent prose
Progress
Tool activity
Artifacts
Approvals
Changes
Evidence
Verification
Repair
Final answer
```

All of these belong to one continuous task thread.

---

# 10. Canonical Agent chat layout

Desktop/wide target:

```text
┌──────────────────┬───────────────────────────────────┬──────────────────────┐
│ Project / Chats  │ Agent Thread                      │ Artifact / Inspector │
│                  │                                   │                      │
│ Project A        │ User                              │ Excel / Word / PDF   │
│   Chat 1         │ ...                               │ Evidence / Changes   │
│   Chat 2         │                                   │                      │
│                  │ Agent                             │                      │
│ Project B        │ progress / tools / markdown       │                      │
│                  │                                   │                      │
│                  │                                   │                      │
│                  │ ───────────────────────────────   │                      │
│                  │ Composer                          │                      │
└──────────────────┴───────────────────────────────────┴──────────────────────┘
```

H2's main application may continue to open on Command Center. This layout applies once the Agent thread is open.

---

# 11. Narrow layout

At narrow widths:

```text
Agent Thread
────────────
header
context chips
messages
composer
```

The project list and artifact inspector become drawers/pages.

Do not compress all three columns until text becomes unreadable.

---

# 12. One shared AgentChatSurface

Implement one logical shared component:

```text
AgentChatSurface
├─ ChatHeader
├─ ContextBar
├─ MessageTimeline
├─ Composer
└─ ArtifactPane
```

Reuse it for:

- Project Agent;
- Global Assistant full thread;
- detached Agent window.

The compact Work Assistant window may use a reduced composer/progress view, but opening task details must navigate into the canonical AgentChatSurface.

---

# 13. Header behavior

## 13.1 Global Assistant header

Example:

```text
H2 Assistant
Scope: This computer
[Excel] [DuToan.xlsx] [BAOCAOGS] [D51:F80]
```

The user must clearly understand that implicit context may come from the whole current computer session.

---

## 13.2 Project Agent header

Example:

```text
H2 Agent · RPBMVN Thạch Bích
Scope: X:\DuAn\ThachBich
[Project] [Excel: DuToan.xlsx]
```

If an explicit outside resource is being used:

```text
[External: D:\DinhMuc\DM1776.xlsx]
```

The external target chip must be visually distinguishable from normal project scope.

---

# 14. Thread item model

Do not represent all thread content as one string type.

Conceptual union:

```text
AgentThreadItem
├─ UserMessage
├─ AgentMarkdownMessage
├─ ProgressBlock
├─ ToolCallBlock
├─ ApprovalBlock
├─ ArtifactBlock
├─ EvidenceBlock
├─ ChangeBlock
├─ VerificationBlock
├─ ErrorBlock
├─ DecisionBlock
└─ FinalResultBlock
```

The underlying implementation may use records/enums/discriminated types, but the behavioral distinction must exist.

---

# 15. User messages

User messages may use a compact bubble/card.

They may contain:

- text;
- files;
- images;
- linked resources;
- selected context;
- explicit scope overrides.

Do not repeat the complete context snapshot inside the visible message body.

Use context chips/attachments.

---

# 16. Agent prose style

Agent prose should render as document-like content, not one giant chat bubble.

Preferred visual grammar:

```text
H2 Agent

## Result

I found **4 formula issues**...

[table]

### Next
...
```

Agent prose should have generous readable width, line spacing and selectable text.

---

# 17. Canonical Markdown support

Agent text must support a safe GitHub-Flavored-Markdown-like subset.

Required:

```text
# headings
## headings

**bold**
*italic*
~~strikethrough~~

`inline code`

- unordered list
1. ordered list

- [x] completed item
- [ ] incomplete item

> blockquote

[link](https://...)

```language
code block
```

| table | columns |
|---|---|

---
```

Also support normal paragraphs, hard/soft line breaks and escaped punctuation.

---

# 18. Markdown safety

Do not execute arbitrary HTML/JavaScript from model output.

Rules:

- raw HTML disabled by default;
- if any HTML subset is later permitted, sanitize strictly;
- links must be explicit and safe;
- file links resolve through H2 resource handling, not arbitrary command execution;
- Markdown is presentation, never authority or permission.

---

# 19. Markdown parsing architecture

Do not implement Markdown using regex replacements.

Use a proper Markdown parser that produces an intermediate syntax tree.

Conceptually:

```text
Markdown source
    ↓
Markdown AST
    ↓
H2RichMessageRenderer
    ↓
Avalonia controls
```

Renderer nodes:

```text
Paragraph
Heading
List
TaskList
Table
CodeBlock
Blockquote
Link
InlineCode
HorizontalRule
```

---

# 20. Store source, not rendered UI

Persist Markdown source and typed thread events.

Do not serialize Avalonia controls.

Example:

```text
AgentMarkdownMessage
{
    Id
    TaskId
    MarkdownSource
    CreatedUtc
}
```

On reopen:

```text
source
→ parse
→ render
```

---

# 21. Tables

Markdown tables must render as real tables.

Required behavior:

- visible column boundaries;
- alignment where declared;
- selectable/copyable text;
- copy row/cell/table;
- horizontal scrolling if too wide;
- bounded height with vertical scrolling for large tables;
- no layout explosion;
- header visually distinct.

Example source:

```markdown
| Sheet | Issues | Status |
|---|---:|---|
| BAOCAOGS | 4 | Fixed |
| KL | 2 | Review |
```

Must not remain raw pipe text.

---

# 22. Large tables

If a table exceeds reasonable chat size:

```text
render compact preview
+
Open in Inspector
```

Example:

```text
Table · 1,245 rows
Showing first 30
[Open table]
```

Do not render thousands of rows inline.

---

# 23. Code blocks

Code blocks must provide:

- language label;
- syntax highlighting where supported;
- copy button;
- horizontal scroll or explicit wrap setting;
- text selection;
- optional line numbers for larger blocks.

Example:

```text
C#                                      Copy
────────────────────────────────────────────
var total = quantity * price;
```

---

# 24. Diff blocks

Diffs are a first-class visual type.

Render:

```diff
- =B5*C7
+ =$B$5*$C$7
```

as a change view, not generic plain code.

For file/document changes, support:

- before;
- after;
- changed lines/cells/paragraphs;
- target identity;
- verification state.

---

# 25. Streaming Agent text

During streaming:

- append into one AgentMarkdownMessage;
- avoid creating one control per token;
- update at a bounded UI cadence;
- preserve scroll stability;
- reparse incrementally or in bounded chunks;
- do not freeze the UI on long Markdown.

Incomplete Markdown constructs may render conservatively until closed.

---

# 26. Progress is typed, not Markdown

Runtime progress must not be model-formatted prose pretending to be system state.

Conceptual:

```text
AgentProgressEvent
{
    TaskId
    Stage
    Status
    Label
    Sequence
    CreatedUtc
}
```

Common stages:

```text
Planning
DiscoveringCapabilities
Reading
Searching
Editing
Running
WaitingForApproval
Verifying
Repairing
Completed
Blocked
```

---

# 27. Progress block behavior

Example:

```text
✓ Located workbook
✓ Read 12 sheets
● Checking formulas
○ Verification pending
```

The UI should update the same logical progress block.

Do not append 50 separate progress chat messages.

---

# 28. No hidden chain-of-thought

The UI may show:

- public reasoning summary when provider explicitly supplies one;
- stage/status;
- tool activity;
- evidence;
- decisions needed.

The UI must not request, expose or persist hidden private chain-of-thought.

A useful user-facing progress summary is enough.

---

# 29. Tool call cards

Tool calls are first-class cards.

Collapsed example:

```text
┌────────────────────────────────────────────┐
│ Excel · Read range                    ✓   │
│ DuToan.xlsx · BAOCAOGS!D51:R88            │
│ 38 rows read                               │
│ [Details]                                  │
└────────────────────────────────────────────┘
```

Default display must show human meaning, not JSON.

---

# 30. Tool card details

Expanded tool card may show:

- tool name;
- provider/extension;
- target;
- bounded arguments;
- duration;
- status;
- bounded output summary;
- evidence/artifact references;
- retry/recovery status.

Raw structured JSON may be available under a developer/details view, not the normal user surface.

---

# 31. Tool result size rule

Never dump unbounded tool output into chat.

Large result flow:

```text
tool raw result
    ↓
Artifact/Evidence store
    ↓
bounded model summary
    ↓
tool card + artifact link
```

The chat shows:

```text
Read 30,000 cells
[Open spreadsheet snapshot]
```

not 30,000-cell JSON.

---

# 32. Approval cards

Permissions/approvals should normally be inline in the thread.

Example:

```text
┌─────────────────────────────────────────┐
│ Permission required                     │
│                                         │
│ Modify 4 cells in:                      │
│ DuToan.xlsx · BAOCAOGS                  │
│                                         │
│ Scope: current workbook session         │
│                                         │
│ [Allow once]        [Deny]              │
└─────────────────────────────────────────┘
```

After choice:

```text
✓ Allowed once
```

or:

```text
✕ Denied
```

Agent continues in the same task where appropriate.

---

# 33. Approval safety

Approval card must display:

- operation class;
- actual target;
- scope;
- whether destructive;
- lifetime;
- whether external to project;
- consequences where material.

Never hide the real target behind vague text like “Agent needs permission”.

---

# 34. Waiting-for-user cards

When the Agent genuinely needs a user business decision:

```text
┌─────────────────────────────────────────┐
│ Need your decision                      │
│                                         │
│ Two estimate files appear current:      │
│ • DuToan-v3.xlsx                        │
│ • DuToan-final.xlsx                     │
│                                         │
│ Which one is authoritative?             │
│ [v3] [final]                            │
└─────────────────────────────────────────┘
```

The task enters WaitingForUser/WaitingForApproval, not Completed.

---

# 35. Error cards

Recoverable error example:

```text
⚠ Workbook state changed.
Agent is reacquiring the current workbook state.
```

Agent should recover automatically where safe.

Do not require the user to resend the task for normal recoverable tool failures.

---

# 36. Blocked cards

Terminal/non-recoverable blocker example:

```text
Blocked
No project Excel workbook is currently open.
The only active workbook is outside this project:
D:\Personal\Budget.xlsx
```

Available choices may be shown:

```text
[Open project workbook]
[Use external workbook explicitly]
```

No silent scope expansion.

---

# 37. Verification blocks

Verification is visibly distinct from tool execution.

Example:

```text
Verification
✓ 4/4 formulas match expected pattern
✓ Workbook recalculated
✓ No unrelated sheets changed
```

Failure:

```text
Verification failed
✕ BAOCAOGS!R4 changed unexpectedly
● Agent repairing
```

---

# 38. Repair behavior

A repair should update the same task thread:

```text
Verification failed
↓
Repairing criterion C3
↓
Tool calls
↓
Verification PASS
```

Do not create a separate new chat/session for repair.

---

# 39. Final result block

A completed task ends with a clear final result block/message.

Recommended structure:

```markdown
## Completed

Short result summary.

### Changes

| Item | Result |
|---|---|
| Formula issues | 4 fixed |
| Unrelated cells changed | 0 |

### Verification

- ✓ Workbook recalculated
- ✓ 4/4 formulas verified
- ✓ Scope preserved

### Remaining

No unresolved issues in the checked scope.
```

Buttons/links:

```text
[Open workbook]
[View evidence]
[Open changes]
```

---

# 40. Final result rules

Do not claim:

- fixed;
- verified;
- complete;

unless host state supports that wording.

If verification is unavailable:

```text
Completed action, verification unavailable
```

must be distinct from:

```text
Verified
```

---

# 41. Artifact model

Artifacts are first-class.

Examples:

- Excel workbook/range snapshot;
- CSV;
- Word document;
- PDF;
- Markdown;
- text;
- code;
- image;
- AutoCAD entity/report snapshot;
- Web evidence set;
- generated file;
- diff.

Chat contains compact cards.

Inspector displays full artifact content.

---

# 42. Artifact card

Example:

```text
┌────────────────────────────────────┐
│ 📊 DuToan.xlsx                     │
│ BAOCAOGS · D1:R300                 │
│ 7 formula issues                   │
│                           [Open]   │
└────────────────────────────────────┘
```

Artifact cards must show identity and provenance, not merely “file created”.

---

# 43. Artifact Inspector pane

Desktop wide mode includes an optional right pane.

Tabs/views may include:

```text
Preview
Changes
Evidence
Metadata
```

The pane opens when the user clicks a file/artifact/evidence/tool result.

It is not permanently required.

---

# 44. Spreadsheet viewer

Excel/CSV/spreadsheet artifacts require a real grid viewer.

Required minimum:

- sheet tabs when workbook has sheets;
- visible rows/columns;
- cell selection;
- copy;
- value view;
- formula view;
- formula bar;
- frozen header labels;
- horizontal + vertical virtualization;
- changed-cell highlighting;
- before/after comparison when available;
- verification markers;
- bounded loading.

---

# 45. Spreadsheet viewer performance

Do not materialize every workbook cell as an Avalonia control.

Use virtualization.

Large range rule:

```text
viewport
+ paged/virtual backing
+ bounded cell formatting
```

The viewer must remain responsive on large sheets.

---

# 46. Spreadsheet formula mode

For formula-related work, show both:

```text
Displayed value
Formula
```

Example:

```text
D51
Value: 1,250,000
Formula: =$B$5*$C$7
```

Changed formula comparison:

```text
Before: =B5*C7
After:  =$B$5*$C$7
```

---

# 47. CSV viewer

CSV/TSV opens in the same grid family.

Do not render raw CSV text by default.

Offer:

```text
Table
Raw
```

only when Raw is useful.

---

# 48. Word/document viewer

Minimum:

- rendered text/paragraph structure;
- search;
- headings where available;
- change locations;
- before/after text;
- evidence/citations;
- open in native Word.

H2 need not become a full Word clone.

For layout-critical review, use actual Word/native rendering evidence where available.

---

# 49. PDF viewer

Required:

- page navigation;
- zoom;
- text search where extracted;
- page evidence links;
- source-page references;
- image page rendering;
- current page indicator.

Do not represent a PDF only as extracted Markdown when page/layout matters.

---

# 50. Markdown artifact viewer

Offer:

```text
Rendered
Source
```

Rendered is default.

---

# 51. Image viewer

Support:

- fit;
- 100%;
- zoom;
- pan;
- metadata/provenance;
- optional comparison.

---

# 52. AutoCAD result inspector

H2 is not required to embed a full DWG renderer initially.

Minimum inspector may show:

- document name/path;
- entity IDs;
- block names;
- attributes;
- layers;
- parameters/actions;
- before/after structured changes;
- state/verification tokens;
- open/focus in AutoCAD.

Future rendering may be added as an extension.

---

# 53. Web evidence inspector

Show:

- page/source title;
- publisher/domain;
- observed/published dates where known;
- evidence summary;
- quoted/extracted relevant fragment;
- relationship to task;
- freshness;
- official/secondary source classification where available;
- open source action.

Do not duplicate full WebEvidence into ProjectRecord.

---

# 54. Composer

Canonical composer:

```text
┌────────────────────────────────────────────────────┐
│ Ask H2 Agent...                                    │
│                                                    │
│ [+] [@] [Context]     Model · Effort   [Shield] ➤ │
└────────────────────────────────────────────────────┘
```

One coherent composer, not multiple scattered toolbars.

---

# 55. Composer +

Menu may include:

- attach file;
- attach folder;
- image;
- current document;
- current selection;
- project resource;
- external resource;
- paste clipboard content.

Actions must not send automatically.

---

# 56. Composer @

May reference:

- project;
- file;
- folder/resource;
- skill;
- known artifact;
- task/evidence item.

Do not turn @ into an unsafe arbitrary command launcher.

---

# 57. Model selector

If user-selectable:

- show provider/model in one compact control;
- show reasoning/effort only when supported;
- never fabricate unsupported levels;
- current selection belongs to local/user Agent settings, not ProjectRecord.

---

# 58. Permission selector

Visible presets:

```text
Observe only
Ask before changes
Allow scoped changes
Use project policy
```

The UI preset maps to Agent permission scopes.

The selector itself does not grant power.

---

# 59. Context bar

The context bar shows actual grounding.

Global example:

```text
[Excel] [DuToan.xlsx] [BAOCAOGS] [D51:F80]
```

Project example:

```text
[Project: Thạch Bích]
[Root: X:\DuAn\ThachBich]
[Excel: DuToan.xlsx]
```

Explicit outside resource:

```text
[External: D:\DinhMuc\DM.xlsx]
```

User can remove optional context where safe.

---

# 60. Context chips are not permission chips

Removing a context chip changes grounding.

It does not automatically revoke a separately granted permission scope unless the permission policy links them.

Likewise, a context chip does not grant mutation authority.

---

# 61. Project Agent no-silent-escape rule

Mandatory:

> Project Agent must never silently widen implicit target scope outside the project because no suitable project resource was found.

Example:

User:

> "Đọc Excel đang mở."

Only open Excel:

```text
D:\Personal\Budget.xlsx
```

Project Agent response must not use it automatically.

Correct behavior:

```text
No open Excel workbook belongs to this project.
```

Then ask or offer explicit choices.

---

# 62. Global Assistant active-first rule

Global Assistant may use the active workbook/document directly when the request is naturally deictic:

> "Sửa công thức này."

subject to stale-state and permission checks.

---

# 63. Project Agent project-first rule

If multiple documents are open:

```text
D:\Personal\Budget.xlsx
X:\DuAn\ThachBich\DuToan.xlsx
```

Project chat:

> "Kiểm tra workbook đang mở."

Target:

```text
X:\DuAn\ThachBich\DuToan.xlsx
```

if that is the valid project candidate.

---

# 64. Ambiguity rule

If multiple equally plausible project resources remain, Agent asks.

Do not choose by arbitrary filename order.

Example:

```text
DuToan-v3.xlsx
DuToan-final.xlsx
```

Ask which is authoritative unless project metadata/evidence resolves it.

---

# 65. Thread persistence

A task thread must survive restart.

Persist enough to reconstruct:

- user messages;
- Agent Markdown;
- important progress milestones;
- tool summaries;
- approvals;
- artifacts/evidence references;
- verification results;
- final state;
- task/project association.

Do not require the old chat history as the Agent's only task database.

---

# 66. Durable vs ephemeral thread data

Durable:

- user request;
- final/important Agent messages;
- approval decisions;
- important tool summaries;
- evidence/artifacts;
- verification;
- terminal state.

Ephemeral:

- token streaming deltas;
- animation state;
- spinner state;
- hidden provider reasoning;
- transient UI hover/open state.

---

# 67. Project storage boundary

Do not embed full Agent runtime thread state into `ProjectRecord`.

Association:

```text
AgentTask
  ProjectId = project.Id
```

H2 ProjectRecord remains project/user truth.

Agent task/evidence truth remains in Agent-side durable storage.

---

# 68. Quick task persistence

Global Assistant task:

```text
ProjectId = null
```

must also survive restart if it is recent/important.

It can later be attached:

```text
AttachProject(taskId, projectId)
```

without rewriting its historical execution.

---

# 69. Thread/task relationship

One Agent task may be one execution inside one conversation thread.

The implementation may support multiple tasks in one conversational thread later, but the minimum model must keep task IDs explicit so verification/evidence remain unambiguous.

Do not infer task ownership solely from visual message order.

---

# 70. Scroll behavior

Required:

- when user is at bottom, streaming follows;
- when user scrolls upward, do not yank them to bottom;
- show "new activity" affordance;
- reopening a thread restores reasonable reading position when possible;
- tool card expansion must not unexpectedly reset scroll.

---

# 71. Long thread virtualization

Do not keep thousands of complex controls alive.

Use virtualization/windowing for older thread items where possible.

Load older history progressively.

---

# 72. Copy behavior

User should be able to copy:

- paragraph;
- Markdown text;
- code block;
- table cell/row/table;
- tool summary;
- path;
- evidence summary;
- final answer.

Copying a visual card should have a sensible text representation.

---

# 73. File/path interactions

Paths displayed in Agent output should be recognized as resources when safe.

Actions may include:

- open;
- reveal;
- copy path;
- inspect.

Never execute a path as a command merely because it appeared in Markdown.

---

# 74. Link behavior

External links:

- clearly external;
- open through safe browser handling;
- no automatic navigation from model output;
- no embedded arbitrary web script execution.

---

# 75. Accessibility

Required:

- keyboard navigation;
- screen-reader names for cards/actions;
- focus order;
- visible focus;
- sufficient contrast;
- table semantics where practical;
- buttons not icon-only without accessible labels;
- reduced-motion compatibility where possible.

---

# 76. Keyboard behavior

Suggested:

```text
Enter             newline
Ctrl+Enter        send
Esc               close transient popup / stop selection flow
Ctrl+K            project/chat switch/search where consistent
Ctrl+Shift+Space  global Work Assistant hotkey
```

Do not change existing user-approved shortcut semantics silently.

---

# 77. Visual density

Target professional technical workspace, not messenger-style decoration.

Principles:

- user bubble compact;
- Agent content document-like;
- muted borders;
- progress compact;
- cards collapsible;
- artifacts identifiable;
- no excessive emojis;
- color conveys state but is not the only state indicator.

---

# 78. Status vocabulary

Canonical user-visible task states:

```text
Queued
Working
Waiting for approval
Waiting for user
Blocked
Failed
Cancelled
Completed
Completed · Verified
```

Avoid inconsistent synonyms across surfaces.

---

# 79. Same-task continuation

After:

- approval;
- tool failure;
- verification failure;
- capability install;
- user decision;

the Agent should continue the same task/thread unless a genuinely new goal begins.

Do not require the user to resend the original prompt.

---

# 80. Capability installation in thread

If capability is missing:

```text
Agent needs an AutoCAD capability.

H2 AutoCAD Tools
Structured AutoCAD inspection/editing
[Install]
```

After install:

```text
✓ Package verified
✓ Tools registered
✓ Skill available
Continuing task…
```

Same task continues.

---

# 81. Plugin/skill detail placement

Plugin/skill internals should not dominate the main chat.

Normal surface:

```text
Using skill: Excel formula audit
```

or a compact card.

Full provenance/details live in inspector/developer details.

---

# 82. Project management remains primary H2 shell

This specification does **not** replace Command Center.

Top-level app still centers project management.

Recommended navigation:

```text
Command Center
Projects
Project
  ├─ Agent
  ├─ Tasks
  ├─ Notes
  ├─ Files
  └─ History
Work Assistant
Settings
```

Agent chat uses this specification whenever opened.

---

# 83. Project Agent relationship to project details

Agent is the primary work execution surface inside the project.

Tasks/Notes/Files/History remain accessible as detail panels/tabs.

The user must not need to leave the thread just to understand what the Agent is doing.

---

# 84. Work Assistant compact mode

Compact mode intentionally shows less:

- context chips;
- permission preset;
- prompt;
- concise progress;
- final/attention state.

It does not need full Markdown/table/artifact rendering.

Action:

```text
Open full thread
```

must navigate to the canonical full AgentChatSurface for that task.

---

# 85. Work Assistant scope label

Compact/global mode must clearly show:

```text
Scope: This computer
```

Project Agent must clearly show:

```text
Scope: <ProjectName>
```

Avoid user confusion between the two modes.

---

# 86. Formatting consistency

The same Markdown source must render equivalently in:

- project Agent;
- detached Agent;
- reopened task thread.

Do not maintain different Markdown engines for different windows.

---

# 87. Existing H2 chat migration

Current useful pieces may be reused:

- composer;
- attachment picker;
- model selector;
- permission selector;
- conversation/history selection;
- file cards;
- streaming infrastructure;
- detached window shell.

But the old message renderer must be replaced/refactored where necessary to support the canonical typed thread surface.

---

# 88. Existing legacy actions

Legacy `h2-actions` parsing is not part of the new Agent chat model.

New Agent mutations use typed host tools and typed cards/events.

Old conversations may still render their historical action audit.

Do not execute old pseudo-action blocks again.

---

# 89. Data model direction

Conceptual public UI records:

```text
AgentThread
{
    ThreadId
    ProjectId?
    Title
    CreatedUtc
    UpdatedUtc
}

AgentThreadItem
{
    ItemId
    ThreadId
    TaskId?
    Kind
    Sequence
    CreatedUtc
    Payload
}
```

Exact storage types may differ.

Do not duplicate Agent task/evidence source of truth merely to render the UI.

---

# 90. Typed card references

Cards should primarily reference authoritative IDs:

```text
TaskId
ToolCallId
ArtifactId
EvidenceId
ApprovalId
VerificationId
```

The UI queries bounded projections.

Do not duplicate full raw artifacts inside thread records.

---

# 91. Performance budgets

Design goals, to be measured later:

- composer typing must remain immediate;
- streaming should not rebuild the entire thread per token;
- 1,000+ historical thread items must not create 1,000 heavy controls eagerly;
- large tables/spreadsheets must virtualize;
- opening an artifact should not load unrelated artifacts;
- Markdown parsing should be cancellable/bounded for very large messages;
- tool cards should keep raw details lazy.

Do not fake success with hard-coded timing thresholds before measurement.

---

# 92. Failure isolation

A malformed Agent Markdown message must not crash the entire conversation.

Fallback:

```text
render safe plain text
+
record renderer diagnostic
```

A broken artifact preview must not lose the artifact or task.

---

# 93. Thread integrity

Do not allow UI actions to reorder authoritative task events.

Visual grouping/collapsing is allowed.

Sequence identity remains deterministic.

---

# 94. Global Assistant acceptance scenarios

## Scenario GA-1 — Active Excel

Foreground:

```text
D:\Work\DuToan.xlsx
Sheet BAOCAOGS
Selection D51
```

User:

> "Kiểm tra công thức này."

Acceptance:

- Global Assistant resolves the active workbook/cell;
- context chips show the target;
- tool calls render inline;
- mutation asks permission if required;
- verification appears;
- final Markdown/table renders correctly.

---

## Scenario GA-2 — Quick AutoCAD

Foreground AutoCAD drawing.

User:

> "Kiểm tra các block đang chọn."

Acceptance:

- active drawing/selection grounds the task;
- structured AutoCAD tools preferred;
- tool/evidence cards shown;
- no H2 project required.

---

# 95. Project Agent acceptance scenarios

## Scenario PA-1 — Ignore external foreground Excel

Project:

```text
X:\DuAn\ThachBich
```

Open:

```text
X:\DuAn\ThachBich\DuToan.xlsx
D:\Personal\Budget.xlsx
```

Foreground:

```text
D:\Personal\Budget.xlsx
```

User in project chat:

> "Đọc file Excel đang mở."

Acceptance:

- external foreground workbook is not used implicitly;
- Agent chooses the valid open project workbook if unambiguous;
- context shows project workbook.

---

## Scenario PA-2 — No project workbook open

Only external workbook is open.

User:

> "Đọc file Excel đang mở."

Acceptance:

- Agent does not silently use external workbook;
- task reports no project workbook is open;
- Agent may offer project workbook choices or request clarification.

---

## Scenario PA-3 — Explicit external override

User:

> "Đọc D:\Reference\DM.xlsx để đối chiếu."

Acceptance:

- exact external file is allowed as explicit task target;
- context shows an External chip;
- parent folder/drive is not implicitly added;
- task remains associated with the project.

---

# 96. Markdown rendering acceptance

One Agent answer containing:

- H2/H3;
- bold;
- italics;
- strike;
- inline code;
- list;
- task list;
- quote;
- link;
- fenced code;
- Markdown table;
- horizontal rule;

must render semantically and remain copyable/selectable.

No raw Markdown markers should appear except inside source/code views.

---

# 97. Table acceptance

Test:

- narrow table;
- wide table;
- 50-row table;
- 1,000-row table via preview/inspector;
- copy;
- horizontal scroll;
- alignment.

No whole-thread layout explosion.

---

# 98. Spreadsheet acceptance

Use real workbook fixture:

- multiple sheets;
- formulas;
- values;
- changed cells;
- 10k+ populated cells.

Acceptance:

- virtualized grid;
- formula/value mode;
- changed-cell display;
- inspector responsive;
- open native workbook action;
- no raw JSON presentation.

---

# 99. Tool card acceptance

Tool card must prove:

- running;
- success;
- failure;
- cancelled;
- recovered/retried;
- large output artifact;
- evidence link.

Cards remain attached to the correct task/tool-call identity after restart.

---

# 100. Approval acceptance

Test:

- read request no approval;
- mutation asks once when policy requires;
- allow;
- deny;
- expired/stale target;
- external project target label.

Approval state survives UI rerender without duplicate execution.

---

# 101. Verification/repair acceptance

Scenario:

```text
mutation
→ verifier FAIL
→ repair
→ verifier PASS
→ final
```

Thread must visibly preserve:

- failed verification;
- repair stage;
- final pass.

No false Completed before final pass.

---

# 102. Restart acceptance

Start task, produce:

- tool cards;
- evidence;
- final answer.

Restart H2.

Acceptance:

- thread is reconstructable;
- final result/evidence visible;
- no raw runtime dictionary dependency;
- project association retained;
- Global task remains unscoped if not attached.

---

# 103. No duplicate Agent architecture

Architecture guard should fail if someone introduces:

- ProjectAgentRuntime;
- GlobalAgentRuntime;
- QuickAgentRuntime;
- separate WorkAssistant ToolRegistry;
- second permission engine;
- second evidence database;
- Agent task arrays inside ProjectRecord.

---

# 104. No fake-only acceptance

Unit/UI tests may use fake adapters.

But final end-to-end acceptance for Agent chat requires:

```text
H2 AgentChatSurface
→ IH2AgentAdapter
→ concrete production bridge
→ accepted AgentRuntime
→ tool/provider
→ evidence/verification
→ final thread
```

A FakeAdapter alone is not proof of the real Agent surface.

---

# 105. Relationship to physical two-PC NAS testing

This chat specification does not block implementation on the currently deferred physical two-PC/NAS test.

However:

- thread/project shared-data behavior must remain compatible with the Product Master storage rules;
- machine-local layout/context must stay local;
- physical two-PC/NAS certification remains required at the final data-integrity gate before claiming production-certified multi-PC support.

---

# 106. Implementation priority

Recommended order:

```text
1. scope model / target resolution
2. canonical AgentChatSurface
3. typed thread item model
4. Markdown renderer
5. progress/tool/approval/verification cards
6. artifact inspector
7. spreadsheet/CSV viewer
8. Word/PDF/image views
9. persistence/restart
10. Global + Project acceptance
11. performance/accessibility polish
```

Do not build every artifact viewer before the core thread model and Markdown renderer are correct.

---

# 107. Required reuse

Prefer reusing:

- current H2 composer behavior where good;
- current attachments;
- existing Agent progress/evidence DTOs;
- current Work Assistant context chips;
- current H2AgentAdapter;
- existing project navigation;
- current rich editor infrastructure only where technically appropriate.

Do not preserve a legacy implementation merely to avoid refactor if it conflicts with this spec.

---

# 108. Final UX rule

The Agent chat should feel like one continuous professional work session:

```text
User goal
↓
Agent understands
↓
progress
↓
tools
↓
approval when needed
↓
artifacts
↓
verification
↓
repair
↓
final result
```

The user should never need to understand the internal separation between model, tool, verifier, plugin and artifact store merely to follow the work.

---

# 109. Final architecture rule

The entire design can be summarized as:

> **H2 remains project-management-first. Global Assistant and Project Agent use the same Codex-style Agent chat surface and the same Agent runtime. Global mode resolves implicit targets from the current computer; Project mode resolves implicit targets from the project, while explicit user targets may extend outside the project without bypassing safety. The thread renders Markdown, tables, code/diffs, progress, tools, approvals, artifacts and verification as typed first-class UI—not raw text or raw JSON.**

This specification is normative for all new H2 Agent chat work.

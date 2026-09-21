# H2 Notes — Current Product Baseline

> **HISTORICAL / CURRENT-IMPLEMENTATION EVIDENCE — NOT FUTURE ARCHITECTURE**
>
> This snapshot is retained to prove what H2 Notes implemented at H2M-000 and to support migration/regression comparison. It must not be used as a future-design directive. The canonical future product authority is `docs/H2_PRODUCT_MASTER_SPEC.md` + `docs/H2_PRODUCT_MASTER_TASKS.md`; the independent data/NAS defect authority is `docs/H2_NOTES_NON_AI_BUG_LEDGER.md`.

Status: **H2M-000 COMPLETE**  
Date: **2026-09-19**  
Purpose: freeze the factual H2 Notes implementation before AI-first product redesign.

## 1. Repository / CI baseline

- `main` HEAD at capture: `8a45794b15a791f761d3f43372e6d397cdb6ab7b`.
- active implementation branch: `feature/nas-multi-device-sync`.
- branch HEAD at H2M-000 inspection: `c22c0e5a5091d95e91f773ac25991a7cc771d29f`.
- last functional Agent/H2 source tested by the full pipeline: `0a7ea1e6586bc5e70e336536603a8044fab654d6`.
- GitHub Actions run: `35448436223` — SUCCESS.
- H2 Notes suite in that run: **336 passed, 0 failed**.
- The branch changes after that functional source are publish/documentation/Phase-13 preparation files only; no `src/` or `tests/` file changed before this baseline.

H2M-000 itself changes documentation only.

## 2. Shared workspace storage baseline

Primary implementation:

- `src/H2Notes.Core/ProjectWorkspaceStore.cs`
- frozen blob inspected: `3b5f8d646f1419bfb6bc3fb8a2139d9e81d7ab7d`.

Current shared workspace schema:

```text
ProjectWorkspaceStore.SchemaVersion = 5
```

The reader accepts schema versions 2, 3, 4 and 5 for migration/compatibility.

Shared layout:

```text
<workspace root>/
  workspace.h2index.json
  projects/<project-guid>.h2project.json
  notes/<note-guid>.h2note.json
  .h2-transaction.json              (during transaction)
  backups/save-<transaction-id>/... (recovery)
  conflicts/...                     (conflict audit where applicable)
```

The index contains shared sheet-level state plus file entries and SHA-256 identities. Project/note payloads are stored separately by stable GUID.

Project file envelope:

```text
SchemaVersion
BoardId
ProjectRecord
```

General note file envelope:

```text
SchemaVersion
NoteRecord
```

Current commit path:

```text
Acquire commit lock
 -> recover prior transaction
 -> read remote snapshot
 -> merge stable entities
 -> build next package
 -> backup affected files
 -> write .h2-transaction.json
 -> publish changed project/note/index files
 -> delete journal
 -> final ReadSnapshot validation
```

That ordering is intentionally recorded because H2-NONAI-002 identifies the journal-before-final-validation risk.

Desktop session state is intentionally removed from the shared index by `BuildPackage`; the Avalonia app stores desktop session locally.

## 3. Current project / note / task data model

Primary implementation:

- `src/H2Notes.Core/SheetModels.cs` — inspected blob `c08c576c3526869be3df6e3b8d469eb942058a27`.
- `src/H2Notes.Core/AiModels.cs` — inspected blob `1438207dfc848825f6d57eed0cd14b5b1d4e42b8`.

Current hierarchy:

```text
SheetState
  -> NoteRecord[]
       -> board/project-hub NoteRecord
            -> ProjectRecord[]
                 -> TaskRecord[]
                 -> project rich notes
                 -> ProjectLink[]
                 -> AiConversation[]
                 -> ProjectLayout
       -> ordinary NoteRecord
       -> standalone AI-chat NoteRecord where applicable
```

`ProjectRecord` currently owns durable project content:

- stable ID;
- created/updated timestamps;
- name/rich name;
- notes/rich notes;
- project checklist `TaskRecord[]`;
- project AI conversations;
- selected AI conversation ID;
- links;
- `ProjectLayout`;
- extension data.

Deterministic project progress is:

```text
completed TaskRecord count / total TaskRecord count
```

`TaskRecord` is the project/user checklist model; it is not an Agent execution-step model.

Existing models use `JsonExtensionData` in important persisted records, which is part of current compatibility behavior and should not be removed casually.

## 4. Current chat/history data model

`AiConversation` currently stores:

- stable conversation ID/revision;
- created/updated timestamps;
- title;
- local draft + draft attachments;
- selected profile;
- marker-only mode;
- permission mode;
- reasoning effort;
- `AiMessage[]`.

`AiMessage` currently stores:

- stable message ID and optional parent ID;
- merge sequence;
- AI run ID;
- device ID;
- role/content;
- provider/model;
- captured context;
- status/error;
- attachments/saved files;
- original timestamp when known;
- timeline-marker flag;
- project-action applied/audit metadata.

Legacy messages with unknown timestamps intentionally remain unknown rather than being assigned a fabricated current time.

This history is user data and is a migration-preservation requirement.

## 5. Current MainWindow information architecture

Primary files:

- `src/H2Notes.Avalonia/MainWindow.axaml` — inspected blob `914fd2e21558d1c5742f33f237ad78294f822f4f`.
- `src/H2Notes.Avalonia/MainWindow.axaml.cs` — inspected blob `39fc4b945f99aec20ce69f66df7d2eb5cebec425`.

Current top-level navigation:

```text
Dự án
Ghi chú
AI
Cài đặt
```

Current project screen is primarily:

```text
project sidebar / picker
        +
project title + deterministic progress + next task
        +
Task pane
        +
Notes pane
        +
optional AI host
```

At narrow sizes the Task/Notes split converts to compact tabs/drawers. AI can be hidden, docked, or detached/floating.

This is the baseline that the Product Master intends to replace with:

```text
Command Center
Project Workspace (AI-first)
Work Assistant
```

without losing the underlying project/task/note data.

## 6. Current project AI execution path

Primary files:

- `src/H2Notes.Avalonia/Controls/AiChatPanel.cs` — inspected blob `41cae598c83bd798e0240492dcf65b8105171888`;
- `src/H2Notes.Core/AiProjectContext.cs` — inspected blob `f8a19a2eed53abff56491ee43d3b85918dd3ab4f`;
- `src/H2Notes.Core/AiProjectActions.cs` — inspected blob `a5d1c0861cd623dbcd956c272b52e0b0d43d2a40`;
- `src/H2Notes.Avalonia/Controls/AiChatPanel.Actions.cs` — inspected blob `f84947a2e46ea4bd3f6dfdf1a599f6e82b6e37bf`.

Current normal project-chat path is:

```text
MainWindow / ProjectAiWindow
 -> AiChatPanel
 -> BuildProjectContext()
 -> AiProjectContext.Prepare(...)
 -> AiClient.StreamEvents(...)
 -> AiMessage streaming/final persistence
 -> AiProjectActions.Parse(model text)
 -> permission-mode validation
 -> AiProjectActions.Apply(ProjectRecord,...)
```

The current project AI path therefore still uses the legacy direct `AiClient` path and model-authored `h2-actions` pseudo-protocol. It does **not** yet use the accepted Agent public integration boundary / H2AgentAdapter.

Current typed legacy project actions are limited to:

```text
add_task
update_task
delete_task
append_note
replace_note
delete_note
```

Legacy direct execution must remain until Agent-adapter parity covers chat, history, attachments, cancel/progress, permissions and project actions.

## 7. Current AI window behavior

`src/H2Notes.Avalonia/MainWindow.ProjectAi.cs` and `ProjectAiWindow` manage an in-board/detached project AI surface.

Existing verification evidence in:

- `docs/ui-verification/2026-09-15-project-ai-window/REPORT.md`;
- associated compact/floating/detached/redocked screenshots.

The same project-scoped AI panel/history can move between board and detached window; this existing conversation/history behavior must be preserved or safely migrated.

## 8. Local vs shared state baseline

`src/H2Notes.Avalonia/LocalConfiguration.cs` currently stores:

```text
DataFolder : string?
DesktopSession : DesktopSessionState?
Ai : AiConnectionSettings
```

Local config lives under the current Windows user's LocalApplicationData H2Notes directory.

Important existing boundary:

- desktop session is local;
- shared workspace state is stored by `ProjectWorkspaceStore`;
- however `ProjectLayout` currently lives inside `ProjectRecord` and includes AI dock/width/height/X/Y and task/note collapse values.

The Product Master must audit which of those layout values are machine-local rather than project content.

## 9. Open non-AI bug baseline

Canonical ledger blob at capture:

`d700417a76480215de5eca4222ff85cbd9edb721`.

Open entries:

| ID | Severity | Status | Area |
|---|---|---|---|
| H2-NONAI-001 | HIGH | OPEN | NAS/multi-PC mixed project/index generation |
| H2-NONAI-002 | HIGH | OPEN | persistence/recovery journal ordering |
| H2-NONAI-003 | MEDIUM | OPEN | sync diagnostics |
| H2-NONAI-004 | HIGH | OPEN-RISK | shared-filesystem capability assumptions |
| H2-NONAI-005 | HIGH | OPEN | persistent mixed-generation recovery |
| H2-NONAI-006 | HIGH | OPEN | no real two-PC/NAS acceptance |
| H2-NONAI-007 | MEDIUM | OPEN-KNOWN-GAP | offline pending-operation durability |
| H2-NONAI-008 | HIGH | OPEN | mapped/UNC location identity and endpoint failover |
| H2-NONAI-009 | HIGH | OPEN | path aliases vs logical workspace identity/locking |

Count at baseline:

- **7 HIGH**
- **2 MEDIUM**
- **9 total open/risk/gap entries**

No HIGH item may be silently ignored by later product integration.

## 10. Current UI evidence baseline

Historical/current implementation evidence retained for comparison:

- `docs/APPROVED_PRODUCT_SPEC.md`;
- `docs/RESPONSIVE_IMPLEMENTATION.md`;
- `docs/UI_ACCEPTANCE.md`;
- `docs/ui-verification/2026-09-15-responsive-final/REPORT.md` plus its UI-01..UI-10 renders;
- `docs/ui-verification/2026-09-15-project-ai-window/REPORT.md` plus compact/floating/detached/redocked renders;
- composer/chat/files/thinking verification folders under `docs/ui-verification/2026-09-15-*`.

The responsive-final report itself explicitly says that snapshot was still “đang làm, chưa nghiệm thu UI”; it is evidence/baseline, not future architecture authority.

## 11. Data-compatibility risks to preserve during redesign

1. Schema 2/3/4 workspaces still have a read/upgrade path into schema 5.
2. Existing `ProjectRecord`, `TaskRecord`, rich notes and links are user data.
3. Existing `AiConversation`, `AiMessage`, attachments, saved files, provider/model metadata and unknown timestamps are user data.
4. Stable project/task/conversation/message GUIDs are used by merge/action behavior.
5. `JsonExtensionData` supports unknown persisted fields and must not be lost casually.
6. Machine-local draft/session behavior must not leak back into shared NAS state.
7. `ProjectLayout` currently mixes likely project UI preference and machine-specific geometry and needs explicit migration.
8. Legacy direct project AI cannot be removed before H2AgentAdapter parity.
9. Current NAS transaction/recovery risks must be resolved before expanding shared persistent state.
10. Real NAS behavior cannot be certified from local filesystem tests alone.

## 12. H2M-000 conclusion

H2M-000 introduces no runtime behavior change.

The baseline is now explicit enough to compare every later Product Master migration against:

```text
storage/schema
project/task/note data
chat/history data
current UI
current AI path
local/shared state
known data-integrity risks
test/CI evidence
```

Next task: **H2M-001 — Mark future product documentation authority**.

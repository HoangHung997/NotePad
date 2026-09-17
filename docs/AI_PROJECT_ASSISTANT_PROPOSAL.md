# Project-aware AI assistant (discussion, not implemented)

Status: proposal following the user's 2026-09-15 feedback. This icon/resize change
does not enable filesystem access, OS activity monitoring, or autonomous actions.

Follow-up correction: independent means a desktop window of the selected project,
not a separate conversation. The same chat editor, draft and history move between
the board and desktop; project selection follows both ways. Earlier standalone
notebooks remain accessible without merging/deleting their data. Local-only
milestones and desktop session restore are implemented. These explicit user-authored milestones are not an automatic work
journal. Folder tools, editor observation and evidence-based resume answers below
remain future agent work; no such access was enabled by the chat UI changes.

## Goal

AI should answer from the selected project's actual work, not just the current
chat window. Switching providers must not erase project memory. After reopening
H2 Notes, the user should be able to ask what remains unfinished and resume it.

## Four parts

1. Project context: stable project ID, name, ordered tasks, completion, next task,
   rich notes, references, linked folders, conversations, and recorded actions.
   Read current drafts before responding; never confuse another project's data.
2. Scoped tools: list/search approved project folders, read supported documents,
   open a selected document with its associated application, and reveal its
   containing folder. The application validates each call; the model does not
   receive arbitrary terminal or unrestricted filesystem access.
3. Durable work journal: append timestamped events for task changes, note saves,
   project/file opens through H2, user-marked in-progress files, successful tool
   results and failures. A daily resume checkpoint links back to original events.
4. Evidence-based answers: cite the task, note section, file path/page/sheet, or
   journal event used. Show when the source was observed and whether it is stale.

## Suggested interface

- AI header shows the current project, connection/model and context scope.
- Context chips: Project / Recent work / Linked folders / Selected text.
- A Files drawer shows linked roots, recent files, and explicitly in-progress files.
- A History tab shows a searchable timeline of recorded work and AI tool actions.
- Tool cards show "Reading", "Opened", "Failed", or "Awaiting approval", with
  the actual target path. Never show success solely because the model says so.
- Resume card: last observed session, next task, unfinished files, source links.
- Folder access and online context previews are manageable and revocable.

## What the assistant can truthfully know

| Question | Evidence | Boundary |
| --- | --- | --- |
| What am I doing in this project? | Current tasks, notes, checkpoint, journal | State when information was last observed. |
| What was my last file? | Last file opened via H2, or supported editor integration | Call it "last opened via H2" unless editing was actually observed. |
| What changed in this folder? | Last scan, file metadata/hash, optional watcher | A changed modification time does not prove who edited it. |
| Open my unfinished files | Explicit in-progress list or saved working set | Ask which files if inferred/ambiguous; do not mass-open a folder. |
| What happened yesterday? | Persisted journal and cited checkpoints | No invented activity while H2 was not observing. |

Access to linked folders does not by itself reveal which file was active in
AutoCAD, Excel or another application. App-specific integrations may later add
observed save/open events, with user opt-in. No keyboard logging or screen capture
is proposed. Unobserved periods remain unknown.

## File support and retrieval

- First stage: folder/file inventory and bounded plain text/Markdown/CSV reads.
- Next: adapters for DOCX, XLSX (sheet/range), PDF text and optional OCR for scans.
- DWG and other specialist/binary formats need explicit adapters or supported
  exports. Listing/opening a file is not the same as understanding its contents.
- Search a local, rebuildable index and send only relevant excerpts. Do not send
  an entire project on every question or rescan every file for each keystroke.
- Refresh stale excerpts on demand; handle unavailable drives, moved files,
  access errors, cancellation and large files without freezing the editor.

## Storage and privacy

- Keep authoritative conversations, references, working set, events and resume
  checkpoints inside the existing per-project data file. Stable IDs survive
  project renames. Paths reference original documents, not copies by default.
- Search indexes and extracted-content caches can be separate, rebuildable local
  caches. They are not a second authoritative project database.
- Save journal batches asynchronously with bounded size. Define retention and
  archival explicitly before full journal history grows without limit.
- Scope roots per project. Resolve real paths and reject traversal/symlink escapes;
  exclude credentials, hidden system folders and explicitly ignored patterns.
- Reading files is separate from uploading them. Show destination, model and
  selected excerpts before online transmission; remember consent only at the
  scope the user chose. Localhost/Ollama alone does not guarantee that a model
  is entirely local: cloud-backed models need the same online disclosure.
- Treat document contents as untrusted reference data, never as tool instructions.
- Opening a document on an explicit request may proceed in the approved scope.
  Executables, scripts, macros, external URLs, writes, renames, deletions and
  outside-scope access require separate validation/approval. No generic shell tool.
- Editing project data should show a proposal/diff and keep an undoable audit trail.
  API keys remain machine-protected, outside portable project files.

## Suggested delivery order (requires agreement)

1. Project context + work journal + resume answers + source links, no arbitrary files.
2. Linked-folder browser, safe file-open tools, bounded text retrieval and citations.
3. Office/PDF adapters and optional editor integrations for observed editing history.
4. Optional approved project/task/note edits with preview, undo and audit history.

Acceptance examples: restart and answer from persisted history; choose the right
project after switching; refuse an escaped path; show unknown activity honestly;
cancel safely; never claim an unread file was understood; no file content goes
online without the agreed scope; changing the model retains all project history.

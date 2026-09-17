# Chat history and independent AI window - 2026-09-15

## User-approved changes

This stage follows the user's additional request, without replacing the original
10-image baseline: send alongside composer, user messages right / AI left, small
message clocks and Zalo-like date/time separators, plus an independently pinned
AI window with persistent history outside projects.

The optional "Chi luu moc" mode records local work milestones without asking AI.
Its state is remembered per conversation. Markers are excluded from all later
AI request history, not just the request at the moment the marker is saved.

## Implementation

- Shared `AiChatPanel` and `ChatMessageView` serve both project chat and standalone
  chat. Input and send/stop occupy the same grid row. Bubbles align independently,
  wrap within 88% of the available message width, and retain selectable text.
- Each dated message displays local HH:mm, with full date/time in a tooltip.
  Dividers appear on a new date or a gap of at least 15 minutes. Legacy missing
  times display as unknown; they are never replaced by the current time.
- A stable message-ID map updates streaming body text rather than accidentally
  updating a timestamp/footer. Interrupted answers keep partial text and time.
- The independent chat opens from the tray, the main app menu, right-click on
  the AI rail button, or the project AI pin menu. It is a real desktop window:
  movable/resizable, initially pinned, no taskbar/minimize/maximize, X hides.
- Standalone history, draft, selected conversation, window bounds and pin state
  persist. Session restore and tray raising include it. No artificial project is
  created and no selected-project context is sent by independent chat.
- Storage uses a dedicated `NoteKind=ai-chat` record in its own `.h2note.json` file;
  project conversations remain inside their respective `.h2project.json` files.
  Merge/import reassign copied message/conversation/parent IDs consistently.

## Storage compatibility

Workspace schema is now 3. Schema 2 is read and upgraded in the same backed-up,
rollback-capable transaction on the first successful save, including all files
even for an incremental save. Previous schema-2 clients reject the new index,
instead of interpreting local-only markers as normal messages and transmitting
them. WPF source, `_ver2`, and original migration source files remain untouched.

The live workspace upgraded successfully: 18 projects retained, 2 regular notes
retained, 1 independent chat added, all 21 indexed file hashes verified, no pending
transaction. The pre-upgrade index hash was found intact in the transaction backup.
The user exited the old app normally before replacement. Updated regular app
started with `--show --show-ai`, PID 2828 at launch, responding.

## Verification and images

- Release build: 0 warnings, 0 errors.
- Complete suite: **94 passed, 0 failed**, including new conversation isolation,
  selected-history/draft/mode persistence, bubble alignment, composer layout at
  340/560/900 DIP, timestamp migration, local-only request filtering, a mock HTTP
  response rendered into the right body, chat-only startup session, safe merge,
  full schema upgrade and interruption rollback.
- Actual Release controls were rendered with explicitly fabricated chat fixtures
  and a separate temporary demo file. No live AI call was made.
- `project-floating-1040x760-render96.png`: compare to UI-06, amended composer,
  bubble direction and clock requirements. Existing surrounding shell retained.
- `project-compact-560x820-render96.png`: compare to UI-04, same chat behavior.
- `independent-420x760-render96.png`: independent desktop adaptation of the warm
  AI panel, with pin/settings/close and timeline.
- `independent-minimum-340x420-render96.png`: compact shell retains input/send;
  message history scrolls instead of pushing the composer out of the window.

Images are RenderTargetBitmap at 96 DPI; app reports screen scaling 1.25. They are
not native screenshots. Native computer-use inventory again returned no H2 Notes
off-taskbar windows, so physical drag/pin/IME/multimonitor interaction is not
certified by this stage. No taskbar behavior was changed for testing. Real
provider/network behavior remains covered by mock-transport regressions only.

Filesystem tools, OS/editor activity tracking and the broader project-aware agent
remain separate work described in `docs/AI_PROJECT_ASSISTANT_PROPOSAL.md`.

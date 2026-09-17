# Icons and resize affordances - 2026-09-15

## Scope and evidence

- Branch: `codex/project-sheet`, existing uncommitted Avalonia implementation.
- Approved baseline: `h2-notes-responsive-hybrid-v1`; all 10 PNG SHA-256 hashes
  and byte lengths verified unchanged. No approved images were regenerated.
- New renders: actual Release application controls, isolated
  `H2Notes-icons-20260915-demo.json`, no real project edits or AI requests.
- RenderTargetBitmap output at 96 DPI, logical dimensions in filenames; native
  screen scaling reported by the application is 1.25. These are control renders,
  not Windows screenshots or proof of ClearType/native cursor appearance.
- UI-01/02/03/04: 560x820; UI-05/06: 1040x760; UI-07: 1440x860;
  UI-08-docked: 1160x820; UI-09/10: 560x600.

## Icon comparison

Compared the approved wide/floating references against the new UI-01, UI-05,
UI-06 and UI-09 renders. The same 24-unit original vector family now supplies:

- Solid terracotta folder, folded-page document, outlined four-point AI sparkle,
  outlined gear, tilted pushpin, three dots and two-stroke close mark.
- Filled AI sparkle in the Ask button, priority star (wired to move-to-top),
  search lens, six-dot section grips, chevrons, clipboard and plus signs.
- B/I/U, font-color indicator, lists, link, highlight, strike, undo and redo.
- AI header settings/pin/close, filled send arrow and diagonal resize grip.
- Ordinary note uses the same H2 badge, pushpin and close icons as the main shell.

No font-dependent symbol is needed for these controls. Icons inherit foreground
colors and retain accessible names/tooltips on their buttons. Font-color swatches
still synchronize with selection. List and link buttons perform real actions;
lists are editable text prefixes, links insert an address as text, not a new
hyperlink-navigation subsystem. Both list actions are also in the context menu.

## Resize behavior

- Window hover and press share the same seven-DIP edge hit test and eight
  direction mapping. Corner targets extend along the edge strip to 14 DIP.
- Temporary cursor override is disposed on leaving the region/window, capture
  loss, close, and resize/maximized state changes. Child I-beam/hand is restored.
- Work/notes and docked-AI splitters have 12-DIP hit areas, paired center grip,
  hover accent and correct horizontal/vertical cursors. The native GridSplitter
  theme is retained so the drawn handle is actually draggable.
- Floating AI has a visible 22-DIP corner target and diagonal cursor.
- Sheet column-boundary hover and press use one hit test. Compact task lists do
  not falsely offer column resizing; row/editor hover clears the column cursor.

## Validation

- Release build succeeded; complete console suite: **83 passed, 0 failed**.
- Tests cover vector render at 16/20/28 DIP and inherited color, all eight window
  edge/corner hit tests, restoring child cursor, non-resizable/maximized states,
  dragging from the outer part of a splitter, AI splitter sizes, star action,
  list undo/format preservation, column hover, and previous 77 regressions.
- Native computer-use inventory did not expose H2 Notes' off-taskbar windows.
  Therefore native mouse pointer appearance, mixed-monitor DPI, IME and physical
  mouse dragging remain **not directly verified**. No taskbar behavior was
  changed just to make automation easier.
- User exited the old instances safely. The isolated render instance was stopped;
  the updated regular app was started with `--show` (PID 27296 at launch).

## Remaining differences / acceptance boundary

This completes the vector-icon and resize-affordance implementation, not full
pixel-level acceptance of the previous responsive shell. Sidebar/text density,
header progress placement, the explicit Layout button, and the AI connection/
context/composer layout still differ from parts of the approved mockups. Narrow
toolbars scroll horizontally to preserve reachable controls. UI-08 evidence is
the docked state, not a native drag-preview screenshot. Native user verification
is still needed; do not mark the entire UI acceptance matrix as passed.

AI filesystem tools, activity tracking, journal and resumable work context are
discussion only in `docs/AI_PROJECT_ASSISTANT_PROPOSAL.md`; none was enabled here.

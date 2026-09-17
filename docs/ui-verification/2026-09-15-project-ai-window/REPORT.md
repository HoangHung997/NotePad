# Project AI desktop window correction

Date: 2026-09-15. Branch: `codex/project-sheet`. This supersedes the standalone
conversation interpretation in the previous chat-history report.

## Behavior and data

- Independent means the window, not the project scope. One AiChatPanel is moved
  between board and desktop hosts. History, draft, conversation selection,
  in-flight response and context callbacks are not cloned.
- The desktop window names the current project and provides a project picker.
  Both selection paths update the board, notes and chat. Hiding the board leaves
  detached chat usable and does not cancel its active reply.
- AI X hides; return/dock options restore the same panel inside the board.
  Placement/pinning/open state are session metadata, while chat stays in the
  selected project's existing file. No additional notebook is created.
- Existing legacy standalone notebooks/history remain accessible from the tray
  with an explicit old-history label. No automatic deletion or merge.
- Resize keeps the latest message/time visible when already at the end; reading
  older history is not forced back to the bottom. No external AI API was called.

## Verification

`dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release -p:OutputPath=bin/ProjectAiReview/`

Result: **100 passed, 0 failed**. Final temporary test evidence:
`C:/Users/hoang/AppData/Local/Temp/H2Notes-tests-777853940bb34e7ab63ab33e8a5a7ba8`.

New tests cover shared panel/draft/history, legacy-data preservation, two-way
selection/context, project-local milestones, board hide vs AI hide, geometry,
uncancelled in-flight mock response, latest/history scroll, and real app startup
hooks in isolated headless child processes for both manual and Windows startup.
The latter also exercise graceful application shutdown and saved window order.

Ten original baseline asset hashes match BASELINE.json; no baseline edits.
The actual Release app was run with isolated demo JSON and deliberately fabricated
conversation messages. Six RenderTargetBitmap captures at 96 DPI are stored here;
native render scaling reported 1.25. These are actual app controls, NOT native
desktop screenshots or proof of Windows pointer/IME/DPI behavior.

- `project-floating-1040x760-render96.png`: original in-board floating host.
- `project-compact-560x820-render96.png`: compact in-board AI page.
- `project-desktop-420x760-render96.png`: independent window of selected project.
- `project-desktop-minimum-340x420-render96.png`: minimum independent window.
- `project-desktop-switch-420x660-render96.png`: another project's empty history.
- `project-redocked-1440x860-render96.png`: same conversation returned to right dock.

Inspected desktop/minimum/switch/redocked captures and UI-06 baseline. The added
project selector and window controls fit; send stays alongside the composer,
bubbles retain their sides and the latest timestamp is visible after resizing.
At minimum height, history is intentionally scrollable. Existing font density,
provider dropdown rather than segmented switch, and absent assistant avatar do
not yet match the raster baseline exactly; not claimed as full visual acceptance.

Native verification attempted through the documented sky computer-use API.
`list_windows` did not return either running H2 Notes instance, so no Windows
mouse/tray/resize or native screenshot verification was possible. No guessed
window handles or alternate UI automation were used.

## Rollout status

Follow-up: user exited normally; the corrected window build and AI diagnostic
fixes are now deployed and open. See [live AI report](D:/VSstudio/Nodepad/docs/ui-verification/2026-09-15-ai-connections/REPORT.md)
for 113 passing tests, PID 16564 and saved-connection results. The pending status
below records the end of the previous turn only.

New build is at `src/H2Notes.Avalonia/bin/ProjectAiReview/H2Notes.Avalonia.exe`.
The prior regular process (PID 2828, Release/net10.0) was still running at the last
check. User was asked to Exit via tray; it was not force-terminated or overwritten.
Only owned isolated demo processes were stopped. Live project data was not edited
by this correction's tests. Pending: after the user's normal exit, build the usual
Release target and launch it with `--show --show-ai`, then verify the new process.

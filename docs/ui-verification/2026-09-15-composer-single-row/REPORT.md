# Single-row composer and unified model card

2026-09-15, codex/project-sheet. User-approved change to the composer only.
Reference: `C:/Users/hoang/AppData/Local/Temp/codex-clipboard-b3dc9c8d-8239-4606-b8ec-6bf4296f3d2e.png`.
All ten original responsive baseline image hashes remain unchanged. WPF and _ver2 untouched.

## Behavior

- One footer row at tested widths 340, 420, 560, 660, 700, 960 DIP and after shrinking again.
  Below 460 DIP available footer width, permission text/chevron collapse to a 32 DIP shield
  with full tooltip and accessibility name. Font sizes do not change. Model text ellipsizes.
- Model and reasoning share one button aligned before mic/send. The rounded popup has the
  saved profile selector, current effort, discrete supported steps and a reset icon. Unknown
  models only display the default-capability maximum label, with no editable effort slider.
- Popup closes on project/conversation change, busy state, Escape or outside dismissal;
  stale/hidden slider events cannot change another project's conversation. Saved profiles,
  credentials and effective permission grants are not modified by resize or opening a menu.
- Removed the separate data-preview toolbar and visible marker checkbox. Both actions are
  under + / Context and @ suggestions at all heights, backed by the same marker state.
  Placeholder/send icon and menu check reflect marker mode; selecting it never sends AI.
- Ctrl+Enter is handled before TextBox consumes Enter. An open @ suggestion takes priority;
  a later Ctrl+Enter saves the private timestamped marker or sends normally.

## Evidence

`final/` contains actual Release controls with fabricated projects and random keyless model
profiles. This is an app render, not a generated mockup. Native renderer reported 1.25 scale;
RenderTargetBitmap output is 96 DPI and is not a native desktop screenshot. Recorded requested
and actual sizes are in `final/layout-sizes.txt`. No mic, paid API call or real document used.

- UI-07: files-docked-1440x860-render96.png retains warm project/task/note/AI shell.
- UI-04: files-compact-560x820-render96.png shows the one-row controls in project chat.
- Detached: files-desktop-420x700-render96.png and composer-wide-900x620-render96.png.
- Minimum: composer-minimum-340x420-render96.png leaves chat visible with one attachment.
- model-reasoning-popup-render96.png: compact shared card, not a second footer dropdown.
- marker-context-popup-render96.png: local-only marker available via @.

Earlier root-level images are intermediate passes; use final/ for visual review. The popup
was reduced after the first render, removing redundant headings/help text from the surface
while retaining descriptions in tooltips. Slider uses the existing Fluent interaction accent.

## Tests and limitations

310 tests passed; see tests.txt. Includes footer overlap/centering/resize/selection, popup
containment, supported slider/reset, stale scope/busy events, +/@ marker synchronization,
private timestamped save without an AI profile, and preview opening without sending.
The checks exposed a consumed Ctrl+Enter shortcut and now cover its repaired path.
Final Release build: 0 warnings, 0 errors (build.txt). Final isolated capture exited normally
with empty stderr; the normal Release app was then reopened with --show --show-ai.

Computer Use did not return a targetable H2 window. Native pointer interactions, IME,
microphone and other display scales are not certified by these headless checks or renders.
No claim of exact native pixel parity at all DPI levels. User exited normally before replacing
the Release executable; the final app is reopened only after the isolated capture process exits.

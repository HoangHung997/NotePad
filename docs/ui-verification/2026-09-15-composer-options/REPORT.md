# Composer Options Handoff

Date: 2026-09-15. Scope: composer/options partials, Windows voice-typing launcher,
vector extensions, new options tests and the approved stale layout assertions.
No edits by this worker to AiChatPanel.cs, settings, Core, WPF or _ver2.

## Integration

- BuildComposer calls BuildComposerOptions once. The existing _profiles control is
  removed from _optionsPanel before insertion; original selection handlers remain.
- CurrentPermission is the effective permission. ProjectAccess requires a project,
  the conversation's saved mode AND a local ProjectAccessConversationIds grant.
  Imported project JSON alone cannot grant access. Standalone chat cannot grant it.
- Explicit ProjectAccess selection presents a cancel-default scope warning, saves
  the local grant, then saves the conversation mode and calls Render without
  replacing the editor. ReadOnly/ConfirmChanges revoke a previous local grant.
  A failed local save rolls back the grant change and leaves permission unchanged.
- PersistComposerPermissions defaults to LocalConfiguration.Save. Tests MUST replace
  it before selecting permissions; AiComposerOptionsTests already does so.
- CreateRequestProfile clones the profile and applies supported conversation effort.
  Unknown models display non-interactive "Toi da" (localized in UI), without an
  invented effort parameter. The saved profile and credentials are not modified.
- Main should snapshot CurrentPermission and use CreateRequestProfile before send;
  refresh options after profile refresh and each preparation/request transition.
  Existing RenderAttachments -> RefreshComposerState covers draft/scope reloads.
  Permission selection already calls Render; do not wire a duplicate Render handler.
- Below 500 DIP panel height: input 44/80 DIP min/max, marker in the plus menu,
  one-line ellipsized status with full tooltip. Parent hides the attachment toolbar.
  Compact plus menu includes history, new chat, checked marker and context toggles.
  Below 620 DIP composer-options width the bottom controls use two rows.

## Verification

- Isolated Release build: 0 errors, 0 warnings. Build outputs stayed under
  C:/Users/hoang/AppData/Local/Temp/h2-composer-20260915-artifacts.
- Final isolated run: 35 passed, 0 failed. Includes AiComposerOptionsTests,
  AiComposerUpgradeTests, IconResizeTests and ThinkingUiTests with actual app styles.
- Checks cover 340-960 DIP widths, at least 90 DIP history at 340x420 with one
  attachment, draft/selection preservation, compact menu marker persistence,
  profile cloning, busy guards, imported permission rejection, approval cancellation,
  stale dialogs, local-save failure, existing paste/drop/mention/undo behavior,
  vector rendering and send/stop icons.
- The dictation checks do not send Win+H. They inspect native INPUT layout,
  exact key-down/up sequence, partial cleanup, invalid-handle refusal, lifecycle
  inactivity and the Windows/Microsoft/draft hint. Test permission saves are fake.
- All ten approved baseline PNG SHA-256 values match BASELINE.json. Neither the
  PNGs nor BASELINE.json changed.

## Visual Acceptance

Inspected parent-generated isolated Release evidence in
[composer-pdf/pass1](../2026-09-15-composer-pdf/pass1/):
composer-minimum-340x420-render96.png, composer-wide-900x620-render96.png and
files-docked-1440x860-render96.png. Capture log records native scaling 1.25;
these are 96-DPI RenderTargetBitmap captures, NOT native screenshots.

Compared with approved UI-07 and the supplied Codex composer reference: warm
ivory/terracotta retained, rounded multiline input above controls, black circular
send, one wide bottom row and two unclipped narrow bottom rows. Pass1 exposed
insufficient message space at minimum height; the final compact policy fixes the
layout assertion but needs a refreshed actual-app capture. The unknown-effort
gray disabled box was replaced by a plain non-interactive label after pass1.

Status: final native visual parity, multi-DPI/IME interaction and Windows microphone
availability remain UNVERIFIED. No microphone activation, Windows privacy change,
live-data overwrite or extra app launch was performed. Parent owns final launch
and consolidated acceptance log after the shared test/capture pass.

The launcher follows Microsoft's [SendInput contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)
and [Windows voice-typing documentation](https://support.microsoft.com/en-us/accessibility/windows/use-voice-typing-to-talk-instead-of-type-on-your-pc).
It reports only whether the shortcut was sent, not whether the OS is listening.

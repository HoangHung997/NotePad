# H2M-091 — DesktopSessionState storage audit

Status: implementation candidate; close only after exact-head CI is green.

## Ownership decision

Desktop session restore is machine-local state.

In a project-workspace/NAS configuration, these values belong to the current Windows profile and must not be shared between PCs:

- selected board for desktop restoration;
- ordered open-window IDs;
- general note/chat/board window placement;
- note/window pin state;
- detached Project AI window placement and visibility.

The authoritative machine-local persistence point is:

`LocalConfiguration.DesktopSession`

stored in:

`%LocalAppData%\H2Notes\local-config-v2.json`

## NAS boundary

`ProjectWorkspaceStore` explicitly writes `DesktopSession = null` to `workspace.h2index.json`.

Shared note serialization also neutralizes legacy machine fields before writing NAS note/index content:

- `IsVisibleOnDesktop = false`;
- `IsPinned = false`;
- general note `Left/Top/Width/Height` reset to safe defaults;
- board `SheetLeft/SheetTop/SheetWidth/SheetHeight` cleared.

The legacy fields remain on `NoteRecord` for backward compatibility with single-file/non-NAS storage and old files; they are not authoritative shared placement in project-workspace mode.

## Local restore

`DesktopSessionState.NoteWindows` stores a `NoteWindowPlacementState` keyed by stable note/notebook ID.

After every project-workspace load/store switch and after an external NAS refresh, Avalonia reapplies the current machine's local desktop session to the in-memory model before restore/refresh is presented.

If a PC has no local desktop session yet, H2 uses a new empty session and safe window defaults. It does not infer open windows, pin state or monitor coordinates from NAS.

## Save lifecycle

Before project-workspace save:

1. open windows flush their current model/geometry;
2. `CaptureDesktopSession` snapshots current note/board placements plus open-window order;
3. the session is copied to `LocalConfiguration.DesktopSession`;
4. NAS serialization strips desktop state.

This keeps existing desktop restore behavior without allowing PC A to move/open windows on PC B.

## Compatibility boundary

H2M-091 does not remove the existing legacy `NoteRecord` geometry properties because single-file compatibility still uses them. It changes project-workspace/NAS ownership so those fields are neutralized in shared serialization and local desktop state wins at runtime.

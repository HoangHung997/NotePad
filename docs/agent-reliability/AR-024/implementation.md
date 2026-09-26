# AR-024 — Production Office slice through H2

Validated code: `4e6b4a07f4103edd654b680ca9af7f485f8b04d8`.

## What E2 proves

Two product-surface slices use the real H2 production bridge instead of a RecordingExecutor:

- Global Work Assistant → H2ProductionAgentAdapter → Agent runtime/tool session → H2OfficeRuntimeTools → separate OfficeHost process → Excel fixture backend.
- Project Agent → the same production bridge/runtime → H2OfficeRuntimeTools → separate OfficeHost process → Word fixture backend.

The slices exercise actual H2 UI entry, task identity, permission/target policy, model tool-call transport fixture, production tool registry, OfficeHost IPC, mutation readback, host verification and task completion projection.

## Target-authority repair

OfficeHost fixture intentionally has no native HWND/PID identity. AR-024 therefore does not fabricate a captured native window. The Global slice removes the capture chip and uses task-local FullAccess, but FullAccess is not target selection. The final user prompt explicitly names the exact fixture workbook path; only then can production grounding authorize that resource. A model-supplied session ID alone never becomes host authority.

## Repair history

- `58ba74ef...`: initial production-slice gate.
- `6c928442...`: import Office protocol snapshot types.
- `2b3061ab...`: remove fake captured-window authority.
- `6e53365f...`: add explicit Excel verify readback without weakening completion.
- `472ba1f2...`: expose bounded blocked-completion diagnostics.
- `f800b4b9...`: cancel superseded AR-024 validation runs.
- `53592771...`: focused AR-024 fail-fast in validator.
- `4e6b4a07...`: exact workbook path grounding; final validated code.

## Validation

Dedicated AR-024 run `36221310522` / job `108346988677`: SUCCESS.

- focused AR-024: 2/2
- OfficeHost: 19/19
- retained AR-023: 9/9
- retained AR-022: 8/8
- retained AR-021: 14/14
- retained AR-020: 36/36
- retained AR-012: 44/44
- retained AR-001: 13/13
- full H2: 1311/1311
- required Agent suites: 75/75

Full Avalonia CI `36221310599` / job `108346979174`: SUCCESS, including self-contained Windows x64 publish and packaged DesktopHost/OfficeHost startup+IPC.

All **16/16** pull-request workflow identities on the exact validated code SHA completed SUCCESS. The first AR-023 attempt contained one transient retained AR-021 probe failure; a same-SHA rerun completed SUCCESS, so no runtime change was made for that non-reproduced flake.

## Acceptance boundary

E1/E2 are PASS. This is not E4 because the model transport is scripted and OfficeHost uses its fixture backend. Real E4 requires an allowed configured model plus native Word/Excel on an authorized Windows machine. The user deferred E4 until testing the final full build. No E4 PASS claim is made.

# AR-063 — CAD closed/live scope separation and live selected-attribute bridge

Validated code: `da8b9daefa1bbb1e86b833a72c68ebd395e62cb0`.

## Implemented scope

AR-063 keeps the existing closed-file AutoCAD/CoreConsole/DXF path intact and adds a separate external live AutoCAD COM provider. Live requirements are never replaced by a saved file, HTTP fetch or an unbound command.

The live provider advertises only:
- list/get current live documents;
- query the current PickFirst selection;
- read attributes from a still-selected block reference;
- read live layers;
- update one attribute on one still-selected attributed block;
- verify exact document/entity tokens and requested attribute postcondition.

It does **not** advertise general live entity mutation, plot/verify-plot, arbitrary AutoCAD commands, LISP/script injection, or general dynamic-block editing.

## Safety and identity

Each live operation reacquires the running AutoCAD application/document, validates document/entity state tokens and checks current selection again before mutation. Structured payloads are bounded. The attribute write is independently read back. Stale document/entity state and selection changes reject before a new mutation.

The external COM bridge currently does not claim transaction/undo atomicity. Real close/reopen, Save As/session rebinding and real AutoCAD undo behavior remain native E3/E4 acceptance.

## Repair history

- `4caca275...` — closed/live split + external COM bridge.
- `d9fe9c0f...` — unavailable live capability remains non-callable/testable.
- `a1cf5603...` — validated live-source observation counts correctly.
- `57ccbcb7...` — production slice reduced to one verified user outcome.
- `da8b9dae...` — readiness notices retain live-source semantics; execution unchanged.

## Validation

Dedicated AR-063 run `36254640413` / job `108439028970`: SUCCESS.
- focused AR-063: 5/5
- MB-113 AutoCAD acceptance: 6/6
- retained AR-062: 6/6
- retained AR-060: 6/6
- retained AR-024: 2/2
- retained AR-033: 42/42
- retained AR-012: 44/44
- full H2: 1348/1348
- required Agent suites: 75/75

Full Avalonia CI `36254640513` / job `108438987210`: SUCCESS, including self-contained Windows x64 publish and packaged DesktopHost/OfficeHost IPC.

All 22/22 pull-request workflow identities on the exact code SHA completed SUCCESS.

## Acceptance boundary

E1/E2 are PASS. Real installed-AutoCAD E3/E4 is DEFERRED_BY_USER / AWAITING_ENVIRONMENT until final-build testing. Fixture/live-COM simulation does not certify native AutoCAD behavior. No native-live, general dynamic-block, plot, transaction/undo, arbitrary command or saved-file-as-live claim is made.

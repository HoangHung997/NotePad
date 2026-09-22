# AR-020 — native-window Office discovery and targeted capture

Status: ACTIVE / AWAITING_ENVIRONMENT. Application tests NOT_RUN until exact CI evidence is reviewed.

The existing OfficeHost now enumerates current-desktop XLMAIN/OpusApp roots and EXCEL7/_WwG native panes via OBJID_NATIVEOM, validates process ID/start/desktop-session and Window.Hwnd/root mapping, and keeps each view distinct. Discovery reads metadata, not workbook cells or Word body/selection text. Unsupported, modal/busy, stale and bounded/incomplete observations are explicit, not an empty successful machine-wide inventory. Hidden/headless/protected-view and unsupported-build coverage is not invented.

A bounded STA-owned catalog retains COM references, deduplicates genuine same-document views, re-probes before use and retires sessions on Save As/close/replacement. Old sessions do not fall back to names, order or GetActiveObject. DocumentId is helper-local, not a portable/restart identity; Save As requires rebind. Captured target HWND/PID/start is used even after H2 takes focus. Capture helper connection is reused but capture data is reobserved, with typed failure and elapsed time. Capture remains 3 seconds; execution limits are not globally raised. Discovery has a separate bounded budget and coverage metrics. COM calls remain dependent on installed Office; helper timeout is the isolation boundary, not a promise to interrupt arbitrary COM in-process.

Native identities pass into the existing production binding and snapshot checks. Word selection capture records range positions, not selected plaintext. Provider/permission and no-effect preflight checks remain authoritative. No engine, parallel state store, new model endpoint or NAS protocol.

E1/E2 tests use injected native-object probes/Office clients plus concrete H2 capture, binding and headless UI projection. They cannot certify native Office. E3 must read DOC-A/B/UNSAVED-ONLY on two native instances and views, including Save As, close/reopen and modal cases; currently NOT_RUN. AR-083 remains DEFERRED_BY_USER and no E5 is claimed.

Sources reviewed: Microsoft Learn AccessibleObjectFromWindow (oleacc.h), Excel Window.Hwnd/ActiveSheet/Selection and Word Window.Hwnd/Document/Selection. Native API documentation is not a compatibility test.

## First build and review repair (not acceptance)

Exact source `9e2536970263eaa92ceea89131969fcd55a8ff3c` was delivered and the Windows environment was measured in run `35729096391`.
The build failed with CS0136 in H2OfficeRuntimeTools (a catch-variable name collided with a discovery pattern variable); tests were NOT_RUN.
Excel.Application and Word.Application were both unregistered on that runner. This is an actual E3 environment gap, not a waived gate.

Review adds a borrowed-view re-probe after selection capture and around snapshot reads, preserving NoEffect=false when an identity error occurs after a possible mutation.
The opt-in native marker helper/runbook reads only exact user-supplied small test resources; it never starts/closes Office, never modifies documents, and never automatically awards E3.
Its fake backend tests prove harness logic only. Native view enumeration/marker/reopen/modal behavior remains AWAITING_ENVIRONMENT until the full native matrix is executed.

## Resumed cache-lifetime review (awaiting repaired-source validation)

Downloaded run 35730353508 evidence confirms code 0827e036264bd4884f17a778e36b63b15d10f35c: AR-020 26/26 x3, AR-012 44/44, AR-011 25/25, AR-010 11/11, AR-001 13/13 and 74/74 independent Agent suites. Windows registration checks found neither Excel.Application nor Word.Application; E3 remains NOT_RUN. PR runs triggered by the bot are separately action_required, not PASS.

Review found that the per-scan view cap did not bound references retained across repeated targeted captures of different roots. A shared helper-lifetime bound now rejects over-capacity probes before changing existing bindings and disposes temporary leases; replacing the same root is not double-counted. Full discovery may reclaim closed roots. An exhausted capture helper is retired without touching Office, retrying the request or silently rebinding an old session; a later explicit capture creates a new helper. Two registered regressions exercise this lifecycle with controlled native-object leases. The manual native probe wrapper's post-kill wait is also bounded at two seconds.

Normal AR-020 validation is now read-only; source changes are committed atomically through the authorized connector, not an in-CI patch writer. No human-approval gate is disabled. New regression results must be inspected on their own exact SHA. AR-020 cannot be DONE until real native E3 marker/multi-instance/view tests pass.

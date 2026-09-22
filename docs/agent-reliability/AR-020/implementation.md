# AR-020 — native-window Office discovery and targeted capture

Status: ACTIVE / AWAITING_ENVIRONMENT. Application tests NOT_RUN until exact CI evidence is reviewed.

The existing OfficeHost now enumerates current-desktop XLMAIN/OpusApp roots and EXCEL7/_WwG native panes via OBJID_NATIVEOM, validates process ID/start/desktop-session and Window.Hwnd/root mapping, and keeps each view distinct. Discovery reads metadata, not workbook cells or Word body/selection text. Unsupported, modal/busy, stale and bounded/incomplete observations are explicit, not an empty successful machine-wide inventory. Hidden/headless/protected-view and unsupported-build coverage is not invented.

A bounded STA-owned catalog retains COM references, deduplicates genuine same-document views, re-probes before use and retires sessions on Save As/close/replacement. Old sessions do not fall back to names, order or GetActiveObject. DocumentId is helper-local, not a portable/restart identity; Save As requires rebind. Captured target HWND/PID/start is used even after H2 takes focus. Capture helper connection is reused but capture data is reobserved, with typed failure and elapsed time. Capture remains 3 seconds; execution limits are not globally raised. Discovery has a separate bounded budget and coverage metrics. COM calls remain dependent on installed Office; helper timeout is the isolation boundary, not a promise to interrupt arbitrary COM in-process.

Native identities pass into the existing production binding and snapshot checks. Word selection capture records range positions, not selected plaintext. Provider/permission and no-effect preflight checks remain authoritative. No engine, parallel state store, new model endpoint or NAS protocol.

E1/E2 tests use injected native-object probes/Office clients plus concrete H2 capture, binding and headless UI projection. They cannot certify native Office. E3 must read DOC-A/B/UNSAVED-ONLY on two native instances and views, including Save As, close/reopen and modal cases; currently NOT_RUN. AR-083 remains DEFERRED_BY_USER and no E5 is claimed.

Sources reviewed: Microsoft Learn AccessibleObjectFromWindow (oleacc.h), Excel Window.Hwnd/ActiveSheet/Selection and Word Window.Hwnd/Document/Selection. Native API documentation is not a compatibility test.

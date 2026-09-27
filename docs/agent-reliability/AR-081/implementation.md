# AR-081 — Agent UI reliability, responsiveness and accessibility

Validated code: `46ec1218d535fa0def938f9393e13e40a705605d`.

## Production UI behavior

AR-081 keeps the approved Global/Project Agent surfaces. It does not create another chat UI or replace the product layout.

- `AgentUiProjector` is a pure projection from host-owned task/progress/recovery/evidence state.
- Typed UI states distinguish queued/running/approval/compaction/job wait/reconnect/reconcile/interrupted/applied-unverified/verified/cancel-requested/completed/blocked/cancelled/failed.
- No model text or hidden reasoning is interpreted as verification or progress state.
- Repeated heartbeat/job-poll events remain durable in the archive but are coalesced only in the rendered activity list.
- Reconnect/reopen reuses the existing turn/event identity and final answer; viewing a thread does not execute tools.
- User up-scroll is preserved and a keyboard-focusable latest-activity action returns to follow mode.
- Approval controls expose meaningful automation names; stale decisions remain visibly stale instead of being hidden as success.
- Task-window state, tooltip and automation name all use the same typed projection.
- Artifact inspector exposes automation names and keeps large spreadsheet previews page-bounded/lazy; Word/PDF controls retain explicit zoom/page actions.

## Validation

Exact validated source: `46ec1218d535fa0def938f9393e13e40a705605d`.

Dedicated AR-081 run `36263139290` / job `108462640014`: SUCCESS.

- AR-081 focused: 6/6
- retained AR-080: 4/4
- retained AR-042: 6/6
- retained AR-041: 6/6
- retained AR-068: 3/3
- retained H2M-132: 1/1
- full H2: 1358/1358
- required Agent suites: 75/75

Full Avalonia CI `36263139306` / job `108462672872`: SUCCESS, including self-contained Windows x64 publish and packaged DesktopHost/OfficeHost startup+IPC.

All 24/24 exact-SHA workflow identities completed SUCCESS. Two first-attempt full-suite timeouts were rerun on the same SHA without product changes:
- AR-052 first attempt timed out in retained AR-030 RC-12; same-SHA rerun SUCCESS.
- AR-042 first attempt timed out in retained AR-066 source-consent fixture; same-SHA rerun SUCCESS.

## Acceptance boundary

E1/E2 are PASS. E4 native interaction is DEFERRED_BY_USER / AWAITING_ENVIRONMENT.

Headless Avalonia tests do not certify:
- native DPI/scaling across monitors,
- Vietnamese/Unicode IME composition,
- real keyboard/focus behavior,
- screen-reader announcements,
- real screenshot/layout interaction,
- native long-running UI liveness with configured model/provider and Office/CAD apps.

No E4 PASS or hidden-chain-of-thought claim is made.

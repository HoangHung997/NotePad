# AR-021 — Excel bounded range/paging implementation

Status: **IMPLEMENTED / E2_PASS / AWAITING_ENVIRONMENT (E3), NOT DONE**  
Validated code: `9d8624c0e1c2491a52b2abed5caaeeb3d5a89ecd`  
Branch: `feature/h2-agent-reliability-ar-000` · PR #3

## Implemented

AR-021 replaces the production read path for Excel range/formula/style/merge/hidden/verify operations with an additive, bounded range contract. The request carries the exact live resource session, worksheet, A1 range, fields, page size, cursor and content version. A page is limited to 512 cells. Values and formulas are read by the page; formatting, merge and hidden-state evidence are only materialized when requested. UsedRange is used for extent metadata, not as permission to snapshot all cells.

Excel discovery and active-sheet/selection projection use metadata without reading the workbook body. The content token is independent of selection/focus. Continuations require the prior content version and are also bound to the normalized requested range, canonical field set and page size; replaying a cursor/version against a different read contract returns `stale_content` instead of silently redirecting or mixing pages.

Native paged reads now attach both `Workbook.SheetChange` and `Workbook.SheetCalculate` COM event sinks. Direct user/external-link cell edits and worksheet recalculation therefore advance the per-session revision, while H2 writes also bump that revision explicitly. If a refreshed catalog binds the same logical session to a replacement workbook COM identity, the old sinks are detached, the revision is reset, and a new tracking generation is minted so pre-rebind continuation tokens cannot remain valid. Multi-page reads fail closed with `content_tracking_unavailable` if Excel events are disabled or either required event sink cannot attach.

Worksheet rename/delete continuation semantics are fail-safe: if the bound sheet disappears before a continuation, the backend returns `stale_content`/no-effect rather than a fresh `sheet_not_found`; the native post-page token also re-reads the actual sheet name to reject a rename racing the page.

Structural consistency policy is intentionally narrower than value/formula paging. Native Excel exposes reliable events for cell changes and recalculation, but not a reliable revision signal for formatting, merged-range or row/column hidden-state edits. Therefore `format`/`merge`/`hidden` requests remain supported only when the requested structural range completes in a single bounded page. A structural request that would require continuation fails before reading with `content_tracking_unavailable`/no-effect instead of claiming an unprovable cross-page snapshot.

Transient native-object acquisition repair: user native screenshots exposed `native_object_unavailable`/`live_resource_required` while Office was open. `OfficeWindowCatalog` retries only no-effect COM object acquisition classified as `provider_busy` or `native_object_unavailable`, using a finite initial/40 ms/100 ms schedule. Non-transient identity/safety errors still fail immediately, and mutations/effects are never replayed by this retry path. The same bounded policy is used when revalidating a native identity.

Bound-session targeted scan repair: once a session has already been validated and retained by the helper, `Require(sessionId)` refreshes that exact known root and may repeat the targeted scan for transient `provider_busy`/`native_object_unavailable` loss. Broad discovery and first capture remain single-scan/no-retry boundaries so a missing enumeration can never invent or guess a live document. An intermediate broader retry (`d0dacf5f...`) was rejected by AR-020/AR-030 and is preserved as a failed attempt.

Exact-window ROT/COM fallback: the user supplied a real H2 failure bundle plus a separate Word integration reference that demonstrated a useful Running Object Table/COM discovery technique. H2 keeps `AccessibleObjectFromWindow(..., OBJID_NATIVEOM)` as the primary path. If a valid observed Office root has no usable `EXCEL7`/`_WwG` child or NativeOM Open fails with only `provider_busy`/`native_object_unavailable`, the helper may enumerate the Windows ROT and inspect Word/Excel `Application.Windows`. A fallback candidate is accepted only when `Window.Hwnd` equals the already-observed top-level HWND and the host independently confirms the same PID, process-start time and desktop session. It never selects by filename, ROT order, `ActiveDocument`/`ActiveWorkbook` or fuzzy title. `stale_resource`, permission and identity failures do not enter this fallback. Controlled E2 regressions cover both Word and Excel exact-window binding plus wrong-HWND/wrong-process-start rejection.

The production OfficeHost implements `IExcelRangeReadClient`/backend support. Older injected test clients remain compatible and can use the historical snapshot path; the packaged production OfficeHost uses the bounded range path.

## Exact evidence

- Dedicated AR-021 workflow `36111369647`, job `107996232046`: **SUCCESS**.
- AR-021 focused **14/14**; OfficeHost **17/17**; retained AR-020 **36/36**, AR-012 **44/44**, AR-001 **13/13**; full H2 **1286/1286**.
- New controlled regressions prove the ROT/COM fallback binds only the exact observed Word or Excel HWND and rejects unrelated HWND/process-start identity instead of choosing the first running Office object.
- Evidence artifact `10853612713`, 36,506 bytes, SHA256 `0cc1a307f3a76ccdba28d9cac0efdbf7f918e74aeba1123079c7516d23bd5384`.
- All **11/11** pull-request workflows and all **22/22** exact-SHA workflows on code `9d8624c0e1c2491a52b2abed5caaeeb3d5a89ecd` succeeded.
- Avalonia CI `36111369556`, job `107995347410`: full H2/Agent/MB/Office/transport gates, self-contained Windows x64 publish and packaged-helper IPC all succeeded.
- Portable artifact `10853806963`, 110,312,844 bytes, SHA256 `d9d0612c75200d7a0dcc7b273721c955bc6c219f77b8a75ebd6af872e8b932c8`.
- Historical intermediate failures remain explicit: `466fddb...` exposed a test-only Path shadowing compile defect fixed by `6a6287a...`; `6a6287a...` then produced **1284/1286** because private dynamic fixture types were inaccessible across the OfficeHost assembly boundary, repaired by `9d8624c...`. No assertion or safety guard was relaxed.

## Remaining acceptance

Native Microsoft Word/Excel E3 is **AWAITING_ENVIRONMENT RETEST**, not PASS. A prior user-machine attempt produced `native_object_unavailable`/`live_resource_required` while Office was open; that is recorded as a native failure observation. The exact-window ROT/COM repair is E2/CI validated but must now be retested on the user's actual Office build. Real Excel must certify bounded reads, direct-edit `SheetChange` delivery, recalculation `SheetCalculate` delivery, event-disabled fail-closed behavior and fresh-process/rebind behavior on the target Office build. Run the synthetic native corpus in [native-acceptance.md](native-acceptance.md); any observed regression stays inside AR-021 until repaired and revalidated.

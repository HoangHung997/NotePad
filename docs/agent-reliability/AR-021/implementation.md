# AR-021 — Excel bounded range/paging implementation

Status: **IMPLEMENTED / E2_PASS / AWAITING_ENVIRONMENT (E3), NOT DONE**  
Validated code: `c00a38790158cbf7cab49a182a0c8597835d8fac`  
Branch: `feature/h2-agent-reliability-ar-000` · PR #3

## Implemented

AR-021 replaces the production read path for Excel range/formula/style/merge/hidden/verify operations with an additive, bounded range contract. The request carries the exact live resource session, worksheet, A1 range, fields, page size, cursor and content version. A page is limited to 512 cells. Values and formulas are read by the page; formatting, merge and hidden-state evidence are only materialized when requested. UsedRange is used for extent metadata, not as permission to snapshot all cells.

Excel discovery and active-sheet/selection projection use metadata without reading the workbook body. The content token is independent of selection/focus. Continuations require the prior content version and are also bound to the normalized requested range, canonical field set and page size; replaying a cursor/version against a different read contract returns `stale_content` instead of silently redirecting or mixing pages.

Native paged reads now attach both `Workbook.SheetChange` and `Workbook.SheetCalculate` COM event sinks. Direct user/external-link cell edits and worksheet recalculation therefore advance the per-session revision, while H2 writes also bump that revision explicitly. If a refreshed catalog binds the same logical session to a replacement workbook COM identity, the old sinks are detached, the revision is reset, and a new tracking generation is minted so pre-rebind continuation tokens cannot remain valid. Multi-page reads fail closed with `content_tracking_unavailable` if Excel events are disabled or either required event sink cannot attach.

Worksheet rename/delete continuation semantics are fail-safe: if the bound sheet disappears before a continuation, the backend returns `stale_content`/no-effect rather than a fresh `sheet_not_found`; the native post-page token also re-reads the actual sheet name to reject a rename racing the page.

Structural consistency policy is intentionally narrower than value/formula paging. Native Excel exposes reliable events for cell changes and recalculation, but not a reliable revision signal for formatting, merged-range or row/column hidden-state edits. Therefore `format`/`merge`/`hidden` requests remain supported only when the requested structural range completes in a single bounded page. A structural request that would require continuation fails before reading with `content_tracking_unavailable`/no-effect instead of claiming an unprovable cross-page snapshot.

Transient native-object acquisition repair: user native screenshots exposed `native_object_unavailable`/`live_resource_required` while Office was open. `OfficeWindowCatalog` retries only no-effect COM object acquisition classified as `provider_busy` or `native_object_unavailable`, using a finite initial/40 ms/100 ms schedule. Non-transient identity/safety errors still fail immediately, and mutations/effects are never replayed by this retry path. The same bounded policy is used when revalidating a native identity.

Bound-session targeted scan repair: once a session has already been validated and retained by the helper, `Require(sessionId)` refreshes that exact known root and may repeat the targeted scan for transient `provider_busy`/`native_object_unavailable` loss. Broad discovery and first capture remain single-scan/no-retry boundaries so a missing enumeration can never invent or guess a live document. An intermediate broader retry (`d0dacf5f...`) was rejected by AR-020/AR-030 and is preserved as a failed attempt.

The production OfficeHost implements `IExcelRangeReadClient`/backend support. Older injected test clients remain compatible and can use the historical snapshot path; the packaged production OfficeHost uses the bounded range path.

## Exact evidence

- Dedicated AR-021 workflow `36094885451`, job `107945931939`: **SUCCESS**.
- AR-021 focused **12/12**; OfficeHost **17/17**; retained AR-020 **36/36**, AR-012 **44/44**, AR-001 **13/13**; full H2 **1284/1284**.
- Bound-session retry regression proves two transient targeted scan losses can recover on the known root, while `stale_resource` is attempted once and first capture/discovery retain their no-retry boundary.
- Evidence artifact `10847666504`, 36,409 bytes, SHA256 `5e4bc2987ec4e32eabe957d6942c911060f5703b06e1681980b162c9d7a5723b`.
- All **11/11** pull-request workflows on exact code SHA `c00a38790158cbf7cab49a182a0c8597835d8fac` succeeded.
- Avalonia CI `36094885377`, job `107944939262`: full H2/Agent/MB/Office/transport gates, self-contained Windows x64 publish and packaged-helper IPC all succeeded.
- Portable artifact `10847831076`, 110,308,752 bytes, SHA256 `9c77abbc0c2c8d278deae90362db96d4e1ae2fb5c6435855e1aa9edd41ba3fe4`.

## Remaining acceptance

Native Microsoft Excel E3 RC-06/07 is **AWAITING_ENVIRONMENT RETEST**, not PASS. A prior user-machine attempt produced `native_object_unavailable`/`live_resource_required` while Office was open; that is recorded as a native failure observation, and the repaired build must now be retested. Real Excel must certify bounded reads, direct-edit `SheetChange` delivery, recalculation `SheetCalculate` delivery, event-disabled fail-closed behavior and fresh-process/rebind behavior on the target Office build. Run the synthetic native corpus in [native-acceptance.md](native-acceptance.md); any observed regression stays inside AR-021 until repaired and revalidated.

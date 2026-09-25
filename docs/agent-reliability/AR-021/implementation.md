# AR-021 — Excel bounded range/paging implementation

Status: **IMPLEMENTED / E2_PASS / AWAITING_ENVIRONMENT (E3), NOT DONE**  
Validated code: `1483743c504f742043e8605ff002f66de8d9abbf`  
Branch: `feature/h2-agent-reliability-ar-000` · PR #3

## Implemented

AR-021 replaces the production read path for Excel range/formula/style/merge/hidden/verify operations with an additive, bounded range contract. The request carries the exact live resource session, worksheet, A1 range, fields, page size, cursor and content version. A page is limited to 512 cells. Values and formulas are read by the page; formatting, merge and hidden-state evidence are only materialized when requested. UsedRange is used for extent metadata, not as permission to snapshot all cells.

Excel discovery and active-sheet/selection projection use metadata without reading the workbook body. The content token is independent of selection/focus. Continuations require the prior content version and are also bound to the normalized requested range, canonical field set and page size; replaying a cursor/version against a different read contract returns `stale_content` instead of silently redirecting or mixing pages.

Native paged reads now attach both `Workbook.SheetChange` and `Workbook.SheetCalculate` COM event sinks. Direct user/external-link cell edits and worksheet recalculation therefore advance the per-session revision, while H2 writes also bump that revision explicitly. If a refreshed catalog binds the same logical session to a replacement workbook COM identity, the old sinks are detached, the revision is reset, and a new tracking generation is minted so pre-rebind continuation tokens cannot remain valid. Multi-page reads fail closed with `content_tracking_unavailable` if Excel events are disabled or either required event sink cannot attach.

Worksheet rename/delete continuation semantics are fail-safe: if the bound sheet disappears before a continuation, the backend returns `stale_content`/no-effect rather than a fresh `sheet_not_found`; the native post-page token also re-reads the actual sheet name to reject a rename racing the page.

Structural consistency policy is intentionally narrower than value/formula paging. Native Excel exposes reliable events for cell changes and recalculation, but not a reliable revision signal for formatting, merged-range or row/column hidden-state edits. Therefore `format`/`merge`/`hidden` requests remain supported only when the requested structural range completes in a single bounded page. A structural request that would require continuation fails before reading with `content_tracking_unavailable`/no-effect instead of claiming an unprovable cross-page snapshot.

The production OfficeHost implements `IExcelRangeReadClient`/backend support. Older injected test clients remain compatible and can use the historical snapshot path; the packaged production OfficeHost uses the bounded range path.

## Exact evidence

- Dedicated AR-021 workflow `36078139948`, job `107894641974`: **SUCCESS**.
- AR-021 focused **10/10**; OfficeHost **17/17** including named-pipe paging and bounded single-page structural evidence; retained AR-020 **36/36**, AR-012 **44/44**, AR-001 **13/13**; full H2 **1282/1282**.
- Structural paging regression proves format/merge/hidden requests requiring continuation fail closed with `content_tracking_unavailable`, while bounded one-page structural reads remain supported.
- Evidence artifact `10841121673`, 36,273 bytes, SHA256 `070e4be37ccecd830063697dfa38838c9256fd5d2e87b37ac33f36da2632ecef`.
- All **11/11** pull-request workflows on exact code SHA `1483743c504f742043e8605ff002f66de8d9abbf` succeeded.
- Avalonia CI `36078139952`, job `107893870274`: full H2/Agent/MB/Office/transport gates, self-contained Windows x64 publish and packaged-helper IPC all succeeded.
- Portable artifact `10841461195`, 110,307,986 bytes, SHA256 `6f80ff52a2e108459bba517ff70b6070fd41b38243953dd04872f2ca20628a54`.

## Remaining acceptance

Native Microsoft Excel E3 RC-06/07 is **AWAITING_ENVIRONMENT**, not PASS. Real Excel must certify bounded reads, direct-edit `SheetChange` delivery, recalculation `SheetCalculate` delivery, event-disabled fail-closed behavior and fresh-process/rebind behavior on the target Office build. Run the synthetic native corpus in [native-acceptance.md](native-acceptance.md); any observed regression stays inside AR-021 until repaired and revalidated.

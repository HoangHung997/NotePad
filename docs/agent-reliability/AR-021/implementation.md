# AR-021 — Excel bounded range/paging implementation

Status: **IMPLEMENTED / E2_PASS / AWAITING_ENVIRONMENT (E3), NOT DONE**  
Validated code: `f0de37c8fa4ee49c54cd0044e70dd84d8cc6662f`  
Branch: `feature/h2-agent-reliability-ar-000` · PR #3

## Implemented

AR-021 replaces the production read path for Excel range/formula/style/merge/hidden/verify operations with an additive, bounded range contract. The request carries the exact live resource session, worksheet, A1 range, fields, page size, cursor and content version. A page is limited to 512 cells. Values and formulas are read by the page; formatting, merge and hidden-state evidence are only materialized when requested. UsedRange is used for extent metadata, not as permission to snapshot all cells.

Excel discovery and active-sheet/selection projection use metadata without reading the workbook body. The content token is independent of selection/focus. Continuations require the prior content version and are also bound to the normalized requested range, canonical field set and page size; replaying a cursor/version against a different read contract returns `stale_content` instead of silently redirecting or mixing pages.

Native paged reads now attach both `Workbook.SheetChange` and `Workbook.SheetCalculate` COM event sinks. Direct user/external-link cell edits and worksheet recalculation therefore advance the per-session revision, while H2 writes also bump that revision explicitly. If a refreshed catalog binds the same logical session to a replacement workbook COM identity, the old sinks are detached, the revision is reset, and a new tracking generation is minted so pre-rebind continuation tokens cannot remain valid. Multi-page reads fail closed with `content_tracking_unavailable` if Excel events are disabled or either required event sink cannot attach.

The production OfficeHost implements `IExcelRangeReadClient`/backend support. Older injected test clients remain compatible and can use the historical snapshot path; the packaged production OfficeHost uses the bounded range path.

## Exact evidence

- Dedicated AR-021 workflow `36003582856`, job `107645945118`: **SUCCESS**.
- AR-021 focused **7/7**; OfficeHost **17/17** including named-pipe paging; retained AR-020 **36/36**, AR-012 **44/44**, AR-001 **13/13**; full H2 **1279/1279**.
- Direct-edit stale-content E2 starts with the fixture workbook already unsaved, then edits another cell between pages; invalidation therefore depends on the content revision rather than merely on a `Saved` true→false transition.
- Recalculation stale-content E2 also starts already unsaved, then recalculates between pages and requires the old continuation to fail as `stale_content`.
- Evidence artifact `10810237920`, 36,122 bytes, SHA256 `45595fb9d8785591520fe7656f74fa02e09f5cc1642488d894c36b92217dae1f`.
- All **11/11** pull-request workflows on exact code SHA `228403a47e53632ce00ae6bd6f7614b869fd8450` succeeded.
- Avalonia CI `36003582843`, job `107645944504`: full H2/Agent/MB/Office/transport gates, self-contained Windows x64 publish and packaged-helper IPC all succeeded.
- Portable artifact `10809482318`, 110,306,705 bytes, SHA256 `c8a2a584ca864dce774fda94472e4bdef564721fbab2a5aba3f28e2b8c61705f`.

## Remaining acceptance

Native Microsoft Excel E3 RC-06/07 is **AWAITING_ENVIRONMENT**, not PASS. Real Excel must certify bounded reads, direct-edit `SheetChange` delivery, recalculation `SheetCalculate` delivery, event-disabled fail-closed behavior and fresh-process/rebind behavior on the target Office build. Run the synthetic native corpus in [native-acceptance.md](native-acceptance.md); any observed regression stays inside AR-021 until repaired and revalidated.

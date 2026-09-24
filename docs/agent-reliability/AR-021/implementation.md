# AR-021 — Excel bounded range/paging implementation

Status: **IMPLEMENTED / E2_PASS / AWAITING_ENVIRONMENT (E3), NOT DONE**  
Validated code: `ad843b075bccd0f5967464ec380717d9fe7dfeea`  
Branch: `feature/h2-agent-reliability-ar-000` · PR #3

## Implemented

AR-021 replaces the production read path for Excel range/formula/style/merge/hidden/verify operations with an additive, bounded range contract. The request carries the exact live resource session, worksheet, A1 range, fields, page size, cursor and content version. A page is limited to 512 cells. Values and formulas are read by the page; formatting, merge and hidden-state evidence are only materialized when requested. UsedRange is used for extent metadata, not as permission to snapshot all cells.

Excel discovery and active-sheet/selection projection now use metadata without reading the workbook body. The content token is independent of selection/focus. Continuations require the prior content version and return `stale_content` instead of silently mixing pages when the observed content generation changes. Native paged reads now attach a `Workbook.SheetChange` COM event sink, advancing a per-session revision for direct user/external-link edits as well as H2 writes. Multi-page reads fail closed with `content_tracking_unavailable` if Excel events are disabled or the event sink cannot attach.

The production OfficeHost implements `IExcelRangeReadClient`/backend support. Older injected test clients remain compatible and can use the historical snapshot path; the packaged production OfficeHost uses the bounded range path.

## Exact evidence

- Dedicated AR-021 workflow `35990959399`, job `107604651591`: SUCCESS.
- AR-021 focused **6/6**; OfficeHost **17/17** including named-pipe paging; retained AR-020 **36/36**, AR-012 **44/44**, AR-001 **13/13**; full H2 **1278/1278**.
- The stale-content E2 regression begins with the fixture workbook already unsaved, then edits another cell between pages; continuation invalidation therefore depends on the content revision rather than merely on a `Saved` true→false transition.
- Evidence artifact `10803594991`, 36,081 bytes, SHA256 `8b697703c2c7db86aac3fe6d5dbf45899d09624743fd6fdfc7f5f5d348cfad0e`.
- All **11/11** pull-request workflows on the exact code SHA succeeded.
- Avalonia CI `35990959453`, job `107608667783`: full H2/Agent/MB/Office/transport gates, self-contained Windows x64 publish and packaged-helper IPC all succeeded.
- Portable artifact `10804717701`, 110,306,213 bytes, SHA256 `a6fbcbfd977f98853180fc85a6e37f5731bd01f3db46b05dcc4a5ae74d26a080`. Downloaded copy was independently CRC-tested and hashed; 482 ZIP entries.

## Remaining acceptance

Native Microsoft Excel E3 RC-06/07 is **AWAITING_ENVIRONMENT**, not PASS. The production COM path now subscribes to `Workbook.SheetChange`, and E2 proves the revision semantics even when the workbook is already unsaved; only a real Excel build can certify event delivery/reconnection for the target environment. Run the synthetic native corpus in [native-acceptance.md](native-acceptance.md); any observed regression stays inside AR-021 until repaired and revalidated.

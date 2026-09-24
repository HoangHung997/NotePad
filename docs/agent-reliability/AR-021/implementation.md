# AR-021 — Excel bounded range/paging implementation

Status: **IMPLEMENTED / E2_PASS / AWAITING_ENVIRONMENT (E3), NOT DONE**  
Validated code: `e7dba71f76e36fc73aee7509e8f39416b8f7296a`  
Branch: `feature/h2-agent-reliability-ar-000` · PR #3

## Implemented

AR-021 replaces the production read path for Excel range/formula/style/merge/hidden/verify operations with an additive, bounded range contract. The request carries the exact live resource session, worksheet, A1 range, fields, page size, cursor and content version. A page is limited to 512 cells. Values and formulas are read by the page; formatting, merge and hidden-state evidence are only materialized when requested. UsedRange is used for extent metadata, not as permission to snapshot all cells.

Excel discovery and active-sheet/selection projection now use metadata without reading the workbook body. The content token is independent of selection/focus. Continuations require the prior content version and return `stale_content` instead of silently mixing pages when the observed content generation changes.

The production OfficeHost implements `IExcelRangeReadClient`/backend support. Older injected test clients remain compatible and can use the historical snapshot path; the packaged production OfficeHost uses the bounded range path.

## Exact evidence

- Dedicated AR-021 workflow `35986970929`, job `107591824435`: SUCCESS.
- AR-021 focused **6/6**; OfficeHost **17/17** including named-pipe paging; retained AR-020 **36/36**, AR-012 **44/44**, AR-001 **13/13**; full H2 **1278/1278**.
- Evidence artifact `10802524268`, 36,069 bytes, SHA256 `89e6d3e610f66438a96ab11b2ec8098b017aaaf83f0e92c17f04db1ddadfc08a`.
- All **11/11** pull-request workflows on the exact code SHA succeeded.
- Avalonia CI `35986970938`, job `107591824824`: full H2/Agent/MB/Office/transport gates, self-contained Windows x64 publish and packaged-helper IPC all succeeded.
- Portable artifact `10803285991`, 110,304,466 bytes, SHA256 `1455b8659fe440c37e3198b4d52cd78b226c723036bf8c3c5d0da3d98c82ec44`. Downloaded copy was independently CRC-tested and hashed; 482 ZIP entries.

## Remaining acceptance

Native Microsoft Excel E3 RC-06/07 is **AWAITING_ENVIRONMENT**, not PASS. The E2 fixture proves paging/content-version behavior for host-observed changes but does not certify every external edit signal on a real Excel build. Run the synthetic native corpus in [native-acceptance.md](native-acceptance.md); any observed regression stays inside AR-021 until repaired and revalidated.

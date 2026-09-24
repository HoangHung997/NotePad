# AR-021 native Excel acceptance — RC-06 / RC-07

This is the remaining **E3** gate. Use an isolated Windows test profile and synthetic workbooks only. Do not use personal or production documents.

## Preconditions

- Run the portable build produced from code `ad843b075bccd0f5967464ec380717d9fe7dfeea`.
- Microsoft Excel is installed and can be discovered by H2 OfficeHost.
- Create a synthetic workbook with: more than 5,000 populated cells; at least two sheets; formulas; one merged range; hidden row and column; and a deliberately sparse UsedRange extending far beyond the small range used for the first read.
- Keep API/model credentials out of the workbook and evidence. A model is not required for the E3 helper/provider boundary.

## Required checks

1. Bind the intended live workbook and read a small range such as `Data!A1:D20`. Confirm returned cells and metrics are bounded to that range, not the whole UsedRange.
2. Read a range larger than one page and follow `nextCursor` with the same `contentVersion` until complete. Confirm no duplicates/gaps and each page has at most 512 cells.
3. Request formulas only, then formatting/merge/hidden evidence on demand. Confirm unrequested heavy fields are not materialized.
4. Change only selection/focus between pages. The continuation must remain valid and must not redirect to the new selection.
5. Put the workbook into an already-unsaved state, read page 1, then directly edit another workbook cell in Excel before requesting page 2. The old continuation must be rejected as `stale_content` through the native content revision; H2 must not concatenate old/new pages. Also confirm that disabling Excel events makes a multi-page read fail closed with `content_tracking_unavailable` rather than silently trusting a weak token.
6. Repeat the boundary at least three independent times where applicable, including a fresh OfficeHost process.
7. Record Excel build/version, exact H2 code/build SHA, session/resource identity, page metrics, result and any failure. Do not call fixture evidence E3.

## Pass condition

RC-06/07 pass only when the real Excel provider reads the exact requested bounded ranges, cursor/version behavior is consistent, selection-only changes do not invalidate content, real content changes do invalidate/restart safely, and no unrelated workbook is read or mutated.

If a case fails, keep AR-021 active, save the failure evidence, repair on the same branch, and rerun focused + full CI before moving to AR-022.

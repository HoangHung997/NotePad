# AR-021 native Excel acceptance — RC-06 / RC-07

This is the remaining **E3** gate. Use an isolated Windows test profile and synthetic workbooks only. Do not use personal or production documents.

## Preconditions

- Run the portable build produced from code `385e88644e7f026d095b22be4cbc6562ecd848e4` or a later docs-only checkpoint whose runtime tree contains that code.nly checkpoint whose runtime tree contains that code.
- Microsoft Excel is installed and can be discovered by H2 OfficeHost.
- Create a synthetic workbook with: more than 5,000 populated cells; at least two sheets; formulas (including at least one formula whose displayed value can change on recalculation); one merged range; hidden row and column; and a deliberately sparse UsedRange extending far beyond the small range used for the first read.
- Keep API/model credentials out of the workbook and evidence. A model is not required for the E3 helper/provider boundary.

## Required checks

1. Bind the intended live workbook and read a small range such as `Data!A1:D20`. Confirm returned cells and metrics are bounded to that range, not the whole UsedRange.
2. Read a range larger than one page and follow `nextCursor` with the same `contentVersion` until complete. Confirm no duplicates/gaps and each page has at most 512 cells.
3. Request formulas only, then formatting/merge/hidden evidence on demand. Confirm unrequested heavy fields are not materialized.
4. Change only selection/focus between pages. The continuation must remain valid and must not redirect to the new selection.
5. Put the workbook into an already-unsaved state, read page 1, then directly edit another workbook cell in Excel before requesting page 2. The old continuation must be rejected as `stale_content` through native `Workbook.SheetChange`; H2 must not concatenate old/new pages.
6. With a multi-page read active, trigger a real worksheet recalculation that changes or can change a displayed formula value. The old continuation must be rejected as `stale_content` through native `Workbook.SheetCalculate`.
7. Reuse page 1's cursor/contentVersion while deliberately changing exactly one item at a time: requested range, normalized field set, or page size. Every replay must fail `stale_content`/no-effect; it must never redirect the continuation to the changed read contract.
8. Rename the target worksheet between pages and confirm the old continuation returns `stale_content`/no-effect rather than a fresh `sheet_not_found`. Also try the renamed sheet name with the old cursor/contentVersion and confirm the continuation cannot cross the rename. If practical, delete/recreate the synthetic sheet and verify the same restart requirement.
9. Temporarily disable Excel events in the synthetic test and confirm a multi-page read fails closed with `content_tracking_unavailable` rather than silently trusting a weak token. Re-enable events before continuing unrelated checks.
10. Close/reopen the synthetic workbook or otherwise force a fresh OfficeHost/catalog binding where practical; confirm a prior continuation cannot be reused against a replacement workbook binding. Also repeat the core boundary checks with a fresh OfficeHost process.
11. Repeat the key direct-edit, recalculation, and continuation-contract boundaries at least three independent times where applicable.
12. Record Excel build/version, exact H2 code/build SHA, session/resource identity, page metrics, result and any failure. Do not call fixture evidence E3.

## Pass condition

RC-06/07 pass only when the real Excel provider reads the exact requested bounded ranges, cursor/version behavior is consistent and bound to the original range/fields/page size, selection-only changes do not invalidate content, direct edits and recalculation invalidate/restart safely, disabled event tracking fails closed, worksheet renames/deletes require restart, replacement bindings cannot reuse old continuations, and no unrelated workbook is read or mutated.

If a case fails, keep AR-021 active, save the failure evidence, repair on the same branch, and rerun focused + full CI before moving to AR-022.

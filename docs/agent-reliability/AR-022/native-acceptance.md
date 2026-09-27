# AR-022 native Excel acceptance — deferred

Use only the final full build from artifact **10896344953**, source SHA `df0ae549d4146fd667fc3e3445eb5a06cc096d0b`.

This acceptance is deferred by the user. It must not be recorded as PASS until it is run on an authorized Windows machine with real Excel.

Use a disposable workbook, not a personal or production document.

1. Create/open a disposable workbook with worksheet `Data` and simple values/formulas.
2. Through the real H2 Work Assistant/Agent, observe the exact live workbook/session and obtain the current content token.
3. Write two or more valid cells in one batch; confirm exact target values and unrelated cells/structure are preserved.
4. Move Excel selection/focus only, then write using the still-current content token. The target must remain the same and the write must not be rejected only because selection moved.
5. Edit workbook content manually, then try the old content token. H2 must reject it as stale before another write.
6. Protect the sheet/cell or choose a merged-range non-anchor in the disposable workbook. H2 must reject before effect.
7. Change a formula dependency, run scoped recalculation on the formula cell/range, and verify the returned calculated value, not only the formula string.
8. If a real Office/COM interruption or lost response occurs after dispatch, H2 must report PartiallyApplied or OutcomeUnknown and must not blindly replay the batch.

For any failure capture: exact user prompt, permission preset, workbook/sheet/range, source/build SHA, visible H2 error/progress, and whether the workbook had zero/partial/full requested changes. Reopen AR-022 for repair; do not convert the failure into another task.

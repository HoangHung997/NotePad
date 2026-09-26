# AR-023 — Word bounded paging and structure-preserving mutation

Validated code: `c87778aa972d10a83a34b046ebf06fd5e5e34eed`.

Implemented:
- bounded paragraph/range/table pages with ContentVersion, cursor and completeness;
- stale continuation rejection after real content changes;
- selection-only movement does not stale content identity;
- lazy formatting runs;
- structured table rows/cells and explicit structural markers for fields/tables/embedded content;
- Word mutation schema can bind paragraph indexes to ContentVersion while exact legacy StateToken compatibility remains;
- existing multiline CV mapping, mixed-format rejection and preservation readback remain active.

Repair history:
- `bbd07f15...`: five C# target-typing compile failures;
- `6672cfbf...`: compile fixed, then test corpus requested 9,000 chars from a shorter fixture;
- `c87778aa...`: fixture length corrected only; runtime limits unchanged.

Validation:
- AR-023 9/9;
- OfficeHost 19/19;
- retained AR-022 8/8, AR-021 14/14, AR-020 36/36, AR-012 44/44, AR-001 13/13;
- full H2 1309/1309;
- 75/75 required Agent suites;
- all 15/15 workflows SUCCESS;
- full Avalonia CI publish and packaged-helper IPC PASS.

Native Word E3 is DEFERRED_BY_USER, not PASS. Layout is not certified by text equality or fixture tests.

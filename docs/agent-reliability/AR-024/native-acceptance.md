# AR-024 E4 native/model acceptance — deferred

Use final portable artifact **10899446974**, source SHA `4e6b4a07f4103edd654b680ca9af7f485f8b04d8`.

This acceptance is deferred by the user and must not be recorded as PASS until it is run on an authorized Windows PC with a configured allowed model and real Office.

Use disposable Word/Excel documents only.

## Global Excel

1. Open a disposable Excel workbook and expose a small known range.
2. In Global Work Assistant, test the exact currently captured workbook and also an explicitly named exact path.
3. Verify ObserveOnly blocks mutation; AskBeforeChanges asks; FullAccess remains task-local and does not select a different target.
4. Read a bounded range, perform one bounded mutation, and verify the exact cell plus preservation of unrelated cells/structure.
5. Change selection only and confirm content identity rules; change workbook content manually and confirm stale mutation is rejected.
6. Open a near-name workbook and confirm ambiguity/wrong-target protection remains fail-closed.

## Project Word

1. Link a disposable Word document to a project and use Project Agent.
2. Read bounded paragraph/range/table content from the linked document.
3. Mutate a plain paragraph with the observed ContentVersion and verify preservation of following paragraphs, tables, sections, headers and footers.
4. Test an unsaved document as a live resource; do not silently substitute a disk copy.
5. Change content after observation and verify stale continuation/write rejection.
6. Mixed-format/unsupported structured mutation must fail before effect.

## Required evidence

Capture build SHA, model/provider identity without secrets, UI entry mode (Global or Project), permission preset, exact resource/session/path, tool outcomes, verification evidence, final task status and before/after document state.

Fixture OfficeHost or scripted model transport is E2 only. E4 requires the real configured model and real native Office provider.

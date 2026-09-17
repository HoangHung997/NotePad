---
name: spreadsheets
description: Create, edit, analyze and verify XLSX workbooks, ranges, formulas and formatting. Use for arbitrary spreadsheet file requests, not live Excel UI control.
---

# Spreadsheet work

Adapted for Lab from the installed OpenAI Codex spreadsheets skill. The original snapshot and provenance are under skill-sources; this is not the Codex artifact runtime.

Use `run_python` with openpyxl for workbook edits, Python for transformations, and `inspect_artifact`/another Python run for verification. Read `references/runtime.md` before the first script. Read `references/workbook-checks.md` for existing workbooks or formulas. Choose the simplest correct method for the actual request; there is no fixed sequence of cell operations or one helper per request.

Inspect the actual workbook before assuming sheet names, headers, range boundaries or data types. User scope and reference formatting take precedence over defaults. Preserve unrelated sheets, cells, formulas and styles. Detect unsupported objects/macros/links before choosing a library; refuse or request an appropriate adapter rather than silently flattening them.

Generate the code needed for the request. Use data-driven criteria and header maps instead of guessing column positions. When only changing a font property, copy the existing style component and change that property, not every font setting. Avoid reconstructing an existing workbook through a data frame just to edit a range.

Keep calculated output as formulas when the user expects recalculation. openpyxl writes formulas but does not calculate them: distinguish formula text, cached values and verified calculated results. Do not report recalculation unless a capable engine actually ran.

Verify independently: reopen the result, compare edited values/styles/formulas with the requested intent, and compare representative untouched regions with the original. Use assertions, not only printed success. For visuals, generate a preview if an appropriate renderer is available; a custom range image is only a partial view, not an Excel rendering certificate. State what could not be verified. Publish only the intended result after verification.

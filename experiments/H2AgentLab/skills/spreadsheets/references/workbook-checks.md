# Checks for an existing workbook

- Load with `data_only=False` when editing formulas. Separately reading `data_only=True` does not calculate formulas; caches can be stale or absent.
- Record sheet names, used range, merged ranges, tables, hidden rows/columns/sheets, validations, conditional formatting, external links and protection as relevant.
- Confirm the exact requested range. Do not reformat the entire used range for a request about one column or cell.
- Preserve number formats and data types. A string that looks like a date or number is not automatically safe to convert.
- Prefer `copy.copy(cell.font)` and explicit property changes when preserving other formatting. Conditional formatting may override static fills in Excel.
- Check formulas relative to destination rows/columns and expected dependencies. Verify boundary cases, empty cells, zero denominators and totals when relevant.
- Compare unaffected cells before/after by value and style properties. Row/column insertion can affect formula references and objects; do not assume openpyxl updates all dependencies.
- Save to `output/`, reopen, assert changes, and report limitations. Existing Excel app sessions and macro-enabled files require a separate compatible adapter; Lab's publication currently supports .xlsx, not .xlsm.

# Selected NeraSpreadSheet source

Source: https://github.com/HoangHung997/NeraSpreadSheet
Revision: e169248ae7adf832745b75e6dae73883cf38e7e3 (main)
Owner: HoangHung997. Reused at the repository owner's request for H2 Notes.

These four files are imported without implementation changes. Paths correspond to
`src/NeraSpreadSheet.Scrolling/{ContinuousScrollController,ScrollContracts}.cs`
and `src/NeraSpreadSheet.DataGrid.Core/{GridColumnDefinition,GridSelection}.cs`.
No independent upstream license grant is asserted.

The H2 Notes Avalonia grid adapts the SDK's visible-viewport rendering and single
editor architecture to project records. It intentionally does NOT include Nera's
spreadsheet workbook/formula/Ribbon/graphics backend dependency graph. This is a
DataGrid/spreadsheet hybrid, not the full Nera spreadsheet control.

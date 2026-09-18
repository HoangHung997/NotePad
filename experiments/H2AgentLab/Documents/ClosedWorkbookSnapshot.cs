using DocumentFormat.OpenXml.Packaging;
using H2Notes.Core;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace H2AgentLab.Documents;

public sealed record ClosedWorkbookSnapshot(
    string Sha256,
    IReadOnlyList<ClosedWorksheetSnapshot> Sheets);

public sealed record ClosedWorksheetSnapshot(
    string Name,
    string State,
    IReadOnlyList<ClosedCellSnapshot> Cells,
    IReadOnlyList<string> MergedRanges,
    IReadOnlyList<uint> HiddenRows,
    IReadOnlyList<string> HiddenColumns);

public sealed record ClosedCellSnapshot(
    string Address,
    string RawValue,
    string? Formula,
    string CellType,
    uint StyleIndex,
    bool Bold,
    bool Italic,
    string? FillPattern,
    string? FillForeground,
    uint NumberFormatId,
    string? NumberFormatCode,
    string? HorizontalAlignment,
    string? VerticalAlignment);

/// <summary>
/// Deterministic read-only snapshot of a closed XLSX. H2 Core AiDocuments owns file safety/size/
/// macro/XML validation before OpenXML inspection. Snapshot content is sorted/stable and contains
/// only verifier-relevant workbook state.
/// </summary>
public sealed class ClosedWorkbookSnapshotReader
{
    public ClosedWorkbookSnapshot Read(string name, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(bytes);

        var attachment = AiDocuments.Read(name, bytes);
        if (!string.Equals(
                attachment.MimeType,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                StringComparison.Ordinal))
            throw new InvalidDataException("Closed workbook snapshot requires .xlsx input.");

        using var stream = new MemoryStream(bytes, writable: false);
        using var document = SpreadsheetDocument.Open(stream, false, new OpenSettings
        {
            AutoSave = false,
            MaxCharactersInPart = 40 * 1024 * 1024
        });

        var main = document.WorkbookPart
            ?? throw new InvalidDataException("Excel workbook part is missing.");
        var workbook = main.Workbook
            ?? throw new InvalidDataException("Excel workbook is missing.");

        var sharedStrings = main.SharedStringTablePart?.SharedStringTable?
            .Elements<S.SharedStringItem>()
            .Select(x => x.InnerText)
            .ToArray() ?? [];

        var stylesheet = main.WorkbookStylesPart?.Stylesheet;
        var cellFormats = stylesheet?.CellFormats?.Elements<S.CellFormat>().ToArray() ?? [];
        var fonts = stylesheet?.Fonts?.Elements<S.Font>().ToArray() ?? [];
        var fills = stylesheet?.Fills?.Elements<S.Fill>().ToArray() ?? [];
        var numberFormats = stylesheet?.NumberingFormats?
            .Elements<S.NumberingFormat>()
            .Where(x => x.NumberFormatId?.Value is not null)
            .ToDictionary(
                x => x.NumberFormatId!.Value,
                x => x.FormatCode?.Value,
                EqualityComparer<uint>.Default)
            ?? new Dictionary<uint, string?>();

        var sheets = new List<ClosedWorksheetSnapshot>();
        foreach (var sheet in workbook.Sheets?.Elements<S.Sheet>() ?? [])
        {
            var nameValue = sheet.Name?.Value
                ?? throw new InvalidDataException("Excel sheet is missing a name.");
            var state = NormalizeSheetState(sheet.State?.Value);
            if (sheet.Id?.Value is not { Length: > 0 } relationshipId
                || main.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
            {
                sheets.Add(new ClosedWorksheetSnapshot(
                    nameValue,
                    state,
                    [],
                    [],
                    [],
                    []));
                continue;
            }

            var worksheet = worksheetPart.Worksheet
                ?? throw new InvalidDataException($"Worksheet '{nameValue}' is missing content.");

            var cells = worksheet.Descendants<S.Cell>()
                .Where(x => x.CellReference?.Value is { Length: > 0 })
                .Select(cell => SnapshotCell(
                    cell,
                    sharedStrings,
                    cellFormats,
                    fonts,
                    fills,
                    numberFormats))
                .OrderBy(x => x.Address, StringComparer.Ordinal)
                .ToArray();

            var merges = worksheet.Elements<S.MergeCells>()
                .SelectMany(x => x.Elements<S.MergeCell>())
                .Select(x => x.Reference?.Value)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            var hiddenRows = worksheet.Descendants<S.Row>()
                .Where(x => x.Hidden?.Value == true && x.RowIndex?.Value is not null)
                .Select(x => x.RowIndex!.Value)
                .OrderBy(x => x)
                .ToArray();

            var hiddenColumns = worksheet.Elements<S.Columns>()
                .SelectMany(x => x.Elements<S.Column>())
                .Where(x => x.Hidden?.Value == true && x.Min?.Value is not null && x.Max?.Value is not null)
                .Select(x => x.Min!.Value == x.Max!.Value
                    ? x.Min.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : x.Min.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "-"
                        + x.Max.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            sheets.Add(new ClosedWorksheetSnapshot(
                nameValue,
                state,
                cells,
                merges,
                hiddenRows,
                hiddenColumns));
        }

        return new ClosedWorkbookSnapshot(
            attachment.Sha256.ToLowerInvariant(),
            sheets.ToArray());
    }

    private static string NormalizeSheetState(S.SheetStateValues? value)
    {
        if (value == S.SheetStateValues.Hidden) return "Hidden";
        if (value == S.SheetStateValues.VeryHidden) return "VeryHidden";
        return "Visible";
    }

    private static string? NormalizePattern(S.PatternValues? value)
    {
        if (value is null) return null;
        if (value == S.PatternValues.Solid) return "Solid";
        if (value == S.PatternValues.None) return "None";
        if (value == S.PatternValues.Gray125) return "Gray125";
        if (value == S.PatternValues.DarkGray) return "DarkGray";
        if (value == S.PatternValues.MediumGray) return "MediumGray";
        if (value == S.PatternValues.LightGray) return "LightGray";
        return "Other";
    }

    private static string NormalizeCellType(S.CellValues? value)
    {
        if (value is null) return "NumberOrGeneral";
        if (value == S.CellValues.SharedString) return "SharedString";
        if (value == S.CellValues.InlineString) return "InlineString";
        if (value == S.CellValues.Boolean) return "Boolean";
        if (value == S.CellValues.String) return "String";
        if (value == S.CellValues.Date) return "Date";
        if (value == S.CellValues.Error) return "Error";
        return "NumberOrGeneral";
    }

    private static ClosedCellSnapshot SnapshotCell(
        S.Cell cell,
        IReadOnlyList<string> sharedStrings,
        IReadOnlyList<S.CellFormat> cellFormats,
        IReadOnlyList<S.Font> fonts,
        IReadOnlyList<S.Fill> fills,
        IReadOnlyDictionary<uint, string?> numberFormats)
    {
        var address = cell.CellReference!.Value!;
        var raw = ReadValue(cell, sharedStrings);
        var formula = cell.CellFormula?.Text;
        var cellType = NormalizeCellType(cell.DataType?.Value);
        var styleIndex = cell.StyleIndex?.Value ?? 0U;

        S.CellFormat? format = styleIndex < cellFormats.Count
            ? cellFormats[(int)styleIndex]
            : null;
        var fontId = format?.FontId?.Value ?? 0U;
        var fillId = format?.FillId?.Value ?? 0U;
        var numberFormatId = format?.NumberFormatId?.Value ?? 0U;

        var font = fontId < fonts.Count ? fonts[(int)fontId] : null;
        var fill = fillId < fills.Count ? fills[(int)fillId] : null;
        var pattern = fill?.PatternFill;
        var alignment = format?.Alignment;

        return new ClosedCellSnapshot(
            address,
            raw,
            formula,
            cellType,
            styleIndex,
            On(font?.Bold),
            On(font?.Italic),
            NormalizePattern(pattern?.PatternType?.Value),
            Color(pattern?.ForegroundColor),
            numberFormatId,
            numberFormats.TryGetValue(numberFormatId, out var code) ? code : null,
            alignment?.Horizontal?.Value.ToString(),
            alignment?.Vertical?.Value.ToString());
    }

    private static string ReadValue(S.Cell cell, IReadOnlyList<string> sharedStrings)
    {
        var value = cell.CellValue?.Text ?? "";
        var type = cell.DataType?.Value;
        if (type == S.CellValues.SharedString)
        {
            if (!int.TryParse(
                    value,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var index)
                || index < 0
                || index >= sharedStrings.Count)
                throw new InvalidDataException("Excel shared string index is invalid.");
            return sharedStrings[index];
        }
        if (type == S.CellValues.InlineString)
            return cell.InlineString?.InnerText ?? "";
        if (type == S.CellValues.Boolean)
            return value == "1" ? "TRUE" : "FALSE";
        return value;
    }

    private static bool On(DocumentFormat.OpenXml.Spreadsheet.BooleanPropertyType? value)
        => value is not null && (value.Val?.Value ?? true);

    private static string? Color(S.ForegroundColor? color)
    {
        if (color is null) return null;
        if (color.Rgb?.Value is { Length: > 0 } rgb) return "rgb:" + rgb.ToUpperInvariant();
        if (color.Indexed?.Value is uint indexed) return "indexed:" + indexed;
        if (color.Theme?.Value is uint theme)
            return "theme:" + theme
                + (color.Tint?.Value is double tint
                    ? ":tint:" + tint.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    : "");
        if (color.Auto?.Value == true) return "auto";
        return null;
    }
}

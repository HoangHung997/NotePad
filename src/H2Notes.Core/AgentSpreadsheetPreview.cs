using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace H2Notes.Core;

public sealed record AgentPreviewCell(string Address, string Value, string? Formula);
public sealed record AgentPreviewRow(int Number, IReadOnlyList<AgentPreviewCell> Cells);
public sealed record AgentPreviewSheet(string Name, IReadOnlyList<AgentPreviewRow> Rows, bool HasMore);

/// <summary>Read-only paged preview. Formulas, macros and external links are never executed.</summary>
public static class AgentSpreadsheetPreview
{
    public const int PageSize = 40;
    private static SpreadsheetDocument Open(string path) => SpreadsheetDocument.Open(path, false,
        new OpenSettings { AutoSave = false, MaxCharactersInPart = 16 * 1024 * 1024 });

    public static IReadOnlyList<string> SheetNames(string path)
    {
        if (Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase)) return [Path.GetFileName(path)];
        using var book = Open(path);
        return book.WorkbookPart!.Workbook.Sheets!.Elements<S.Sheet>().Select(s => s.Name?.Value ?? "").ToArray();
    }

    public static AgentPreviewSheet Read(string path, string sheet, int page)
    {
        if (page < 0) throw new ArgumentOutOfRangeException(nameof(page));
        if (Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase)) return ReadCsv(path, page);
        using var book = Open(path); var workbook = book.WorkbookPart!;
        var entry = workbook.Workbook.Sheets!.Elements<S.Sheet>().Single(s => s.Name == sheet);
        var part = (WorksheetPart)workbook.GetPartById(entry.Id!);
        var strings = workbook.SharedStringTablePart?.SharedStringTable?.Elements<S.SharedStringItem>().Select(x => x.InnerText).ToArray() ?? [];
        using var reader = OpenXmlReader.Create(part); var rows = new List<AgentPreviewRow>(); var skipped = 0;
        while (reader.Read())
        {
            if (reader.ElementType != typeof(S.Row) || !reader.IsStartElement) continue;
            // Skip() positions on the next sibling; the loop's Read() would skip that row's start.
            if (skipped++ < page * PageSize) continue;
            if (rows.Count == PageSize) return new(sheet, rows, true);
            var row = (S.Row)reader.LoadCurrentElement();
            var cells = row.Elements<S.Cell>().Take(256).Select(c =>
            {
                var value = c.InlineString?.InnerText ?? c.CellValue?.Text ?? "";
                if (c.DataType?.Value == S.CellValues.SharedString && int.TryParse(value, out var i) && i >= 0 && i < strings.Length) value = strings[i];
                return new AgentPreviewCell(c.CellReference?.Value ?? "", value, c.CellFormula?.Text);
            }).ToArray();
            rows.Add(new((int)(row.RowIndex?.Value ?? (uint)skipped), cells));
        }
        return new(sheet, rows, false);
    }

    private static AgentPreviewSheet ReadCsv(string path, int page)
    {
        using var reader = new StreamReader(path); var rows = new List<AgentPreviewRow>();
        var values = new List<string>(); var value = new StringBuilder(); var quoted = false; var number = 1;
        while (true)
        {
            var n = reader.Read();
            if (n == '"')
            {
                if (quoted && reader.Peek() == '"') { reader.Read(); value.Append('"'); }
                else quoted = !quoted;
            }
            else if (n == ',' && !quoted) { values.Add(value.ToString()); value.Clear(); }
            else if (n < 0 || n == '\n' && !quoted)
            {
                if (n < 0 && values.Count == 0 && value.Length == 0) break;
                values.Add(value.ToString().TrimEnd('\r')); value.Clear();
                if (number > page * PageSize)
                {
                    if (rows.Count == PageSize) return new(Path.GetFileName(path), rows, true);
                    rows.Add(new(number, values.Take(256).Select((v, i) => new AgentPreviewCell(ColumnName(i) + number, v, null)).ToArray()));
                }
                values.Clear(); number++; if (n < 0) break;
            }
            else value.Append((char)n);
            if (value.Length > 1_000_000 || values.Count > 16_384) throw new InvalidDataException("Một hàng CSV vượt giới hạn xem trước.");
        }
        return new(Path.GetFileName(path), rows, false);
    }

    public static string ColumnName(int column)
    {
        var name = "";
        for (var n = column + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }
}

using System.Globalization;

namespace H2AgentLab.OfficeProtocol;

public static class ExcelRangeReadLimits
{
    public const int DefaultPageCells = 256;
    public const int MaxPageCells = 512;

    public static int NormalizePageSize(int pageSize)
    {
        if (pageSize == 0) return DefaultPageCells;
        if (pageSize is < 1 or > MaxPageCells)
            throw new ArgumentOutOfRangeException(nameof(pageSize),
                $"Excel range page_size must be 1..{MaxPageCells}.");
        return pageSize;
    }
}

public static class ExcelRangeReadFields
{
    public const string Value = "value";
    public const string Formula = "formula";
    public const string Format = "format";
    public const string Merge = "merge";
    public const string Hidden = "hidden";

    private static readonly HashSet<string> Supported =
        new(StringComparer.Ordinal) { Value, Formula, Format, Merge, Hidden };

    public static IReadOnlyList<string> Normalize(IReadOnlyList<string>? fields)
    {
        if (fields is null || fields.Count == 0) return [Value, Formula];
        var result = new List<string>();
        foreach (var raw in fields)
        {
            var field = (raw ?? "").Trim().ToLowerInvariant() switch
            {
                "values" => Value,
                "formulas" => Formula,
                "styles" => Format,
                "merges" => Merge,
                "hidden_state" => Hidden,
                var value => value
            };
            if (!Supported.Contains(field))
                throw new ArgumentException(
                    $"Unsupported Excel range field '{raw}'. Supported fields: value, formula, format, merge, hidden.",
                    nameof(fields));
            if (!result.Contains(field, StringComparer.Ordinal)) result.Add(field);
        }
        return result;
    }
}

public readonly record struct ExcelRangeBounds(
    int StartRow,
    int StartColumn,
    int EndRow,
    int EndColumn)
{
    public int RowCount => EndRow - StartRow + 1;
    public int ColumnCount => EndColumn - StartColumn + 1;
    public long CellCount => (long)RowCount * ColumnCount;
    public string Address => ExcelRangeReadRules.RangeAddress(this);
}

public sealed record ExcelRangePagePlan(
    ExcelRangeBounds Bounds,
    string? NextCursor,
    bool Complete)
{
    public int CellCount => checked(Bounds.RowCount * Bounds.ColumnCount);
}

public static class ExcelRangeReadRules
{
    public const int MaxExcelRows = 1_048_576;
    public const int MaxExcelColumns = 16_384;

    public static ExcelRangeBounds ParseRange(string range)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(range);
        var parts = range.Trim().Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2)
            throw new ArgumentException("Excel range must be one A1 cell or one rectangular A1:B2 range.", nameof(range));
        var first = ParseCell(parts[0]);
        var last = parts.Length == 1 ? first : ParseCell(parts[1]);
        if (first.Row > last.Row || first.Column > last.Column)
            throw new ArgumentException("Excel range must run from the top-left cell to the bottom-right cell.", nameof(range));
        return new(first.Row, first.Column, last.Row, last.Column);
    }

    public static ExcelRangePagePlan PlanPage(
        ExcelRangeBounds requested,
        int pageSize,
        string? cursor)
    {
        pageSize = ExcelRangeReadLimits.NormalizePageSize(pageSize);
        var (rowOffset, columnOffset) = DecodeCursor(cursor);
        if (rowOffset < 0 || columnOffset < 0
            || rowOffset >= requested.RowCount
            || columnOffset >= requested.ColumnCount)
            throw new ArgumentException("Excel range cursor is outside the requested range.", nameof(cursor));

        var remainingColumns = requested.ColumnCount - columnOffset;
        int rows;
        int columns;
        if (columnOffset != 0 || requested.ColumnCount > pageSize)
        {
            rows = 1;
            columns = Math.Min(pageSize, remainingColumns);
        }
        else
        {
            columns = requested.ColumnCount;
            rows = Math.Min(
                requested.RowCount - rowOffset,
                Math.Max(1, pageSize / columns));
        }

        var page = new ExcelRangeBounds(
            requested.StartRow + rowOffset,
            requested.StartColumn + columnOffset,
            requested.StartRow + rowOffset + rows - 1,
            requested.StartColumn + columnOffset + columns - 1);

        var nextRow = rowOffset;
        var nextColumn = columnOffset;
        if (rows > 1 || (columnOffset == 0 && columns == requested.ColumnCount))
        {
            nextRow += rows;
            nextColumn = 0;
        }
        else
        {
            nextColumn += columns;
            if (nextColumn >= requested.ColumnCount)
            {
                nextRow++;
                nextColumn = 0;
            }
        }

        var complete = nextRow >= requested.RowCount;
        return new(
            page,
            complete ? null : EncodeCursor(nextRow, nextColumn),
            complete);
    }

    public static string CellAddress(int row, int column)
    {
        if (row is < 1 or > MaxExcelRows) throw new ArgumentOutOfRangeException(nameof(row));
        if (column is < 1 or > MaxExcelColumns) throw new ArgumentOutOfRangeException(nameof(column));
        var value = column;
        Span<char> letters = stackalloc char[3];
        var index = letters.Length;
        while (value > 0)
        {
            value--;
            letters[--index] = (char)('A' + value % 26);
            value /= 26;
        }
        return new string(letters[index..]) + row.ToString(CultureInfo.InvariantCulture);
    }

    public static string RangeAddress(ExcelRangeBounds bounds)
    {
        var first = CellAddress(bounds.StartRow, bounds.StartColumn);
        var last = CellAddress(bounds.EndRow, bounds.EndColumn);
        return first == last ? first : first + ":" + last;
    }

    public static bool Intersects(ExcelRangeBounds left, ExcelRangeBounds right)
        => left.StartRow <= right.EndRow
            && left.EndRow >= right.StartRow
            && left.StartColumn <= right.EndColumn
            && left.EndColumn >= right.StartColumn;

    private static (int Row, int Column) ParseCell(string cell)
    {
        var value = (cell ?? "").Trim().Replace("$", "", StringComparison.Ordinal).ToUpperInvariant();
        if (value.Length is < 2 or > 10 || value.Contains('!') || value.Contains('[') || value.Contains(']'))
            throw new ArgumentException("Excel range uses A1-style cells without sheet prefixes.", nameof(cell));

        var split = 0;
        while (split < value.Length && value[split] is >= 'A' and <= 'Z') split++;
        if (split == 0 || split == value.Length)
            throw new ArgumentException("Excel range uses A1-style cells.", nameof(cell));

        var column = 0;
        for (var i = 0; i < split; i++)
            column = checked(column * 26 + (value[i] - 'A' + 1));
        if (column is < 1 or > MaxExcelColumns)
            throw new ArgumentException("Excel range column is outside Excel limits.", nameof(cell));

        if (!int.TryParse(value.AsSpan(split), NumberStyles.None, CultureInfo.InvariantCulture, out var row)
            || row is < 1 or > MaxExcelRows)
            throw new ArgumentException("Excel range row is outside Excel limits.", nameof(cell));
        return (row, column);
    }

    private static string EncodeCursor(int rowOffset, int columnOffset)
        => rowOffset.ToString(CultureInfo.InvariantCulture) + ":" + columnOffset.ToString(CultureInfo.InvariantCulture);

    private static (int RowOffset, int ColumnOffset) DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return (0, 0);
        var parts = cursor.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var row)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var column))
            throw new ArgumentException("Excel range cursor is invalid.", nameof(cursor));
        return (row, column);
    }
}

public sealed record ExcelReadRangeRequest(
    string SessionId,
    string SheetName,
    string Range,
    IReadOnlyList<string>? Fields = null,
    int PageSize = 0,
    string? Cursor = null,
    string? ContentVersion = null);

public sealed record ExcelRangeCellState(
    string Address,
    string? Value,
    string? Formula,
    bool? Bold,
    bool? Italic,
    long? FillColor,
    string? NumberFormat,
    string? HorizontalAlignment,
    string? VerticalAlignment);

public sealed record ExcelSheetExtent(
    string Address,
    int FirstRow,
    int FirstColumn,
    int LastRow,
    int LastColumn,
    long CellCount);

public sealed record ExcelRangeReadMetrics(
    int CellsRead,
    int PayloadBytes,
    long ElapsedMilliseconds);

public sealed record ExcelRangeReadPage(
    string SessionId,
    string Name,
    string FullName,
    bool Saved,
    string SheetName,
    string RequestedRange,
    string PageRange,
    IReadOnlyList<string> Fields,
    IReadOnlyList<ExcelRangeCellState> Cells,
    IReadOnlyList<string> MergedRanges,
    IReadOnlyList<int> HiddenRows,
    IReadOnlyList<int> HiddenColumns,
    ExcelSheetExtent UsedExtent,
    string ContentVersion,
    string? NextCursor,
    bool Complete,
    string Provenance,
    ExcelRangeReadMetrics Metrics)
{
    public OfficeNativeIdentity? NativeIdentity { get; init; }
}

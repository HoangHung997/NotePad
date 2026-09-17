namespace NeraSpreadSheet.DataGrid.Core;

public readonly record struct GridSelection(long StartRow, long EndRow)
{
    public long FirstRow => Math.Min(StartRow, EndRow);

    public long LastRow => Math.Max(StartRow, EndRow);

    public long RowCount => checked(LastRow - FirstRow + 1);
}

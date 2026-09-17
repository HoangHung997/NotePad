namespace NeraSpreadSheet.DataGrid.Core;

public sealed record GridColumnDefinition
{
    public GridColumnDefinition(
        string key,
        string header,
        Type dataType,
        bool isReadOnly = false,
        double preferredWidth = 120)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("A column key is required.", nameof(key));
        }

        if (string.IsNullOrWhiteSpace(header))
        {
            throw new ArgumentException("A column header is required.", nameof(header));
        }

        if (!double.IsFinite(preferredWidth) || preferredWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(preferredWidth));
        }

        Key = key.Trim();
        Header = header.Trim();
        DataType = dataType ?? throw new ArgumentNullException(nameof(dataType));
        IsReadOnly = isReadOnly;
        PreferredWidth = preferredWidth;
    }

    public string Key { get; }

    public string Header { get; }

    public Type DataType { get; }

    public bool IsReadOnly { get; }

    public double PreferredWidth { get; }
}

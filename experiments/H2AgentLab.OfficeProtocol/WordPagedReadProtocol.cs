using System.Globalization;

namespace H2AgentLab.OfficeProtocol;

public static class WordPagedReadLimits
{
    public const int DefaultParagraphs = 64;
    public const int MaxParagraphs = 256;
    public const int DefaultRangeCharacters = 4096;
    public const int MaxRangeCharacters = 16384;
    public const int DefaultTables = 8;
    public const int MaxTables = 32;
    public const int MaxPageRuns = 4096;
    public const int MaxRequestedRangeCharacters = 2_000_000;

    public static int ParagraphPageSize(int value) => Normalize(value, DefaultParagraphs, MaxParagraphs, "paragraph page_size");
    public static int RangePageSize(int value) => Normalize(value, DefaultRangeCharacters, MaxRangeCharacters, "range page_size");
    public static int TablePageSize(int value) => Normalize(value, DefaultTables, MaxTables, "table page_size");
    private static int Normalize(int value, int fallback, int max, string name)
    {
        if (value == 0) return fallback;
        if (value is < 1 || value > max) throw new ArgumentOutOfRangeException(nameof(value), $"Word {name} must be 1..{max}.");
        return value;
    }
}

public static class WordPagedReadRules
{
    public static int Start(string? cursor, string prefix, int fallback, int minimum, int maximumExclusive)
    {
        var value=fallback;
        if(!string.IsNullOrWhiteSpace(cursor))
        {
            var expected=prefix+":";
            if(!cursor.StartsWith(expected,StringComparison.Ordinal)
                || !int.TryParse(cursor.AsSpan(expected.Length),NumberStyles.None,CultureInfo.InvariantCulture,out value))
                throw new ArgumentException("Word page cursor is invalid.",nameof(cursor));
        }
        if(value<minimum || value>=maximumExclusive)throw new ArgumentException("Word page cursor is outside the requested content.",nameof(cursor));
        return value;
    }
    public static string Cursor(string prefix,int next)=>prefix+":"+next.ToString(CultureInfo.InvariantCulture);
}

public sealed record WordReadMetrics(int ItemsRead,int ItemsScanned,int CharactersRead,int PayloadBytes,long ElapsedMilliseconds);

public sealed record WordParagraphPageItem(
    int Index,
    string Text,
    string Style,
    IReadOnlyList<WordRunState> Runs,
    IReadOnlyList<string> StructuralKinds);

public sealed record WordParagraphReadRequest(
    string SessionId,
    int StartParagraph=0,
    int PageSize=0,
    bool IncludeFormatting=false,
    string? Query=null,
    bool OutlineOnly=false,
    string? Cursor=null,
    string? ContentVersion=null);

public sealed record WordParagraphReadPage(
    string SessionId,
    string Name,
    string FullName,
    bool Saved,
    int TotalParagraphs,
    IReadOnlyList<WordParagraphPageItem> Paragraphs,
    string ContentVersion,
    string? NextCursor,
    bool Complete,
    string Provenance,
    WordReadMetrics Metrics)
{
    public OfficeNativeIdentity? NativeIdentity { get; init; }
}

public sealed record WordRangeReadRequest(
    string SessionId,
    int Start,
    int Length,
    int PageSize=0,
    bool IncludeFormatting=false,
    string? Cursor=null,
    string? ContentVersion=null);

public sealed record WordRangeReadPage(
    string SessionId,
    string Name,
    string FullName,
    bool Saved,
    int RequestedStart,
    int RequestedLength,
    int PageStart,
    int PageLength,
    string Text,
    IReadOnlyList<WordRunState> Runs,
    IReadOnlyList<string> StructuralKinds,
    bool StructuredContentComplete,
    string ContentVersion,
    string? NextCursor,
    bool Complete,
    string Provenance,
    WordReadMetrics Metrics)
{
    public OfficeNativeIdentity? NativeIdentity { get; init; }
}

public sealed record WordTableReadRequest(
    string SessionId,
    int StartTable=0,
    int PageSize=0,
    string? Cursor=null,
    string? ContentVersion=null);

public sealed record WordTableReadPage(
    string SessionId,
    string Name,
    string FullName,
    bool Saved,
    int TotalTables,
    IReadOnlyList<WordTableState> Tables,
    string ContentVersion,
    string? NextCursor,
    bool Complete,
    string Provenance,
    WordReadMetrics Metrics)
{
    public OfficeNativeIdentity? NativeIdentity { get; init; }
}

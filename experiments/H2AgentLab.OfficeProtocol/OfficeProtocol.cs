using System.Text.Json;

namespace H2AgentLab.OfficeProtocol;

public sealed record OfficeRpcRequest(string Id, string Method, JsonElement Parameters);
public sealed record OfficeRpcError(string Code, string Message);
public sealed record OfficeRpcResponse(string Id, bool Ok, JsonElement? Result, OfficeRpcError? Error);

public sealed record OfficePermission(bool Granted);

public sealed record ExcelWorkbookInfo(
    string SessionId,
    string Name,
    string FullName,
    bool Saved,
    string ActiveSheet,
    string SelectionAddress,
    string StateToken);

public sealed record ExcelDiscovery(
    IReadOnlyList<ExcelWorkbookInfo> Workbooks,
    string? ActiveSessionId);

public sealed record ExcelCellState(
    string Address,
    string Value,
    string Formula,
    bool Bold,
    bool Italic,
    long? FillColor,
    string NumberFormat,
    string HorizontalAlignment,
    string VerticalAlignment);

public sealed record ExcelSheetState(
    string Name,
    string Visibility,
    IReadOnlyList<ExcelCellState> Cells,
    IReadOnlyList<string> MergedRanges,
    IReadOnlyList<int> HiddenRows,
    IReadOnlyList<int> HiddenColumns);

public sealed record ExcelLiveSnapshot(
    string SessionId,
    string Name,
    string FullName,
    bool Saved,
    string ActiveSheet,
    string SelectionAddress,
    IReadOnlyList<ExcelSheetState> Sheets,
    string StateToken);

public sealed record ExcelSnapshotRequest(string SessionId);

public sealed record ExcelCellPatch(
    string Address,
    string? Value = null,
    bool ClearValue = false,
    string? Formula = null,
    bool? Bold = null,
    bool? Italic = null,
    long? FillColor = null,
    string? NumberFormat = null);

public sealed record ExcelPatchRequest(
    string SessionId,
    string StateToken,
    bool PermissionGranted,
    string SheetName,
    IReadOnlyList<ExcelCellPatch> Cells);

public sealed record ExcelPatchResult(
    ExcelLiveSnapshot Before,
    ExcelLiveSnapshot After,
    IReadOnlyList<string> ChangedCells);

public sealed record ExcelRecalculateRequest(
    string SessionId,
    string StateToken,
    bool PermissionGranted);

public sealed record OfficeSaveCopyRequest(
    string SessionId,
    string StateToken,
    bool PermissionGranted,
    string DestinationPath);

public sealed record OfficeSaveCopyResult(
    string SessionId,
    string DestinationPath,
    string SourceStateToken,
    string SavedCopySha256);

public sealed record WordDocumentInfo(
    string SessionId,
    string Name,
    string FullName,
    bool Saved,
    int SelectionStart,
    int SelectionEnd,
    string SelectionText,
    string StateToken);

public sealed record WordDiscovery(
    IReadOnlyList<WordDocumentInfo> Documents,
    string? ActiveSessionId);

public sealed record WordRunState(
    int Index,
    string Text,
    string Style,
    bool Bold,
    bool Italic,
    bool Underline);

public sealed record WordParagraphState(
    int Index,
    string Text,
    string Style,
    IReadOnlyList<WordRunState> Runs);

public sealed record WordTableState(
    int Index,
    IReadOnlyList<IReadOnlyList<string>> Rows);

public sealed record WordSectionState(
    int Index,
    float PageWidth,
    float PageHeight,
    float MarginTop,
    float MarginRight,
    float MarginBottom,
    float MarginLeft);

public sealed record WordPartState(
    string Key,
    IReadOnlyList<WordParagraphState> Paragraphs);

public sealed record WordLiveSnapshot(
    string SessionId,
    string Name,
    string FullName,
    bool Saved,
    int SelectionStart,
    int SelectionEnd,
    string SelectionText,
    IReadOnlyList<WordParagraphState> Paragraphs,
    IReadOnlyList<WordTableState> Tables,
    IReadOnlyList<WordSectionState> Sections,
    IReadOnlyList<WordPartState> Headers,
    IReadOnlyList<WordPartState> Footers,
    string StateToken);

public sealed record WordSnapshotRequest(string SessionId);

public sealed record WordParagraphPatch(
    int ParagraphIndex,
    string? Text = null,
    bool? Bold = null,
    bool? Italic = null,
    bool? Underline = null);

public sealed record WordPatchRequest(
    string SessionId,
    string StateToken,
    bool PermissionGranted,
    IReadOnlyList<WordParagraphPatch> Paragraphs);

public sealed record WordPatchResult(
    WordLiveSnapshot Before,
    WordLiveSnapshot After,
    IReadOnlyList<int> ChangedParagraphs);

public sealed record OfficePingResult(
    int ProcessId,
    string ProtocolVersion,
    bool FixtureMode,
    bool StaThread);

public static class OfficeProtocolConstants
{
    public const string Version = "1.0";
    public const int MaxMessageBytes = 4 * 1024 * 1024;
}

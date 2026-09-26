using System.Text.Json;

namespace H2AgentLab.OfficeProtocol;

public sealed record OfficeRpcRequest(string Id, string Method, JsonElement Parameters);
public sealed record OfficeRpcError(string Code, string Message)
{
    public bool NoEffect { get; init; }
}
public sealed record OfficeRpcResponse(string Id, bool Ok, JsonElement? Result, OfficeRpcError? Error);

public sealed record OfficePermission(bool Granted);

public sealed record ExcelWorkbookInfo(
    string SessionId,
    string Name,
    string FullName,
    bool Saved,
    string ActiveSheet,
    string SelectionAddress,
    string StateToken)
{
    public OfficeNativeIdentity? NativeIdentity { get; init; }
}


public sealed record ExcelDiscovery(
    IReadOnlyList<ExcelWorkbookInfo> Workbooks,
    string? ActiveSessionId)
{
    public OfficeDiscoveryReport? Report { get; init; }
}

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
    string StateToken)
{
    public OfficeNativeIdentity? NativeIdentity { get; init; }
    public string? ContentToken { get; init; }
}


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

public sealed record ExcelPatchRequest(string SessionId,string StateToken,bool PermissionGranted,string SheetName,IReadOnlyList<ExcelCellPatch> Cells)
{
    public string? ContentToken { get; init; } public string? LogicalOperationId { get; init; } public string? BatchId { get; init; }
    public string? ChunkId { get; init; } public int ChunkIndex { get; init; } public int ChunkCount { get; init; }=1;
}
public sealed record ExcelPatchResult(ExcelLiveSnapshot Before,ExcelLiveSnapshot After,IReadOnlyList<string> ChangedCells)
{
    public string LogicalOperationId { get; init; }=""; public string BatchId { get; init; }=""; public string ChunkId { get; init; }="";
    public int ChunkIndex { get; init; } public int ChunkCount { get; init; }=1;
    public ExcelPatchMutationStatus MutationStatus { get; init; }=ExcelPatchMutationStatus.Applied;
    public ExcelPatchMutationEffect MutationEffect { get; init; }=ExcelPatchMutationEffect.Applied;
    public bool ReadbackComplete { get; init; }=true; public string? ErrorCode { get; init; } public string? ErrorMessage { get; init; }
    public IReadOnlyList<string> AppliedCells { get; init; }=[]; public IReadOnlyList<string> UnappliedCells { get; init; }=[];
    public IReadOnlyList<string> UnknownCells { get; init; }=[]; public string? ContentTokenBefore { get; init; } public string? ContentTokenAfter { get; init; }
}
public sealed record ExcelRecalculateRequest(string SessionId,string StateToken,bool PermissionGranted)
{ public string? ContentToken { get; init; } public string? SheetName { get; init; } public string? Range { get; init; } }

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
    string StateToken)
{
    public OfficeNativeIdentity? NativeIdentity { get; init; }
}


public sealed record WordDiscovery(
    IReadOnlyList<WordDocumentInfo> Documents,
    string? ActiveSessionId)
{
    public OfficeDiscoveryReport? Report { get; init; }
}

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
    string StateToken)
{
    public OfficeNativeIdentity? NativeIdentity { get; init; }
}


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

public sealed record WordLanguageEvidenceRequest(
    string SessionId,
    string StateToken);

public sealed record WordSpellingEvidence(
    int Start,
    int Length,
    string Text,
    IReadOnlyList<string> Suggestions,
    bool NativeEvidence);

public sealed record WordGrammarEvidence(
    int Start,
    int Length,
    string Text,
    string Message,
    bool NativeEvidence);

public sealed record WordLegalCitationEvidence(
    string CitationText,
    string DocumentId,
    int Start,
    int Length,
    bool NativeDocumentEvidence);

public sealed record WordLanguageEvidenceResult(
    string SessionId,
    string StateToken,
    IReadOnlyList<WordSpellingEvidence> Spelling,
    IReadOnlyList<WordGrammarEvidence> Grammar,
    IReadOnlyList<WordLegalCitationEvidence> Citations,
    string Provider);

public sealed record OfficePingResult(
    int ProcessId,
    string ProtocolVersion,
    bool FixtureMode,
    bool StaThread);

public static class OfficeProtocolConstants
{
    public const string Version = "1.2";
    public const int MaxMessageBytes = 4 * 1024 * 1024;
}

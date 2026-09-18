using System.Text.Json;
using H2AgentLab.OfficeProtocol;

namespace H2AgentLab.OfficeHost;

public sealed class FixtureOfficeBackend : IOfficeBackend
{
    private readonly ExcelFixture _excel = new();
    private readonly WordFixture _word = new();

    public ExcelDiscovery DiscoverExcel()
    {
        var snapshot = ExcelSnapshot();
        return new ExcelDiscovery(
            [Info(snapshot)],
            snapshot.SessionId);
    }

    public ExcelLiveSnapshot SnapshotExcel(string sessionId)
    {
        RequireSession(sessionId, _excel.SessionId, "Excel workbook");
        return ExcelSnapshot();
    }

    public ExcelPatchResult PatchExcel(ExcelPatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        OfficeHostSafety.RequirePermission(request.PermissionGranted);
        var before = SnapshotExcel(request.SessionId);
        OfficeHostSafety.RequireState(request.StateToken, before.StateToken);

        if (!string.Equals(request.SheetName, _excel.SheetName, StringComparison.Ordinal))
            throw new OfficeHostFaultException("sheet_not_found", "Fixture Excel sheet not found.");
        if (request.Cells.Count is < 1 or > 128)
            throw new OfficeHostFaultException("invalid_request", "Excel patch must contain 1..128 cells.");

        var changed = new List<string>();
        foreach (var patch in request.Cells)
        {
            var address = NormalizeCellAddress(patch.Address);
            if (!_excel.Cells.TryGetValue(address, out var cell))
                throw new OfficeHostFaultException("cell_not_found", $"Excel cell '{address}' is outside the fixture scope.");

            if (patch.ClearValue)
            {
                cell.Value = "";
                cell.Formula = "";
            }
            if (patch.Value is not null)
            {
                cell.Value = patch.Value;
                cell.Formula = "";
            }
            if (patch.Formula is not null)
                cell.Formula = patch.Formula;
            if (patch.Bold is bool bold) cell.Bold = bold;
            if (patch.Italic is bool italic) cell.Italic = italic;
            if (patch.FillColor is long fill) cell.FillColor = fill;
            if (patch.NumberFormat is not null) cell.NumberFormat = patch.NumberFormat;
            changed.Add(address);
        }

        _excel.Saved = false;
        var after = ExcelSnapshot();
        return new ExcelPatchResult(before, after, changed.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    public ExcelLiveSnapshot RecalculateExcel(ExcelRecalculateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        OfficeHostSafety.RequirePermission(request.PermissionGranted);
        var before = SnapshotExcel(request.SessionId);
        OfficeHostSafety.RequireState(request.StateToken, before.StateToken);

        if (_excel.Cells.TryGetValue("B1", out var b1)
            && string.Equals(b1.Formula, "=A1", StringComparison.OrdinalIgnoreCase)
            && _excel.Cells.TryGetValue("A1", out var a1))
            b1.Value = a1.Value;

        _excel.Saved = false;
        return ExcelSnapshot();
    }

    public OfficeSaveCopyResult SaveExcelCopy(OfficeSaveCopyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        OfficeHostSafety.RequirePermission(request.PermissionGranted);
        var snapshot = SnapshotExcel(request.SessionId);
        OfficeHostSafety.RequireState(request.StateToken, snapshot.StateToken);
        var destination = OfficeHostSafety.ValidateCopyDestination(request.DestinationPath, snapshot.FullName);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        File.WriteAllBytes(destination, bytes);
        return new OfficeSaveCopyResult(snapshot.SessionId, destination, snapshot.StateToken, OfficeHostSafety.Sha256(bytes));
    }

    public WordDiscovery DiscoverWord()
    {
        var snapshot = WordSnapshot();
        return new WordDiscovery(
            [Info(snapshot)],
            snapshot.SessionId);
    }

    public WordLiveSnapshot SnapshotWord(string sessionId)
    {
        RequireSession(sessionId, _word.SessionId, "Word document");
        return WordSnapshot();
    }

    public WordPatchResult PatchWord(WordPatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        OfficeHostSafety.RequirePermission(request.PermissionGranted);
        var before = SnapshotWord(request.SessionId);
        OfficeHostSafety.RequireState(request.StateToken, before.StateToken);

        if (request.Paragraphs.Count is < 1 or > 128)
            throw new OfficeHostFaultException("invalid_request", "Word patch must contain 1..128 paragraphs.");

        var changed = new List<int>();
        foreach (var patch in request.Paragraphs)
        {
            if (patch.ParagraphIndex < 0 || patch.ParagraphIndex >= _word.Paragraphs.Count)
                throw new OfficeHostFaultException("paragraph_not_found", $"Word paragraph {patch.ParagraphIndex} does not exist.");
            var paragraph = _word.Paragraphs[patch.ParagraphIndex];
            if (patch.Text is not null) paragraph.Text = patch.Text;
            if (patch.Bold is bool bold) paragraph.Bold = bold;
            if (patch.Italic is bool italic) paragraph.Italic = italic;
            if (patch.Underline is bool underline) paragraph.Underline = underline;
            changed.Add(patch.ParagraphIndex);
        }

        _word.Saved = false;
        var after = WordSnapshot();
        return new WordPatchResult(before, after, changed.Distinct().OrderBy(x => x).ToArray());
    }

    public OfficeSaveCopyResult SaveWordCopy(OfficeSaveCopyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        OfficeHostSafety.RequirePermission(request.PermissionGranted);
        var snapshot = SnapshotWord(request.SessionId);
        OfficeHostSafety.RequireState(request.StateToken, snapshot.StateToken);
        var destination = OfficeHostSafety.ValidateCopyDestination(request.DestinationPath, snapshot.FullName);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        File.WriteAllBytes(destination, bytes);
        return new OfficeSaveCopyResult(snapshot.SessionId, destination, snapshot.StateToken, OfficeHostSafety.Sha256(bytes));
    }

    private ExcelLiveSnapshot ExcelSnapshot()
    {
        var cells = _excel.Cells.Values
            .OrderBy(x => x.Address, StringComparer.Ordinal)
            .Select(x => new ExcelCellState(
                x.Address,
                x.Value,
                x.Formula,
                x.Bold,
                x.Italic,
                x.FillColor,
                x.NumberFormat,
                "General",
                "General"))
            .ToArray();
        var sheet = new ExcelSheetState(
            _excel.SheetName,
            "Visible",
            cells,
            ["A1:B1"],
            [3],
            [3]);
        var basis = new
        {
            _excel.SessionId,
            _excel.Name,
            _excel.FullName,
            _excel.Saved,
            _excel.SheetName,
            _excel.SelectionAddress,
            Sheet = sheet
        };
        return new ExcelLiveSnapshot(
            _excel.SessionId,
            _excel.Name,
            _excel.FullName,
            _excel.Saved,
            _excel.SheetName,
            _excel.SelectionAddress,
            [sheet],
            OfficeHostSafety.StableToken(basis));
    }

    private WordLiveSnapshot WordSnapshot()
    {
        var paragraphs = _word.Paragraphs
            .Select((x, index) => new WordParagraphState(
                index,
                x.Text,
                "Normal",
                [
                    new WordRunState(
                        0,
                        x.Text,
                        "DefaultParagraphFont",
                        x.Bold,
                        x.Italic,
                        x.Underline)
                ]))
            .ToArray();
        var tables = new[]
        {
            new WordTableState(
                0,
                [
                    (IReadOnlyList<string>)new[] { "A", "B" }
                ])
        };
        var sections = new[]
        {
            new WordSectionState(0, 612, 792, 72, 72, 72, 72)
        };
        var headers = new[]
        {
            new WordPartState(
                "section:0:header:Default",
                [
                    new WordParagraphState(
                        0,
                        "Fixture Header",
                        "Header",
                        [new WordRunState(0, "Fixture Header", "DefaultParagraphFont", false, false, false)])
                ])
        };
        var footers = new[]
        {
            new WordPartState(
                "section:0:footer:Default",
                [
                    new WordParagraphState(
                        0,
                        "Fixture Footer",
                        "Footer",
                        [new WordRunState(0, "Fixture Footer", "DefaultParagraphFont", false, false, false)])
                ])
        };
        var basis = new
        {
            _word.SessionId,
            _word.Name,
            _word.FullName,
            _word.Saved,
            _word.SelectionStart,
            _word.SelectionEnd,
            _word.SelectionText,
            Paragraphs = paragraphs,
            Tables = tables,
            Sections = sections,
            Headers = headers,
            Footers = footers
        };
        return new WordLiveSnapshot(
            _word.SessionId,
            _word.Name,
            _word.FullName,
            _word.Saved,
            _word.SelectionStart,
            _word.SelectionEnd,
            _word.SelectionText,
            paragraphs,
            tables,
            sections,
            headers,
            footers,
            OfficeHostSafety.StableToken(basis));
    }

    private static ExcelWorkbookInfo Info(ExcelLiveSnapshot snapshot)
        => new(
            snapshot.SessionId,
            snapshot.Name,
            snapshot.FullName,
            snapshot.Saved,
            snapshot.ActiveSheet,
            snapshot.SelectionAddress,
            snapshot.StateToken);

    private static WordDocumentInfo Info(WordLiveSnapshot snapshot)
        => new(
            snapshot.SessionId,
            snapshot.Name,
            snapshot.FullName,
            snapshot.Saved,
            snapshot.SelectionStart,
            snapshot.SelectionEnd,
            snapshot.SelectionText,
            snapshot.StateToken);

    private static void RequireSession(string actual, string expected, string kind)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new OfficeHostFaultException("session_not_found", $"{kind} session is no longer available.");
    }

    private static string NormalizeCellAddress(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var value = address.Trim().ToUpperInvariant();
        if (value.Length > 32 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '$' or ':')))
            throw new OfficeHostFaultException("invalid_request", "Invalid Excel address.");
        return value.Replace("$", "", StringComparison.Ordinal);
    }

    private sealed class ExcelFixture
    {
        public string SessionId { get; } = "excel-fixture-1";
        public string Name { get; } = "UnsavedFixture.xlsx";
        public string FullName { get; } = Path.Combine(Path.GetTempPath(), "H2AgentLab", "UnsavedFixture.xlsx");
        public string SheetName { get; } = "Data";
        public string SelectionAddress { get; } = "A1";
        public bool Saved { get; set; }
        public Dictionary<string, ExcelCellFixture> Cells { get; } = new(StringComparer.Ordinal)
        {
            ["A1"] = new("A1", "UNSAVED-EXCEL", "", true, false, 65535, "General"),
            ["B1"] = new("B1", "UNSAVED-EXCEL", "=A1", false, true, null, "General"),
            ["A2"] = new("A2", "42", "", false, false, null, "0.00")
        };
    }

    private sealed class ExcelCellFixture(
        string address,
        string value,
        string formula,
        bool bold,
        bool italic,
        long? fillColor,
        string numberFormat)
    {
        public string Address { get; } = address;
        public string Value { get; set; } = value;
        public string Formula { get; set; } = formula;
        public bool Bold { get; set; } = bold;
        public bool Italic { get; set; } = italic;
        public long? FillColor { get; set; } = fillColor;
        public string NumberFormat { get; set; } = numberFormat;
    }

    private sealed class WordFixture
    {
        public string SessionId { get; } = "word-fixture-1";
        public string Name { get; } = "UnsavedFixture.docx";
        public string FullName { get; } = Path.Combine(Path.GetTempPath(), "H2AgentLab", "UnsavedFixture.docx");
        public bool Saved { get; set; }
        public int SelectionStart { get; } = 0;
        public int SelectionEnd { get; } = 12;
        public string SelectionText => Paragraphs[0].Text;
        public List<WordParagraphFixture> Paragraphs { get; } =
        [
            new("UNSAVED-WORD", true, false, false),
            new("Preserve me", false, true, false)
        ];
    }

    private sealed class WordParagraphFixture(
        string text,
        bool bold,
        bool italic,
        bool underline)
    {
        public string Text { get; set; } = text;
        public bool Bold { get; set; } = bold;
        public bool Italic { get; set; } = italic;
        public bool Underline { get; set; } = underline;
    }
}

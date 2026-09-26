using System.Text.Json;
using H2AgentLab.OfficeProtocol;

namespace H2AgentLab.OfficeHost;

public sealed class FixtureOfficeBackend : IOfficeBackend, IExcelRangeReadBackend
{
    private readonly ExcelFixture _excel = new();
    private readonly WordFixture _word = new();
    private long _excelRevision = 1;
    private readonly int _excelPatchFaultAfterWrites; private readonly bool _excelPatchReadbackFailsAfterFault; private readonly bool _excelSheetProtected;

    public FixtureOfficeBackend(int extraExcelRows=0,int sparseExcelLastRow=0,int excelPatchFaultAfterWrites=-1,
        bool excelPatchReadbackFailsAfterFault=false,bool excelSheetProtected=false)
    {
        if (extraExcelRows is < 0 or > 20_000) throw new ArgumentOutOfRangeException(nameof(extraExcelRows));
        if(excelPatchFaultAfterWrites < -1 || excelPatchFaultAfterWrites > ExcelPatchLimits.MaxCells)throw new ArgumentOutOfRangeException(nameof(excelPatchFaultAfterWrites));
        _excelPatchFaultAfterWrites=excelPatchFaultAfterWrites;_excelPatchReadbackFailsAfterFault=excelPatchReadbackFailsAfterFault;_excelSheetProtected=excelSheetProtected;
        if (sparseExcelLastRow is < 0 or > ExcelRangeReadRules.MaxExcelRows) throw new ArgumentOutOfRangeException(nameof(sparseExcelLastRow));
        for (var row = 3; row < 3 + extraExcelRows; row++)
        {
            var address = "A" + row;
            _excel.Cells.Add(address, new ExcelCellFixture(address, "UNCHANGED-" + row, "", false, false, null, "General"));
        }
        if (sparseExcelLastRow > 0)
        {
            var address = "Z" + sparseExcelLastRow;
            _excel.Cells[address] = new ExcelCellFixture(address, "SPARSE-END", "", false, false, null, "General");
        }
    }

    public ExcelDiscovery DiscoverExcel()
    {
        var info = new ExcelWorkbookInfo(
            _excel.SessionId,
            _excel.Name,
            _excel.FullName,
            _excel.Saved,
            _excel.SheetName,
            _excel.SelectionAddress,
            "");
        return new ExcelDiscovery([info], _excel.SessionId);
    }

    public void MoveExcelSelectionForFixture(string address)
    {
        var bounds = ExcelRangeReadRules.ParseRange(address);
        if (bounds.CellCount != 1)
            throw new ArgumentException("Fixture selection must be one cell.", nameof(address));
        _excel.SelectionAddress = bounds.Address;
    }

    public void RenameExcelSheetForFixture(string sheetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sheetName);
        _excel.SheetName = sheetName.Trim();
        _excel.Saved = false;
        _excelRevision++;
    }

    public void SetExcelValueForFixture(string address, string value, string formula = "")
    {
        var bounds = ExcelRangeReadRules.ParseRange(address);
        if (bounds.CellCount != 1)
            throw new ArgumentException("Fixture edit must target one cell.", nameof(address));
        var normalized = bounds.Address;
        if (!_excel.Cells.TryGetValue(normalized, out var cell))
        {
            cell = new ExcelCellFixture(normalized, "", "", false, false, null, "General");
            _excel.Cells[normalized] = cell;
        }
        cell.Value = value ?? "";
        cell.Formula = formula ?? "";
        _excel.Saved = false;
        _excelRevision++;
    }

    public ExcelLiveSnapshot SnapshotExcel(string sessionId)
    {
        RequireSession(sessionId, _excel.SessionId, "Excel workbook");
        return ExcelSnapshot();
    }

    public ExcelRangeReadPage ReadExcelRange(ExcelReadRangeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireSession(request.SessionId, _excel.SessionId, "Excel workbook");
        if (!string.Equals(request.SheetName, _excel.SheetName, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(request.Cursor) || !string.IsNullOrWhiteSpace(request.ContentVersion))
                throw new OfficeHostFaultException(
                    "stale_content",
                    "Fixture Excel sheet changed or is no longer available; restart the range read.",
                    true);
            throw new OfficeHostFaultException("sheet_not_found", "Fixture Excel sheet not found.", true);
        }

        ExcelRangeBounds requested;
        IReadOnlyList<string> fields;
        int pageSize;
        try
        {
            requested = ExcelRangeReadRules.ParseRange(request.Range);
            fields = ExcelRangeReadFields.Normalize(request.Fields);
            pageSize = ExcelRangeReadLimits.NormalizePageSize(request.PageSize);
        }
        catch (Exception ex) when (ex is ArgumentException)
        {
            throw new OfficeHostFaultException("invalid_request", ex.Message, true);
        }

        ExcelRangePagePlan plan;
        try { plan = ExcelRangeReadRules.PlanPage(requested, pageSize, request.Cursor); }
        catch (Exception ex) when (ex is ArgumentException)
        { throw new OfficeHostFaultException("invalid_cursor", ex.Message, true); }

        var structuralPaging = !plan.Complete
            && fields.Any(field => field is ExcelRangeReadFields.Format or ExcelRangeReadFields.Merge or ExcelRangeReadFields.Hidden);
        if (structuralPaging)
            throw new OfficeHostFaultException(
                "content_tracking_unavailable",
                "Paged Excel format/merge/hidden reads are not safe because native Excel change events do not provide a reliable structural-edit revision. Request a bounded structural range that completes in one page.",
                true);

        var extent = ExcelExtent();
        var contentVersion = OfficeHostSafety.StableToken(new
        {
            _excel.SessionId,
            _excel.Name,
            _excel.FullName,
            _excel.Saved,
            _excel.SheetName,
            extent.Address,
            _excelRevision,
            requestedRange = requested.Address,
            fields = fields.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            pageSize
        });
        if (!string.IsNullOrWhiteSpace(request.Cursor) && string.IsNullOrWhiteSpace(request.ContentVersion))
            throw new OfficeHostFaultException("invalid_request", "A continuation cursor requires content_version.", true);
        if (!string.IsNullOrWhiteSpace(request.ContentVersion)
            && !string.Equals(request.ContentVersion, contentVersion, StringComparison.Ordinal))
            throw new OfficeHostFaultException("stale_content", "Excel content changed after the previous page; restart the range read.", true);

        var started = Environment.TickCount64;
        var cells = new List<ExcelRangeCellState>(plan.CellCount);
        var wantValue = fields.Contains(ExcelRangeReadFields.Value, StringComparer.Ordinal);
        var wantFormula = fields.Contains(ExcelRangeReadFields.Formula, StringComparer.Ordinal);
        var wantFormat = fields.Contains(ExcelRangeReadFields.Format, StringComparer.Ordinal);
        for (var row = plan.Bounds.StartRow; row <= plan.Bounds.EndRow; row++)
        for (var column = plan.Bounds.StartColumn; column <= plan.Bounds.EndColumn; column++)
        {
            var address = ExcelRangeReadRules.CellAddress(row, column);
            _excel.Cells.TryGetValue(address, out var cell);
            cells.Add(new ExcelRangeCellState(
                address,
                wantValue ? cell?.Value ?? "" : null,
                wantFormula ? cell?.Formula ?? "" : null,
                wantFormat ? cell?.Bold ?? false : null,
                wantFormat ? cell?.Italic ?? false : null,
                wantFormat ? cell?.FillColor : null,
                wantFormat ? cell?.NumberFormat ?? "General" : null,
                wantFormat ? "General" : null,
                wantFormat ? "General" : null));
        }

        var merges = fields.Contains(ExcelRangeReadFields.Merge, StringComparer.Ordinal)
            && ExcelRangeReadRules.Intersects(plan.Bounds, ExcelRangeReadRules.ParseRange("A1:B1"))
            ? new[] { "A1:B1" }
            : [];
        var hiddenRows = fields.Contains(ExcelRangeReadFields.Hidden, StringComparer.Ordinal)
            && plan.Bounds.StartRow <= 3 && plan.Bounds.EndRow >= 3 ? new[] { 3 } : [];
        var hiddenColumns = fields.Contains(ExcelRangeReadFields.Hidden, StringComparer.Ordinal)
            && plan.Bounds.StartColumn <= 3 && plan.Bounds.EndColumn >= 3 ? new[] { 3 } : [];
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(new { cells, merges, hiddenRows, hiddenColumns }).Length;
        return new ExcelRangeReadPage(
            _excel.SessionId,
            _excel.Name,
            _excel.FullName,
            _excel.Saved,
            _excel.SheetName,
            requested.Address,
            plan.Bounds.Address,
            fields,
            cells,
            merges,
            hiddenRows,
            hiddenColumns,
            extent,
            contentVersion,
            plan.NextCursor,
            plan.Complete,
            "FixtureLiveDocument",
            new(plan.CellCount, payloadBytes, Math.Max(0, Environment.TickCount64 - started)));
    }

    public ExcelPatchResult PatchExcel(ExcelPatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);OfficeHostSafety.RequirePermission(request.PermissionGranted);
        IReadOnlyList<ExcelCellPatch> cells;try{cells=ExcelPatchMutationRules.ValidateAndNormalize(request.Cells);}
        catch(ArgumentException ex){throw new OfficeHostFaultException("invalid_request",ex.Message,true);}
        var before=SnapshotExcel(request.SessionId);RequireExcelMutationPrecondition(request.StateToken,request.ContentToken,before);
        if(request.SheetName!=_excel.SheetName)throw new OfficeHostFaultException("sheet_not_found","Fixture Excel sheet not found.",true);
        if(_excelSheetProtected)throw new OfficeHostFaultException("protected_cell","The fixture worksheet is protected; no cells were written.",true);
        foreach(var patch in cells){if(!_excel.Cells.ContainsKey(patch.Address))throw new OfficeHostFaultException("cell_not_found",$"Excel cell '{patch.Address}' is outside the fixture scope.",true);
            if(IsMergedNonAnchor(patch.Address,"A1:B1"))throw new OfficeHostFaultException("merged_cell_non_anchor",$"Excel cell '{patch.Address}' is not the top-left cell of its merged range.",true);}
        var applied=0;Exception? failure=null;
        foreach(var patch in cells){if(_excelPatchFaultAfterWrites>=0&&applied>=_excelPatchFaultAfterWrites){failure=new IOException("Injected Excel write failure.");break;}
            try{Apply(_excel.Cells[patch.Address],patch);applied++;}catch(Exception ex){failure=ex;break;}}
        if(applied>0){_excel.Saved=false;_excelRevision++;}
        if(failure is not null&&_excelPatchReadbackFailsAfterFault)return ExcelPatchMutationRules.Classify(request,before,null,cells,"outcome_unknown","Fixture readback was intentionally unavailable after a possible write.");
        return ExcelPatchMutationRules.Classify(request,before,ExcelSnapshot(),cells,failure is null?null:"injected_write_failure",
            failure is null?null:"Excel stopped before the whole fixture batch completed.");
    }

    public ExcelLiveSnapshot RecalculateExcel(ExcelRecalculateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);OfficeHostSafety.RequirePermission(request.PermissionGranted);
        var before=SnapshotExcel(request.SessionId);RequireExcelMutationPrecondition(request.StateToken,request.ContentToken,before);
        if(!string.IsNullOrWhiteSpace(request.SheetName)&&request.SheetName!=_excel.SheetName)throw new OfficeHostFaultException("sheet_not_found","Fixture Excel sheet not found.",true);
        if(!string.IsNullOrWhiteSpace(request.Range)&&string.IsNullOrWhiteSpace(request.SheetName))throw new OfficeHostFaultException("invalid_request","Scoped Excel recalculation requires sheet name.",true);
        var includesB1=true;if(!string.IsNullOrWhiteSpace(request.Range)){ExcelRangeBounds scope;try{scope=ExcelRangeReadRules.ParseRange(request.Range);}
            catch(ArgumentException ex){throw new OfficeHostFaultException("invalid_request",ex.Message,true);}includesB1=ExcelRangeReadRules.Intersects(scope,ExcelRangeReadRules.ParseRange("B1"));}
        if(includesB1&&_excel.Cells.TryGetValue("B1",out var b1)&&b1.Formula.Equals("=A1",StringComparison.OrdinalIgnoreCase)&&_excel.Cells.TryGetValue("A1",out var a1))b1.Value=a1.Value;
        _excel.Saved=false;_excelRevision++;return ExcelSnapshot();
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

        if (WordPatchRules.ValidationError(before, request.Paragraphs) is { } problem)
            throw new OfficeHostFaultException("word_patch_rejected", problem);

        var changed = new List<int>();
        foreach (var patch in request.Paragraphs.OrderByDescending(p => p.ParagraphIndex))
        {
            if (patch.ParagraphIndex < 0 || patch.ParagraphIndex >= _word.Paragraphs.Count)
                throw new OfficeHostFaultException("paragraph_not_found", $"Word paragraph {patch.ParagraphIndex} does not exist.");
            var paragraph = _word.Paragraphs[patch.ParagraphIndex];
            if (patch.Bold is bool bold) paragraph.Bold = bold;
            if (patch.Italic is bool italic) paragraph.Italic = italic;
            if (patch.Underline is bool underline) paragraph.Underline = underline;
            if (patch.Text is not null)
            {
                _word.Paragraphs.RemoveAt(patch.ParagraphIndex);
                _word.Paragraphs.InsertRange(patch.ParagraphIndex, WordPatchRules.Lines(patch.Text)
                    .Select(text => new WordParagraphFixture(text, paragraph.Bold, paragraph.Italic, paragraph.Underline)));
            }
            changed.Add(patch.ParagraphIndex);
        }

        _word.Saved = false;
        var after = WordSnapshot();
        return new WordPatchResult(before, after, changed.Distinct().OrderBy(x => x).ToArray());
    }

    public WordLanguageEvidenceResult InspectWordLanguage(WordLanguageEvidenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = SnapshotWord(request.SessionId);
        OfficeHostSafety.RequireState(request.StateToken, snapshot.StateToken);

        var text = string.Join("\n", snapshot.Paragraphs.Select(x => x.Text));
        var spellingText = "mispell";
        var spellingStart = text.IndexOf(spellingText, StringComparison.Ordinal);
        var citationText = "Nghị định số 214/2025/NĐ-CP";
        var citationStart = text.IndexOf(citationText, StringComparison.Ordinal);

        return new WordLanguageEvidenceResult(
            snapshot.SessionId,
            snapshot.StateToken,
            spellingStart >= 0
                ? [new WordSpellingEvidence(
                    spellingStart,
                    spellingText.Length,
                    spellingText,
                    ["misspell"],
                    NativeEvidence: false)]
                : [],
            [],
            citationStart >= 0
                ? [new WordLegalCitationEvidence(
                    citationText,
                    "214/2025/NĐ-CP",
                    citationStart,
                    citationText.Length,
                    NativeDocumentEvidence: false)]
                : [],
            "fixture-office-host");
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

    private ExcelSheetExtent ExcelExtent()
    {
        var bounds = _excel.Cells.Keys
            .Select(ExcelRangeReadRules.ParseRange)
            .ToArray();
        var firstRow = bounds.Min(x => x.StartRow);
        var firstColumn = bounds.Min(x => x.StartColumn);
        var lastRow = bounds.Max(x => x.EndRow);
        var lastColumn = bounds.Max(x => x.EndColumn);
        var extent = new ExcelRangeBounds(firstRow, firstColumn, lastRow, lastColumn);
        return new ExcelSheetExtent(
            extent.Address,
            firstRow,
            firstColumn,
            lastRow,
            lastColumn,
            extent.CellCount);
    }

    private static void Apply(ExcelCellFixture cell,ExcelCellPatch patch)
    {if(patch.ClearValue){cell.Value="";cell.Formula="";}if(patch.Value is not null){cell.Value=patch.Value;cell.Formula="";}if(patch.Formula is not null)cell.Formula=patch.Formula;
     if(patch.Bold is bool b)cell.Bold=b;if(patch.Italic is bool i)cell.Italic=i;if(patch.FillColor is long f)cell.FillColor=f;if(patch.NumberFormat is not null)cell.NumberFormat=patch.NumberFormat;}
    private static bool IsMergedNonAnchor(string address,string mergedRange){var m=ExcelRangeReadRules.ParseRange(mergedRange);
        return ExcelRangeReadRules.Intersects(ExcelRangeReadRules.ParseRange(address),m)&&address!=ExcelRangeReadRules.CellAddress(m.StartRow,m.StartColumn);}
    private static void RequireExcelMutationPrecondition(string stateToken,string? contentToken,ExcelLiveSnapshot current)
    {if(!string.IsNullOrWhiteSpace(contentToken)){if(contentToken!=ExcelPatchMutationRules.ContentToken(current))throw new OfficeHostFaultException("stale_content","Excel content changed since the caller observed it.",true);return;}
     try{OfficeHostSafety.RequireState(stateToken,current.StateToken);}catch(OfficeHostFaultException ex){throw new OfficeHostFaultException(ex.Code,ex.Message,true);}}

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
        var contentBasis=new{_excel.SessionId,_excel.Name,_excel.FullName,_excel.Saved,_excel.SheetName,Sheet=sheet,Revision=_excelRevision};
        return new ExcelLiveSnapshot(_excel.SessionId,_excel.Name,_excel.FullName,_excel.Saved,_excel.SheetName,_excel.SelectionAddress,[sheet],
            OfficeHostSafety.StableToken(basis)){ContentToken=OfficeHostSafety.StableToken(contentBasis)};
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
        public string SheetName { get; set; } = "Data";
        public string SelectionAddress { get; set; } = "A1";
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
            new("Preserve me", false, true, false),
            new("mispell · Nghị định số 214/2025/NĐ-CP", false, false, false)
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

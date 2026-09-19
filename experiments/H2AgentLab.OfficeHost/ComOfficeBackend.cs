using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using H2AgentLab.OfficeProtocol;

namespace H2AgentLab.OfficeHost;

public sealed class ComOfficeBackend : IOfficeBackend
{
    private const int MaxExcelCells = 5_000;
    private const int MaxWordParagraphs = 2_000;
    private const int MaxWordRuns = 5_000;
    private const int MaxWordTables = 200;
    private const int MaxWordLanguageItems = 200;
    private static readonly Regex LegalCitationPattern = new(
        @"\b(?:Luật|Nghị định|Thông tư|Quyết định)\s+(?:số\s+)?(?<id>[0-9]+(?:/[0-9]{4})?/[A-ZĐ0-9-]+(?:-[A-ZĐ0-9]+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public ExcelDiscovery DiscoverExcel()
    {
        dynamic? app = TryActive("Excel.Application");
        if (app is null) return new ExcelDiscovery([], null);
        try
        {
            var list = new List<ExcelWorkbookInfo>();
            var count = Convert.ToInt32(app.Workbooks.Count, CultureInfo.InvariantCulture);
            var activeSession = "";
            dynamic? active = null;
            try { active = app.ActiveWorkbook; } catch { }
            for (var i = 1; i <= count; i++)
            {
                dynamic workbook = app.Workbooks[i];
                try
                {
                    var snapshot = SnapshotExcelInternal(app, workbook);
                    list.Add(new ExcelWorkbookInfo(
                        snapshot.SessionId,
                        snapshot.Name,
                        snapshot.FullName,
                        snapshot.Saved,
                        snapshot.ActiveSheet,
                        snapshot.SelectionAddress,
                        snapshot.StateToken));
                    if (active is not null && SessionIdForExcel(app, active) == snapshot.SessionId)
                        activeSession = snapshot.SessionId;
                }
                finally { Release(workbook); }
            }
            Release(active);
            return new ExcelDiscovery(list, string.IsNullOrEmpty(activeSession) ? null : activeSession);
        }
        finally { Release(app); }
    }

    public ExcelLiveSnapshot SnapshotExcel(string sessionId)
    {
        var (app, workbook) = FindExcel(sessionId);
        try { return SnapshotExcelInternal(app, workbook); }
        finally { Release(workbook); Release(app); }
    }

    public ExcelPatchResult PatchExcel(ExcelPatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        OfficeHostSafety.RequirePermission(request.PermissionGranted);
        var (app, workbook) = FindExcel(request.SessionId);
        try
        {
            var before = SnapshotExcelInternal(app, workbook);
            OfficeHostSafety.RequireState(request.StateToken, before.StateToken);
            if (request.Cells.Count is < 1 or > 128)
                throw new OfficeHostFaultException("invalid_request", "Excel patch must contain 1..128 single-cell operations.");

            dynamic? sheet = null;
            try
            {
                sheet = workbook.Worksheets[request.SheetName];
            }
            catch
            {
                throw new OfficeHostFaultException("sheet_not_found", $"Excel sheet '{request.SheetName}' is not available.");
            }

            try
            {
                var changed = new List<string>();
                foreach (var patch in request.Cells)
                {
                    var address = ValidateSingleCellAddress(patch.Address);
                    dynamic cell = sheet.Range[address];
                    try
                    {
                        if (patch.ClearValue)
                        {
                            cell.ClearContents();
                        }
                        if (patch.Value is not null)
                        {
                            cell.Value2 = patch.Value;
                        }
                        if (patch.Formula is not null)
                        {
                            cell.Formula = patch.Formula;
                        }
                        if (patch.Bold is bool bold) cell.Font.Bold = bold;
                        if (patch.Italic is bool italic) cell.Font.Italic = italic;
                        if (patch.FillColor is long fill) cell.Interior.Color = fill;
                        if (patch.NumberFormat is not null) cell.NumberFormat = patch.NumberFormat;
                        changed.Add(address);
                    }
                    finally { Release(cell); }
                }

                var after = SnapshotExcelInternal(app, workbook);
                return new ExcelPatchResult(
                    before,
                    after,
                    changed.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            }
            finally { Release(sheet); }
        }
        finally { Release(workbook); Release(app); }
    }

    public ExcelLiveSnapshot RecalculateExcel(ExcelRecalculateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        OfficeHostSafety.RequirePermission(request.PermissionGranted);
        var (app, workbook) = FindExcel(request.SessionId);
        try
        {
            var before = SnapshotExcelInternal(app, workbook);
            OfficeHostSafety.RequireState(request.StateToken, before.StateToken);
            app.Calculate();
            return SnapshotExcelInternal(app, workbook);
        }
        finally { Release(workbook); Release(app); }
    }

    public OfficeSaveCopyResult SaveExcelCopy(OfficeSaveCopyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        OfficeHostSafety.RequirePermission(request.PermissionGranted);
        var (app, workbook) = FindExcel(request.SessionId);
        try
        {
            var snapshot = SnapshotExcelInternal(app, workbook);
            OfficeHostSafety.RequireState(request.StateToken, snapshot.StateToken);
            var destination = OfficeHostSafety.ValidateCopyDestination(request.DestinationPath, snapshot.FullName);
            workbook.SaveCopyAs(destination);
            var bytes = File.ReadAllBytes(destination);
            return new OfficeSaveCopyResult(
                snapshot.SessionId,
                destination,
                snapshot.StateToken,
                OfficeHostSafety.Sha256(bytes));
        }
        finally { Release(workbook); Release(app); }
    }

    public WordDiscovery DiscoverWord()
    {
        dynamic? app = TryActive("Word.Application");
        if (app is null) return new WordDiscovery([], null);
        try
        {
            var list = new List<WordDocumentInfo>();
            var count = Convert.ToInt32(app.Documents.Count, CultureInfo.InvariantCulture);
            var activeSession = "";
            dynamic? active = null;
            try { active = app.ActiveDocument; } catch { }
            for (var i = 1; i <= count; i++)
            {
                dynamic document = app.Documents[i];
                try
                {
                    var snapshot = SnapshotWordInternal(app, document);
                    list.Add(new WordDocumentInfo(
                        snapshot.SessionId,
                        snapshot.Name,
                        snapshot.FullName,
                        snapshot.Saved,
                        snapshot.SelectionStart,
                        snapshot.SelectionEnd,
                        snapshot.SelectionText,
                        snapshot.StateToken));
                    if (active is not null && SessionIdForWord(app, active) == snapshot.SessionId)
                        activeSession = snapshot.SessionId;
                }
                finally { Release(document); }
            }
            Release(active);
            return new WordDiscovery(list, string.IsNullOrEmpty(activeSession) ? null : activeSession);
        }
        finally { Release(app); }
    }

    public WordLiveSnapshot SnapshotWord(string sessionId)
    {
        var (app, document) = FindWord(sessionId);
        try { return SnapshotWordInternal(app, document); }
        finally { Release(document); Release(app); }
    }

    public WordPatchResult PatchWord(WordPatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        OfficeHostSafety.RequirePermission(request.PermissionGranted);
        var (app, document) = FindWord(request.SessionId);
        try
        {
            var before = SnapshotWordInternal(app, document);
            OfficeHostSafety.RequireState(request.StateToken, before.StateToken);
            if (request.Paragraphs.Count is < 1 or > 128)
                throw new OfficeHostFaultException("invalid_request", "Word patch must contain 1..128 paragraph operations.");

            var paragraphCount = Convert.ToInt32(document.Paragraphs.Count, CultureInfo.InvariantCulture);
            var changed = new List<int>();
            foreach (var patch in request.Paragraphs)
            {
                if (patch.ParagraphIndex < 0 || patch.ParagraphIndex >= paragraphCount)
                    throw new OfficeHostFaultException("paragraph_not_found", $"Word paragraph {patch.ParagraphIndex} does not exist.");

                dynamic paragraph = document.Paragraphs[patch.ParagraphIndex + 1];
                dynamic range = paragraph.Range;
                dynamic? contentRange = null;
                try
                {
                    var start = Convert.ToInt32(range.Start, CultureInfo.InvariantCulture);
                    var end = Convert.ToInt32(range.End, CultureInfo.InvariantCulture);
                    contentRange = document.Range(start, Math.Max(start, end - 1));
                    if (patch.Text is not null) contentRange.Text = patch.Text;
                    if (patch.Bold is bool bold) contentRange.Font.Bold = bold ? -1 : 0;
                    if (patch.Italic is bool italic) contentRange.Font.Italic = italic ? -1 : 0;
                    if (patch.Underline is bool underline) contentRange.Font.Underline = underline ? 1 : 0;
                    changed.Add(patch.ParagraphIndex);
                }
                finally
                {
                    Release(contentRange);
                    Release(range);
                    Release(paragraph);
                }
            }

            var after = SnapshotWordInternal(app, document);
            return new WordPatchResult(
                before,
                after,
                changed.Distinct().OrderBy(x => x).ToArray());
        }
        finally { Release(document); Release(app); }
    }

    public WordLanguageEvidenceResult InspectWordLanguage(WordLanguageEvidenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (app, document) = FindWord(request.SessionId);
        try
        {
            var snapshot = SnapshotWordInternal(app, document);
            OfficeHostSafety.RequireState(request.StateToken, snapshot.StateToken);

            var spelling = ReadWordSpellingEvidence(app, document);
            var grammar = ReadWordGrammarEvidence(document);
            var citations = ReadWordCitationEvidence(document);
            return new WordLanguageEvidenceResult(
                snapshot.SessionId,
                snapshot.StateToken,
                spelling,
                grammar,
                citations,
                "word-com-native");
        }
        finally
        {
            Release(document);
            Release(app);
        }
    }

    public OfficeSaveCopyResult SaveWordCopy(OfficeSaveCopyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        OfficeHostSafety.RequirePermission(request.PermissionGranted);
        var (app, document) = FindWord(request.SessionId);
        dynamic? copy = null;
        try
        {
            var snapshot = SnapshotWordInternal(app, document);
            OfficeHostSafety.RequireState(request.StateToken, snapshot.StateToken);
            var destination = OfficeHostSafety.ValidateCopyDestination(request.DestinationPath, snapshot.FullName);

            copy = app.Documents.Add();
            CopyWordDocument(document, copy);
            var extension = Path.GetExtension(destination).ToLowerInvariant();
            if (extension == ".pdf")
                copy.ExportAsFixedFormat(destination, 17);
            else
                copy.SaveAs2(destination);

            var bytes = File.ReadAllBytes(destination);
            return new OfficeSaveCopyResult(
                snapshot.SessionId,
                destination,
                snapshot.StateToken,
                OfficeHostSafety.Sha256(bytes));
        }
        finally
        {
            if (copy is not null)
            {
                try { copy.Close(false); } catch { }
            }
            Release(copy);
            Release(document);
            Release(app);
        }
    }

    private static ExcelLiveSnapshot SnapshotExcelInternal(dynamic app, dynamic workbook)
    {
        var sessionId = SessionIdForExcel(app, workbook);
        var name = SafeString(() => workbook.Name);
        var fullName = SafeString(() => workbook.FullName, name);
        var saved = SafeBool(() => workbook.Saved);
        var activeSheetName = "";
        var selectionAddress = "";
        try
        {
            dynamic activeSheet = workbook.ActiveSheet;
            try { activeSheetName = SafeString(() => activeSheet.Name); }
            finally { Release(activeSheet); }
        }
        catch { }
        try
        {
            if (SessionIdForExcel(app, app.ActiveWorkbook) == sessionId)
                selectionAddress = SafeString(() => app.Selection.Address[false, false]);
        }
        catch { }

        var sheets = new List<ExcelSheetState>();
        var sheetCount = Convert.ToInt32(workbook.Worksheets.Count, CultureInfo.InvariantCulture);
        var totalCells = 0;
        for (var i = 1; i <= sheetCount; i++)
        {
            dynamic sheet = workbook.Worksheets[i];
            dynamic? used = null;
            try
            {
                var sheetName = SafeString(() => sheet.Name);
                var visibility = ExcelVisibility(Convert.ToInt32(sheet.Visible, CultureInfo.InvariantCulture));
                used = sheet.UsedRange;
                var rowStart = Convert.ToInt32(used.Row, CultureInfo.InvariantCulture);
                var colStart = Convert.ToInt32(used.Column, CultureInfo.InvariantCulture);
                var rowCount = Math.Max(1, Convert.ToInt32(used.Rows.Count, CultureInfo.InvariantCulture));
                var colCount = Math.Max(1, Convert.ToInt32(used.Columns.Count, CultureInfo.InvariantCulture));
                if ((long)rowCount * colCount + totalCells > MaxExcelCells)
                    throw new OfficeHostFaultException("snapshot_too_large", $"Excel live snapshot exceeds {MaxExcelCells} used cells.");

                var cells = new List<ExcelCellState>();
                var merges = new HashSet<string>(StringComparer.Ordinal);
                var hiddenRows = new List<int>();
                var hiddenColumns = new List<int>();

                for (var rowOffset = 0; rowOffset < rowCount; rowOffset++)
                {
                    dynamic rowRange = sheet.Rows[rowStart + rowOffset];
                    try
                    {
                        if (SafeBool(() => rowRange.Hidden))
                            hiddenRows.Add(rowStart + rowOffset);
                    }
                    finally { Release(rowRange); }
                }
                for (var colOffset = 0; colOffset < colCount; colOffset++)
                {
                    dynamic colRange = sheet.Columns[colStart + colOffset];
                    try
                    {
                        if (SafeBool(() => colRange.Hidden))
                            hiddenColumns.Add(colStart + colOffset);
                    }
                    finally { Release(colRange); }
                }

                for (var r = 0; r < rowCount; r++)
                for (var col = 0; col < colCount; col++)
                {
                    dynamic cell = sheet.Cells[rowStart + r, colStart + col];
                    try
                    {
                        var address = SafeString(() => cell.Address[false, false]);
                        var value = ConvertOfficeValue(SafeObject(() => cell.Value2));
                        var formula = SafeString(() => cell.Formula);
                        var bold = SafeBool(() => cell.Font.Bold);
                        var italic = SafeBool(() => cell.Font.Italic);
                        long? fill = SafeLong(() => cell.Interior.Color);
                        var numberFormat = SafeString(() => cell.NumberFormat);
                        var horizontal = ConvertOfficeValue(SafeObject(() => cell.HorizontalAlignment));
                        var vertical = ConvertOfficeValue(SafeObject(() => cell.VerticalAlignment));
                        cells.Add(new ExcelCellState(
                            address,
                            value,
                            formula,
                            bold,
                            italic,
                            fill,
                            numberFormat,
                            horizontal,
                            vertical));

                        if (SafeBool(() => cell.MergeCells))
                        {
                            dynamic area = cell.MergeArea;
                            try { merges.Add(SafeString(() => area.Address[false, false])); }
                            finally { Release(area); }
                        }
                    }
                    finally { Release(cell); }
                }

                totalCells += cells.Count;
                sheets.Add(new ExcelSheetState(
                    sheetName,
                    visibility,
                    cells.OrderBy(x => x.Address, StringComparer.Ordinal).ToArray(),
                    merges.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                    hiddenRows.ToArray(),
                    hiddenColumns.ToArray()));
            }
            finally
            {
                Release(used);
                Release(sheet);
            }
        }

        var basis = new
        {
            sessionId,
            name,
            fullName,
            saved,
            activeSheetName,
            selectionAddress,
            sheets
        };
        return new ExcelLiveSnapshot(
            sessionId,
            name,
            fullName,
            saved,
            activeSheetName,
            selectionAddress,
            sheets,
            OfficeHostSafety.StableToken(basis));
    }

    private static WordLiveSnapshot SnapshotWordInternal(dynamic app, dynamic document)
    {
        var sessionId = SessionIdForWord(app, document);
        var name = SafeString(() => document.Name);
        var fullName = SafeString(() => document.FullName, name);
        var saved = SafeBool(() => document.Saved);
        var selectionStart = 0;
        var selectionEnd = 0;
        var selectionText = "";
        try
        {
            if (SessionIdForWord(app, app.ActiveDocument) == sessionId)
            {
                dynamic selection = app.Selection;
                dynamic range = selection.Range;
                try
                {
                    selectionStart = Convert.ToInt32(range.Start, CultureInfo.InvariantCulture);
                    selectionEnd = Convert.ToInt32(range.End, CultureInfo.InvariantCulture);
                    selectionText = CleanWordText(SafeString(() => range.Text));
                }
                finally { Release(range); Release(selection); }
            }
        }
        catch { }

        var paragraphs = ReadWordParagraphs(document);
        var tables = ReadWordTables(document);
        var sections = ReadWordSections(document);
        var headers = ReadWordParts(document, header: true);
        var footers = ReadWordParts(document, header: false);

        var basis = new
        {
            sessionId,
            name,
            fullName,
            saved,
            selectionStart,
            selectionEnd,
            selectionText,
            paragraphs,
            tables,
            sections,
            headers,
            footers
        };
        return new WordLiveSnapshot(
            sessionId,
            name,
            fullName,
            saved,
            selectionStart,
            selectionEnd,
            selectionText,
            paragraphs,
            tables,
            sections,
            headers,
            footers,
            OfficeHostSafety.StableToken(basis));
    }

    private static IReadOnlyList<WordSpellingEvidence> ReadWordSpellingEvidence(
        dynamic app,
        dynamic document)
    {
        var results = new List<WordSpellingEvidence>();
        dynamic? errors = null;
        try
        {
            errors = document.SpellingErrors;
            var count = Math.Min(
                Convert.ToInt32(errors.Count, CultureInfo.InvariantCulture),
                MaxWordLanguageItems);
            for (var i = 1; i <= count; i++)
            {
                dynamic? range = null;
                try
                {
                    range = errors[i];
                    var raw = SafeString(() => range.Text);
                    var text = CleanWordText(raw);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var suggestions = ReadSpellingSuggestions(app, text);
                    var start = Convert.ToInt32(range.Start, CultureInfo.InvariantCulture);
                    var end = Convert.ToInt32(range.End, CultureInfo.InvariantCulture);
                    results.Add(new WordSpellingEvidence(
                        start,
                        Math.Max(0, end - start),
                        text,
                        suggestions,
                        NativeEvidence: true));
                }
                catch
                {
                    // One inaccessible proofing range must not invalidate all other native evidence.
                }
                finally
                {
                    Release(range);
                }
            }
        }
        finally
        {
            Release(errors);
        }

        return results;
    }

    private static IReadOnlyList<string> ReadSpellingSuggestions(dynamic app, string text)
    {
        dynamic? suggestions = null;
        try
        {
            suggestions = app.GetSpellingSuggestions(text);
            var count = Math.Min(
                Convert.ToInt32(suggestions.Count, CultureInfo.InvariantCulture),
                12);
            var result = new List<string>();
            for (var i = 1; i <= count; i++)
            {
                dynamic? suggestion = null;
                try
                {
                    suggestion = suggestions[i];
                    var value = SafeString(() => suggestion.Name).Trim();
                    if (value.Length > 0 && !result.Contains(value, StringComparer.OrdinalIgnoreCase))
                        result.Add(value);
                }
                catch
                {
                }
                finally
                {
                    Release(suggestion);
                }
            }
            return result;
        }
        catch
        {
            return [];
        }
        finally
        {
            Release(suggestions);
        }
    }

    private static IReadOnlyList<WordGrammarEvidence> ReadWordGrammarEvidence(dynamic document)
    {
        var results = new List<WordGrammarEvidence>();
        dynamic? errors = null;
        try
        {
            errors = document.GrammaticalErrors;
            var count = Math.Min(
                Convert.ToInt32(errors.Count, CultureInfo.InvariantCulture),
                MaxWordLanguageItems);
            for (var i = 1; i <= count; i++)
            {
                dynamic? range = null;
                try
                {
                    range = errors[i];
                    var text = CleanWordText(SafeString(() => range.Text));
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var start = Convert.ToInt32(range.Start, CultureInfo.InvariantCulture);
                    var end = Convert.ToInt32(range.End, CultureInfo.InvariantCulture);
                    results.Add(new WordGrammarEvidence(
                        start,
                        Math.Max(0, end - start),
                        text,
                        "Word native grammar candidate.",
                        NativeEvidence: true));
                }
                catch
                {
                }
                finally
                {
                    Release(range);
                }
            }
        }
        finally
        {
            Release(errors);
        }

        return results;
    }

    private static IReadOnlyList<WordLegalCitationEvidence> ReadWordCitationEvidence(dynamic document)
    {
        dynamic? content = null;
        try
        {
            content = document.Content;
            var raw = SafeString(() => content.Text);
            return LegalCitationPattern.Matches(raw)
                .Cast<Match>()
                .Take(MaxWordLanguageItems)
                .Select(match => new WordLegalCitationEvidence(
                    match.Value.Trim(),
                    match.Groups["id"].Value.ToUpperInvariant(),
                    match.Index,
                    match.Length,
                    NativeDocumentEvidence: true))
                .ToArray();
        }
        finally
        {
            Release(content);
        }
    }

    private static IReadOnlyList<WordParagraphState> ReadWordParagraphs(dynamic document)
    {
        var count = Convert.ToInt32(document.Paragraphs.Count, CultureInfo.InvariantCulture);
        if (count > MaxWordParagraphs)
            throw new OfficeHostFaultException("snapshot_too_large", $"Word live snapshot exceeds {MaxWordParagraphs} paragraphs.");

        var result = new List<WordParagraphState>();
        var runBudget = MaxWordRuns;
        for (var i = 1; i <= count; i++)
        {
            dynamic paragraph = document.Paragraphs[i];
            dynamic range = paragraph.Range;
            try
            {
                var text = CleanWordText(SafeString(() => range.Text));
                var style = StyleName(range);
                var runs = ReadWordRuns(document, range, ref runBudget);
                result.Add(new WordParagraphState(i - 1, text, style, runs));
            }
            finally { Release(range); Release(paragraph); }
        }
        return result;
    }

    private static IReadOnlyList<WordRunState> ReadWordRuns(
        dynamic document,
        dynamic paragraphRange,
        ref int budget)
    {
        var start = Convert.ToInt32(paragraphRange.Start, CultureInfo.InvariantCulture);
        var end = Math.Max(start, Convert.ToInt32(paragraphRange.End, CultureInfo.InvariantCulture) - 1);
        var runs = new List<WordRunState>();
        for (var position = start; position < end;)
        {
            if (budget-- <= 0)
                throw new OfficeHostFaultException("snapshot_too_large", $"Word live snapshot exceeds {MaxWordRuns} formatting runs.");

            dynamic character = document.Range(position, position + 1);
            try
            {
                var bold = SafeBool(() => character.Font.Bold);
                var italic = SafeBool(() => character.Font.Italic);
                var underline = SafeLong(() => character.Font.Underline) is long u && u != 0;
                var style = StyleName(character);
                var text = CleanWordText(SafeString(() => character.Text));

                if (runs.Count > 0)
                {
                    var previous = runs[^1];
                    if (previous.Bold == bold
                        && previous.Italic == italic
                        && previous.Underline == underline
                        && previous.Style == style)
                    {
                        runs[^1] = previous with { Text = previous.Text + text };
                    }
                    else
                    {
                        runs.Add(new WordRunState(runs.Count, text, style, bold, italic, underline));
                    }
                }
                else
                {
                    runs.Add(new WordRunState(0, text, style, bold, italic, underline));
                }
            }
            finally { Release(character); }
            position++;
        }
        return runs;
    }

    private static IReadOnlyList<WordTableState> ReadWordTables(dynamic document)
    {
        var count = Convert.ToInt32(document.Tables.Count, CultureInfo.InvariantCulture);
        if (count > MaxWordTables)
            throw new OfficeHostFaultException("snapshot_too_large", $"Word live snapshot exceeds {MaxWordTables} tables.");

        var tables = new List<WordTableState>();
        for (var t = 1; t <= count; t++)
        {
            dynamic table = document.Tables[t];
            try
            {
                var rows = new List<IReadOnlyList<string>>();
                var rowCount = Convert.ToInt32(table.Rows.Count, CultureInfo.InvariantCulture);
                for (var r = 1; r <= rowCount; r++)
                {
                    dynamic row = table.Rows[r];
                    try
                    {
                        var cells = new List<string>();
                        var cellCount = Convert.ToInt32(row.Cells.Count, CultureInfo.InvariantCulture);
                        for (var c = 1; c <= cellCount; c++)
                        {
                            dynamic cell = row.Cells[c];
                            dynamic range = cell.Range;
                            try { cells.Add(CleanWordText(SafeString(() => range.Text))); }
                            finally { Release(range); Release(cell); }
                        }
                        rows.Add(cells);
                    }
                    finally { Release(row); }
                }
                tables.Add(new WordTableState(t - 1, rows));
            }
            finally { Release(table); }
        }
        return tables;
    }

    private static IReadOnlyList<WordSectionState> ReadWordSections(dynamic document)
    {
        var count = Convert.ToInt32(document.Sections.Count, CultureInfo.InvariantCulture);
        var sections = new List<WordSectionState>();
        for (var i = 1; i <= count; i++)
        {
            dynamic section = document.Sections[i];
            dynamic setup = section.PageSetup;
            try
            {
                sections.Add(new WordSectionState(
                    i - 1,
                    SafeFloat(() => setup.PageWidth),
                    SafeFloat(() => setup.PageHeight),
                    SafeFloat(() => setup.TopMargin),
                    SafeFloat(() => setup.RightMargin),
                    SafeFloat(() => setup.BottomMargin),
                    SafeFloat(() => setup.LeftMargin)));
            }
            finally { Release(setup); Release(section); }
        }
        return sections;
    }

    private static IReadOnlyList<WordPartState> ReadWordParts(dynamic document, bool header)
    {
        var parts = new List<WordPartState>();
        var sectionCount = Convert.ToInt32(document.Sections.Count, CultureInfo.InvariantCulture);
        var types = new (int Index, string Name)[] { (1, "Default"), (2, "First"), (3, "Even") };
        for (var s = 1; s <= sectionCount; s++)
        {
            dynamic section = document.Sections[s];
            try
            {
                dynamic collection = header ? section.Headers : section.Footers;
                try
                {
                    foreach (var type in types)
                    {
                        dynamic part = collection[type.Index];
                        try
                        {
                            if (!SafeBool(() => part.Exists)) continue;
                            dynamic range = part.Range;
                            try
                            {
                                var paragraphStates = new[]
                                {
                                    new WordParagraphState(
                                        0,
                                        CleanWordText(SafeString(() => range.Text)),
                                        StyleName(range),
                                        new WordRunState[]
                                        {
                                            new WordRunState(
                                                0,
                                                CleanWordText(SafeString(() => range.Text)),
                                                StyleName(range),
                                                SafeBool(() => range.Font.Bold),
                                                SafeBool(() => range.Font.Italic),
                                                SafeLong(() => range.Font.Underline) is long u && u != 0)
                                        })
                                };
                                parts.Add(new WordPartState(
                                    $"section:{s - 1}:{(header ? "header" : "footer")}:{type.Name}",
                                    paragraphStates));
                            }
                            finally { Release(range); }
                        }
                        finally { Release(part); }
                    }
                }
                finally { Release(collection); }
            }
            finally { Release(section); }
        }
        return parts.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
    }

    private static void CopyWordDocument(dynamic source, dynamic copy)
    {
        dynamic sourceContent = source.Content;
        dynamic targetContent = copy.Content;
        try { targetContent.FormattedText = sourceContent.FormattedText; }
        finally { Release(targetContent); Release(sourceContent); }

        var sectionCount = Convert.ToInt32(Math.Min(source.Sections.Count, copy.Sections.Count), CultureInfo.InvariantCulture);
        for (var i = 1; i <= sectionCount; i++)
        {
            dynamic sourceSection = source.Sections[i];
            dynamic targetSection = copy.Sections[i];
            dynamic sourceSetup = sourceSection.PageSetup;
            dynamic targetSetup = targetSection.PageSetup;
            try
            {
                targetSetup.PageWidth = sourceSetup.PageWidth;
                targetSetup.PageHeight = sourceSetup.PageHeight;
                targetSetup.TopMargin = sourceSetup.TopMargin;
                targetSetup.RightMargin = sourceSetup.RightMargin;
                targetSetup.BottomMargin = sourceSetup.BottomMargin;
                targetSetup.LeftMargin = sourceSetup.LeftMargin;

                foreach (var type in new[] { 1, 2, 3 })
                {
                    CopyHeaderFooter(sourceSection.Headers, targetSection.Headers, type);
                    CopyHeaderFooter(sourceSection.Footers, targetSection.Footers, type);
                }
            }
            finally
            {
                Release(targetSetup);
                Release(sourceSetup);
                Release(targetSection);
                Release(sourceSection);
            }
        }
    }

    private static void CopyHeaderFooter(dynamic sourceCollection, dynamic targetCollection, int index)
    {
        dynamic source = sourceCollection[index];
        dynamic target = targetCollection[index];
        try
        {
            if (!SafeBool(() => source.Exists)) return;
            target.Exists = true;
            target.LinkToPrevious = false;
            target.Range.FormattedText = source.Range.FormattedText;
        }
        finally { Release(target); Release(source); }
    }

    private static (dynamic App, dynamic Workbook) FindExcel(string sessionId)
    {
        dynamic? app = TryActive("Excel.Application")
            ?? throw new OfficeHostFaultException("office_unavailable", "No running Excel instance was found.");
        try
        {
            var count = Convert.ToInt32(app.Workbooks.Count, CultureInfo.InvariantCulture);
            for (var i = 1; i <= count; i++)
            {
                dynamic workbook = app.Workbooks[i];
                if (SessionIdForExcel(app, workbook) == sessionId)
                    return (app, workbook);
                Release(workbook);
            }
            throw new OfficeHostFaultException("session_not_found", "Excel workbook session is no longer available.");
        }
        catch
        {
            Release(app);
            throw;
        }
    }

    private static (dynamic App, dynamic Document) FindWord(string sessionId)
    {
        dynamic? app = TryActive("Word.Application")
            ?? throw new OfficeHostFaultException("office_unavailable", "No running Word instance was found.");
        try
        {
            var count = Convert.ToInt32(app.Documents.Count, CultureInfo.InvariantCulture);
            for (var i = 1; i <= count; i++)
            {
                dynamic document = app.Documents[i];
                if (SessionIdForWord(app, document) == sessionId)
                    return (app, document);
                Release(document);
            }
            throw new OfficeHostFaultException("session_not_found", "Word document session is no longer available.");
        }
        catch
        {
            Release(app);
            throw;
        }
    }

    private static string SessionIdForExcel(dynamic app, dynamic workbook)
        => OfficeHostSafety.StableSessionId(
            "excel",
            SafeString(() => app.Hwnd),
            SafeString(() => workbook.Name),
            SafeString(() => workbook.FullName));

    private static string SessionIdForWord(dynamic app, dynamic document)
        => OfficeHostSafety.StableSessionId(
            "word",
            SafeString(() => app.Hwnd),
            SafeString(() => document.Name),
            SafeString(() => document.FullName));

    private static dynamic? TryActive(string progId)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var type = Type.GetTypeFromProgID(progId, throwOnError: false);
        if (type is null) return null;
        var clsid = type.GUID;
        var hr = GetActiveObject(ref clsid, IntPtr.Zero, out var value);
        if (hr < 0 || value is null) return null;
        return value;
    }

    private static string ValidateSingleCellAddress(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var value = address.Trim().Replace("$", "", StringComparison.Ordinal).ToUpperInvariant();
        if (value.Contains(':', StringComparison.Ordinal)
            || value.Length is < 2 or > 16
            || value.Any(c => !(char.IsAsciiLetterOrDigit(c))))
            throw new OfficeHostFaultException("invalid_request", "Excel patch addresses must be single A1-style cells.");
        return value;
    }

    private static string ExcelVisibility(int value)
        => value switch { -1 => "Visible", 0 => "Hidden", 2 => "VeryHidden", _ => value.ToString(CultureInfo.InvariantCulture) };

    private static string StyleName(dynamic range)
    {
        try
        {
            dynamic style = range.Style;
            try { return SafeString(() => style.NameLocal, SafeString(() => style.Name)); }
            finally { Release(style); }
        }
        catch { return ""; }
    }

    private static string CleanWordText(string value)
        => value.TrimEnd((char)13, (char)7);

    private static string ConvertOfficeValue(object? value)
        => value switch
        {
            null => "",
            string text => text,
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            float number => number.ToString("R", CultureInfo.InvariantCulture),
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            bool boolean => boolean ? "TRUE" : "FALSE",
            DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };

    private static object? SafeObject(Func<object?> read)
    {
        try { return read(); } catch { return null; }
    }

    private static string SafeString(Func<object?> read, string fallback = "")
        => ConvertOfficeValue(SafeObject(read)) is { } value && value.Length > 0 ? value : fallback;

    private static bool SafeBool(Func<object?> read)
    {
        var value = SafeObject(read);
        if (value is bool b) return b;
        try { return Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0; } catch { return false; }
    }

    private static long? SafeLong(Func<object?> read)
    {
        try { return Convert.ToInt64(SafeObject(read), CultureInfo.InvariantCulture); } catch { return null; }
    }

    private static float SafeFloat(Func<object?> read)
    {
        try { return Convert.ToSingle(SafeObject(read), CultureInfo.InvariantCulture); } catch { return 0; }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.FinalReleaseComObject(value); } catch { }
        }
    }

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(
        ref Guid rclsid,
        IntPtr reserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object? ppunk);
}

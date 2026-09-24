using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using H2AgentLab.OfficeProtocol;

namespace H2AgentLab.OfficeHost;

public sealed class ComOfficeBackend : IOfficeBackend, IOfficeCaptureBackend, IExcelRangeReadBackend, IDisposable
{
    private const int MaxExcelCells = 5_000;
    private const int MaxWordParagraphs = 2_000;
    private const int MaxWordRuns = 5_000;
    private const int MaxWordTables = 200;
    private const int MaxWordLanguageItems = 200;
    private static readonly Regex LegalCitationPattern = new(
        @"\b(?:Luật|Nghị định|Thông tư|Quyết định)\s+(?:số\s+)?(?<id>[0-9]+(?:/[0-9]{4})?/[A-ZĐ0-9-]+(?:-[A-ZĐ0-9]+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Guid ExcelWorkbookEventsIid = new("00024412-0000-0000-C000-000000000046");
    private const int ExcelWorkbookSheetChangeDispId = 0x0000061C;

    private readonly OfficeWindowCatalog _catalog;
    private readonly ConcurrentDictionary<string, long> _excelRevisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExcelWorkbookEventSubscription> _excelWorkbookSubscriptions = new(StringComparer.Ordinal);
    private long _excelTrackingGeneration;

    private delegate void ExcelWorkbookSheetChangeHandler(object sheet, object target);
    private sealed record ExcelWorkbookEventSubscription(
        object Workbook,
        ExcelWorkbookSheetChangeHandler Handler,
        long Generation);

    public ComOfficeBackend(IOfficeWindowProbe? probe = null) => _catalog = new(probe ?? new OfficeNativeWindowProbe());

    public void Dispose()
    {
        lock (_excelWorkbookSubscriptions)
        {
            foreach (var subscription in _excelWorkbookSubscriptions.Values)
            {
                try
                {
                    ComEventsHelper.Remove(
                        subscription.Workbook,
                        ExcelWorkbookEventsIid,
                        ExcelWorkbookSheetChangeDispId,
                        subscription.Handler);
                }
                catch { }
            }
            _excelWorkbookSubscriptions.Clear();
        }
        _catalog.Dispose();
    }
    public OfficeCaptureResult Capture(OfficeCaptureRequest request) => _catalog.Capture(request);

    public ExcelDiscovery DiscoverExcel()
    {
        var views = _catalog.Refresh("excel");
        return new(views.Select(ExcelInfo).ToArray(), _catalog.ActiveSession(views)) { Report=_catalog.LastReport };
    }

    public ExcelLiveSnapshot SnapshotExcel(string sessionId)
    {
        var bound = _catalog.Require("excel", sessionId);
        dynamic app = bound.App; dynamic workbook = bound.Document;
        try { return SnapshotExcelInternal(bound); }
        finally { /* Native references remain owned by the bounded STA catalog. */ }
    }

    public ExcelRangeReadPage ReadExcelRange(ExcelReadRangeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bound = _catalog.Require("excel", request.SessionId);
        _catalog.ValidateCurrent(bound, noEffect: true);
        dynamic workbook = bound.Document;
        dynamic app = bound.App;

        ExcelRangeBounds requested;
        IReadOnlyList<string> fields;
        int pageSize;
        ExcelRangePagePlan plan;
        try
        {
            requested = ExcelRangeReadRules.ParseRange(request.Range);
            fields = ExcelRangeReadFields.Normalize(request.Fields);
            pageSize = ExcelRangeReadLimits.NormalizePageSize(request.PageSize);
            plan = ExcelRangeReadRules.PlanPage(requested, pageSize, request.Cursor);
        }
        catch (Exception ex) when (ex is ArgumentException)
        {
            throw new OfficeHostFaultException("invalid_request", ex.Message, true);
        }

        dynamic? sheet = null;
        try
        {
            try { sheet = workbook.Worksheets[request.SheetName]; }
            catch { throw new OfficeHostFaultException("sheet_not_found", $"Excel sheet '{request.SheetName}' is not available.", true); }

            var needsContentTracking = !plan.Complete
                || !string.IsNullOrWhiteSpace(request.Cursor)
                || !string.IsNullOrWhiteSpace(request.ContentVersion);
            var trackingGeneration = needsContentTracking
                ? EnsureExcelWorkbookChangeTracking(bound.SessionId, workbook)
                : 0L;
            if (needsContentTracking && !SafeBool(() => app.EnableEvents))
                throw new OfficeHostFaultException(
                    "content_tracking_unavailable",
                    "Excel events are disabled; paged range reads cannot safely detect workbook edits.",
                    true);

            var sheetName = SafeString(() => sheet.Name, request.SheetName);
            var name = SafeString(() => workbook.Name);
            var fullName = SafeString(() => workbook.FullName, name);
            var saved = SafeBool(() => workbook.Saved);
            var extent = ReadExcelExtent(sheet);
            var contentVersion = ExcelContentVersion(
                bound.SessionId,
                name,
                fullName,
                saved,
                sheetName,
                extent,
                trackingGeneration);
            if (!string.IsNullOrWhiteSpace(request.Cursor) && string.IsNullOrWhiteSpace(request.ContentVersion))
                throw new OfficeHostFaultException("invalid_request", "A continuation cursor requires content_version.", true);
            if (!string.IsNullOrWhiteSpace(request.ContentVersion)
                && !string.Equals(request.ContentVersion, contentVersion, StringComparison.Ordinal))
                throw new OfficeHostFaultException("stale_content", "Excel content changed after the previous page; restart the range read.", true);

            var started = Environment.TickCount64;
            var wantValue = fields.Contains(ExcelRangeReadFields.Value, StringComparer.Ordinal);
            var wantFormula = fields.Contains(ExcelRangeReadFields.Formula, StringComparer.Ordinal);
            var wantFormat = fields.Contains(ExcelRangeReadFields.Format, StringComparer.Ordinal);
            var wantMerge = fields.Contains(ExcelRangeReadFields.Merge, StringComparer.Ordinal);
            var wantHidden = fields.Contains(ExcelRangeReadFields.Hidden, StringComparer.Ordinal);
            dynamic? pageRange = null;
            object? values = null;
            object? formulas = null;
            var cells = new List<ExcelRangeCellState>(plan.CellCount);
            var merges = new HashSet<string>(StringComparer.Ordinal);
            var hiddenRows = new List<int>();
            var hiddenColumns = new List<int>();
            try
            {
                pageRange = sheet.Range[plan.Bounds.Address];
                if (wantValue) values = SafeObject(() => pageRange.Value2);
                if (wantFormula) formulas = SafeObject(() => pageRange.Formula);

                for (var rowOffset = 0; rowOffset < plan.Bounds.RowCount; rowOffset++)
                for (var columnOffset = 0; columnOffset < plan.Bounds.ColumnCount; columnOffset++)
                {
                    var row = plan.Bounds.StartRow + rowOffset;
                    var column = plan.Bounds.StartColumn + columnOffset;
                    var address = ExcelRangeReadRules.CellAddress(row, column);
                    bool? bold = null;
                    bool? italic = null;
                    long? fill = null;
                    string? numberFormat = null;
                    string? horizontal = null;
                    string? vertical = null;
                    dynamic? cell = null;
                    try
                    {
                        if (wantFormat || wantMerge)
                        {
                            cell = sheet.Cells[row, column];
                            if (wantFormat)
                            {
                                bold = SafeBool(() => cell.Font.Bold);
                                italic = SafeBool(() => cell.Font.Italic);
                                fill = SafeLong(() => cell.Interior.Color);
                                numberFormat = SafeString(() => cell.NumberFormat);
                                horizontal = ConvertOfficeValue(SafeObject(() => cell.HorizontalAlignment));
                                vertical = ConvertOfficeValue(SafeObject(() => cell.VerticalAlignment));
                            }
                            if (wantMerge && SafeBool(() => cell.MergeCells))
                            {
                                dynamic? area = null;
                                try
                                {
                                    area = cell.MergeArea;
                                    var mergeAddress = SafeString(() => area.Address[false, false]);
                                    if (mergeAddress.Length > 0) merges.Add(mergeAddress);
                                }
                                finally { Release(area); }
                            }
                        }

                        cells.Add(new ExcelRangeCellState(
                            address,
                            wantValue ? ConvertOfficeValue(MatrixValue(values, rowOffset, columnOffset)) : null,
                            wantFormula ? ConvertOfficeValue(MatrixValue(formulas, rowOffset, columnOffset)) : null,
                            bold,
                            italic,
                            fill,
                            numberFormat,
                            horizontal,
                            vertical));
                    }
                    finally { Release(cell); }
                }

                if (wantHidden)
                {
                    for (var row = plan.Bounds.StartRow; row <= plan.Bounds.EndRow; row++)
                    {
                        dynamic? rowRange = null;
                        try
                        {
                            rowRange = sheet.Rows[row];
                            if (SafeBool(() => rowRange.Hidden)) hiddenRows.Add(row);
                        }
                        finally { Release(rowRange); }
                    }
                    for (var column = plan.Bounds.StartColumn; column <= plan.Bounds.EndColumn; column++)
                    {
                        dynamic? columnRange = null;
                        try
                        {
                            columnRange = sheet.Columns[column];
                            if (SafeBool(() => columnRange.Hidden)) hiddenColumns.Add(column);
                        }
                        finally { Release(columnRange); }
                    }
                }
            }
            finally { Release(pageRange); }

            // Re-read only lightweight workbook/extent metadata after the page. The version is
            // deliberately independent of ActiveSheet/Selection. For paged reads a Workbook.SheetChange
            // COM event advances the session revision for direct user/external-link edits as well as
            // H2 writes; a post-page token check rejects changes that race the current page.
            var afterExtent = ReadExcelExtent(sheet);
            var afterVersion = ExcelContentVersion(
                bound.SessionId,
                SafeString(() => workbook.Name, name),
                SafeString(() => workbook.FullName, fullName),
                SafeBool(() => workbook.Saved),
                sheetName,
                afterExtent,
                trackingGeneration);
            if (!string.Equals(contentVersion, afterVersion, StringComparison.Ordinal))
                throw new OfficeHostFaultException("stale_content", "Excel content metadata changed while reading this page; restart the range read.", true);

            _catalog.ValidateCurrent(bound, noEffect: true);
            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                cells,
                MergedRanges = merges,
                hiddenRows,
                hiddenColumns
            }).Length;
            return new ExcelRangeReadPage(
                bound.SessionId,
                name,
                fullName,
                saved,
                sheetName,
                requested.Address,
                plan.Bounds.Address,
                fields,
                cells,
                merges.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                hiddenRows.ToArray(),
                hiddenColumns.ToArray(),
                afterExtent,
                contentVersion,
                plan.NextCursor,
                plan.Complete,
                "LiveDocument",
                new ExcelRangeReadMetrics(plan.CellCount, payloadBytes, Math.Max(0, Environment.TickCount64 - started)))
            { NativeIdentity = bound.Identity };
        }
        finally { Release(sheet); }
    }

    public ExcelPatchResult PatchExcel(ExcelPatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        OfficeHostSafety.RequirePermission(request.PermissionGranted);
        var countError = ExcelPatchLimits.ValidationError(request.Cells?.Count ?? -1);
        if (request.Cells is null || countError is not null)
            throw new OfficeHostFaultException(ExcelPatchLimits.ErrorCode, countError ?? "Excel patch cells are required.");
        var bound = _catalog.Require("excel", request.SessionId);
        dynamic app = bound.App; dynamic workbook = bound.Document;
        try
        {
            var before = SnapshotExcelInternal(bound, beforeMutation: true);
            OfficeHostSafety.RequireState(request.StateToken, before.StateToken);

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

                BumpExcelRevision(request.SessionId);
                var after = SnapshotExcelInternal(bound);
                return new ExcelPatchResult(
                    before,
                    after,
                    changed.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            }
            finally { Release(sheet); }
        }
        finally { /* Native references remain owned by the bounded STA catalog. */ }
    }

    public ExcelLiveSnapshot RecalculateExcel(ExcelRecalculateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        OfficeHostSafety.RequirePermission(request.PermissionGranted);
        var bound = _catalog.Require("excel", request.SessionId);
        dynamic app = bound.App; dynamic workbook = bound.Document;
        try
        {
            var before = SnapshotExcelInternal(bound, beforeMutation: true);
            OfficeHostSafety.RequireState(request.StateToken, before.StateToken);
            app.Calculate();
            BumpExcelRevision(request.SessionId);
            return SnapshotExcelInternal(bound);
        }
        finally { /* Native references remain owned by the bounded STA catalog. */ }
    }

    public OfficeSaveCopyResult SaveExcelCopy(OfficeSaveCopyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        OfficeHostSafety.RequirePermission(request.PermissionGranted);
        var bound = _catalog.Require("excel", request.SessionId);
        dynamic app = bound.App; dynamic workbook = bound.Document;
        try
        {
            var snapshot = SnapshotExcelInternal(bound, beforeMutation: true);
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
        finally { /* Native references remain owned by the bounded STA catalog. */ }
    }

    public WordDiscovery DiscoverWord()
    {
        var views = _catalog.Refresh("word");
        return new(views.Select(v => new WordDocumentInfo(v.SessionId,v.Name,v.FullName,v.Saved,0,0,"","")
            { NativeIdentity=v.Identity }).ToArray(), _catalog.ActiveSession(views)) { Report=_catalog.LastReport };
    }

    public WordLiveSnapshot SnapshotWord(string sessionId)
    {
        var bound = _catalog.Require("word", sessionId);
        dynamic app = bound.App; dynamic document = bound.Document;
        try { return SnapshotWordInternal(bound); }
        finally { /* Native references remain owned by the bounded STA catalog. */ }
    }

    public WordPatchResult PatchWord(WordPatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        OfficeHostSafety.RequirePermission(request.PermissionGranted);
        var bound = _catalog.Require("word", request.SessionId);
        dynamic app = bound.App; dynamic document = bound.Document;
        try
        {
            var before = SnapshotWordInternal(bound, beforeMutation: true);
            OfficeHostSafety.RequireState(request.StateToken, before.StateToken);
            if (WordPatchRules.ValidationError(before, request.Paragraphs) is { } problem)
                throw new OfficeHostFaultException("word_patch_rejected", problem);

            // Inspect every target before the first write. Text replacement is restricted to
            // plain body paragraphs; fields, tables and embedded content need narrower tools.
            foreach (var patch in request.Paragraphs.Where(p => p.Text is not null))
            {
                dynamic paragraph = document.Paragraphs[patch.ParagraphIndex + 1];
                dynamic range = paragraph.Range;
                try
                {
                    var text = (string?)range.Text ?? "";
                    if (Convert.ToInt32(range.Tables.Count) != 0 || Convert.ToInt32(range.Fields.Count) != 0
                        || Convert.ToInt32(range.InlineShapes.Count) != 0 || Convert.ToInt32(range.ContentControls.Count) != 0
                        || text.Contains('\f'))
                        throw new OfficeHostFaultException("word_patch_rejected", "Text replacement cannot remove a table, field, embedded object, content control or section break. Select a plain body paragraph.");
                }
                finally { Release(range); Release(paragraph); }
            }
            var changed = new List<int>();
            // Work backwards so a multiline replacement cannot shift later input indexes.
            foreach (var patch in request.Paragraphs.OrderByDescending(p => p.ParagraphIndex))
            {
                dynamic paragraph = document.Paragraphs[patch.ParagraphIndex + 1];
                dynamic range = paragraph.Range;
                dynamic? contentRange = null;
                try
                {
                    var start = Convert.ToInt32(range.Start, CultureInfo.InvariantCulture);
                    var end = Convert.ToInt32(range.End, CultureInfo.InvariantCulture);
                    contentRange = document.Range(start, Math.Max(start, end - 1));
                    if (patch.Text is not null) contentRange.Text = WordPatchRules.NormalizeText(patch.Text).Replace('\n', '\r');
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

            var after = SnapshotWordInternal(bound);
            return new WordPatchResult(
                before,
                after,
                changed.Distinct().OrderBy(x => x).ToArray());
        }
        finally { /* Native references remain owned by the bounded STA catalog. */ }
    }

    public WordLanguageEvidenceResult InspectWordLanguage(WordLanguageEvidenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bound = _catalog.Require("word", request.SessionId);
        dynamic app = bound.App; dynamic document = bound.Document;
        try
        {
            var snapshot = SnapshotWordInternal(bound, beforeMutation: true);
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
            // Native source references are borrowed from the catalog, not owned here.
        }
    }

    public OfficeSaveCopyResult SaveWordCopy(OfficeSaveCopyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        OfficeHostSafety.RequirePermission(request.PermissionGranted);
        var bound = _catalog.Require("word", request.SessionId);
        dynamic app = bound.App; dynamic document = bound.Document;
        dynamic? copy = null;
        try
        {
            var snapshot = SnapshotWordInternal(bound, beforeMutation: true);
            OfficeHostSafety.RequireState(request.StateToken, snapshot.StateToken);
            var destination = OfficeHostSafety.ValidateCopyDestination(request.DestinationPath, snapshot.FullName);

            copy = app.Documents.Add();
            CopyWordDocument(document, copy);
            var extension = Path.GetExtension(destination).ToLowerInvariant();
            if (extension == ".pdf")
                copy.ExportAsFixedFormat(destination, 17);
            else
                copy.SaveAs2(destination);

            // Word holds an exclusive file lock while the newly saved copy is open.
            // Close only this method's temporary document before hashing the saved bytes.
            copy.Close(false);
            Release(copy); copy = null;
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
            // Native source references are borrowed from the catalog, not owned here.
        }
    }

    private ExcelWorkbookInfo ExcelInfo(OfficeViewLease bound)
    {
        dynamic workbook = bound.Document;
        dynamic view = bound.View;
        var name = SafeString(() => workbook.Name, bound.Name);
        var fullName = SafeString(() => workbook.FullName, bound.FullName);
        var saved = SafeBool(() => workbook.Saved);
        var activeSheetName = "";
        var selectionAddress = "";
        dynamic? activeSheet = null;
        dynamic? selection = null;
        try
        {
            try
            {
                activeSheet = view.ActiveSheet;
                activeSheetName = SafeString(() => activeSheet.Name);
            }
            catch { }
            try
            {
                selection = view.Selection;
                selectionAddress = SafeString(() => selection.Address[false, false]);
            }
            catch { }
        }
        finally
        {
            Release(selection);
            Release(activeSheet);
        }
        return new ExcelWorkbookInfo(
            bound.SessionId,
            name,
            fullName,
            saved,
            activeSheetName,
            selectionAddress,
            "")
        { NativeIdentity = bound.Identity };
    }

    private ExcelSheetExtent ReadExcelExtent(dynamic sheet)
    {
        dynamic? used = null;
        try
        {
            used = sheet.UsedRange;
            var firstRow = Convert.ToInt32(used.Row, CultureInfo.InvariantCulture);
            var firstColumn = Convert.ToInt32(used.Column, CultureInfo.InvariantCulture);
            var rowCount = Math.Max(1, Convert.ToInt32(used.Rows.Count, CultureInfo.InvariantCulture));
            var columnCount = Math.Max(1, Convert.ToInt32(used.Columns.Count, CultureInfo.InvariantCulture));
            var bounds = new ExcelRangeBounds(
                firstRow,
                firstColumn,
                checked(firstRow + rowCount - 1),
                checked(firstColumn + columnCount - 1));
            return new ExcelSheetExtent(
                bounds.Address,
                bounds.StartRow,
                bounds.StartColumn,
                bounds.EndRow,
                bounds.EndColumn,
                bounds.CellCount);
        }
        finally { Release(used); }
    }

    private long EnsureExcelWorkbookChangeTracking(string sessionId, object workbook)
    {
        lock (_excelWorkbookSubscriptions)
        {
            if (_excelWorkbookSubscriptions.TryGetValue(sessionId, out var existing))
                return existing.Generation;

            ExcelWorkbookSheetChangeHandler handler = (_, _) => BumpExcelRevision(sessionId);
            try
            {
                ComEventsHelper.Combine(
                    workbook,
                    ExcelWorkbookEventsIid,
                    ExcelWorkbookSheetChangeDispId,
                    handler);
            }
            catch (Exception ex)
            {
                throw new OfficeHostFaultException(
                    "content_tracking_unavailable",
                    $"Excel Workbook.SheetChange tracking could not be attached ({ex.GetType().Name}).",
                    true);
            }

            var generation = Interlocked.Increment(ref _excelTrackingGeneration);
            _excelWorkbookSubscriptions[sessionId] = new ExcelWorkbookEventSubscription(
                workbook,
                handler,
                generation);
            return generation;
        }
    }

    private string ExcelContentVersion(
        string sessionId,
        string name,
        string fullName,
        bool saved,
        string sheetName,
        ExcelSheetExtent extent,
        long trackingGeneration)
    {
        _excelRevisions.TryGetValue(sessionId, out var revision);
        return OfficeHostSafety.StableToken(new
        {
            sessionId,
            name,
            fullName,
            saved,
            sheetName,
            extent.Address,
            extent.FirstRow,
            extent.FirstColumn,
            extent.LastRow,
            extent.LastColumn,
            trackingGeneration,
            revision
        });
    }

    private void BumpExcelRevision(string sessionId)
        => _excelRevisions.AddOrUpdate(
            sessionId,
            1,
            static (_, revision) => checked(revision + 1));

    private static object? MatrixValue(object? value, int rowOffset, int columnOffset)
    {
        if (value is not Array array || array.Rank != 2)
            return rowOffset == 0 && columnOffset == 0 ? value : null;
        var row = array.GetLowerBound(0) + rowOffset;
        var column = array.GetLowerBound(1) + columnOffset;
        return row <= array.GetUpperBound(0) && column <= array.GetUpperBound(1)
            ? array.GetValue(row, column)
            : null;
    }

    private ExcelLiveSnapshot SnapshotExcelInternal(OfficeViewLease bound, bool beforeMutation = false)
    {
        _catalog.ValidateCurrent(bound, beforeMutation);
        dynamic app = bound.App; dynamic workbook = bound.Document; dynamic view = bound.View;
        var sessionId = bound.SessionId;
        var name = SafeString(() => workbook.Name);
        var fullName = SafeString(() => workbook.FullName, name);
        var saved = SafeBool(() => workbook.Saved);
        var activeSheetName = "";
        var selectionAddress = "";
        try
        {
            dynamic activeSheet = view.ActiveSheet;
            try { activeSheetName = SafeString(() => activeSheet.Name); }
            finally { Release(activeSheet); }
        }
        catch { }
        try
        {
            dynamic selection = view.Selection;
            try { selectionAddress = SafeString(() => selection.Address[false, false]); }
            finally { Release(selection); }
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
        var observed = new ExcelLiveSnapshot(
            sessionId,
            name,
            fullName,
            saved,
            activeSheetName,
            selectionAddress,
            sheets,
            OfficeHostSafety.StableToken(basis)) { NativeIdentity = bound.Identity };
        _catalog.ValidateCurrent(bound, beforeMutation);
        return observed;
    }

    private WordLiveSnapshot SnapshotWordInternal(OfficeViewLease bound, bool beforeMutation = false)
    {
        _catalog.ValidateCurrent(bound, beforeMutation);
        dynamic app = bound.App; dynamic document = bound.Document; dynamic view = bound.View;
        var sessionId = bound.SessionId;
        var name = SafeString(() => document.Name);
        var fullName = SafeString(() => document.FullName, name);
        var saved = SafeBool(() => document.Saved);
        var selectionStart = 0;
        var selectionEnd = 0;
        var selectionText = "";
        try
        {
            {
                dynamic selection = view.Selection;
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
        var observed = new WordLiveSnapshot(
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
            OfficeHostSafety.StableToken(basis)) { NativeIdentity = bound.Identity };
        _catalog.ValidateCurrent(bound, beforeMutation);
        return observed;
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
        if (start == end)
        {
            // Empty paragraphs still have formatting on their paragraph mark. Keep it in
            // the snapshot so inserting into a blank document has a checkable expectation.
            runs.Add(new WordRunState(0, "", StyleName(paragraphRange), SafeBool(() => paragraphRange.Font.Bold),
                SafeBool(() => paragraphRange.Font.Italic), SafeLong(() => paragraphRange.Font.Underline) is long u && u != 0));
        }
        int[]? boundaries = null;
        try { boundaries = WordRunBoundaries.TryRead((string)paragraphRange.WordOpenXML, CleanWordText((string)paragraphRange.Text)); }
        catch (System.Runtime.InteropServices.COMException) { }
        if (boundaries?.Sum() != end - start) boundaries = null;
        var segment = 0;
        for (var position = start; position < end;)
        {
            if (budget-- <= 0)
                throw new OfficeHostFaultException("snapshot_too_large", $"Word live snapshot exceeds {MaxWordRuns} formatting runs.");

            var length = boundaries is null ? 1 : boundaries[segment++];
            dynamic character = document.Range(position, position + length);
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
            position += length;
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
            // Different COM property accesses can share one RCW (e.g. ActiveSheet and
            // Worksheets[index]). Releasing every reference invalidates an outer operation.
            try { Marshal.ReleaseComObject(value); } catch { }
        }
    }

}

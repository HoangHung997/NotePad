using System.Reflection;
using System.Text.Json;
using H2AgentLab.Integration;
using H2AgentLab.Office;
using H2AgentLab.OfficeHost;
using H2AgentLab.OfficeProtocol;
using H2AgentLab.Tools;

internal static class H2ExcelRangeReadTests
{
    public static void Run(Action<string, Action> test)
    {
        test("AR-021 E1 range planner bounds every page and preserves rectangular order", () =>
        {
            var requested = ExcelRangeReadRules.ParseRange("A1:XFD2");
            var first = ExcelRangeReadRules.PlanPage(requested, ExcelRangeReadLimits.MaxPageCells, null);
            Check(first.CellCount == ExcelRangeReadLimits.MaxPageCells, "First wide page exceeded or under-used the page bound.");
            Check(first.Bounds.Address == "A1:SR1", "Unexpected first wide page: " + first.Bounds.Address);
            Check(!first.Complete && !string.IsNullOrWhiteSpace(first.NextCursor), "Wide page did not expose continuation.");

            var second = ExcelRangeReadRules.PlanPage(requested, ExcelRangeReadLimits.MaxPageCells, first.NextCursor);
            Check(second.Bounds.StartRow == 1 && second.Bounds.StartColumn == 513, "Continuation skipped or repeated columns.");
            Check(second.CellCount <= ExcelRangeReadLimits.MaxPageCells, "Continuation exceeded page bound.");

            Check(ExcelRangeReadRules.ParseRange("$A$1:$B$2").Address == "A1:B2", "Absolute A1 normalization failed.");
            Expect<ArgumentException>(() => ExcelRangeReadRules.ParseRange("Sheet1!A1:B2"));
            Expect<ArgumentOutOfRangeException>(() => ExcelRangeReadLimits.NormalizePageSize(ExcelRangeReadLimits.MaxPageCells + 1));
        });

        test("AR-021 E2 fixture pages more than 5000 cells without a total-workbook cutoff", () =>
        {
            var backend = new FixtureOfficeBackend(extraExcelRows: 6_000);
            var reader = (IExcelRangeReadBackend)backend;
            var session = backend.DiscoverExcel().ActiveSessionId!;
            var cursor = (string?)null;
            var version = (string?)null;
            var addresses = new List<string>();
            var pages = 0;
            do
            {
                var page = reader.ReadExcelRange(new(
                    session,
                    "Data",
                    "A1:A6002",
                    [ExcelRangeReadFields.Value, ExcelRangeReadFields.Formula],
                    ExcelRangeReadLimits.MaxPageCells,
                    cursor,
                    version));
                pages++;
                Check(page.Metrics.CellsRead == page.Cells.Count, "Measured cells differ from returned page cells.");
                Check(page.Metrics.CellsRead is > 0 and <= ExcelRangeReadLimits.MaxPageCells, "Page cell budget was not enforced.");
                Check(page.Metrics.PayloadBytes > 0 && page.Metrics.PayloadBytes < OfficeProtocolConstants.MaxMessageBytes, "Page payload is unbounded.");
                if (version is not null) Check(page.ContentVersion == version, "Unchanged content version drifted between pages.");
                version = page.ContentVersion;
                addresses.AddRange(page.Cells.Select(x => x.Address));
                cursor = page.NextCursor;
                if (page.Complete) Check(cursor is null, "Complete page still exposed a cursor.");
                Check(pages < 100, "Range pagination failed to converge.");
            }
            while (cursor is not null);

            Check(addresses.Count == 6002, "Large range did not return every requested cell.");
            Check(addresses.Distinct(StringComparer.Ordinal).Count() == 6002, "Large range duplicated cells across pages.");
            Check(addresses[0] == "A1" && addresses[^1] == "A6002", "Large range lost row-major boundaries.");
            Check(pages > 1, "Large range incorrectly collapsed into one result.");
        });

        test("AR-021 E2 sparse UsedRange metadata does not cause a small range to materialize the sheet", () =>
        {
            var backend = new FixtureOfficeBackend(sparseExcelLastRow: 6_001);
            var reader = (IExcelRangeReadBackend)backend;
            var session = backend.DiscoverExcel().ActiveSessionId!;
            var page = reader.ReadExcelRange(new(
                session,
                "Data",
                "A1:B2",
                [ExcelRangeReadFields.Value],
                32));
            Check(page.UsedExtent.Address == "A1:Z6001", "Sparse used extent metadata is incorrect.");
            Check(page.UsedExtent.CellCount > 100_000, "Sparse fixture did not establish a large used extent.");
            Check(page.Cells.Count == 4 && page.Metrics.CellsRead == 4, "Small range read expanded to the sparse UsedRange.");
            Check(page.Complete && page.NextCursor is null, "Small range should complete in one page.");
        });

        test("AR-021 E2 content version ignores selection but rejects changed content between pages", () =>
        {
            var backend = new FixtureOfficeBackend(extraExcelRows: 900);
            var reader = (IExcelRangeReadBackend)backend;
            var session = backend.DiscoverExcel().ActiveSessionId!;
            var first = reader.ReadExcelRange(new(session, "Data", "A1:A902", [ExcelRangeReadFields.Value], 128));
            Check(first.NextCursor is not null, "Fixture did not create a multi-page read.");

            backend.MoveExcelSelectionForFixture("B2");
            var second = reader.ReadExcelRange(new(
                session,
                "Data",
                "A1:A902",
                [ExcelRangeReadFields.Value],
                128,
                first.NextCursor,
                first.ContentVersion));
            Check(second.ContentVersion == first.ContentVersion, "Selection-only change invalidated content version.");

            backend.SetExcelValueForFixture("A700", "CHANGED-BETWEEN-PAGES");
            OfficeHostFaultException? stale = null;
            try
            {
                _ = reader.ReadExcelRange(new(
                    session,
                    "Data",
                    "A1:A902",
                    [ExcelRangeReadFields.Value],
                    128,
                    second.NextCursor,
                    first.ContentVersion));
            }
            catch (OfficeHostFaultException ex) { stale = ex; }
            Check(stale?.Code == "stale_content" && stale.NoEffect, "Changed content was silently mixed into the old cursor.");
        });

        test("AR-021 E2 range fields keep formatting and structure on demand", () =>
        {
            var backend = new FixtureOfficeBackend();
            var reader = (IExcelRangeReadBackend)backend;
            var session = backend.DiscoverExcel().ActiveSessionId!;

            var formulas = reader.ReadExcelRange(new(session, "Data", "B1", [ExcelRangeReadFields.Formula], 8));
            var b1Formula = formulas.Cells.Single();
            Check(b1Formula.Formula == "=A1" && b1Formula.Value is null && b1Formula.Italic is null, "Formula-only read materialized unrelated fields.");

            var styles = reader.ReadExcelRange(new(session, "Data", "B1", [ExcelRangeReadFields.Format], 8));
            var b1Style = styles.Cells.Single();
            Check(b1Style.Formula is null && b1Style.Value is null && b1Style.Italic == true, "Formatting was not isolated on demand.");

            var structure = reader.ReadExcelRange(new(
                session,
                "Data",
                "A1:C3",
                [ExcelRangeReadFields.Merge, ExcelRangeReadFields.Hidden],
                32));
            Check(structure.MergedRanges.SequenceEqual(["A1:B1"]), "Merged range evidence is missing.");
            Check(structure.HiddenRows.SequenceEqual([3]) && structure.HiddenColumns.SequenceEqual([3]), "Hidden row/column evidence is missing.");
        });

        test("AR-021 E2 production Office runtime dispatches bounded range capability without legacy snapshot", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "h2-ar021-runtime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var client = new RangeClient(root);
                var type = typeof(H2ProductionAgentAdapter).Assembly.GetType("H2AgentLab.Integration.H2OfficeRuntimeTools", true)!;
                using var office = (IDisposable)Activator.CreateInstance(type, [new Func<bool>(() => true), root, null, null])!;
                type.GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(office, client);
                var registry = new ToolRegistry();
                type.GetMethod("Register")!.Invoke(office, [registry]);
                Check(registry.TryGet("excel.read_range", out var descriptor), "Production registry lost excel.read_range.");

                var parameters = descriptor.CallableSchema.GetProperty("function").GetProperty("parameters");
                var required = parameters.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToArray();
                Check(required.Contains("session_id") && required.Contains("sheet_name") && required.Contains("range"), "Range schema does not require resource/sheet/range.");
                var pageSchema = parameters.GetProperty("properties").GetProperty("page_size");
                Check(pageSchema.GetProperty("maximum").GetInt32() == ExcelRangeReadLimits.MaxPageCells, "Tool schema drifted from protocol page limit.");

                var call = new ToolCall(
                    "ar021-range",
                    "excel.read_range",
                    JsonSerializer.SerializeToElement(new
                    {
                        session_id = client.SessionId,
                        sheet_name = "Data",
                        range = "A1:B2",
                        fields = new[] { "value", "formula" },
                        page_size = 2
                    }));
                var raw = descriptor.Executor.ExecuteAsync(call, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                using var json = JsonDocument.Parse(raw);
                Check(json.RootElement.GetProperty("PageRange").GetString() == "A1:B1", "Production runtime returned the wrong page.");
                Check(client.RangeReads == 1 && client.SnapshotReads == 0, "Production range read fell back to a full workbook snapshot.");
                Check(client.Discoveries >= 2, "Production target was not revalidated after the range read.");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        });
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class RangeClient : IOfficeSessionClient, IExcelRangeReadClient
    {
        private readonly string _path;
        public RangeClient(string root) => _path = Path.Combine(root, "RangeFixture.xlsx");
        public string SessionId { get; } = "ar021-session";
        public string InstanceIdentity => "ar021-range-client";
        public int Discoveries { get; private set; }
        public int SnapshotReads { get; private set; }
        public int RangeReads { get; private set; }

        public Task<ExcelDiscovery> DiscoverExcelAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Discoveries++;
            return Task.FromResult(new ExcelDiscovery(
                [new ExcelWorkbookInfo(SessionId, "RangeFixture.xlsx", _path, false, "Data", "A1", "")],
                SessionId));
        }

        public Task<ExcelLiveSnapshot> SnapshotExcelAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SnapshotReads++;
            var cell = new ExcelCellState("A1", "LEGACY-SNAPSHOT-SHOULD-NOT-BE-USED", "", false, false, null, "General", "General", "General");
            var sheet = new ExcelSheetState("Data", "Visible", [cell], [], [], []);
            return Task.FromResult(new ExcelLiveSnapshot(
                sessionId,
                "RangeFixture.xlsx",
                _path,
                false,
                "Data",
                "A1",
                [sheet],
                "legacy-state"));
        }

        public Task<ExcelRangeReadPage> ReadExcelRangeAsync(ExcelReadRangeRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RangeReads++;
            var requested = ExcelRangeReadRules.ParseRange(request.Range);
            var plan = ExcelRangeReadRules.PlanPage(requested, request.PageSize, request.Cursor);
            var fields = ExcelRangeReadFields.Normalize(request.Fields);
            var cells = new List<ExcelRangeCellState>();
            for (var row = plan.Bounds.StartRow; row <= plan.Bounds.EndRow; row++)
            for (var column = plan.Bounds.StartColumn; column <= plan.Bounds.EndColumn; column++)
            {
                var address = ExcelRangeReadRules.CellAddress(row, column);
                cells.Add(new(
                    address,
                    fields.Contains(ExcelRangeReadFields.Value) ? "RANGE-" + address : null,
                    fields.Contains(ExcelRangeReadFields.Formula) ? "" : null,
                    null, null, null, null, null, null));
            }
            return Task.FromResult(new ExcelRangeReadPage(
                request.SessionId,
                "RangeFixture.xlsx",
                _path,
                false,
                request.SheetName,
                requested.Address,
                plan.Bounds.Address,
                fields,
                cells,
                [],
                [],
                [],
                new("A1:B2", 1, 1, 2, 2, 4),
                "range-content-v1",
                plan.NextCursor,
                plan.Complete,
                "LiveDocument",
                new(plan.CellCount, 128, 0)));
        }

        public Task<ExcelPatchResult> PatchExcelAsync(ExcelPatchRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<ExcelLiveSnapshot> RecalculateExcelAsync(ExcelRecalculateRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<OfficeSaveCopyResult> SaveExcelCopyAsync(OfficeSaveCopyRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<WordDiscovery> DiscoverWordAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<WordLiveSnapshot> SnapshotWordAsync(string sessionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<WordPatchResult> PatchWordAsync(WordPatchRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<WordLanguageEvidenceResult> InspectWordLanguageAsync(WordLanguageEvidenceRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<OfficeSaveCopyResult> SaveWordCopyAsync(OfficeSaveCopyRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public void Dispose() { }
    }
}

using H2AgentLab.Documents;

namespace H2AgentLab.Verification;

public sealed record ExcelExpectedCell(
    string SheetName,
    string Address,
    ClosedCellSnapshot Expected)
{
    public string Key => SheetName + "!" + Address;
}

public sealed record ExcelVerificationExpectation
{
    public ExcelVerificationExpectation(
        IEnumerable<ExcelExpectedCell>? expectedCells = null,
        bool preserveOtherCells = true,
        bool preserveSheetStructure = true,
        bool preserveMerges = true,
        bool preserveHiddenState = true)
    {
        var cells = (expectedCells ?? Array.Empty<ExcelExpectedCell>()).ToArray();
        if (cells.Any(x => x is null))
            throw new ArgumentException("Expected Excel cells cannot contain null.", nameof(expectedCells));
        foreach (var cell in cells)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(cell.SheetName);
            ArgumentException.ThrowIfNullOrWhiteSpace(cell.Address);
            ArgumentNullException.ThrowIfNull(cell.Expected);
            if (!string.Equals(cell.Address.Trim(), cell.Expected.Address, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"Expected cell address '{cell.Address}' does not match snapshot address '{cell.Expected.Address}'.",
                    nameof(expectedCells));
        }

        var duplicate = cells
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Duplicate expected Excel cell '{duplicate.Key}'.", nameof(expectedCells));

        ExpectedCells = Array.AsReadOnly(cells);
        PreserveOtherCells = preserveOtherCells;
        PreserveSheetStructure = preserveSheetStructure;
        PreserveMerges = preserveMerges;
        PreserveHiddenState = preserveHiddenState;
    }

    public IReadOnlyList<ExcelExpectedCell> ExpectedCells { get; }
    public bool PreserveOtherCells { get; }
    public bool PreserveSheetStructure { get; }
    public bool PreserveMerges { get; }
    public bool PreserveHiddenState { get; }
}

public static class ExcelVerifier
{
    public const string VerifierId = "excel-closed";
    public const string TargetCellsCriterionId = "excel.targets";
    public const string PreserveCellsCriterionId = "excel.preserve-cells";
    public const string StructureCriterionId = "excel.structure";

    public static VerificationReport Verify(
        ClosedWorkbookSnapshot before,
        ClosedWorkbookSnapshot after,
        ExcelVerificationExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(expectation);

        var beforeSheets = before.Sheets.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var afterSheets = after.Sheets.ToDictionary(x => x.Name, StringComparer.Ordinal);

        var targetFailures = new List<string>();
        var targetEvidence = new List<string>();
        foreach (var expected in expectation.ExpectedCells.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (!afterSheets.TryGetValue(expected.SheetName, out var sheet))
            {
                targetFailures.Add($"{expected.Key}: sheet missing");
                continue;
            }

            var actual = sheet.Cells.SingleOrDefault(x => x.Address == expected.Address);
            if (actual is null)
            {
                targetFailures.Add($"{expected.Key}: cell missing");
                continue;
            }

            targetEvidence.Add($"excel:{expected.Key}");
            if (actual != expected.Expected)
                targetFailures.Add($"{expected.Key}: expected {Describe(expected.Expected)}, actual {Describe(actual)}");
        }

        var targetResult = Result(
            TargetCellsCriterionId,
            targetFailures.Count == 0,
            targetEvidence,
            targetFailures.Count == 0 ? null : string.Join("; ", targetFailures));

        var expectedKeys = expectation.ExpectedCells.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var preservationFailures = new List<string>();
        var preservationEvidence = new List<string>();

        if (expectation.PreserveOtherCells)
        {
            foreach (var sheetName in beforeSheets.Keys.Union(afterSheets.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
            {
                beforeSheets.TryGetValue(sheetName, out var beforeSheet);
                afterSheets.TryGetValue(sheetName, out var afterSheet);
                var beforeCells = (beforeSheet?.Cells ?? Array.Empty<ClosedCellSnapshot>())
                    .ToDictionary(x => x.Address, StringComparer.Ordinal);
                var afterCells = (afterSheet?.Cells ?? Array.Empty<ClosedCellSnapshot>())
                    .ToDictionary(x => x.Address, StringComparer.Ordinal);

                foreach (var address in beforeCells.Keys.Union(afterCells.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var key = sheetName + "!" + address;
                    if (expectedKeys.Contains(key)) continue;
                    beforeCells.TryGetValue(address, out var beforeCell);
                    afterCells.TryGetValue(address, out var afterCell);
                    if (beforeCell != afterCell)
                        preservationFailures.Add($"{key}: changed outside expected target set");
                    else if (beforeCell is not null)
                        preservationEvidence.Add($"preserved:{key}");
                }
            }
        }

        var preservationResult = Result(
            PreserveCellsCriterionId,
            preservationFailures.Count == 0,
            preservationEvidence.Take(64),
            preservationFailures.Count == 0 ? null : string.Join("; ", preservationFailures.Take(20)));

        var structureFailures = new List<string>();
        var structureEvidence = new List<string>();
        if (expectation.PreserveSheetStructure)
        {
            var beforeNames = before.Sheets.Select(x => x.Name).ToArray();
            var afterNames = after.Sheets.Select(x => x.Name).ToArray();
            if (!beforeNames.SequenceEqual(afterNames, StringComparer.Ordinal))
                structureFailures.Add("Sheet order/name set changed.");
        }

        foreach (var name in beforeSheets.Keys.Intersect(afterSheets.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            var left = beforeSheets[name];
            var right = afterSheets[name];

            if (expectation.PreserveMerges
                && !left.MergedRanges.SequenceEqual(right.MergedRanges, StringComparer.Ordinal))
                structureFailures.Add($"{name}: merged ranges changed.");

            if (expectation.PreserveHiddenState
                && (left.State != right.State
                    || !left.HiddenRows.SequenceEqual(right.HiddenRows)
                    || !left.HiddenColumns.SequenceEqual(right.HiddenColumns, StringComparer.Ordinal)))
                structureFailures.Add($"{name}: sheet/row/column hidden state changed.");

            structureEvidence.Add($"excel-structure:{name}");
        }

        var structureResult = Result(
            StructureCriterionId,
            structureFailures.Count == 0,
            structureEvidence,
            structureFailures.Count == 0 ? null : string.Join("; ", structureFailures));

        return new VerificationReport(
            VerifierId,
            [targetResult, preservationResult, structureResult],
            reportEvidenceIds:
            [
                "before-sha256:" + before.Sha256,
                "after-sha256:" + after.Sha256
            ]);
    }

    private static VerificationCriterionResult Result(
        string id,
        bool passed,
        IEnumerable<string> evidence,
        string? failure)
        => passed
            ? new VerificationCriterionResult(id, VerificationCriterionStatus.Passed, evidence)
            : new VerificationCriterionResult(
                id,
                VerificationCriterionStatus.Failed,
                evidence,
                new VerificationFailure(id, failure ?? "Excel verification failed.", evidence));

    private static string Describe(ClosedCellSnapshot cell)
        => $"value={cell.RawValue}, formula={cell.Formula ?? "<none>"}, style={cell.StyleIndex}, "
            + $"bold={cell.Bold}, italic={cell.Italic}, fill={cell.FillForeground ?? "<none>"}";
}

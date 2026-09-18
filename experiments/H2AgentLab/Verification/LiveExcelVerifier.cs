using System.Text.Json;
using H2AgentLab.OfficeProtocol;

namespace H2AgentLab.Verification;

public sealed record LiveExcelExpectedCell(
    string SheetName,
    string Address,
    ExcelCellState Expected);

public sealed record LiveExcelVerificationExpectation(
    IReadOnlyList<LiveExcelExpectedCell> ExpectedCells,
    bool PreserveOtherCells = true,
    bool PreserveStructure = true);

public static class LiveExcelVerifier
{
    public const string VerifierId = "excel-live";
    public const string TargetCriterionId = "excel-live.targets";
    public const string PreserveCriterionId = "excel-live.preserve";

    public static VerificationReport Verify(
        ExcelLiveSnapshot before,
        ExcelLiveSnapshot after,
        LiveExcelVerificationExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(expectation);

        var failures = new List<string>();
        var evidence = new List<string>();
        var afterSheets = after.Sheets.ToDictionary(x => x.Name, StringComparer.Ordinal);
        foreach (var expected in expectation.ExpectedCells)
        {
            if (!afterSheets.TryGetValue(expected.SheetName, out var sheet))
            {
                failures.Add($"{expected.SheetName}: sheet missing");
                continue;
            }
            var actual = sheet.Cells.SingleOrDefault(x => x.Address == expected.Address);
            if (actual is null)
            {
                failures.Add($"{expected.SheetName}!{expected.Address}: cell missing");
                continue;
            }
            evidence.Add($"excel-live:{expected.SheetName}!{expected.Address}");
            if (!Same(actual, expected.Expected))
                failures.Add($"{expected.SheetName}!{expected.Address}: target state mismatch");
        }

        var target = Result(
            TargetCriterionId,
            failures.Count == 0,
            evidence,
            failures.Count == 0 ? null : string.Join("; ", failures));

        var preserveFailures = new List<string>();
        if (expectation.PreserveOtherCells)
        {
            var targetKeys = expectation.ExpectedCells
                .Select(x => x.SheetName + "!" + x.Address)
                .ToHashSet(StringComparer.Ordinal);

            var beforeMap = Flatten(before);
            var afterMap = Flatten(after);
            foreach (var key in beforeMap.Keys.Union(afterMap.Keys, StringComparer.Ordinal))
            {
                if (targetKeys.Contains(key)) continue;
                beforeMap.TryGetValue(key, out var left);
                afterMap.TryGetValue(key, out var right);
                if (!SameNullable(left, right))
                    preserveFailures.Add($"{key}: changed outside target set");
            }
        }

        if (expectation.PreserveStructure)
        {
            if (!before.Sheets.Select(x => x.Name).SequenceEqual(after.Sheets.Select(x => x.Name), StringComparer.Ordinal))
                preserveFailures.Add("Sheet order/name changed.");
            var beforeSheets = before.Sheets.ToDictionary(x => x.Name, StringComparer.Ordinal);
            foreach (var sheet in after.Sheets)
            {
                if (!beforeSheets.TryGetValue(sheet.Name, out var left))
                    continue;
                if (left.Visibility != sheet.Visibility
                    || !left.MergedRanges.SequenceEqual(sheet.MergedRanges, StringComparer.Ordinal)
                    || !left.HiddenRows.SequenceEqual(sheet.HiddenRows)
                    || !left.HiddenColumns.SequenceEqual(sheet.HiddenColumns))
                    preserveFailures.Add($"{sheet.Name}: visibility/merge/hidden state changed.");
            }
        }

        var preserve = Result(
            PreserveCriterionId,
            preserveFailures.Count == 0,
            [],
            preserveFailures.Count == 0 ? null : string.Join("; ", preserveFailures.Take(20)));

        return new VerificationReport(
            VerifierId,
            [target, preserve],
            ["before-state:" + before.StateToken, "after-state:" + after.StateToken]);
    }

    private static Dictionary<string, ExcelCellState> Flatten(ExcelLiveSnapshot snapshot)
        => snapshot.Sheets
            .SelectMany(sheet => sheet.Cells.Select(cell => new { Key = sheet.Name + "!" + cell.Address, Cell = cell }))
            .ToDictionary(x => x.Key, x => x.Cell, StringComparer.Ordinal);

    private static bool Same(ExcelCellState left, ExcelCellState right)
        => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    private static bool SameNullable(ExcelCellState? left, ExcelCellState? right)
        => left is null ? right is null : right is not null && Same(left, right);

    private static VerificationCriterionResult Result(
        string id,
        bool passed,
        IEnumerable<string> evidence,
        string? failure)
        => passed
            ? new(id, VerificationCriterionStatus.Passed, evidence)
            : new(
                id,
                VerificationCriterionStatus.Failed,
                evidence,
                new VerificationFailure(id, failure ?? "Live Excel verification failed.", evidence));
}

namespace H2AgentLab.Office;

/// <summary>
/// Machine-verifiable manual acceptance for a workstation with Microsoft Office installed.
/// Prepare at least one open unsaved Excel workbook and one open unsaved Word document, then run
/// this command against the non-fixture OfficeHost. No mutation is performed by this probe.
/// </summary>
public static class V2OfficeLiveAcceptance
{
    public static async Task<int> Run(
        string outputDirectory,
        string hostExecutable)
    {
        var root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);
        var lines = new List<string>();
        var failed = 0;

        void Check(bool condition, string message)
        {
            if (!condition)
            {
                failed++;
                lines.Add("FAIL " + message);
            }
            else
            {
                lines.Add("PASS " + message);
            }
        }

        try
        {
            using var client = new OfficeHostClient(
                hostExecutable,
                fixtureMode: false,
                defaultTimeout: TimeSpan.FromSeconds(15));

            var ping = await client.PingAsync();
            Check(ping.StaThread && !ping.FixtureMode, "real OfficeHost runs as separate STA COM host");

            var excel = await client.DiscoverExcelAsync();
            Check(excel.ActiveSessionId is not null, "active Excel workbook is discoverable");
            if (excel.ActiveSessionId is not null)
            {
                var snapshot = await client.SnapshotExcelAsync(excel.ActiveSessionId);
                Check(!snapshot.Saved, "active Excel workbook exposes unsaved state");
                Check(snapshot.StateToken.Length == 64, "active Excel workbook has machine-verifiable state token");
                Check(snapshot.Sheets.Count > 0, "active Excel workbook structured sheets are readable");
                Check(!string.IsNullOrWhiteSpace(snapshot.ActiveSheet), "active Excel sheet is observed");
            }

            var word = await client.DiscoverWordAsync();
            Check(word.ActiveSessionId is not null, "active Word document is discoverable");
            if (word.ActiveSessionId is not null)
            {
                var snapshot = await client.SnapshotWordAsync(word.ActiveSessionId);
                Check(!snapshot.Saved, "active Word document exposes unsaved state");
                Check(snapshot.StateToken.Length == 64, "active Word document has machine-verifiable state token");
                Check(snapshot.Paragraphs.Count > 0, "active Word structured paragraphs are readable");
            }
        }
        catch (Exception ex)
        {
            failed++;
            lines.Add("FAIL live Office acceptance: " + ex.GetType().Name + ": " + ex.Message);
        }

        lines.Add($"RESULT: {lines.Count(x => x.StartsWith("PASS ", StringComparison.Ordinal))} passed, {failed} failed.");
        var report = Path.Combine(root, "v2-office-live-acceptance.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }
}

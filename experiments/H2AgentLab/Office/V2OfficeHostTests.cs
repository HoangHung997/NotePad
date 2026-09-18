using System.Text.Json;
using H2AgentLab.OfficeProtocol;
using H2AgentLab.Verification;

namespace H2AgentLab.Office;

public static class V2OfficeHostTests
{
    public static async Task<int> Run(string outputDirectory, string hostExecutable)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new v2 OfficeHost test directory.");
        Directory.CreateDirectory(root);

        hostExecutable = Path.GetFullPath(hostExecutable);
        var lines = new List<string>();
        var failed = 0;

        async Task Test(string name, Func<Task> action)
        {
            try
            {
                await action();
                lines.Add("PASS " + name);
            }
            catch (Exception ex)
            {
                failed++;
                lines.Add("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        await Test("0801 separate OfficeHost process is STA named-pipe protocol with no AI project reference", async () =>
        {
            using var client = new OfficeHostClient(hostExecutable, fixtureMode: true);
            var ping = await client.PingAsync();
            Check(ping.StaThread, "OfficeHost RPC execution is not on an STA thread.");
            Check(ping.FixtureMode, "Fixture process did not report fixture mode.");
            Check(ping.ProtocolVersion == OfficeProtocolConstants.Version, "OfficeHost protocol version mismatch.");
            Check(ping.ProcessId != Environment.ProcessId, "OfficeHost is not a separate process.");

            var repo = FindRepoRoot();
            var project = File.ReadAllText(Path.Combine(repo, "experiments", "H2AgentLab.OfficeHost", "H2AgentLab.OfficeHost.csproj"));
            Check(project.Contains("H2AgentLab.OfficeProtocol", StringComparison.Ordinal), "OfficeHost does not reference the isolated protocol project.");
            Check(!project.Contains("H2Notes.Core", StringComparison.Ordinal)
                && !project.Contains("../H2AgentLab/H2AgentLab.csproj", StringComparison.Ordinal),
                "OfficeHost helper project gained model/H2 Agent Lab dependencies.");
        });

        await Test("0802 timeout and crash stop host; next call starts a fresh process", async () =>
        {
            using var client = new OfficeHostClient(
                hostExecutable,
                fixtureMode: true,
                defaultTimeout: TimeSpan.FromSeconds(3));

            _ = await client.PingAsync();
            var firstPid = client.ProcessId;
            Check(client.StartCount == 1 && firstPid is not null, "OfficeHost did not start exactly once.");

            try
            {
                _ = await client.FixtureDelayAsync(2_000, TimeSpan.FromMilliseconds(100));
                throw new InvalidOperationException("OfficeHost timeout fixture unexpectedly completed.");
            }
            catch (TimeoutException)
            {
            }

            var afterTimeout = await client.PingAsync();
            Check(client.StartCount == 2 && afterTimeout.ProcessId != firstPid, "OfficeHost did not restart after timeout.");

            try
            {
                _ = await client.FixtureCrashAsync();
                throw new InvalidOperationException("OfficeHost crash fixture unexpectedly returned.");
            }
            catch (IOException)
            {
            }

            var afterCrash = await client.PingAsync();
            Check(client.StartCount == 3 && afterCrash.ProcessId != afterTimeout.ProcessId, "OfficeHost did not restart after crash.");
        });

        await Test("0803 Excel discovery returns stable active unsaved workbook session", async () =>
        {
            using var client = new OfficeHostClient(hostExecutable, fixtureMode: true);
            var first = await client.DiscoverExcelAsync();
            var second = await client.DiscoverExcelAsync();

            Check(first.Workbooks.Count == 1 && second.Workbooks.Count == 1, "Fixture Excel workbook discovery count is wrong.");
            Check(first.ActiveSessionId == first.Workbooks[0].SessionId, "Active Excel session was not identified.");
            Check(first.Workbooks[0].SessionId == second.Workbooks[0].SessionId, "Excel session ID is not stable.");
            Check(!first.Workbooks[0].Saved, "Fixture Excel workbook must represent unsaved state.");
        });

        await Test("0804 Excel live snapshot observes unsaved values formulas styles selection and hidden state", async () =>
        {
            using var client = new OfficeHostClient(hostExecutable, fixtureMode: true);
            var discovery = await client.DiscoverExcelAsync();
            var snapshot = await client.SnapshotExcelAsync(discovery.ActiveSessionId!);
            var data = snapshot.Sheets.Single(x => x.Name == "Data");
            var a1 = data.Cells.Single(x => x.Address == "A1");
            var b1 = data.Cells.Single(x => x.Address == "B1");

            Check(!snapshot.Saved && snapshot.SelectionAddress == "A1", "Excel snapshot lost unsaved/selection state.");
            Check(a1.Value == "UNSAVED-EXCEL" && a1.Bold, "Excel snapshot did not observe unsaved styled A1.");
            Check(b1.Formula == "=A1" && b1.Italic, "Excel snapshot did not observe formula/style state.");
            Check(data.MergedRanges.Contains("A1:B1", StringComparer.Ordinal)
                && data.HiddenRows.Contains(3)
                && data.HiddenColumns.Contains(3),
                "Excel snapshot lost merge/hidden state.");
            Check(snapshot.StateToken.Length == 64, "Excel state token is not a SHA-256 identity.");
        });

        await Test("0805 Excel structured patch returns before/after and rejects stale or denied mutation", async () =>
        {
            using var client = new OfficeHostClient(hostExecutable, fixtureMode: true);
            var discovery = await client.DiscoverExcelAsync();
            var before = await client.SnapshotExcelAsync(discovery.ActiveSessionId!);
            var patched = await client.PatchExcelAsync(new ExcelPatchRequest(
                before.SessionId,
                before.StateToken,
                true,
                "Data",
                [new ExcelCellPatch("A1", Value: "PATCHED", Bold: false)]));

            var afterA1 = patched.After.Sheets.Single().Cells.Single(x => x.Address == "A1");
            Check(patched.Before.StateToken == before.StateToken
                && patched.ChangedCells.SequenceEqual(new[] { "A1" })
                && afterA1.Value == "PATCHED"
                && !afterA1.Bold
                && patched.After.StateToken != before.StateToken,
                "Excel patch before/after evidence is incorrect.");

            await ExpectCode(
                "stale_state",
                () => client.PatchExcelAsync(new ExcelPatchRequest(
                    before.SessionId,
                    before.StateToken,
                    true,
                    "Data",
                    [new ExcelCellPatch("A1", Value: "STALE")])));

            await ExpectCode(
                "permission_denied",
                () => client.PatchExcelAsync(new ExcelPatchRequest(
                    patched.After.SessionId,
                    patched.After.StateToken,
                    false,
                    "Data",
                    [new ExcelCellPatch("A1", Value: "DENIED")])));
        });

        await Test("0806 Excel recalc and save-copy preserve original identity and never overwrite", async () =>
        {
            using var client = new OfficeHostClient(hostExecutable, fixtureMode: true);
            var discovery = await client.DiscoverExcelAsync();
            var before = await client.SnapshotExcelAsync(discovery.ActiveSessionId!);
            var patched = await client.PatchExcelAsync(new ExcelPatchRequest(
                before.SessionId,
                before.StateToken,
                true,
                "Data",
                [new ExcelCellPatch("A1", Value: "RECALC")]));
            var recalculated = await client.RecalculateExcelAsync(new ExcelRecalculateRequest(
                patched.After.SessionId,
                patched.After.StateToken,
                true));

            Check(recalculated.Sheets.Single().Cells.Single(x => x.Address == "B1").Value == "RECALC",
                "Excel recalc did not update dependent fixture value.");

            var destination = Path.Combine(root, "excel-copy.fixture");
            var saved = await client.SaveExcelCopyAsync(new OfficeSaveCopyRequest(
                recalculated.SessionId,
                recalculated.StateToken,
                true,
                destination));
            Check(File.Exists(destination)
                && saved.SavedCopySha256.Length == 64
                && saved.DestinationPath == Path.GetFullPath(destination),
                "Excel save-copy evidence is incomplete.");
            Check(!File.Exists(recalculated.FullName), "Fixture original was unexpectedly created/overwritten.");

            await ExpectCode(
                "destination_exists",
                () => client.SaveExcelCopyAsync(new OfficeSaveCopyRequest(
                    recalculated.SessionId,
                    recalculated.StateToken,
                    true,
                    destination)));
        });

        await Test("0807 live Excel verifier validates target and preservation fields", async () =>
        {
            using var client = new OfficeHostClient(hostExecutable, fixtureMode: true);
            var session = (await client.DiscoverExcelAsync()).ActiveSessionId!;
            var before = await client.SnapshotExcelAsync(session);
            var patch = await client.PatchExcelAsync(new ExcelPatchRequest(
                session,
                before.StateToken,
                true,
                "Data",
                [new ExcelCellPatch("A1", Value: "VERIFY-EXCEL")]));
            var expected = patch.After.Sheets.Single().Cells.Single(x => x.Address == "A1");
            var report = LiveExcelVerifier.Verify(
                before,
                patch.After,
                new LiveExcelVerificationExpectation(
                    [new LiveExcelExpectedCell("Data", "A1", expected)]));

            Check(report.Passed, "Expected live Excel mutation did not pass verifier.");

            var data = patch.After.Sheets.Single();
            var badB1 = data.Cells.Single(x => x.Address == "B1") with { Formula = "=A2" };
            var badData = data with
            {
                Cells = data.Cells.Select(x => x.Address == "B1" ? badB1 : x).ToArray()
            };
            var bad = patch.After with { Sheets = [badData] };
            Check(!LiveExcelVerifier.Verify(
                    before,
                    bad,
                    new LiveExcelVerificationExpectation(
                        [new LiveExcelExpectedCell("Data", "A1", expected)]))
                .Passed,
                "Live Excel verifier missed unrelated formula regression.");
        });

        await Test("0808 Excel unsaved-state acceptance fixture is machine-verified across process boundary", async () =>
        {
            using var client = new OfficeHostClient(hostExecutable, fixtureMode: true);
            var discovery = await client.DiscoverExcelAsync();
            var snapshot = await client.SnapshotExcelAsync(discovery.ActiveSessionId!);
            Check(!snapshot.Saved, "Excel acceptance fixture is not unsaved.");
            Check(snapshot.Sheets.SelectMany(x => x.Cells).Any(x => x.Value == "UNSAVED-EXCEL"),
                "Excel acceptance fixture unsaved marker is not observable.");
            var second = await client.SnapshotExcelAsync(snapshot.SessionId);
            Check(second.StateToken == snapshot.StateToken, "Unchanged unsaved Excel snapshot is not deterministic.");
        });

        await Test("0809 Word discovery returns stable active document and selection", async () =>
        {
            using var client = new OfficeHostClient(hostExecutable, fixtureMode: true);
            var first = await client.DiscoverWordAsync();
            var second = await client.DiscoverWordAsync();
            Check(first.Documents.Count == 1
                && first.ActiveSessionId == first.Documents[0].SessionId
                && first.Documents[0].SessionId == second.Documents[0].SessionId,
                "Word discovery/session identity is not stable.");
            Check(!first.Documents[0].Saved
                && first.Documents[0].SelectionText == "UNSAVED-WORD",
                "Word discovery did not observe active unsaved selection.");
        });

        await Test("0810 Word live snapshot observes unsaved paragraphs styles tables sections headers and footers", async () =>
        {
            using var client = new OfficeHostClient(hostExecutable, fixtureMode: true);
            var session = (await client.DiscoverWordAsync()).ActiveSessionId!;
            var snapshot = await client.SnapshotWordAsync(session);
            Check(!snapshot.Saved
                && snapshot.Paragraphs[0].Text == "UNSAVED-WORD"
                && snapshot.Paragraphs[0].Runs[0].Bold,
                "Word unsaved paragraph/run state is missing.");
            Check(snapshot.Tables.Count == 1
                && snapshot.Sections.Count == 1
                && snapshot.Headers.Single().Paragraphs.Single().Text == "Fixture Header"
                && snapshot.Footers.Single().Paragraphs.Single().Text == "Fixture Footer",
                "Word structured snapshot is incomplete.");
        });

        await Test("0811 Word structured text/format patch returns before/after and stale-state protection", async () =>
        {
            using var client = new OfficeHostClient(hostExecutable, fixtureMode: true);
            var session = (await client.DiscoverWordAsync()).ActiveSessionId!;
            var before = await client.SnapshotWordAsync(session);
            var patched = await client.PatchWordAsync(new WordPatchRequest(
                session,
                before.StateToken,
                true,
                [new WordParagraphPatch(0, Text: "PATCHED-WORD", Bold: false, Italic: true)]));

            Check(patched.ChangedParagraphs.SequenceEqual(new[] { 0 })
                && patched.After.Paragraphs[0].Text == "PATCHED-WORD"
                && !patched.After.Paragraphs[0].Runs[0].Bold
                && patched.After.Paragraphs[0].Runs[0].Italic,
                "Word patch before/after state is incorrect.");

            await ExpectCode(
                "stale_state",
                () => client.PatchWordAsync(new WordPatchRequest(
                    session,
                    before.StateToken,
                    true,
                    [new WordParagraphPatch(0, Text: "STALE")])));
        });

        await Test("0812 Word save-copy writes separate copy and refuses overwrite", async () =>
        {
            using var client = new OfficeHostClient(hostExecutable, fixtureMode: true);
            var session = (await client.DiscoverWordAsync()).ActiveSessionId!;
            var snapshot = await client.SnapshotWordAsync(session);
            var destination = Path.Combine(root, "word-copy.fixture");
            var saved = await client.SaveWordCopyAsync(new OfficeSaveCopyRequest(
                session,
                snapshot.StateToken,
                true,
                destination));
            Check(File.Exists(destination)
                && saved.SavedCopySha256.Length == 64
                && !File.Exists(snapshot.FullName),
                "Word save-copy did not preserve original identity.");

            await ExpectCode(
                "destination_exists",
                () => client.SaveWordCopyAsync(new OfficeSaveCopyRequest(
                    session,
                    snapshot.StateToken,
                    true,
                    destination)));
        });

        await Test("0813 live Word verifier validates target paragraph and preservation state", async () =>
        {
            using var client = new OfficeHostClient(hostExecutable, fixtureMode: true);
            var session = (await client.DiscoverWordAsync()).ActiveSessionId!;
            var before = await client.SnapshotWordAsync(session);
            var patched = await client.PatchWordAsync(new WordPatchRequest(
                session,
                before.StateToken,
                true,
                [new WordParagraphPatch(0, Text: "VERIFY-WORD")]));
            var expected = patched.After.Paragraphs[0];
            var expectation = new LiveWordVerificationExpectation(
                [new LiveWordExpectedParagraph(0, expected)]);

            Check(LiveWordVerifier.Verify(before, patched.After, expectation).Passed,
                "Expected live Word mutation did not pass verifier.");

            var badHeader = patched.After.Headers[0] with
            {
                Paragraphs =
                [
                    patched.After.Headers[0].Paragraphs[0] with { Text = "REGRESSED" }
                ]
            };
            var bad = patched.After with { Headers = [badHeader] };
            Check(!LiveWordVerifier.Verify(before, bad, expectation).Passed,
                "Live Word verifier missed header regression.");
        });

        await Test("0814 Word unsaved-state acceptance fixture is machine-verified across process boundary", async () =>
        {
            using var client = new OfficeHostClient(hostExecutable, fixtureMode: true);
            var discovery = await client.DiscoverWordAsync();
            var snapshot = await client.SnapshotWordAsync(discovery.ActiveSessionId!);
            Check(!snapshot.Saved
                && snapshot.Paragraphs.Any(x => x.Text == "UNSAVED-WORD")
                && snapshot.SelectionText == "UNSAVED-WORD",
                "Word acceptance fixture unsaved state is not observable.");
            var second = await client.SnapshotWordAsync(snapshot.SessionId);
            Check(second.StateToken == snapshot.StateToken, "Unchanged unsaved Word snapshot is not deterministic.");
        });

        await Test("0815 OfficeHost safety rejects wrong session stale state user denial and recovers after timeout/crash", async () =>
        {
            using var client = new OfficeHostClient(hostExecutable, fixtureMode: true);
            await ExpectCode("session_not_found", () => client.SnapshotExcelAsync("wrong-session"));
            await ExpectCode("session_not_found", () => client.SnapshotWordAsync("wrong-session"));

            var excelSession = (await client.DiscoverExcelAsync()).ActiveSessionId!;
            var excel = await client.SnapshotExcelAsync(excelSession);
            await ExpectCode(
                "permission_denied",
                () => client.PatchExcelAsync(new ExcelPatchRequest(
                    excel.SessionId,
                    excel.StateToken,
                    false,
                    "Data",
                    [new ExcelCellPatch("A1", Value: "DENIED")])));

            var patched = await client.PatchExcelAsync(new ExcelPatchRequest(
                excel.SessionId,
                excel.StateToken,
                true,
                "Data",
                [new ExcelCellPatch("A1", Value: "FRESH")]));
            await ExpectCode(
                "stale_state",
                () => client.RecalculateExcelAsync(new ExcelRecalculateRequest(
                    excel.SessionId,
                    excel.StateToken,
                    true)));

            try
            {
                _ = await client.FixtureDelayAsync(3_000, TimeSpan.FromMilliseconds(100));
                throw new InvalidOperationException("Timeout safety fixture unexpectedly returned.");
            }
            catch (TimeoutException)
            {
            }
            _ = await client.PingAsync();

            try
            {
                _ = await client.FixtureCrashAsync();
                throw new InvalidOperationException("Crash safety fixture unexpectedly returned.");
            }
            catch (IOException)
            {
            }
            var ping = await client.PingAsync();
            Check(ping.StaThread && client.StartCount >= 3,
                "OfficeHost did not recover to a fresh STA process after timeout/crash.");
            Check(patched.After.StateToken != excel.StateToken, "Safety fixture did not create a stale-state boundary.");
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "v2-office-host-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static async Task ExpectCode<T>(
        string expectedCode,
        Func<Task<T>> action)
    {
        try
        {
            _ = await action();
            throw new InvalidOperationException($"Expected OfficeHost error '{expectedCode}' was not raised.");
        }
        catch (OfficeHostClientException ex) when (ex.Code == expectedCode)
        {
        }
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(current.FullName, "experiments")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root for OfficeHost project boundary test.");
    }
}

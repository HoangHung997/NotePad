using System.Text.Json;
using H2AgentLab.Office;
using H2AgentLab.OfficeProtocol;
using H2AgentLab.Verification;

namespace H2AgentLab.Acceptance;

public static class MbOfficeAcceptanceTests
{
    private sealed record OfficeCase(
        string Id,
        string Requirement,
        string Evidence,
        bool Passed,
        string? Failure);

    public static async Task<int> Run(
        string outputDirectory,
        string hostExecutable)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-110 Office acceptance directory.");
        Directory.CreateDirectory(root);
        hostExecutable = Path.GetFullPath(hostExecutable);

        var cases = new List<OfficeCase>();

        async Task Case(
            string id,
            string requirement,
            string evidence,
            Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
                cases.Add(new OfficeCase(id, requirement, evidence, true, null));
            }
            catch (Exception ex)
            {
                cases.Add(new OfficeCase(
                    id,
                    requirement,
                    evidence,
                    false,
                    ex.GetType().Name + ": " + ex.Message));
            }
        }

        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        await Case(
            "OFFICE-EXCEL-UNSAVED",
            "open Excel unsaved state is observed through OfficeHost",
            "OfficeHost excel.discover + excel.snapshot fixture",
            async () =>
            {
                using var client = new OfficeHostClient(hostExecutable, fixtureMode: true);
                var discovery = await client.DiscoverExcelAsync().ConfigureAwait(false);
                Check(discovery.ActiveSessionId is not null,
                    "Fixture active Excel workbook is missing.");
                var snapshot = await client.SnapshotExcelAsync(discovery.ActiveSessionId!)
                    .ConfigureAwait(false);
                var data = snapshot.Sheets.Single(x => x.Name == "Data");
                Check(!snapshot.Saved
                    && snapshot.SelectionAddress == "A1"
                    && data.Cells.Single(x => x.Address == "A1").Value == "UNSAVED-EXCEL"
                    && data.Cells.Single(x => x.Address == "B1").Formula == "=A1",
                    "Unsaved Excel values/formulas/selection were not observed.");
            }).ConfigureAwait(false);

        await Case(
            "OFFICE-WORD-UNSAVED",
            "open Word unsaved state is observed through OfficeHost",
            "OfficeHost word.discover + word.snapshot fixture",
            async () =>
            {
                using var client = new OfficeHostClient(hostExecutable, fixtureMode: true);
                var discovery = await client.DiscoverWordAsync().ConfigureAwait(false);
                Check(discovery.ActiveSessionId is not null,
                    "Fixture active Word document is missing.");
                var snapshot = await client.SnapshotWordAsync(discovery.ActiveSessionId!)
                    .ConfigureAwait(false);
                Check(!snapshot.Saved
                    && snapshot.SelectionText == "UNSAVED-WORD"
                    && snapshot.Paragraphs.Any(x => x.Text == "UNSAVED-WORD")
                    && snapshot.Tables.Count == 1
                    && snapshot.Headers.Count > 0
                    && snapshot.Footers.Count > 0,
                    "Unsaved Word selection/body/structure were not observed.");
            }).ConfigureAwait(false);

        await Case(
            "OFFICE-STRUCTURED-MUTATION",
            "structured Excel and Word mutation execute with before/after state",
            "OfficeHost excel.patch + word.patch fixture",
            async () =>
            {
                using var client = new OfficeHostClient(hostExecutable, fixtureMode: true);

                var excelSession = (await client.DiscoverExcelAsync().ConfigureAwait(false)).ActiveSessionId!;
                var excelBefore = await client.SnapshotExcelAsync(excelSession).ConfigureAwait(false);
                var excelPatch = await client.PatchExcelAsync(new ExcelPatchRequest(
                    excelSession,
                    excelBefore.StateToken,
                    true,
                    "Data",
                    [new ExcelCellPatch("A1", Value: "MB110-EXCEL", Bold: false)]))
                    .ConfigureAwait(false);
                Check(excelPatch.ChangedCells.SequenceEqual(["A1"])
                    && excelPatch.After.StateToken != excelBefore.StateToken
                    && excelPatch.After.Sheets.Single(x => x.Name == "Data")
                        .Cells.Single(x => x.Address == "A1").Value == "MB110-EXCEL",
                    "Structured Excel mutation did not return correct before/after state.");

                var wordSession = (await client.DiscoverWordAsync().ConfigureAwait(false)).ActiveSessionId!;
                var wordBefore = await client.SnapshotWordAsync(wordSession).ConfigureAwait(false);
                var wordPatch = await client.PatchWordAsync(new WordPatchRequest(
                    wordSession,
                    wordBefore.StateToken,
                    true,
                    [new WordParagraphPatch(
                        0,
                        Text: "MB110-WORD",
                        Bold: false,
                        Italic: true)]))
                    .ConfigureAwait(false);
                Check(wordPatch.ChangedParagraphs.SequenceEqual([0])
                    && wordPatch.After.StateToken != wordBefore.StateToken
                    && wordPatch.After.Paragraphs[0].Text == "MB110-WORD",
                    "Structured Word mutation did not return correct before/after state.");
            }).ConfigureAwait(false);

        await Case(
            "OFFICE-PRESERVATION",
            "structured Office mutation preserves unrelated content and structure",
            "LiveExcelVerifier + LiveWordVerifier preservation criteria",
            async () =>
            {
                using var excelClient = new OfficeHostClient(hostExecutable, fixtureMode: true);
                var excelSession = (await excelClient.DiscoverExcelAsync().ConfigureAwait(false)).ActiveSessionId!;
                var excelBefore = await excelClient.SnapshotExcelAsync(excelSession).ConfigureAwait(false);
                var excelPatched = await excelClient.PatchExcelAsync(new ExcelPatchRequest(
                    excelSession,
                    excelBefore.StateToken,
                    true,
                    "Data",
                    [new ExcelCellPatch("A1", Value: "PRESERVE-EXCEL")]))
                    .ConfigureAwait(false);
                var excelExpected = excelPatched.After.Sheets.Single(x => x.Name == "Data")
                    .Cells.Single(x => x.Address == "A1");
                var excelReport = LiveExcelVerifier.Verify(
                    excelBefore,
                    excelPatched.After,
                    new LiveExcelVerificationExpectation(
                        [new LiveExcelExpectedCell("Data", "A1", excelExpected)]));
                Check(excelReport.Passed
                    && excelReport.Criteria.Single(x =>
                        x.CriterionId == LiveExcelVerifier.PreserveCriterionId).Status
                        == VerificationCriterionStatus.Passed,
                    "Live Excel verifier detected preservation regression.");

                using var wordClient = new OfficeHostClient(hostExecutable, fixtureMode: true);
                var wordSession = (await wordClient.DiscoverWordAsync().ConfigureAwait(false)).ActiveSessionId!;
                var wordBefore = await wordClient.SnapshotWordAsync(wordSession).ConfigureAwait(false);
                var wordPatched = await wordClient.PatchWordAsync(new WordPatchRequest(
                    wordSession,
                    wordBefore.StateToken,
                    true,
                    [new WordParagraphPatch(0, Text: "PRESERVE-WORD")]))
                    .ConfigureAwait(false);
                var wordReport = LiveWordVerifier.Verify(
                    wordBefore,
                    wordPatched.After,
                    new LiveWordVerificationExpectation(
                        [new LiveWordExpectedParagraph(0, wordPatched.After.Paragraphs[0])]));
                Check(wordReport.Passed
                    && wordReport.Criteria.Single(x =>
                        x.CriterionId == LiveWordVerifier.PreserveCriterionId).Status
                        == VerificationCriterionStatus.Passed,
                    "Live Word verifier detected preservation regression.");
            }).ConfigureAwait(false);

        await Case(
            "OFFICE-NATIVE-LANGUAGE",
            "Word spelling/grammar and legal citation evidence have a state-bound native OfficeHost path",
            "word.languageEvidence protocol + ComOfficeBackend native source guard",
            async () =>
            {
                using var client = new OfficeHostClient(hostExecutable, fixtureMode: true);
                var session = (await client.DiscoverWordAsync().ConfigureAwait(false)).ActiveSessionId!;
                var snapshot = await client.SnapshotWordAsync(session).ConfigureAwait(false);
                var evidence = await client.InspectWordLanguageAsync(
                    new WordLanguageEvidenceRequest(session, snapshot.StateToken))
                    .ConfigureAwait(false);

                Check(evidence.Spelling.Any(x =>
                        x.Text == "mispell"
                        && x.Suggestions.Contains("misspell", StringComparer.OrdinalIgnoreCase))
                    && evidence.Citations.Any(x =>
                        x.DocumentId == "214/2025/NĐ-CP"),
                    "Fixture language/citation evidence did not cross OfficeHost protocol.");

                var repo = FindRepoRoot();
                var com = File.ReadAllText(Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab.OfficeHost",
                    "ComOfficeBackend.cs"));
                var server = File.ReadAllText(Path.Combine(
                    repo,
                    "experiments",
                    "H2AgentLab.OfficeHost",
                    "OfficeHostServer.cs"));
                Check(com.Contains("document.SpellingErrors", StringComparison.Ordinal)
                    && com.Contains("document.GrammaticalErrors", StringComparison.Ordinal)
                    && com.Contains("document.Content", StringComparison.Ordinal)
                    && com.Contains("\"word-com-native\"", StringComparison.Ordinal),
                    "Real OfficeHost backend lacks native Word language/document evidence path.");
                Check(server.Contains("\"word.languageEvidence\"", StringComparison.Ordinal),
                    "OfficeHost server does not expose the state-bound Word language evidence RPC.");
            }).ConfigureAwait(false);

        await Case(
            "OFFICE-VERIFIER",
            "Office live verifiers remain the semantic completion evidence for structured mutations",
            "LiveExcelVerifier + LiveWordVerifier + structured capability catalog",
            () =>
            {
                var names = StructuredOfficeCapabilityCatalog.All
                    .Select(x => x.Name)
                    .ToHashSet(StringComparer.Ordinal);
                Check(names.Contains("excel.verify_range")
                    && names.Contains("word.verify_range")
                    && names.Contains("word.get_spelling_errors")
                    && names.Contains("word.get_grammar_candidates")
                    && names.Contains("word.extract_legal_citations"),
                    "Structured Office capability surface lost verifier/language/citation tools.");

                var repo = FindRepoRoot();
                Check(File.Exists(Path.Combine(
                        repo,
                        "experiments",
                        "H2AgentLab",
                        "Verification",
                        "LiveExcelVerifier.cs"))
                    && File.Exists(Path.Combine(
                        repo,
                        "experiments",
                        "H2AgentLab",
                        "Verification",
                        "LiveWordVerifier.cs")),
                    "Live Office verifier implementations are missing.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

        var passed = cases.Count(x => x.Passed);
        var failed = cases.Count - passed;
        var gatePassed = failed == 0;

        var lines = cases.Select(x =>
                (x.Passed ? "PASS " : "FAIL ")
                + x.Id + " " + x.Requirement
                + " | evidence=" + x.Evidence
                + (x.Failure is null ? "" : " | " + x.Failure))
            .ToList();
        lines.Add($"RESULT: {passed} passed, {failed} failed.");
        lines.Add($"GATE: {(gatePassed ? "PASS" : "FAIL")} MB-110 Office acceptance.");

        await File.WriteAllLinesAsync(
            Path.Combine(root, "mb-office-acceptance-tests.txt"),
            lines).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(root, "mb-office-acceptance-tests.json"),
            JsonSerializer.Serialize(
                new
                {
                    gate = "MB-110",
                    passed = gatePassed,
                    passedCases = passed,
                    failedCases = failed,
                    cases
                },
                new JsonSerializerOptions { WriteIndented = true }))
            .ConfigureAwait(false);

        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return gatePassed ? 0 : 1;
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

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}

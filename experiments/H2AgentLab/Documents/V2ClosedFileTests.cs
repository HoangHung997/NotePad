using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using H2AgentLab.Verification;
using H2Notes.Core;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace H2AgentLab.Documents;

public static class V2ClosedFileTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new v2 closed-file test directory.");
        Directory.CreateDirectory(root);

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
                lines.Add("FAIL " + name + ": " + ex.Message);
            }
        }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        await Test("Closed XLSX snapshot and verifier preserve formulas styles fills merges and hidden state", () =>
        {
            var reader = new ClosedWorkbookSnapshotReader();
            var before = reader.Read(
                "before.xlsx",
                CreateWorkbookFixture(targetChanged: false, regressFormula: false, regressHidden: false));
            var after = reader.Read(
                "after.xlsx",
                CreateWorkbookFixture(targetChanged: true, regressFormula: false, regressHidden: false));
            var bad = reader.Read(
                "bad.xlsx",
                CreateWorkbookFixture(targetChanged: true, regressFormula: true, regressHidden: true));

            var beforeData = before.Sheets.Single(x => x.Name == "Data");
            var beforeA1 = beforeData.Cells.Single(x => x.Address == "A1");
            var beforeA2 = beforeData.Cells.Single(x => x.Address == "A2");

            Check(beforeA1.Bold && beforeA1.Italic, "XLSX fixture lost bold/italic style.");
            Check(beforeA1.FillPattern == "Solid"
                && beforeA1.FillForeground?.Contains("FFFF00", StringComparison.Ordinal) == true,
                "XLSX fixture lost solid fill.");
            Check(beforeA2.Formula == "SUM(A1,1)", "XLSX fixture lost formula.");
            Check(beforeData.MergedRanges.SequenceEqual(new[] { "A1:B1" }),
                "XLSX fixture lost merge.");
            Check(beforeData.HiddenRows.SequenceEqual(new uint[] { 2 })
                && beforeData.HiddenColumns.SequenceEqual(new[] { "2" }),
                "XLSX fixture lost row/column hidden state.");
            Check(before.Sheets.Single(x => x.Name == "Hidden").State == "Hidden",
                "XLSX fixture lost hidden sheet state.");

            var expectedA1 = after.Sheets.Single(x => x.Name == "Data")
                .Cells.Single(x => x.Address == "A1");
            var expectation = new ExcelVerificationExpectation(
                [new ExcelExpectedCell("Data", "A1", expectedA1)]);

            var pass = ExcelVerifier.Verify(before, after, expectation);
            Check(pass.Passed, "Expected XLSX edit failed closed-file verification.");

            var fail = ExcelVerifier.Verify(before, bad, expectation);
            Check(!fail.Passed, "Regressed XLSX unexpectedly passed verification.");
            Check(fail.Failures.Any(x => x.CriterionId == ExcelVerifier.PreserveCellsCriterionId),
                "XLSX formula regression was not detected.");
            Check(fail.Failures.Any(x => x.CriterionId == ExcelVerifier.StructureCriterionId),
                "XLSX hidden-state regression was not detected.");
            return Task.CompletedTask;
        });

        await Test("Closed DOCX snapshot and verifier preserve runs tables sections headers and footers", () =>
        {
            var reader = new ClosedWordSnapshotReader();
            var beforeBytes = CreateWordFixture("world", headerChanged: false, dropTable: false);
            var afterBytes = CreateWordFixture("WORLD", headerChanged: false, dropTable: false);
            var badBytes = CreateWordFixture("WORLD", headerChanged: true, dropTable: true);

            var before = reader.Read("before.docx", beforeBytes);
            var after = reader.Read("after.docx", afterBytes);
            var bad = reader.Read("bad.docx", badBytes);

            var paragraph = before.BodyParagraphs.Single();
            Check(paragraph.StyleId == "BodyStyle", "DOCX fixture lost paragraph style.");
            Check(paragraph.Runs.Count == 2
                && paragraph.Runs[0].Bold
                && paragraph.Runs[1].Italic,
                "DOCX fixture lost bold/italic run formatting.");
            Check(before.Tables.Count == 1
                && before.Tables[0].Rows[0].Cells.Select(x => x.Text).SequenceEqual(new[] { "A", "B" }),
                "DOCX fixture lost table content.");
            Check(before.Sections.Count == 1
                && before.Sections[0].MarginLeftTwips == 1440
                && before.Sections[0].HeaderReferences.Count == 1
                && before.Sections[0].FooterReferences.Count == 1,
                "DOCX fixture lost section/header-footer references.");
            Check(before.Headers.Single().Paragraphs.Single().Text == "Header fixture"
                && before.Footers.Single().Paragraphs.Single().Text == "Footer fixture",
                "DOCX fixture lost header/footer content.");

            var expected = after.BodyParagraphs.Single();
            var expectation = new WordVerificationExpectation(
                expectedBodyParagraphs:
                [new WordExpectedBodyParagraph(0, expected)]);

            var pass = WordVerifier.Verify(before, after, expectation);
            Check(pass.Passed, "Expected DOCX edit failed closed-file verification.");

            var fail = WordVerifier.Verify(before, bad, expectation);
            Check(!fail.Passed, "Regressed DOCX unexpectedly passed verification.");
            Check(fail.Failures.Any(x => x.CriterionId == WordVerifier.StructureCriterionId),
                "DOCX table/header regression was not detected.");
            return Task.CompletedTask;
        });

        await Test("Lab document inspection still uses H2 Core extraction for closed Office files", () =>
        {
            var service = new LabDocumentInspectionService();
            var word = service.Inspect(
                "fixture.docx",
                CreateWordFixture("world", headerChanged: false, dropTable: false));
            var excel = service.Inspect(
                "fixture.xlsx",
                CreateWorkbookFixture(targetChanged: false, regressFormula: false, regressHidden: false));

            Check(word.Text.Contains("Hello world", StringComparison.Ordinal)
                && word.Text.Contains("Header fixture", StringComparison.Ordinal)
                && word.Text.Contains("Footer fixture", StringComparison.Ordinal),
                "H2 Core Word extraction did not flow through Lab inspection.");
            Check(excel.Text.Contains("SUM(A1,1)", StringComparison.Ordinal)
                && excel.Text.Contains("[Sheet: Data", StringComparison.Ordinal),
                "H2 Core Excel extraction did not flow through Lab inspection.");
            return Task.CompletedTask;
        });

        await Test("Closed snapshot output is deterministic for identical Office bytes", () =>
        {
            var xlsx = CreateWorkbookFixture(false, false, false);
            var docx = CreateWordFixture("world", false, false);
            var workbookReader = new ClosedWorkbookSnapshotReader();
            var wordReader = new ClosedWordSnapshotReader();

            Check(
                JsonSerializer.Serialize(workbookReader.Read("a.xlsx", xlsx))
                == JsonSerializer.Serialize(workbookReader.Read("b.xlsx", xlsx)),
                "XLSX snapshot changed for identical bytes.");
            Check(
                JsonSerializer.Serialize(wordReader.Read("a.docx", docx))
                == JsonSerializer.Serialize(wordReader.Read("b.docx", docx)),
                "DOCX snapshot changed for identical bytes.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "v2-closed-file-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static byte[] CreateWorkbookFixture(
        bool targetChanged,
        bool regressFormula,
        bool regressHidden)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new S.Workbook();

            var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
            stylesPart.Stylesheet = new S.Stylesheet(
                new S.Fonts(
                    new S.Font(),
                    new S.Font(new S.Bold(), new S.Italic()))
                { Count = 2 },
                new S.Fills(
                    new S.Fill(new S.PatternFill { PatternType = S.PatternValues.None }),
                    new S.Fill(new S.PatternFill { PatternType = S.PatternValues.Gray125 }),
                    new S.Fill(new S.PatternFill(
                        new S.ForegroundColor { Rgb = new HexBinaryValue { Value = "FFFFFF00" } })
                    { PatternType = S.PatternValues.Solid }))
                { Count = 3 },
                new S.Borders(new S.Border()) { Count = 1 },
                new S.CellStyleFormats(new S.CellFormat()) { Count = 1 },
                new S.CellFormats(
                    new S.CellFormat(),
                    new S.CellFormat
                    {
                        FontId = 1,
                        FillId = 2,
                        BorderId = 0,
                        ApplyFont = true,
                        ApplyFill = true
                    })
                { Count = 2 });
            stylesPart.Stylesheet.Save();

            var dataPart = workbookPart.AddNewPart<WorksheetPart>();
            var row1 = new S.Row { RowIndex = 1 };
            row1.Append(new S.Cell
            {
                CellReference = "A1",
                CellValue = new S.CellValue(targetChanged ? "99" : "42"),
                StyleIndex = 1
            });
            var row2 = new S.Row { RowIndex = 2, Hidden = !regressHidden };
            row2.Append(new S.Cell
            {
                CellReference = "A2",
                CellFormula = new S.CellFormula(regressFormula ? "SUM(A1,2)" : "SUM(A1,1)"),
                CellValue = new S.CellValue("43")
            });
            dataPart.Worksheet = new S.Worksheet(
                new S.Columns(new S.Column { Min = 2, Max = 2, Hidden = true }),
                new S.SheetData(row1, row2),
                new S.MergeCells(new S.MergeCell { Reference = "A1:B1" }));
            dataPart.Worksheet.Save();

            var hiddenPart = workbookPart.AddNewPart<WorksheetPart>();
            hiddenPart.Worksheet = new S.Worksheet(
                new S.SheetData(
                    new S.Row(
                        new S.Cell
                        {
                            CellReference = "A1",
                            DataType = S.CellValues.InlineString,
                            InlineString = new S.InlineString(new S.Text("secret"))
                        })
                    { RowIndex = 1 }));
            hiddenPart.Worksheet.Save();

            var sheets = workbookPart.Workbook.AppendChild(new S.Sheets());
            sheets.Append(
                new S.Sheet
                {
                    Name = "Data",
                    SheetId = 1,
                    Id = workbookPart.GetIdOfPart(dataPart),
                    State = S.SheetStateValues.Visible
                },
                new S.Sheet
                {
                    Name = "Hidden",
                    SheetId = 2,
                    Id = workbookPart.GetIdOfPart(hiddenPart),
                    State = regressHidden ? S.SheetStateValues.Visible : S.SheetStateValues.Hidden
                });
            workbookPart.Workbook.Save();
        }
        return stream.ToArray();
    }

    private static byte[] CreateWordFixture(
        string targetWord,
        bool headerChanged,
        bool dropTable)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var main = document.AddMainDocumentPart();

            var styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new W.Styles(
                new W.Style(new W.StyleName { Val = "Body Style" })
                {
                    Type = W.StyleValues.Paragraph,
                    StyleId = "BodyStyle"
                });
            styles.Styles.Save();

            var header = main.AddNewPart<HeaderPart>();
            header.Header = new W.Header(
                new W.Paragraph(
                    new W.Run(
                        new W.Text(headerChanged ? "Header changed" : "Header fixture"))));
            header.Header.Save();

            var footer = main.AddNewPart<FooterPart>();
            footer.Footer = new W.Footer(
                new W.Paragraph(new W.Run(new W.Text("Footer fixture"))));
            footer.Footer.Save();

            var paragraph = new W.Paragraph(
                new W.ParagraphProperties(new W.ParagraphStyleId { Val = "BodyStyle" }),
                new W.Run(
                    new W.RunProperties(new W.Bold()),
                    new W.Text("Hello ") { Space = SpaceProcessingModeValues.Preserve }),
                new W.Run(
                    new W.RunProperties(new W.Italic()),
                    new W.Text(targetWord)));

            var table = new W.Table(
                new W.TableRow(
                    new W.TableCell(new W.Paragraph(new W.Run(new W.Text("A")))),
                    new W.TableCell(new W.Paragraph(new W.Run(new W.Text("B"))))));

            var section = new W.SectionProperties(
                new W.HeaderReference
                {
                    Id = main.GetIdOfPart(header),
                    Type = W.HeaderFooterValues.Default
                },
                new W.FooterReference
                {
                    Id = main.GetIdOfPart(footer),
                    Type = W.HeaderFooterValues.Default
                },
                new W.PageSize { Width = 12240, Height = 15840 },
                new W.PageMargin
                {
                    Top = 1440,
                    Right = 1440,
                    Bottom = 1440,
                    Left = 1440,
                    Header = 720,
                    Footer = 720,
                    Gutter = 0
                });

            var body = new W.Body(paragraph);
            if (!dropTable)
                body.Append(table);
            body.Append(section);
            main.Document = new W.Document(body);
            main.Document.Save();
        }
        return stream.ToArray();
    }
}

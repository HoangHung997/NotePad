using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace H2Notes.Core;

public sealed class AiArtifact
{
    public string FileName { get; set; } = "";
    public string Text { get; set; } = "";
    public List<AiArtifactSheet> Sheets { get; set; } = [];
    public Guid? LayoutSourceId { get; set; }
}
public sealed class AiArtifactSheet
{
    public string Name { get; set; } = "Sheet1";
    public List<List<string>> Rows { get; set; } = [];
}

// Deliberately data-only: the model cannot supply code, file paths, formulas or macros to execute.
public static class AiArtifacts
{
    public const string Instructions = """
        Nếu người dùng yêu cầu tạo tệp Word/Excel/văn bản, trả lời ngắn và xuất khối fenced code h2-file chứa JSON hợp lệ:
        ```h2-file
        {"fileName":"Bao-cao.docx","text":"Tiêu đề\nNội dung đầy đủ","sheets":[]}
        ```
        Với Excel: {"fileName":"Cong-viec.xlsx","text":"","sheets":[{"name":"Cong viec","rows":[["Công việc","Trạng thái"],["Việc A","Chưa xong"]]}]}
        Định dạng cho phép: .docx, .xlsx, .txt, .md, .csv. CSV dùng sheets[0].rows. Word dùng text và có thể kèm sheets làm bảng.
        Chỉ tạo khi người dùng yêu cầu. Không đưa code/script, không dùng đường dẫn, URL, macro. Mọi ô là giá trị tĩnh, không phải công thức thực thi.
        Số trong rows vẫn biểu diễn bằng chuỗi JSON, dùng dấu chấm thập phân, không dấu ngăn nhóm. App có thể chuyển số ngắn thành ô số Excel.
        Tối đa 4 tệp, 8 sheet/tệp, 2.000 hàng/sheet, 64 cột, 120.000 ký tự/tệp. Không tự cắt tài liệu mà giả là đủ.
        App sẽ hiện bản nháp và nút lưu. Chưa được nói đã lưu/đã mở tệp: chỉ người dùng bấm Lưu mới tạo tệp thật.
        Nếu yêu cầu chép PDF/ảnh thành Word GIỮ BỐ CỤC, không dùng text thuần: xuất {"fileName":"Ban-sao.docx","text":"","sheets":[],"layoutSourceId":"ID tệp tham khảo"}.
        Chỉ dùng ID đã có trong dữ liệu tệp. App dựng Word local bằng MinerU khi người dùng bấm nút, có sửa chữ OCR trước khi lưu. Font ước lượng; bảng/dấu/chữ ký và vùng chưa nhận dạng giữ ảnh. Không khẳng định giống 100% hoặc đã chuyển thành công. Nếu bản đính kèm chỉ còn Markdown, cần chọn lại PDF gốc.
        """;
    private static readonly Regex Blocks = new(@"```h2-file\s*\r?\n(?<json>[\s\S]*?)\r?\n```", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public static IReadOnlyList<AiArtifact> Parse(string response)
    {
        if (response.Length > 600_000) throw new InvalidDataException("Phản hồi quá lớn để tạo tệp.");
        var matches = Blocks.Matches(response);
        if (matches.Count > 4) throw new InvalidDataException("Chỉ tạo tối đa 4 tệp mỗi trả lời.");
        return matches.Select(m =>
        {
            var artifact = JsonSerializer.Deserialize<AiArtifact>(m.Groups["json"].Value, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 12 })
                ?? throw new InvalidDataException("Bản nháp tệp trống.");
            Validate(artifact); return artifact;
        }).ToArray();
    }
    public static void Validate(AiArtifact a)
    {
        if (string.IsNullOrWhiteSpace(a.FileName) || a.FileName.Length > 100 || a.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || a.FileName.Contains('/') || a.FileName.Contains('\\') || a.FileName.Trim() != a.FileName
            || Regex.IsMatch(a.FileName, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", RegexOptions.IgnoreCase))
            throw new InvalidDataException("Tên tệp AI đề xuất không hợp lệ; không cho phép đường dẫn.");
        var ext = Path.GetExtension(a.FileName).ToLowerInvariant();
        if (a.LayoutSourceId is { } source && (source == Guid.Empty || ext != ".docx" || a.Text != "" || a.Sheets is null || a.Sheets.Count != 0))
            throw new InvalidDataException("Bản Word giữ bố cục cần đúng một ID tệp gốc, không trộn nội dung do AI viết lại.");
        if (ext is not ".docx" and not ".xlsx" and not ".txt" and not ".md" and not ".csv") throw new InvalidDataException("Loại tệp không được phép tạo.");
        if (a.Text is null || a.Sheets is null || a.Sheets.Count > 8) throw new InvalidDataException("Nội dung hoặc số sheet không hợp lệ.");
        if ((ext is ".xlsx" or ".csv") && a.Sheets.Count == 0 || ext == ".csv" && a.Sheets.Count != 1)
            throw new InvalidDataException("Excel cần sheet; CSV chỉ chứa một sheet.");
        if ((ext is ".xlsx" or ".csv") && a.Text.Length > 0) throw new InvalidDataException("Excel/CSV cần toàn bộ nội dung trong sheets; không tự bỏ phần text.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long chars = a.Text.Length;
        foreach (var sheet in a.Sheets)
        {
            if (sheet is null || string.IsNullOrWhiteSpace(sheet.Name) || sheet.Name.Length > 31 || sheet.Name.IndexOfAny(['[', ']', ':', '*', '?', '/', '\\']) >= 0
                || sheet.Name.StartsWith('\'') || sheet.Name.EndsWith('\'') || !names.Add(sheet.Name)
                || sheet.Rows is null || sheet.Rows.Count > 2000 || sheet.Rows.Any(r => r is null || r.Count > 64 || r.Any(v => v is null || v.Length > 32767)))
                throw new InvalidDataException("Sheet không hợp lệ hoặc quá giới hạn hàng/cột.");
            chars += sheet.Rows.Sum(r => r.Sum(v => (long)v.Length));
        }
        if (chars > 120_000) throw new InvalidDataException("Bản nháp tệp vượt 120.000 ký tự.");
        if (ext is ".txt" or ".md" && a.Sheets.Count > 0) throw new InvalidDataException("Tệp văn bản cần đưa toàn bộ bảng vào text; không tự bỏ bảng.");
    }
    public static string Preview(AiArtifact a) => a.FileName + (a.LayoutSourceId is not null ? "\n\nYêu cầu dựng Word giữ bố cục từ tệp đã đính kèm. Chưa chạy OCR, chưa tạo hoặc lưu Word. Chọn Dựng Word để đối chiếu chữ/phông trước khi lưu." : "\n\n" + a.Text + "\n" + string.Join("\n\n", a.Sheets.Select(s => s.Name + "\n" + string.Join("\n", s.Rows.Select(r => string.Join(" | ", r))))));
    public static string WithoutBlocks(string response) => Blocks.Replace(response, "[Bản nháp tệp bên dưới]").Trim();

    public static byte[] Create(AiArtifact a)
    {
        Validate(a); var ext = Path.GetExtension(a.FileName).ToLowerInvariant();
        if (a.LayoutSourceId is not null) throw new InvalidOperationException("Word giữ bố cục cần xử lý tệp nguồn; không tạo Word rỗng thay thế.");
        if (ext is ".txt" or ".md") return new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(a.Text)).ToArray();
        if (ext == ".csv")
        {
            string Escape(string value)
            {
                // Spreadsheet software may execute CSV formulas even inside quoted fields.
                if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@') || value.StartsWith('\t') || value.StartsWith('\r')) value = "'" + value;
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(string.Join("\r\n", a.Sheets[0].Rows.Select(r => string.Join(",", r.Select(Escape)))))).ToArray();
        }
        using var stream = new MemoryStream();
        if (ext == ".docx")
        {
            using var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true);
            var main = doc.AddMainDocumentPart(); var body = new W.Body(); main.Document = new W.Document(body);
            W.Paragraph Paragraph(string text, bool heading = false) => new(new W.ParagraphProperties(new W.SpacingBetweenLines { After = heading ? "180" : "100" }),
                new W.Run(new W.RunProperties(new W.RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" }, new W.Bold { Val = heading }, new W.FontSize { Val = heading ? "30" : "22" }),
                    new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));
            var lines = a.Text.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++) body.Append(Paragraph(lines[i], i == 0));
            foreach (var sheet in a.Sheets)
            {
                body.Append(Paragraph(sheet.Name, true));
                var table = new W.Table(new W.TableProperties(new W.TableBorders(new W.TopBorder { Val = W.BorderValues.Single, Size = 4 },
                    new W.BottomBorder { Val = W.BorderValues.Single, Size = 4 }, new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4 }, new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4 })));
                var columns = sheet.Rows.Count == 0 ? 1 : Math.Max(1, sheet.Rows.Max(r => r.Count));
                table.Append(new W.TableGrid(Enumerable.Range(0, columns).Select(_ => new W.GridColumn { Width = (9600 / columns).ToString() })));
                foreach (var row in sheet.Rows) table.Append(new W.TableRow(Enumerable.Range(0, columns).Select(i => new W.TableCell(Paragraph(i < row.Count ? row[i] : "")))));
                body.Append(table);
            }
            body.Append(new W.SectionProperties(new W.PageSize { Width = 11906, Height = 16838 }, new W.PageMargin { Top = 1134, Bottom = 1134, Left = 1134, Right = 1134, Header = 567, Footer = 567, Gutter = 0 }));
        }
        else
        {
            using var doc = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true);
            var main = doc.AddWorkbookPart(); main.Workbook = new S.Workbook(); var sheets = main.Workbook.AppendChild(new S.Sheets()); uint id = 1;
            var styles = main.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = new S.Stylesheet(
                new S.Fonts(new S.Font(new S.FontSize { Val = 11 }, new S.FontName { Val = "Calibri" }),
                    new S.Font(new S.Bold(), new S.FontSize { Val = 11 }, new S.FontName { Val = "Calibri" })) { Count = 2 },
                new S.Fills(new S.Fill(new S.PatternFill { PatternType = S.PatternValues.None }), new S.Fill(new S.PatternFill { PatternType = S.PatternValues.Gray125 }),
                    new S.Fill(new S.PatternFill(new S.ForegroundColor { Rgb = "FFF4E7DC" }, new S.BackgroundColor { Indexed = 64 }) { PatternType = S.PatternValues.Solid })) { Count = 3 },
                new S.Borders(new S.Border()) { Count = 1 }, new S.CellStyleFormats(new S.CellFormat()) { Count = 1 },
                new S.CellFormats(new S.CellFormat(new S.Alignment { WrapText = true, Vertical = S.VerticalAlignmentValues.Top }) { FontId = 0, FillId = 0, BorderId = 0, ApplyAlignment = true },
                    new S.CellFormat(new S.Alignment { WrapText = true, Vertical = S.VerticalAlignmentValues.Top }) { FontId = 1, FillId = 2, BorderId = 0, ApplyFont = true, ApplyFill = true, ApplyAlignment = true }) { Count = 2 });
            foreach (var sheet in a.Sheets)
            {
                var part = main.AddNewPart<WorksheetPart>(); var data = new S.SheetData();
                var count = sheet.Rows.Count == 0 ? 1u : (uint)Math.Max(1, sheet.Rows.Max(r => r.Count));
                part.Worksheet = new S.Worksheet(new S.SheetViews(new S.SheetView(new S.Pane { VerticalSplit = 1, TopLeftCell = "A2", ActivePane = S.PaneValues.BottomLeft, State = S.PaneStateValues.Frozen }) { WorkbookViewId = 0 }),
                    new S.Columns(new S.Column { Min = 1, Max = count, Width = 32, CustomWidth = true }), data);
                sheets.Append(new S.Sheet { Id = main.GetIdOfPart(part), SheetId = id++, Name = sheet.Name });
                for (var r = 0; r < sheet.Rows.Count; r++)
                {
                    var row = new S.Row { RowIndex = (uint)r + 1 }; data.Append(row);
                    for (var c = 0; c < sheet.Rows[r].Count; c++)
                    {
                        var value = sheet.Rows[r][c];
                        var cell = new S.Cell { CellReference = Column(c + 1) + (r + 1), DataType = S.CellValues.InlineString, StyleIndex = r == 0 ? 1u : 0u };
                        if (r > 0 && value.Count(char.IsDigit) <= 15 && Regex.IsMatch(value, @"^-?(0|[1-9][0-9]*)(\.[0-9]+)?$"))
                        { cell.DataType = S.CellValues.Number; cell.CellValue = new S.CellValue(value); }
                        else cell.InlineString = new S.InlineString(new S.Text(value) { Space = SpaceProcessingModeValues.Preserve });
                        row.Append(cell);
                    }
                }
            }
        }
        return stream.ToArray();
    }
    private static string Column(int column) { var value = ""; while (column > 0) { column--; value = (char)('A' + column % 26) + value; column /= 26; } return value; }
}

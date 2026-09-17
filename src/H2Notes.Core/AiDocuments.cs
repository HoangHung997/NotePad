using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace H2Notes.Core;

public sealed class AiAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string MimeType { get; set; } = "";
    public byte[] Data { get; set; } = [];
    public string Text { get; set; } = "";
    public string Notice { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public AiPdfEngine? PdfEngine { get; set; }
    public string? SourceName { get; set; }
    public string? SourceSha256 { get; set; }
    [JsonIgnore] public bool IsImage => MimeType is "image/png" or "image/jpeg" or "image/webp";
    [JsonIgnore] public bool IsPdf => MimeType == "application/pdf";
    [JsonIgnore] public bool HasImageOcr => IsImage && PdfEngine is AiPdfEngine.GotOcr or AiPdfEngine.MinerU or AiPdfEngine.Docling
        && !string.IsNullOrWhiteSpace(Text);
}

public static class AiDocuments
{
    public const int MaxFileBytes = 8 * 1024 * 1024;
    public const int MaxTextCharacters = 120_000;
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".pdf", ".docx", ".xlsx", ".txt", ".md", ".csv", ".json"];

    public static AiAttachment Read(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > MaxFileBytes) throw new InvalidDataException("Mỗi tệp tối đa 8 MB.");
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = input.Read(buffer)) != 0)
        {
            if (output.Length + read > MaxFileBytes) throw new InvalidDataException("Mỗi tệp tối đa 8 MB.");
            output.Write(buffer, 0, read);
        }
        return Read(Path.GetFileName(path), output.ToArray());
    }

    public static AiAttachment Read(string name, byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxFileBytes) throw new InvalidDataException("Tệp rỗng hoặc lớn hơn 8 MB.");
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (!Extensions.Contains(extension)) throw new InvalidDataException("Chưa hỗ trợ loại tệp này. Word/Excel cũ cần lưu lại thành .docx/.xlsx.");
        var item = new AiAttachment { Name = Path.GetFileName(name), Data = bytes, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
        if (extension == ".pdf")
        {
            AiPdf.ValidateBytes(bytes);
            item.MimeType = "application/pdf";
            item.PdfEngine = AiPdfEngine.Direct;
            item.Notice = "PDF gốc chỉ gửi tới model hỗ trợ PDF. Ollama cần chuyển PDF sang Markdown bằng OCR đã cài trong Thiết lập AI.";
            return item;
        }
        if (extension is ".png" or ".jpg" or ".jpeg" or ".webp")
        {
            item.MimeType = extension == ".png" ? "image/png" : extension == ".webp" ? "image/webp" : "image/jpeg";
            var valid = item.MimeType switch
            {
                "image/png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                "image/jpeg" => bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255,
                _ => bytes.Length >= 12 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP"
            };
            if (!valid) throw new InvalidDataException("Nội dung ảnh không khớp định dạng.");
            item.Notice = "Gửi ảnh gốc cho model có khả năng đọc ảnh; không tự OCR bằng model khác.";
            return item;
        }
        item.MimeType = extension == ".docx" ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
            : extension == ".xlsx" ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "text/plain";
        if (extension is ".docx" or ".xlsx")
        {
            GuardOffice(bytes);
            item.Text = extension == ".docx" ? ReadWord(bytes) : ReadWorkbook(bytes);
            item.Notice = extension == ".docx" ? "Đọc chữ và bảng, header/footer/chú thích; không đọc ảnh, đối tượng nhúng hoặc tái tạo bố cục."
                : "Đọc mọi sheet, địa chỉ ô, giá trị lưu và công thức; không tính lại công thức, không đọc biểu đồ/ảnh. Số/ngày giữ thêm giá trị gốc.";
        }
        else
        {
            using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), true);
            item.Text = reader.ReadToEnd();
            if (item.Text.Contains('\0')) throw new InvalidDataException("Tệp không phải văn bản UTF-8/Unicode hợp lệ.");
        }
        if (item.Text.Length > MaxTextCharacters) throw new InvalidDataException("Tệp vượt 120.000 ký tự sau trích xuất. Hãy chia nhỏ, app không tự cắt nội dung.");
        if (string.IsNullOrWhiteSpace(item.Text)) throw new InvalidDataException("Không tìm thấy chữ/ô dữ liệu có thể đọc. Với tài liệu scan hãy gửi ảnh.");
        return item;
    }

    private static void GuardOffice(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Count > 2000 || zip.Entries.Sum(e => e.Length) > 40L * 1024 * 1024)
            throw new InvalidDataException("Tài liệu bung nén quá lớn để đọc an toàn.");
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.Contains("vbaProject", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Không nhận tài liệu có macro.");
            if (!entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) continue;
            using var stream = entry.Open();
            using var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 40 * 1024 * 1024 });
            while (xml.Read()) { }
        }
    }

    private static OpenSettings Settings() => new() { AutoSave = false, MaxCharactersInPart = 40 * 1024 * 1024 };
    private static string ReadWord(byte[] bytes)
    {
        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false, Settings());
        var main = doc.MainDocumentPart ?? throw new InvalidDataException("Word thiếu nội dung.");
        if (main.Document?.Body?.Descendants<W.Text>().Any(t => !string.IsNullOrWhiteSpace(t.Text)) != true
            && !main.HeaderParts.Any(h => h.Header?.InnerText.Length > 0) && !main.FooterParts.Any(f => f.Footer?.InnerText.Length > 0))
            throw new InvalidDataException("Word không có chữ đọc được; tài liệu scan cần gửi ảnh.");
        var result = new StringBuilder();
        void Add(DocumentFormat.OpenXml.OpenXmlElement? root, string label)
        {
            if (root is null) return; result.AppendLine(label);
            foreach (var paragraph in root.Descendants<W.Paragraph>())
            {
                if (paragraph.Ancestors<W.TableCell>().FirstOrDefault() is { Parent: W.TableRow row } cell && row.Parent is W.Table table)
                    result.Append($"[Bảng, hàng {table.Elements<W.TableRow>().TakeWhile(r => r != row).Count() + 1}, ô {row.Elements<W.TableCell>().TakeWhile(c => c != cell).Count() + 1}] ");
                foreach (var element in paragraph.Descendants())
                {
                    if (element is W.Text text) result.Append(text.Text);
                    else if (element is W.TabChar) result.Append('\t');
                    else if (element is W.Break or W.CarriageReturn) result.AppendLine();
                }
                result.AppendLine(); CheckLength(result);
            }
        }
        Add(main.Document?.Body, "[Nội dung Word; các ô bảng theo thứ tự đọc]");
        foreach (var h in main.HeaderParts) Add(h.Header, "[Đầu trang]");
        foreach (var f in main.FooterParts) Add(f.Footer, "[Chân trang]");
        Add(main.FootnotesPart?.Footnotes, "[Chú thích chân trang]");
        Add(main.EndnotesPart?.Endnotes, "[Chú thích cuối]");
        Add(main.WordprocessingCommentsPart?.Comments, "[Bình luận]");
        return result.ToString();
    }

    private static string ReadWorkbook(byte[] bytes)
    {
        using var doc = SpreadsheetDocument.Open(new MemoryStream(bytes), false, Settings());
        var main = doc.WorkbookPart ?? throw new InvalidDataException("Excel thiếu workbook.");
        var workbook = main.Workbook ?? throw new InvalidDataException("Excel thiếu nội dung workbook.");
        var strings = main.SharedStringTablePart?.SharedStringTable?.Elements<S.SharedStringItem>().Select(s => s.InnerText).ToArray() ?? [];
        var formats = main.WorkbookStylesPart?.Stylesheet?.CellFormats?.Elements<S.CellFormat>().ToArray() ?? [];
        var custom = main.WorkbookStylesPart?.Stylesheet?.NumberingFormats?.Elements<S.NumberingFormat>().ToDictionary(f => f.NumberFormatId!.Value, f => f.FormatCode?.Value ?? "") ?? [];
        var result = new StringBuilder();
        var cells = 0;
        foreach (var sheet in workbook.Sheets?.Elements<S.Sheet>() ?? [])
        {
            result.AppendLine($"[Sheet: {sheet.Name}; trạng thái: {sheet.State?.Value.ToString() ?? "Visible"}]");
            if (main.GetPartById(sheet.Id!) is not WorksheetPart part) { result.AppendLine("[Không phải worksheet; chưa đọc]"); continue; }
            foreach (var cell in part.Worksheet?.Descendants<S.Cell>() ?? [])
            {
                var value = cell.CellValue?.Text ?? "";
                if (cell.DataType?.Value == S.CellValues.SharedString)
                { if (!int.TryParse(value, out var i) || i < 0 || i >= strings.Length) throw new InvalidDataException("Excel lỗi shared string."); value = strings[i]; }
                else if (cell.DataType?.Value == S.CellValues.InlineString) value = cell.InlineString?.InnerText ?? "";
                else if (cell.DataType?.Value == S.CellValues.Boolean) value = value == "1" ? "TRUE" : "FALSE";
                var style = cell.StyleIndex?.Value ?? 0;
                var format = style < formats.Length ? formats[style].NumberFormatId?.Value ?? 0 : 0;
                var suffix = custom.TryGetValue(format, out var code) ? " [format: " + code + "]" : "";
                if (format is >= 14 and <= 22 or >= 45 and <= 47 && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var days) && days >= 1 && days < 2_000_000)
                {
                    var date = main.Workbook.WorkbookProperties?.Date1904?.Value == true ? new DateTime(1904, 1, 1).AddDays(days) : DateTime.FromOADate(days);
                    suffix += " [ngày/giờ: " + date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "]";
                }
                if (value.Length == 0 && cell.CellFormula is null) continue;
                cells++;
                result.AppendLine($"{cell.CellReference}: {value}{suffix}" + (cell.CellFormula is { } formula ? " [công thức chưa tính lại: " + formula.Text + "]" : ""));
                CheckLength(result);
            }
        }
        if (cells == 0) throw new InvalidDataException("Excel không có ô dữ liệu đọc được.");
        return result.ToString();
    }

    private static void CheckLength(StringBuilder text)
    { if (text.Length > MaxTextCharacters) throw new InvalidDataException("Nội dung sau trích xuất vượt 120.000 ký tự; cần chia nhỏ tài liệu."); }

    public static string Describe(IEnumerable<AiAttachment> attachments) => string.Concat(attachments.Select(a =>
        "\n\nTệp tham khảo " + System.Text.Json.JsonSerializer.Serialize(a.Name) + " [ID " + a.Id + "]: " + a.Notice + "\n" + a.Text));

    public static IReadOnlyList<AiImage> NativeImages(IEnumerable<AiAttachment> attachments) => attachments
        .Where(a => a.IsImage && !a.HasImageOcr).Select(a => new AiImage(a.MimeType, a.Data)).ToArray();

    public static IReadOnlyList<AiFile> NativeFiles(IEnumerable<AiAttachment> attachments) => attachments
        .Where(a => a.IsPdf).Select(a =>
        {
            AiPdf.ValidateBytes(a.Data);
            return new AiFile(a.Name, a.MimeType, a.Data);
        }).ToArray();
}

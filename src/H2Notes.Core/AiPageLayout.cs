using System.Globalization;
using System.Text.Json;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using V = DocumentFormat.OpenXml.Vml;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace H2Notes.Core;

public sealed class AiPageLayout
{
    public int SchemaVersion { get; set; } = 1;
    public List<AiLayoutPage> Pages { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public const int MaxBytes = 32 * 1024 * 1024;

    public static AiPageLayout Parse(string json)
    {
        if (json.Length > MaxBytes) throw new InvalidDataException("Bố cục quá lớn.");
        var result = JsonSerializer.Deserialize<AiPageLayout>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 12 })
            ?? throw new InvalidDataException("Không có bố cục.");
        result.Validate(); return result;
    }

    public void Validate()
    {
        if (SchemaVersion != 1 || Pages is null || Pages.Count is < 1 or > 10 || Warnings is null || Warnings.Count > 20
            || Warnings.Any(s => s is null || s.Length > 2000)) throw new InvalidDataException("Bố cục Word không hợp lệ (tối đa 10 trang).");
        long characters = 0, imageBytes = 0;
        foreach (var page in Pages)
        {
            if (page is null || !Number(page.Width, 36, 1584) || !Number(page.Height, 36, 1584)
                || page.Items is null || page.Items.Count is < 1 or > 2000 || page.BackgroundPng is null)
                throw new InvalidDataException("Trang Word không hợp lệ hoặc không có chữ sửa được.");
            imageBytes += page.BackgroundPng.Length;
            if (imageBytes > MaxBytes || !page.BackgroundPng.AsSpan().StartsWith(new byte[] {137, 80, 78, 71, 13, 10, 26, 10}))
                throw new InvalidDataException("Ảnh bố cục không hợp lệ.");
            // Bound decoded PNG dimensions before a preview renderer allocates a bitmap.
            if (page.BackgroundPng.Length < 24) throw new InvalidDataException("Ảnh bố cục thiếu dữ liệu.");
            var width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(page.BackgroundPng.AsSpan(16));
            var height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(page.BackgroundPng.AsSpan(20));
            if (width is 0 or > 14400 || height is 0 or > 14400 || (long)width * height > 20_000_000)
                throw new InvalidDataException("Ảnh bố cục vượt giới hạn điểm ảnh.");
            foreach (var item in page.Items)
            {
                if (item is null || item.Text is null || item.Text.Length is < 1 or > 4000 || item.Text.Contains('\n') || item.Text.Contains('\r')
                    || item.Font is not ("Times New Roman" or "Arial") || !Number(item.FontSize, 6, 48)
                    || item.HorizontalScale is < 10 or > 400
                    || !Number(item.X, 0, page.Width) || !Number(item.Y, 0, page.Height)
                    || !Number(item.Width, .1, page.Width - item.X + .1) || !Number(item.Height, .1, page.Height - item.Y + .1)
                    || !Number(item.Confidence, 0, 1)) throw new InvalidDataException("Dòng chữ hoặc vị trí trong bố cục không hợp lệ.");
                XmlConvert.VerifyXmlChars(item.Text);
                if (item.Runs is not null && (item.Runs.Count is < 1 or > 256 || item.Runs.Any(r => r is null || string.IsNullOrEmpty(r.Text))
                    || string.Concat(item.Runs.Select(r => r.Text)) != item.Text)) throw new InvalidDataException("Định dạng từng đoạn không khớp nội dung dòng.");
                if ((characters += item.Text.Length) > 120000) throw new InvalidDataException("Chữ bố cục vượt 120.000 ký tự.");
            }
        }
    }

    private static bool Number(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;

    public byte[] CreateWord()
    {
        Validate();
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var main = doc.AddMainDocumentPart();
            var body = new W.Body(); main.Document = new W.Document(body);
            doc.PackageProperties.Title = "Bản Word tái dựng bố cục từ tài liệu gốc";
            doc.PackageProperties.Description = "Chữ OCR sửa được; đồ họa/vùng chưa nhận dạng giữ ảnh. Phông ước lượng, cần đối chiếu bản gốc. Không phải bản gốc có chữ ký số.";
            var id = 0;
            for (var p = 0; p < Pages.Count; p++)
            {
                var page = Pages[p];
                var anchor = new W.Paragraph(new W.ParagraphProperties(new W.SpacingBetweenLines { Before = "0", After = "0", Line = "20", LineRule = W.LineSpacingRuleValues.Exact }));
                var image = main.AddImagePart(ImagePartType.Png);
                using (var bytes = new MemoryStream(page.BackgroundPng)) image.FeedData(bytes);
                string Style(double x, double y, double width, double height, int z) => FormattableString.Invariant(
                    $"position:absolute;margin-left:{x:0.##}pt;margin-top:{y:0.##}pt;width:{width:0.##}pt;height:{height:0.##}pt;z-index:{z};mso-position-horizontal-relative:page;mso-position-vertical-relative:page");
                anchor.Append(new W.Run(new W.Picture(new V.Shape(new V.ImageData { RelationshipId = main.GetIdOfPart(image), Title = "Đồ họa gốc; chữ OCR đã tách để sửa" })
                { Id = "h2Background" + p, Style = Style(0, 0, page.Width, page.Height, -251658240), Stroked = false, Filled = false })));
                body.Append(anchor);
                foreach (var item in page.Items)
                {
                    var fontSize = ((int)Math.Round(item.FontSize * 2)).ToString(CultureInfo.InvariantCulture);
                    var runs = (item.Runs ?? [new() { Text = item.Text, Bold = item.Bold, Italic = item.Italic }]).Select(span =>
                        new W.Run(new W.RunProperties(new W.RunFonts { Ascii = item.Font, HighAnsi = item.Font, ComplexScript = item.Font },
                            new W.Bold { Val = span.Bold }, new W.Italic { Val = span.Italic }, new W.CharacterScale { Val = item.HorizontalScale },
                            new W.FontSize { Val = fontSize }), new W.Text(span.Text) { Space = SpaceProcessingModeValues.Preserve })).ToArray();
                    ++id;
                    var frame = new W.FrameProperties();
                    void Attribute(string name, string value) => frame.SetAttribute(new OpenXmlAttribute("w", name, "http://schemas.openxmlformats.org/wordprocessingml/2006/main", value));
                    Attribute("w", ((int)Math.Round((item.Width + 3) * 20)).ToString(CultureInfo.InvariantCulture));
                    Attribute("x", ((int)Math.Round(item.X * 20)).ToString(CultureInfo.InvariantCulture));
                    Attribute("y", ((int)Math.Round(Math.Max(0, item.Y - item.FontSize * .2) * 20)).ToString(CultureInfo.InvariantCulture));
                    Attribute("hAnchor", "page"); Attribute("vAnchor", "page"); Attribute("wrap", "none");
                    Attribute("hSpace", "0"); Attribute("vSpace", "0");
                    var paragraph = new W.Paragraph(new W.ParagraphProperties(frame, new W.SpacingBetweenLines
                    { Before = "0", After = "0", Line = ((int)(item.FontSize * 24)).ToString(CultureInfo.InvariantCulture), LineRule = W.LineSpacingRuleValues.Exact }));
                    paragraph.Append(runs);
                    body.Append(paragraph);
                }
                var section = new W.SectionProperties(new W.SectionType { Val = W.SectionMarkValues.NextPage },
                    new W.PageSize { Width = (uint)Math.Round(page.Width * 20), Height = (uint)Math.Round(page.Height * 20) },
                    new W.PageMargin { Top = 0, Bottom = 0, Left = 0, Right = 0, Header = 0, Footer = 0, Gutter = 0 });
                if (p == Pages.Count - 1) body.Append(section);
                else body.Append(new W.Paragraph(new W.ParagraphProperties(section)));
            }
        }
        return stream.ToArray();
    }
}

public sealed class AiLayoutPage
{
    public double Width { get; set; }
    public double Height { get; set; }
    public byte[] BackgroundPng { get; set; } = [];
    public List<AiLayoutText> Items { get; set; } = [];
}

public sealed class AiLayoutText
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string Text { get; set; } = "";
    public string Font { get; set; } = "Times New Roman";
    public double FontSize { get; set; } = 12;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public int HorizontalScale { get; set; } = 100;
    public double Confidence { get; set; }
    public List<AiLayoutRun>? Runs { get; set; }

    public void FormatSelection(int start, int length, bool? bold = null, bool? italic = null)
    {
        if (start < 0 || start > Text.Length || length <= 0 || length > Text.Length - start) return;
        var result = new List<AiLayoutRun>(); var offset = 0;
        foreach (var run in Runs ?? [new() { Text = Text, Bold = Bold, Italic = Italic }])
        {
            var boundaries = new[] { 0, Math.Clamp(start - offset, 0, run.Text.Length), Math.Clamp(start + length - offset, 0, run.Text.Length), run.Text.Length }.Distinct().Order().ToArray();
            foreach (var position in boundaries.Zip(boundaries.Skip(1)))
            {
                var selected = offset + position.First >= start && offset + position.Second <= start + length;
                result.Add(new() { Text = run.Text[position.First..position.Second], Bold = selected ? bold ?? run.Bold : run.Bold, Italic = selected ? italic ?? run.Italic : run.Italic });
            }
            offset += run.Text.Length;
        }
        var merged = new List<AiLayoutRun>();
        foreach (var run in result)
            if (merged.LastOrDefault() is { } previous && previous.Bold == run.Bold && previous.Italic == run.Italic) previous.Text += run.Text;
            else merged.Add(run);
        if (merged.Count > 256) throw new InvalidDataException("Quá nhiều đoạn định dạng trong một dòng.");
        Runs = merged;
    }
}

public sealed class AiLayoutRun
{
    public string Text { get; set; } = "";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
}

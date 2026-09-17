using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Avalonia.Controls;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;
using W = DocumentFormat.OpenXml.Wordprocessing;

internal static class AiLayoutTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Word layout contains editable text, original graphics and valid OpenXML without external links", () =>
        {
            var layout = Fixture();
            var bytes = layout.CreateWord();
            using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
            var errors = new OpenXmlValidator().Validate(doc).ToArray();
            Check(errors.Length == 0, string.Join("; ", errors.Select(e => e.Description)));
            Check(doc.MainDocumentPart!.Document!.Descendants<W.Text>().Single().Text == "Sample & <text>");
            Check(doc.MainDocumentPart.ImageParts.Count() == 1);
            Check(!doc.MainDocumentPart.ExternalRelationships.Any() && !doc.MainDocumentPart.HyperlinkRelationships.Any());
            Check(doc.MainDocumentPart.Document.Descendants<W.CharacterScale>().Single().Val!.Value == 92);
        });
        test("Word layout keeps per-page dimensions and rejects image-only or malformed outputs", () =>
        {
            var layout = Fixture(); layout.Pages.Add(Fixture().Pages.Single()); layout.Pages[1].Width = 500;
            using var doc = WordprocessingDocument.Open(new MemoryStream(layout.CreateWord()), false);
            Check(new OpenXmlValidator().Validate(doc).Count() == 0);
            Check(doc.MainDocumentPart!.Document!.Descendants<W.SectionProperties>().Count() == 2);
            foreach (var change in new Action<AiPageLayout>[] {
                l => l.Pages.Clear(), l => l.Pages[0].Items.Clear(), l => l.Pages[0].Width = double.NaN,
                l => l.Pages[0].Items[0].Font = "Unknown", l => l.Pages[0].Items[0].HorizontalScale = 500,
                l => l.Pages[0].Items[0].X = -1, l => l.Pages[0].Items[0].Text = "a\nb",
                l => l.Pages[0].BackgroundPng = [1,2,3], l => l.SchemaVersion = 99 })
            {
                var bad = Fixture(); change(bad); Throws<InvalidDataException>(bad.Validate);
            }
            var bomb = Fixture(); System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bomb.Pages[0].BackgroundPng.AsSpan(16), 15000);
            Throws<InvalidDataException>(bomb.Validate);
        });
        test("Layout selection formatting preserves exact text and limits changes to selected characters", () =>
        {
            var layout = Fixture(); var line = layout.Pages[0].Items[0];
            line.FormatSelection(7, 1, true); layout.Validate();
            Check(line.Runs!.Count == 3 && line.Runs[1].Text == "&" && line.Runs[1].Bold && !line.Runs[0].Bold);
            Check(string.Concat(line.Runs.Select(r => r.Text)) == line.Text);
            using var doc = WordprocessingDocument.Open(new MemoryStream(layout.CreateWord()), false);
            Check(new OpenXmlValidator().Validate(doc).Count() == 0);
            line.Runs[0].Text = "wrong"; Throws<InvalidDataException>(layout.Validate);
        });
        test("AI layout artifact is an explicit source request, never silently exports blank or model-rewritten Word", () =>
        {
            var id = Guid.NewGuid();
            var text = "```h2-file\n" + JsonSerializer.Serialize(new { fileName = "Copy.docx", text = "", sheets = Array.Empty<object>(), layoutSourceId = id }) + "\n```";
            var artifact = AiArtifacts.Parse(text).Single();
            Check(artifact.LayoutSourceId == id && AiArtifacts.Preview(artifact).Contains("Chưa chạy OCR"));
            Throws<InvalidOperationException>(() => AiArtifacts.Create(artifact));
            artifact.Text = "replacement"; Throws<InvalidDataException>(() => AiArtifacts.Validate(artifact));
            artifact.Text = ""; artifact.FileName = "x.xlsx"; Throws<InvalidDataException>(() => AiArtifacts.Validate(artifact));
            var source = AiDocuments.Read("test.txt", Encoding.UTF8.GetBytes("literal data"));
            Check(AiDocuments.Describe([source]).Contains(source.Id.ToString()));
        });
        test("Layout review exposes editable OCR and formatting without saving or contacting a model", () =>
        {
            var layout = Fixture();
            var window = new LayoutReviewWindow(layout);
            window.Show(); window.UpdateLayout();
            var controls = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).ToArray();
            Check(controls.OfType<TextBlock>().SelectMany(b => b.Inlines?.OfType<global::Avalonia.Controls.Documents.Run>() ?? [])
                .Count(r => r.Text == "Sample & <text>") == 1, "Preview duplicates OCR text");
            var text = controls.OfType<TextBox>().First(t => t.Text == "Sample & <text>");
            text.Text = "Corrected text";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Check(layout.Pages[0].Items[0].Text == "Corrected text");
            Check(controls.OfType<Button>().Any(b => b.Name == "LayoutReviewSave"));
            Check(controls.OfType<ComboBox>().Any(b => b.Name == "LayoutPage"));
            window.Close();
        });
    }

    public static AiPageLayout Fixture()
    {
        // Real one-pixel PNG; embedded only as synthetic fixture graphics.
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAE0lEQVR4nGP8//8/AwMDEwMYAAAkBgMBXaJOiAAAAABJRU5ErkJggg==");
        return new AiPageLayout { Warnings = ["Synthetic fixture, approximate font"], Pages = [new() { Width = 595, Height = 842, BackgroundPng = png,
            Items = [new() { X = 60, Y = 60, Width = 150, Height = 16, Text = "Sample & <text>", FontSize = 12, HorizontalScale = 92 }] }] };
    }
    private static void Check(bool condition, string message = "Layout assertion failed") { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}

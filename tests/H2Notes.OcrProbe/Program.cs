using System.Diagnostics;
using H2Notes.Core;

if (args.Length == 5 && args[0] == "--allow-layout")
{
    var input = AiDocuments.Read(Path.GetFullPath(args[2]));
    var hash = input.Sha256;
    using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    var layout = await AiPdfProcessor.PrepareLayoutAsync(input, new AiPdfSettings { RuntimeRoot = Path.GetFullPath(args[1]), MaxPages = 10, TimeoutSeconds = 600 }, Path.GetFullPath(args[3]), token: stop.Token);
    if (AiDocuments.Read(Path.GetFullPath(args[2])).Sha256 != hash) throw new Exception("Source changed");
    using var output = new FileStream(Path.GetFullPath(args[4]), FileMode.CreateNew, FileAccess.Write);
    await System.Text.Json.JsonSerializer.SerializeAsync(output, layout);
    Console.WriteLine($"PASS layout subprocess: pages={layout.Pages.Count}; lines={layout.Pages.Sum(p => p.Items.Count)}; sourceUnchanged=true; noLlm=true");
    return 0;
}

if (args.Length == 3 && args[0] == "--layout-json")
{
    var layout = AiPageLayout.Parse(await File.ReadAllTextAsync(args[1]));
    var bytes = layout.CreateWord();
    using var check = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(new MemoryStream(bytes), false);
    var errors = new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(check).ToArray();
    foreach (var error in errors) Console.Error.WriteLine(error.Description + " " + error.Path?.XPath);
    if (errors.Length != 0) return 1;
    using var output = new FileStream(Path.GetFullPath(args[2]), FileMode.CreateNew, FileAccess.Write);
    await output.WriteAsync(bytes);
    Console.WriteLine($"PASS Word layout: pages={layout.Pages.Count}; editableLines={layout.Pages.Sum(p => p.Items.Count)}; bytes={bytes.Length}; schemaValid=true");
    return 0;
}

if (!(args.Length == 5 || args.Length == 7 && args[5] == "--markdown-output") || args[0] != "--allow-local-ocr" || !Enum.TryParse<AiPdfEngine>(args[1], out var engine) || engine == AiPdfEngine.Direct)
{
    Console.Error.WriteLine("Use --allow-local-ocr Docling|GotOcr|MinerU runtime-root synthetic-pdf bridge-path. No LLM request is made.");
    return 2;
}
try
{
    var clock = Stopwatch.StartNew();
    var settings = new AiPdfSettings { Engine = engine, OcrImages = true, RuntimeRoot = Path.GetFullPath(args[2]), TimeoutSeconds = 600, MaxPages = 3 };
    var input = AiDocuments.Read(Path.GetFullPath(args[3]));
    Console.WriteLine(AiPdfProcessor.RuntimeStatus(settings, Path.GetFullPath(args[4])));
    var result = await AiPdfProcessor.PrepareAttachmentAsync(input, settings, Path.GetFullPath(args[4]),
        new Progress<string>(message => Console.WriteLine(message)));
    if (result.IsPdf || !(result.HasImageOcr || result.MimeType == "text/markdown") || string.IsNullOrWhiteSpace(result.Text) || result.SourceName != input.Name)
        throw new InvalidDataException("Incomplete Markdown/provenance.");
    var user = new AiMessage { Attachments = [result], Content = "Summarize the fixture." };
    var turns = AiProjectContext.Prepare(new(), user, "");
    if (turns.Any(t => t.Files is { Count: > 0 } || t.Images is { Count: > 0 }) || !turns.Last().Content.Contains(result.Text))
        throw new InvalidDataException("Prepared request lost the converted Markdown.");
    if (input.IsImage && (!result.Data.SequenceEqual(input.Data) || result.Sha256 != input.Sha256))
        throw new InvalidDataException("Original image was changed.");
    if (args.Length == 7)
    {
        using var output = new FileStream(Path.GetFullPath(args[6]), FileMode.CreateNew, FileAccess.Write);
        await output.WriteAsync(System.Text.Encoding.UTF8.GetBytes(result.Text));
    }
    Console.WriteLine($"PASS {engine}: chars={result.Text.Length}; originalBinaryNotSent=true; provenance=true; imagePreserved={input.IsImage}; seconds={clock.Elapsed.TotalSeconds:0.0}");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL " + error.GetType().Name + ": " + error.Message);
    return 1;
}

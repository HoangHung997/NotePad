using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using H2Notes.Core;
using static ReasoningCapabilityTests;

internal static class PdfAiTests
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Count 1/Kids[3 0 R]>>endobj\n3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]>>endobj\n%%EOF");

    public static void Run(Action<string, Action> test)
    {
        test("Image OCR is opt-in; converted images keep originals but send text across history", () =>
        {
            var image = AiDocuments.Read("scan.png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg=="));
            var turns = AiProjectContext.Prepare(new(), new() { Attachments = [image] }, "");
            var settings = new AiPdfSettings { Engine = AiPdfEngine.MinerU };
            Check(!settings.OcrImages && !AiPdfProcessor.NeedsPreparation(turns, settings));
            Check(ReferenceEquals(image, AiPdfProcessor.PrepareAttachmentAsync(image, settings, "missing").GetAwaiter().GetResult()));
            settings.OcrImages = true;
            Check(AiPdfProcessor.NeedsPreparation(turns, settings));
            settings.Engine = AiPdfEngine.Direct;
            Check(!AiPdfProcessor.NeedsPreparation(turns, settings));
            var bytes = image.Data.ToArray();
            image.Text = "OCR fixture text"; image.PdfEngine = AiPdfEngine.MinerU;
            var stored = ProjectWorkspaceStore.Clone(image);
            Check(stored.HasImageOcr && stored.Data.SequenceEqual(bytes));
            var conversation = new AiConversation { Messages = [new() { Attachments = [stored] }] };
            var prepared = AiProjectContext.Prepare(conversation, new() { Content = "Summarize", Attachments = [stored] }, "");
            Check(prepared.All(t => t.Images is null or { Count: 0 }));
            Check(prepared.Count(t => t.Content.Contains("OCR fixture text")) == 2);
        });
        test("PDF accepts raw bytes, preserves history and never labels it as extracted text", () =>
        {
            var pdf = AiDocuments.Read("document.PDF", Pdf);
            Check(pdf.IsPdf && pdf.Text == "" && pdf.Data.SequenceEqual(Pdf));
            var conversation = new AiConversation { Messages = [new() { Content = "read", Attachments = [pdf] }] };
            var copy = JsonSerializer.Deserialize<AiConversation>(JsonSerializer.Serialize(conversation))!;
            var turn = AiHistory.RequestTurns(copy).Single();
            Check(turn.Files!.Single().Data.SequenceEqual(Pdf) && turn.Images!.Count == 0);
            var pending = AiProjectContext.Prepare(new(), new() { Attachments = [pdf] }, "").Last();
            Check(pending.Files!.Count == 1);
        });
        test("PDF validates signature and 8MB bound without trusting MIME or history", () =>
        {
            Throws<InvalidDataException>(() => AiDocuments.Read("bad.pdf", [1, 2, 3]));
            Throws<InvalidDataException>(() => AiDocuments.Read("empty.pdf", []));
            Throws<InvalidDataException>(() => AiDocuments.Read("large.pdf", new byte[AiDocuments.MaxFileBytes + 1]));
            Throws<InvalidDataException>(() => AiDocuments.NativeFiles([new() { MimeType = "application/pdf", Data = [] }]));
        });
        test("PDF preprocessed Markdown replays as text with source provenance, never a native file", () =>
        {
            var attachment = AiDocuments.Read("document.md", Encoding.UTF8.GetBytes("# Extracted\nAll pages."));
            attachment.PdfEngine = AiPdfEngine.Docling; attachment.SourceName = "document.pdf"; attachment.SourceSha256 = "source-hash";
            var conversation = new AiConversation { Messages = [new() { Attachments = [attachment] }] };
            var copy = JsonSerializer.Deserialize<AiConversation>(JsonSerializer.Serialize(conversation))!;
            var turn = AiHistory.RequestTurns(copy).Single();
            Check(turn.Content.Contains("All pages.") && turn.Files!.Count == 0);
            Check(copy.Messages[0].Attachments[0].SourceSha256 == "source-hash");
        });
        foreach (var protocol in new[] { AiProtocol.OpenAiResponses, AiProtocol.OpenAiChat, AiProtocol.Gemini })
            test("PDF native wire shape and historical bytes: " + protocol, () =>
            {
                var profile = Profile(protocol, protocol == AiProtocol.Gemini ? "gemini-2.5-pro" : "gpt-4.1");
                using var handler = new CapabilityStub(protocol); using var client = new AiClient(handler);
                var turns = new AiTurn[] { new("user", "old", Files: [new("old.pdf", "application/pdf", Pdf)]), new("assistant", "read"), new("user", "new", Files: [new("new.pdf", "application/pdf", Pdf)]) };
                Drain(client.StreamEvents(profile, "test-secret", turns)).GetAwaiter().GetResult();
                using var json = JsonDocument.Parse(handler.Body!); var root = json.RootElement;
                var messages = root.GetProperty(protocol == AiProtocol.OpenAiResponses ? "input" : protocol == AiProtocol.Gemini ? "contents" : "messages");
                foreach (var index in new[] { 0, 2 })
                {
                    var parts = messages[index].GetProperty(protocol == AiProtocol.Gemini ? "parts" : "content");
                    var file = parts[1];
                    if (protocol == AiProtocol.Gemini)
                    {
                        Check(file.GetProperty("inlineData").GetProperty("mimeType").GetString() == "application/pdf");
                        Check(file.GetProperty("inlineData").GetProperty("data").GetString() == Convert.ToBase64String(Pdf));
                    }
                    else
                    {
                        Check(file.GetProperty("type").GetString() == (protocol == AiProtocol.OpenAiResponses ? "input_file" : "file"));
                        if (protocol == AiProtocol.OpenAiChat) file = file.GetProperty("file");
                        Check(file.GetProperty("file_data").GetString() == "data:application/pdf;base64," + Convert.ToBase64String(Pdf));
                    }
                }
                Check(!handler.Body!.Contains("test-secret"));
            });
        test("PDF rejects Ollama and unsupported or spoofed compatible providers before sending", () =>
        {
            foreach (var profile in new[] { Profile(AiProtocol.Ollama, "qwen3"), Profile(AiProtocol.OpenAiResponses, "unknown"),
                new AiProfile { Protocol = AiProtocol.OpenAiChat, Model = "gpt-4.1", BaseUrl = "https://api.openai.com.evil.test/v1" } })
            {
                using var handler = new CapabilityStub(profile.Protocol); using var client = new AiClient(handler);
                var error = Throws<InvalidOperationException>(() => Drain(client.StreamEvents(profile, "", [new("user", "", Files: [new("x.pdf", "application/pdf", Pdf)])])).GetAwaiter().GetResult());
                Check(handler.Calls == 0 && error.Message.Contains("Markdown"));
            }
        });
        test("PDF supported list covers common documented vision models but not arbitrary suffixes", () =>
        {
            foreach (var model in new[] { "gpt-4.1", "gpt-4.1-mini", "gpt-4.1-nano", "gpt-5-mini", "gpt-5-nano", "gpt-5.1", "gpt-5.4" })
                Check(AiModelCapabilities.SupportsNativePdf(Profile(AiProtocol.OpenAiChat, model)));
            Check(!AiModelCapabilities.SupportsNativePdf(Profile(AiProtocol.OpenAiChat, "gpt-4.1-custom")));
        });
        test("PDF budget counts history and rejects unsupported attachment roles", () =>
        {
            var bytes = new byte[AiDocuments.MaxFileBytes]; Pdf.CopyTo(bytes, 0);
            Throws<InvalidOperationException>(() => AiPdf.ValidateBudget([new("user", "", Files: [new("x.pdf", "application/pdf", bytes), new("y.pdf", "application/pdf", Pdf)])]));
            Throws<InvalidOperationException>(() => AiPdf.ValidateBudget([new("system", "", Files: [new("x.pdf", "application/pdf", Pdf)])]));
            Throws<InvalidDataException>(() => AiPdf.ValidateBudget([new("user", "", Files: [new("x.bin", "application/octet-stream", Pdf)])]));
        });
        test("PDF direct and text-only preprocessing never require a runtime or download", () =>
        {
            var profile = Profile(AiProtocol.OpenAiResponses, "gpt-4.1");
            AiTurn[] turns = [new("user", "", Files: [new("x.pdf", "application/pdf", Pdf)])];
            var result = AiPdfProcessor.PrepareTurnsAsync(turns, profile, new(), "missing").GetAwaiter().GetResult();
            Check(ReferenceEquals(turns, result));
            AiTurn[] plain = [new("user", "text")];
            var invalid = new AiPdfSettings { Engine = (AiPdfEngine)100, RuntimeRoot = "invalid", MaxPages = -1 };
            Check(ReferenceEquals(plain, AiPdfProcessor.PrepareTurnsAsync(plain, profile, invalid, "missing").GetAwaiter().GetResult()));
            Check(AiPdfProcessor.RuntimeStatus(invalid, "missing").Length > 0);
        });
        test("PDF preprocessing honors cancellation before reading runtime or launching", () =>
        {
            using var stop = new CancellationTokenSource(); stop.Cancel();
            Throws<OperationCanceledException>(() => AiPdfProcessor.PrepareAttachmentAsync(AiDocuments.Read("x.pdf", Pdf),
                new() { Engine = AiPdfEngine.Docling }, "missing", token: stop.Token).GetAwaiter().GetResult());
        });
        test("PDF preprocessing refuses untrusted bridge and invalid page/time settings", () =>
        {
            foreach (var settings in new[] { new AiPdfSettings { Engine = AiPdfEngine.Docling, MaxPages = 101 },
                new AiPdfSettings { Engine = AiPdfEngine.Docling, TimeoutSeconds = 601 }, new AiPdfSettings { Engine = AiPdfEngine.Docling } })
                Throws<InvalidOperationException>(() => AiPdfProcessor.PrepareAttachmentAsync(AiDocuments.Read("x.pdf", Pdf), settings, "malicious-command").GetAwaiter().GetResult());
        });
        test("PDF OCR output reader rejects overflow, invalid UTF8, empty and NUL without truncating", () =>
        {
            var path = Path.GetTempFileName();
            try
            {
                foreach (var content in new[] { "", " \n ", "bad\0data", new string('x', AiDocuments.MaxTextCharacters + 1) })
                {
                    File.WriteAllText(path, content, new UTF8Encoding(false));
                    Throws<InvalidDataException>(() => ReadMarkdown(path).GetAwaiter().GetResult());
                }
                File.WriteAllBytes(path, [255, 255]);
                Throws<DecoderFallbackException>(() => ReadMarkdown(path).GetAwaiter().GetResult());
                File.WriteAllText(path, new string('a', AiDocuments.MaxTextCharacters), new UTF8Encoding(false));
                Check(ReadMarkdown(path).GetAwaiter().GetResult().Length == AiDocuments.MaxTextCharacters);
            }
            finally { File.Delete(path); }
        });
        test("PDF runtime manifest is readiness-only and cannot choose executable or escape model directory", () =>
        {
            var folder = Directory.CreateTempSubdirectory("h2-pdf-runtime-test-").FullName;
            try
            {
                var bridge = Path.Combine(folder, "tools", "ocr", "convert.py");
                Directory.CreateDirectory(Path.GetDirectoryName(bridge)!); File.WriteAllText(bridge, "# synthetic, never executed");
                var expectedPython = OperatingSystem.IsWindows() ? "venv/Scripts/python.exe" : "venv/bin/python";
                var python = Path.Combine(folder, expectedPython);
                Directory.CreateDirectory(Path.GetDirectoryName(python)!); File.WriteAllText(python, "synthetic, never executed");
                var models = Path.Combine(folder, "models", "docling"); Directory.CreateDirectory(models);
                File.WriteAllText(Path.Combine(models, "placeholder"), "synthetic");
                var settings = new AiPdfSettings { Engine = AiPdfEngine.Docling, RuntimeRoot = folder };
                void Manifest(bool ready, string executable, string modelPath) => File.WriteAllText(Path.Combine(folder, "runtime.json"),
                    JsonSerializer.Serialize(new { schemaVersion = 1, pythonExecutable = executable, engines = new { docling = new { ready, modelsPath = modelPath } } }));
                foreach (var (ready, executable, modelPath) in new[] { (false, expectedPython, "models/docling"),
                    (true, "../../evil.exe", "models/docling"), (true, expectedPython, "../../documents") })
                {
                    Manifest(ready, executable, modelPath);
                    Check(!AiPdfProcessor.RuntimeStatus(settings, bridge).Contains("đã được"));
                    Throws<InvalidOperationException>(() => AiPdfProcessor.PrepareAttachmentAsync(AiDocuments.Read("x.pdf", Pdf), settings, bridge).GetAwaiter().GetResult());
                }
                Manifest(true, expectedPython, "models/docling");
                Check(AiPdfProcessor.RuntimeStatus(settings, bridge).Contains("đã được"));
                var runtime = typeof(AiPdfProcessor).GetMethod("ReadRuntime", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [settings, bridge]);
                var input = Path.Combine(folder, "name with spaces & shell.pdf");
                var start = (ProcessStartInfo)typeof(AiPdfProcessor).GetMethod("BuildStartInfo", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, [runtime, input, Path.Combine(folder, "out.md"), folder, settings])!;
                Check(!start.UseShellExecute && start.CreateNoWindow && start.Arguments == "" && start.ArgumentList.Contains(input));
                Check(start.ArgumentList[0] == "-I" && start.FileName == python);
                Check(start.Environment["HF_HUB_OFFLINE"] == "1" && !start.Environment.ContainsKey("OPENAI_API_KEY") && !start.Environment.ContainsKey("PYTHONPATH"));
                File.WriteAllText(Path.Combine(folder, "runtime.json"), "[]");
                Check(!AiPdfProcessor.RuntimeStatus(settings, bridge).Contains("đã được"));
            }
            finally { Directory.Delete(folder, true); }
        });
        if (OperatingSystem.IsWindows())
        {
            test("PDF bounded local subprocess cancels promptly", () => Task.Run(async () =>
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
                var watch = Stopwatch.StartNew();
                try { await RunChild("Start-Sleep -Seconds 30", stop.Token); throw new Exception("Expected cancellation"); }
                catch (OperationCanceledException) { Check(watch.Elapsed < TimeSpan.FromSeconds(8)); }
            }).GetAwaiter().GetResult());
            test("PDF bounded local subprocess stops stdout flooding and redacts failed diagnostics", () => Task.Run(async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await RunChild("[Console]::Write(('x' * 100000)); Start-Sleep -Seconds 30", timeout.Token); throw new Exception("Expected output limit"); }
                catch (InvalidDataException) { }
                try { await RunChild("[Console]::Error.Write('synthetic-secret'); exit 3", timeout.Token); throw new Exception("Expected readiness error"); }
                catch (InvalidOperationException error) { Check(!error.Message.Contains("synthetic-secret")); }
            }).GetAwaiter().GetResult());
        }
    }

    private static Task<string> ReadMarkdown(string path) => (Task<string>)typeof(AiPdfProcessor)
        .GetMethod("ReadMarkdownAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [path, CancellationToken.None])!;

    // The shared top-level runner also declares Throws; keep lookup inside this test class.
    private static T Throws<T>(Action action) where T : Exception => ReasoningCapabilityTests.Throws<T>(action);

    private static Task RunChild(string fixedTestScript, CancellationToken token)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", fixedTestScript }) start.ArgumentList.Add(arg);
        return (Task)typeof(AiPdfProcessor).GetMethod("RunAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [start, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".md"), token, AiDocuments.MaxTextCharacters * 4])!;
    }
}

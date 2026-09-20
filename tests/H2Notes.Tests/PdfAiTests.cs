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
            var turns = AiLegacyRequestContext.Prepare(new(), new() { Attachments = [image] }, "");
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
            var prepared = AiLegacyRequestContext.Prepare(conversation, new() { Content = "Summarize", Attachments = [stored] }, "");
            Check(prepared.Skip(1).Take(prepared.Count - 2).All(t => (t.Images?.Count ?? 0) == 0));
            Check(prepared.Count(t => t.Content.Contains("OCR fixture text")) == 2);
            Check((prepared[^1].Images?.Count ?? 0) == 0, "Cached OCR image should send text rather than image bytes");
        });
        test("PDF current send keeps raw bytes while historical turns never resend them", () =>
        {
            var pdf = AiDocuments.Read("document.PDF", Pdf);
            Check(pdf.IsPdf && pdf.Text == "" && pdf.Data.SequenceEqual(Pdf));
            var conversation = new AiConversation { Messages = [new() { Content = "read", Attachments = [pdf] }] };
            var copy = JsonSerializer.Deserialize<AiConversation>(JsonSerializer.Serialize(conversation))!;
            var turn = AiHistory.RequestTurns(copy).Single();
            Check((turn.Files?.Count ?? 0) == 0 && (turn.Images?.Count ?? 0) == 0);
            Check(turn.Content.Contains("document.PDF") && turn.Content.Contains("PDF gốc"));
            var pending = AiLegacyRequestContext.Prepare(new(), new() { Attachments = [pdf] }, "").Last();
            Check(pending.Files!.Count == 1 && pending.Files[0].Data.SequenceEqual(Pdf));
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
            Check(turn.Content.Contains("All pages.") && (turn.Files?.Count ?? 0) == 0 && (turn.Images?.Count ?? 0) == 0);
            Check(copy.Messages[0].Attachments[0].SourceSha256 == "source-hash");
        });
        foreach (var protocol in new[] { AiProtocol.OpenAiResponses, AiProtocol.OpenAiChat, AiProtocol.Gemini })
            test("PDF native wire shape and explicitly supplied bytes: " + protocol, () =>
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
            var pdf = new AiTurn("user", "x", Files: [new("a.pdf", "application/pdf", Pdf)]);
            foreach (var profile in new[]
            {
                new AiProfile { Protocol = AiProtocol.Ollama, BaseUrl = "http://localhost:11434", Model = "llava" },
                new AiProfile { Protocol = AiProtocol.OpenAiChat, BaseUrl = "https://example.test/v1", Model = "gpt-4.1" },
                new AiProfile { Protocol = AiProtocol.OpenAiResponses, BaseUrl = "https://example.test/v1", Model = "gpt-4.1" },
                new AiProfile { Protocol = AiProtocol.Gemini, BaseUrl = "https://example.test/v1", Model = "gemini-2.5-pro" }
            }) Throws<InvalidOperationException>(() => AiPdf.ValidateRequest(profile, [pdf]));
        });
        test("PDF supported list covers common documented vision models but not arbitrary suffixes", () =>
        {
            foreach (var profile in new[]
            {
                Profile(AiProtocol.OpenAiResponses, "gpt-4.1"), Profile(AiProtocol.OpenAiResponses, "gpt-5"),
                Profile(AiProtocol.OpenAiChat, "gpt-4.1-mini"), Profile(AiProtocol.Gemini, "gemini-2.5-pro")
            }) AiPdf.ValidateRequest(profile, [new("user", "x", Files: [new("a.pdf", "application/pdf", Pdf)])]);
            Throws<InvalidOperationException>(() => AiPdf.ValidateRequest(Profile(AiProtocol.OpenAiResponses, "gpt-4.1-evil"), [new("user", "x", Files: [new("a.pdf", "application/pdf", Pdf)])]));
        });
        test("PDF budget counts history and rejects unsupported attachment roles", () =>
        {
            var over = new byte[AiDocuments.MaxFileBytes]; "%PDF-"u8.CopyTo(over);
            Throws<InvalidOperationException>(() => AiPdf.ValidateBudget([new("user", "a", Files: [new("a.pdf", "application/pdf", over), new("b.pdf", "application/pdf", over)])]));
            Throws<InvalidOperationException>(() => AiPdf.ValidateBudget([new("assistant", "a", Files: [new("a.pdf", "application/pdf", Pdf)])]));
        });
        test("PDF direct and text-only preprocessing never require a runtime or download", () =>
        {
            var direct = new AiPdfSettings { Engine = AiPdfEngine.Direct };
            var turns = new[] { new AiTurn("user", "x", Files: [new("a.pdf", "application/pdf", Pdf)]) };
            Check(ReferenceEquals(turns, AiPdfProcessor.PrepareTurnsAsync(turns, Profile(AiProtocol.Gemini, "gemini-2.5-pro"), direct, "missing").GetAwaiter().GetResult()));
            var textOnly = new[] { new AiTurn("user", "plain") };
            var local = new AiPdfSettings { Engine = AiPdfEngine.Docling };
            Check(ReferenceEquals(textOnly, AiPdfProcessor.PrepareTurnsAsync(textOnly, Profile(AiProtocol.Gemini, "gemini-2.5-pro"), local, "missing").GetAwaiter().GetResult()));
        });
        test("PDF preprocessing honors cancellation before reading runtime or launching", () =>
        {
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Throws<OperationCanceledException>(() => AiPdfProcessor.PrepareTurnsAsync([new("user", "x", Files: [new("a.pdf", "application/pdf", Pdf)])], new(), new() { Engine = AiPdfEngine.Docling }, "missing", token: cancel.Token).GetAwaiter().GetResult());
        });
        test("PDF preprocessing refuses untrusted bridge and invalid page/time settings", () =>
        {
            var temp = Path.Combine(Path.GetTempPath(), "h2-pdf-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
            try
            {
                var manifest = Path.Combine(temp, "runtime.json"); File.WriteAllText(manifest, "{}");
                var settings = new AiPdfSettings { Engine = AiPdfEngine.Docling, RuntimeRoot = temp };
                Throws<InvalidOperationException>(() => AiPdfProcessor.PrepareTurnsAsync([new("user", "x", Files: [new("a.pdf", "application/pdf", Pdf)])], new(), settings, Path.Combine(temp, "evil.py")).GetAwaiter().GetResult());
                settings.MaxPages = 101;
                Throws<InvalidOperationException>(() => AiPdfProcessor.PrepareTurnsAsync([new("user", "x", Files: [new("a.pdf", "application/pdf", Pdf)])], new(), settings, "missing").GetAwaiter().GetResult());
            }
            finally { Directory.Delete(temp, true); }
        });
        test("PDF OCR output reader rejects overflow, invalid UTF8, empty and NUL without truncating", () =>
        {
            var method = typeof(AiPdfProcessor).GetMethod("ReadMarkdownAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            var dir = Path.Combine(Path.GetTempPath(), "h2-pdf-output-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            try
            {
                var file = Path.Combine(dir, "out.md");
                File.WriteAllText(file, "ok"); Check((string)((Task<string>)method.Invoke(null, [file, CancellationToken.None])!).GetAwaiter().GetResult() == "ok");
                File.WriteAllBytes(file, new byte[(AiDocuments.MaxTextCharacters * 4) + 1]); Throws<InvalidDataException>(() => ((Task<string>)method.Invoke(null, [file, CancellationToken.None])!).GetAwaiter().GetResult());
                File.WriteAllBytes(file, [0xff, 0xfe]); Throws<DecoderFallbackException>(() => ((Task<string>)method.Invoke(null, [file, CancellationToken.None])!).GetAwaiter().GetResult());
                File.WriteAllText(file, "   "); Throws<InvalidDataException>(() => ((Task<string>)method.Invoke(null, [file, CancellationToken.None])!).GetAwaiter().GetResult());
                File.WriteAllText(file, "a\0b"); Throws<InvalidDataException>(() => ((Task<string>)method.Invoke(null, [file, CancellationToken.None])!).GetAwaiter().GetResult());
            }
            finally { Directory.Delete(dir, true); }
        });
        test("PDF runtime manifest is readiness-only and cannot choose executable or escape model directory", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "h2-pdf-runtime-" + Guid.NewGuid().ToString("N"));
            var tools = Path.Combine(root, "tools", "ocr"); var runtime = Path.Combine(root, "runtime"); Directory.CreateDirectory(tools); Directory.CreateDirectory(runtime);
            var bridge = Path.Combine(tools, "convert.py"); File.WriteAllText(bridge, "# safe");
            var venv = Path.Combine(runtime, "venv", OperatingSystem.IsWindows() ? "Scripts" : "bin"); Directory.CreateDirectory(venv);
            File.WriteAllText(Path.Combine(venv, OperatingSystem.IsWindows() ? "python.exe" : "python"), "python");
            foreach (var engine in new[] { "docling", "got-ocr", "mineru" }) { var model = Path.Combine(runtime, "models", engine); Directory.CreateDirectory(model); File.WriteAllText(Path.Combine(model, "weight.bin"), "x"); }
            File.WriteAllText(Path.Combine(runtime, "runtime.json"), "{\"schemaVersion\":1,\"pythonExecutable\":\"" + (OperatingSystem.IsWindows() ? "venv/Scripts/python.exe" : "venv/bin/python") + "\",\"engines\":{\"docling\":{\"ready\":true,\"modelsPath\":\"models/docling\"},\"got-ocr\":{\"ready\":true,\"modelsPath\":\"models/got-ocr\"},\"mineru\":{\"ready\":true,\"modelsPath\":\"models/mineru\"}}}");
            try
            {
                var settings = new AiPdfSettings { Engine = AiPdfEngine.Docling, RuntimeRoot = runtime };
                Check(AiPdfProcessor.RuntimeStatus(settings, bridge).Contains("sẵn sàng offline"));
            }
            finally { Directory.Delete(root, true); }
        });
        test("PDF bounded local subprocess cancels promptly", () => RunProcessLimitCase(true));
        test("PDF bounded local subprocess stops stdout flooding and redacts failed diagnostics", () => RunProcessLimitCase(false));
    }

    private static AiProfile Profile(AiProtocol protocol, string model) => new()
    {
        Protocol = protocol,
        Model = model,
        BaseUrl = protocol == AiProtocol.Gemini ? "https://generativelanguage.googleapis.com/v1beta"
            : protocol == AiProtocol.OpenAiResponses ? "https://api.openai.com/v1" : "https://api.openai.com/v1"
    };

    private static void Check(bool value, string message = "PDF assertion failed") { if (!value) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (TargetInvocationException ex) when (ex.InnerException is T) { return; }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void RunProcessLimitCase(bool cancel)
    {
        var method = typeof(AiPdfProcessor).GetMethod("RunAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var temp = Path.Combine(Path.GetTempPath(), "h2-pdf-process-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        var output = Path.Combine(temp, "out.md");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                start.ArgumentList.Add("/c"); start.ArgumentList.Add(cancel ? "ping -n 30 127.0.0.1 >NUL" : "for /L %i in (1,1,8000) do @echo diagnostic-secret-%i 1>&2");
                using var cts = new CancellationTokenSource(cancel ? 180 : 4000);
                if (cancel)
                    Throws<OperationCanceledException>(() => ((Task)method.Invoke(null, [start, output, cts.Token, (int)(AiDocuments.MaxTextCharacters * 4)])!).GetAwaiter().GetResult());
                else
                    Throws<InvalidDataException>(() => ((Task)method.Invoke(null, [start, output, cts.Token, (int)(AiDocuments.MaxTextCharacters * 4)])!).GetAwaiter().GetResult());
            }
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    private sealed class CapabilityStub(AiProtocol protocol) : HttpMessageHandler, IDisposable
    {
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var payload = protocol switch
            {
                AiProtocol.OpenAiResponses => "data: {\"type\":\"response.output_text.delta\",\"delta\":\"ok\"}\n\ndata: {\"type\":\"response.completed\"}\n\n",
                AiProtocol.Gemini => "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}\n\n",
                _ => "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n"
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(payload) };
        }
    }
}

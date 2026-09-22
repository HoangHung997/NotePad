using System.Text;
using System.Text.Json;
using H2AgentLab;
using H2AgentLab.Runtime;
using H2AgentLab.Web;
using H2Notes.Core;

internal static class H2AgentCapabilityRepairTests
{
    public static void Run(Action<string, Action> test)
    {
        test("News export preserves Vietnamese source metadata and rejects invented stale duplicate selections", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var feed = new WebFeedPage("https://example.test/rss", now, 2, 0, [
                new("Mỗi lượng vàng giảm một triệu đồng", "https://example.test/current", now.AddHours(-2), "", "Observed description"),
                new("Old", "https://example.test/old", now.AddDays(-9), "", "Old description")]);
            var selection = new NewsSelection("https://example.test/current", "Kinh tế", "Tóm tắt thử.");
            var report = NewsDigest.Compose([feed], [selection], 72, now);
            using var parsed = JsonDocument.Parse(report.Json);
            Check(parsed.RootElement.GetProperty("articles")[0].GetProperty("title").GetString() == feed.Items[0].Title, "Model could replace source title");
            Check(report.Markdown.Contains(feed.Items[0].Title), "Markdown title changed accents");
            foreach (var invalid in new[] { new[] { selection with { Url = "https://example.test/unknown" } }, new[] { selection, selection }, new[] { selection with { Url = "https://example.test/old" } } })
            {
                var rejected = false; try { NewsDigest.Compose([feed], invalid, 72, now); } catch (IOException) { rejected = true; }
                Check(rejected, "Unknown/stale/duplicate article allowed");
            }
        });
        test("Large tool evidence is readable in bounded chunks and rejects foreign handles", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "h2-evidence-read-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                var state = Path.Combine(root, "state");
                var store = new H2AgentLab.Session.ArtifactStore(state);
                var full = string.Concat(Enumerable.Repeat("Tiếng Việt ", 1500)) + "TAIL-EVIDENCE";
                var artifact = store.StoreText(H2AgentLab.Session.AgentArtifactKind.ToolOutput, "fixture", "web.read_feed", full, "feed", 1);
                using var host = new AgentTools(new SafeWorkspace(root), state, (_, _) => Task.FromResult(true), (_, _) => { });
                var registry = H2AgentLab.Tools.NormalRuntimeToolRegistry.Create(host);
                Check(registry.TryGet("read_tool_output", out var tool), "Missing explicit artifact reader");
                var accumulated = new StringBuilder(); var offset = 0;
                while (offset < full.Length)
                {
                    var raw = tool.Executor.ExecuteAsync(new ToolCall("read", tool.Name, JsonSerializer.SerializeToElement(new { artifact_id = artifact.Handle.Id, offset = offset.ToString() })), CancellationToken.None).AsTask().Result;
                    Check(raw.Length < AgentRuntimeEvidenceProjector.MaxInlineToolOutputCharacters, "Chunk would recursively become another truncated artifact");
                    using var json = JsonDocument.Parse(raw); accumulated.Append(json.RootElement.GetProperty("content").GetString());
                    var next = json.RootElement.GetProperty("nextOffset").GetInt32(); Check(next > offset, "Chunk did not advance"); offset = next;
                }
                Check(accumulated.ToString() == full, "Evidence content lost in chunking");
                var denied = tool.Executor.ExecuteAsync(new ToolCall("foreign", tool.Name, JsonSerializer.SerializeToElement(new { artifact_id = "h2a1_" + new string('a', 32), offset = "0" })), CancellationToken.None).AsTask().Result;
                Check(denied.Contains("false"), "Unrecorded evidence handle accepted");
            }
            finally { Directory.Delete(root, true); }
        });
        test("RSS filters stale/future dates and duplicate URLs without inventing articles", () =>
        {
            const string rss = "<rss><channel><item><title>Today</title><link>https://example.com/a</link><pubDate>Mon, 21 Sep 2026 15:00:00 +0700</pubDate></item><item><title>Duplicate</title><link>https://example.com/a</link><pubDate>Mon, 21 Sep 2026 15:00:00 +0700</pubDate></item><item><title>Old</title><link>https://example.com/b</link><pubDate>Mon, 21 Sep 2020 15:00:00 +0700</pubDate></item><item><title>Undated</title><link>https://example.com/c</link></item></channel></rss>";
            var result = WebFeedReader.Read(new("https://example.com/feed", "application/xml", Encoding.UTF8.GetBytes(rss)),
                new DateTimeOffset(2026, 9, 21, 18, 0, 0, TimeSpan.Zero), maxAgeHours: 72);
            Check(result.Items.Count == 1 && result.Items[0].Title == "Today" && result.ExcludedItems == 3, "RSS filtering failed");
            Check(result.Items[0].Published!.Value.Hour == 8, "Timezone was ignored");
        });
        test("RSS rejects DTD and non-feed HTML rather than treating it as news", () =>
        {
            foreach (var input in new[] { "<!DOCTYPE rss [<!ENTITY x SYSTEM 'file:///C:/private.txt'>]><rss><channel>&x;</channel></rss>", "<html><body>Sign in</body></html>" })
            {
                var rejected = false;
                try { WebFeedReader.Read(new("https://example.com", "text/xml", Encoding.UTF8.GetBytes(input)), DateTimeOffset.UtcNow); }
                catch (Exception ex) when (ex is System.Xml.XmlException or InvalidDataException) { rejected = true; }
                Check(rejected, "Unsafe/non-feed input was accepted");
            }
        });
        test("Assistant PDF preparation rejects unsupported native PDF and preserves source bytes", () =>
        {
            var bytes = Encoding.ASCII.GetBytes("%PDF-1.7\nfixture\n%%EOF");
            var attachment = AiDocuments.Read("fixture.pdf", bytes);
            var rejected = false;
            try { AiPdfProcessor.PrepareAttachmentsAsync([attachment], new AiProfile { Protocol = AiProtocol.Ollama, Model = "gemma4:cloud" }, new(), "unused").GetAwaiter().GetResult(); }
            catch (InvalidOperationException ex) { rejected = ex.Message.Contains("PDF"); }
            Check(rejected && attachment.Data.SequenceEqual(bytes), "PDF was dropped or source was changed");
        });
        test("Assistant OCR cancellation leaves attachments unchanged", () =>
        {
            var original = AiDocuments.Read("fixture.pdf", Encoding.ASCII.GetBytes("%PDF-1.7\nfixture\n%%EOF"));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var stopped = false;
            try { AiPdfProcessor.PrepareAttachmentsAsync([original], new(), new() { Engine = AiPdfEngine.Docling }, "unused", token: cancelled.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && original.IsPdf && original.Text.Length == 0, "Cancellation changed the PDF");
        });
        test("Pre-execution Python errors return failed verification without requiring a nonexistent run", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "h2-capability-verifier-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                var verifier = new PythonRuntimeDomainVerifier(new SafeWorkspace(root), root);
                var outcome = verifier.VerifyAsync(null!, new ToolCall("error", "run_python", JsonSerializer.SerializeToElement(new { })),
                    "{\"success\":false,\"error\":\"file_busy\"}", CancellationToken.None).GetAwaiter().GetResult();
                Check(!outcome.Passed && outcome.EvidenceIds.Count == 0, "Failed call was treated as a verified run");
            }
            finally { Directory.Delete(root, true); }
        });
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}

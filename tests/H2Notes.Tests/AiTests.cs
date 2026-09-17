using System.Net;
using System.Text;
using System.Text.Json;
using H2Notes.Core;

internal static class AiTests
{
    public static void Run(Action<string, Action> test)
    {
        foreach (var protocol in Enum.GetValues<AiProtocol>())
            test("AI streams " + protocol + " with explicit model, context and protocol authentication", () =>
            {
                var payload = protocol switch
                {
                    AiProtocol.Ollama => "{\"message\":{\"content\":\"Xin \"},\"done\":false}\n{\"message\":{\"content\":\"chào\"},\"done\":true}\n",
                    AiProtocol.OpenAiResponses => "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Xin chào\"}\n\ndata: {\"type\":\"response.completed\"}\n\n",
                    AiProtocol.Gemini => "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Xin chào\"}]},\"finishReason\":\"STOP\"}]}\n\n",
                    _ => "data: {\"choices\":[{\"delta\":{\"content\":\"Xin chào\"}}]}\n\ndata: [DONE]\n\n"
                };
                var handler = new Stub(payload); using var client = new AiClient(handler);
                var profile = new AiProfile { Protocol = protocol, BaseUrl = protocol == AiProtocol.Ollama ? "http://localhost:11434" : "https://example.test/v1", Model = "test-model" };
                var text = Collect(client, profile, "private-test-key", [new("user", "User approved context")]).GetAwaiter().GetResult();
                Check(text == "Xin chào" && handler.Calls == 1);
                Check(handler.Body!.Contains("User approved context") && !handler.Body.Contains("private-test-key"));
                Check(protocol == AiProtocol.Ollama ? handler.Auth is null && handler.GoogleKey is null : protocol == AiProtocol.Gemini ? handler.GoogleKey == "private-test-key" : handler.Auth == "Bearer private-test-key");
                Check(handler.Url!.Contains(protocol switch { AiProtocol.Ollama => "/api/chat", AiProtocol.OpenAiResponses => "/responses", AiProtocol.Gemini => ":streamGenerateContent?alt=sse", _ => "/chat/completions" }));
            });
        test("AI refuses unsafe online URLs, embedded credentials and key query strings", () =>
        {
            foreach (var url in new[] { "http://example.test", "https://user:password@example.test", "https://example.test?key=secret", "file:///tmp" })
                Throws<InvalidOperationException>(() => AiClient.Endpoint(new() { Protocol = AiProtocol.OpenAiChat, BaseUrl = url }, "models"));
            Check(AiClient.Endpoint(new() { BaseUrl = "http://127.0.0.1:11434" }, "api/tags").AbsolutePath == "/api/tags");
        });
        test("AI truncated stream is not reported complete or automatically retried", () =>
        {
            var handler = new Stub("data: {\"choices\":[{\"delta\":{\"content\":\"Partial\"}}]}\n\n"); using var client = new AiClient(handler);
            Throws<IOException>(() => Collect(client, new() { Protocol = AiProtocol.OpenAiChat, BaseUrl = "https://example.test", Model = "m" }, "", [new("user", "hello")]).GetAwaiter().GetResult());
            Check(handler.Calls == 1);
        });
        test("AI server errors do not reveal response body or retry", () =>
        {
            var handler = new Stub("server echoed secret", HttpStatusCode.Unauthorized); using var client = new AiClient(handler);
            try { Collect(client, new() { Model = "m" }, "secret", [new("user", "hello")]).GetAwaiter().GetResult(); throw new Exception("Expected server error"); }
            catch (HttpRequestException ex) { Check(!ex.Message.Contains("secret") && handler.Calls == 1); }
        });
        test("AI Ollama models and memory requests contain no project data", () =>
        {
            var handler = new Stub("{\"models\":[{\"name\":\"local-a:latest\"},{\"name\":\"local-b\"}]}"); using var client = new AiClient(handler);
            var result = client.ListModels(new(), "unused").GetAwaiter().GetResult(); Check(result.Count == 2 && handler.Url!.EndsWith("/api/tags") && handler.Body is null);
            client.SetOllamaLoaded(new() { Model = "local-a:latest" }, false).GetAwaiter().GetResult();
            using var json = JsonDocument.Parse(handler.Body!); Check(json.RootElement.GetProperty("keep_alive").GetString() == "0" && !handler.Body!.Contains("messages"));
        });
        test("AI canceled request does not start a fallback provider", () =>
        {
            var handler = new Stub(""); using var client = new AiClient(handler); using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Throws<OperationCanceledException>(() => Collect(client, new() { Model = "m" }, "", [new("user", "hello")], cancel.Token).GetAwaiter().GetResult());
            Check(handler.Calls <= 1);
        });
        foreach (var (status, expected) in new[] { (401, "authentication"), (402, "payment"), (403, "permission"), (404, "model_unavailable"), (429, "quota"), (503, "server") })
            test("AI HTTP " + status + " has an actionable safe category without echoing secrets", () =>
            {
                var handler = new Stub("{\"error\":{\"message\":\"private-key sensitive-prompt https://example.test?key=secret\"}}", (HttpStatusCode)status);
                using var client = new AiClient(handler);
                try { Collect(client, new() { Model = "m" }, "private-key", [new("user", "sensitive-prompt")]).GetAwaiter().GetResult(); throw new Exception("Expected error"); }
                catch (AiServiceException ex) { Check(ex.Code == expected && ex.StatusCode == (HttpStatusCode)status && !ex.Message.Contains("private-key") && !ex.Message.Contains("sensitive-prompt") && !ex.Message.Contains("key=secret")); }
                Check(handler.Calls == 1);
            });
        test("AI distinguishes Gemini model restriction and cloud extra usage without raw server messages", () =>
        {
            var restricted = AiFailure.FromStatus(HttpStatusCode.NotFound, "This model is no longer available to new users. secret");
            var paid = AiFailure.FromStatus(HttpStatusCode.PaymentRequired, "this model uses extra usage only and balance is empty. secret");
            Check(restricted.Message.Contains("người dùng mới") && paid.Message.Contains("Extra Usage") && !restricted.Message.Contains("secret") && !paid.Message.Contains("secret"));
        });
        test("AI model list HTTP failure keeps the same actionable error as chat", () =>
        {
            using var client = new AiClient(new Stub("{\"error\":\"private-key\"}", HttpStatusCode.Unauthorized));
            try { client.ListModels(new(), "").GetAwaiter().GetResult(); throw new Exception("Expected error"); }
            catch (AiServiceException ex) { Check(ex.Code == "authentication" && !ex.Message.Contains("private-key")); }
        });
        test("AI completed stream without text is an error, not a blank successful answer", () =>
        {
            using var client = new AiClient(new Stub("{\"message\":{\"content\":\"\"},\"done\":true}\n"));
            try { Collect(client, new() { Model = "m" }, "", [new("user", "hello")]).GetAwaiter().GetResult(); throw new Exception("Expected error"); }
            catch (AiServiceException ex) { Check(ex.Code == "empty_reply"); }
        });
        test("Ollama location labels distinguish local, cloud tags and remote servers", () =>
        {
            Check(new AiProfile { Model = "minimax-m3:cloud" }.IsOllamaCloud);
            Check(new AiProfile { Model = "gpt-oss:120b-cloud" }.IsOllamaCloud);
            Check(new AiProfile { Model = "gemma3:4b" }.ProcessingLocation.Contains("trên máy"));
            Check(new AiProfile { Model = "m", BaseUrl = "https://ollama.com" }.IsOllamaCloud);
            Check(new AiProfile { Model = "m", BaseUrl = "https://example.test" }.ProcessingLocation.Contains("từ xa"));
            Check(new AiProfile { Model = "m", Protocol = AiProtocol.Gemini }.ProcessingLocation == "API online");
        });
        test("Ollama cloud is never described or requested as a local RAM load", () =>
        {
            var handler = new Stub("{}"); using var client = new AiClient(handler);
            Throws<InvalidOperationException>(() => client.SetOllamaLoaded(new() { Model = "minimax-m3:cloud" }, true).GetAwaiter().GetResult());
            Check(handler.Calls == 0);
        });
        test("Shared AI memory survives devices and retrieves project/time evidence without model context growth", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "h2-ai-memory-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var state = SheetStorage.Demo();
                var project = state.Notes[0].Projects[0];
                project.Name = "Krong Ia Taun"; project.NameRich = RichDocument.Plain("Krong Ia Taun");
                project.Notes = "Diện tích dự án Krong được điều chỉnh từ 8,5 ha lên 11,49 ha.";
                project.NotesRich = RichDocument.Plain(project.Notes);
                project.UpdatedAtUtc = new DateTime(2026, 9, 17, 1, 0, 0, DateTimeKind.Utc);
                project.Conversations.Add(new AiConversation { Messages = [new AiMessage
                {
                    Role = "user", Content = "Sáng nay bắt đầu làm hồ sơ hoàn công Krong.",
                    CreatedAt = new DateTime(2026, 9, 17, 2, 15, 0, DateTimeKind.Utc), DeviceId = "PC-A"
                }] });
                new AiMemoryStore(root, "PC-A").SyncFromState(state);
                Check(File.Exists(Path.Combine(root, "memory", "revision.json")));
                Check(Directory.EnumerateFiles(Path.Combine(root, "memory"), "*.h2memory.json", SearchOption.AllDirectories).Any());

                var pcB = new AiMemoryStore(root, "PC-B");
                var facts = pcB.Query("diện tích Krong", project.Id, false, 20, new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero));
                Check(facts.Records.Any(r => r.Text.Contains("11,49 ha", StringComparison.Ordinal)));
                var morning = pcB.Query("sáng nay Krong làm gì", project.Id, false, 20, new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero));
                Check(morning.Records.Any(r => r.Text.Contains("hồ sơ hoàn công Krong", StringComparison.Ordinal)));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });
        test("Interactive AI context keeps recent turns and never resends historical image bytes", () =>
        {
            var conversation = new AiConversation();
            for (var i = 0; i < 30; i++)
            {
                var message = new AiMessage { Role = i % 2 == 0 ? "user" : "assistant", Content = "turn-" + i, CreatedAt = DateTime.UtcNow.AddMinutes(i - 30) };
                if (i == 25) message.Attachments.Add(new AiAttachment
                {
                    Name = "old.png", MimeType = "image/png", Data = [137, 80, 78, 71], Text = "OCR-CACHED-TABLE", PdfEngine = AiPdfEngine.GotOcr,
                    Sha256 = "OLD", SourceSha256 = "OLD"
                });
                conversation.Messages.Add(message);
            }
            var pending = new AiMessage { Role = "user", Content = "new request" };
            var turns = AiProjectContext.Prepare(conversation, pending, "{\"current\":true}");
            Check(turns.Count == AiProjectContext.RecentConversationMessages + 2);
            Check(turns.Skip(1).Take(AiProjectContext.RecentConversationMessages).All(t => (t.Images?.Count ?? 0) == 0 && (t.Files?.Count ?? 0) == 0));
            Check(turns.Any(t => t.Content.Contains("OCR-CACHED-TABLE", StringComparison.Ordinal)));
            Check(turns.All(t => !t.Content.Contains("turn-0", StringComparison.Ordinal)));
            Check(turns[^1].Content.Contains("new request", StringComparison.Ordinal));
        });
    }
    private static async Task<string> Collect(AiClient client, AiProfile profile, string key, AiTurn[] turns, CancellationToken token = default)
    { var text = new StringBuilder(); await foreach (var part in client.Stream(profile, key, turns, token)) text.Append(part); return text.ToString(); }
    private static void Check(bool condition) { if (!condition) throw new Exception("AI assertion failed"); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class Stub(string payload, HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls; public string? Body, Url, Auth, GoogleKey;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++; Url = request.RequestUri!.AbsoluteUri; Auth = request.Headers.Authorization?.ToString();
            GoogleKey = request.Headers.TryGetValues("x-goog-api-key", out var keys) ? keys.Single() : null;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(code) { Content = new StringContent(payload) };
        }
    }
}

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using H2Notes.Avalonia;
using H2Notes.Core;

// Explicit opt-in diagnostics only, never part of the automated test suite.
// Uses saved connections but does not load a workspace or persist a conversation.
internal static class LiveAiProbe
{
    public static async Task<int> Run(string[] args)
    {
        var settings = LocalConfiguration.Read();
        var ids = args.Where(a => Guid.TryParse(a, out _)).Select(Guid.Parse).ToHashSet();
        if (ids.Count == 0) { Console.WriteLine("Specify saved profile IDs and --allow-live-ai."); return 2; }
        var failures = 0;
        foreach (var profile in settings.Ai.Profiles.Where(p => ids.Contains(p.Id)))
        {
            if (ids.Count == 1 && args.FirstOrDefault(a => a.StartsWith("--model=")) is { } modelArgument) profile.Model = modelArgument[8..];
            var watch = Stopwatch.StartNew();
            var handler = new AuditHandler(); using var client = new AiClient(handler);
            var result = new Dictionary<string, object?> { ["profile"] = profile.Name, ["id"] = profile.Id, ["model"] = profile.Model,
                ["protocol"] = profile.Protocol.ToString(), ["host"] = AiClient.Endpoint(profile, "models").Host };
            string key;
            try
            {
                key = (string)typeof(LocalConfiguration).Assembly.GetType("H2Notes.Avalonia.SecretVault")!
                    .GetMethod("Read", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [profile.Id])!;
                result["hasSavedKey"] = key.Length > 0;
            }
            catch { result["error"] = "Saved credential could not be decrypted"; Console.WriteLine(JsonSerializer.Serialize(result)); failures++; continue; }
            try
            {
                var models = await client.ListModels(profile, key);
                result["modelsCount"] = models.Count; result["selectedModelListed"] = models.Contains(profile.Model);
                if (profile.Protocol == AiProtocol.Gemini) result["flashLiteModels"] = models.Where(m => m.Contains("flash-lite", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (profile.Protocol == AiProtocol.Ollama)
                {
                    result["installedModels"] = models;
                    result["loadedModels"] = await client.LoadedOllamaModels(profile);
                }
            }
            catch (Exception ex) { result["modelListError"] = ex.GetType().Name; }
            if (args.Contains("--list-only")) { result["requests"] = handler.Events; Console.WriteLine(JsonSerializer.Serialize(result)); continue; }
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(75));
                var reply = new StringBuilder();
                await foreach (var part in client.Stream(profile, key, [new("user", "Reply with exactly OK. No explanation.")], limit.Token)) reply.Append(part);
                result["chatCompleted"] = true; result["replyChars"] = reply.Length;
                result["isOkReply"] = reply.ToString().Trim().Equals("OK", StringComparison.OrdinalIgnoreCase);
                if (reply.Length == 0) failures++;
            }
            catch (Exception ex) { result["chatCompleted"] = false; result["chatError"] = ex.GetType().Name;
                if (ex is AiServiceException service) { result["failureCode"] = service.Code; result["safeError"] = service.Message; } failures++; }
            result["requests"] = handler.Events; result["elapsedMs"] = watch.ElapsedMilliseconds;
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
        return failures == 0 ? 0 : 1;
    }
    private sealed class AuditHandler() : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
    {
        public List<object> Events { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            var response = await base.SendAsync(request, token);
            string? providerStatus = null; var reasons = new List<string>();
            if (!response.IsSuccessStatusCode && response.Content.Headers.ContentLength is < 65536)
            {
                try
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                    if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                    {
                        if (error.TryGetProperty("status", out var status) && SafeCode(status.GetString()) is { } value) providerStatus = value;
                        if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                            foreach (var detail in details.EnumerateArray())
                                if (detail.TryGetProperty("reason", out var reason) && SafeCode(reason.GetString()) is { } code) reasons.Add(code);
                    }
                }
                catch (JsonException) { }
            }
            Events.Add(new { method = request.Method.Method, path = request.RequestUri!.AbsolutePath, status = (int)response.StatusCode,
                contentType = response.Content.Headers.ContentType?.MediaType, providerStatus, reasons, elapsedMs = watch.ElapsedMilliseconds });
            return response;
        }
        private static string? SafeCode(string? value) => value is not null && Regex.IsMatch(value, "^[A-Z_]{1,80}$") ? value : null;
    }
}

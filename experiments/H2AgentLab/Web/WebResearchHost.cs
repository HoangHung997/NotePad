using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using H2AgentLab.Providers;
using H2AgentLab.Tools;

namespace H2AgentLab.Web;

public sealed record WebSearchHit(
    string Url,
    string Title,
    string Publisher,
    string Snippet,
    DateTime? PublishedAt = null);

public sealed record WebFetchedDocument(
    string Url,
    string ContentType,
    byte[] Bytes,
    string? Title = null,
    string? Publisher = null,
    DateTime? PublishedAt = null,
    DateTime? EffectiveAt = null);

public interface IWebResearchBackend
{
    Task<IReadOnlyList<WebSearchHit>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken);

    Task<WebFetchedDocument> FetchAsync(
        string url,
        CancellationToken cancellationToken);

    Task<string> OpenBrowserFallbackAsync(
        string url,
        CancellationToken cancellationToken);
}

public sealed class HttpWebResearchBackend : IWebResearchBackend
{
    private readonly HttpClient _http;
    private readonly Func<string, int, CancellationToken, Task<IReadOnlyList<WebSearchHit>>>? _search;

    public HttpWebResearchBackend(
        HttpClient http,
        Func<string, int, CancellationToken, Task<IReadOnlyList<WebSearchHit>>>? search = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _search = search;
    }

    public Task<IReadOnlyList<WebSearchHit>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken)
        => _search is not null
            ? _search(query, maxResults, cancellationToken)
            : throw new InvalidOperationException(
                "No structured web search backend is configured. Browser fallback must not be silently substituted for search.");

    public async Task<WebFetchedDocument> FetchAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var uri = ValidateHttpUrl(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("H2AgentLab", "2.0"));
        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        const int maxBytes = 32 * 1024 * 1024;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[32 * 1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + count > maxBytes)
                throw new IOException("Web download exceeds 32 MB.");
            memory.Write(buffer, 0, count);
        }

        return new WebFetchedDocument(
            response.RequestMessage?.RequestUri?.ToString() ?? uri.ToString(),
            response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream",
            memory.ToArray(),
            PublishedAt: response.Content.Headers.LastModified?.UtcDateTime);
    }

    public Task<string> OpenBrowserFallbackAsync(
        string url,
        CancellationToken cancellationToken)
    {
        _ = ValidateHttpUrl(url);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(url);
    }

    private static Uri ValidateHttpUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("Web URL must be absolute HTTP/HTTPS.", nameof(value));
        return uri;
    }
}

public sealed class WebResearchHost : ICapabilityProvider
{
    private readonly IWebResearchBackend _backend;
    private ProviderHealthState _health = new(ProviderHealthStatus.Disconnected, DateTime.UtcNow);

    public WebResearchHost(
        IWebResearchBackend backend,
        string providerId = "web-research",
        string providerVersion = "1.0.0")
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        Provenance = new ProviderProvenance(
            providerId,
            providerVersion,
            "web-research-host",
            "web");
    }

    public ProviderProvenance Provenance { get; }
    public ProviderHealthState Health => _health;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _health = new(ProviderHealthStatus.Ready, DateTime.UtcNow);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _health = new(ProviderHealthStatus.Disconnected, DateTime.UtcNow);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProviderNamespaceSummary>> ListNamespacesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ProviderNamespaceSummary>>(
        [
            new("web", "Structured current-web research and bounded browser fallback.",
                ["search", "fetch", "download", "extract", "metadata", "browser"])
        ]);
    }

    public Task<IReadOnlyList<ProviderToolSummary>> ListToolSummariesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ProviderToolSummary>>(
            ToolDefinitions().Select(x => x.Summary).ToArray());
    }

    public Task<IReadOnlyList<ProviderToolDefinition>> LoadToolDefinitionsAsync(
        IReadOnlyList<string> toolNames,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var map = ToolDefinitions().ToDictionary(x => x.Summary.Name, StringComparer.Ordinal);
        var selected = new List<ProviderToolDefinition>();
        foreach (var name in toolNames.Distinct(StringComparer.Ordinal))
        {
            if (!map.TryGetValue(name, out var definition))
                throw new KeyNotFoundException("Unknown WebResearchHost tool: " + name);
            selected.Add(definition);
        }
        return Task.FromResult<IReadOnlyList<ProviderToolDefinition>>(selected);
    }

    public Task<IReadOnlyList<ProviderResourceSummary>> ListResourcesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ProviderResourceSummary>>([]);
    }

    public Task<string> ReadResourceAsync(
        string resourceId,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("WebResearchHost exposes fetched bodies through WebEvidenceStore, not provider resources.");

    public async ValueTask<string> ExecuteToolAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        EnsureReady();
        switch (toolName)
        {
            case "web.search":
            {
                var query = RequiredString(arguments, "query", 2_000);
                var max = OptionalInt(arguments, "max_results", 8, 1, 20);
                var hits = await _backend.SearchAsync(query, max, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Serialize(hits.Take(max));
            }

            case "web.fetch":
            case "web.download":
            {
                var url = RequiredString(arguments, "url", 4_000);
                var document = await _backend.FetchAsync(url, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Serialize(new
                {
                    document.Url,
                    document.ContentType,
                    bytes = document.Bytes.Length,
                    sha256 = global::H2AgentLab.SafeWorkspace.Hash(document.Bytes).ToLowerInvariant(),
                    document.Title,
                    document.Publisher,
                    document.PublishedAt,
                    document.EffectiveAt,
                    bodyBase64 = toolName == "web.download"
                        ? Convert.ToBase64String(document.Bytes)
                        : null,
                    text = toolName == "web.fetch"
                        ? ExtractText(document.ContentType, document.Bytes, 64_000)
                        : null
                });
            }

            case "web.extract":
            {
                var contentType = RequiredString(arguments, "content_type", 256);
                var data = Convert.FromBase64String(RequiredString(arguments, "body_base64", 8_000_000));
                return JsonSerializer.Serialize(new
                {
                    text = ExtractText(contentType, data, 64_000)
                });
            }

            case "web.get_metadata":
            {
                var url = RequiredString(arguments, "url", 4_000);
                var document = await _backend.FetchAsync(url, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Serialize(new
                {
                    document.Url,
                    document.ContentType,
                    bytes = document.Bytes.Length,
                    sha256 = global::H2AgentLab.SafeWorkspace.Hash(document.Bytes).ToLowerInvariant(),
                    document.Title,
                    document.Publisher,
                    document.PublishedAt,
                    document.EffectiveAt
                });
            }

            case "web.open_browser":
            {
                var url = RequiredString(arguments, "url", 4_000);
                return JsonSerializer.Serialize(new
                {
                    opened = await _backend.OpenBrowserFallbackAsync(url, cancellationToken).ConfigureAwait(false),
                    fallback = true
                });
            }

            default:
                throw new KeyNotFoundException("Unknown WebResearchHost tool: " + toolName);
        }
    }

    private void EnsureReady()
    {
        if (_health.Status != ProviderHealthStatus.Ready)
            throw new InvalidOperationException("WebResearchHost is not connected.");
    }

    private static IReadOnlyList<ProviderToolDefinition> ToolDefinitions()
    {
        static ProviderToolDefinition Def(
            string name,
            string description,
            object properties,
            string[] required,
            AgentToolAccess access = AgentToolAccess.ReadOnly,
            AgentToolRisk risk = AgentToolRisk.Low)
        {
            var summary = new ProviderToolSummary(
                name,
                "web",
                description,
                access,
                risk,
                SupportsParallel: access == AgentToolAccess.ReadOnly,
                SchemaVersion: "v1",
                ResourceScope: "web:public",
                SerializationKey: "web",
                ToolVersion: "1.0.0");
            var schema = JsonSerializer.SerializeToElement(new
            {
                type = "function",
                function = new
                {
                    name,
                    description,
                    parameters = new
                    {
                        type = "object",
                        properties,
                        required,
                        additionalProperties = false
                    }
                }
            });
            return new(summary, schema);
        }

        return
        [
            Def("web.search", "Search current approved web sources.",
                new { query = new { type = "string" }, max_results = new { type = "integer", minimum = 1, maximum = 20 } }, ["query"]),
            Def("web.fetch", "Fetch and extract bounded content from an HTTP/HTTPS URL.",
                new { url = new { type = "string" } }, ["url"]),
            Def("web.download", "Download bounded bytes from an HTTP/HTTPS URL.",
                new { url = new { type = "string" } }, ["url"]),
            Def("web.extract", "Extract bounded text from previously downloaded bytes.",
                new { content_type = new { type = "string" }, body_base64 = new { type = "string" } }, ["content_type", "body_base64"]),
            Def("web.get_metadata", "Fetch current URL metadata/hash without putting the full body in context.",
                new { url = new { type = "string" } }, ["url"]),
            Def("web.open_browser", "Fallback browser navigation only when structured web access is insufficient.",
                new { url = new { type = "string" } }, ["url"], AgentToolAccess.Mutating, AgentToolRisk.Medium)
        ];
    }

    public static string ExtractText(
        string contentType,
        byte[] bytes,
        int maxCharacters)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (maxCharacters is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(maxCharacters));

        string text;
        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            var html = Encoding.UTF8.GetString(bytes);
            text = Regex.Replace(html, "<script[\s\S]*?</script>", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<style[\s\S]*?</style>", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<[^>]+>", " ");
            text = WebUtility.HtmlDecode(text);
        }
        else if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            text = Encoding.UTF8.GetString(bytes);
        }
        else
        {
            return "";
        }

        text = Regex.Replace(text, "\s+", " ").Trim();
        return text.Length <= maxCharacters
            ? text
            : text[..maxCharacters] + "…[truncated]";
    }

    private static string RequiredString(
        JsonElement arguments,
        string name,
        int max)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(name, out var node)
            || node.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(node.GetString()))
            throw new ArgumentException($"WebResearchHost requires string '{name}'.");
        var value = node.GetString()!.Trim();
        return value.Length <= max
            ? value
            : throw new ArgumentException($"WebResearchHost '{name}' exceeds {max} characters.");
    }

    private static int OptionalInt(
        JsonElement arguments,
        string name,
        int fallback,
        int min,
        int max)
    {
        if (!arguments.TryGetProperty(name, out var node))
            return fallback;
        if (!node.TryGetInt32(out var value) || value < min || value > max)
            throw new ArgumentException($"WebResearchHost '{name}' must be {min}..{max}.");
        return value;
    }

    public ValueTask DisposeAsync()
    {
        _health = new(ProviderHealthStatus.Disconnected, DateTime.UtcNow);
        return ValueTask.CompletedTask;
    }
}

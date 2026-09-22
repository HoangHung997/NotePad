using System.Text;
using System.Text.Json;
using H2AgentLab.Runtime;
using H2AgentLab.Tools;
using H2AgentLab.Web;

namespace H2AgentLab.Integration;

internal sealed class H2NewsDigestTools(SafeWorkspace workspace, string stateRoot) : IAgentRuntimeDomainVerifier
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WebFeedPage> _feeds = new();
    private readonly Dictionary<string, string[]> _verified = new();
    public string DomainId => "news-source-readback";
    public void Observe(WebFeedPage page) { lock (_gate) _feeds[page.Source] = page; }
    public void Register(ToolRegistry registry)
    {
        const string name = "save_news_digest";
        const string description = "Save selected news from web.read_feed as NEW JSON and Markdown files in the workspace. Host preserves exact source titles/URLs/dates, filters age and rejects unknown/duplicate articles. Supply only observed URLs, categories and Vietnamese summaries; do not manually retype source titles. Read feeds before calling.";
        registry.Register(new ToolDescriptor(name, registry.Namespaces.Single(n => n.Name == "files"), description, AgentToolRisk.Medium,
            AgentToolAccess.Mutating, false, "v1", JsonSerializer.SerializeToElement(new { type = "function", function = new {
                name, description, parameters = new { type = "object", properties = new {
                    path = new { type = "string", description = "New JSON path, such as news.json" }, markdown_path = new { type = "string" },
                    max_age_hours = new { type = "integer", minimum = 1, maximum = 8760 },
                    articles = new { type = "array", maxItems = 50, items = new { type = "object", properties = new {
                        url = new { type = "string" }, category = new { type = "string" }, summary_vi = new { type = "string" } },
                        required = new[] { "url", "category", "summary_vi" }, additionalProperties = false } } },
                    required = new[] { "path", "markdown_path", "max_age_hours", "articles" }, additionalProperties = false } } }),
            new DelegatingToolExecutor("news-digest", Execute), resourceScope: new("workspace", workspace.Root),
            serializationKey: "news-digest", canProvideVerificationEvidence: true));
    }
    private ValueTask<string> Execute(ToolCall call, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = workspace.Resolve(call.Arguments.GetProperty("path").GetString()!);
        var markdownPath = workspace.Resolve(call.Arguments.GetProperty("markdown_path").GetString()!);
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !markdownPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || File.Exists(path) || File.Exists(markdownPath)) throw new IOException("Choose new .json and .md paths; existing files are preserved.");
        WebFeedPage[] feeds; lock (_gate) feeds = _feeds.Values.ToArray();
        var selected = call.Arguments.GetProperty("articles").EnumerateArray().Select(a => new NewsSelection(
            a.GetProperty("url").GetString()!, a.GetProperty("category").GetString()!, a.GetProperty("summary_vi").GetString()!)).ToArray();
        var content = NewsDigest.Compose(feeds, selected, call.Arguments.GetProperty("max_age_hours").GetInt32(), DateTimeOffset.UtcNow);
        workspace.Write(path, Encoding.UTF8.GetBytes(content.Json), "", stateRoot);
        workspace.Write(markdownPath, Encoding.UTF8.GetBytes(content.Markdown), "", stateRoot);
        if (Encoding.UTF8.GetString(workspace.Read(path)) != content.Json || Encoding.UTF8.GetString(workspace.Read(markdownPath)) != content.Markdown)
            throw new IOException("Saved news digest did not match source-grounded content.");
        var hashes = new[] { SafeWorkspace.Hash(workspace.Read(path)), SafeWorkspace.Hash(workspace.Read(markdownPath)) };
        lock (_gate) _verified[call.Id] = hashes;
        return ValueTask.FromResult(JsonSerializer.Serialize(new { path, markdown_path = markdownPath, articles = selected.Length, hashes,
            verification = "Titles, URLs and dates match observed source feeds; both saved files read back. AI summary meaning still requires source comparison." }));
    }
    public bool CanVerify(ToolCall call, string output) => call.Name == "save_news_digest";
    public Task<AgentRuntimeDomainVerification> VerifyAsync(AgentRuntimeVerificationContext context, ToolCall call, string output, CancellationToken ct)
    { lock (_gate) return Task.FromResult(_verified.TryGetValue(call.Id, out var hashes)
        ? new AgentRuntimeDomainVerification(DomainId, true, hashes.Select(h => "news-file-sha256:" + h).ToArray())
        : new AgentRuntimeDomainVerification(DomainId, false, [], "No saved source-grounded digest.")); }
}

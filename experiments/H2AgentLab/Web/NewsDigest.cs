using System.Text;
using System.Text.Json;

namespace H2AgentLab.Web;

public sealed record NewsSelection(string Url, string Category, string SummaryVi);
public static class NewsDigest
{
    public static (string Json, string Markdown) Compose(IReadOnlyList<WebFeedPage> observed,
        IReadOnlyList<NewsSelection> selected, int maxAgeHours, DateTimeOffset now)
    {
        if (observed.Count == 0 || selected.Count > 50 || maxAgeHours is < 1 or > 8760)
            throw new ArgumentException("Read feeds first and select at most 50 items with a valid age filter.");
        var known = observed.SelectMany(f => f.Items).GroupBy(i => i.Url).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var urls = new HashSet<string>(StringComparer.Ordinal); var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var articles = new List<object>(); var markdown = new StringBuilder("# Tin tức đã đối chiếu nguồn\n\n");
        foreach (var choice in selected)
        {
            if (!known.TryGetValue(choice.Url, out var item)) throw new IOException("Article URL was not observed in this task. Read the source feed; do not invent a URL.");
            if (item.Published is null || item.Published < now.AddHours(-maxAgeHours) || item.Published > now.AddMinutes(5))
                throw new IOException("Article is outside the requested publication date window.");
            if (!urls.Add(item.Url) || !titles.Add(item.Title.Normalize())) throw new IOException("Duplicate article URL/title.");
            if (choice.Category.Length > 150 || string.IsNullOrWhiteSpace(choice.SummaryVi) || choice.SummaryVi.Length > 3000)
                throw new ArgumentException("Category/summary exceeds allowed size or is empty.");
            articles.Add(new { title = item.Title, url = item.Url, published = item.Published, category = choice.Category,
                summary_vi = choice.SummaryVi, source_description = item.Description });
            var title = item.Title.Replace("[", "\\[").Replace("]", "\\]");
            markdown.Append("- [").Append(title).Append("](<").Append(item.Url.Replace(">", "%3E")).Append(">) — ")
                .Append(item.Published.Value.ToString("u")).Append('\n').Append("  ").Append(choice.SummaryVi.Replace("\n", " ")).Append('\n');
        }
        var report = new { fetched_at_utc = observed.Max(f => f.FetchedAtUtc), sources = observed.Select(f => f.Source).Distinct().ToArray(),
            articles, summary_note = "Tóm tắt do AI soạn; tiêu đề, URL và ngày lấy nguyên từ nguồn đã đọc." };
        return (JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), markdown.ToString());
    }
}

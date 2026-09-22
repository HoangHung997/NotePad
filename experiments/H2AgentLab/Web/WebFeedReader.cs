using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace H2AgentLab.Web;

public sealed record WebFeedItem(string Title, string Url, DateTimeOffset? Published, string Category, string Description);
public sealed record WebFeedPage(string Source, DateTimeOffset FetchedAtUtc, int TotalItems, int ExcludedItems, IReadOnlyList<WebFeedItem> Items);

public static class WebFeedReader
{
    public static WebFeedPage Read(WebFetchedDocument source, DateTimeOffset now, int maxItems = 20, int maxAgeHours = 0)
    {
        if (maxItems is < 1 or > 50 || maxAgeHours is < 0 or > 8760) throw new ArgumentOutOfRangeException(nameof(maxItems));
        using var stream = new MemoryStream(source.Bytes);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
        var document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName is not ("rss" or "feed" or "RDF")) throw new InvalidDataException("URL did not return an RSS/Atom feed.");
        var items = document.Descendants().Where(e => e.Name.LocalName is "item" or "entry").Take(500).ToArray();
        var result = new List<WebFeedItem>();
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            string Field(params string[] names) => item.Elements().FirstOrDefault(e => names.Contains(e.Name.LocalName))?.Value ?? "";
            var title = Clean(Field("title"), 500);
            var link = item.Elements().FirstOrDefault(e => e.Name.LocalName == "link" && (e.Attribute("rel")?.Value is null or "alternate"));
            var address = link?.Attribute("href")?.Value ?? link?.Value ?? "";
            if (!Uri.TryCreate(new Uri(source.Url), address, out var uri) || uri.Scheme is not ("https" or "http") || title.Length == 0) continue;
            DateTimeOffset? published = DateTimeOffset.TryParse(Field("pubDate", "published", "updated", "date"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.ToUniversalTime() : null;
            if (maxAgeHours > 0 && (published is null || published < now.AddHours(-maxAgeHours) || published > now.AddMinutes(5))) continue;
            if (!urls.Add(uri.AbsoluteUri) || !titles.Add(title)) continue;
            result.Add(new(title, uri.AbsoluteUri, published, Clean(Field("category"), 150), Clean(Field("description", "summary", "content"), 1200)));
        }
        return new(source.Url, now, items.Length, items.Length - result.Count, result.OrderByDescending(i => i.Published).Take(maxItems).ToArray());
    }

    private static string Clean(string text, int limit)
    {
        text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", " "));
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > limit ? text[..limit] : text;
    }
}

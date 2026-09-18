using System.Text.RegularExpressions;

namespace H2AgentLab.Tools;

public sealed record ToolSearchResult(
    ToolDescriptor Descriptor,
    double Score,
    IReadOnlyList<string> MatchedTerms);

/// <summary>
/// Deterministic lexical/BM25-style search over registered tool metadata. The in-memory corpus is
/// rebuilt only when ToolRegistry.Version changes; no embedding model or provider is involved.
/// </summary>
public sealed partial class ToolSearchIndex
{
    private readonly ToolRegistry _registry;
    private readonly object _gate = new();
    private long _cachedRegistryVersion = -1;
    private SearchDocument[] _documents = [];
    private Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
    private double _averageLength = 1;

    public ToolSearchIndex(ToolRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public long CachedRegistryVersion
    {
        get { lock (_gate) return _cachedRegistryVersion; }
    }

    public long RebuildCount { get; private set; }

    public IReadOnlyList<ToolSearchResult> Search(string query, int maxResults = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (maxResults is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(maxResults));

        var queryTerms = Tokenize(query).Distinct(StringComparer.Ordinal).ToArray();
        if (queryTerms.Length == 0) return Array.Empty<ToolSearchResult>();

        lock (_gate)
        {
            EnsureCurrent();
            if (_documents.Length == 0) return Array.Empty<ToolSearchResult>();

            var results = new List<ToolSearchResult>();
            foreach (var document in _documents)
            {
                var score = 0d;
                var matched = new List<string>();
                foreach (var term in queryTerms)
                {
                    if (!document.TermFrequency.TryGetValue(term, out var frequency))
                        continue;

                    matched.Add(term);
                    var df = _documentFrequency[term];
                    var idf = Math.Log(1d + ((_documents.Length - df + 0.5d) / (df + 0.5d)));
                    const double k1 = 1.2d;
                    const double b = 0.75d;
                    var denominator = frequency
                        + k1 * (1d - b + b * document.Length / Math.Max(1d, _averageLength));
                    score += idf * (frequency * (k1 + 1d)) / denominator;
                }

                if (matched.Count == 0) continue;

                var normalizedQuery = NormalizeIdentifierLike(query);
                if (document.Descriptor.Name == normalizedQuery)
                    score += 200d;
                else if (query.Contains(document.Descriptor.Name, StringComparison.OrdinalIgnoreCase))
                    score += 100d;
                else
                {
                    var nameTerms = Tokenize(document.Descriptor.Name).Distinct(StringComparer.Ordinal).ToArray();
                    if (nameTerms.Length > 0 && nameTerms.All(queryTerms.Contains))
                        score += 25d;
                }
                if (document.Descriptor.Namespace.Name == normalizedQuery)
                    score += 1.5d;

                results.Add(new ToolSearchResult(
                    document.Descriptor,
                    score,
                    matched.OrderBy(x => x, StringComparer.Ordinal).ToArray()));
            }

            return results
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Descriptor.Name, StringComparer.Ordinal)
                .Take(maxResults)
                .ToArray();
        }
    }

    private void EnsureCurrent()
    {
        if (_cachedRegistryVersion == _registry.Version)
            return;

        var docs = _registry.Tools
            .Select(BuildDocument)
            .OrderBy(x => x.Descriptor.Name, StringComparer.Ordinal)
            .ToArray();

        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var doc in docs)
            foreach (var term in doc.TermFrequency.Keys)
                df[term] = df.TryGetValue(term, out var count) ? count + 1 : 1;

        _documents = docs;
        _documentFrequency = df;
        _averageLength = docs.Length == 0 ? 1 : docs.Average(x => (double)x.Length);
        _cachedRegistryVersion = _registry.Version;
        RebuildCount++;
    }

    private static SearchDocument BuildDocument(ToolDescriptor descriptor)
    {
        var terms = new List<string>();

        // Name terms are intentionally repeated to make exact callable intent rank strongly.
        var nameTerms = Tokenize(descriptor.Name).ToArray();
        terms.AddRange(nameTerms);
        terms.AddRange(nameTerms);
        terms.AddRange(nameTerms);

        var namespaceTerms = Tokenize(descriptor.Namespace.Name).ToArray();
        terms.AddRange(namespaceTerms);
        terms.AddRange(namespaceTerms);

        terms.AddRange(Tokenize(descriptor.Description));
        terms.AddRange(Tokenize(descriptor.Namespace.Description));

        var frequencies = terms
            .GroupBy(x => x, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        return new SearchDocument(descriptor, frequencies, Math.Max(1, terms.Count));
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        foreach (Match match in TokenRegex().Matches(text.ToLowerInvariant().Replace('_', ' ').Replace('-', ' ')))
        {
            var value = match.Value;
            if (value.Length > 1)
                yield return value;
        }
    }

    private static string NormalizeIdentifierLike(string value)
        => value.Trim().ToLowerInvariant().Replace(' ', '_');

    [GeneratedRegex(@"[p{L}p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    private sealed record SearchDocument(
        ToolDescriptor Descriptor,
        IReadOnlyDictionary<string, int> TermFrequency,
        int Length);
}

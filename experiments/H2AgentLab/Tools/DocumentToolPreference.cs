namespace H2AgentLab.Tools;

/// <summary>
/// Host-side ranking policy that keeps structured document inspection ahead of arbitrary Python for
/// ordinary Word/Excel/document work. Python remains discoverable as an escape hatch and is not
/// demoted when the user explicitly asks for Python/script/custom unsupported transformation.
/// </summary>
public static class DocumentToolPreference
{
    private static readonly string[] DocumentTerms =
    [
        "document", "docx", "word", "excel", "xlsx", "workbook", "spreadsheet",
        "sheet", "paragraph", "table", "header", "footer", "formula", "cell"
    ];

    private static readonly string[] ExplicitPythonTerms =
    [
        "python", "script", "custom transform", "unsupported transform",
        "arbitrary transform", "code"
    ];

    private static readonly HashSet<string> StructuredToolNames =
        new(StringComparer.Ordinal)
        {
            "read_file",
            "word_paragraphs",
            "check_word"
        };

    public static IReadOnlyList<ToolSearchResult> Apply(
        string query,
        IReadOnlyList<ToolSearchResult> candidates,
        int maxResults)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(candidates);
        if (maxResults is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(maxResults));

        if (!IsDocumentIntent(query) || IsExplicitPythonIntent(query))
            return candidates.Take(maxResults).ToArray();

        return candidates
            .OrderBy(x => Priority(x.Descriptor))
            .ThenByDescending(x => x.Score)
            .ThenBy(x => x.Descriptor.Name, StringComparer.Ordinal)
            .Take(maxResults)
            .ToArray();
    }

    public static bool IsDocumentIntent(string query)
    {
        var normalized = query.ToLowerInvariant();
        return DocumentTerms.Any(term => normalized.Contains(term, StringComparison.Ordinal));
    }

    public static bool IsExplicitPythonIntent(string query)
    {
        var normalized = query.ToLowerInvariant();
        return ExplicitPythonTerms.Any(term => normalized.Contains(term, StringComparison.Ordinal));
    }

    private static int Priority(ToolDescriptor descriptor)
    {
        if (StructuredToolNames.Contains(descriptor.Name)
            || descriptor.Namespace.Name == "office")
            return 0;
        if (descriptor.Namespace.Name == "python"
            || descriptor.Name == "run_python")
            return 2;
        return 1;
    }
}

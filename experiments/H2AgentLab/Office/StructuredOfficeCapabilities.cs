using System.Text.Json;
using System.Text.RegularExpressions;
using H2AgentLab.Tools;

namespace H2AgentLab.Office;

public sealed record OfficeCapabilityDefinition(
    string Name,
    string Namespace,
    string Description,
    AgentToolAccess Access,
    AgentToolRisk Risk,
    bool SupportsParallel,
    string ResourceScope);

public static class StructuredOfficeCapabilityCatalog
{
    private static readonly string[] WordNames =
    [
        "word.list_documents",
        "word.get_active_document",
        "word.get_selection",
        "word.read_outline",
        "word.read_range",
        "word.find_text",
        "word.read_paragraphs",
        "word.read_runs",
        "word.read_styles",
        "word.read_tables",
        "word.read_sections",
        "word.read_headers_footers",
        "word.replace_range",
        "word.insert_text",
        "word.apply_format",
        "word.save_copy",
        "word.export",
        "word.verify_range",
        "word.get_spelling_errors",
        "word.get_grammar_candidates",
        "word.extract_legal_citations"
    ];

    private static readonly string[] ExcelNames =
    [
        "excel.list_workbooks",
        "excel.get_active_workbook",
        "excel.get_active_sheet",
        "excel.get_selection",
        "excel.read_range",
        "excel.read_formulas",
        "excel.read_styles",
        "excel.read_merges",
        "excel.read_hidden_state",
        "excel.write_range",
        "excel.set_formula",
        "excel.apply_format",
        "excel.recalculate",
        "excel.save_copy",
        "excel.verify_range"
    ];

    public static IReadOnlyList<OfficeCapabilityDefinition> All
        => WordNames.Select(Word)
            .Concat(ExcelNames.Select(Excel))
            .ToArray();

    public static IReadOnlyList<string> WordCapabilityNames => WordNames;
    public static IReadOnlyList<string> ExcelCapabilityNames => ExcelNames;

    public static IReadOnlyList<ToolDescriptor> RegisterInto(
        ToolRegistry registry,
        IAgentToolExecutor executor,
        string providerVersion = "1.0.0")
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(executor);

        var descriptors = new List<ToolDescriptor>();
        foreach (var capability in All)
        {
            if (registry.TryGet(capability.Name, out _))
                continue;

            var schema = JsonSerializer.SerializeToElement(new
            {
                type = "function",
                function = new
                {
                    name = capability.Name,
                    description = capability.Description,
                    parameters = new
                    {
                        type = "object",
                        properties = new { },
                        additionalProperties = true
                    }
                }
            });

            var descriptor = new ToolDescriptor(
                capability.Name,
                new ToolNamespace(
                    capability.Namespace,
                    $"Structured live {capability.Namespace} capabilities."),
                capability.Description,
                capability.Risk,
                capability.Access,
                capability.SupportsParallel,
                "v1",
                schema,
                executor,
                provenance: new ToolProvenance(
                    "office-host",
                    providerVersion,
                    "local-office-host",
                    providerVersion),
                resourceScope: new ToolResourceScope(
                    capability.ResourceScope,
                    capability.ResourceScope),
                serializationKey: capability.Namespace,
                canProvideVerificationEvidence: false);
            registry.Register(descriptor);
            descriptors.Add(descriptor);
        }

        return descriptors;
    }

    private static OfficeCapabilityDefinition Word(string name)
    {
        var mutating = name is
            "word.replace_range"
            or "word.insert_text"
            or "word.apply_format"
            or "word.save_copy"
            or "word.export";
        return new(
            name,
            "word",
            Description(name),
            mutating ? AgentToolAccess.Mutating : AgentToolAccess.ReadOnly,
            mutating ? AgentToolRisk.Medium : AgentToolRisk.Low,
            !mutating,
            "office:word:active");
    }

    private static OfficeCapabilityDefinition Excel(string name)
    {
        var mutating = name is
            "excel.write_range"
            or "excel.set_formula"
            or "excel.apply_format"
            or "excel.recalculate"
            or "excel.save_copy";
        return new(
            name,
            "excel",
            Description(name),
            mutating ? AgentToolAccess.Mutating : AgentToolAccess.ReadOnly,
            mutating ? AgentToolRisk.Medium : AgentToolRisk.Low,
            !mutating,
            "office:excel:active");
    }

    private static string Description(string name)
        => "Structured Office capability: " + name.Replace('.', ' ').Replace('_', ' ') + ".";
}

public sealed record WordSpellingCandidate(
    int Start,
    int Length,
    string Text,
    IReadOnlyList<string> Suggestions,
    string EvidenceId,
    bool NativeEvidence);

public sealed record WordGrammarCandidate(
    int Start,
    int Length,
    string Text,
    string Message,
    string EvidenceId,
    bool NativeEvidence);

public sealed record WordLegalCitation(
    string CitationText,
    string DocumentId,
    int Start,
    int Length,
    string EvidenceId);

public interface IWordLanguageEvidenceProvider
{
    Task<IReadOnlyList<WordSpellingCandidate>> GetSpellingErrorsAsync(
        string text,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WordGrammarCandidate>> GetGrammarCandidatesAsync(
        string text,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WordLegalCitation>> ExtractLegalCitationsAsync(
        string text,
        CancellationToken cancellationToken);
}

/// <summary>
/// Deterministic fallback extractor for legal citations. A native Word language provider should be
/// preferred for spelling/grammar when available; this extractor is suitable for citation parsing.
/// </summary>
public sealed class DefaultWordLanguageEvidenceProvider : IWordLanguageEvidenceProvider
{
    private static readonly Regex LegalPattern = new(
        @"(?:Luật|Nghị định|Thông tư|Quyết định)s+(?:sốs+)?(?<id>d+(?:/d{4})?/[A-ZĐ-]+(?:-[A-ZĐ]+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public Task<IReadOnlyList<WordSpellingCandidate>> GetSpellingErrorsAsync(
        string text,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<WordSpellingCandidate>>([]);
    }

    public Task<IReadOnlyList<WordGrammarCandidate>> GetGrammarCandidatesAsync(
        string text,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<WordGrammarCandidate>>([]);
    }

    public Task<IReadOnlyList<WordLegalCitation>> ExtractLegalCitationsAsync(
        string text,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        text ??= "";
        var results = LegalPattern.Matches(text)
            .Select(match => new WordLegalCitation(
                match.Value,
                match.Groups["id"].Value.ToUpperInvariant(),
                match.Index,
                match.Length,
                "evidence:word-citation:" + Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(
                            match.Value + "|" + match.Index)))
                    .ToLowerInvariant()[..24]))
            .ToArray();
        return Task.FromResult<IReadOnlyList<WordLegalCitation>>(results);
    }
}

namespace H2AgentLab.OfficeProtocol;

/// <summary>Validate the whole batch before touching Word. All indexes address the original snapshot.</summary>
public static class WordPatchRules
{
    public static string NormalizeText(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
    public static string[] Lines(string text) => NormalizeText(text).Split('\n');

    public static bool HasUniformFormatting(WordParagraphState paragraph)
        => paragraph.Runs.Select(r => (r.Style, r.Bold, r.Italic, r.Underline)).Distinct().Count() <= 1;

    public static string? ValidationError(WordLiveSnapshot before, IReadOnlyList<WordParagraphPatch> patches)
    {
        if (patches.Count is < 1 or > 100) return "Use 1–100 paragraph patches.";
        if (patches.Select(p => p.ParagraphIndex).Distinct().Count() != patches.Count)
            return "Each original paragraph index may occur only once in a batch.";
        foreach (var patch in patches)
        {
            if (patch.ParagraphIndex < 0 || patch.ParagraphIndex >= before.Paragraphs.Count)
                return $"Paragraph {patch.ParagraphIndex} does not exist. Read word.read_paragraphs and use its zero-based indexes; to add paragraphs, put newline-separated text in an existing paragraph patch.";
            if (patch.Text is null) continue;
            if (patch.Text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
                return "Text may contain tabs and newlines, but not Word structure/control characters.";
            if (!HasUniformFormatting(before.Paragraphs[patch.ParagraphIndex]))
                return $"Paragraph {patch.ParagraphIndex} has mixed character formatting. Use word.apply_format without text first only if the requested rewrite permits uniform formatting; otherwise preserve this paragraph.";
        }
        if (patches.Sum(p => (long)(p.Text?.Length ?? 0)) > 100_000
            || before.Paragraphs.Count + patches.Sum(p => p.Text is null ? 0 : Lines(p.Text).Length - 1) > 2_000)
            return "The edit exceeds the bounded Word snapshot (100,000 inserted characters / 2,000 paragraphs). Split it into smaller edits.";
        return null;
    }
}

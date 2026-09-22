using H2AgentLab.OfficeProtocol;

namespace H2AgentLab.Verification;

/// <summary>Expected target formatting comes from the pre-action snapshot and explicit patch,
/// never from the result being checked.</summary>
public static class OfficeMutationReadback
{
    public static ExcelCellState ExpectedExcelCell(ExcelCellState before, ExcelCellPatch patch, ExcelCellState observed)
        => before with
        {
            Value = patch.Formula is not null ? observed.Value : patch.ClearValue ? "" : patch.Value ?? before.Value,
            Formula = patch.Formula ?? (patch.ClearValue || patch.Value is not null ? "" : before.Formula),
            Bold = patch.Bold ?? before.Bold, Italic = patch.Italic ?? before.Italic,
            FillColor = patch.FillColor ?? before.FillColor, NumberFormat = patch.NumberFormat ?? before.NumberFormat
        };

    public static bool CanReplaceWordText(WordParagraphState before)
        => WordPatchRules.HasUniformFormatting(before);

    public static bool VerifyWordPatch(WordLiveSnapshot before, WordLiveSnapshot after, IReadOnlyList<WordParagraphPatch> patches)
    {
        if (WordPatchRules.ValidationError(before, patches) is not null || before.SessionId != after.SessionId) return false;
        var byIndex = patches.ToDictionary(p => p.ParagraphIndex);
        var position = 0;
        foreach (var original in before.Paragraphs)
        {
            if (!byIndex.TryGetValue(original.Index, out var patch))
            {
                if (position >= after.Paragraphs.Count || !Same(original with { Index = position }, after.Paragraphs[position])) return false;
                position++;
                continue;
            }
            var texts = patch.Text is null ? new string?[] { null } : WordPatchRules.Lines(patch.Text);
            foreach (var text in texts)
            {
                if (position >= after.Paragraphs.Count || after.Paragraphs[position].Index != position
                    || !VerifyWordTarget(original, after.Paragraphs[position], patch with { Text = text })) return false;
                position++;
            }
        }
        return position == after.Paragraphs.Count && Same(before.Tables, after.Tables) && Same(before.Sections, after.Sections)
            && Same(before.Headers, after.Headers) && Same(before.Footers, after.Footers);
    }

    private static bool Same<T>(T left, T right)
        => System.Text.Json.JsonSerializer.Serialize(left) == System.Text.Json.JsonSerializer.Serialize(right);

    public static bool VerifyWordTarget(WordParagraphState before, WordParagraphState after, WordParagraphPatch patch)
    {
        if (before.Style != after.Style || after.Text.TrimEnd('\r', '\n') != (patch.Text ?? before.Text).TrimEnd('\r', '\n')) return false;
        if (patch.Text is not null)
        {
            if (!CanReplaceWordText(before) || before.Runs.Count == 0) return false;
            var first = before.Runs[0];
            return after.Runs.All(r => r.Style == first.Style && r.Bold == (patch.Bold ?? first.Bold)
                && r.Italic == (patch.Italic ?? first.Italic) && r.Underline == (patch.Underline ?? first.Underline));
        }
        var expected = before.Runs.SelectMany(r => r.Text.Select(c => (c, r.Style,
            Bold: patch.Bold ?? r.Bold, Italic: patch.Italic ?? r.Italic, Underline: patch.Underline ?? r.Underline)));
        var actual = after.Runs.SelectMany(r => r.Text.Select(c => (c, r.Style, r.Bold, r.Italic, r.Underline)));
        return expected.SequenceEqual(actual);
    }
}

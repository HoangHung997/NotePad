using H2AgentLab.OfficeProtocol;

namespace H2AgentLab.Integration;

/// <summary>Only preflight rejections with no writes can be superseded by a verified correction
/// to the exact same original paragraphs in the same unchanged document state.</summary>
public sealed class OfficeRejectedMutationRecovery
{
    private sealed record Rejection(string Id, string Tool, string Session, string State, int[] Indexes);
    private readonly List<Rejection> _rejections = [];

    public string Reject(string tool, WordLiveSnapshot before, IReadOnlyList<WordParagraphPatch> patches)
    {
        var id = "office-rejected-" + Guid.NewGuid().ToString("N");
        _rejections.Add(new(id, tool, before.SessionId, before.StateToken, Indexes(patches)));
        return id;
    }

    public string[] Resolve(string tool, WordLiveSnapshot before, IReadOnlyList<WordParagraphPatch> patches, bool verified)
    {
        if (!verified) return [];
        var indexes = Indexes(patches);
        var matches = _rejections.Where(r => r.Tool == tool && r.Session == before.SessionId
            && r.State == before.StateToken && r.Indexes.SequenceEqual(indexes)).ToArray();
        foreach (var item in matches) _rejections.Remove(item);
        return matches.Select(r => r.Id).ToArray();
    }

    public void ObserveFormatting(WordLiveSnapshot before, WordLiveSnapshot after, IReadOnlyList<WordParagraphPatch> patches, bool verified)
    {
        if (!verified || patches.Any(p => p.Text is not null)) return;
        var indexes = Indexes(patches);
        for (var i = 0; i < _rejections.Count; i++)
        {
            var r = _rejections[i];
            if (r.Session == before.SessionId && r.State == before.StateToken && indexes.All(r.Indexes.Contains))
                _rejections[i] = r with { State = after.StateToken };
        }
    }

    private static int[] Indexes(IReadOnlyList<WordParagraphPatch> patches)
        => patches.Select(p => p.ParagraphIndex).Distinct().Order().ToArray();
}

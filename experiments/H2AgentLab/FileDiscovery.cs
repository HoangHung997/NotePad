namespace H2AgentLab;

public sealed record FileScan(IReadOnlyList<string> Files, bool Truncated, int UnreadableFolders, int VisitedFolders);

public static class FileDiscovery
{
    public static object Find(SafeWorkspace workspace, string query, string relative, CancellationToken cancellationToken = default)
    {
        if (query.Length is < 1 or > 180) throw new ArgumentException("Search a file name of 1..180 characters.");
        var scan = workspace.Scan(relative, 4000, cancellationToken);
        var leaf = Path.GetFileName(query.Replace('\\', '/'));
        var stem = Path.GetFileNameWithoutExtension(leaf);
        var matches = scan.Files.Select(p => new
        {
            path = p,
            distance = Distance(Path.GetFileNameWithoutExtension(p), stem),
            exact = Path.GetFileName(p).Equals(leaf, StringComparison.OrdinalIgnoreCase),
            contains = Path.GetFileName(p).Contains(leaf, StringComparison.OrdinalIgnoreCase),
            sameExtension = Path.GetExtension(leaf).Length == 0 || Path.GetExtension(p).Equals(Path.GetExtension(leaf), StringComparison.OrdinalIgnoreCase)
        }).Where(x => x.exact || x.contains || (x.sameExtension && x.distance <= Math.Clamp(stem.Length / 4, 1, 3)))
          .OrderByDescending(x => x.exact).ThenByDescending(x => x.contains).ThenBy(x => x.distance).ThenBy(x => x.path).Take(20).ToArray();
        return new { query, scope = relative, matches, scan.Truncated, scan.UnreadableFolders, scan.VisitedFolders,
            searchedSupportedFiles = scan.Files.Count, scopeIsSelectedWorkspaceOnly = true,
            note = "Observed file names only, not file contents or proof of identity. If ambiguous, ask. No matches in a limited scan does not prove universal absence. Excluded secrets/build folders and links are not searched." };
    }
    public static int Distance(string a, string b)
    {
        a = a.ToUpperInvariant(); b = b.ToUpperInvariant();
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var next = new int[b.Length + 1]; next[0] = i;
            for (var j = 1; j <= b.Length; j++) next[j] = Math.Min(Math.Min(prev[j] + 1, next[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            prev = next;
        }
        return prev[b.Length];
    }
}

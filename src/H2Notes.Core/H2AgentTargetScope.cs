using System.Text.RegularExpressions;

namespace H2Notes.Core;

public sealed record H2AgentTargetPath(string Path, bool IncludeChildren, string Source);

/// <summary>Grounding targets are independent of execution permission. A file never grants its parent directory.</summary>
public static class H2AgentTargetScope
{
    public static IReadOnlyList<H2AgentTargetPath> FromUserRequest(string prompt)
    {
        var paths = new List<H2AgentTargetPath>();
        var unquoted = prompt.ToCharArray();
        foreach (Match match in Regex.Matches(prompt, "[\"'`](?<path>(?:[A-Za-z]:[\\\\/]|\\\\\\\\)[^\"'`\\r\\n]+)[\"'`]"))
        {
            Add(paths, match.Groups["path"].Value, "user-path");
            Array.Fill(unquoted, ' ', match.Index, match.Length);
        }
        foreach (Match match in Regex.Matches(new string(unquoted), "(?:[A-Za-z]:[\\\\/]|\\\\\\\\)[^\\s\"'`<>|]+"))
            Add(paths, match.Value.TrimEnd('.', ',', ';', ')'), "user-path");
        return paths.DistinctBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static void Add(List<H2AgentTargetPath> paths, string path, string source)
    {
        try
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile) path = uri.LocalPath;
            if (!System.IO.Path.IsPathFullyQualified(path)) return;
            var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            if (full == System.IO.Path.GetPathRoot(full) || full.IndexOfAny(['*', '?', '\0']) >= 0) return;
            paths.Add(new(full, Directory.Exists(full), source));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
    }

    public static bool Contains(IReadOnlyList<H2AgentTargetPath>? targets, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path)) return false;
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            return targets?.Any(t => full.Equals(t.Path, StringComparison.OrdinalIgnoreCase)
                || t.IncludeChildren && full.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(t.Path) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch (ArgumentException) { return false; }
    }
}

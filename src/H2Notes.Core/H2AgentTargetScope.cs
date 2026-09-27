using System.Text.RegularExpressions;

namespace H2Notes.Core;

public sealed record H2AgentTargetPath(string Path, bool IncludeChildren, string Source);

/// <summary>Host-selected grounding, NOT an execution grant. A file never grants its parent.
/// Unknown mapped aliases and reparse points are not guessed into equivalence.</summary>
public static class H2AgentTargetScope
{
    public static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly Regex DeviceName = new(@"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])($|\.)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<H2AgentTargetPath> FromUserRequest(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var paths = new List<H2AgentTargetPath>();
        var unquoted = prompt.ToCharArray();
        foreach (Match match in Regex.Matches(prompt, "[\"'`](?<path>(?:[A-Za-z]:[\\\\/]|\\\\\\\\)[^\"'`\\r\\n]+)[\"'`]"))
        {
            Add(paths, match.Groups["path"].Value, "user-path");
            Array.Fill(unquoted, ' ', match.Index, match.Length);
        }
        foreach (Match match in Regex.Matches(new string(unquoted), "(?:[A-Za-z]:[\\\\/]|\\\\\\\\)[^\\s\"'`<>|]+"))
            // Do not remove a trailing dot/space: doing so can redirect an invalid Windows name.
            Add(paths, match.Value.TrimEnd(',', ';', ')'), "user-path");
        return paths.DistinctBy(p => p.Path, PathComparer).ToArray();
    }

    public static void Add(List<H2AgentTargetPath> paths, string path, string source)
    {
        ArgumentNullException.ThrowIfNull(paths);
        // Ordinary Windows paths must not pass through URI canonicalization before validation.
        // Only an explicitly supplied file URI uses that compatibility conversion.
        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out var uri) || !uri.IsFile
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return;
            path = uri.LocalPath;
        }
        if (!TryNormalize(path, out var full) || full == System.IO.Path.GetPathRoot(full)
            || !HasNoReparsePoints(full)) return;
        paths.Add(new(full, Directory.Exists(full), source));
    }

    /// <summary>Lexical ordinary-file normalization only; this does not prove live document identity.
    /// Reject device/ADS syntax before GetFullPath can normalize an ambiguous name.</summary>
    public static bool TryNormalize(string? path, out string full, string? baseDirectory = null)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32_767 || path.Any(char.IsControl)
            || path.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0) return false;
        var slash = path.Replace('\\', '/');
        if (slash.StartsWith("//./", StringComparison.Ordinal) || slash.StartsWith("//?/", StringComparison.Ordinal)
            || slash.StartsWith("/??/", StringComparison.Ordinal)) return false;
        var parts = slash.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part is "." or "..") continue;
            if (part.Contains(':') && !(i == 0 && part.Length == 2 && char.IsAsciiLetter(part[0]) && part[1] == ':')) return false;
            if (part.EndsWith(' ') || part.EndsWith('.') || DeviceName.IsMatch(part)) return false;
        }
        try
        {
            if (!System.IO.Path.IsPathFullyQualified(path))
            {
                if (baseDirectory is null || !System.IO.Path.IsPathFullyQualified(baseDirectory)
                    || System.IO.Path.IsPathRooted(path) || path.Contains(':')) return false;
                path = System.IO.Path.GetFullPath(path, baseDirectory);
            }
            full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        { return false; }
    }

    /// <summary>Check all existing ancestors, including dangling links. Inaccessible/unprovable
    /// ancestors fail closed. This is a preflight check, not a promise of race-free native I/O.</summary>
    public static bool HasNoReparsePoints(string path)
    {
        if (!TryNormalize(path, out var full)) return false;
        try
        {
            var observedAncestor = false;
            for (string? current = full; current is not null; current = System.IO.Path.GetDirectoryName(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                    observedAncestor = true;
                }
                catch (FileNotFoundException) { } // New output: continue checking the parent.
                catch (DirectoryNotFoundException) { }
            }
            return observedAncestor;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }

    public static bool Contains(IReadOnlyList<H2AgentTargetPath>? targets, string? path)
    {
        if (!TryNormalize(path, out var full) || !HasNoReparsePoints(full)) return false;
        return targets?.Any(t => TryNormalize(t.Path, out var root) && HasNoReparsePoints(root)
            && (PathComparer.Equals(full, root) || t.IncludeChildren
                && full.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(root)
                    + System.IO.Path.DirectorySeparatorChar, PathComparison))) == true;
    }
}

namespace H2Notes.Core;

public enum H2ProjectResourceSource
{
    ProjectLink = 0,
    SavedFile = 1,
    AgentEvidence = 2
}

public enum H2ProjectResourceKind
{
    WebLink = 0,
    LocalPath = 1,
    SavedFile = 2,
    AgentEvidence = 3,
    Reference = 4
}

public sealed record ProjectResourceProjection(
    string ResourceId,
    Guid ProjectId,
    H2ProjectResourceSource Source,
    H2ProjectResourceKind Kind,
    string Label,
    string? Target,
    string? Sha256,
    DateTime? ObservedUtc,
    Guid? AgentTaskId = null,
    string? EvidenceId = null);

public enum H2ResourceTargetKind
{
    None = 0,
    Web = 1,
    File = 2,
    Directory = 3,
    MissingLocal = 4,
    Unsafe = 5
}

public sealed record H2ResourceTargetDecision(
    H2ResourceTargetKind Kind,
    string? Target)
{
    public bool CanOpen => Kind is H2ResourceTargetKind.Web
        or H2ResourceTargetKind.File
        or H2ResourceTargetKind.Directory;

    public bool CanReveal => OperatingSystem.IsWindows()
        && Kind is H2ResourceTargetKind.File or H2ResourceTargetKind.Directory;
}

/// <summary>
/// Pure/safe target classification for resource UI actions. It never executes a target.
/// Only http/https and existing absolute local/UNC paths are actionable.
/// </summary>
public static class H2ResourceTargetPolicy
{
    public static H2ResourceTargetDecision Evaluate(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return new(H2ResourceTargetKind.None, null);

        var value = target.Trim();
        if (value.Length > 32_000)
            return new(H2ResourceTargetKind.Unsafe, value);

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is "http" or "https")
                return new(H2ResourceTargetKind.Web, uri.AbsoluteUri);

            if (uri.IsFile)
                return EvaluateLocal(uri.LocalPath);

            // drive-letter paths may parse as URI schemes on some platforms; let the
            // filesystem path branch below handle them when they are fully qualified.
            if (!LooksLikeWindowsDrive(value))
                return new(H2ResourceTargetKind.Unsafe, value);
        }

        return EvaluateLocal(value);
    }

    private static H2ResourceTargetDecision EvaluateLocal(string value)
    {
        try
        {
            if (!Path.IsPathFullyQualified(value))
                return new(H2ResourceTargetKind.Unsafe, value);

            var full = Path.GetFullPath(value);
            if (File.Exists(full))
                return new(H2ResourceTargetKind.File, full);
            if (Directory.Exists(full))
                return new(H2ResourceTargetKind.Directory, full);
            return new(H2ResourceTargetKind.MissingLocal, full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(H2ResourceTargetKind.Unsafe, value);
        }
    }

    private static bool LooksLikeWindowsDrive(string value)
        => value.Length >= 3
           && char.IsLetter(value[0])
           && value[1] == ':'
           && (value[2] == '\\' || value[2] == '/');
}

/// <summary>
/// Rebuildable files/resources projection. ProjectRecord continues to persist only its
/// existing ProjectLink/user data; Agent evidence remains Agent-owned.
/// </summary>
public sealed class H2ProjectResourceProjectionService
{
    private readonly IH2AgentAdapter _agent;

    public H2ProjectResourceProjectionService(IH2AgentAdapter agent)
        => _agent = agent ?? throw new ArgumentNullException(nameof(agent));

    public IReadOnlyList<ProjectResourceProjection> Build(ProjectRecord project, int limit = 120)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));

        var result = new List<ProjectResourceProjection>();
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var link in project.Links)
        {
            var label = string.IsNullOrWhiteSpace(link.Label) ? link.Target : link.Label;
            var kind = LinkKind(link.Target);
            result.Add(new(
                "link:" + link.Id.ToString("N"),
                project.Id,
                H2ProjectResourceSource.ProjectLink,
                kind,
                Bound(label, 240),
                link.Target,
                null,
                project.UpdatedAtUtc));
            if (!string.IsNullOrWhiteSpace(link.Target))
                seenTargets.Add(NormalizeTargetKey(link.Target));
        }

        var savedFiles = project.Conversations
            .SelectMany(conversation => conversation.Messages)
            .SelectMany(message => message.SavedFiles)
            .OrderByDescending(file => file.SavedAt)
            .ToArray();

        foreach (var file in savedFiles)
        {
            if (string.IsNullOrWhiteSpace(file.Path)) continue;
            var key = NormalizeTargetKey(file.Path);
            if (!seenTargets.Add(key)) continue;

            result.Add(new(
                "saved:" + StableResourceId(file.Path, file.Sha256),
                project.Id,
                H2ProjectResourceSource.SavedFile,
                H2ProjectResourceKind.SavedFile,
                Bound(string.IsNullOrWhiteSpace(file.Name) ? Path.GetFileName(file.Path) : file.Name, 240),
                file.Path,
                string.IsNullOrWhiteSpace(file.Sha256) ? null : file.Sha256,
                file.SavedAt));
        }

        foreach (var task in _agent.GetRecentTasks(project.Id, Math.Min(limit, 100))
                     .Where(task => task.ProjectId == project.Id)
                     .OrderByDescending(task => task.UpdatedUtc))
        {
            foreach (var evidence in task.Evidence)
            {
                if (string.IsNullOrWhiteSpace(evidence.EvidenceId)) continue;
                if (result.Any(item => item.EvidenceId == evidence.EvidenceId)) continue;

                var label = string.IsNullOrWhiteSpace(evidence.Summary)
                    ? $"Agent evidence · {evidence.Kind}"
                    : evidence.Summary!;

                result.Add(new(
                    "evidence:" + evidence.EvidenceId,
                    project.Id,
                    H2ProjectResourceSource.AgentEvidence,
                    H2ProjectResourceKind.AgentEvidence,
                    Bound(label, 300),
                    Target: null,
                    evidence.Sha256,
                    task.UpdatedUtc,
                    task.TaskId,
                    evidence.EvidenceId));
            }
        }

        return result
            .OrderBy(item => SourcePriority(item.Source))
            .ThenByDescending(item => item.ObservedUtc ?? DateTime.MinValue)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    private static H2ProjectResourceKind LinkKind(string target)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
            return H2ProjectResourceKind.WebLink;

        if (!string.IsNullOrWhiteSpace(target)
            && (Path.IsPathFullyQualified(target)
                || (Uri.TryCreate(target, UriKind.Absolute, out var fileUri) && fileUri.IsFile)))
            return H2ProjectResourceKind.LocalPath;

        return H2ProjectResourceKind.Reference;
    }

    private static int SourcePriority(H2ProjectResourceSource source)
        => source switch
        {
            H2ProjectResourceSource.ProjectLink => 0,
            H2ProjectResourceSource.SavedFile => 1,
            _ => 2
        };

    private static string NormalizeTargetKey(string target)
        => target.Trim().Replace('/', '\\');

    private static string StableResourceId(string path, string? sha)
    {
        var raw = (sha ?? "") + "|" + path;
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(raw)))[..24].ToLowerInvariant();
    }

    private static string Bound(string value, int max)
    {
        value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= max ? value : value[..max];
    }
}

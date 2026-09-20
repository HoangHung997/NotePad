namespace H2Notes.Core;

public enum H2EvidenceInspectionKind
{
    Web = 0,
    File = 1,
    Other = 2
}

public sealed record H2EvidenceInspectionProjection(
    string EvidenceId,
    Guid ProjectId,
    Guid AgentTaskId,
    H2EvidenceInspectionKind InspectionKind,
    string Kind,
    string Summary,
    string? Sha256,
    string? SourceUri,
    string? LocalPath,
    string? Provenance,
    DateTime ObservedUtc);

/// <summary>
/// Rebuildable evidence inspector projection. Evidence identity and provenance remain Agent-owned:
/// every build resolves the authoritative evidence through IH2AgentAdapter.GetEvidence().
/// </summary>
public sealed class H2EvidenceInspectionProjectionService
{
    private readonly IH2AgentAdapter _agent;

    public H2EvidenceInspectionProjectionService(IH2AgentAdapter agent)
        => _agent = agent ?? throw new ArgumentNullException(nameof(agent));

    public IReadOnlyList<H2EvidenceInspectionProjection> Build(
        ProjectRecord project,
        int limit = 120)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));

        var result = new List<H2EvidenceInspectionProjection>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var task in _agent.GetRecentTasks(project.Id, Math.Min(limit, 100))
                     .Where(task => task.ProjectId == project.Id)
                     .OrderByDescending(task => task.UpdatedUtc))
        {
            foreach (var reference in task.Evidence)
            {
                if (string.IsNullOrWhiteSpace(reference.EvidenceId)
                    || !seen.Add(reference.EvidenceId))
                    continue;

                // Re-fetch by identity so H2 never treats the task summary copy as authoritative.
                var evidence = _agent.GetEvidence(reference.EvidenceId) ?? reference;
                result.Add(new(
                    evidence.EvidenceId,
                    project.Id,
                    task.TaskId,
                    Classify(evidence),
                    Bound(evidence.Kind, 120),
                    Bound(string.IsNullOrWhiteSpace(evidence.Summary)
                        ? evidence.Kind
                        : evidence.Summary!, 1200),
                    NormalizeHash(evidence.Sha256),
                    NormalizeWebUri(evidence.SourceUri),
                    NormalizeLocalPath(evidence.LocalPath),
                    BoundOrNull(evidence.Provenance, 500),
                    task.UpdatedUtc));
            }
        }

        return result
            .OrderByDescending(item => item.ObservedUtc)
            .ThenBy(item => item.EvidenceId, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    private static H2EvidenceInspectionKind Classify(H2AgentEvidence evidence)
    {
        if (NormalizeWebUri(evidence.SourceUri) is not null)
            return H2EvidenceInspectionKind.Web;
        if (NormalizeLocalPath(evidence.LocalPath) is not null)
            return H2EvidenceInspectionKind.File;

        var kind = (evidence.Kind ?? "").ToLowerInvariant();
        if (kind.Contains("web", StringComparison.Ordinal)
            || kind.Contains("citation", StringComparison.Ordinal)
            || kind.Contains("source", StringComparison.Ordinal))
            return H2EvidenceInspectionKind.Web;
        if (kind.Contains("file", StringComparison.Ordinal)
            || kind.Contains("artifact", StringComparison.Ordinal)
            || kind.Contains("document", StringComparison.Ordinal))
            return H2EvidenceInspectionKind.File;
        return H2EvidenceInspectionKind.Other;
    }

    private static string? NormalizeWebUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https"
            ? uri.AbsoluteUri
            : null;
    }

    private static string? NormalizeLocalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var path = value.Trim();
            return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? NormalizeHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var hash = value.Trim().ToLowerInvariant();
        return hash.Length <= 256 ? hash : hash[..256];
    }

    private static string Bound(string value, int max)
    {
        value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= max ? value : value[..max];
    }

    private static string? BoundOrNull(string? value, int max)
        => string.IsNullOrWhiteSpace(value) ? null : Bound(value, max);
}

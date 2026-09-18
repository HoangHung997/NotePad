namespace H2AgentLab.Tools;

public sealed record InteractionAdapterCandidate
{
    public InteractionAdapterCandidate(
        string adapterId,
        string capabilityFamily,
        ToolInteractionFidelity interactionFidelity,
        bool available = true,
        bool explicitRequestOnly = false,
        IEnumerable<string>? explicitRequestTerms = null)
    {
        AdapterId = ToolNamespace.NormalizeId(adapterId, nameof(adapterId));
        Preference = new ToolPreferenceMetadata(
            capabilityFamily,
            interactionFidelity,
            explicitRequestOnly,
            explicitRequestTerms);
        Available = available;
    }

    public string AdapterId { get; }
    public ToolPreferenceMetadata Preference { get; }
    public bool Available { get; }
}

/// <summary>
/// Generic adapter ordering:
/// structured typed interface > accessibility/UIA > screenshot/pixel > escape hatch.
/// Providers/extensions supply metadata; the core contains no application-family switch.
/// </summary>
public static class InteractionAdapterPreference
{
    public static InteractionAdapterCandidate? Choose(
        string query,
        IEnumerable<InteractionAdapterCandidate> candidates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .Where(x => x is not null && x.Available)
            .Where(x => !x.Preference.ExplicitRequestOnly
                || x.Preference.MatchesExplicitRequest(query))
            .OrderByDescending(x => IsExactAdapterRequest(query, x))
            .ThenBy(x => DocumentToolPreference.FidelityRank(
                x.Preference.InteractionFidelity))
            .ThenBy(x => x.AdapterId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static bool IsExactAdapterRequest(
        string query,
        InteractionAdapterCandidate candidate)
    {
        var normalized = query
            .Trim()
            .ToLowerInvariant()
            .Replace(' ', '_');
        return string.Equals(
                normalized,
                candidate.AdapterId,
                StringComparison.Ordinal)
            || query.Contains(
                candidate.AdapterId,
                StringComparison.OrdinalIgnoreCase);
    }
}

namespace H2AgentLab.Tools;

/// <summary>
/// Compatibility name for the generic metadata-driven tool preference policy.
/// The core does not know application families. Providers/extensions declare equivalent
/// capability families and interaction fidelity on ToolDescriptor.Preference.
/// </summary>
public static class DocumentToolPreference
{
    public static IReadOnlyList<ToolSearchResult> Apply(
        string query,
        IReadOnlyList<ToolSearchResult> candidates,
        int maxResults)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(candidates);
        if (maxResults is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(maxResults));

        var filtered = candidates
            .Where(x => IsEligible(query, x.Descriptor.Preference))
            .Take(50)
            .ToList();

        var indexed = filtered
            .Select((result, index) => new IndexedResult(index, result))
            .Where(x => x.Result.Descriptor.Preference is not null)
            .GroupBy(
                x => x.Result.Descriptor.Preference!.CapabilityFamily,
                StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToArray();

        foreach (var group in indexed)
        {
            var slots = group
                .Select(x => x.Index)
                .OrderBy(x => x)
                .ToArray();
            var ordered = group
                .Select(x => x.Result)
                .OrderByDescending(x => IsExactToolRequest(query, x.Descriptor))
                .ThenBy(x => FidelityRank(
                    x.Descriptor.Preference!.InteractionFidelity))
                .ThenByDescending(x => x.Score)
                .ThenBy(x => x.Descriptor.Name, StringComparer.Ordinal)
                .ToArray();

            for (var i = 0; i < slots.Length; i++)
                filtered[slots[i]] = ordered[i];
        }

        return filtered
            .Take(maxResults)
            .ToArray();
    }

    private static bool IsEligible(
        string query,
        ToolPreferenceMetadata? preference)
        => preference is null
            || !preference.ExplicitRequestOnly
            || preference.MatchesExplicitRequest(query);

    private static bool IsExactToolRequest(
        string query,
        ToolDescriptor descriptor)
    {
        var normalized = query
            .Trim()
            .ToLowerInvariant()
            .Replace(' ', '_');
        return string.Equals(
                normalized,
                descriptor.Name,
                StringComparison.Ordinal)
            || query.Contains(
                descriptor.Name,
                StringComparison.OrdinalIgnoreCase);
    }

    internal static int FidelityRank(ToolInteractionFidelity fidelity)
        => fidelity switch
        {
            ToolInteractionFidelity.Structured => 0,
            ToolInteractionFidelity.Accessibility => 1,
            ToolInteractionFidelity.Visual => 2,
            ToolInteractionFidelity.EscapeHatch => 3,
            _ => 4
        };

    private sealed record IndexedResult(
        int Index,
        ToolSearchResult Result);
}

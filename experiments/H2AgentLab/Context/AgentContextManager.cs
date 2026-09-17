using System.Text;
using H2AgentLab.Prompting;
using H2AgentLab.Transport;

namespace H2AgentLab.Context;

/// <summary>
/// Explicit deterministic limits for active v2 model context. Character bounds are intentionally
/// provider-neutral; provider token accounting remains telemetry and can later trigger compaction.
/// These are hard output caps, not targets the manager attempts to fill.
/// </summary>
public sealed record AgentContextBudget
{
    public int MaxTotalCharacters { get; init; } = 24_000;
    public int MaxTaskContractCharacters { get; init; } = 4_000;
    public int MaxCurrentStateCharacters { get; init; } = 5_000;
    public int MaxRecentTurnsCharacters { get; init; } = 7_000;
    public int MaxToolSummariesCharacters { get; init; } = 4_000;
    public int MaxCompactedHistoryCharacters { get; init; } = 4_000;
    public int MaxCharactersPerItem { get; init; } = 2_000;
    public int MaxRecentTurns { get; init; } = 8;
    public int MaxToolSummaries { get; init; } = 8;

    public void Validate()
    {
        Positive(MaxTotalCharacters, nameof(MaxTotalCharacters));
        NonNegative(MaxTaskContractCharacters, nameof(MaxTaskContractCharacters));
        NonNegative(MaxCurrentStateCharacters, nameof(MaxCurrentStateCharacters));
        NonNegative(MaxRecentTurnsCharacters, nameof(MaxRecentTurnsCharacters));
        NonNegative(MaxToolSummariesCharacters, nameof(MaxToolSummariesCharacters));
        NonNegative(MaxCompactedHistoryCharacters, nameof(MaxCompactedHistoryCharacters));
        Positive(MaxCharactersPerItem, nameof(MaxCharactersPerItem));
        NonNegative(MaxRecentTurns, nameof(MaxRecentTurns));
        NonNegative(MaxToolSummaries, nameof(MaxToolSummaries));
        if (MaxTotalCharacters > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(MaxTotalCharacters), "Context budget exceeds the 1,000,000-character safety limit.");
    }

    private static void Positive(int value, string name)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(name, "Context budget must be positive.");
    }

    private static void NonNegative(int value, string name)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(name, "Context budget cannot be negative.");
    }
}

public sealed record AgentContextTurn
{
    public AgentContextTurn(string sourceId, AgentTransportMessageRole role, string content, long sequence, double relevance = 1)
    {
        SourceId = ValidateSourceId(sourceId);
        Content = content ?? "";
        Role = role;
        Sequence = sequence;
        Relevance = ValidateRelevance(relevance);
    }

    public string SourceId { get; }
    public AgentTransportMessageRole Role { get; }
    public string Content { get; }
    public long Sequence { get; }
    public double Relevance { get; }

    internal static string ValidateSourceId(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        sourceId = sourceId.Trim();
        if (sourceId.Length > 128 || sourceId.Any(char.IsControl))
            throw new ArgumentException("Context source IDs must be at most 128 characters and contain no control characters.", nameof(sourceId));
        return sourceId;
    }

    internal static double ValidateRelevance(double relevance)
    {
        if (!double.IsFinite(relevance) || relevance < 0 || relevance > 1)
            throw new ArgumentOutOfRangeException(nameof(relevance), "Context relevance must be between 0 and 1.");
        return relevance;
    }
}

public sealed record AgentContextToolSummary
{
    public AgentContextToolSummary(string sourceId, string toolName, string summary, long sequence, double relevance = 1)
    {
        SourceId = AgentContextTurn.ValidateSourceId(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ToolName = toolName.Trim();
        if (ToolName.Length > 128 || ToolName.Any(char.IsControl))
            throw new ArgumentException("Tool name is too long or contains control characters.", nameof(toolName));
        Summary = summary ?? "";
        Sequence = sequence;
        Relevance = AgentContextTurn.ValidateRelevance(relevance);
    }

    public string SourceId { get; }
    public string ToolName { get; }
    public string Summary { get; }
    public long Sequence { get; }
    public double Relevance { get; }
}

public sealed record AgentContextInput(
    string? TaskContract = null,
    string? CurrentState = null,
    IReadOnlyList<AgentContextTurn>? RecentTurns = null,
    IReadOnlyList<AgentContextToolSummary>? ToolSummaries = null,
    string? CompactedHistory = null);

public sealed record AgentContextUsage(
    int TotalCharacters,
    int TaskContractCharacters,
    int CurrentStateCharacters,
    int RecentTurnsCharacters,
    int ToolSummariesCharacters,
    int CompactedHistoryCharacters,
    int SelectedRecentTurns,
    int DroppedRecentTurns,
    int SelectedToolSummaries,
    int DroppedToolSummaries,
    bool TaskContractTruncated,
    bool CurrentStateTruncated,
    bool CompactedHistoryTruncated);

public sealed record AgentContextSnapshot(
    AgentPromptRuntimeContext RuntimeContext,
    AgentContextBudget Budget,
    AgentContextUsage Usage,
    IReadOnlyList<string> RecentTurnSourceIds,
    IReadOnlyList<string> ToolSummarySourceIds);

/// <summary>
/// Produces bounded dynamic context without touching the frozen v1 session prompt path. Priority is
/// task contract -> current state -> recent relevant turns -> relevant tool summaries -> compacted
/// history. Within list sections relevance wins first, then recency. Selected turns are rendered in
/// chronological order so the model still sees a coherent local conversation.
/// </summary>
public sealed class AgentContextManager
{
    private const string TruncatedMarker = "…[truncated]";
    private readonly AgentContextBudget _budget;

    public AgentContextManager(AgentContextBudget? budget = null)
    {
        _budget = budget ?? new AgentContextBudget();
        _budget.Validate();
    }

    public AgentContextSnapshot Build(AgentContextInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var remaining = _budget.MaxTotalCharacters;

        var taskSource = Normalize(input.TaskContract);
        var taskLimit = Math.Min(_budget.MaxTaskContractCharacters, remaining);
        var task = Clip(taskSource, taskLimit, out var taskTruncated);
        remaining -= task.Length;

        var working = new StringBuilder();
        var currentSource = Normalize(input.CurrentState);
        var current = AddScalarSection(working, "Current state", currentSource,
            _budget.MaxCurrentStateCharacters, ref remaining, out var currentTruncated);

        var recent = BuildTurnSection(input.RecentTurns ?? [], AvailableForNextSection(working, remaining));
        AppendWorkingSection(working, recent.Text, ref remaining);

        var tools = BuildToolSection(input.ToolSummaries ?? [], AvailableForNextSection(working, remaining));
        AppendWorkingSection(working, tools.Text, ref remaining);

        var compactedSource = Normalize(input.CompactedHistory);
        var compactedCandidate = BuildScalarSection("Compacted history", compactedSource,
            Math.Min(_budget.MaxCompactedHistoryCharacters, AvailableForNextSection(working, remaining)), out var compactedTruncated);
        var compacted = AppendWorkingSection(working, compactedCandidate, ref remaining);
        if (compacted.Length < compactedCandidate.Length) compactedTruncated = compactedSource.Length > 0;

        var total = task.Length + working.Length;
        if (total > _budget.MaxTotalCharacters)
            throw new InvalidOperationException("AgentContextManager exceeded its total character budget.");

        var usage = new AgentContextUsage(
            TotalCharacters: total,
            TaskContractCharacters: task.Length,
            CurrentStateCharacters: current.Length,
            RecentTurnsCharacters: recent.EmittedCharacters,
            ToolSummariesCharacters: tools.EmittedCharacters,
            CompactedHistoryCharacters: compacted.Length,
            SelectedRecentTurns: recent.SourceIds.Count,
            DroppedRecentTurns: Math.Max(0, recent.EligibleCount - recent.SourceIds.Count),
            SelectedToolSummaries: tools.SourceIds.Count,
            DroppedToolSummaries: Math.Max(0, tools.EligibleCount - tools.SourceIds.Count),
            TaskContractTruncated: taskTruncated,
            CurrentStateTruncated: currentTruncated,
            CompactedHistoryTruncated: compactedTruncated);

        return new AgentContextSnapshot(
            new AgentPromptRuntimeContext(
                TaskContract: EmptyToNull(task),
                WorkingState: EmptyToNull(working.ToString()),
                LiveEnvironment: null),
            _budget,
            usage,
            recent.SourceIds,
            tools.SourceIds);
    }

    private string AddScalarSection(
        StringBuilder working,
        string heading,
        string source,
        int sectionBudget,
        ref int remaining,
        out bool truncated)
    {
        var available = AvailableForNextSection(working, remaining);
        var candidate = BuildScalarSection(heading, source, Math.Min(sectionBudget, available), out truncated);
        var emitted = AppendWorkingSection(working, candidate, ref remaining);
        if (emitted.Length < candidate.Length) truncated = source.Length > 0;
        return emitted;
    }

    private static string BuildScalarSection(string heading, string source, int limit, out bool truncated)
    {
        if (source.Length == 0)
        {
            truncated = false;
            return "";
        }
        if (limit <= 0)
        {
            truncated = true;
            return "";
        }
        var prefix = "## " + heading + "\n";
        if (limit <= prefix.Length)
        {
            truncated = true;
            return "";
        }
        var body = Clip(source, limit - prefix.Length, out truncated);
        return body.Length == 0 ? "" : prefix + body;
    }

    private SectionBuildResult BuildTurnSection(IReadOnlyList<AgentContextTurn> input, int availableTotal)
    {
        var eligible = input
            .Where(x => x.Relevance > 0 && !string.IsNullOrWhiteSpace(x.Content))
            .OrderByDescending(x => x.Relevance)
            .ThenByDescending(x => x.Sequence)
            .ThenBy(x => x.SourceId, StringComparer.Ordinal)
            .Take(_budget.MaxRecentTurns)
            .ToArray();
        var eligibleCount = input.Count(x => x.Relevance > 0 && !string.IsNullOrWhiteSpace(x.Content));
        var limit = Math.Min(_budget.MaxRecentTurnsCharacters, availableTotal);
        return BuildListSection(
            "Recent relevant turns",
            eligible,
            eligibleCount,
            x => x.SourceId,
            x => $"[turn:{x.SourceId}] {x.Role}: ",
            x => x.Content,
            x => x.Sequence,
            limit);
    }

    private SectionBuildResult BuildToolSection(IReadOnlyList<AgentContextToolSummary> input, int availableTotal)
    {
        var eligible = input
            .Where(x => x.Relevance > 0 && !string.IsNullOrWhiteSpace(x.Summary))
            .OrderByDescending(x => x.Relevance)
            .ThenByDescending(x => x.Sequence)
            .ThenBy(x => x.SourceId, StringComparer.Ordinal)
            .Take(_budget.MaxToolSummaries)
            .ToArray();
        var eligibleCount = input.Count(x => x.Relevance > 0 && !string.IsNullOrWhiteSpace(x.Summary));
        var limit = Math.Min(_budget.MaxToolSummariesCharacters, availableTotal);
        return BuildListSection(
            "Relevant tool summaries",
            eligible,
            eligibleCount,
            x => x.SourceId,
            x => $"[tool:{x.SourceId}] {x.ToolName}: ",
            x => x.Summary,
            x => x.Sequence,
            limit);
    }

    private SectionBuildResult BuildListSection<T>(
        string heading,
        IReadOnlyList<T> ranked,
        int eligibleCount,
        Func<T, string> sourceId,
        Func<T, string> prefix,
        Func<T, string> text,
        Func<T, long> sequence,
        int limit)
    {
        var header = "## " + heading + "\n";
        if (limit <= header.Length || ranked.Count == 0)
            return new("", [], eligibleCount, 0);

        var available = limit - header.Length;
        var selected = new List<(T Item, string Line)>();
        foreach (var item in ranked)
        {
            var itemPrefix = prefix(item);
            var separator = selected.Count == 0 ? 0 : 1;
            if (available <= separator + itemPrefix.Length) continue;
            var contentLimit = Math.Min(_budget.MaxCharactersPerItem, available - separator - itemPrefix.Length);
            var clipped = Clip(Normalize(text(item)), contentLimit, out _);
            if (clipped.Length == 0) continue;
            var line = itemPrefix + clipped;
            selected.Add((item, line));
            available -= separator + line.Length;
        }
        if (selected.Count == 0) return new("", [], eligibleCount, 0);

        selected.Sort((a, b) =>
        {
            var order = sequence(a.Item).CompareTo(sequence(b.Item));
            return order != 0 ? order : string.CompareOrdinal(sourceId(a.Item), sourceId(b.Item));
        });
        var body = string.Join('\n', selected.Select(x => x.Line));
        var output = header + body;
        return new(output, selected.Select(x => sourceId(x.Item)).ToArray(), eligibleCount, output.Length);
    }

    private static int AvailableForNextSection(StringBuilder working, int remaining)
    {
        var separator = working.Length == 0 ? 0 : 2;
        return Math.Max(0, remaining - separator);
    }

    private static string AppendWorkingSection(StringBuilder working, string section, ref int remaining)
    {
        if (string.IsNullOrEmpty(section) || remaining <= 0) return "";
        var separator = working.Length == 0 ? "" : "\n\n";
        if (remaining <= separator.Length) return "";
        var room = remaining - separator.Length;
        var emitted = section.Length <= room ? section : section[..room];
        if (emitted.Length == 0) return "";
        working.Append(separator);
        working.Append(emitted);
        remaining -= separator.Length + emitted.Length;
        return emitted;
    }

    private static string Clip(string value, int limit, out bool truncated)
    {
        if (limit <= 0)
        {
            truncated = value.Length > 0;
            return "";
        }
        if (value.Length <= limit)
        {
            truncated = false;
            return value;
        }
        truncated = true;
        if (limit <= TruncatedMarker.Length) return value[..limit];
        return value[..(limit - TruncatedMarker.Length)] + TruncatedMarker;
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : value.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;

    private sealed record SectionBuildResult(
        string Text,
        IReadOnlyList<string> SourceIds,
        int EligibleCount,
        int EmittedCharacters);
}

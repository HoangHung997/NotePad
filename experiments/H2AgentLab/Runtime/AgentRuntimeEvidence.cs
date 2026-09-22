using H2AgentLab.Session;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;

namespace H2AgentLab.Runtime;

public sealed record AgentRuntimeEvidenceProjection(
    AgentEvidenceReference Evidence,
    string ModelContent);

/// <summary>
/// Bridges normal ToolRegistry execution to the existing durable ArtifactStore. Raw output remains
/// host-owned; the model receives either a small inline result plus evidence handle or, for large
/// output, only the bounded ArtifactStore projection.
/// </summary>
public sealed class AgentRuntimeEvidenceProjector
{
    public const int MaxInlineToolOutputCharacters = 8_000;
    public const int MaxSummaryCharacters = 480;

    private readonly ArtifactStore _store;

    public AgentRuntimeEvidenceProjector(ArtifactStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public AgentRuntimeEvidenceProjection? Project(
        ToolDescriptor descriptor,
        string sourceId,
        long sequence,
        string output,
        bool forceEvidence = false)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        output ??= "";

        var requiresEvidence = forceEvidence || descriptor.IsMutating
            || descriptor.CanProvideVerificationEvidence
            || output.Length > Math.Min(MaxInlineToolOutputCharacters, descriptor.Limits.MaxOutputCharacters);
        if (!requiresEvidence)
            return null;

        var summary = BuildSummary(descriptor.Name, output);
        var projection = _store.StoreText(
            AgentArtifactKind.ToolOutput,
            sourceId,
            descriptor.Name,
            output,
            summary,
            sequence);

        var evidence = new AgentEvidenceReference(
            AgentEvidenceKind.ToolResult,
            projection.Handle.Id,
            projection.Handle.Sha256,
            summary);

        if (output.Length > Math.Min(MaxInlineToolOutputCharacters, descriptor.Limits.MaxOutputCharacters))
        {
            return new AgentRuntimeEvidenceProjection(
                evidence,
                projection.ContextSummary.Summary);
        }

        var evidenceLine =
            $"[evidence:{projection.Handle.Id}] sha256={projection.Handle.Sha256}; bytes={projection.Handle.Bytes}";
        var modelContent = string.IsNullOrEmpty(output)
            ? evidenceLine
            : output.TrimEnd() + Environment.NewLine + evidenceLine;

        return new AgentRuntimeEvidenceProjection(evidence, modelContent);
    }

    private static string BuildSummary(string toolName, string output)
    {
        var normalized = (output ?? "")
            .Trim()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        if (normalized.Length == 0)
            return $"{toolName} completed with an empty textual result.";

        const string marker = "…[summary truncated]";
        var prefix = toolName + " observed: ";
        var room = Math.Max(0, MaxSummaryCharacters - prefix.Length);
        if (normalized.Length > room)
        {
            if (room <= marker.Length)
                normalized = normalized[..room];
            else
                normalized = normalized[..(room - marker.Length)] + marker;
        }

        return prefix + normalized;
    }
}

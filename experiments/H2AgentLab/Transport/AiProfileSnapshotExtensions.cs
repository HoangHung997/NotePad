using H2Notes.Core;

namespace H2AgentLab.Transport;

internal static class AiProfileSnapshotExtensions
{
    /// <summary>
    /// Agent Lab keeps a request-local profile snapshot so UI/settings mutation cannot change an active
    /// transport turn. H2Notes.Core currently keeps its own Copy helper internal to that assembly; this
    /// adapter mirrors the public profile fields until V2-0210 extracts a shared public snapshot helper.
    /// </summary>
    public static AiProfile Copy(this AiProfile source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new AiProfile
        {
            Id = source.Id,
            Name = source.Name,
            Protocol = source.Protocol,
            BaseUrl = source.BaseUrl,
            Model = source.Model,
            TimeoutSeconds = source.TimeoutSeconds,
            WaitForCompletion = source.WaitForCompletion,
            RequestReasoningSummary = source.RequestReasoningSummary,
            ReasoningEffort = source.ReasoningEffort,
            OllamaThinking = source.OllamaThinking
        };
    }
}

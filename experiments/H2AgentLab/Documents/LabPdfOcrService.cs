using H2Notes.Core;

namespace H2AgentLab.Documents;

/// <summary>
/// Thin Lab wrapper around H2 Core PDF/OCR preparation. All validation, runtime discovery,
/// page limits, offline OCR execution and layout parsing remain owned by H2Notes.Core.
/// </summary>
public sealed class LabPdfOcrService
{
    public string RuntimeStatus(AiPdfSettings settings, string bridgePath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgePath);
        return AiPdfProcessor.RuntimeStatus(settings, bridgePath);
    }

    public bool NeedsPreparation(
        IReadOnlyList<AiTurn> turns,
        AiPdfSettings settings)
    {
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(settings);
        return AiPdfProcessor.NeedsPreparation(turns, settings);
    }

    public async Task<AiAttachment> PrepareAttachmentAsync(
        string name,
        byte[] bytes,
        AiPdfSettings settings,
        string bridgePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgePath);

        var attachment = AiDocuments.Read(name, bytes);
        return await AiPdfProcessor.PrepareAttachmentAsync(
            attachment,
            settings,
            bridgePath,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<AiPageLayout> PrepareLayoutAsync(
        string name,
        byte[] bytes,
        AiPdfSettings settings,
        string bridgePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgePath);

        var attachment = AiDocuments.Read(name, bytes);
        return await AiPdfProcessor.PrepareLayoutAsync(
            attachment,
            settings,
            bridgePath,
            progress,
            cancellationToken).ConfigureAwait(false);
    }
}

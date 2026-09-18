using H2Notes.Core;

namespace H2AgentLab.Documents;

public sealed record LabDocumentInspection(
    string Name,
    string MimeType,
    string Sha256,
    int ByteLength,
    string Text,
    string Notice,
    bool IsPdf,
    bool IsImage);

/// <summary>
/// Thin Lab adapter over H2 Core document safety/sniffing/hash/Office extraction.
/// It deliberately owns no independent DOCX/XLSX/text reader.
/// </summary>
public sealed class LabDocumentInspectionService
{
    public LabDocumentInspection Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return FromAttachment(AiDocuments.Read(path));
    }

    public LabDocumentInspection Inspect(string name, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(bytes);
        return FromAttachment(AiDocuments.Read(name, bytes));
    }

    public AiAttachment ReadAttachment(string name, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(bytes);
        return AiDocuments.Read(name, bytes);
    }

    private static LabDocumentInspection FromAttachment(AiAttachment attachment)
        => new(
            attachment.Name,
            attachment.MimeType,
            attachment.Sha256.ToLowerInvariant(),
            attachment.Data.Length,
            attachment.Text,
            attachment.Notice,
            attachment.IsPdf,
            attachment.IsImage);
}

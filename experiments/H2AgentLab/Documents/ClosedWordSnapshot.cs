using DocumentFormat.OpenXml.Packaging;
using H2Notes.Core;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace H2AgentLab.Documents;

public sealed record ClosedWordSnapshot(
    string Sha256,
    IReadOnlyList<ClosedWordParagraphSnapshot> BodyParagraphs,
    IReadOnlyList<ClosedWordTableSnapshot> Tables,
    IReadOnlyList<ClosedWordSectionSnapshot> Sections,
    IReadOnlyList<ClosedWordPartSnapshot> Headers,
    IReadOnlyList<ClosedWordPartSnapshot> Footers);

public sealed record ClosedWordParagraphSnapshot(
    int Index,
    string Text,
    string? StyleId,
    IReadOnlyList<ClosedWordRunSnapshot> Runs);

public sealed record ClosedWordRunSnapshot(
    int Index,
    string Text,
    string? StyleId,
    bool Bold,
    bool Italic,
    bool Underline);

public sealed record ClosedWordTableSnapshot(
    int Index,
    IReadOnlyList<ClosedWordTableRowSnapshot> Rows);

public sealed record ClosedWordTableRowSnapshot(
    int Index,
    IReadOnlyList<ClosedWordTableCellSnapshot> Cells);

public sealed record ClosedWordTableCellSnapshot(
    int Index,
    string Text,
    IReadOnlyList<ClosedWordParagraphSnapshot> Paragraphs);

public sealed record ClosedWordSectionSnapshot(
    int Index,
    uint? PageWidthTwips,
    uint? PageHeightTwips,
    int? MarginTopTwips,
    uint? MarginRightTwips,
    int? MarginBottomTwips,
    uint? MarginLeftTwips,
    IReadOnlyList<ClosedWordHeaderFooterReferenceSnapshot> HeaderReferences,
    IReadOnlyList<ClosedWordHeaderFooterReferenceSnapshot> FooterReferences);

public sealed record ClosedWordHeaderFooterReferenceSnapshot(
    string RelationshipId,
    string Type);

public sealed record ClosedWordPartSnapshot(
    string RelationshipId,
    IReadOnlyList<ClosedWordParagraphSnapshot> Paragraphs);

/// <summary>
/// Deterministic read-only verifier snapshot for a closed DOCX. H2 Core AiDocuments performs file
/// size/signature/ZIP/XML/macro safety checks before the package is inspected.
/// </summary>
public sealed class ClosedWordSnapshotReader
{
    public ClosedWordSnapshot Read(string name, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(bytes);

        var attachment = AiDocuments.Read(name, bytes);
        if (!string.Equals(
                attachment.MimeType,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                StringComparison.Ordinal))
            throw new InvalidDataException("Closed Word snapshot requires .docx input.");

        using var stream = new MemoryStream(bytes, writable: false);
        using var document = WordprocessingDocument.Open(stream, false, new OpenSettings
        {
            AutoSave = false,
            MaxCharactersInPart = 40 * 1024 * 1024
        });

        var main = document.MainDocumentPart
            ?? throw new InvalidDataException("Word main document part is missing.");
        var body = main.Document?.Body
            ?? throw new InvalidDataException("Word body is missing.");

        var bodyParagraphs = body.Elements<W.Paragraph>()
            .Select((paragraph, index) => SnapshotParagraph(paragraph, index))
            .ToArray();

        var tables = body.Elements<W.Table>()
            .Select((table, tableIndex) =>
                new ClosedWordTableSnapshot(
                    tableIndex,
                    table.Elements<W.TableRow>()
                        .Select((row, rowIndex) =>
                            new ClosedWordTableRowSnapshot(
                                rowIndex,
                                row.Elements<W.TableCell>()
                                    .Select((cell, cellIndex) =>
                                    {
                                        var paragraphs = cell.Elements<W.Paragraph>()
                                            .Select((paragraph, paragraphIndex) =>
                                                SnapshotParagraph(paragraph, paragraphIndex))
                                            .ToArray();
                                        return new ClosedWordTableCellSnapshot(
                                            cellIndex,
                                            string.Concat(paragraphs.Select(x => x.Text)),
                                            paragraphs);
                                    })
                                    .ToArray()))
                        .ToArray()))
            .ToArray();

        var sections = body.Descendants<W.SectionProperties>()
            .Select((section, index) => SnapshotSection(section, index))
            .ToArray();

        var headers = main.HeaderParts
            .Select(part => new ClosedWordPartSnapshot(
                main.GetIdOfPart(part),
                (part.Header?.Elements<W.Paragraph>() ?? Enumerable.Empty<W.Paragraph>())
                    .Select((paragraph, index) => SnapshotParagraph(paragraph, index))
                    .ToArray()))
            .OrderBy(x => x.RelationshipId, StringComparer.Ordinal)
            .ToArray();

        var footers = main.FooterParts
            .Select(part => new ClosedWordPartSnapshot(
                main.GetIdOfPart(part),
                (part.Footer?.Elements<W.Paragraph>() ?? Enumerable.Empty<W.Paragraph>())
                    .Select((paragraph, index) => SnapshotParagraph(paragraph, index))
                    .ToArray()))
            .OrderBy(x => x.RelationshipId, StringComparer.Ordinal)
            .ToArray();

        return new ClosedWordSnapshot(
            attachment.Sha256.ToLowerInvariant(),
            bodyParagraphs,
            tables,
            sections,
            headers,
            footers);
    }

    private static ClosedWordParagraphSnapshot SnapshotParagraph(W.Paragraph paragraph, int index)
    {
        var runs = paragraph.Elements<W.Run>()
            .Select((run, runIndex) => SnapshotRun(run, runIndex))
            .ToArray();
        return new ClosedWordParagraphSnapshot(
            index,
            string.Concat(runs.Select(x => x.Text)),
            paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value,
            runs);
    }

    private static ClosedWordRunSnapshot SnapshotRun(W.Run run, int index)
    {
        var properties = run.RunProperties;
        return new ClosedWordRunSnapshot(
            index,
            ReadRunText(run),
            properties?.RunStyle?.Val?.Value,
            On(properties?.Bold),
            On(properties?.Italic),
            Underlined(properties?.Underline));
    }

    private static string ReadRunText(W.Run run)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var child in run.ChildElements)
        {
            switch (child)
            {
                case W.Text text:
                    builder.Append(text.Text);
                    break;
                case W.TabChar:
                    builder.Append('\t');
                    break;
                case W.Break:
                case W.CarriageReturn:
                    builder.Append('\n');
                    break;
            }
        }
        return builder.ToString();
    }

    private static ClosedWordSectionSnapshot SnapshotSection(W.SectionProperties section, int index)
    {
        var size = section.GetFirstChild<W.PageSize>();
        var margins = section.GetFirstChild<W.PageMargin>();
        var headers = section.Elements<W.HeaderReference>()
            .Where(x => !string.IsNullOrWhiteSpace(x.Id?.Value))
            .Select(x => new ClosedWordHeaderFooterReferenceSnapshot(
                x.Id!.Value!,
                HeaderFooterType(x.Type?.Value)))
            .OrderBy(x => x.Type, StringComparer.Ordinal)
            .ThenBy(x => x.RelationshipId, StringComparer.Ordinal)
            .ToArray();
        var footers = section.Elements<W.FooterReference>()
            .Where(x => !string.IsNullOrWhiteSpace(x.Id?.Value))
            .Select(x => new ClosedWordHeaderFooterReferenceSnapshot(
                x.Id!.Value!,
                HeaderFooterType(x.Type?.Value)))
            .OrderBy(x => x.Type, StringComparer.Ordinal)
            .ThenBy(x => x.RelationshipId, StringComparer.Ordinal)
            .ToArray();

        return new ClosedWordSectionSnapshot(
            index,
            size?.Width?.Value,
            size?.Height?.Value,
            margins?.Top?.Value,
            margins?.Right?.Value,
            margins?.Bottom?.Value,
            margins?.Left?.Value,
            headers,
            footers);
    }

    private static bool On(W.OnOffType? value)
        => value is not null && (value.Val?.Value ?? true);

    private static bool Underlined(W.Underline? value)
        => value is not null
            && value.Val?.Value != W.UnderlineValues.None;

    private static string HeaderFooterType(W.HeaderFooterValues? value)
    {
        if (value == W.HeaderFooterValues.First) return "First";
        if (value == W.HeaderFooterValues.Even) return "Even";
        return "Default";
    }
}

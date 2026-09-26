using System.Text;
using System.Text.Json;
using H2AgentLab.Documents;
using H2Notes.Core;

namespace H2AgentLab;

public sealed record ScriptArtifactVerification(
    string RunId,
    string Path,
    string Sha256,
    string Format,
    bool SignatureVerified,
    bool StructureVerified,
    bool ContentVerified,
    bool FormulaValueClassified,
    bool LayoutVerified,
    int PrimaryItemCount,
    int SecondaryItemCount,
    string EvidenceId,
    DateTime VerifiedUtc,
    string Note)
{
    public bool RequiresFurtherVerification
        => !ContentVerified || (Format == "pdf" && !LayoutVerified);
}

public sealed record ScriptArtifactInspection(
    ScriptArtifactVerification Verification,
    int Characters,
    string Content,
    bool Truncated);

public sealed partial class ScriptWorkspace
{
    public ScriptArtifactInspection VerifyArtifact(string id, string path)
    {
        var run = Load(id);
        var data = Read(id, path);
        var artifact = run.Artifacts.Single(x => x.Path == path);
        var sha = SafeWorkspace.Hash(data);
        if (!string.Equals(sha, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Artifact bytes changed after the recorded run.");

        var ext = Path.GetExtension(path).ToLowerInvariant();
        string format;
        bool signature = true, structure = false, contentVerified = false, formulaValue = false, layout = false;
        int primary = 0, secondary = 0;
        string content = "";
        string note;

        if (ext == ".docx")
        {
            var attachment = AiDocuments.Read(path, data);
            var snapshot = new ClosedWordSnapshotReader().Read(path, data);
            format = "docx";
            structure = true;
            contentVerified = true;
            primary = snapshot.BodyParagraphs.Count
                + snapshot.Headers.Sum(x => x.Paragraphs.Count)
                + snapshot.Footers.Sum(x => x.Paragraphs.Count);
            secondary = snapshot.Tables.Count + snapshot.Sections.Count;
            content = attachment.Text;
            note = "Closed DOCX independently reopened with H2 Core safety checks and OpenXML structure reader. Text/tables/sections/header/footer structure classified; rendered layout is not certified.";
        }
        else if (ext == ".xlsx")
        {
            var attachment = AiDocuments.Read(path, data);
            var snapshot = new ClosedWorkbookSnapshotReader().Read(path, data);
            format = "xlsx";
            structure = true;
            contentVerified = true;
            formulaValue = true;
            primary = snapshot.Sheets.Sum(x => x.Cells.Count);
            secondary = snapshot.Sheets.Sum(x => x.Cells.Count(c => !string.IsNullOrWhiteSpace(c.Formula)));
            content = attachment.Text;
            note = "Closed XLSX independently reopened with H2 Core safety checks and OpenXML workbook reader. Stored values, formulas, styles, merges and sheet structure classified; formulas were not recalculated and rendered layout/charts are not certified.";
        }
        else if (ext == ".pdf")
        {
            _ = AiDocuments.Read(path, data); // exact signature/size policy only; no invented text/layout proof.
            format = "pdf";
            signature = true;
            structure = false;
            contentVerified = false;
            note = "PDF signature/size verified only. This host has no independent PDF content/layout parser in the standard runtime; extract/render with the configured PDF path before claiming content or layout verification.";
        }
        else if (SafeWorkspace.TextExtensions.Contains(ext))
        {
            using var reader = new StreamReader(new MemoryStream(data), new UTF8Encoding(false, true), true);
            content = reader.ReadToEnd();
            if (content.Contains('\0')) throw new InvalidDataException("Artifact is not valid text.");
            format = "text";
            structure = true;
            contentVerified = true;
            primary = content.Length;
            note = "Exact UTF text bytes reopened independently.";
        }
        else if (ext is ".png" or ".jpg" or ".jpeg")
        {
            _ = AiDocuments.Read(path, data);
            format = "image";
            contentVerified = false;
            note = "Image signature verified only; visual content is not certified until view/vision evidence exists.";
        }
        else
            throw new IOException("Artifact format is not supported for verification.");

        var receipt = new ScriptArtifactVerification(
            id, path, sha, format, signature, structure, contentVerified, formulaValue, layout,
            primary, secondary, ArtifactVerificationEvidenceId(id, path, sha), DateTime.UtcNow,
            note);
        SaveVerification(receipt);
        return new(receipt, content.Length,
            content[..Math.Min(16_000, content.Length)],
            content.Length > 16_000);
    }

    public ScriptArtifactVerification LoadVerification(string id, string path)
    {
        var data = Read(id, path);
        var sha = SafeWorkspace.Hash(data);
        var verificationPath = VerificationPath(id, path, sha);
        if (!File.Exists(verificationPath))
            throw new AgentFaultException("verification_required",
                "Artifact has not been independently inspected after its latest bytes. Run inspect_artifact first.", false);
        var receipt = JsonSerializer.Deserialize<ScriptArtifactVerification>(File.ReadAllBytes(verificationPath))
            ?? throw new IOException("Invalid artifact verification receipt.");
        if (receipt.RunId != id || receipt.Path != path
            || !string.Equals(receipt.Sha256, sha, StringComparison.OrdinalIgnoreCase)
            || receipt.VerifiedUtc.Kind != DateTimeKind.Utc)
            throw new IOException("Artifact verification receipt does not match current bytes.");
        return receipt;
    }

    private void SaveVerification(ScriptArtifactVerification receipt)
    {
        var path = VerificationPath(receipt.RunId, receipt.Path, receipt.Sha256);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ProjectWorkspaceStore.AtomicWrite(path,
            JsonSerializer.SerializeToUtf8Bytes(receipt, new JsonSerializerOptions { WriteIndented = true }));
    }

    private string VerificationPath(string runId, string path, string sha)
        => Path.Combine(RunsRoot, runId, "verification", StableSuffix(path, sha) + ".json");

    private static string ArtifactVerificationEvidenceId(string runId, string path, string sha)
        => "evidence:artifact-verification:" + runId + ":" + StableSuffix(path, sha);
}

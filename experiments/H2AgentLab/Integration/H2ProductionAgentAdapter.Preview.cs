using System.Security.Cryptography;
using System.Text.Json;
using H2Notes.Core;

namespace H2AgentLab.Integration;

public sealed partial class H2ProductionAgentAdapter
{
    private readonly SemaphoreSlim _previewGate = new(1, 1);

    public async Task<H2AgentPagePreview> PreviewPdfAsync(string evidenceId, int page, CancellationToken cancellationToken = default)
        => await PreviewDocumentPdfAsync(GetEvidence(evidenceId) ?? throw new FileNotFoundException("Không có bằng chứng cho tệp này."), page, cancellationToken);

    public async Task<H2AgentPagePreview> PreviewDocumentPdfAsync(H2AgentEvidence evidence, int page, CancellationToken cancellationToken = default)
    {
        if (page is < 0 or > 999) throw new ArgumentOutOfRangeException(nameof(page));
        var source = evidence.LocalPath ?? throw new FileNotFoundException("Bằng chứng không có tệp cục bộ.");
        if(!Path.IsPathFullyQualified(source))throw new InvalidDataException("Cần đường dẫn đầy đủ của tệp đã chọn.");
        if (!Path.GetExtension(source).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Cần tệp PDF.");
        if (new FileInfo(source).Length > 32 * 1024 * 1024) throw new IOException("PDF trên 32 MB · mở bằng ứng dụng trên máy.");
        var bytes = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (evidence.Sha256 is { Length: > 0 } expected && !hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Tệp đã thay đổi sau lúc ghi bằng chứng. Mở tệp gốc để xem bản hiện tại; không dùng bằng chứng cũ để xác nhận bản này.");
        var root = Path.Combine(_stateRoot, "previews", hash.ToLowerInvariant(), page.ToString());
        await _previewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var png = Path.Combine(root, "page.png"); var metadata = Path.Combine(root, "page.json");
            if (!File.Exists(png) || !File.Exists(metadata))
            {
                Directory.CreateDirectory(root); Directory.CreateDirectory(Path.Combine(root, "tmp"));
                await File.WriteAllBytesAsync(Path.Combine(root, "document.pdf"), bytes, cancellationToken).ConfigureAwait(false);
                File.Copy(Path.Combine(AppContext.BaseDirectory, "runtime", "worker.py"), Path.Combine(root, "worker.py"), true);
                var code = "import json\nimport pypdfium2 as pdfium\n" +
                    "doc = pdfium.PdfDocument('document.pdf')\n" +
                    "page = doc[" + page + "]\n" +
                    "scale = min(2, 1600 / max(page.get_size()))\n" +
                    "page.render(scale=scale).to_pil().save('page.png')\n" +
                    "text = page.get_textpage().get_text_range()\n" +
                    "with open('page.json', 'w', encoding='utf-8') as f: json.dump({'pages': len(doc), 'text': text[:100000]}, f, ensure_ascii=False)\n";
                await File.WriteAllTextAsync(Path.Combine(root, "task.py"), code, cancellationToken).ConfigureAwait(false);
                var exit = await WindowsPythonSandbox.Run(root, cancellationToken).ConfigureAwait(false);
                if (exit != 0 || !File.Exists(png)) throw new IOException("Không dựng được trang PDF trong bộ xem cục bộ.");
                File.Delete(Path.Combine(root, "document.pdf"));
            }
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(metadata, cancellationToken).ConfigureAwait(false));
            return new(await File.ReadAllBytesAsync(png, cancellationToken).ConfigureAwait(false),
                json.RootElement.GetProperty("pages").GetInt32(), json.RootElement.GetProperty("text").GetString() ?? "", hash);
        }
        finally { _previewGate.Release(); }
    }
}

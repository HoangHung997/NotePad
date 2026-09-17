namespace H2Notes.Core;

public enum AiPdfEngine { Direct, GotOcr, MinerU, Docling }

// Machine-local settings only; never put runtime paths into project attachments.
public sealed class AiPdfSettings
{
    public AiPdfEngine Engine { get; set; } = AiPdfEngine.Direct;
    public bool OcrImages { get; set; }
    public string RuntimeRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "H2Notes", "ocr-runtime");
    public int MaxPages { get; set; } = 100;
    public int TimeoutSeconds { get; set; } = 180;
}

public static class AiPdf
{
    public const int MaxPages = 100;
    public const int MaxTimeoutSeconds = 600;
    public const int MaxRequestPdfBytes = 8 * 1024 * 1024;
    // Keeps inline Gemini requests below its 20 MB request limit after base64 encoding.
    public const int MaxRequestBinaryBytes = 12 * 1024 * 1024;
    public const string OcrInstruction = "Model/endpoint này chưa được xác nhận nhận PDF gốc. Trong Thiết lập AI > PDF, chọn Docling, GOT-OCR2 hoặc MinerU đã cài và sẵn sàng để chuyển PDF sang Markdown; hoặc chọn model PDF trên API OpenAI/Gemini chính thức. Không tự bỏ PDF hoặc đổi nhà cung cấp.";

    public static void ValidateBytes(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > AiDocuments.MaxFileBytes)
            throw new InvalidDataException("Mỗi PDF tối đa 8 MB và không được rỗng.");
        if (!bytes.AsSpan().StartsWith("%PDF-"u8))
            throw new InvalidDataException("Nội dung tệp không có chữ ký PDF hợp lệ.");
    }

    public static void ValidateBudget(IReadOnlyList<AiTurn> turns)
    {
        var pdfBytes = 0L;
        foreach (var turn in turns)
            foreach (var file in turn.Files ?? [])
            {
                if (turn.Role != "user") throw new InvalidOperationException("PDF chỉ được đính kèm tin người dùng; không tự bỏ tệp ở vai trò khác.");
                if (file.MimeType != "application/pdf") throw new InvalidDataException("Tệp nhị phân gửi trực tiếp hiện chỉ hỗ trợ PDF và ảnh.");
                ValidateBytes(file.Data);
                if (string.IsNullOrWhiteSpace(file.Name) || file.Name.Length > 255 || file.Name.Any(char.IsControl))
                    throw new InvalidDataException("Tên PDF không hợp lệ.");
                pdfBytes += file.Data.Length;
            }
        if (pdfBytes > MaxRequestPdfBytes)
            throw new InvalidOperationException("Tổng PDF trong tin mới và lịch sử vượt 8 MB. Mở trao đổi mới hoặc chuyển PDF sang Markdown; app không tự bỏ tệp.");
        if (pdfBytes + turns.SelectMany(t => t.Images ?? []).Sum(i => (long)i.Data.Length) > MaxRequestBinaryBytes)
            throw new InvalidOperationException("Tổng PDF và ảnh vượt 12 MB. Giảm tệp hoặc mở trao đổi mới; app không tự cắt dữ liệu.");
    }

    internal static void ValidateRequest(AiProfile profile, IReadOnlyList<AiTurn> turns)
    {
        ValidateBudget(turns);
        if (turns.Any(t => t.Files is { Count: > 0 }) && !AiModelCapabilities.SupportsNativePdf(profile))
            throw new InvalidOperationException(OcrInstruction);
    }
}

using H2Notes.Core;

namespace H2Notes.Avalonia.Controls;

internal static class AiComposerInputData
{
    internal const long MaxProjectBytes = 32L * 1024 * 1024;

    internal static void CheckBitmapSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 40_000_000)
            throw new InvalidDataException("Ảnh clipboard tối đa 40 megapixel. Hãy chụp/cắt vùng nhỏ hơn trước khi dán.");
    }

    internal readonly record struct Mention(int Start, int Length, string Query);

    internal static Mention? FindMention(string? text, int caret, int selectionStart, int selectionEnd)
    {
        if (text is null || caret <= 0 || caret > text.Length || selectionStart != selectionEnd) return null;
        var start = caret - 1;
        while (start >= 0 && (char.IsLetterOrDigit(text[start]) || text[start] is '_' or '-')) start--;
        if (start < 0 || text[start] != '@' || start > 0 && !char.IsWhiteSpace(text[start - 1]) && "([{".IndexOf(text[start - 1]) < 0) return null;
        var end = caret;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '_' or '-')) end++;
        return new Mention(start, end - start, text[(start + 1)..caret]);
    }

    internal static string SearchKey(string value) => new string(value.Normalize(System.Text.NormalizationForm.FormD)
        .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
        .Select(c => c is 'đ' or 'Đ' ? 'd' : char.ToLowerInvariant(c)).ToArray());

    internal static string LocalPath(string value)
    {
        value = value.Trim().Trim('"');
        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsFile
                || !string.IsNullOrEmpty(uri.Host) && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new InvalidDataException("Chỉ nhận liên kết file:// trên máy, không đọc máy chủ từ xa.");
            value = uri.LocalPath;
        }
        if (!Path.IsPathFullyQualified(value) || value.StartsWith(@"\\") || value.StartsWith("//")
            || value.Contains("://", StringComparison.Ordinal))
            throw new InvalidDataException("Chỉ nhận đường dẫn tệp trên máy. Không tự tải URL hoặc đọc thư mục mạng.");
        return Path.GetFullPath(value);
    }

    internal static string[]? LocalPathsFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#')).ToArray();
        if (lines.Length == 0) return null;
        try { return lines.Select(LocalPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or NotSupportedException) { return null; }
    }

    internal static void CheckCount(int existing, int incoming)
    {
        if (incoming < 0 || existing + incoming > 4)
            throw new InvalidDataException("Tối đa 4 tệp trong một tin nhắn.");
    }

    internal static void CheckBudget(IEnumerable<AiConversation> conversations, AiConversation draft, IReadOnlyList<AiAttachment> incoming)
    {
        CheckCount(draft.DraftAttachments.Count, incoming.Count);
        if (incoming.Any(a => a.Data.Length == 0 || a.Data.Length > AiDocuments.MaxFileBytes))
            throw new InvalidDataException("Mỗi tệp phải có nội dung và không quá 8 MB.");
        var stored = conversations.Sum(c => c.DraftAttachments.Sum(a => (long)a.Data.Length)
            + c.Messages.Sum(m => m.Attachments.Sum(a => (long)a.Data.Length)));
        if (stored + incoming.Sum(a => (long)a.Data.Length) > MaxProjectBytes)
            throw new InvalidDataException("Bản sao đính kèm trong dự án vượt 32 MB. Không tự xóa hay cắt lịch sử.");
    }

    internal static AiAttachment ReadLocalFile(string path)
    {
        path = LocalPath(path);
        if (!AiDocuments.Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            throw new InvalidDataException("Chưa hỗ trợ loại tệp này. Chọn ảnh, .docx, .xlsx, TXT, MD, CSV hoặc JSON.");
        // Resolve only reparse points: Windows link resolution rejects ordinary drive roots.
        // Still inspect every ancestor so a local-looking directory link cannot read a remote file.
        for (FileSystemInfo? entry = new FileInfo(path); entry is not null;
             entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent)
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 && entry.ResolveLinkTarget(true) is { } target)
                LocalPath(target.FullName);
        using var input = File.OpenRead(path);
        if (input.Length == 0 || input.Length > AiDocuments.MaxFileBytes)
            throw new InvalidDataException("Tệp rỗng hoặc lớn hơn 8 MB.");
        using var output = new LimitedAttachmentStream();
        input.CopyTo(output);
        return AiDocuments.Read(Path.GetFileName(path), output.ToArray());
    }

    // Bound actual reads/PNG encoding too, not just the length observed before reading a changing file.
    internal sealed class LimitedAttachmentStream : MemoryStream
    {
        private void Check(int count)
        {
            if (Position + count > AiDocuments.MaxFileBytes)
                throw new InvalidDataException("Mỗi tệp tối đa 8 MB; hãy chọn ảnh/tệp nhỏ hơn.");
        }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
        public override void SetLength(long value)
        {
            if (value > AiDocuments.MaxFileBytes) throw new InvalidDataException("Mỗi tệp tối đa 8 MB.");
            base.SetLength(value);
        }
    }
}

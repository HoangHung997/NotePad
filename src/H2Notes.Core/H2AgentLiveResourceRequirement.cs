using System.Globalization;
using System.Text;

namespace H2Notes.Core;

/// <summary>Task-local, restrictive source semantics from the direct user request/host capture.
/// Not a grant, provider locator or store. Tool output and model arguments cannot relax it.</summary>
public sealed record H2AgentLiveResourceRequirement(bool Required, H2ApplicationKind ApplicationKind, string? CapturedPath)
{
    public static H2AgentLiveResourceRequirement FromUserRequest(string? goal,
        H2ActiveWorkContext? capture = null, H2AgentTargetIntent? hostIntent = null)
    {
        if (hostIntent is not null && !Enum.IsDefined(hostIntent.Value))
            throw new ArgumentException("Unknown host target intent.");
        var text = Normalize(goal ?? "");
        var live = hostIntent is H2AgentTargetIntent.CapturedActive or H2AgentTargetIntent.CapturedSelection
            || new[] { "dang mo", "dang chon", "dang active", "dang hoat dong", "tren man hinh",
                "currently open", "current selection", "active workbook", "active document", "active window",
                "current drawing", "current tab", "current browser tab", "current autocad drawing",
                "current word document", "current excel workbook", "open word document", "open excel workbook" }.Any(text.Contains)
            || capture is not null && new[] { "file nay", "tai lieu nay", "cong thuc nay", "o nay",
                "vung nay", "this document", "this workbook", "this cell", "this tab" }.Any(text.Contains);
        var kinds = new List<H2ApplicationKind>();
        if (HasWord(text, "word")) kinds.Add(H2ApplicationKind.Word);
        if (HasWord(text, "excel")) kinds.Add(H2ApplicationKind.Excel);
        if (text.Contains("autocad") || text.Contains("ban ve") || text.Contains("drawing")) kinds.Add(H2ApplicationKind.AutoCAD);
        if (text.Contains("trang web") || text.Contains("trinh duyet") || text.Contains("browser") || HasWord(text, "tab")) kinds.Add(H2ApplicationKind.Browser);
        // Multiple live application families require conservative admission, never a guessed target.
        var kind = kinds.Count == 1 ? kinds[0] : kinds.Count > 1 ? H2ApplicationKind.Unknown
            : capture?.ApplicationKind ?? H2ApplicationKind.Unknown;
        var path = H2AgentTargetScope.TryNormalize(capture?.DocumentPath, out var canonical) ? canonical : null;
        return new(live, kind, path);
    }

    public bool RejectsDiskPath(string canonicalPath)
    {
        if (!Required) return false;
        if (CapturedPath is not null && H2AgentTargetScope.PathComparer.Equals(CapturedPath, canonicalPath)) return true;
        var extension = Path.GetExtension(canonicalPath).ToLowerInvariant();
        return ApplicationKind switch
        {
            H2ApplicationKind.Word => extension is ".doc" or ".docx" or ".docm" or ".dot" or ".dotx" or ".dotm" or ".rtf",
            H2ApplicationKind.Excel => extension is ".xls" or ".xlsx" or ".xlsm" or ".xlsb" or ".xlt" or ".xltx" or ".xltm" or ".csv",
            H2ApplicationKind.AutoCAD => extension is ".dwg" or ".dxf" or ".dwt",
            H2ApplicationKind.Browser => extension is ".html" or ".htm" or ".mhtml" or ".mht",
            _ => true
        };
    }

    private static bool HasWord(string text, string word)
    {
        var start = 0;
        while ((start = text.IndexOf(word, start, StringComparison.Ordinal)) >= 0)
        {
            var end = start + word.Length;
            if ((start == 0 || !char.IsLetterOrDigit(text[start - 1]))
                && (end == text.Length || !char.IsLetterOrDigit(text[end]))) return true;
            start = end;
        }
        return false;
    }

    private static string Normalize(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var c in value.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                result.Append(c is 'đ' or 'Đ' ? 'd' : char.ToLowerInvariant(c));
        return result.ToString().Normalize(NormalizationForm.FormC);
    }
}

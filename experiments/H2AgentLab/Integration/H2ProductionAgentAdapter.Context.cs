using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using H2AgentLab.Office;
using H2Notes.Core;

namespace H2AgentLab.Integration;

public sealed partial class H2ProductionAgentAdapter
{
    private static H2AgentTaskContext? SnapshotContext(H2AgentTaskContext? context)
    {
        if (context is null) return null;
        var attachments = context.Attachments ?? [];
        var images = context.Images ?? [];
        var files = context.Files ?? [];
        if (attachments.Count > 24 || images.Count > 24 || files.Count > 24
            || attachments.Sum(x => (long)x.Data.Length) + images.Sum(x => (long)x.Data.Length)
                + files.Sum(x => (long)x.Data.Length) > 128 * 1024 * 1024
            || attachments.Any(x => x.Text.Length > AiDocuments.MaxTextCharacters))
            throw new ArgumentException("Tệp đính kèm vượt giới hạn của một lượt Agent.");
        return context with
        {
            TargetPaths = context.TargetPaths?.ToArray(),
            RecentTurns = context.RecentTurns?.TakeLast(32).Select(t => t with { Content = Bound(t.Content, 16000) }).ToArray(),
            Images = images.Select(i => i with { Data = i.Data.ToArray() }).ToArray(),
            Files = files.Select(f => f with { Data = f.Data.ToArray() }).ToArray(),
            Attachments = attachments.Select(a => new AiAttachment { Id = a.Id, Name = a.Name, MimeType = a.MimeType,
                Data = a.Data.ToArray(), Text = a.Text, Notice = a.Notice, Sha256 = a.Sha256,
                PdfEngine = a.PdfEngine, SourceName = a.SourceName, SourceSha256 = a.SourceSha256 }).ToArray()
        };
    }

    public H2ActiveWorkContextEnrichment? CaptureActiveWorkContext(H2ActiveWorkContext foregroundContext)
    {
        if (foregroundContext.ApplicationKind is not (H2ApplicationKind.Excel or H2ApplicationKind.Word)) return null;
        try
        {
            using var client = new OfficeHostClient(H2HelperLocator.Resolve("H2AgentLab.OfficeHost"),
                defaultTimeout: TimeSpan.FromSeconds(3));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var handle = foregroundContext.NativeWindowHandle.ToString(CultureInfo.InvariantCulture);
            if (foregroundContext.ApplicationKind == H2ApplicationKind.Excel)
            {
                var discovery = client.DiscoverExcelAsync(timeout.Token).GetAwaiter().GetResult();
                var selected = discovery.Workbooks.SingleOrDefault(item => item.SessionId == discovery.ActiveSessionId
                    && item.SessionId == OfficeSessionId("excel", handle, item.Name, item.FullName));
                return selected is null ? null : new(H2ApplicationKind.Excel, selected.SessionId,
                    selected.FullName, selected.ActiveSheet + "!" + selected.SelectionAddress, "office-host");
            }
            else
            {
                var discovery = client.DiscoverWordAsync(timeout.Token).GetAwaiter().GetResult();
                var selected = discovery.Documents.SingleOrDefault(item => item.SessionId == discovery.ActiveSessionId
                    && item.SessionId == OfficeSessionId("word", handle, item.Name, item.FullName));
                return selected is null ? null : new(H2ApplicationKind.Word, selected.SessionId,
                    selected.FullName, selected.SelectionText, "office-host");
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
        { return null; }
    }

    public bool RevalidateActiveWorkContext(H2ActiveWorkContext context)
    {
        if (context.Provider != "office-host") return string.IsNullOrEmpty(context.DocumentSessionId);
        var current = CaptureActiveWorkContext(context);
        return current is not null && current.DocumentSessionId == context.DocumentSessionId
            && string.Equals(current.DocumentPath, context.DocumentPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string OfficeSessionId(params string[] parts)
        => "office-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts)))).ToLowerInvariant()[..24];
}

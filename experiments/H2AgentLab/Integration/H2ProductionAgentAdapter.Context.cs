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

    private readonly object _captureGate = new();
    private IOfficeSessionClient? _captureClient;

    public H2ActiveWorkContextEnrichment? CaptureActiveWorkContext(H2ActiveWorkContext foregroundContext)
    {
        ArgumentNullException.ThrowIfNull(foregroundContext);
        if (foregroundContext.ApplicationKind is not (H2ApplicationKind.Excel or H2ApplicationKind.Word)) return null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        H2ActiveWorkContextEnrichment Fault(string code) => new(foregroundContext.ApplicationKind, Provider: "office-host")
            { Status="Unavailable", ErrorCode=code, ElapsedMilliseconds=clock.ElapsedMilliseconds };
        lock (_captureGate)
        {
            if (_disposed) return Fault("provider_unavailable");
            try
            {
                if (foregroundContext.ProcessId<=0 || foregroundContext.ProcessStartUtcTicks<=0 || foregroundContext.NativeWindowHandle<=0)
                    return Fault("stale_resource");
                // Reuse the owned helper connection, NOT stale capture results. Every request targets
                // the original HWND/PID/start, even when H2 has since become foreground.
                _captureClient ??= _officeClientFactory is not null ? _officeClientFactory()
                    : new OfficeHostClient(H2HelperLocator.Resolve("H2AgentLab.OfficeHost"));
                if (_captureClient is not IOfficeCaptureClient targeted) return Fault("unsupported_operation");
                using var deadline=new CancellationTokenSource(TimeSpan.FromMilliseconds(OfficeProtocol.OfficeDiscoveryLimits.CaptureDeadlineMilliseconds));
                var capture=targeted.CaptureAsync(new(foregroundContext.ApplicationKind==H2ApplicationKind.Excel?"excel":"word",
                    foregroundContext.NativeWindowHandle,foregroundContext.ProcessId,foregroundContext.ProcessStartUtcTicks),deadline.Token).GetAwaiter().GetResult();
                if(capture.Identity is { } identity && (identity.ProcessId!=foregroundContext.ProcessId
                    || identity.ProcessStartUtcTicks!=foregroundContext.ProcessStartUtcTicks || identity.RootWindowHandle!=foregroundContext.NativeWindowHandle))
                    return Fault("stale_resource");
                if (capture.Status is not ("Ready" or "Degraded") || capture.Identity is null || string.IsNullOrWhiteSpace(capture.SessionId))
                    return Fault(capture.Code ?? "native_object_unavailable");
                return new(foregroundContext.ApplicationKind,capture.SessionId,capture.FullName,capture.Selection,"office-host")
                    { NativeViewIdentity=capture.Identity.ViewIdentity,Status=capture.Status,ErrorCode=capture.Code,
                      ElapsedMilliseconds=clock.ElapsedMilliseconds };
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
            {
                var code=ex is OperationCanceledException or TimeoutException?"deadline_exceeded"
                    : ex is OfficeHostClientException office?office.Code:ex is FileNotFoundException?"needs_configuration":"provider_unavailable";
                _captureClient?.Dispose();_captureClient=null;
                return Fault(code); // retain window metadata + typed failure, never silently null
            }
        }
    }

    public bool RevalidateActiveWorkContext(H2ActiveWorkContext context)
    {
        if (context.Provider != "office-host") return string.IsNullOrEmpty(context.DocumentSessionId);
        var current = CaptureActiveWorkContext(context);
        return current is { Status: "Ready" or "Degraded" } && current.DocumentSessionId == context.DocumentSessionId
            && string.Equals(current.DocumentPath, context.DocumentPath, StringComparison.OrdinalIgnoreCase)
            && (context.NativeViewIdentity is null || context.NativeViewIdentity==current.NativeViewIdentity);
    }

    private void DisposeCaptureClient()
    { lock(_captureGate) { _captureClient?.Dispose(); _captureClient=null; } }
}

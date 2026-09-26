using System.Runtime.ExceptionServices;
using System.Text.Json;
using H2AgentLab.OfficeHost;
using H2AgentLab.OfficeProtocol;

/// <summary>E1/E2 boundary fixtures only. The top-level native enumerator is injected;
/// these tests do not start Office, read a native document, or establish E3 acceptance.</summary>
internal static class H2OfficeEnumerationFailureTests
{
    public static void Run(Action<string, Action> test)
    {
        foreach (var application in new[] { "excel", "word" })
        {
            test($"AR-020 enumeration failure never becomes a complete empty {application} catalog", () => OnSta(() =>
            {
                var calls = 0;
                var probe = new OfficeNativeWindowProbe(_ => { calls++; return false; });
                using var backend = new ComOfficeBackend(probe);
                var (report, count) = Discover(backend, application);
                Check(calls == 1 && count == 0, "Failure was retried or a document was invented.");
                Check(report is { Complete: false } && report.Issues.Count == 1
                    && report.Issues[0].Code == "native_object_unavailable",
                    "Failed EnumWindows was reported as a successful empty discovery.");
            }));

            test($"AR-020 successful empty {application} enumeration stays a complete empty catalog", () => OnSta(() =>
            {
                var calls = 0;
                using var backend = new ComOfficeBackend(new OfficeNativeWindowProbe(_ => { calls++; return true; }));
                var (report, count) = Discover(backend, application);
                Check(calls == 1 && count == 0 && report is { Complete: true }
                    && report.Issues.Count == 0,
                    "An actually successful empty enumeration became a provider failure.");
            }));
        }

        test("AR-020 enumeration failure keeps targeted capture unavailable without a guessed session", () => OnSta(() =>
        {
            var calls = 0;
            using var backend = new ComOfficeBackend(new OfficeNativeWindowProbe(_ => { calls++; return false; }));
            var result = backend.Capture(new("excel", 1001, 11, 101));
            Check(calls == 1 && result.Status == "Unavailable"
                && result.Code == "native_object_unavailable" && result.SessionId is null
                && result.Identity is null && result.Selection is null && result.FullName is null,
                "A failed root scan became a valid capture or lost its failure class.");
        }));

        test("AR-020 enumeration exception is typed without copying private error text", () => OnSta(() =>
        {
            const string sensitive = "TEST-PRIVATE-EXCEPTION-TEXT";
            using var backend = new ComOfficeBackend(new OfficeNativeWindowProbe(
                _ => throw new UnauthorizedAccessException(sensitive)));
            var (report, count) = Discover(backend, "word");
            Check(count == 0 && report is { Complete: false } && report.Issues.Count == 1
                && report.Issues[0].Code == "permission_denied",
                "Enumeration failure escaped the typed discovery boundary.");
            Check(!JsonSerializer.Serialize(report).Contains(sensitive, StringComparison.Ordinal),
                "Raw exception text leaked into discovery metadata.");
        }));

        test("AR-020 later explicit enumeration observes recovery without an automatic retry", () => OnSta(() =>
        {
            var calls = 0;
            using var backend = new ComOfficeBackend(new OfficeNativeWindowProbe(_ => ++calls != 1));
            var first = backend.DiscoverExcel();
            Check(calls == 1 && first.Report is { Complete: false },
                "A failed enumeration retried inside the same request or was called complete.");
            var second = backend.DiscoverExcel();
            Check(calls == 2 && second.Report is { Complete: true } && second.Workbooks.Count == 0
                && second.Report.Issues.Count == 0,
                "A later explicit discovery retained stale error state.");
        }));

        test("AR-020 unsupported application is rejected before native enumeration", () => OnSta(() =>
        {
            var calls = 0;
            var probe = new OfficeNativeWindowProbe(_ => { calls++; return true; });
            try
            {
                probe.Enumerate("powerpoint");
                throw new InvalidOperationException("Expected invalid_arguments before enumeration.");
            }
            catch (OfficeHostFaultException error)
            {
                Check(error.Code == "invalid_arguments" && error.NoEffect && calls == 0,
                    "An unsupported application was treated as a Word discovery.");
            }
        }));
    }

    private static (OfficeDiscoveryReport? Report, int Count) Discover(ComOfficeBackend backend, string application)
    {
        if (application == "excel")
        {
            var result = backend.DiscoverExcel();
            return (result.Report, result.Workbooks.Count);
        }
        var word = backend.DiscoverWord();
        return (word.Report, word.Documents.Count);
    }

    private static void OnSta(Action action)
    {
        // The real boundary must remain Windows/STA even when enumeration is injected.
        // A missing native platform is a failed environment prerequisite, never a skipped PASS.
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Run these Office boundary fixtures on Windows.");
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("Bounded enumeration fixture did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

using System.Reflection;
using System.Text.Json;
using Avalonia.Threading;
using H2Notes.Avalonia;
using H2Notes.Core;

internal static class H2ActiveWorkContextTests
{
    public static void Run(Action<string, Action> test)
    {
        test("ActiveWorkContext captures bounded foreground and optional structured provider context", () =>
        {
            var backend = new FakeBackend
            {
                Foreground = new WorkAssistantWindowSnapshot(
                    0x1234,
                    4242,
                    987654321,
                    "EXCEL",
                    new string('W', 700))
            };
            var provider = new FakeProvider
            {
                Enrichment = new H2ActiveWorkContextEnrichment(
                    ApplicationKind: null,
                    DocumentSessionId: new string('S', 300),
                    DocumentPath: @"C:\" + new string('D', 1_100),
                    Selection: new string('R', 1_400),
                    Provider: new string('P', 140))
            };
            var capture = new WorkAssistantActiveContextCapture(backend, () => provider);

            var context = capture.Capture()
                ?? throw new Exception("Foreground context was not captured.");

            Check(context.ProcessId == 4242
                && context.ProcessStartUtcTicks == 987654321
                && context.NativeWindowHandle == 0x1234,
                "Foreground process/window identity is wrong.");
            Check(context.ApplicationKind == H2ApplicationKind.Excel,
                "Excel process was not classified.");
            Check(context.WindowTitle.Length == 500,
                "Window title bound was not enforced.");
            Check(context.DocumentSessionId?.Length == 240,
                "Document session identity bound was not enforced.");
            Check(context.DocumentPath?.Length == 1_024,
                "Document path bound was not enforced.");
            Check(context.Selection?.Length == 1_200,
                "Selection bound was not enforced.");
            Check(context.Provider?.Length == 120,
                "Provider label bound was not enforced.");
            Check(context.WindowIdentity.Contains("1234:4242:987654321", StringComparison.Ordinal),
                "Stable foreground window identity is incomplete.");
            Check(context.ToBoundedSummary().Length <= 4_000,
                "ActiveWorkContext summary is unbounded.");
            Check(provider.CaptureCalls == 1,
                "Optional structured provider enrichment was not queried exactly once.");
        });

        test("ActiveWorkContext revalidation rejects stale window process or provider session", () =>
        {
            var snapshot = new WorkAssistantWindowSnapshot(
                0x99,
                77,
                123456,
                "WINWORD",
                "HoSoKhaoSat.docx - Word");
            var backend = new FakeBackend { Foreground = snapshot, Inspected = snapshot };
            var provider = new FakeProvider
            {
                Enrichment = new H2ActiveWorkContextEnrichment(
                    H2ApplicationKind.Word,
                    "word-session-77",
                    @"C:\HoSoKhaoSat.docx",
                    "paragraph:12-15",
                    "office.word"),
                RevalidateResult = true
            };
            var capture = new WorkAssistantActiveContextCapture(backend, () => provider);
            var context = capture.Capture()
                ?? throw new Exception("Context capture failed.");

            Check(capture.Revalidate(context),
                "Fresh target failed revalidation.");

            backend.Inspected = snapshot with { ProcessStartUtcTicks = 123457 };
            Check(!capture.Revalidate(context),
                "PID/window reuse with different process start was accepted.");

            backend.Inspected = snapshot;
            provider.RevalidateResult = false;
            Check(!capture.Revalidate(context),
                "Stale provider document/session was accepted.");
        });

        test("App captures ActiveWorkContext before compact assistant and clears stale target", () =>
        {
            var snapshot = new WorkAssistantWindowSnapshot(
                0x51,
                501,
                9001,
                "acad",
                "BanVe01.dwg - AutoCAD");
            var backend = new FakeBackend { Foreground = snapshot, Inspected = snapshot };
            var provider = new FakeProvider
            {
                Enrichment = new H2ActiveWorkContextEnrichment(
                    H2ApplicationKind.AutoCAD,
                    "acad-doc-501",
                    @"C:\CAD\BanVe01.dwg",
                    "14 selected entities",
                    "autocad.native"),
                RevalidateResult = true
            };
            var capture = new WorkAssistantActiveContextCapture(backend, () => provider);

            var app = new App();
            app.LocalSettings.WorkAssistant.Enabled = true;
            app.WorkAssistantContextCapture = capture;
            var before = JsonSerializer.Serialize(app.State);

            app.ShowWorkAssistantCompact();
            Pump();
            try
            {
                var current = app.CurrentWorkAssistantContext
                    ?? throw new Exception("App did not capture active context before opening compact assistant.");
                Check(current.ApplicationKind == H2ApplicationKind.AutoCAD
                    && current.DocumentSessionId == "acad-doc-501"
                    && current.Selection == "14 selected entities",
                    "Captured Work Assistant context lost provider grounding.");

                Check(app.TryGetValidatedWorkAssistantContext(out var validated)
                    && validated?.WindowIdentity == current.WindowIdentity,
                    "Fresh context did not pass App revalidation guard.");

                backend.Inspected = snapshot with { ProcessId = 777 };
                Check(!app.TryGetValidatedWorkAssistantContext(out var stale)
                    && stale is null
                    && app.CurrentWorkAssistantContext is null,
                    "Stale context was not cleared before later mutation use.");

                Check(before == JsonSerializer.Serialize(app.State),
                    "Active context capture mutated shared SheetState/project truth.");
            }
            finally
            {
                Compact(app)?.Close();
            }
        });

        test("ActiveWorkContext remains ephemeral and is not persisted as project truth", () =>
        {
            foreach (var type in new[]
            {
                typeof(SheetState),
                typeof(ProjectRecord),
                typeof(TaskRecord),
                typeof(ProjectLayout),
                typeof(LocalConfiguration),
                typeof(WorkAssistantSettings)
            })
            {
                foreach (var property in type.GetProperties())
                    Check(property.PropertyType != typeof(H2ActiveWorkContext)
                        && property.PropertyType != typeof(H2ActiveWorkContextEnrichment),
                        $"ActiveWorkContext leaked into persisted model {type.Name}.{property.Name}");
            }

            var shared = JsonSerializer.Serialize(new SheetState());
            var local = JsonSerializer.Serialize(new LocalConfiguration());
            foreach (var marker in new[]
            {
                "ActiveWorkContext", "DocumentSessionId", "NativeWindowHandle", "WindowIdentity"
            })
            {
                Check(!shared.Contains(marker, StringComparison.OrdinalIgnoreCase),
                    "ActiveWorkContext leaked into shared JSON: " + marker);
                Check(!local.Contains(marker, StringComparison.OrdinalIgnoreCase),
                    "ActiveWorkContext leaked into local persistent config: " + marker);
            }

            var repo = FindRepoRoot();
            var appSource = File.ReadAllText(Path.Combine(
                repo, "src", "H2Notes.Avalonia", "App.axaml.cs"));
            var captureIndex = appSource.IndexOf(
                "CurrentWorkAssistantContext = WorkAssistantContextCapture.Capture();",
                StringComparison.Ordinal);
            var setContextIndex = appSource.IndexOf(
                "compact.SetActiveContext(CurrentWorkAssistantContext);",
                StringComparison.Ordinal);
            var openIndex = appSource.IndexOf(
                "compact.OpenFromHotkey();",
                StringComparison.Ordinal);
            Check(captureIndex >= 0
                && setContextIndex > captureIndex
                && openIndex > setContextIndex,
                "Foreground context is not captured/projected before compact assistant takes focus.");
            Check(appSource.Contains(
                    "WorkAssistantContextCapture.Revalidate(context)",
                    StringComparison.Ordinal),
                "App has no explicit stale-target revalidation guard.");
        });
    }

    private static WorkAssistantCompactWindow? Compact(App app)
    {
        var field = typeof(App).GetField(
            "_workAssistantCompact",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(app) as WorkAssistantCompactWindow;
    }

    private static void Pump()
    {
        for (var i = 0; i < 6; i++)
            Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(current.FullName, "src")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed class FakeBackend : IWorkAssistantWindowContextBackend
    {
        public WorkAssistantWindowSnapshot? Foreground { get; set; }
        public WorkAssistantWindowSnapshot? Inspected { get; set; }

        public WorkAssistantWindowSnapshot? CaptureForeground() => Foreground;

        public WorkAssistantWindowSnapshot? InspectWindow(long nativeWindowHandle)
            => Inspected is { } value && value.NativeWindowHandle == nativeWindowHandle
                ? value
                : null;
    }

    private sealed class FakeProvider : IH2ActiveWorkContextProvider
    {
        public H2ActiveWorkContextEnrichment? Enrichment { get; set; }
        public bool RevalidateResult { get; set; } = true;
        public int CaptureCalls { get; private set; }
        public int RevalidateCalls { get; private set; }

        public H2ActiveWorkContextEnrichment? CaptureActiveWorkContext(H2ActiveWorkContext foregroundContext)
        {
            CaptureCalls++;
            return Enrichment;
        }

        public bool RevalidateActiveWorkContext(H2ActiveWorkContext context)
        {
            RevalidateCalls++;
            return RevalidateResult;
        }
    }
}

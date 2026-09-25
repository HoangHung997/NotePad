using H2AgentLab.DesktopProtocol;
using H2AgentLab.Transport;
using H2AgentLab.Tools;

namespace H2AgentLab.Desktop;

public static class V2DesktopHostTests
{
    public static async Task<int> Run(
        string outputDirectory,
        string hostExecutable)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new v2 DesktopHost test directory.");
        Directory.CreateDirectory(root);

        hostExecutable = Path.GetFullPath(hostExecutable);
        var lines = new List<string>();
        var failed = 0;

        async Task Test(string name, Func<Task> action)
        {
            try
            {
                await action();
                lines.Add("PASS " + name);
            }
            catch (Exception ex)
            {
                failed++;
                lines.Add("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        await Test("0901 DesktopHost is a separate STA process isolated from model UI and Python projects", async () =>
        {
            using var client = new DesktopHostClient(hostExecutable, fixtureMode: true);
            var ping = await client.PingAsync();

            Check(ping.StaThread, "DesktopHost RPC execution is not STA.");
            Check(ping.FixtureMode, "DesktopHost fixture mode was not reported.");
            Check(ping.ProcessId != Environment.ProcessId, "DesktopHost is not a separate process.");
            Check(ping.ProtocolVersion == DesktopProtocolConstants.Version, "Desktop protocol version mismatch.");
            Check(client.ProcessId == ping.ProcessId,
                "DesktopHost ping identity does not match the process started by the client.");
            DesktopHostClient.ValidatePreflightIdentity(client.ProcessId, ping);
            DesktopHostClient.ValidateLiveProcessIdentity(ping.ProcessId, client.ProcessId);

            foreach (var actual in new int?[] { null, ping.ProcessId + 1 })
            {
                try
                {
                    DesktopHostClient.ValidateLiveProcessIdentity(ping.ProcessId, actual);
                    throw new InvalidOperationException("Changed DesktopHost process identity was accepted after preflight.");
                }
                catch (DesktopHostClientException ex) when (ex.Code == "preflight_unavailable")
                {
                }
            }

            try
            {
                DesktopHostClient.ValidatePreflightIdentity(
                    ping.ProcessId + 1,
                    ping);
                throw new InvalidOperationException("Mismatched DesktopHost PID was accepted.");
            }
            catch (DesktopHostClientException ex) when (ex.Code == "preflight_unavailable")
            {
            }

            try
            {
                DesktopHostClient.ValidatePreflightIdentity(
                    ping.ProcessId,
                    ping with { ProtocolVersion = DesktopProtocolConstants.Version + "-stale" });
                throw new InvalidOperationException("Mismatched DesktopHost protocol was accepted.");
            }
            catch (DesktopHostClientException ex) when (ex.Code == "protocol_mismatch")
            {
            }

            var repo = FindRepoRoot();
            var project = File.ReadAllText(
                Path.Combine(repo, "experiments", "H2AgentLab.DesktopHost", "H2AgentLab.DesktopHost.csproj"));
            Check(project.Contains("H2AgentLab.DesktopProtocol", StringComparison.Ordinal),
                "DesktopHost does not reference isolated protocol project.");
            Check(!project.Contains("H2Notes.Core", StringComparison.Ordinal)
                && !project.Contains("../H2AgentLab/H2AgentLab.csproj", StringComparison.Ordinal)
                && !project.Contains("H2AgentLab.OfficeHost", StringComparison.Ordinal),
                "DesktopHost helper gained UI/model/Python/OfficeHost project coupling.");
        });

        await Test("0902 window enumeration exposes only policy-allowed fixture window", async () =>
        {
            using var client = new DesktopHostClient(hostExecutable, fixtureMode: true);
            var windows = await client.ListWindowsAsync();

            Check(windows.Count == 1, "Fixture desktop enumeration count is wrong.");
            var window = windows[0];
            Check(window.ProcessName == "H2DesktopFixture"
                && window.Title == "H2 Desktop Fixture Window",
                "Fixture safe window identity is wrong.");
            Check(window.SessionId == (await client.ListWindowsAsync())[0].SessionId,
                "Desktop window session ID is not stable.");
        });

        await Test("0903 observe returns bounded PNG bounds DPI foreground metadata and state identity", async () =>
        {
            using var client = new DesktopHostClient(hostExecutable, fixtureMode: true);
            var window = (await client.ListWindowsAsync()).Single();
            var observation = await client.ObserveAsync(window.SessionId);

            Check(observation.Window.Bounds.Width == 640
                && observation.Window.Bounds.Height == 480
                && observation.Window.Dpi == 96
                && observation.Window.Foreground,
                "Desktop observation lost bounds/DPI/foreground metadata.");
            Check(observation.ScreenshotPng.Length > 8
                && observation.ScreenshotPng.Length <= DesktopProtocolConstants.MaxScreenshotBytes
                && observation.ScreenshotPng.AsSpan().StartsWith(
                    new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                "Desktop screenshot is not a bounded PNG.");
            Check(observation.ScreenshotSha256.Length == 64
                && observation.StateId.Length == 64,
                "Desktop observation hash/state identity is invalid.");
        });

        await Test("0904 compact UIA tree emits short-lived state-bound element tokens", async () =>
        {
            using var client = new DesktopHostClient(hostExecutable, fixtureMode: true);
            var session = (await client.ListWindowsAsync()).Single().SessionId;
            var first = await client.ObserveAsync(session);

            Check(first.Elements.Count is > 0 and <= DesktopProtocolConstants.MaxElements,
                "Desktop UIA tree size is outside bounds.");
            Check(first.Elements.All(x => !string.IsNullOrWhiteSpace(x.Token)
                && x.Token.StartsWith("el-", StringComparison.Ordinal)),
                "Desktop element token is missing.");

            var oldToken = first.Elements.Single(x => x.Name == "Increment").Token;
            var second = await client.ObserveAsync(session);
            await ExpectCode(
                "stale_element",
                () => client.ActAsync(new DesktopActionRequest(
                    session,
                    second.StateId,
                    true,
                    DesktopActionKinds.Click,
                    ElementToken: oldToken)));
        });

        await Test("0905 click double-click key type scroll drag and wait actions operate only on observed state", async () =>
        {
            using var client = new DesktopHostClient(hostExecutable, fixtureMode: true);
            var session = (await client.ListWindowsAsync()).Single().SessionId;

            async Task<DesktopObservation> Observe()
                => await client.ObserveAsync(session);

            var observation = await Observe();
            var button = observation.Elements.Single(x => x.Name == "Increment");
            _ = await client.ActAsync(new DesktopActionRequest(
                session, observation.StateId, true, DesktopActionKinds.Click, ElementToken: button.Token));

            observation = await Observe();
            button = observation.Elements.Single(x => x.Name == "Increment");
            _ = await client.ActAsync(new DesktopActionRequest(
                session, observation.StateId, true, DesktopActionKinds.DoubleClick, ElementToken: button.Token));

            observation = await Observe();
            var textbox = observation.Elements.Single(x => x.Name == "Fixture text");
            _ = await client.ActAsync(new DesktopActionRequest(
                session, observation.StateId, true, DesktopActionKinds.Type, ElementToken: textbox.Token, Text: "typed"));

            observation = await Observe();
            _ = await client.ActAsync(new DesktopActionRequest(
                session, observation.StateId, true, DesktopActionKinds.Key, Key: "ENTER"));

            observation = await Observe();
            var scroll = observation.Elements.Single(x => x.Name == "Fixture scroll area");
            _ = await client.ActAsync(new DesktopActionRequest(
                session, observation.StateId, true, DesktopActionKinds.Scroll, ElementToken: scroll.Token, ScrollDelta: 120));

            observation = await Observe();
            var resize = observation.Elements.Single(x => x.Name == "Resize handle");
            _ = await client.ActAsync(new DesktopActionRequest(
                session,
                observation.StateId,
                true,
                DesktopActionKinds.Drag,
                ElementToken: resize.Token,
                EndX: observation.Window.Bounds.X + 700,
                EndY: observation.Window.Bounds.Y + 520));

            observation = await Observe();
            var wait = await client.ActAsync(new DesktopActionRequest(
                session,
                observation.StateId,
                false,
                DesktopActionKinds.Wait,
                WaitMilliseconds: 5));

            Check(!wait.Mutated && !wait.RequiresObservation, "Desktop wait was incorrectly treated as mutation.");
            Check(observation.Window.Bounds.Width == 700 && observation.Window.Bounds.Height == 520,
                "Desktop drag did not resize fixture state.");
            Check(observation.Elements.Single(x => x.Name == "Fixture text").Value == "typed",
                "Desktop type action did not persist observed text.");
        });

        await Test("0906 mutation cannot become final evidence until a newer observation confirms mutation_id", async () =>
        {
            using var client = new DesktopHostClient(hostExecutable, fixtureMode: true);
            var session = (await client.ListWindowsAsync()).Single().SessionId;
            var before = await client.ObserveAsync(session);
            var button = before.Elements.Single(x => x.Name == "Increment");
            var action = await client.ActAsync(new DesktopActionRequest(
                session,
                before.StateId,
                true,
                DesktopActionKinds.Click,
                ElementToken: button.Token));

            Check(action.Mutated && action.RequiresObservation && action.MutationId is not null,
                "Desktop mutation did not require post-action observation.");
            Check(!DesktopEvidenceGate.IsMutationVerifiedByNewObservation(action, before),
                "Pre-mutation observation was accepted as final mutation evidence.");

            var after = await client.ObserveAsync(session);
            DesktopEvidenceGate.EnsureMutationEvidence(action, after);
            Check(after.ObservedMutationId == action.MutationId,
                "New observation did not bind to prior mutation.");
        });

        await Test("0907 stale state and coordinates are rejected after resize", async () =>
        {
            using var client = new DesktopHostClient(hostExecutable, fixtureMode: true);
            var session = (await client.ListWindowsAsync()).Single().SessionId;
            var before = await client.ObserveAsync(session);
            var oldX = before.Window.Bounds.X + 40;
            var oldY = before.Window.Bounds.Y + 55;
            var resize = before.Elements.Single(x => x.Name == "Resize handle");

            _ = await client.ActAsync(new DesktopActionRequest(
                session,
                before.StateId,
                true,
                DesktopActionKinds.Drag,
                ElementToken: resize.Token,
                EndX: before.Window.Bounds.X + 720,
                EndY: before.Window.Bounds.Y + 540));

            await ExpectCode(
                "stale_state",
                () => client.ActAsync(new DesktopActionRequest(
                    session,
                    before.StateId,
                    true,
                    DesktopActionKinds.Click,
                    X: oldX,
                    Y: oldY)));

            var after = await client.ObserveAsync(session);
            Check(after.Window.Bounds.Width == 720
                && after.Window.Bounds.Height == 540
                && after.StateId != before.StateId,
                "Resize did not invalidate old coordinate state.");
        });

        await Test("0908 screenshot pixels are sent only to transports with native image input", async () =>
        {
            using var client = new DesktopHostClient(hostExecutable, fixtureMode: true);
            var session = (await client.ListWindowsAsync()).Single().SessionId;
            var observation = await client.ObserveAsync(session);

            var textOnly = DesktopVisionInputAdapter.Prepare(
                observation,
                AgentTransportCapabilities.Minimal);
            Check(!textOnly.PixelsIncluded
                && textOnly.Images.Count == 0
                && textOnly.Text.Contains("state_id=", StringComparison.Ordinal),
                "UIA fallback was not preserved for non-vision transport.");

            var vision = DesktopVisionInputAdapter.Prepare(
                observation,
                AgentTransportCapabilities.OpenAiResponsesHttp);
            Check(vision.PixelsIncluded
                && vision.Images.Count == 1
                && vision.Images[0].MimeType == "image/png"
                && vision.Images[0].Data.SequenceEqual(observation.ScreenshotPng),
                "Vision-capable transport did not receive screenshot pixels.");
        });

        await Test("0909 generic interaction fidelity prefers structured then accessibility then pixels", () =>
        {
            var candidates = new[]
            {
                new InteractionAdapterCandidate(
                    "fixture.pixel",
                    "active-content",
                    ToolInteractionFidelity.Visual),
                new InteractionAdapterCandidate(
                    "fixture.uia",
                    "active-content",
                    ToolInteractionFidelity.Accessibility),
                new InteractionAdapterCandidate(
                    "fixture.structured",
                    "active-content",
                    ToolInteractionFidelity.Structured)
            };

            Check(
                InteractionAdapterPreference.Choose(
                    "edit the active content",
                    candidates)?.AdapterId == "fixture.structured",
                "Structured adapter was not preferred over UIA/pixel adapters.");

            var withoutStructured = candidates
                .Where(x => x.AdapterId != "fixture.structured")
                .ToArray();
            Check(
                InteractionAdapterPreference.Choose(
                    "edit the active content",
                    withoutStructured)?.AdapterId == "fixture.uia",
                "Accessibility/UIA adapter was not preferred over pixel adapter.");

            Check(
                InteractionAdapterPreference.Choose(
                    "fixture.pixel",
                    candidates)?.AdapterId == "fixture.pixel",
                "Explicit adapter request did not select the requested pixel adapter.");
            return Task.CompletedTask;
        });

        await Test("0910 dedicated desktop fixture verifies observe-act-observe resize safety cancel and denial", async () =>
        {
            using var client = new DesktopHostClient(
                hostExecutable,
                fixtureMode: true,
                defaultTimeout: TimeSpan.FromSeconds(5));
            var session = (await client.ListWindowsAsync()).Single().SessionId;
            var before = await client.ObserveAsync(session);

            await ExpectCode(
                "permission_denied",
                () => client.ActAsync(new DesktopActionRequest(
                    session,
                    before.StateId,
                    false,
                    DesktopActionKinds.Click,
                    ElementToken: before.Elements.Single(x => x.Name == "Increment").Token)));

            var action = await client.ActAsync(new DesktopActionRequest(
                session,
                before.StateId,
                true,
                DesktopActionKinds.Click,
                ElementToken: before.Elements.Single(x => x.Name == "Increment").Token));
            var after = await client.ObserveAsync(session);
            DesktopEvidenceGate.EnsureMutationEvidence(action, after);

            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            try
            {
                _ = await client.ActAsync(
                    new DesktopActionRequest(
                        session,
                        after.StateId,
                        false,
                        DesktopActionKinds.Wait,
                        WaitMilliseconds: 2_000),
                    cancel.Token);
                throw new InvalidOperationException("Cancelled desktop wait unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
            }

            var restarted = await client.PingAsync();
            Check(client.StartCount >= 2 && restarted.StaThread,
                "DesktopHost did not recover after cancelled call.");
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "v2-desktop-host-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static async Task ExpectCode<T>(
        string expectedCode,
        Func<Task<T>> action)
    {
        try
        {
            _ = await action();
            throw new InvalidOperationException($"Expected DesktopHost error '{expectedCode}' was not raised.");
        }
        catch (DesktopHostClientException ex) when (ex.Code == expectedCode)
        {
        }
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(current.FullName, "experiments")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root for DesktopHost project boundary test.");
    }
}

using System.Diagnostics;
using System.Text.Json;
using H2AgentLab.Desktop;
using H2AgentLab.DesktopProtocol;
using H2AgentLab.Transport;

namespace H2AgentLab.Acceptance;

public static class MbDesktopComputerUseAcceptanceTests
{
    private sealed record AcceptanceCase(string Id, bool Passed, string? Failure);

    public static async Task<int> Run(string outputDirectory, string hostExecutable)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-112 acceptance directory.");
        Directory.CreateDirectory(root);
        hostExecutable = Path.GetFullPath(hostExecutable);

        var cases = new List<AcceptanceCase>();

        async Task Case(string id, Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
                cases.Add(new AcceptanceCase(id, true, null));
            }
            catch (Exception ex)
            {
                cases.Add(new AcceptanceCase(id, false, ex.GetType().Name + ": " + ex.Message));
            }
        }

        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        DesktopHostClient Client() => new(
            hostExecutable,
            fixtureMode: true,
            defaultTimeout: TimeSpan.FromSeconds(5));

        await Case("safe-window-enumeration", async () =>
        {
            using var client = Client();
            var windows = await client.ListWindowsAsync().ConfigureAwait(false);
            Check(windows.Count == 1, "Expected one safe fixture window.");
            var window = windows.Single();
            Check(window.ProcessName == "H2DesktopFixture"
                && window.Title == "H2 Desktop Fixture Window"
                && !string.IsNullOrWhiteSpace(window.SessionId),
                "Safe window identity/session was not preserved.");
        }).ConfigureAwait(false);

        await Case("uia", async () =>
        {
            using var client = Client();
            var session = (await client.ListWindowsAsync().ConfigureAwait(false)).Single().SessionId;
            var observed = await client.ObserveAsync(session).ConfigureAwait(false);
            Check(observed.Elements.Count is > 0 and <= DesktopProtocolConstants.MaxElements,
                "UIA observation is not bounded.");
            Check(observed.Elements.All(x =>
                    x.Token.StartsWith("el-", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(x.ControlType)),
                "UIA tokens/types are invalid.");
        }).ConfigureAwait(false);

        await Case("typed-actions", async () =>
        {
            using var client = Client();
            var session = (await client.ListWindowsAsync().ConfigureAwait(false)).Single().SessionId;

            var observed = await client.ObserveAsync(session).ConfigureAwait(false);
            var button = observed.Elements.Single(x => x.Name == "Increment");
            var click = await client.ActAsync(new DesktopActionRequest(
                session,
                observed.StateId,
                true,
                DesktopActionKinds.Click,
                ElementToken: button.Token)).ConfigureAwait(false);
            Check(click.Mutated && click.RequiresObservation, "Typed click was not a mutation.");

            observed = await client.ObserveAsync(session).ConfigureAwait(false);
            var textbox = observed.Elements.Single(x => x.Name == "Fixture text");
            var type = await client.ActAsync(new DesktopActionRequest(
                session,
                observed.StateId,
                true,
                DesktopActionKinds.Type,
                ElementToken: textbox.Token,
                Text: "MB112")).ConfigureAwait(false);
            Check(type.Mutated && type.RequiresObservation, "Typed text action was not a mutation.");

            var after = await client.ObserveAsync(session).ConfigureAwait(false);
            Check(after.Elements.Any(x => x.Name == "Fixture text" && x.Value == "MB112"),
                "Typed text was not observable after action.");
        }).ConfigureAwait(false);

        await Case("stale-state-guard", async () =>
        {
            using var client = Client();
            var session = (await client.ListWindowsAsync().ConfigureAwait(false)).Single().SessionId;
            var first = await client.ObserveAsync(session).ConfigureAwait(false);
            var oldToken = first.Elements.Single(x => x.Name == "Increment").Token;
            var second = await client.ObserveAsync(session).ConfigureAwait(false);

            await ExpectCode(
                "stale_element",
                () => client.ActAsync(new DesktopActionRequest(
                    session,
                    second.StateId,
                    true,
                    DesktopActionKinds.Click,
                    ElementToken: oldToken))).ConfigureAwait(false);

            var button = second.Elements.Single(x => x.Name == "Increment");
            _ = await client.ActAsync(new DesktopActionRequest(
                session,
                second.StateId,
                true,
                DesktopActionKinds.Click,
                ElementToken: button.Token)).ConfigureAwait(false);

            await ExpectCode(
                "stale_state",
                () => client.ActAsync(new DesktopActionRequest(
                    session,
                    second.StateId,
                    true,
                    DesktopActionKinds.Click,
                    ElementToken: button.Token))).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Case("observe-after-act", async () =>
        {
            using var client = Client();
            var session = (await client.ListWindowsAsync().ConfigureAwait(false)).Single().SessionId;
            var before = await client.ObserveAsync(session).ConfigureAwait(false);
            var button = before.Elements.Single(x => x.Name == "Increment");
            var action = await client.ActAsync(new DesktopActionRequest(
                session,
                before.StateId,
                true,
                DesktopActionKinds.Click,
                ElementToken: button.Token)).ConfigureAwait(false);

            Check(!DesktopEvidenceGate.IsMutationVerifiedByNewObservation(action, before),
                "Pre-action observation was accepted as mutation evidence.");
            var after = await client.ObserveAsync(session).ConfigureAwait(false);
            DesktopEvidenceGate.EnsureMutationEvidence(action, after);
        }).ConfigureAwait(false);

        await Case("vision-fallback", async () =>
        {
            using var client = Client();
            var session = (await client.ListWindowsAsync().ConfigureAwait(false)).Single().SessionId;
            var observed = await client.ObserveAsync(session).ConfigureAwait(false);

            var textOnly = DesktopVisionInputAdapter.Prepare(observed, AgentTransportCapabilities.Minimal);
            Check(!textOnly.PixelsIncluded
                && textOnly.Images.Count == 0
                && textOnly.Text.Contains("state_id=", StringComparison.Ordinal),
                "Text-only transport lost UIA fallback.");

            var vision = DesktopVisionInputAdapter.Prepare(
                observed,
                AgentTransportCapabilities.OpenAiResponsesHttp);
            Check(vision.PixelsIncluded
                && vision.Images.Count == 1
                && vision.Images[0].Data.SequenceEqual(observed.ScreenshotPng),
                "Vision-capable transport did not receive observed pixels.");
        }).ConfigureAwait(false);

        await Case("application-launch-wait-activate", async () =>
        {
            using var client = Client();
            var before = await client.ListWindowsAsync().ConfigureAwait(false);
            Check(before.Count == 1, "Fixture application lifecycle did not start from one known window.");

            var launched = await client.LaunchApplicationAsync(
                new DesktopApplicationLaunchRequest("notepad", true, 1000)).ConfigureAwait(false);
            Check(launched.NewWindowObserved
                && !launched.ReusedExistingWindow
                && launched.ProcessName == "notepad"
                && !string.IsNullOrWhiteSpace(launched.Window.SessionId),
                "Fixture launch did not return a newly observed application window.");

            var waited = await client.WaitForApplicationWindowAsync(
                new DesktopApplicationWaitRequest("notepad", 1000)).ConfigureAwait(false);
            Check(waited.SessionId == launched.Window.SessionId,
                "Wait-for-window redirected to another application session.");

            var activated = await client.ActivateWindowAsync(
                new DesktopApplicationActivateRequest(launched.Window.SessionId, true)).ConfigureAwait(false);
            Check(activated.SessionId == launched.Window.SessionId && activated.Foreground,
                "Activate did not verify the exact observed window as foreground.");
        }).ConfigureAwait(false);

        await Case("application-launch-guards", async () =>
        {
            using var client = Client();
            await ExpectCode(
                "permission_denied",
                () => client.LaunchApplicationAsync(
                    new DesktopApplicationLaunchRequest("notepad", false, 1000))).ConfigureAwait(false);
            await ExpectCode(
                "invalid_application",
                () => client.LaunchApplicationAsync(
                    new DesktopApplicationLaunchRequest(@"C:\Windows\notepad.exe", true, 1000))).ConfigureAwait(false);
            await ExpectCode(
                "session_not_found",
                () => client.ActivateWindowAsync(
                    new DesktopApplicationActivateRequest("not-observed", true))).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Case("sensitive-app-blocks", async () =>
        {
            var policyDir = Path.Combine(root, "desktop-host-policy");
            var start = new ProcessStartInfo(hostExecutable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("--self-test");
            start.ArgumentList.Add(policyDir);

            using var process = Process.Start(start)
                ?? throw new IOException("Could not start DesktopHost self-test.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            Check(process.ExitCode == 0, "DesktopHost safety self-test failed.");

            var report = File.ReadAllText(Path.Combine(policyDir, "desktop-host-self-tests.txt"));
            Check(report.Contains("RESULT: 4 passed, 0 failed.", StringComparison.Ordinal),
                "DesktopHost sensitive-app safety suite is not fully green.");
        }).ConfigureAwait(false);

        var passed = cases.Count(x => x.Passed);
        var failed = cases.Count - passed;
        var gatePassed = failed == 0;

        var lines = cases.Select(x =>
                (x.Passed ? "PASS " : "FAIL ") + x.Id
                + (x.Failure is null ? "" : " | " + x.Failure))
            .ToList();
        lines.Add($"RESULT: {passed} passed, {failed} failed.");
        lines.Add($"GATE: {(gatePassed ? "PASS" : "FAIL")} MB-112 Desktop/computer-use acceptance.");

        await File.WriteAllLinesAsync(
            Path.Combine(root, "mb-desktop-computer-use-acceptance-tests.txt"),
            lines).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(root, "mb-desktop-computer-use-acceptance-tests.json"),
            JsonSerializer.Serialize(
                new
                {
                    gate = "MB-112",
                    passed = gatePassed,
                    passedCases = passed,
                    failedCases = failed,
                    cases
                },
                new JsonSerializerOptions { WriteIndented = true }))
            .ConfigureAwait(false);

        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return gatePassed ? 0 : 1;
    }

    private static async Task ExpectCode<T>(string code, Func<Task<T>> action)
    {
        try
        {
            _ = await action().ConfigureAwait(false);
            throw new InvalidOperationException("Expected DesktopHost error '" + code + "'.");
        }
        catch (DesktopHostClientException ex) when (ex.Code == code)
        {
        }
    }
}

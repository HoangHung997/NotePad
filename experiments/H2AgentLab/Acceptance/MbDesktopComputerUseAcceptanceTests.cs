using System.Diagnostics;
using System.Text.Json;
using H2AgentLab.Desktop;
using H2AgentLab.DesktopProtocol;
using H2AgentLab.Integration;
using H2AgentLab.Office;
using H2AgentLab.OfficeProtocol;
using H2AgentLab.Runtime;
using H2AgentLab.Tools;
using H2AgentLab.Transport;
using H2Notes.Core;

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

            var reused = await client.LaunchApplicationAsync(
                new DesktopApplicationLaunchRequest("notepad", true, 1000)).ConfigureAwait(false);
            Check(!reused.NewWindowObserved
                && reused.ReusedExistingWindow
                && reused.Window.SessionId == launched.Window.SessionId,
                "Second single-instance launch did not verify reuse of the exact existing safe window.");

            var activated = await client.ActivateWindowAsync(
                new DesktopApplicationActivateRequest(launched.Window.SessionId, true)).ConfigureAwait(false);
            Check(activated.SessionId == launched.Window.SessionId && activated.Foreground,
                "Activate did not verify the exact observed window as foreground.");
        }).ConfigureAwait(false);

        await Case("application-new-window-mode-never-reuses-existing", async () =>
        {
            using var client = Client();
            var first = await client.LaunchApplicationAsync(
                new DesktopApplicationLaunchRequest("notepad", true, 1000)).ConfigureAwait(false);
            var second = await client.LaunchApplicationAsync(
                new DesktopApplicationLaunchRequest(
                    "notepad",
                    true,
                    1000,
                    RequireNewWindow: true)).ConfigureAwait(false);
            Check(second.NewWindowObserved
                && !second.ReusedExistingWindow
                && second.Window.SessionId != first.Window.SessionId,
                "new_window mode reused the existing application window.");
        }).ConfigureAwait(false);

        await Case("application-lifecycle-verifier", async () =>
        {
            var verifier = new H2DesktopRuntimeVerifier();
            var launch = new ToolCall(
                "launch",
                "launch_app",
                JsonSerializer.SerializeToElement(new { application = "notepad", mode = "new_window" }));
            var good = JsonSerializer.Serialize(new
            {
                requestedApplication = "notepad",
                requestedMode = "new_window",
                application = "notepad",
                process = "notepad",
                newWindowObserved = true,
                reusedExistingWindow = false,
                window = new
                {
                    session_id = "win-1",
                    hwnd = 1001L,
                    pid = 22,
                    process_started_utc_ticks = 33L,
                    process = "notepad",
                    foreground = true
                },
                verifiedByHostObservation = true
            });
            var report = await verifier.VerifyAsync(
                null!,
                launch,
                good,
                CancellationToken.None).ConfigureAwait(false);
            Check(report.Passed
                && report.EvidenceIds.Single() == "desktop-window:win-1",
                "Host-observed app launch was not verified.");

            var weak = JsonSerializer.Serialize(new
            {
                requestedApplication = "notepad",
                requestedMode = "new_window",
                application = "notepad",
                process = "notepad",
                newWindowObserved = false,
                reusedExistingWindow = true,
                window = new { session_id = "win-1", pid = 22 },
                verifiedByHostObservation = true
            });
            Check(!((await verifier.VerifyAsync(
                        null!,
                        launch,
                        weak,
                        CancellationToken.None).ConfigureAwait(false)).Passed),
                "Unobserved/semantically weak app launch was incorrectly verified.");

            var wrongRequested = JsonSerializer.Serialize(new
            {
                requestedApplication = "excel",
                requestedMode = "new_window",
                application = "notepad",
                process = "notepad",
                newWindowObserved = true,
                reusedExistingWindow = false,
                window = new
                {
                    session_id = "win-1",
                    hwnd = 1001L,
                    pid = 22,
                    process_started_utc_ticks = 33L,
                    process = "notepad",
                    title = "Untitled",
                    foreground = true,
                    dpi = 96
                },
                verifiedByHostObservation = true
            });
            Check(!((await verifier.VerifyAsync(
                        null!,
                        launch,
                        wrongRequested,
                        CancellationToken.None).ConfigureAwait(false)).Passed),
                "launch_app verified a result for a different requested application.");

            var activate = new ToolCall(
                "activate",
                "activate_app",
                JsonSerializer.SerializeToElement(new { session_id = "expected-window" }));
            var wrongWindow = JsonSerializer.Serialize(new
            {
                window = new
                {
                    session_id = "different-window",
                    hwnd = 1002L,
                    pid = 23,
                    process_started_utc_ticks = 34L,
                    process = "notepad",
                    title = "Untitled",
                    foreground = true,
                    dpi = 96
                },
                activated = true,
                verifiedByHostObservation = true
            });
            Check(!((await verifier.VerifyAsync(
                        null!,
                        activate,
                        wrongWindow,
                        CancellationToken.None).ConfigureAwait(false)).Passed),
                "activate_app verified a different session than the exact requested window.");
        }).ConfigureAwait(false);

        await Case("application-wait-fails-closed-on-ambiguity", async () =>
        {
            using var client = Client();
            _ = await client.LaunchApplicationAsync(
                new DesktopApplicationLaunchRequest("word", true, 1000)).ConfigureAwait(false);
            _ = await client.LaunchApplicationAsync(
                new DesktopApplicationLaunchRequest(
                    "word",
                    true,
                    1000,
                    RequireNewWindow: true)).ConfigureAwait(false);
            await ExpectCode(
                "ambiguous_target",
                () => client.WaitForApplicationWindowAsync(
                    new DesktopApplicationWaitRequest("word", 1000))).ConfigureAwait(false);
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

        await Case("application-permission-scope-does-not-widen-machine-authority", async () =>
        {
            var workspace = Path.Combine(root, "app-permission-workspace");
            var state = Path.Combine(root, "app-permission-state");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(state);

            using var tools = new global::H2AgentLab.AgentTools(
                new global::H2AgentLab.SafeWorkspace(workspace),
                state,
                (_, _) => Task.FromResult(true),
                (_, _) => { })
            {
                ReadOnly = false
            };
            var registry = NormalRuntimeToolRegistry.Create(tools);
            Check(registry.TryGet("launch_app", out var launch),
                "launch_app is missing from the normal runtime registry.");
            var call = new ToolCall(
                "permission-launch",
                "launch_app",
                JsonSerializer.SerializeToElement(new
                {
                    application = "Word",
                    mode = "reuse_or_launch"
                }));

            async Task<AgentRuntimePermissionDecision> Authorize(
                H2AgentPermissionScope scope,
                Guid? projectId)
            {
                var context = new H2AgentTaskContext(
                    WorkspaceRoot: workspace,
                    Summary: "AR-061 permission scope fixture",
                    PermissionScope: scope);
                using var session = new H2ProductionToolSession(
                    Guid.NewGuid(),
                    projectId,
                    readOnly: false,
                    context,
                    projects: null,
                    approve: (_, _, _) => Task.FromResult(true));
                return await session.AuthorizeAsync(
                    new AgentRuntimePermissionRequest(
                        null!,
                        launch,
                        call,
                        null),
                    CancellationToken.None).ConfigureAwait(false);
            }

            var now = DateTime.UtcNow;
            var workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
            var workspaceAsk = new H2AgentPermissionScope(
                H2AgentPermissionMode.AskBeforeChanges,
                H2AgentResourceScopeKind.Workspace,
                "workspace:" + workspaceRoot,
                mutationAllowed: true,
                approvalRequired: true,
                now,
                now.AddMinutes(10),
                documentPath: workspaceRoot);
            Check((await Authorize(workspaceAsk, null).ConfigureAwait(false)).Allowed,
                "Workspace AskBeforeChanges did not authorize explicit app launch.");

            var documentAsk = new H2AgentPermissionScope(
                H2AgentPermissionMode.AskBeforeChanges,
                H2AgentResourceScopeKind.Document,
                "document:fixture",
                mutationAllowed: true,
                approvalRequired: true,
                now,
                now.AddMinutes(10),
                applicationKind: H2ApplicationKind.Word,
                documentSessionId: "word-session",
                documentPath: Path.Combine(workspace, "doc.docx"));
            Check((await Authorize(documentAsk, null).ConfigureAwait(false)).Allowed,
                "AskBeforeChanges did not admit exact per-call app approval from a document context.");

            var documentAuto = new H2AgentPermissionScope(
                H2AgentPermissionMode.AllowScopedChanges,
                H2AgentResourceScopeKind.Document,
                "document:auto-fixture",
                mutationAllowed: true,
                approvalRequired: false,
                now,
                now.AddMinutes(10),
                applicationKind: H2ApplicationKind.Word,
                documentSessionId: "word-auto-session",
                documentPath: Path.Combine(workspace, "auto.docx"));
            Check(!(await Authorize(documentAuto, null).ConfigureAwait(false)).Allowed,
                "AllowScopedChanges silently widened document authority into machine app launch.");

            var project = Guid.NewGuid();
            var projectPolicy = new H2AgentPermissionScope(
                H2AgentPermissionMode.UseProjectPolicy,
                H2AgentResourceScopeKind.Project,
                "project:" + project.ToString("N"),
                mutationAllowed: true,
                approvalRequired: true,
                now,
                now.AddMinutes(10));
            Check((await Authorize(projectPolicy, project).ConfigureAwait(false)).Allowed,
                "Exact project policy did not authorize explicit app launch.");

            var wrongProject = Guid.NewGuid();
            Check(!(await Authorize(projectPolicy, wrongProject).ConfigureAwait(false)).Allowed,
                "A different project inherited app launch authority from another project policy.");

            var fullAccess = new H2AgentPermissionScope(
                H2AgentPermissionMode.FullAccess,
                H2AgentResourceScopeKind.Machine,
                H2AgentPermissionScope.CurrentMachineResourceKey,
                mutationAllowed: true,
                approvalRequired: false,
                now,
                now.AddMinutes(10));
            Check((await Authorize(fullAccess, null).ConfigureAwait(false)).Allowed,
                "Current-machine FullAccess did not authorize app launch.");
        }).ConfigureAwait(false);

        await Case("task-launched-excel-binds-and-writes-through-production-office-runtime", async () =>
        {
            var workspace = Path.Combine(root, "launched-office-workspace");
            var state = Path.Combine(root, "launched-office-state");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(state);

            var now = DateTime.UtcNow;
            var scope = new H2AgentPermissionScope(
                H2AgentPermissionMode.AskBeforeChanges,
                H2AgentResourceScopeKind.Workspace,
                "workspace:" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)),
                mutationAllowed: true,
                approvalRequired: true,
                issuedUtc: now,
                expiresUtc: now.AddMinutes(30),
                documentPath: workspace);
            var context = new H2AgentTaskContext(
                workspace,
                "",
                PermissionScope: scope,
                TargetIntent: H2AgentTargetIntent.OpenDocument);
            var binding = new H2AgentTargetBindingPolicy(null, workspace);
            using var fakeOffice = new TaskLaunchedExcelClient();
            using var session = new H2ProductionToolSession(
                Guid.NewGuid(),
                null,
                readOnly: false,
                context,
                projects: null,
                approve: (_, _, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(true);
                },
                targetPolicy: binding,
                officeClientFactory: () => fakeOffice,
                userGoal: "Mở Excel trắng mới rồi điền Xin chào vào A1");
            using var tools = new global::H2AgentLab.AgentTools(
                new global::H2AgentLab.SafeWorkspace(workspace),
                state,
                (_, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(true);
                },
                (_, _) => { })
            {
                ReadOnly = false,
                ProductionSession = session
            };

            var registry = NormalRuntimeToolRegistry.Create(tools);
            var verifiers = new List<IAgentRuntimeDomainVerifier>();
            registry = session.Configure(tools, registry, verifiers);
            Check(registry.TryGet("excel.get_active_workbook", out var getWorkbook)
                && registry.TryGet("excel.write_range", out var writeRange),
                "Workspace AskBeforeChanges did not expose launched-window Office tools.");

            var launchedWindow = new DesktopWindowInfo(
                "desktop-launched-excel",
                0x12345,
                7001,
                638943000000000000L,
                "EXCEL",
                "Book1 - Excel",
                new DesktopBounds(100, 100, 900, 700),
                96,
                true);
            var launched = new DesktopApplicationLaunchResult(
                "Excel",
                "excel",
                "EXCEL",
                true,
                false,
                launchedWindow);
            session.ObserveLaunchedApplicationWindow(launched);
            fakeOffice.Bind(launchedWindow);

            var getOutput = await getWorkbook.Executor.ExecuteAsync(
                new ToolCall("get-launched-excel", "excel.get_active_workbook",
                    JsonSerializer.SerializeToElement(new { })),
                CancellationToken.None).ConfigureAwait(false);
            using (var parsed = JsonDocument.Parse(getOutput))
            {
                Check(parsed.RootElement.GetProperty("SessionId").GetString() == TaskLaunchedExcelClient.SessionId,
                    "Launched Excel window did not bind its exact Office session.");
                Check(parsed.RootElement.GetProperty("StateToken").GetString() == "state-1",
                    "Launched Excel snapshot did not provide the expected state token.");
            }

            var writeCall = new ToolCall(
                "write-launched-excel",
                "excel.write_range",
                JsonSerializer.SerializeToElement(new
                {
                    session_id = TaskLaunchedExcelClient.SessionId,
                    state_token = "state-1",
                    sheet_name = "Sheet1",
                    cells = new[] { new { address = "A1", value = "Xin chào" } }
                }));
            var writeOutput = await writeRange.Executor.ExecuteAsync(
                writeCall,
                CancellationToken.None).ConfigureAwait(false);
            using (var parsed = JsonDocument.Parse(writeOutput))
            {
                Check(!parsed.RootElement.TryGetProperty("success", out var success)
                        || success.ValueKind != JsonValueKind.False,
                    "Launched Excel write returned a failure payload: " + writeOutput);
            }
            var after = await fakeOffice.SnapshotExcelAsync(
                TaskLaunchedExcelClient.SessionId,
                CancellationToken.None).ConfigureAwait(false);
            Check(after.Sheets.Single().Cells.Single(cell => cell.Address == "A1").Value == "Xin chào"
                && after.StateToken == "state-2",
                "Launched Excel write did not survive Office readback.");

            var wrongCall = writeCall with
            {
                Id = "wrong-launched-session",
                Arguments = JsonSerializer.SerializeToElement(new
                {
                    session_id = "other-session",
                    state_token = "state-2",
                    sheet_name = "Sheet1",
                    cells = new[] { new { address = "A1", value = "WRONG" } }
                })
            };
            var decision = await session.AuthorizeAsync(
                new AgentRuntimePermissionRequest(null!, writeRange, wrongCall, null),
                CancellationToken.None).ConfigureAwait(false);
            Check(!decision.Allowed,
                "A different Office session inherited authority from the task-launched Excel window.");

            var wrongNative = H2AgentResourceBinding.FromLiveObservation(
                H2ApplicationKind.Excel,
                "office-host",
                "wrong-native-session",
                "Book2",
                DateTime.UtcNow,
                providerInstanceId: "fake-office:wrong",
                providerVersion: "fixture-v1",
                processId: launchedWindow.ProcessId,
                processStartUtcTicks: launchedWindow.ProcessStartedUtcTicks,
                windowIdentity: $"win32:{launchedWindow.Handle + 1:x}:{launchedWindow.ProcessId}:{launchedWindow.ProcessStartedUtcTicks}",
                viewIdentity: "wrong-view",
                dirty: true);
            Check(!session.IsTaskLaunchedOfficeCandidate(wrongNative),
                "A different HWND was accepted as the task-launched Office target.");
        }).ConfigureAwait(false);

        await Case("full-access-launched-office-never-falls-back-to-another-window", async () =>
        {
            var workspace = Path.Combine(root, "full-access-launched-office-workspace");
            var state = Path.Combine(root, "full-access-launched-office-state");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(state);

            var now = DateTime.UtcNow;
            var scope = new H2AgentPermissionScope(
                H2AgentPermissionMode.FullAccess,
                H2AgentResourceScopeKind.Machine,
                H2AgentPermissionScope.CurrentMachineResourceKey,
                mutationAllowed: true,
                approvalRequired: false,
                issuedUtc: now,
                expiresUtc: now.AddMinutes(30));
            var context = new H2AgentTaskContext(
                workspace,
                "",
                PermissionScope: scope,
                TargetIntent: H2AgentTargetIntent.OpenDocument);
            var binding = new H2AgentTargetBindingPolicy(null, workspace);
            using var fakeOffice = new TaskLaunchedExcelClient();
            using var session = new H2ProductionToolSession(
                Guid.NewGuid(),
                null,
                readOnly: false,
                context,
                projects: null,
                approve: (_, _, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(true);
                },
                targetPolicy: binding,
                officeClientFactory: () => fakeOffice,
                userGoal: "Mở Excel trắng mới rồi điền Xin chào vào A1");
            using var tools = new global::H2AgentLab.AgentTools(
                new global::H2AgentLab.SafeWorkspace(
                    workspace,
                    () => scope.HasFullAccessAt(DateTime.UtcNow)),
                state,
                (_, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(true);
                },
                (_, _) => { })
            {
                ReadOnly = false,
                ProductionSession = session
            };

            var registry = NormalRuntimeToolRegistry.Create(tools);
            var verifiers = new List<IAgentRuntimeDomainVerifier>();
            registry = session.Configure(tools, registry, verifiers);
            Check(registry.TryGet("excel.get_active_workbook", out var getWorkbook),
                "FullAccess did not expose live Excel tools.");

            var launchedWindow = new DesktopWindowInfo(
                "desktop-full-access-launched-excel",
                0x12345,
                7001,
                638943000000000000L,
                "EXCEL",
                "Book1 - Excel",
                new DesktopBounds(100, 100, 900, 700),
                96,
                true);
            session.ObserveLaunchedApplicationWindow(new DesktopApplicationLaunchResult(
                "Excel",
                "excel",
                "EXCEL",
                true,
                false,
                launchedWindow));

            // OfficeHost sees only a different pre-existing workbook/window. FullAccess is execution
            // permission, not authority to substitute that workbook for the task-created blank Excel.
            fakeOffice.Bind(launchedWindow with
            {
                SessionId = "desktop-old-excel",
                Handle = launchedWindow.Handle + 1,
                Title = "Old.xlsx - Excel"
            });

            var output = await getWorkbook.Executor.ExecuteAsync(
                new ToolCall(
                    "full-access-wrong-office",
                    "excel.get_active_workbook",
                    JsonSerializer.SerializeToElement(new { })),
                CancellationToken.None).ConfigureAwait(false);
            using var parsed = JsonDocument.Parse(output);
            Check(parsed.RootElement.TryGetProperty("error", out var error)
                && error.GetString() == "resource_not_found",
                "FullAccess silently fell back from the task-created Excel HWND to another workbook: " + output);
        }).ConfigureAwait(false);

        await Case("document-scope-does-not-inherit-task-launched-office-window", async () =>
        {
            var workspace = Path.Combine(root, "document-scope-launched-office-workspace");
            Directory.CreateDirectory(workspace);
            var now = DateTime.UtcNow;
            var scope = new H2AgentPermissionScope(
                H2AgentPermissionMode.AskBeforeChanges,
                H2AgentResourceScopeKind.Document,
                "document:existing-excel-session",
                mutationAllowed: true,
                approvalRequired: true,
                issuedUtc: now,
                expiresUtc: now.AddMinutes(30),
                applicationKind: H2ApplicationKind.Excel,
                documentSessionId: "existing-excel-session",
                documentPath: Path.Combine(workspace, "Existing.xlsx"));
            var context = new H2AgentTaskContext(
                workspace,
                "",
                PermissionScope: scope,
                TargetIntent: H2AgentTargetIntent.OpenDocument);
            using var session = new H2ProductionToolSession(
                Guid.NewGuid(),
                null,
                readOnly: false,
                context,
                projects: null,
                approve: (_, _, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(true);
                });

            var launchedWindow = new DesktopWindowInfo(
                "desktop-document-scope-new-excel",
                0x22345,
                7101,
                638943000000000100L,
                "EXCEL",
                "Book2 - Excel",
                new DesktopBounds(120, 120, 900, 700),
                96,
                true);
            session.ObserveLaunchedApplicationWindow(new DesktopApplicationLaunchResult(
                "Excel",
                "excel",
                "EXCEL",
                true,
                false,
                launchedWindow));

            var launchedBinding = H2AgentResourceBinding.FromLiveObservation(
                H2ApplicationKind.Excel,
                "office-host",
                "new-excel-session",
                "Book2",
                DateTime.UtcNow,
                providerInstanceId: "office-document-scope-fixture",
                providerVersion: "fixture-v1",
                processId: launchedWindow.ProcessId,
                processStartUtcTicks: launchedWindow.ProcessStartedUtcTicks,
                windowIdentity: $"win32:{launchedWindow.Handle:x}:{launchedWindow.ProcessId}:{launchedWindow.ProcessStartedUtcTicks}",
                viewIdentity: "new-excel-view");

            Check(!session.HasTaskLaunchedOfficeAuthority(H2ApplicationKind.Excel)
                && !session.IsTaskLaunchedOfficeCandidate(launchedBinding),
                "A Document-scoped task inherited authority over a newly launched Excel window.");

            var rejected = false;
            try { session.PinTaskLaunchedOfficeBinding(launchedBinding); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected,
                "A Document-scoped task pinned a newly launched Excel session outside its granted document.");
            await Task.CompletedTask;
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
            Check(report.Contains("RESULT: 12 passed, 0 failed.", StringComparison.Ordinal),
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

    private sealed class TaskLaunchedExcelClient : IOfficeSessionClient
    {
        public const string SessionId = "task-launched-excel-session";
        private OfficeNativeIdentity? _native;
        private ExcelLiveSnapshot? _snapshot;
        public string InstanceIdentity => "task-launched-office-fixture";

        public void Bind(DesktopWindowInfo window)
        {
            _native = new OfficeNativeIdentity(
                window.ProcessId,
                window.ProcessStartedUtcTicks,
                1,
                window.Handle,
                window.Handle,
                0,
                "task-launched-book1",
                "fixture-v1");
            _snapshot = Snapshot("state-1", "", "KEEP");
        }

        public Task<ExcelDiscovery> DiscoverExcelAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = Current();
            var info = new ExcelWorkbookInfo(
                snapshot.SessionId,
                snapshot.Name,
                snapshot.FullName,
                snapshot.Saved,
                snapshot.ActiveSheet,
                snapshot.SelectionAddress,
                snapshot.StateToken)
            {
                NativeIdentity = _native
            };
            return Task.FromResult(new ExcelDiscovery([info], SessionId)
            {
                Report = new OfficeDiscoveryReport(
                    true,
                    OfficeDiscoveryLimits.Coverage,
                    1,
                    1,
                    1,
                    [])
            });
        }

        public Task<ExcelLiveSnapshot> SnapshotExcelAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireSession(sessionId);
            return Task.FromResult(Current());
        }

        public Task<ExcelPatchResult> PatchExcelAsync(
            ExcelPatchRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireSession(request.SessionId);
            var before = Current();
            if (request.StateToken != before.StateToken)
                throw new OfficeHostClientException("stale_resource", "Fixture state token changed.", noEffect: true);
            if (!request.PermissionGranted)
                throw new OfficeHostClientException("permission_denied", "Fixture permission denied.", noEffect: true);
            if (request.SheetName != "Sheet1")
                throw new OfficeHostClientException("resource_not_found", "Fixture sheet missing.", noEffect: true);

            var cells = before.Sheets.Single().Cells
                .ToDictionary(cell => cell.Address, cell => cell, StringComparer.OrdinalIgnoreCase);
            foreach (var patch in request.Cells)
            {
                if (!cells.TryGetValue(patch.Address, out var old))
                    throw new OfficeHostClientException("resource_not_found", "Fixture cell missing.", noEffect: true);
                cells[patch.Address] = old with
                {
                    Value = patch.ClearValue ? "" : patch.Value ?? old.Value,
                    Formula = patch.Formula ?? old.Formula,
                    Bold = patch.Bold ?? old.Bold,
                    Italic = patch.Italic ?? old.Italic,
                    FillColor = patch.FillColor ?? old.FillColor,
                    NumberFormat = patch.NumberFormat ?? old.NumberFormat
                };
            }

            var after = before with
            {
                Sheets =
                [
                    before.Sheets.Single() with
                    {
                        Cells = cells.Values.OrderBy(cell => cell.Address, StringComparer.Ordinal).ToArray()
                    }
                ],
                StateToken = "state-2"
            };
            _snapshot = after;
            return Task.FromResult(new ExcelPatchResult(
                before,
                after,
                request.Cells.Select(cell => cell.Address).ToArray()));
        }

        public Task<ExcelLiveSnapshot> RecalculateExcelAsync(
            ExcelRecalculateRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromException<ExcelLiveSnapshot>(new NotSupportedException("Not used by AR-061 fixture."));

        public Task<OfficeSaveCopyResult> SaveExcelCopyAsync(
            OfficeSaveCopyRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromException<OfficeSaveCopyResult>(new NotSupportedException("Not used by AR-061 fixture."));

        public Task<WordDiscovery> DiscoverWordAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WordDiscovery([], null)
            {
                Report = new OfficeDiscoveryReport(true, OfficeDiscoveryLimits.Coverage, 1, 0, 0, [])
            });

        public Task<WordLiveSnapshot> SnapshotWordAsync(
            string sessionId,
            CancellationToken cancellationToken = default)
            => Task.FromException<WordLiveSnapshot>(new NotSupportedException("Not used by AR-061 fixture."));

        public Task<WordPatchResult> PatchWordAsync(
            WordPatchRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromException<WordPatchResult>(new NotSupportedException("Not used by AR-061 fixture."));

        public Task<WordLanguageEvidenceResult> InspectWordLanguageAsync(
            WordLanguageEvidenceRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromException<WordLanguageEvidenceResult>(new NotSupportedException("Not used by AR-061 fixture."));

        public Task<OfficeSaveCopyResult> SaveWordCopyAsync(
            OfficeSaveCopyRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromException<OfficeSaveCopyResult>(new NotSupportedException("Not used by AR-061 fixture."));

        private ExcelLiveSnapshot Current()
            => _snapshot ?? throw new InvalidOperationException("Task-launched Excel fixture is not bound.");

        private void RequireSession(string sessionId)
        {
            if (sessionId != SessionId)
                throw new OfficeHostClientException("session_not_found", "Fixture session mismatch.", noEffect: true);
        }

        private ExcelLiveSnapshot Snapshot(string token, string a1, string b1)
            => new(
                SessionId,
                "Book1",
                "Book1",
                false,
                "Sheet1",
                "A1",
                [
                    new ExcelSheetState(
                        "Sheet1",
                        "visible",
                        [
                            Cell("A1", a1),
                            Cell("B1", b1)
                        ],
                        [],
                        [],
                        [])
                ],
                token)
            {
                NativeIdentity = _native
            };

        private static ExcelCellState Cell(string address, string value)
            => new(
                address,
                value,
                "",
                false,
                false,
                null,
                "General",
                "General",
                "Bottom");

        public void Dispose() { }
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

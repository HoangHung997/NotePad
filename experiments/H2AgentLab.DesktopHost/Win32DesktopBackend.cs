using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using H2AgentLab.DesktopProtocol;
using System.Windows.Forms;

namespace H2AgentLab.DesktopHost;

public sealed class Win32DesktopBackend : IDesktopBackend
{
    private readonly Dictionary<string, ObservationRecord> _observations = new(StringComparer.Ordinal);
    private long _observationSequence;
    private long _mutationSequence;

    public IReadOnlyList<DesktopWindowInfo> ListWindows()
    {
        var result = new List<DesktopWindowInfo>();
        foreach (AutomationElement element in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            try
            {
                var current = element.Current;
                if (current.NativeWindowHandle == 0
                    || current.ProcessId <= 0
                    || current.IsOffscreen
                    || string.IsNullOrWhiteSpace(current.Name))
                    continue;

                using var process = Process.GetProcessById(current.ProcessId);
                var processName = process.ProcessName;
                if (!DesktopSafetyPolicy.IsWindowAllowed(processName, current.Name))
                    continue;

                var handle = new IntPtr(current.NativeWindowHandle);
                var bounds = ReadBounds(handle);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    continue;

                var started = process.StartTime.ToUniversalTime().Ticks;
                var sessionId = DesktopHostState.SessionId(
                    current.ProcessId.ToString(CultureInfo.InvariantCulture),
                    started.ToString(CultureInfo.InvariantCulture),
                    current.NativeWindowHandle.ToString(CultureInfo.InvariantCulture));

                result.Add(new DesktopWindowInfo(
                    sessionId,
                    current.NativeWindowHandle,
                    current.ProcessId,
                    started,
                    processName,
                    current.Name,
                    bounds,
                    GetDpiForWindow(handle),
                    GetForegroundWindow() == handle));

                if (result.Count >= 80) break;
            }
            catch (Exception ex) when (
                ex is ElementNotAvailableException
                    or InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or ArgumentException)
            {
            }
        }

        return result
            .OrderByDescending(x => x.Foreground)
            .ThenBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public DesktopApplicationLaunchResult LaunchApplication(DesktopApplicationLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        DesktopSafetyPolicy.RequirePermission(request.PermissionGranted);
        var resolved = DesktopApplicationResolver.ResolveForLaunch(request.Application);
        var before = ListWindows()
            .Where(x => string.Equals(x.ProcessName, resolved.ProcessName, StringComparison.OrdinalIgnoreCase)
                && IsApplicationLaunchWindow(resolved.ProcessName, x.Handle))
            .ToArray();
        var beforeSessions = before.Select(x => x.SessionId).ToHashSet(StringComparer.Ordinal);
        var beforeForeground = before.Where(x => x.Foreground).Select(x => x.SessionId).ToHashSet(StringComparer.Ordinal);

        // If exactly one safe target is already running, "open/start app" is satisfied by
        // activating that exact observed window. Do not start the executable again and risk
        // creating an extra blank document/window.
        if (!request.RequireNewWindow && before.Length == 1)
        {
            var reused = ActivateWindow(new DesktopApplicationActivateRequest(
                before[0].SessionId,
                PermissionGranted: true));
            return new(
                request.Application,
                resolved.ApplicationId,
                resolved.ProcessName,
                NewWindowObserved: false,
                ReusedExistingWindow: true,
                reused);
        }
        if (!request.RequireNewWindow && before.Length > 1)
            throw new DesktopHostFaultException(
                "ambiguous_target",
                "More than one safe application window is already running; enumerate and activate an exact session_id. No new process was started.");

        try
        {
            var startInfo = new ProcessStartInfo(resolved.ExecutablePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(resolved.ExecutablePath) ?? AppContext.BaseDirectory
            };
            if (request.RequireNewWindow)
                startInfo.Arguments = DesktopApplicationResolver.NewWindowArgumentsForProcess(resolved.ProcessName);

            using var started = Process.Start(startInfo);
            if (started is null)
                throw new DesktopHostFaultException("provider_unavailable", "Windows did not start the requested application.");
        }
        catch (DesktopHostFaultException) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new DesktopHostFaultException("app_not_found", "Windows could not start the requested registered application.");
        }

        var wait = Math.Clamp(
            request.WaitMilliseconds,
            250,
            DesktopProtocolConstants.MaxApplicationWaitMilliseconds);
        var startedUtc = DateTime.UtcNow;
        var deadline = startedUtc.AddMilliseconds(wait);
        do
        {
            var current = ListWindows()
                .Where(x => string.Equals(x.ProcessName, resolved.ProcessName, StringComparison.OrdinalIgnoreCase)
                    && IsApplicationLaunchWindow(resolved.ProcessName, x.Handle))
                .ToArray();
            var created = SelectUniqueCreatedWindow(current, beforeSessions);
            if (created is not null)
                return new(request.Application, resolved.ApplicationId, resolved.ProcessName, true, false, created);

            if (!request.RequireNewWindow)
            {
                var promoted = current.FirstOrDefault(x => x.Foreground && !beforeForeground.Contains(x.SessionId));
                if (promoted is not null)
                    return new(request.Application, resolved.ApplicationId, resolved.ProcessName, false, true, promoted);
            }

            Thread.Sleep(100);
        }
        while (DateTime.UtcNow < deadline);

        throw new DesktopHostFaultException(
            "launch_unverified",
            request.RequireNewWindow
                ? "A distinct new application window was requested, but no new safe HWND/session was observed."
                : "The application launch was requested, but no exact new, activated or single reusable safe window was observed.");
    }

    internal static DesktopWindowInfo? SelectUniqueCreatedWindow(
        IReadOnlyList<DesktopWindowInfo> current,
        IReadOnlySet<string> beforeSessions)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(beforeSessions);
        var created = current
            .Where(x => !beforeSessions.Contains(x.SessionId))
            .ToArray();
        return created.Length switch
        {
            0 => null,
            1 => created[0],
            _ => throw new DesktopHostFaultException(
                "launch_ambiguous",
                "The application launch produced multiple new safe windows. The app may be open, but no exact target can be selected without new observation.")
        };
    }

    public DesktopWindowInfo WaitForApplicationWindow(DesktopApplicationWaitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var processName = DesktopApplicationResolver.ProcessNameFor(request.Application);
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Clamp(request.WaitMilliseconds, 0, DesktopProtocolConstants.MaxApplicationWaitMilliseconds));
        do
        {
            var found = ListWindows()
                .Where(x => string.Equals(x.ProcessName, processName, StringComparison.OrdinalIgnoreCase)
                    && IsApplicationLaunchWindow(processName, x.Handle))
                .ToArray();
            if (found.Length == 1) return found[0];
            if (found.Length > 1)
                throw new DesktopHostFaultException(
                    "ambiguous_target",
                    "More than one safe visible window exists for this application; enumerate and use an exact session_id.");
            if (DateTime.UtcNow >= deadline) break;
            Thread.Sleep(100);
        }
        while (true);

        throw new DesktopHostFaultException(
            "app_not_found",
            "No safe visible window for the requested application was observed.");
    }

    private static bool IsApplicationLaunchWindow(string processName, long handle)
    {
        var className = WindowClass(new IntPtr(handle));
        return IsApplicationLaunchWindowClass(processName, className);
    }

    internal static bool IsApplicationLaunchWindowClass(string processName, string className)
        => processName switch
        {
            var name when name.Equals("WINWORD", StringComparison.OrdinalIgnoreCase)
                => className.Equals("OpusApp", StringComparison.Ordinal),
            var name when name.Equals("EXCEL", StringComparison.OrdinalIgnoreCase)
                => className.Equals("XLMAIN", StringComparison.Ordinal),
            _ => true
        };

    private static string WindowClass(IntPtr handle)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(handle, buffer, buffer.Capacity) > 0
            ? buffer.ToString()
            : "";
    }

    public DesktopWindowInfo ActivateWindow(DesktopApplicationActivateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        DesktopSafetyPolicy.RequirePermission(request.PermissionGranted);
        var window = ResolveWindow(request.SessionId);
        var handle = new IntPtr(window.Handle);
        TryActivateExactWindow(handle);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (GetForegroundWindow() == handle)
                return ResolveWindow(request.SessionId);
            Thread.Sleep(50);
        }

        // One bounded retry after the OS had time to process restore/focus messages.
        TryActivateExactWindow(handle);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (GetForegroundWindow() == handle)
                return ResolveWindow(request.SessionId);
            Thread.Sleep(50);
        }

        throw new DesktopHostFaultException(
            "foreground_failed",
            "Windows did not confirm the exact observed application window as foreground.");
    }

    private static void TryActivateExactWindow(IntPtr handle)
    {
        if (IsIconic(handle)) _ = ShowWindowAsync(handle, 9); // SW_RESTORE

        var currentThread = GetCurrentThreadId();
        var targetThread = GetWindowThreadProcessId(handle, out _);
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero
            ? 0u
            : GetWindowThreadProcessId(foreground, out _);

        var attachedForeground = false;
        var attachedTarget = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
                attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);
            if (targetThread != 0
                && targetThread != currentThread
                && targetThread != foregroundThread)
                attachedTarget = AttachThreadInput(currentThread, targetThread, true);

            _ = BringWindowToTop(handle);
            _ = SetForegroundWindow(handle);
        }
        finally
        {
            if (attachedTarget)
                _ = AttachThreadInput(currentThread, targetThread, false);
            if (attachedForeground)
                _ = AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    public DesktopObservation Observe(string sessionId)
    {
        var window = ResolveWindow(sessionId);
        var built = BuildObservation(window, issueTokens: true);
        var observedMutation = _observations.TryGetValue(sessionId, out var old)
            ? old.PendingMutationId
            : null;

        _observationSequence++;
        var record = new ObservationRecord(
            built.StateId,
            built.WindowFingerprint,
            built.Tokens,
            DateTime.UtcNow,
            _observationSequence,
            PendingMutationId: null);
        _observations[sessionId] = record;

        return new DesktopObservation(
            window,
            built.StateId,
            built.Screenshot,
            built.ScreenshotSha256,
            built.Elements,
            _observationSequence,
            observedMutation);
    }

    public DesktopActionResult Act(DesktopActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        DesktopSafetyPolicy.RequireAction(request.Action);

        if (request.Action == DesktopActionKinds.Wait)
        {
            Thread.Sleep(Math.Clamp(request.WaitMilliseconds ?? 0, 0, 5_000));
            return new DesktopActionResult(
                request.Action,
                request.StateId,
                _observations.TryGetValue(request.SessionId, out var waitRecord)
                    ? waitRecord.ObservationSequence
                    : 0,
                false,
                false,
                null,
                "wait completed");
        }

        DesktopSafetyPolicy.RequirePermission(request.PermissionGranted);
        if (!_observations.TryGetValue(request.SessionId, out var observed))
            throw new DesktopHostFaultException("stale_state", "Observe the selected window before acting.");
        if (DateTime.UtcNow - observed.ObservedUtc > TimeSpan.FromSeconds(DesktopProtocolConstants.ElementTokenLifetimeSeconds))
            throw new DesktopHostFaultException("stale_state", "Desktop observation expired; observe again.");
        if (!string.Equals(request.StateId, observed.StateId, StringComparison.Ordinal))
            throw new DesktopHostFaultException("stale_state", "Desktop state_id does not match the most recent observation.");

        var window = ResolveWindow(request.SessionId);
        var current = BuildObservation(window, issueTokens: false);
        if (!string.Equals(current.StateId, request.StateId, StringComparison.Ordinal)
            || !string.Equals(current.WindowFingerprint, observed.WindowFingerprint, StringComparison.Ordinal))
            throw new DesktopHostFaultException("stale_state", "Desktop window changed since observation; observe again before acting.");

        var token = ResolveToken(observed, request.ElementToken);
        switch (request.Action)
        {
            case DesktopActionKinds.Click:
                Click(window, token, request, doubleClick: false);
                break;
            case DesktopActionKinds.DoubleClick:
                Click(window, token, request, doubleClick: true);
                break;
            case DesktopActionKinds.Type:
                TypeText(token, request.Text ?? "");
                break;
            case DesktopActionKinds.Key:
                SendKey(window, request.Key);
                break;
            case DesktopActionKinds.Scroll:
                Scroll(window, token, request);
                break;
            case DesktopActionKinds.Drag:
                Drag(window, request);
                break;
            default:
                throw new DesktopHostFaultException("invalid_action", "Unsupported desktop action.");
        }

        _mutationSequence++;
        var mutationId = DesktopHostState.MutationId(_mutationSequence);
        _observations[request.SessionId] = observed with
        {
            PendingMutationId = mutationId,
            Tokens = new Dictionary<string, TokenRecord>(StringComparer.Ordinal)
        };

        return new DesktopActionResult(
            request.Action,
            request.StateId,
            observed.ObservationSequence,
            true,
            true,
            mutationId,
            "desktop mutation executed; a newer observation is required before final evidence");
    }

    private static void Click(
        DesktopWindowInfo window,
        TokenRecord? token,
        DesktopActionRequest request,
        bool doubleClick)
    {
        if (token is not null)
        {
            var element = FindElement(window, token);
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            {
                ((InvokePattern)invoke).Invoke();
                if (doubleClick) ((InvokePattern)invoke).Invoke();
                return;
            }

            var rectangle = element.Current.BoundingRectangle;
            if (rectangle.IsEmpty)
                throw new DesktopHostFaultException("invalid_target", "Observed UI element has no clickable bounds.");
            NativeClick(
                (int)Math.Round(rectangle.Left + rectangle.Width / 2),
                (int)Math.Round(rectangle.Top + rectangle.Height / 2),
                doubleClick);
            return;
        }

        var x = request.X ?? throw new DesktopHostFaultException("invalid_request", "Observed X coordinate is required.");
        var y = request.Y ?? throw new DesktopHostFaultException("invalid_request", "Observed Y coordinate is required.");
        if (!window.Bounds.Contains(x, y))
            throw new DesktopHostFaultException("invalid_target", "Coordinate is outside the observed window bounds.");
        NativeClick(x, y, doubleClick);
    }

    private static void TypeText(TokenRecord? token, string text)
    {
        if (token is null)
            throw new DesktopHostFaultException("invalid_target", "Typing requires an observed element token.");
        if (text.Length > 10_000)
            throw new DesktopHostFaultException("invalid_request", "Typed text exceeds 10,000 characters.");

        var element = FindElement(token.Window, token);
        if (element.Current.IsPassword)
            throw new DesktopHostFaultException("sensitive_target", "Password controls cannot be read or changed.");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
            throw new DesktopHostFaultException("invalid_target", "Observed element does not support structured text assignment.");
        var pattern = (ValuePattern)value;
        if (pattern.Current.IsReadOnly)
            throw new DesktopHostFaultException("invalid_target", "Observed text control is read-only.");
        pattern.SetValue(text);
    }

    private static void SendKey(DesktopWindowInfo window, string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 64)
            throw new DesktopHostFaultException("invalid_request", "Key/chord is missing or too long.");
        if (!SetForegroundWindow(new IntPtr(window.Handle)))
            throw new DesktopHostFaultException("foreground_failed", "Could not focus the observed window for key input.");
        SendKeys.SendWait(NormalizeKey(key));
    }

    private static void Scroll(
        DesktopWindowInfo window,
        TokenRecord? token,
        DesktopActionRequest request)
    {
        var delta = Math.Clamp(request.ScrollDelta ?? 0, -20_000, 20_000);
        if (delta == 0)
            throw new DesktopHostFaultException("invalid_request", "Scroll delta cannot be zero.");

        if (token is not null)
        {
            var element = FindElement(window, token);
            if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out var scroll))
            {
                var pattern = (ScrollPattern)scroll;
                pattern.ScrollVertical(delta > 0 ? ScrollAmount.LargeIncrement : ScrollAmount.LargeDecrement);
                return;
            }
        }

        var x = request.X ?? window.Bounds.X + window.Bounds.Width / 2;
        var y = request.Y ?? window.Bounds.Y + window.Bounds.Height / 2;
        if (!window.Bounds.Contains(x, y))
            throw new DesktopHostFaultException("invalid_target", "Scroll coordinate is outside the observed window.");
        SetCursorPos(x, y);
        mouse_event(MouseEventWheel, 0, 0, delta, UIntPtr.Zero);
    }

    private static void Drag(
        DesktopWindowInfo window,
        DesktopActionRequest request)
    {
        var x = request.X ?? throw new DesktopHostFaultException("invalid_request", "Drag start X is required.");
        var y = request.Y ?? throw new DesktopHostFaultException("invalid_request", "Drag start Y is required.");
        var endX = request.EndX ?? throw new DesktopHostFaultException("invalid_request", "Drag end X is required.");
        var endY = request.EndY ?? throw new DesktopHostFaultException("invalid_request", "Drag end Y is required.");
        if (!window.Bounds.Contains(x, y)
            || endX < 0 || endY < 0)
            throw new DesktopHostFaultException("invalid_target", "Drag coordinates are outside the observed safe start state.");

        SetCursorPos(x, y);
        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        try
        {
            SetCursorPos(endX, endY);
            Thread.Sleep(60);
        }
        finally
        {
            mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
        }
    }

    private static string NormalizeKey(string key)
    {
        var normalized = key.Trim();
        return normalized.ToUpperInvariant() switch
        {
            "ENTER" => "{ENTER}",
            "TAB" => "{TAB}",
            "ESC" or "ESCAPE" => "{ESC}",
            "BACKSPACE" => "{BACKSPACE}",
            "DELETE" => "{DELETE}",
            "CTRL+A" => "^a",
            "CTRL+C" => "^c",
            "CTRL+V" => "^v",
            "CTRL+Z" => "^z",
            _ when normalized.Length == 1 => normalized,
            _ => throw new DesktopHostFaultException("invalid_request", "Unsupported safe key/chord.")
        };
    }

    private DesktopWindowInfo ResolveWindow(string sessionId)
    {
        var window = ListWindows().SingleOrDefault(x => x.SessionId == sessionId);
        return window
            ?? throw new DesktopHostFaultException("session_not_found", "Desktop window session is unavailable or blocked by policy.");
    }

    private BuiltObservation BuildObservation(
        DesktopWindowInfo window,
        bool issueTokens)
    {
        var handle = new IntPtr(window.Handle);
        var screenshot = CaptureWindow(handle, window.Bounds);
        var screenshotSha = DesktopHostState.Sha256(screenshot);

        var root = AutomationElement.FromHandle(handle);
        if (root.Current.ProcessId != window.ProcessId
            || !DesktopSafetyPolicy.IsWindowAllowed(window.ProcessName, root.Current.Name))
            throw new DesktopHostFaultException("stale_state", "Desktop window identity changed or became sensitive.");

        var rawNodes = Walk(root).Take(DesktopProtocolConstants.MaxElements).ToArray();
        var elementSignatures = rawNodes
            .Select(x => DescribeSignature(x.Element, x.Depth))
            .Where(x => x is not null)
            .Select(x => x!)
            .ToArray();

        var stateId = DesktopHostState.StableToken(new
        {
            window.SessionId,
            window.Handle,
            window.ProcessId,
            window.ProcessStartedUtcTicks,
            root.Current.Name,
            window.Bounds,
            window.Dpi,
            ScreenshotSha = screenshotSha,
            Elements = elementSignatures.Select(x => new
            {
                x.Name,
                x.ControlType,
                x.Value,
                x.Bounds,
                x.Enabled,
                x.Offscreen,
                x.CanInvoke,
                x.CanSetValue,
                x.CanScroll,
                x.Depth
            }).ToArray()
        });

        var tokens = new Dictionary<string, TokenRecord>(StringComparer.Ordinal);
        var elements = new List<DesktopElementInfo>();
        if (issueTokens)
        {
            foreach (var item in rawNodes)
            {
                var signature = DescribeSignature(item.Element, item.Depth);
                if (signature is null) continue;

                var token = "el-" + Guid.NewGuid().ToString("N");
                int[] runtimeId;
                try { runtimeId = item.Element.GetRuntimeId(); }
                catch (ElementNotAvailableException) { continue; }

                tokens[token] = new TokenRecord(
                    window,
                    runtimeId,
                    signature.Name,
                    signature.ControlType,
                    signature.Value,
                    stateId,
                    DateTime.UtcNow.AddSeconds(DesktopProtocolConstants.ElementTokenLifetimeSeconds));

                elements.Add(new DesktopElementInfo(
                    token,
                    signature.Name,
                    signature.ControlType,
                    signature.Value,
                    signature.Bounds,
                    signature.Enabled,
                    signature.Offscreen,
                    signature.CanInvoke,
                    signature.CanSetValue,
                    signature.CanScroll,
                    signature.Depth));
            }
        }

        var fingerprint = DesktopHostState.StableToken(new
        {
            window.SessionId,
            window.Handle,
            window.ProcessId,
            window.ProcessStartedUtcTicks,
            window.Title,
            window.Bounds,
            window.Dpi
        });

        return new BuiltObservation(
            stateId,
            fingerprint,
            screenshot,
            screenshotSha,
            elements,
            tokens);
    }

    private static IEnumerable<(AutomationElement Element, int Depth)> Walk(AutomationElement root)
    {
        var pending = new Queue<(AutomationElement Element, int Depth)>();
        pending.Enqueue((root, 0));
        var visited = 0;
        while (pending.Count > 0 && visited++ < 600)
        {
            var (element, depth) = pending.Dequeue();
            bool password;
            try { password = element.Current.IsPassword; }
            catch (ElementNotAvailableException) { continue; }
            if (password) continue;

            yield return (element, depth);
            if (depth >= 10) continue;

            AutomationElement? child;
            try { child = TreeWalker.ControlViewWalker.GetFirstChild(element); }
            catch (ElementNotAvailableException) { continue; }
            var siblings = 0;
            while (child is not null && siblings++ < 120)
            {
                pending.Enqueue((child, depth + 1));
                try { child = TreeWalker.ControlViewWalker.GetNextSibling(child); }
                catch (ElementNotAvailableException) { child = null; }
            }
        }
    }

    private static ElementSignature? DescribeSignature(
        AutomationElement element,
        int depth)
    {
        try
        {
            var current = element.Current;
            if (current.IsPassword) return null;
            var name = Truncate(current.Name, 300);
            var type = current.ControlType?.ProgrammaticName ?? "ControlType.Unknown";
            var value = "";
            var canSet = false;
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
            {
                var info = ((ValuePattern)valuePattern).Current;
                value = Truncate(info.Value, 1_500);
                canSet = !info.IsReadOnly;
            }
            else if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern))
            {
                value = Truncate(((TextPattern)textPattern).DocumentRange.GetText(1_500), 1_500);
            }

            var rect = current.BoundingRectangle;
            var bounds = rect.IsEmpty
                ? new DesktopBounds(0, 0, 0, 0)
                : new DesktopBounds(
                    (int)Math.Round(rect.X),
                    (int)Math.Round(rect.Y),
                    Math.Max(0, (int)Math.Round(rect.Width)),
                    Math.Max(0, (int)Math.Round(rect.Height)));

            return new ElementSignature(
                name,
                type,
                value,
                bounds,
                current.IsEnabled,
                current.IsOffscreen,
                element.TryGetCurrentPattern(InvokePattern.Pattern, out _),
                canSet,
                element.TryGetCurrentPattern(ScrollPattern.Pattern, out _),
                depth);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    private static TokenRecord? ResolveToken(
        ObservationRecord observed,
        string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        if (!observed.Tokens.TryGetValue(token, out var record)
            || record.ExpiresUtc < DateTime.UtcNow
            || !string.Equals(record.StateId, observed.StateId, StringComparison.Ordinal))
            throw new DesktopHostFaultException("stale_element", "Desktop element token expired or belongs to another observation.");
        return record;
    }

    private static AutomationElement FindElement(
        DesktopWindowInfo window,
        TokenRecord token)
    {
        if (token.Window.SessionId != window.SessionId)
            throw new DesktopHostFaultException("stale_element", "Element token belongs to another window.");

        var root = AutomationElement.FromHandle(new IntPtr(window.Handle));
        foreach (var item in Walk(root))
        {
            try
            {
                if (!item.Element.GetRuntimeId().SequenceEqual(token.RuntimeId))
                    continue;
                var signature = DescribeSignature(item.Element, item.Depth)
                    ?? throw new DesktopHostFaultException("stale_element", "Observed UI element is no longer readable.");
                if (signature.Name != token.Name
                    || signature.ControlType != token.ControlType
                    || signature.Value != token.Value)
                    throw new DesktopHostFaultException("stale_element", "Observed UI element changed since observation.");
                return item.Element;
            }
            catch (ElementNotAvailableException)
            {
            }
        }

        throw new DesktopHostFaultException("stale_element", "Observed UI element no longer exists.");
    }

    private static byte[] CaptureWindow(
        IntPtr handle,
        DesktopBounds bounds)
    {
        if (bounds.Width > 4_096 || bounds.Height > 4_096 || (long)bounds.Width * bounds.Height > 20_000_000)
            throw new DesktopHostFaultException("screenshot_too_large", "Observed window is too large to capture safely.");

        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using var graphics = Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();
        var printed = false;
        try
        {
            printed = PrintWindow(handle, hdc, 2);
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }

        if (!printed)
            graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, new Size(bounds.Width, bounds.Height));

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        var bytes = stream.ToArray();
        if (bytes.Length > DesktopProtocolConstants.MaxScreenshotBytes)
            throw new DesktopHostFaultException("screenshot_too_large", "Desktop screenshot exceeds 4 MB.");
        return bytes;
    }

    private static DesktopBounds ReadBounds(IntPtr handle)
    {
        if (!GetWindowRect(handle, out var rect))
            throw new DesktopHostFaultException("window_unavailable", "Could not read desktop window bounds.");
        return new DesktopBounds(
            rect.Left,
            rect.Top,
            Math.Max(0, rect.Right - rect.Left),
            Math.Max(0, rect.Bottom - rect.Top));
    }

    private static void NativeClick(int x, int y, bool doubleClick)
    {
        SetCursorPos(x, y);
        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
        if (doubleClick)
        {
            Thread.Sleep(80);
            mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
        }
    }

    private static string Truncate(string? value, int max)
    {
        value ??= "";
        value = value.Replace('\r', ' ').Replace('\n', ' ');
        return value.Length <= max ? value : value[..max];
    }

    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventWheel = 0x0800;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(
        uint dwFlags,
        int dx,
        int dy,
        int dwData,
        UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private sealed record ElementSignature(
        string Name,
        string ControlType,
        string Value,
        DesktopBounds Bounds,
        bool Enabled,
        bool Offscreen,
        bool CanInvoke,
        bool CanSetValue,
        bool CanScroll,
        int Depth);

    private sealed record TokenRecord(
        DesktopWindowInfo Window,
        int[] RuntimeId,
        string Name,
        string ControlType,
        string Value,
        string StateId,
        DateTime ExpiresUtc);

    private sealed record ObservationRecord(
        string StateId,
        string WindowFingerprint,
        IReadOnlyDictionary<string, TokenRecord> Tokens,
        DateTime ObservedUtc,
        long ObservationSequence,
        string? PendingMutationId);

    private sealed record BuiltObservation(
        string StateId,
        string WindowFingerprint,
        byte[] Screenshot,
        string ScreenshotSha256,
        IReadOnlyList<DesktopElementInfo> Elements,
        IReadOnlyDictionary<string, TokenRecord> Tokens);
}

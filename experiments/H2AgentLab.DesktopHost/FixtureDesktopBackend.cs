using System.Drawing;
using System.Drawing.Imaging;
using H2AgentLab.DesktopProtocol;

namespace H2AgentLab.DesktopHost;

public sealed class FixtureDesktopBackend : IDesktopBackend
{
    private const string SessionId = "desktop-fixture-1";

    private readonly Dictionary<string, TokenRecord> _tokens = new(StringComparer.Ordinal);
    private DesktopBounds _bounds = new(100, 100, 640, 480);
    private string _text = "fixture text";
    private string _status = "ready";
    private int _scroll;
    private long _observationSequence;
    private long _mutationSequence;
    private string? _pendingMutationId;
    private string? _lastObservedStateId;

    public IReadOnlyList<DesktopWindowInfo> ListWindows()
        =>
        [
            new DesktopWindowInfo(
                SessionId,
                424242,
                Environment.ProcessId,
                1,
                "H2DesktopFixture",
                "H2 Desktop Fixture Window",
                _bounds,
                96,
                true)
        ];

    public DesktopObservation Observe(string sessionId)
    {
        RequireSession(sessionId);
        _observationSequence++;
        var screenshot = RenderScreenshot();
        var screenshotSha = DesktopHostState.Sha256(screenshot);
        var stateId = CurrentStateId(screenshotSha);
        var observedMutation = _pendingMutationId;
        _pendingMutationId = null;
        _lastObservedStateId = stateId;
        _tokens.Clear();

        var elements = new[]
        {
            Element("button", "Increment", "Button", _status, new DesktopBounds(_bounds.X + 20, _bounds.Y + 40, 120, 36), canInvoke: true, canSetValue: false, canScroll: false, depth: 1, stateId),
            Element("textbox", "Fixture text", "Edit", _text, new DesktopBounds(_bounds.X + 20, _bounds.Y + 100, 260, 32), canInvoke: false, canSetValue: true, canScroll: false, depth: 1, stateId),
            Element("scroll", "Fixture scroll area", "Pane", _scroll.ToString(System.Globalization.CultureInfo.InvariantCulture), new DesktopBounds(_bounds.X + 320, _bounds.Y + 40, 240, 260), canInvoke: false, canSetValue: false, canScroll: true, depth: 1, stateId),
            Element("resize", "Resize handle", "Thumb", "", new DesktopBounds(_bounds.X + _bounds.Width - 24, _bounds.Y + _bounds.Height - 24, 20, 20), canInvoke: false, canSetValue: false, canScroll: false, depth: 1, stateId)
        };

        return new DesktopObservation(
            ListWindows().Single(),
            stateId,
            screenshot,
            screenshotSha,
            elements,
            _observationSequence,
            observedMutation);
    }

    public DesktopActionResult Act(DesktopActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireSession(request.SessionId);
        DesktopSafetyPolicy.RequireAction(request.Action);

        if (request.Action == DesktopActionKinds.Wait)
        {
            var wait = Math.Clamp(request.WaitMilliseconds ?? 0, 0, 2_000);
            Thread.Sleep(wait);
            return new DesktopActionResult(
                request.Action,
                request.StateId,
                _observationSequence,
                false,
                false,
                null,
                $"waited {wait} ms");
        }

        DesktopSafetyPolicy.RequirePermission(request.PermissionGranted);
        var currentScreenshot = RenderScreenshot();
        var currentState = CurrentStateId(DesktopHostState.Sha256(currentScreenshot));
        if (string.IsNullOrWhiteSpace(_lastObservedStateId)
            || !string.Equals(request.StateId, _lastObservedStateId, StringComparison.Ordinal)
            || !string.Equals(request.StateId, currentState, StringComparison.Ordinal))
            throw new DesktopHostFaultException("stale_state", "Desktop state changed since the last observation.");

        var token = ResolveToken(request.ElementToken, request.StateId);
        switch (request.Action)
        {
            case DesktopActionKinds.Click:
                RequireElementOrCoordinate(token, request, "button");
                _status = _status.StartsWith("clicked:", StringComparison.Ordinal)
                    ? "clicked:" + (ParseCount(_status) + 1)
                    : "clicked:1";
                break;

            case DesktopActionKinds.DoubleClick:
                RequireElementOrCoordinate(token, request, "button");
                _status = "double-clicked";
                break;

            case DesktopActionKinds.Type:
                if (token?.Key != "textbox")
                    throw new DesktopHostFaultException("invalid_target", "Typing requires the observed fixture text box token.");
                if ((request.Text?.Length ?? 0) > 10_000)
                    throw new DesktopHostFaultException("invalid_request", "Desktop typed text exceeds 10,000 characters.");
                _text = request.Text ?? "";
                break;

            case DesktopActionKinds.Key:
                if (string.IsNullOrWhiteSpace(request.Key) || request.Key.Length > 64)
                    throw new DesktopHostFaultException("invalid_request", "Desktop key is missing or too long.");
                _status = "key:" + request.Key.Trim();
                break;

            case DesktopActionKinds.Scroll:
                if (token?.Key != "scroll")
                    throw new DesktopHostFaultException("invalid_target", "Scroll requires the observed scroll-pane token.");
                _scroll += Math.Clamp(request.ScrollDelta ?? 0, -10_000, 10_000);
                break;

            case DesktopActionKinds.Drag:
                if (token?.Key != "resize")
                    throw new DesktopHostFaultException("invalid_target", "Fixture drag requires the observed resize-handle token.");
                var endX = request.EndX ?? throw new DesktopHostFaultException("invalid_request", "Drag end X is required.");
                var endY = request.EndY ?? throw new DesktopHostFaultException("invalid_request", "Drag end Y is required.");
                _bounds = _bounds with
                {
                    Width = Math.Clamp(endX - _bounds.X, 320, 1_200),
                    Height = Math.Clamp(endY - _bounds.Y, 240, 900)
                };
                break;

            default:
                throw new DesktopHostFaultException("invalid_action", "Unsupported fixture desktop action.");
        }

        _mutationSequence++;
        var mutationId = DesktopHostState.MutationId(_mutationSequence);
        _pendingMutationId = mutationId;
        _tokens.Clear();
        _lastObservedStateId = null;

        return new DesktopActionResult(
            request.Action,
            request.StateId,
            _observationSequence,
            true,
            true,
            mutationId,
            "mutation executed; observe again before using it as final evidence");
    }

    private DesktopElementInfo Element(
        string key,
        string name,
        string type,
        string value,
        DesktopBounds bounds,
        bool canInvoke,
        bool canSetValue,
        bool canScroll,
        int depth,
        string stateId)
    {
        var token = "el-" + Guid.NewGuid().ToString("N");
        _tokens[token] = new TokenRecord(
            key,
            stateId,
            DateTime.UtcNow.AddSeconds(DesktopProtocolConstants.ElementTokenLifetimeSeconds));
        return new DesktopElementInfo(
            token,
            name,
            type,
            value,
            bounds,
            true,
            false,
            canInvoke,
            canSetValue,
            canScroll,
            depth);
    }

    private TokenRecord? ResolveToken(string? token, string stateId)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        if (!_tokens.TryGetValue(token, out var record)
            || record.ExpiresUtc < DateTime.UtcNow
            || !string.Equals(record.StateId, stateId, StringComparison.Ordinal))
            throw new DesktopHostFaultException("stale_element", "Desktop element token expired or belongs to another observation.");
        return record;
    }

    private void RequireElementOrCoordinate(
        TokenRecord? token,
        DesktopActionRequest request,
        string expectedKey)
    {
        if (token is not null)
        {
            if (token.Key != expectedKey)
                throw new DesktopHostFaultException("invalid_target", "Observed element token does not match this action.");
            return;
        }

        var x = request.X ?? throw new DesktopHostFaultException("invalid_request", "Observed coordinate X is required.");
        var y = request.Y ?? throw new DesktopHostFaultException("invalid_request", "Observed coordinate Y is required.");
        var button = new DesktopBounds(_bounds.X + 20, _bounds.Y + 40, 120, 36);
        if (!button.Contains(x, y))
            throw new DesktopHostFaultException("invalid_target", "Observed coordinate is outside the fixture button.");
    }

    private string CurrentStateId(string screenshotSha)
        => DesktopHostState.StableToken(new
        {
            SessionId,
            _bounds,
            _text,
            _status,
            _scroll,
            screenshotSha
        });

    private byte[] RenderScreenshot()
    {
        using var bitmap = new Bitmap(Math.Max(1, _bounds.Width), Math.Max(1, _bounds.Height));
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        graphics.DrawRectangle(Pens.Black, 20, 40, 120, 36);
        graphics.DrawString("Increment", SystemFonts.DefaultFont, Brushes.Black, 28, 50);
        graphics.DrawRectangle(Pens.Gray, 20, 100, 260, 32);
        graphics.DrawString(_text, SystemFonts.DefaultFont, Brushes.Black, 24, 108);
        graphics.DrawString(_status, SystemFonts.DefaultFont, Brushes.Black, 20, 160);
        graphics.DrawString("scroll=" + _scroll, SystemFonts.DefaultFont, Brushes.Black, 320, 50);

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        var bytes = stream.ToArray();
        if (bytes.Length > DesktopProtocolConstants.MaxScreenshotBytes)
            throw new DesktopHostFaultException("screenshot_too_large", "Fixture screenshot exceeds protocol limit.");
        return bytes;
    }

    private void RequireSession(string sessionId)
    {
        if (!string.Equals(sessionId, SessionId, StringComparison.Ordinal))
            throw new DesktopHostFaultException("session_not_found", "Desktop fixture window session is not available.");
    }

    private static int ParseCount(string status)
        => int.TryParse(status[(status.LastIndexOf(':') + 1)..], out var value) ? value : 0;

    private sealed record TokenRecord(string Key, string StateId, DateTime ExpiresUtc);
}

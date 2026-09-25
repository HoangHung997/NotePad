using H2AgentLab.DesktopProtocol;

namespace H2AgentLab.Desktop;

public sealed record DesktopWindowChoice(DesktopWindowInfo Window)
{
    public override string ToString()
        => Window.Title + " · " + Window.ProcessName + " · PID " + Window.ProcessId;
}

public static class DesktopHostLocator
{
    public const string EnvironmentVariable = "H2_AGENT_DESKTOP_HOST";

    public static string ResolveExecutable()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var explicitPath = Path.GetFullPath(configured);
            if (CompleteHelper(explicitPath))
                return explicitPath;
            throw new FileNotFoundException(
                "Configured DesktopHost executable was not found.",
                explicitPath);
        }

        var fileName = "H2AgentLab.DesktopHost.exe";
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(AppContext.BaseDirectory, "DesktopHost", fileName),
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "H2AgentLab.DesktopHost",
                "bin",
                "Release",
                "net10.0-windows",
                fileName)),
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "H2AgentLab.DesktopHost",
                "bin",
                "Debug",
                "net10.0-windows",
                fileName))
        };

        var found = candidates.FirstOrDefault(CompleteHelper);
        if (found is not null)
            return found;

        throw new global::H2AgentLab.AgentFaultException(
            "unavailable",
            "DesktopHost chưa có cạnh H2 Agent Lab. Build/copy H2AgentLab.DesktopHost hoặc đặt "
            + EnvironmentVariable + " tới executable đã được tin cậy.",
            recoverable: false);
    }

    public static DesktopHostClient CreateClient()
        => new(ResolveExecutable());

    public static DesktopHostClient CreateClientForApplicationLifecycle()
    {
        try
        {
            return CreateClient();
        }
        catch (global::H2AgentLab.AgentFaultException ex) when (ex.Code == "unavailable")
        {
            throw new global::H2AgentLab.AgentFaultException(
                "app_preflight_unavailable",
                "DesktopHost packaged helper is incomplete or unavailable before application launch.",
                false);
        }
    }

    private static bool CompleteHelper(string path)
        => File.Exists(path)
            && File.Exists(Path.ChangeExtension(path, ".dll"))
            && File.Exists(Path.ChangeExtension(path, ".deps.json"))
            && File.Exists(Path.ChangeExtension(path, ".runtimeconfig.json"));
}

/// <summary>
/// Selected-window compatibility surface backed entirely by the isolated DesktopHost process.
/// It preserves the narrow inspect/click/type contract while using DesktopHost state IDs,
/// short-lived element tokens, host policy and observe-after-act evidence.
/// </summary>
public sealed class SelectedDesktopWindowController : IDisposable
{
    private readonly DesktopHostClient _client;
    private readonly bool _ownsClient;
    private DesktopWindowInfo _target;
    private readonly DesktopWindowInfo _boundTarget;
    private DesktopObservation? _observation;
    private DateTime _observedUtc;
    private bool _disposed;

    public SelectedDesktopWindowController(
        DesktopWindowInfo target)
        : this(DesktopHostLocator.CreateClient(), target, ownsClient: true)
    {
    }

    public SelectedDesktopWindowController(
        DesktopHostClient client,
        DesktopWindowInfo target,
        bool ownsClient = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _boundTarget = target;
        _ownsClient = ownsClient;
    }

    public DesktopWindowInfo Target => _target;

    public static async Task<IReadOnlyList<DesktopWindowChoice>> ListAsync(
        CancellationToken cancellationToken)
    {
        using var client = DesktopHostLocator.CreateClient();
        var windows = await client.ListWindowsAsync(cancellationToken)
            .ConfigureAwait(false);
        return windows.Select(x => new DesktopWindowChoice(x)).ToArray();
    }

    public async Task<object> Inspect(
        Func<global::H2AgentLab.Approval, CancellationToken, Task<bool>> approve,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(approve);

        var approved = await approve(
            new global::H2AgentLab.Approval(
                "Đọc cửa sổ đã chọn",
                TargetLabel()
                + "\nNội dung UIA của đúng cửa sổ này có thể gửi tới model hiện chọn. "
                + "DesktopHost chặn cửa sổ nhạy cảm, ô mật khẩu và giới hạn cây UI."),
            cancellationToken).ConfigureAwait(false);
        if (!approved)
            throw new global::H2AgentLab.AgentFaultException(
                "denied",
                "Chưa được phép đọc cửa sổ.",
                recoverable: false);

        try
        {
            await ValidateDispatchTargetAsync(cancellationToken).ConfigureAwait(false);
            var observation = await _client.ObserveAsync(
                _target.SessionId,
                cancellationToken).ConfigureAwait(false);
            EnsureBoundTarget(observation.Window);
            _target = observation.Window;
            _observation = observation;
            _observedUtc = DateTime.UtcNow;

            return new
            {
                window = _target.Title,
                process = _target.ProcessName,
                sessionId = _target.SessionId,
                stateId = observation.StateId,
                controls = observation.Elements.Select(x => new
                {
                    x.Token,
                    name = x.Name,
                    type = x.ControlType,
                    x.Value,
                    x.CanInvoke,
                    x.CanSetValue
                }).ToArray(),
                maxAgeSeconds = DesktopProtocolConstants.ElementTokenLifetimeSeconds,
                truncatedAt = DesktopProtocolConstants.MaxElements,
                screenshotSha256 = observation.ScreenshotSha256
            };
        }
        catch (DesktopHostClientException ex)
        {
            throw Map(ex);
        }
    }

    public async Task<object> Act(
        string action,
        string token,
        string text,
        Func<global::H2AgentLab.Approval, CancellationToken, Task<bool>> approve,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(approve);
        text ??= "";

        var observed = _observation;
        if (observed is null
            || DateTime.UtcNow - _observedUtc
                > TimeSpan.FromSeconds(DesktopProtocolConstants.ElementTokenLifetimeSeconds))
        {
            _observation = null;
            throw new global::H2AgentLab.AgentFaultException(
                "stale_state",
                "Cần đọc lại cửa sổ; token UIA đã hết hạn.");
        }

        var control = observed.Elements.SingleOrDefault(x =>
            string.Equals(x.Token, token, StringComparison.Ordinal));
        if (control is null)
            throw new global::H2AgentLab.AgentFaultException(
                "stale_state",
                "Token điều khiển không thuộc lần quan sát hiện tại.");

        if (text.Length > 10_000)
            throw new IOException("Text quá dài.");

        var hostAction = action switch
        {
            "click_control" when control.CanInvoke => DesktopActionKinds.Click,
            "type_control" when control.CanSetValue => DesktopActionKinds.Type,
            "click_control" => throw new IOException(
                "Điều khiển không hỗ trợ Invoke; không tự đoán tọa độ."),
            "type_control" => throw new IOException(
                "Điều khiển không hỗ trợ SetValue hoặc là chỉ đọc."),
            _ => throw new ArgumentException(
                "Selected desktop controller supports only click_control/type_control.",
                nameof(action))
        };

        var preview = "Cửa sổ: " + TargetLabel()
            + "\nĐiều khiển: " + control.Name + " (" + control.ControlType + ")\n"
            + (hostAction == DesktopActionKinds.Type
                ? "Giá trị cũ: " + control.Value + "\nThay bằng: " + text
                : "Kích hoạt điều khiển đã quan sát. Thao tác có thể làm thay đổi dữ liệu trong ứng dụng.");

        var approved = await approve(
            new global::H2AgentLab.Approval(
                hostAction == DesktopActionKinds.Type
                    ? "Thay nội dung ô nhập"
                    : "Bấm điều khiển",
                preview),
            cancellationToken).ConfigureAwait(false);
        if (!approved)
            throw new global::H2AgentLab.AgentFaultException(
                "denied",
                "Người dùng từ chối.",
                recoverable: false);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ValidateDispatchTargetAsync(cancellationToken).ConfigureAwait(false);
            var result = await _client.ActAsync(
                new DesktopActionRequest(
                    _target.SessionId,
                    observed.StateId,
                    PermissionGranted: true,
                    hostAction,
                    ElementToken: token,
                    Text: hostAction == DesktopActionKinds.Type ? text : null),
                cancellationToken).ConfigureAwait(false);

            var after = await _client.ObserveAsync(
                _target.SessionId,
                cancellationToken).ConfigureAwait(false);
            EnsureBoundTarget(after.Window);
            DesktopEvidenceGate.EnsureMutationEvidence(result, after);
            _target = after.Window;
            _observation = null;

            var valueObserved = hostAction != DesktopActionKinds.Type
                || after.Elements.Any(x =>
                    string.Equals(x.Name, control.Name, StringComparison.Ordinal)
                    && string.Equals(x.ControlType, control.ControlType, StringComparison.Ordinal)
                    && string.Equals(x.Value, text, StringComparison.Ordinal));

            return new
            {
                action = hostAction,
                result.MutationId,
                mutationVerifiedByNewObservation = true,
                postStateId = after.StateId,
                valueObserved,
                verifiedTaskOutcome = false,
                next = "Inspect the resulting UI/task state before claiming business success."
            };
        }
        catch (DesktopHostClientException ex)
        {
            _observation = null;
            throw Map(ex);
        }
    }

    public static bool SameTarget(DesktopWindowInfo bound, DesktopWindowInfo observed)
        => bound.SessionId == observed.SessionId && bound.Handle == observed.Handle
            && bound.ProcessId == observed.ProcessId && bound.ProcessStartedUtcTicks > 0
            && bound.ProcessStartedUtcTicks == observed.ProcessStartedUtcTicks
            && string.Equals(bound.ProcessName, observed.ProcessName, StringComparison.OrdinalIgnoreCase);

    private async Task ValidateDispatchTargetAsync(CancellationToken ct)
    {
        var observed = await _client.ListWindowsAsync(ct).ConfigureAwait(false);
        if (observed.Count(w => SameTarget(_boundTarget, w)) != 1)
            throw new Tools.ToolPreflightException("stale_resource");
    }

    private void EnsureBoundTarget(DesktopWindowInfo current)
    {
        if (SameTarget(_boundTarget, current)) return;
        _observation = null;
        // Could be after a click/type; do not label this as no effect or permit automatic retry.
        throw new IOException("Desktop response changed the bound window/session. Reconcile before another action.");
    }

    private string TargetLabel()
        => _target.Title + " · " + _target.ProcessName + " · PID " + _target.ProcessId;

    private static Exception Map(DesktopHostClientException ex)
        => ex.Code switch
        {
            "stale_state" or "stale_element" or "session_not_found"
                => new global::H2AgentLab.AgentFaultException(
                    "stale_state",
                    ex.Message),
            "permission_denied"
                => new global::H2AgentLab.AgentFaultException(
                    "denied",
                    ex.Message,
                    recoverable: false),
            "sensitive_target"
                => new global::H2AgentLab.AgentFaultException(
                    "boundary",
                    ex.Message,
                    recoverable: false),
            "invalid_request" or "invalid_action" or "invalid_target"
                => new global::H2AgentLab.AgentFaultException(
                    "invalid_arguments",
                    ex.Message),
            _ => ex
        };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsClient)
            _client.Dispose();
    }
}

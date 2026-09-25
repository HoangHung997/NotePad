using System.Text.Json;
using H2AgentLab.Runtime;

namespace H2AgentLab.Integration;

/// <summary>
/// Verifies only host-observed desktop outcomes. App launch/activation can be certified from the
/// exact DesktopHost window identity returned after the action. Generic clicks still require a
/// later task-state inspection; text assignment requires a fresh observation of the new value.
/// </summary>
internal sealed class H2DesktopRuntimeVerifier : IAgentRuntimeDomainVerifier
{
    public string DomainId => "h2-desktop";

    public bool CanVerify(ToolCall call, string output)
        => call.Name is "click_control" or "type_control" or "launch_app" or "activate_app";

    public Task<AgentRuntimeDomainVerification> VerifyAsync(
        AgentRuntimeVerificationContext context,
        ToolCall call,
        string output,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;

        if (call.Name == "type_control")
        {
            var passed = root.TryGetProperty("mutationVerifiedByNewObservation", out var observed)
                && observed.ValueKind == JsonValueKind.True
                && root.TryGetProperty("valueObserved", out var value)
                && value.ValueKind == JsonValueKind.True
                && root.TryGetProperty("postStateId", out var state)
                && !string.IsNullOrWhiteSpace(state.GetString());
            return Task.FromResult(new AgentRuntimeDomainVerification(
                DomainId,
                passed,
                passed ? ["desktop-state:" + state.GetString()] : [],
                passed ? null : "Desktop text action has no verified resulting state. Inspect it before claiming success."));
        }

        if (call.Name is "launch_app" or "activate_app")
        {
            var observed = root.TryGetProperty("verifiedByHostObservation", out var hostObserved)
                && hostObserved.ValueKind == JsonValueKind.True;
            var hasWindowObject = root.TryGetProperty("window", out var window)
                && window.ValueKind == JsonValueKind.Object;
            var sessionId = hasWindowObject
                && window.TryGetProperty("session_id", out var session)
                ? session.GetString()
                : null;
            var hasWindow = hasWindowObject
                && !string.IsNullOrWhiteSpace(sessionId)
                && window.TryGetProperty("hwnd", out var hwnd)
                && hwnd.TryGetInt64(out var windowHandle)
                && windowHandle > 0
                && window.TryGetProperty("pid", out var pid)
                && pid.TryGetInt32(out var processId)
                && processId > 0
                && window.TryGetProperty("process_started_utc_ticks", out var started)
                && started.TryGetInt64(out var processStarted)
                && processStarted > 0
                && window.TryGetProperty("process", out var windowProcess)
                && !string.IsNullOrWhiteSpace(windowProcess.GetString());
            var semantic = call.Name == "launch_app"
                ? ((root.TryGetProperty("newWindowObserved", out var created) && created.ValueKind == JsonValueKind.True)
                    || (root.TryGetProperty("reusedExistingWindow", out var reused) && reused.ValueKind == JsonValueKind.True))
                    && root.TryGetProperty("process", out var launchedProcess)
                    && launchedProcess.ValueKind == JsonValueKind.String
                    && string.Equals(launchedProcess.GetString(), windowProcess.GetString(), StringComparison.OrdinalIgnoreCase)
                : hasWindowObject
                    && call.Arguments.TryGetProperty("session_id", out var requestedSession)
                    && requestedSession.ValueKind == JsonValueKind.String
                    && string.Equals(requestedSession.GetString(), sessionId, StringComparison.Ordinal)
                    && root.TryGetProperty("activated", out var activated)
                    && activated.ValueKind == JsonValueKind.True
                    && window.TryGetProperty("foreground", out var foreground)
                    && foreground.ValueKind == JsonValueKind.True;
            var passed = observed && hasWindow && semantic;
            return Task.FromResult(new AgentRuntimeDomainVerification(
                DomainId,
                passed,
                passed ? ["desktop-window:" + sessionId] : [],
                passed ? null : "DesktopHost did not verify the requested application window outcome."));
        }

        return Task.FromResult(new AgentRuntimeDomainVerification(
            DomainId,
            false,
            [],
            "Desktop click has no verified resulting task state. Inspect it before claiming success."));
    }
}

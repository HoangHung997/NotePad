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
            var observedMutation = root.TryGetProperty("mutationVerifiedByNewObservation", out var observed)
                && observed.ValueKind == JsonValueKind.True;
            var observedValue = root.TryGetProperty("valueObserved", out var value)
                && value.ValueKind == JsonValueKind.True;
            var stateId = root.TryGetProperty("postStateId", out var state)
                && state.ValueKind == JsonValueKind.String
                ? state.GetString()
                : null;
            var passed = observedMutation
                && observedValue
                && !string.IsNullOrWhiteSpace(stateId);
            return Task.FromResult(new AgentRuntimeDomainVerification(
                DomainId,
                passed,
                passed ? ["desktop-state:" + stateId] : [],
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
            var windowProcessName = hasWindowObject
                && window.TryGetProperty("process", out var windowProcess)
                && windowProcess.ValueKind == JsonValueKind.String
                ? windowProcess.GetString()
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
                && !string.IsNullOrWhiteSpace(windowProcessName);
            var semantic = call.Name == "launch_app"
                ? call.Arguments.TryGetProperty("application", out var requestedArgument)
                    && requestedArgument.ValueKind == JsonValueKind.String
                    && call.Arguments.TryGetProperty("mode", out var requestedModeArgument)
                    && requestedModeArgument.ValueKind == JsonValueKind.String
                    && root.TryGetProperty("requestedApplication", out var requestedApplication)
                    && requestedApplication.ValueKind == JsonValueKind.String
                    && string.Equals(
                        requestedArgument.GetString()?.Trim(),
                        requestedApplication.GetString()?.Trim(),
                        StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("requestedMode", out var requestedMode)
                    && requestedMode.ValueKind == JsonValueKind.String
                    && string.Equals(
                        requestedModeArgument.GetString(),
                        requestedMode.GetString(),
                        StringComparison.Ordinal)
                    && LaunchModeSatisfied(
                        requestedMode.GetString(),
                        root)
                    && root.TryGetProperty("application", out var applicationId)
                    && applicationId.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(applicationId.GetString())
                    && root.TryGetProperty("process", out var launchedProcess)
                    && launchedProcess.ValueKind == JsonValueKind.String
                    && string.Equals(launchedProcess.GetString(), windowProcessName, StringComparison.OrdinalIgnoreCase)
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

    private static bool LaunchModeSatisfied(string? mode, JsonElement root)
    {
        var created = root.TryGetProperty("newWindowObserved", out var createdValue)
            && createdValue.ValueKind == JsonValueKind.True;
        var reused = root.TryGetProperty("reusedExistingWindow", out var reusedValue)
            && reusedValue.ValueKind == JsonValueKind.True;
        return mode switch
        {
            "new_window" => created && !reused,
            "reuse_or_launch" => created || reused,
            _ => false
        };
    }
}

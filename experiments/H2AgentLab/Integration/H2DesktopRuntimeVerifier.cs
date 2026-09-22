using System.Text.Json;
using H2AgentLab.Runtime;

namespace H2AgentLab.Integration;

/// <summary>Accept only host-produced observation of the requested text value. Invoking an
/// arbitrary control does not prove a business outcome and remains unverified.</summary>
internal sealed class H2DesktopRuntimeVerifier : IAgentRuntimeDomainVerifier
{
    public string DomainId => "h2-desktop";
    public bool CanVerify(ToolCall call, string output) => call.Name is "click_control" or "type_control";
    public Task<AgentRuntimeDomainVerification> VerifyAsync(AgentRuntimeVerificationContext context,
        ToolCall call, string output, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        var passed = call.Name == "type_control" && root.TryGetProperty("mutationVerifiedByNewObservation", out var observed)
            && observed.ValueKind == JsonValueKind.True && root.TryGetProperty("valueObserved", out var value)
            && value.ValueKind == JsonValueKind.True && root.TryGetProperty("postStateId", out var state)
            && !string.IsNullOrWhiteSpace(state.GetString());
        return Task.FromResult(new AgentRuntimeDomainVerification(DomainId, passed,
            passed ? ["desktop-state:" + root.GetProperty("postStateId").GetString()] : [],
            passed ? null : "Desktop action has no verified resulting task state. Inspect it before claiming success."));
    }
}

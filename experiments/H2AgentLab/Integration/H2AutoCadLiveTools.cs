using System.Text.Json;
using H2AgentLab.Cad;
using H2AgentLab.Runtime;
using H2AgentLab.Tools;

namespace H2AgentLab.Integration;

/// <summary>
/// Production live-AutoCAD adapter over IAutoCadNativeBridge. The default bridge is external COM/ROT;
/// deterministic tests may inject the same typed bridge contract. Only the narrow AR-063 live matrix
/// is registered. General update_entity/plot/dynamic-block mutation remains unsupported here.
/// </summary>
internal sealed class H2AutoCadLiveTools(
    IAutoCadNativeBridge bridge,
    Func<bool> authorized) : IAgentRuntimeDomainVerifier, IDisposable
{
    public const string VerifierId = "autocad-live-readback";
    public string DomainId => VerifierId;
    private readonly IAutoCadNativeBridge _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    private readonly Func<bool> _authorized = authorized ?? throw new ArgumentNullException(nameof(authorized));
    private readonly Dictionary<string, AgentRuntimeDomainVerification> _reports = new(StringComparer.Ordinal);

    public void Register(ToolRegistry registry)
    {
        var executor = new AutoCadNativeToolExecutor(
            _bridge,
            (_, _) => ValueTask.FromResult(_authorized()));
        foreach (var descriptor in AutoCadProviderPolicy.BuildDescriptors(
            executor,
            providerVersion: "com-v1",
            supportedNames: AutoCadOperationMatrix.LiveComImplementedTools,
            providerId: "autocad-com-live",
            serverId: "external-com-rot",
            capabilityDescription: "Structured live AutoCAD external COM capability. Current PickFirst selection only for entity/attribute mutation."))
            registry.Register(descriptor);
    }

    public bool CanVerify(ToolCall call, string output)
        => call.Name == "autocad.update_attribute";

    public async Task<AgentRuntimeDomainVerification> VerifyAsync(
        AgentRuntimeVerificationContext context,
        ToolCall call,
        string output,
        CancellationToken cancellationToken)
    {
        if (_reports.TryGetValue(call.Id, out var cached)) return cached;
        try
        {
            var mutation = JsonSerializer.Deserialize<AutoCadMutationResult>(output)
                ?? throw new InvalidOperationException("AutoCAD live mutation returned no typed result.");
            var tag = Required(call, "attribute_tag");
            var value = Required(call, "value");
            var expectation = JsonSerializer.SerializeToElement(new
            {
                kind = "entity",
                entityHandle = mutation.After.Handle,
                entityStateToken = mutation.After.StateToken,
                expectation = new
                {
                    attributes = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [tag] = value
                    }
                }
            });
            var passed = await _bridge.VerifyAsync(
                mutation.After.DocumentSessionId,
                mutation.After.DocumentStateToken,
                expectation,
                cancellationToken).ConfigureAwait(false);
            var evidence = mutation.EvidenceId + ":readback:" + mutation.After.StateToken[..Math.Min(16, mutation.After.StateToken.Length)];
            return _reports[call.Id] = new(
                VerifierId,
                passed,
                passed ? [evidence] : [],
                passed ? null : "Live AutoCAD attribute readback did not match exact document/entity state.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            return _reports[call.Id] = new(VerifierId, false, [], "Live AutoCAD verification failed: " + ex.Message);
        }
    }

    private static string Required(ToolCall call, string name)
    {
        if (!call.Arguments.TryGetProperty(name, out var node)
            || node.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(node.GetString()))
            throw new ArgumentException("AutoCAD live call requires " + name + ".");
        return node.GetString()!;
    }

    public void Dispose() { }
}

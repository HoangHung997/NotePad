using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using H2AgentLab.Extensions;
using H2AgentLab.Providers;
using H2AgentLab.Skills;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Verification;

namespace H2AgentLab.FirstPartyExtensions.Calculator;

public sealed class CalculatorExtension : IAgentExtension
{
    public CalculatorExtension()
    {
        Provider = new CalculatorCapabilityProvider();
    }

    public CalculatorCapabilityProvider Provider { get; }

    public AgentExtensionMetadata Metadata { get; } = new(
        "calculator-extension",
        "1.0.0",
        "Calculator Extension",
        "Deterministic MB-60 fixture proving application-neutral extension registration.",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["category"] = "fixture",
            ["transport"] = "in-process"
        });

    public void Register(AgentExtensionRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        registration.RegisterTool(BuildAddTool());
        registration.RegisterSkillSource(new CalculatorSkillSource());
        registration.RegisterVerifier(new CalculatorArtifactVerifier());
        registration.RegisterProvider(Provider);
    }

    private static ToolDescriptor BuildAddTool()
    {
        const string name = "calculator.add";
        const string description = "Add two numbers deterministically.";
        return new ToolDescriptor(
            name,
            new ToolNamespace("calculator", "Deterministic arithmetic tools."),
            description,
            AgentToolRisk.Low,
            AgentToolAccess.ReadOnly,
            supportsParallel: true,
            schemaVersion: "1.0.0",
            callableSchema: JsonSerializer.SerializeToElement(new
            {
                type = "function",
                function = new
                {
                    name,
                    description,
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            left = new { type = "number" },
                            right = new { type = "number" }
                        },
                        required = new[] { "left", "right" },
                        additionalProperties = false
                    }
                }
            }),
            executor: new DelegatingToolExecutor(
                "calculator-extension",
                static (call, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    var left = Number(call.Arguments, "left");
                    var right = Number(call.Arguments, "right");
                    return ValueTask.FromResult(JsonSerializer.Serialize(new
                    {
                        left,
                        right,
                        sum = left + right
                    }));
                }),
            provenance: new ToolProvenance(
                "calculator.extension",
                "1.0.0",
                "inproc",
                "1.0.0"));
    }

    internal static double Number(JsonElement arguments, string property)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(property, out var node)
            || node.ValueKind != JsonValueKind.Number
            || !node.TryGetDouble(out var value)
            || double.IsNaN(value)
            || double.IsInfinity(value))
            throw new ArgumentException(
                $"Calculator tool requires finite numeric '{property}'.");
        return value;
    }
}

public sealed class CalculatorSkillSource : ISkillSource
{
    private const string Content =
        "# Calculator\nUse calculator.add for deterministic addition. Verify observed numeric output.";
    private static readonly string Hash = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Content))).ToLowerInvariant();

    private static readonly SkillSummary Summary = new(
        new SkillIdentity(
            SkillSourceKind.BuiltIn,
            "calculator-extension",
            null,
            null,
            "calculator-basics",
            Hash),
        "calculator-basics",
        "Deterministic addition guidance for calculator extension tools.",
        "installed",
        "first-party-extension");

    public string SourceId => "calculator-extension";
    public SkillSourceKind SourceKind => SkillSourceKind.BuiltIn;

    public IReadOnlyList<SkillSummary> Search(string query, int maxResults = 20)
    {
        if (maxResults is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maxResults));
        return BuiltInSkillSource.Rank([Summary], query, maxResults);
    }

    public SkillContent Read(SkillIdentity identity)
    {
        EnsureIdentity(identity);
        return new SkillContent(
            Summary,
            Content,
            Array.Empty<string>());
    }

    public SkillResourceContent ReadResource(
        SkillIdentity identity,
        string relativePath)
    {
        EnsureIdentity(identity);
        throw new FileNotFoundException(
            $"Calculator skill has no resource '{relativePath}'.");
    }

    private static void EnsureIdentity(SkillIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity != Summary.Identity)
            throw new InvalidOperationException(
                "Calculator skill identity does not match this source.");
    }
}

public sealed class CalculatorArtifactVerifier : IArtifactVerifier
{
    public const string Id = "calculator-result";
    public string VerifierId => Id;

    public bool CanVerify(ArtifactVerificationTarget target)
        => string.Equals(
            target.MediaType,
            "application/x-calculator-result",
            StringComparison.OrdinalIgnoreCase);

    public ValueTask<VerificationReport> VerifyAsync(
        ArtifactVerificationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var evidence = "calculator:verified:" + request.Target.ArtifactId;
        var criteria = request.CriterionIds
            .Select(id => new VerificationCriterionResult(
                id,
                VerificationCriterionStatus.Passed,
                [evidence]))
            .ToArray();
        return ValueTask.FromResult(
            new VerificationReport(
                VerifierId,
                criteria,
                [evidence]));
    }
}

public sealed class CalculatorCapabilityProvider : ICapabilityProvider
{
    private ProviderHealthState _health = new(
        ProviderHealthStatus.Disconnected,
        DateTime.UtcNow);

    public ProviderProvenance Provenance { get; } = new(
        "calculator.provider",
        "1.0.0",
        "calculator-inproc",
        "in-process");

    public ProviderHealthState Health => _health;
    public int ConnectCount { get; private set; }
    public int DisconnectCount { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConnectCount++;
        _health = new ProviderHealthState(
            ProviderHealthStatus.Ready,
            DateTime.UtcNow);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DisconnectCount++;
        _health = new ProviderHealthState(
            ProviderHealthStatus.Disconnected,
            DateTime.UtcNow);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProviderNamespaceSummary>> ListNamespacesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ProviderNamespaceSummary>>(
        [
            new ProviderNamespaceSummary(
                "calculator",
                "Deterministic arithmetic capability provider.",
                ["add", "arithmetic", "numbers"])
        ]);
    }

    public Task<IReadOnlyList<ProviderToolSummary>> ListToolSummariesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ProviderToolSummary>>(
        [
            new ProviderToolSummary(
                "calculator.add",
                "calculator",
                "Add two numbers deterministically.",
                AgentToolAccess.ReadOnly,
                AgentToolRisk.Low,
                SupportsParallel: true,
                "1.0.0",
                "calculator:read",
                "calculator",
                "1.0.0")
        ]);
    }

    public Task<IReadOnlyList<ProviderToolDefinition>> LoadToolDefinitionsAsync(
        IReadOnlyList<string> toolNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolNames);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ProviderToolDefinition>>(
            Array.Empty<ProviderToolDefinition>());
    }

    public Task<IReadOnlyList<ProviderResourceSummary>> ListResourcesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ProviderResourceSummary>>(
            Array.Empty<ProviderResourceSummary>());
    }

    public Task<string> ReadResourceAsync(
        string resourceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new KeyNotFoundException(
            $"Calculator provider resource '{resourceId}' is unavailable.");
    }

    public ValueTask<string> ExecuteToolAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(toolName, "calculator.add", StringComparison.Ordinal))
            throw new KeyNotFoundException(
                $"Calculator provider tool '{toolName}' is unavailable.");
        var left = CalculatorExtension.Number(arguments, "left");
        var right = CalculatorExtension.Number(arguments, "right");
        return ValueTask.FromResult(
            JsonSerializer.Serialize(new
            {
                left,
                right,
                sum = left + right
            }));
    }

    public ValueTask DisposeAsync()
    {
        _health = new ProviderHealthState(
            ProviderHealthStatus.Disconnected,
            DateTime.UtcNow);
        return ValueTask.CompletedTask;
    }
}

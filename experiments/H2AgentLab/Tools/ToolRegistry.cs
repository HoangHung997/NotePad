using System.Text.Json;
using H2AgentLab.Tasking;

namespace H2AgentLab.Tools;

public enum AgentToolAccess
{
    ReadOnly = 0,
    Mutating = 1
}

public enum AgentToolRisk
{
    Low = 0,
    Medium = 1,
    High = 2
}

public interface IAgentToolExecutor
{
    string ExecutorId { get; }
    ValueTask<string> ExecuteAsync(global::H2AgentLab.ToolCall call, CancellationToken cancellationToken);
}

public sealed class DelegatingToolExecutor : IAgentToolExecutor
{
    private readonly Func<global::H2AgentLab.ToolCall, CancellationToken, ValueTask<string>> _execute;

    public DelegatingToolExecutor(
        string executorId,
        Func<global::H2AgentLab.ToolCall, CancellationToken, ValueTask<string>> execute)
    {
        ExecutorId = ToolNamespace.NormalizeId(executorId, nameof(executorId));
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public string ExecutorId { get; }

    public ValueTask<string> ExecuteAsync(
        global::H2AgentLab.ToolCall call,
        CancellationToken cancellationToken)
        => _execute(call, cancellationToken);
}

public sealed record ToolProvenance(
    string ProviderId,
    string ProviderVersion,
    string? ServerId,
    string ToolVersion);

public enum ToolInteractionFidelity
{
    Structured = 0,
    Accessibility = 1,
    Visual = 2,
    EscapeHatch = 3,
    Unspecified = 4
}

public sealed record ToolPreferenceMetadata
{
    public ToolPreferenceMetadata(
        string capabilityFamily,
        ToolInteractionFidelity interactionFidelity,
        bool explicitRequestOnly = false,
        IEnumerable<string>? explicitRequestTerms = null)
    {
        CapabilityFamily = ToolNamespace.NormalizeId(
            capabilityFamily,
            nameof(capabilityFamily));
        if (!Enum.IsDefined(interactionFidelity))
            throw new ArgumentOutOfRangeException(nameof(interactionFidelity));
        InteractionFidelity = interactionFidelity;
        ExplicitRequestOnly = explicitRequestOnly;

        var terms = (explicitRequestTerms ?? Array.Empty<string>())
            .Select(x => (x ?? "").Trim().ToLowerInvariant())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (terms.Any(x => x.Length > 128 || x.Any(char.IsControl)))
            throw new ArgumentException(
                "Explicit request terms must be <=128 characters and contain no control characters.",
                nameof(explicitRequestTerms));
        if (explicitRequestOnly && terms.Length == 0)
            throw new ArgumentException(
                "Explicit-only preference metadata requires at least one request term.",
                nameof(explicitRequestTerms));
        ExplicitRequestTerms = Array.AsReadOnly(terms);
    }

    public string CapabilityFamily { get; }
    public ToolInteractionFidelity InteractionFidelity { get; }
    public bool ExplicitRequestOnly { get; }
    public IReadOnlyList<string> ExplicitRequestTerms { get; }

    public bool MatchesExplicitRequest(string query)
    {
        query ??= "";
        var normalized = query.ToLowerInvariant();
        return ExplicitRequestTerms.Any(term =>
            normalized.Contains(term, StringComparison.Ordinal));
    }
}

public sealed record ToolResourceScope(
    string ScopeId,
    string ResourcePattern);

public sealed record ToolNamespace
{
    public ToolNamespace(string name, string description)
    {
        Name = NormalizeId(name, nameof(name));
        Description = NormalizeText(description, nameof(description), 1_000);
    }

    public string Name { get; }
    public string Description { get; }

    internal static string NormalizeId(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 64
            || normalized.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException("Identifier must be <=64 chars using ASCII letters, digits, '.', '-' or '_'.", parameterName);
        return normalized;
    }

    internal static string NormalizeText(string? value, string parameterName, int max)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > max)
            throw new ArgumentException($"Text exceeds {max} characters.", parameterName);
        return normalized;
    }
}

public sealed record ToolDescriptor
{
    public ToolDescriptor(
        string name,
        ToolNamespace toolNamespace,
        string description,
        AgentToolRisk risk,
        AgentToolAccess access,
        bool supportsParallel,
        string schemaVersion,
        JsonElement callableSchema,
        IAgentToolExecutor executor,
        ToolProvenance? provenance = null,
        ToolResourceScope? resourceScope = null,
        string? serializationKey = null,
        bool canProvideVerificationEvidence = false,
        ToolPreferenceMetadata? preference = null)
    {
        Name = ToolNamespace.NormalizeId(name, nameof(name));
        Namespace = toolNamespace ?? throw new ArgumentNullException(nameof(toolNamespace));
        Description = ToolNamespace.NormalizeText(description, nameof(description), 2_000);
        if (!Enum.IsDefined(risk)) throw new ArgumentOutOfRangeException(nameof(risk));
        if (!Enum.IsDefined(access)) throw new ArgumentOutOfRangeException(nameof(access));
        Risk = risk;
        Access = access;
        SupportsParallel = supportsParallel;
        SchemaVersion = ToolNamespace.NormalizeId(schemaVersion, nameof(schemaVersion));
        if (callableSchema.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Callable schema must be a JSON object.", nameof(callableSchema));
        CallableSchema = callableSchema.Clone();
        Executor = executor ?? throw new ArgumentNullException(nameof(executor));
        Provenance = provenance;
        ResourceScope = resourceScope;
        SerializationKey = string.IsNullOrWhiteSpace(serializationKey)
            ? null
            : ToolNamespace.NormalizeId(serializationKey, nameof(serializationKey));
        CanProvideVerificationEvidence = canProvideVerificationEvidence;
        Preference = preference;
    }

    public string Name { get; }
    public ToolNamespace Namespace { get; }
    public string Description { get; }
    public AgentToolRisk Risk { get; }
    public AgentToolAccess Access { get; }
    public bool SupportsParallel { get; }
    public string SchemaVersion { get; }
    public JsonElement CallableSchema { get; }
    public IAgentToolExecutor Executor { get; }
    public ToolProvenance? Provenance { get; }
    public ToolResourceScope? ResourceScope { get; }
    public string? SerializationKey { get; }
    public bool CanProvideVerificationEvidence { get; }
    public ToolPreferenceMetadata? Preference { get; }
    public bool IsMutating => Access == AgentToolAccess.Mutating;
}

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolNamespace> _namespaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolDescriptor> _tools = new(StringComparer.Ordinal);
    private long _version;

    public long Version => _version;
    public IReadOnlyList<ToolNamespace> Namespaces => _namespaces.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
    public IReadOnlyList<ToolDescriptor> Tools => _tools.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();

    public void RegisterNamespace(ToolNamespace toolNamespace)
    {
        ArgumentNullException.ThrowIfNull(toolNamespace);
        if (_namespaces.TryGetValue(toolNamespace.Name, out var existing))
        {
            if (existing != toolNamespace)
                throw new InvalidOperationException($"Tool namespace '{toolNamespace.Name}' is already registered with different metadata.");
            return;
        }
        _namespaces.Add(toolNamespace.Name, toolNamespace);
        _version++;
    }

    public void Register(ToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        RegisterNamespace(descriptor.Namespace);
        if (_tools.ContainsKey(descriptor.Name))
            throw new InvalidOperationException($"Tool '{descriptor.Name}' is already registered.");
        _tools.Add(descriptor.Name, descriptor);
        _version++;
    }

    public bool TryGet(string name, out ToolDescriptor descriptor)
        => _tools.TryGetValue(ToolNamespace.NormalizeId(name, nameof(name)), out descriptor!);

    public IReadOnlyList<ToolDescriptor> GetNamespace(string name)
    {
        var normalized = ToolNamespace.NormalizeId(name, nameof(name));
        return _tools.Values
            .Where(x => x.Namespace.Name == normalized)
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public int UnregisterWhere(Func<ToolDescriptor, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var names = _tools.Values
            .Where(predicate)
            .Select(x => x.Name)
            .ToArray();
        foreach (var name in names)
            _tools.Remove(name);

        if (names.Length > 0)
        {
            var usedNamespaces = _tools.Values
                .Select(x => x.Namespace.Name)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var key in _namespaces.Keys.Where(x => !usedNamespaces.Contains(x)).ToArray())
                _namespaces.Remove(key);
            _version++;
        }
        return names.Length;
    }
}

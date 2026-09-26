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
        ToolPreferenceMetadata? preference = null,
        ToolReadiness? readiness = null,
        ToolContractLimits? limits = null,
        IReadOnlyList<string>? dependencies = null,
        IReadOnlyList<string>? supportedOperations = null,
        ToolResultFormat resultFormat = ToolResultFormat.Auto,
        Func<global::H2AgentLab.ToolCall, ToolExecutionOutput?>? preflight = null,
        Func<ToolReadiness>? readinessSnapshot = null)
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
        Readiness = readiness ?? new(ToolReadinessState.Ready);
        Limits = limits ?? new();
        if (!Enum.IsDefined(Readiness.State) || !Enum.IsDefined(resultFormat)
            || Limits.MaxOutputCharacters is < 1_024 or > ToolOutcomeBridge.MaxModelOutputCharacters
            || Limits.MaxBatchItems is < 1)
            throw new ArgumentException("Invalid tool readiness or limits.");
        Dependencies = Array.AsReadOnly((dependencies ?? []).Select(d => ToolNamespace.NormalizeId(d, nameof(dependencies))).Distinct().ToArray());
        SupportedOperations = Array.AsReadOnly((supportedOperations ?? [Name]).Select(o => ToolNamespace.NormalizeId(o, nameof(supportedOperations))).Distinct().ToArray());
        ResultFormat = resultFormat;
        Preflight = preflight;
        ReadinessSnapshot = readinessSnapshot;
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
    public ToolReadiness Readiness { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<global::H2AgentLab.ToolCall, ToolExecutionOutput?>? Preflight { get; }
    // Host-owned, cheap cached/in-memory readiness only. Never connect or launch apps here.
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<ToolReadiness>? ReadinessSnapshot { get; }
    public ToolReadiness CurrentReadiness
    {
        get
        {
            if (ReadinessSnapshot is null) return Readiness;
            try
            {
                var snapshot = ReadinessSnapshot();
                return snapshot is not null && Enum.IsDefined(snapshot.State) ? snapshot : new(ToolReadinessState.Unavailable);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            { return new(ToolReadinessState.Unavailable); }
        }
    }
    public string EffectClass => IsMutating ? "may_change_resource" : "observational";
    public ToolContractLimits Limits { get; }
    public IReadOnlyList<string> Dependencies { get; }
    public IReadOnlyList<string> SupportedOperations { get; }
    public ToolResultFormat ResultFormat { get; }
    public string OutputSchemaVersion => "h2-outcome-v1";
    public bool IsMutating => Access == AgentToolAccess.Mutating;
}

public sealed class ToolRegistry
{
    private readonly object _gate = new();
    private Dictionary<string, ToolNamespace> _namespaces = new(StringComparer.Ordinal);
    private Dictionary<string, ToolDescriptor> _tools = new(StringComparer.Ordinal);
    private long _version;
    // Non-callable notices share the registry but never acquire an executor.
    private readonly Dictionary<string, ToolCapabilityNotice> _notices = new(StringComparer.Ordinal);
    public IReadOnlyList<ToolCapabilityNotice> CapabilityNotices
    { get { lock (_gate) return _notices.Values.OrderBy(n => n.Name, StringComparer.Ordinal).ToArray(); } }

    public void RegisterCapabilityNotice(ToolCapabilityNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var name = ToolNamespace.NormalizeId(notice.Name, nameof(notice));
        if (notice.Readiness is null || !Enum.IsDefined(notice.Readiness.State) || notice.Readiness.CanExecute)
            throw new ArgumentException("A capability notice cannot advertise an executable tool.");
        var validated = notice with { Name = name,
            Description = ToolNamespace.NormalizeText(notice.Description, nameof(notice), 512) };
        lock (_gate) { _notices[name] = validated; _version++; }
    }

    public void SetReadiness(string name, ToolReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        if (!Enum.IsDefined(readiness.State)) throw new ArgumentException("Invalid readiness.");
        lock (_gate)
        {
            if (!TryGet(name, out var descriptor)) throw new KeyNotFoundException("Tool is not registered.");
            _tools[descriptor.Name] = descriptor with { Readiness = readiness };
            _version++;
        }
    }

    public long Version { get { lock (_gate) return _version; } }
    public IReadOnlyList<ToolNamespace> Namespaces
    { get { lock (_gate) return _namespaces.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray(); } }
    public IReadOnlyList<ToolDescriptor> Tools
    { get { lock (_gate) return _tools.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray(); } }

    public void RegisterNamespace(ToolNamespace toolNamespace)
    {
        ArgumentNullException.ThrowIfNull(toolNamespace);
        lock (_gate)
        {
            ValidateNamespace(_namespaces, toolNamespace);
            if (_namespaces.TryAdd(toolNamespace.Name, toolNamespace)) _version++;
        }
    }

    public void Register(ToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (_gate)
        {
            // Validate both indexes before changing either of them.
            if (_tools.ContainsKey(descriptor.Name))
                throw new InvalidOperationException($"Tool '{descriptor.Name}' is already registered.");
            ValidateNamespace(_namespaces, descriptor.Namespace);
            if (_namespaces.TryAdd(descriptor.Namespace.Name, descriptor.Namespace)) _version++;
            _tools.Add(descriptor.Name, descriptor);
            _version++;
        }
    }

    /// <summary>Validate the complete replacement before publishing either registry index.
    /// The optional host commit runs only after validation; failure leaves the old registry intact.
    /// Existing descriptor objects remain immutable. This does not itself revoke captured executors.</summary>
    public void ReplaceWhere(Func<ToolDescriptor, bool> predicate,
        IReadOnlyList<ToolDescriptor> replacements, Action? commit = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(replacements);
        var incoming = replacements.ToArray();
        if (incoming.Any(x => x is null)) throw new ArgumentException("Replacement contains null.");
        lock (_gate)
        {
            var removed = _tools.Values.Where(predicate).ToArray();
            var next = new Dictionary<string, ToolDescriptor>(_tools, StringComparer.Ordinal);
            foreach (var old in removed) next.Remove(old.Name);
            var namespaces = new Dictionary<string, ToolNamespace>(_namespaces, StringComparer.Ordinal);
            foreach (var name in removed.Select(x => x.Namespace.Name).Distinct(StringComparer.Ordinal))
                if (!next.Values.Any(x => x.Namespace.Name == name)) namespaces.Remove(name);
            foreach (var descriptor in incoming)
            {
                if (!next.TryAdd(descriptor.Name, descriptor))
                    throw new InvalidOperationException($"Tool '{descriptor.Name}' is already registered.");
                ValidateNamespace(namespaces, descriptor.Namespace);
                namespaces.TryAdd(descriptor.Namespace.Name, descriptor.Namespace);
            }
            commit?.Invoke();
            _tools = next;
            _namespaces = namespaces;
            if (removed.Length > 0 || incoming.Length > 0) _version++;
        }
    }

    private static void ValidateNamespace(Dictionary<string, ToolNamespace> namespaces, ToolNamespace value)
    {
        if (namespaces.TryGetValue(value.Name, out var existing) && existing != value)
            throw new InvalidOperationException($"Tool namespace '{value.Name}' is already registered with different metadata.");
    }

    public bool TryGet(string name, out ToolDescriptor descriptor)
    {
        descriptor = null!;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var normalized = name.Trim().ToLowerInvariant();
        if (normalized.Length > 64 || normalized.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))) return false;
        lock (_gate) return _tools.TryGetValue(normalized, out descriptor!);
    }

    public IReadOnlyList<ToolDescriptor> GetNamespace(string name)
    {
        var normalized = ToolNamespace.NormalizeId(name, nameof(name));
        lock (_gate) return _tools.Values.Where(x => x.Namespace.Name == normalized)
            .OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
    }

    public int UnregisterWhere(Func<ToolDescriptor, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        lock (_gate)
        {
            var names = _tools.Values.Where(predicate).Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
            ReplaceWhere(x => names.Contains(x.Name), Array.Empty<ToolDescriptor>());
            return names.Count;
        }
    }
}

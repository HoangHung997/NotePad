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
        IAgentToolExecutor executor)
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
}

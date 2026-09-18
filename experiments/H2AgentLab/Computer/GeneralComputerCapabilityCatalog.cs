using H2AgentLab.Tools;

namespace H2AgentLab.Computer;

/// <summary>
/// Compatibility projection over ToolRegistry. It no longer defines the universe of computer
/// capabilities; first-party/provider extensions own their own registrations.
/// </summary>
public static class GeneralComputerCapabilityCatalog
{
    private const string ComputerProviderPrefix = "firstparty.computer.";

    public static IReadOnlyList<ToolDescriptor> All(ToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Tools
            .Where(IsComputerCapability)
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<ToolDescriptor> Namespace(
        ToolRegistry registry,
        string name)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = ToolNamespace.NormalizeId(name, nameof(name));
        return All(registry)
            .Where(x => x.Namespace.Name == normalized)
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public static bool ContainsMonolithicUnsafeControl(ToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return All(registry).Any(x =>
            x.Name.Contains(
                "control_computer",
                StringComparison.OrdinalIgnoreCase)
            || x.Name.Contains(
                "run_anything",
                StringComparison.OrdinalIgnoreCase)
            || x.Name.Contains(
                "arbitrary_command",
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsComputerCapability(ToolDescriptor descriptor)
        => descriptor.Provenance?.ProviderId.StartsWith(
            ComputerProviderPrefix,
            StringComparison.Ordinal) == true;
}

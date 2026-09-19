using H2AgentLab.Cad;
using H2AgentLab.Extensions;
using H2AgentLab.Tools;

namespace H2AgentLab.FirstPartyExtensions.Cad;

public sealed class AutoCadFirstPartyExtension : IAgentExtension
{
    private readonly IAgentToolExecutor _executor;
    private readonly string _providerVersion;

    public AutoCadFirstPartyExtension(
        IAgentToolExecutor executor,
        string providerVersion = "1.0.0")
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _providerVersion = ToolNamespace.NormalizeId(
            providerVersion,
            nameof(providerVersion));
    }

    public AutoCadFirstPartyExtension(
        IAutoCadNativeBridge bridge,
        Func<string, CancellationToken, ValueTask<bool>> authorizeMutation,
        string providerVersion = "1.0.0")
        : this(
            new AutoCadNativeToolExecutor(
                bridge ?? throw new ArgumentNullException(nameof(bridge)),
                authorizeMutation ?? throw new ArgumentNullException(nameof(authorizeMutation))),
            providerVersion)
    {
    }

    public AgentExtensionMetadata Metadata => new(
        "autocad-first-party-extension",
        _providerVersion,
        "AutoCAD Extension",
        "Reference card over the typed native-plugin AutoCAD provider contract.");

    public void Register(AgentExtensionRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        foreach (var descriptor in AutoCadProviderPolicy.BuildDescriptors(
            _executor,
            _providerVersion))
            registration.RegisterTool(descriptor);
    }
}

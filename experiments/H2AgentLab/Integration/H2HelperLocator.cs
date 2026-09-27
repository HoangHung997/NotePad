namespace H2AgentLab.Integration;

internal static class H2HelperLocator
{
    public static bool IsPackaged(string helper)
        => File.Exists(Path.Combine(AppContext.BaseDirectory, helper + ".exe"))
            && File.Exists(Path.Combine(AppContext.BaseDirectory, helper + ".dll"))
            && File.Exists(Path.Combine(AppContext.BaseDirectory, helper + ".runtimeconfig.json"));

    public static string Resolve(string helper)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, helper + ".exe");
        if (!File.Exists(executable) || !File.Exists(Path.ChangeExtension(executable, ".dll"))
            || !File.Exists(Path.Combine(AppContext.BaseDirectory, helper + ".runtimeconfig.json")))
            throw new H2AgentLab.Tools.ToolPreflightException("provider_unavailable");
        return executable;
    }
}

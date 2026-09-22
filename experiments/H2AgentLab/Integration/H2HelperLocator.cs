namespace H2AgentLab.Integration;

internal static class H2HelperLocator
{
    public static string Resolve(string helper)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, helper + ".exe");
        if (!File.Exists(executable) || !File.Exists(Path.ChangeExtension(executable, ".dll"))
            || !File.Exists(Path.Combine(AppContext.BaseDirectory, helper + ".runtimeconfig.json")))
            throw new IOException("Thành phần " + helper + " chưa được đóng gói đầy đủ cạnh ứng dụng.");
        return executable;
    }
}

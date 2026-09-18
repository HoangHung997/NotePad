using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace H2AgentLab.DesktopHost;

internal static class DesktopHostState
{
    public static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string StableToken<T>(T value)
        => Sha256(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    public static string SessionId(params string[] parts)
        => "desktop-" + Sha256(Encoding.UTF8.GetBytes(string.Join("\n", parts)))[..24];

    public static string MutationId(long sequence)
        => "mutation-" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N")[..12];
}

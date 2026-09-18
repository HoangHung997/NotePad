using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace H2AgentLab.OfficeHost;

internal static class OfficeHostSafety
{
    public static void RequirePermission(bool granted)
    {
        if (!granted)
            throw new OfficeHostFaultException("permission_denied", "The requested Office mutation was not approved.");
    }

    public static void RequireState(string supplied, string current)
    {
        if (string.IsNullOrWhiteSpace(supplied) || !string.Equals(supplied, current, StringComparison.Ordinal))
            throw new OfficeHostFaultException("stale_state", "Office state changed since the caller observed it. Read a fresh snapshot before mutating.");
    }

    public static string ValidateCopyDestination(string destinationPath, string? sourcePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (destinationPath.Any(char.IsControl)
            || !Path.IsPathFullyQualified(destinationPath)
            || destinationPath.StartsWith(@"\\", StringComparison.Ordinal)
            || destinationPath.StartsWith("//", StringComparison.Ordinal))
            throw new OfficeHostFaultException("invalid_destination", "Save-copy destination must be an absolute local path.");

        var full = Path.GetFullPath(destinationPath);
        if (!string.IsNullOrWhiteSpace(sourcePath)
            && Path.IsPathFullyQualified(sourcePath)
            && string.Equals(
                full.TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            throw new OfficeHostFaultException("overwrite_blocked", "Acceptance save-copy must not overwrite the live original.");

        if (File.Exists(full) || Directory.Exists(full))
            throw new OfficeHostFaultException("destination_exists", "Save-copy destination already exists; refusing to overwrite it.");

        var parent = Path.GetDirectoryName(full);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            throw new OfficeHostFaultException("invalid_destination", "Save-copy destination directory does not exist.");

        return full;
    }

    public static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string StableToken<T>(T value)
        => Sha256(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    public static string StableSessionId(params string[] parts)
        => "office-" + Sha256(Encoding.UTF8.GetBytes(string.Join("
", parts)))[..24];
}

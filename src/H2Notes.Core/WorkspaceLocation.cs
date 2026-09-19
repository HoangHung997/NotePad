using System.Runtime.InteropServices;
using System.Text;

namespace H2Notes.Core;

public enum WorkspaceLocationKind
{
    LocalFixed,
    LocalRemovable,
    MappedNetwork,
    UncNetwork,
    UnsupportedOrUnknownNetworkProvider
}

public sealed record WorkspaceLocationInfo(
    string DisplayPath,
    string CanonicalPath,
    WorkspaceLocationKind Kind,
    string? ResolvedNetworkPath);

public sealed record WorkspaceLocationProfile(
    Guid? WorkspaceId,
    WorkspaceLocationKind Kind,
    string DisplayPath,
    string CanonicalPath,
    string? ResolvedNetworkPath,
    DateTime LastSuccessfulUtc);

public static class WorkspaceLocation
{
    public static WorkspaceLocationInfo Inspect(string path)
        => Inspect(path, DriveTypeForRoot, ResolveMappedDrive);

    // The injected delegates exist so path/network classification can be tested deterministically
    // without requiring a real mapped drive on the CI machine.
    public static WorkspaceLocationInfo Inspect(
        string path,
        Func<string, DriveType> driveTypeForRoot,
        Func<string, string?> mappedDriveResolver)
    {
        ArgumentNullException.ThrowIfNull(driveTypeForRoot);
        ArgumentNullException.ThrowIfNull(mappedDriveResolver);
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Workspace path is empty.", nameof(path));

        var full = Normalize(path);
        if (IsUnc(full))
            return new(full, NormalizeNetwork(full), WorkspaceLocationKind.UncNetwork, NormalizeNetwork(full));

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(root))
            return new(full, full, WorkspaceLocationKind.UnsupportedOrUnknownNetworkProvider, null);

        DriveType driveType;
        try { driveType = driveTypeForRoot(root); }
        catch { driveType = DriveType.Unknown; }

        if (driveType == DriveType.Network)
        {
            string? remote = null;
            try { remote = mappedDriveResolver(root); } catch { }
            if (!string.IsNullOrWhiteSpace(remote))
            {
                var relative = full[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var resolved = relative.Length == 0
                    ? NormalizeNetwork(remote)
                    : NormalizeNetwork(remote.TrimEnd('\\', '/') + "\\" + relative.Replace('/', '\\'));
                return new(full, resolved, WorkspaceLocationKind.MappedNetwork, resolved);
            }
            return new(full, full, WorkspaceLocationKind.MappedNetwork, null);
        }

        var kind = driveType switch
        {
            DriveType.Fixed => WorkspaceLocationKind.LocalFixed,
            DriveType.Removable => WorkspaceLocationKind.LocalRemovable,
            _ => WorkspaceLocationKind.UnsupportedOrUnknownNetworkProvider
        };
        return new(full, full, kind, null);
    }

    public static bool SameCanonicalLocation(WorkspaceLocationInfo left, WorkspaceLocationInfo right)
        => left.CanonicalPath.Equals(right.CanonicalPath, StringComparison.OrdinalIgnoreCase);

    private static DriveType DriveTypeForRoot(string root)
    {
        try { return new DriveInfo(root).DriveType; }
        catch { return DriveType.Unknown; }
    }

    private static string? ResolveMappedDrive(string root)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var localName = root.TrimEnd('\\', '/');
        if (localName.Length < 2 || localName[1] != ':') return null;
        localName = localName[..2];

        var capacity = 512;
        while (capacity <= 32_768)
        {
            var buffer = new StringBuilder(capacity);
            var length = capacity;
            var result = WNetGetConnection(localName, buffer, ref length);
            if (result == 0) return buffer.ToString();
            if (result != ErrorMoreData) return null;
            capacity = Math.Max(capacity * 2, length + 1);
        }
        return null;
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root) && full.Equals(root, StringComparison.OrdinalIgnoreCase))
            return root.TrimEnd(Path.AltDirectorySeparatorChar);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsUnc(string path)
        => path.StartsWith(@"\\", StringComparison.Ordinal);

    private static string NormalizeNetwork(string path)
    {
        var value = path.Replace('/', '\\');
        while (value.Length > 2 && value.EndsWith('\\')) value = value[..^1];
        return value;
    }

    private const int ErrorMoreData = 234;

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(
        string lpLocalName,
        StringBuilder lpRemoteName,
        ref int lpnLength);
}

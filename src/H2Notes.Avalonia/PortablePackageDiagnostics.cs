using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using H2AgentLab;
using H2AgentLab.Integration;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public sealed record H2PortableManifestFile(string Path,long Bytes,string Sha256);
public sealed record H2PortableManifest(
    int SchemaVersion,
    string SourceSha,
    string SourceBranch,
    string RuntimeIdentifier,
    bool SelfContained,
    string ProductVersion,
    int FileCount,
    long TotalBytes,
    string ContentSha256,
    IReadOnlyList<H2PortableManifestFile> Files);

public sealed record H2PortableManifestValidation(
    bool Passed,
    string? SourceSha,
    string? ContentSha256,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<string> Errors);

/// <summary>
/// Offline copy-and-run preflight. It validates the exact packaged bytes and reports dependency
/// readiness without contacting AI/search/browser endpoints or opening user Office/CAD resources.
/// </summary>
public static class PortablePackageDiagnostics
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<int> VerifyAsync(string output)
        => await VerifyAsync(AppContext.BaseDirectory, LocalConfiguration.SettingsDirectory, output).ConfigureAwait(false);

    public static async Task<int> VerifyAsync(string packageRoot,string settingsDirectory,string output)
    {
        packageRoot=Path.GetFullPath(packageRoot);
        settingsDirectory=Path.GetFullPath(settingsDirectory);
        output=Path.GetFullPath(output);
        if(Directory.Exists(output))throw new IOException("Choose a new verification directory.");
        Directory.CreateDirectory(output);

        var manifest=ValidateManifest(packageRoot);
        var dependencies=new List<H2PortableDependencyStatus>();
        try
        {
            dependencies.AddRange(H2PortableDependencyProbe.Capture());
            dependencies.AddRange(InspectConfiguredProviders(settingsDirectory,packageRoot));

            var python=Path.Combine(packageRoot,"python");
            var pythonReady=WindowsPythonSandbox.RuntimeRoot == Path.Combine(AppContext.BaseDirectory,"python")
                && SamePath(packageRoot,AppContext.BaseDirectory)
                    ? WindowsPythonSandbox.RuntimeProblem(python) is null
                    : File.Exists(Path.Combine(python,"python.exe"))
                      && File.Exists(Path.Combine(python,"h2-sandbox-manifest.json"));
            dependencies.Add(pythonReady
                ? new("python.sandbox","Ready","packaged","Bundled Agent Python runtime is present.")
                : new("python.sandbox","Unavailable","packaged_python_missing","Bundled Agent Python runtime is missing or incomplete."));

            var helpersPassed=false;
            if(dependencies.Where(x=>x.Id is "desktop.helper" or "office.helper").All(x=>x.State=="Ready")
                && SamePath(packageRoot,AppContext.BaseDirectory))
            {
                helpersPassed=await H2ProductionDiagnostics.VerifyHelpersAsync(Path.Combine(output,"helpers.json")).ConfigureAwait(false)==0;
            }
            else if(SamePath(packageRoot,AppContext.BaseDirectory))
            {
                await File.WriteAllTextAsync(Path.Combine(output,"helpers.json"),
                    JsonSerializer.Serialize(new{ok=false,error="packaged_helper_missing"},Json)).ConfigureAwait(false);
            }
            dependencies.Add(helpersPassed
                ? new("helpers.ipc","Ready","ipc_pass","Packaged DesktopHost and OfficeHost started and answered IPC.")
                : new("helpers.ipc","Unavailable","ipc_not_verified","Packaged helper IPC could not be verified."));

            var agentPassed=false;
            if(pythonReady && SamePath(packageRoot,AppContext.BaseDirectory))
                agentPassed=await LabEnvironment.Verify(Path.Combine(output,"agent")).ConfigureAwait(false)==0;
            dependencies.Add(agentPassed
                ? new("documents.python","Ready","portable_environment_pass","Packaged document/Python environment passed offline verification.")
                : new("documents.python","Unavailable","portable_environment_failed","Packaged document/Python environment did not pass offline verification."));

            var passed=manifest.Passed&&pythonReady&&helpersPassed&&agentPassed;
            var report=new
            {
                passed,
                manifest,
                package=new{
                    runtimeIdentifier="win-x64",
                    selfContained=manifest.Passed,
                    standardOcrModelsBundled=Directory.Exists(Path.Combine(packageRoot,"ocr-runtime","models")),
                    settingsProfileProvided=Environment.GetEnvironmentVariable(LocalConfiguration.SettingsDirectoryEnvironmentVariable) is {Length:>0}
                },
                dependencies,
                limits="Offline portable preflight only. No model/search/browser call, native Office/CAD document attach, personal document access, second-PC/NAS test, or E4 claim."
            };
            await File.WriteAllTextAsync(Path.Combine(output,"portable-check.json"),JsonSerializer.Serialize(report,Json)).ConfigureAwait(false);
            return passed?0:1;
        }
        catch(Exception ex)
        {
            var safe=SafeMessage(ex,packageRoot,settingsDirectory);
            await File.WriteAllTextAsync(Path.Combine(output,"portable-check.json"),
                JsonSerializer.Serialize(new{
                    passed=false,
                    manifest,
                    errorType=ex.GetType().Name,
                    error=safe,
                    dependencies,
                    limits="No secrets or document contents are included in this report."
                },Json)).ConfigureAwait(false);
            return 1;
        }
    }

    public static H2PortableManifestValidation ValidateManifest(string root)
    {
        var errors=new List<string>();
        root=Path.GetFullPath(root);
        var manifestPath=Path.Combine(root,"portable-manifest.json");
        if(!File.Exists(manifestPath))
            return new(false,null,null,0,0,["portable_manifest_missing"]);
        try
        {
            var info=new FileInfo(manifestPath);
            if(info.Length is <=0 or >8*1024*1024)
                return new(false,null,null,0,0,["portable_manifest_size_invalid"]);
            var manifest=JsonSerializer.Deserialize<H2PortableManifest>(File.ReadAllBytes(manifestPath),Json)
                ?? throw new InvalidDataException("portable_manifest_empty");
            if(manifest.SchemaVersion!=1)errors.Add("portable_manifest_schema");
            if(!IsSha(manifest.SourceSha,40))errors.Add("portable_source_sha_invalid");
            if(!string.Equals(manifest.RuntimeIdentifier,"win-x64",StringComparison.Ordinal))errors.Add("portable_runtime_identifier");
            if(!manifest.SelfContained)errors.Add("portable_not_self_contained");
            if(manifest.Files is null || manifest.Files.Count is <1 or >10_000)errors.Add("portable_manifest_file_count");
            if(manifest.Files is null)
                return new(false,manifest.SourceSha,manifest.ContentSha256,0,0,errors.AsReadOnly());

            var map=new Dictionary<string,H2PortableManifestFile>(StringComparer.OrdinalIgnoreCase);
            foreach(var entry in manifest.Files)
            {
                var path=NormalizeRelative(entry.Path);
                if(path is null){errors.Add("portable_manifest_path");continue;}
                if(!map.TryAdd(path,entry)){errors.Add("portable_manifest_duplicate_path");continue;}
                if(entry.Bytes<0||!IsSha(entry.Sha256,64))errors.Add("portable_manifest_entry_invalid");
                if(IsSensitivePortablePath(path))errors.Add("portable_contains_local_state:"+path);
            }

            var actual=Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)
                .Select(path=>Path.GetRelativePath(root,path).Replace('\\','/'))
                .Where(path=>!path.Equals("portable-manifest.json",StringComparison.OrdinalIgnoreCase))
                .OrderBy(path=>path,StringComparer.OrdinalIgnoreCase).ToArray();
            foreach(var path in actual)
                if(IsSensitivePortablePath(path))errors.Add("portable_contains_local_state:"+path);
            if(actual.Length!=map.Count||actual.Any(path=>!map.ContainsKey(path))
                ||map.Keys.Any(path=>!actual.Contains(path,StringComparer.OrdinalIgnoreCase)))
                errors.Add("portable_manifest_inventory_mismatch");

            long total=0;
            foreach(var pair in map.OrderBy(x=>x.Key,StringComparer.OrdinalIgnoreCase))
            {
                var full=Path.GetFullPath(Path.Combine(root,pair.Key.Replace('/',Path.DirectorySeparatorChar)));
                if(!IsInside(root,full)||!File.Exists(full)){errors.Add("portable_file_missing:"+pair.Key);continue;}
                if((File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0){errors.Add("portable_reparse_file:"+pair.Key);continue;}
                var size=new FileInfo(full).Length;
                total=checked(total+size);
                if(size!=pair.Value.Bytes)errors.Add("portable_size_mismatch:"+pair.Key);
                var sha=HashFile(full);
                if(!sha.Equals(pair.Value.Sha256,StringComparison.OrdinalIgnoreCase))errors.Add("portable_hash_mismatch:"+pair.Key);
            }

            var required=new[]{
                "H2Notes.Avalonia.exe","H2Notes.Avalonia.dll",
                "H2AgentLab.DesktopHost.exe","H2AgentLab.DesktopHost.dll","H2AgentLab.DesktopHost.runtimeconfig.json",
                "H2AgentLab.OfficeHost.exe","H2AgentLab.OfficeHost.dll","H2AgentLab.OfficeHost.runtimeconfig.json",
                "hostfxr.dll","hostpolicy.dll","coreclr.dll",
                "PORTABLE-README.txt","VERIFY-PORTABLE.ps1","python/python.exe"
            };
            foreach(var name in required)
                if(!map.ContainsKey(name))errors.Add("portable_required_file_missing:"+name);

            var canonical=CanonicalManifest(map.Values);
            var rootHash=HashBytes(Encoding.UTF8.GetBytes(canonical));
            if(!rootHash.Equals(manifest.ContentSha256,StringComparison.OrdinalIgnoreCase))
                errors.Add("portable_content_hash_mismatch");
            if(manifest.FileCount!=map.Count)errors.Add("portable_file_count_mismatch");
            if(manifest.TotalBytes!=total)errors.Add("portable_total_bytes_mismatch");

            return new(errors.Count==0,manifest.SourceSha,manifest.ContentSha256,map.Count,total,
                Array.AsReadOnly(errors.Distinct(StringComparer.Ordinal).ToArray()));
        }
        catch(Exception ex) when(ex is JsonException or IOException or UnauthorizedAccessException
                                 or InvalidDataException or CryptographicException or OverflowException)
        {
            errors.Add("portable_manifest_unreadable:"+ex.GetType().Name);
            return new(false,null,null,0,0,Array.AsReadOnly(errors.ToArray()));
        }
    }

    public static IReadOnlyList<H2PortableDependencyStatus> InspectConfiguredProviders(string settingsDirectory,string packageRoot)
    {
        var result=new List<H2PortableDependencyStatus>();
        settingsDirectory=Path.GetFullPath(settingsDirectory);
        packageRoot=Path.GetFullPath(packageRoot);

        LocalConfiguration? config=null;
        var configPath=Path.Combine(settingsDirectory,"local-config-v2.json");
        if(File.Exists(configPath))
        {
            try
            {
                if(new FileInfo(configPath).Length>2*1024*1024)throw new InvalidDataException("local_config_too_large");
                config=JsonSerializer.Deserialize<LocalConfiguration>(File.ReadAllBytes(configPath),Json);
            }
            catch(Exception ex) when(ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                result.Add(new("local.settings","Degraded","local_config_unreadable",
                    "Local settings exist but cannot be parsed by portable preflight."));
            }
        }
        else result.Add(new("local.settings","Ready","clean_profile","No previous H2 local configuration was found in the selected clean profile."));

        var profiles=config?.Ai?.Profiles??[];
        var ollama=profiles.Where(p=>p.Protocol==AiProtocol.Ollama).ToArray();
        var ollamaConfigured=ollama.Any(p=>!string.IsNullOrWhiteSpace(p.Model)&&ValidEndpoint(p));
        result.Add(ollamaConfigured
            ? new("ollama.endpoint","Degraded","configured_not_live_probed",
                "An Ollama endpoint/model is configured. Portable preflight does not contact it.")
            : new("ollama.endpoint","NeedsConfiguration","ollama_not_configured",
                "No complete Ollama endpoint/model profile is configured for this Windows profile."));

        var online=profiles.Where(p=>p.Protocol!=AiProtocol.Ollama).ToArray();
        var onlineReady=online.Any(p=>!string.IsNullOrWhiteSpace(p.Model)&&ValidEndpoint(p)
            &&File.Exists(Path.Combine(settingsDirectory,"credentials",p.Id.ToString("N")+".bin")));
        result.Add(onlineReady
            ? new("ai.online_credentials","Degraded","vault_entry_present_not_read",
                "An online model profile has a machine-local vault entry. The secret is not read or printed by portable preflight.")
            : new("ai.online_credentials","NeedsConfiguration","online_profile_or_vault_missing",
                "No complete online model profile plus machine-local credential vault entry was found."));

        var searchConfigured=!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("H2_BRAVE_SEARCH_API_KEY"));
        result.Add(searchConfigured
            ? new("web.search","Degraded","configured_not_live_probed",
                "Brave Search credential is configured in the process environment. No search request was sent.")
            : new("web.search","NeedsConfiguration","search_backend_not_configured",
                "Set H2_BRAVE_SEARCH_API_KEY to enable live search. Explicit-URL fetch is a separate capability."));

        var browserRaw=Environment.GetEnvironmentVariable("H2_BROWSER_CDP_ENDPOINT");
        var browserValid=TryLoopbackEndpoint(browserRaw);
        result.Add(browserValid
            ? new("browser.live_tab","Degraded","configured_not_live_probed",
                "A loopback browser DevTools endpoint is configured. No browser connection was opened.")
            : new("browser.live_tab","NeedsConfiguration",
                string.IsNullOrWhiteSpace(browserRaw)?"browser_backend_not_configured":"browser_endpoint_invalid",
                "Configure H2_BROWSER_CDP_ENDPOINT with an explicit loopback HTTP/HTTPS DevTools endpoint."));

        var ocrRoot=Path.Combine(packageRoot,"ocr-runtime");
        if(!Directory.Exists(ocrRoot))
            result.Add(new("ocr.local","NeedsConfiguration","optional_ocr_pack_not_bundled",
                "The standard portable app intentionally excludes multi-GB OCR models. Install/package OCR separately when needed."));
        else
        {
            var ocr=PortableOcrPackager.Inspect(ocrRoot);
            result.Add(ocr.Ready
                ? new("ocr.local","Ready","portable_ocr_ready","Portable OCR runtime is bundled and complete.")
                : new("ocr.local","Degraded","portable_ocr_incomplete","An OCR runtime folder exists but is not fully portable/ready."));
        }
        return result.AsReadOnly();
    }

    private static bool ValidEndpoint(AiProfile profile)
    {
        try
        {
            _=AiClient.Endpoint(profile,profile.Protocol==AiProtocol.Ollama?"api/tags":"models");
            return true;
        }
        catch(Exception ex) when(ex is InvalidOperationException or UriFormatException){return false;}
    }

    private static bool TryLoopbackEndpoint(string? value)
    {
        if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https")
            ||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0)return false;
        if(uri.Host.Equals("localhost",StringComparison.OrdinalIgnoreCase))return true;
        return System.Net.IPAddress.TryParse(uri.Host,out var address)&&System.Net.IPAddress.IsLoopback(address);
    }

    private static string CanonicalManifest(IEnumerable<H2PortableManifestFile> files)
        => string.Concat(files.OrderBy(x=>x.Path,StringComparer.OrdinalIgnoreCase)
            .Select(x=>NormalizeRelative(x.Path)+"\n"+x.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                       +"\n"+x.Sha256.ToLowerInvariant()+"\n"));

    private static string HashFile(string path)
    {
        using var stream=File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private static string HashBytes(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string? NormalizeRelative(string? value)
    {
        if(string.IsNullOrWhiteSpace(value)||Path.IsPathRooted(value)||value.Any(char.IsControl))return null;
        var path=value.Replace('\\','/').Trim('/');
        if(path.Length==0||path.Length>512||path.Split('/').Any(x=>x is "" or "." or ".."))return null;
        return path;
    }

    private static bool IsSensitivePortablePath(string relative)
    {
        relative=relative.Replace('\\','/');
        var parts=relative.Split('/',StringSplitOptions.RemoveEmptyEntries);
        if(parts.Any(p=>p.Equals("credentials",StringComparison.OrdinalIgnoreCase)
                     ||p.Equals("agent-runtime",StringComparison.OrdinalIgnoreCase)
                     ||p.Equals("workspace-v2",StringComparison.OrdinalIgnoreCase)
                     ||p.Equals("task-records",StringComparison.OrdinalIgnoreCase)
                     ||p.Equals("journal-v2",StringComparison.OrdinalIgnoreCase)))return true;
        var name=Path.GetFileName(relative);
        return name.Equals("local-config-v2.json",StringComparison.OrdinalIgnoreCase)
               ||name.Equals(".env",StringComparison.OrdinalIgnoreCase)
               ||name.Contains("api-key",StringComparison.OrdinalIgnoreCase)
               ||name.Contains("apikey",StringComparison.OrdinalIgnoreCase)
               ||name.Equals("secrets.json",StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInside(string root,string path)
    {
        root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))+Path.DirectorySeparatorChar;
        path=Path.GetFullPath(path);
        return path.StartsWith(root,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal);
    }

    private static bool IsSha(string? value,int length)
        => value is not null&&value.Length==length&&value.All(c=>Uri.IsHexDigit(c));

    private static bool SamePath(string a,string b)
        => Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal);

    private static string SafeMessage(Exception ex,string packageRoot,string settingsRoot)
    {
        var message=(ex.Message??ex.GetType().Name).Replace('\r',' ').Replace('\n',' ');
        foreach(var value in new[]{packageRoot,settingsRoot})
            if(!string.IsNullOrWhiteSpace(value))message=message.Replace(value,"<redacted-path>",
                OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal);
        return message.Length<=500?message:message[..500];
    }
}

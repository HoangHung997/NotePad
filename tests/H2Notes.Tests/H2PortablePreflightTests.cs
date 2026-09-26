using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using H2Notes.Avalonia;
using H2Notes.Core;

internal static class H2PortablePreflightTests
{
    private const string SourceSha = "0123456789abcdef0123456789abcdef01234567";

    public static void Run(Action<string,Action> test)
    {
        test("AR-082 manifest validates full inventory from Unicode and space path without OCR bundle",()=>Fixture(root=>{
            var package=Path.Combine(root,"Gói H2 sạch Việt Nam","Ứng dụng 测试");
            BuildPackage(package);WriteManifest(package);
            var result=PortablePackageDiagnostics.ValidateManifest(package);
            Check(result.Passed,"Valid portable fixture failed: "+string.Join(",",result.Errors));
            Check(result.SourceSha==SourceSha&&result.FileCount>=14&&result.TotalBytes>0,"Portable identity/count was not retained.");
            Check(!Directory.Exists(Path.Combine(package,"ocr-runtime")),"Standard fixture unexpectedly bundled OCR models.");
        }));

        test("AR-082 manifest rejects tampered bytes and missing self-contained runtime",()=>Fixture(root=>{
            var package=Path.Combine(root,"portable");BuildPackage(package);WriteManifest(package);
            File.AppendAllText(Path.Combine(package,"H2Notes.Avalonia.dll"),"tamper");
            File.Delete(Path.Combine(package,"hostfxr.dll"));
            var result=PortablePackageDiagnostics.ValidateManifest(package);
            Check(!result.Passed
                &&result.Errors.Any(x=>x.StartsWith("portable_hash_mismatch:H2Notes.Avalonia.dll",StringComparison.Ordinal))
                &&result.Errors.Any(x=>x=="portable_required_file_missing:hostfxr.dll"||x=="portable_manifest_inventory_mismatch"),
                "Tamper/runtime loss was not rejected.");
        }));

        test("AR-082 manifest refuses machine-local credentials workspace and Agent journal even when hashed",()=>Fixture(root=>{
            var package=Path.Combine(root,"portable");BuildPackage(package);
            Directory.CreateDirectory(Path.Combine(package,"credentials"));
            File.WriteAllText(Path.Combine(package,"credentials","fake.bin"),"not-a-real-secret");
            Directory.CreateDirectory(Path.Combine(package,"agent-runtime","journal-v2"));
            File.WriteAllText(Path.Combine(package,"agent-runtime","journal-v2","event.json"),"{}");
            WriteManifest(package);
            var result=PortablePackageDiagnostics.ValidateManifest(package);
            Check(!result.Passed
                &&result.Errors.Any(x=>x.StartsWith("portable_contains_local_state:credentials/",StringComparison.OrdinalIgnoreCase))
                &&result.Errors.Any(x=>x.StartsWith("portable_contains_local_state:agent-runtime/",StringComparison.OrdinalIgnoreCase)),
                "Portable policy accepted local credentials/Agent journal.");
        }));

        test("AR-082 clean profile reports missing providers independently and keeps OCR optional",()=>Fixture(root=>{
            var settings=Path.Combine(root,"clean settings");Directory.CreateDirectory(settings);
            var package=Path.Combine(root,"portable");BuildPackage(package);
            var oldSearch=Environment.GetEnvironmentVariable("H2_BRAVE_SEARCH_API_KEY");
            var oldBrowser=Environment.GetEnvironmentVariable("H2_BROWSER_CDP_ENDPOINT");
            try
            {
                Environment.SetEnvironmentVariable("H2_BRAVE_SEARCH_API_KEY",null);
                Environment.SetEnvironmentVariable("H2_BROWSER_CDP_ENDPOINT",null);
                var status=PortablePackageDiagnostics.InspectConfiguredProviders(settings,package);
                Check(State(status,"ollama.endpoint")=="NeedsConfiguration","Clean profile fabricated Ollama readiness.");
                Check(State(status,"ai.online_credentials")=="NeedsConfiguration","Clean profile fabricated online credential readiness.");
                Check(State(status,"web.search")=="NeedsConfiguration","Clean profile fabricated search readiness.");
                Check(State(status,"browser.live_tab")=="NeedsConfiguration","Clean profile fabricated browser readiness.");
                Check(status.Single(x=>x.Id=="ocr.local").Code=="optional_ocr_pack_not_bundled","Standard app incorrectly requires multi-GB OCR pack.");
            }
            finally
            {
                Environment.SetEnvironmentVariable("H2_BRAVE_SEARCH_API_KEY",oldSearch);
                Environment.SetEnvironmentVariable("H2_BROWSER_CDP_ENDPOINT",oldBrowser);
            }
        }));

        test("AR-082 configured profile/vault presence is reported without secret disclosure",()=>Fixture(root=>{
            var settings=Path.Combine(root,"profile");Directory.CreateDirectory(settings);
            var package=Path.Combine(root,"portable");BuildPackage(package);
            var ollama=new AiProfile{Protocol=AiProtocol.Ollama,BaseUrl="http://localhost:11434",Model="fixture-local",Name="Local"};
            var api=new AiProfile{Protocol=AiProtocol.OpenAiChat,BaseUrl="https://example.test/v1",Model="fixture-api",Name="API"};
            var config=new LocalConfiguration{DeviceId="ar082-fixture",Ai=new AiConnectionSettings{Profiles=[ollama,api],SelectedId=api.Id}};
            File.WriteAllText(Path.Combine(settings,"local-config-v2.json"),JsonSerializer.Serialize(config));
            var credentials=Path.Combine(settings,"credentials");Directory.CreateDirectory(credentials);
            const string secret="AR082_FAKE_SECRET_NEVER_OUTPUT";
            File.WriteAllBytes(Path.Combine(credentials,api.Id.ToString("N")+".bin"),Encoding.UTF8.GetBytes(secret));
            var oldSearch=Environment.GetEnvironmentVariable("H2_BRAVE_SEARCH_API_KEY");
            var oldBrowser=Environment.GetEnvironmentVariable("H2_BROWSER_CDP_ENDPOINT");
            try
            {
                Environment.SetEnvironmentVariable("H2_BRAVE_SEARCH_API_KEY",secret);
                Environment.SetEnvironmentVariable("H2_BROWSER_CDP_ENDPOINT","http://127.0.0.1:9222");
                var status=PortablePackageDiagnostics.InspectConfiguredProviders(settings,package);
                Check(State(status,"ollama.endpoint")=="Degraded","Configured Ollama profile was not detected.");
                Check(State(status,"ai.online_credentials")=="Degraded","Vault-entry presence was not detected.");
                Check(State(status,"web.search")=="Degraded"&&State(status,"browser.live_tab")=="Degraded","Configured web/browser endpoints were not detected.");
                Check(!JsonSerializer.Serialize(status).Contains(secret,StringComparison.Ordinal),"Portable readiness leaked a credential value.");
            }
            finally
            {
                Environment.SetEnvironmentVariable("H2_BRAVE_SEARCH_API_KEY",oldSearch);
                Environment.SetEnvironmentVariable("H2_BROWSER_CDP_ENDPOINT",oldBrowser);
            }
        }));
    }

    private static string State(IReadOnlyList<H2AgentLab.Integration.H2PortableDependencyStatus> status,string id)
        => status.Single(x=>x.Id==id).State;

    private static void BuildPackage(string root)
    {
        Directory.CreateDirectory(root);
        foreach(var relative in new[]{
            "H2Notes.Avalonia.exe","H2Notes.Avalonia.dll",
            "H2AgentLab.DesktopHost.exe","H2AgentLab.DesktopHost.dll","H2AgentLab.DesktopHost.runtimeconfig.json",
            "H2AgentLab.OfficeHost.exe","H2AgentLab.OfficeHost.dll","H2AgentLab.OfficeHost.runtimeconfig.json",
            "hostfxr.dll","hostpolicy.dll","coreclr.dll","PORTABLE-README.txt","VERIFY-PORTABLE.ps1"})
        {
            var path=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,"fixture:"+relative);
        }
        Directory.CreateDirectory(Path.Combine(root,"python"));
        File.WriteAllText(Path.Combine(root,"python","python.exe"),"fixture-python");
    }

    private static void WriteManifest(string root)
    {
        var files=Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)
            .Where(path=>!Path.GetFileName(path).Equals("portable-manifest.json",StringComparison.OrdinalIgnoreCase))
            .Select(path=>{
                var relative=Path.GetRelativePath(root,path).Replace('\\','/');
                var bytes=new FileInfo(path).Length;
                using var stream=File.OpenRead(path);
                var sha=Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                return new H2PortableManifestFile(relative,bytes,sha);
            }).OrderBy(x=>x.Path,StringComparer.OrdinalIgnoreCase).ToArray();
        var canonical=string.Concat(files.Select(x=>x.Path+"\n"+x.Bytes+"\n"+x.Sha256+"\n"));
        var contentSha=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var manifest=new H2PortableManifest(1,SourceSha,"fixture","win-x64",true,"fixture",files.Length,files.Sum(x=>x.Bytes),contentSha,files);
        File.WriteAllText(Path.Combine(root,"portable-manifest.json"),JsonSerializer.Serialize(manifest,new JsonSerializerOptions{WriteIndented=true}));
    }

    private static void Fixture(Action<string> action)
    {
        var root=Path.Combine(Path.GetTempPath(),"h2-ar082-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{action(root);}finally{try{Directory.Delete(root,true);}catch{}}
    }

    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}

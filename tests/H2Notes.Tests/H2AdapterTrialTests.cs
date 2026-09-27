using System.IO.Compression;
using System.Text;
using System.Text.Json;
using H2AgentLab;
using H2AgentLab.Plugins;
using H2AgentLab.Tools;

internal static class H2AdapterTrialTests
{
    private const string PluginId = "h2.fixture.adapter";
    private const string Publisher = "fixture.publisher";

    public static void Run(Action<string,Action> test)
    {
        test("AR-071 task-local adapter trial uses PluginManager staging and never promotes global registry", () =>
            Fixture(root =>
            {
                var (manager, registry) = Stable(root);
                var candidate = BuildPackage(root,"2.0.0",true,["workspace.read"]);
                var workspace=Workspace(root,"ORIGINAL");
                var trials=new AdapterTrialManager(manager);
                var runner=new TrialRunner("2026.1");
                var request=Request(workspace,candidate,"2026.1");

                var report=trials.RunAsync(request,runner).GetAwaiter().GetResult();
                Check(report.Status=="Passed"&&report.Compatible&&!report.Promoted&&!report.CacheReused,
                    "Successful trial status/promotion flags are wrong.");
                Check(report.StablePluginVersionBefore=="1.0.0"&&report.StablePluginVersionAfter=="1.0.0",
                    "Trial changed stable active plugin version.");
                Check(registry.TryGet("plugin.echo",out var stable)
                    && stable.Provenance?.ProviderVersion=="1.0.0",
                    "Candidate trial replaced global ToolRegistry descriptor.");
                Check(report.Deltas.Count==1&&report.Deltas[0].Path=="output/result.txt"
                    && report.Readback.Single().Path=="output/result.txt",
                    "Trial diff/readback did not reflect only candidate copy output.");
                Check(File.ReadAllText(Path.Combine(workspace,"input","data.txt"))=="ORIGINAL",
                    "Trial modified original workspace input instead of a copy.");
                Check(File.Exists(report.ReceiptPath)&&!report.TrialBytesRetained,
                    "Hash-only trial receipt was not retained after staged bytes were discarded.");
                Check(!Directory.EnumerateDirectories(
                    Path.Combine(root,"state","adapter-trials",request.TaskId.ToString("N"))).Any(),
                    "Task-local staged adapter bytes survived completed trial.");
            }));

        test("AR-071 adapter trial workspace blocks scope escape while candidate still completes on copy", () =>
            Fixture(root =>
            {
                var (manager,registry)=Stable(root);
                var candidate=BuildPackage(root,"2.0.0",true,["workspace.read"]);
                var workspace=Workspace(root,"SAFE");
                var runner=new TrialRunner("2026.1"){TryEscape=true};
                var report=new AdapterTrialManager(manager).RunAsync(
                    Request(workspace,candidate,"2026.1"),runner).GetAwaiter().GetResult();
                Check(report.Status=="Passed"&&runner.EscapeBlocked,
                    "Candidate trial escaped SafeWorkspace or scope check was not exercised.");
                Check(!File.Exists(Path.Combine(root,"state","outside.txt"))
                    && registry.TryGet("plugin.echo",out var descriptor)
                    && descriptor.Provenance?.ProviderVersion=="1.0.0",
                    "Scope escape affected host state or stable adapter.");
            }));

        test("AR-071 cache reuse always probes current capability and app version change invalidates reuse", () =>
            Fixture(root =>
            {
                var (manager,_)=Stable(root);
                var candidate=BuildPackage(root,"2.0.0",true,["workspace.read"]);
                var workspace=Workspace(root,"CACHE");
                var task=Guid.NewGuid();
                var trials=new AdapterTrialManager(manager);
                var runner=new TrialRunner("2026.1");

                var first=trials.RunAsync(Request(workspace,candidate,"2026.1",task),runner).GetAwaiter().GetResult();
                var second=trials.RunAsync(Request(workspace,candidate,"2026.1",task),runner).GetAwaiter().GetResult();
                Check(!first.CacheReused&&second.CacheReused&&runner.Probes==2&&runner.Executions==2,
                    "Cache was reused without a fresh capability probe or same environment was not reusable.");

                runner.RequiredApplicationVersion="2026.1";
                var changed=trials.RunAsync(Request(workspace,candidate,"2027.0",task),runner).GetAwaiter().GetResult();
                Check(changed.Status=="Incompatible"&&!changed.CacheReused&&runner.Probes==3&&runner.Executions==2,
                    "Old adapter cache survived host application version change or incompatible adapter executed.");
            }));

        test("AR-071 bad package hash fails before trial and leaves stable version intact", () =>
            Fixture(root =>
            {
                var (manager,registry)=Stable(root);
                var candidate=BuildPackage(root,"2.0.0",true,["workspace.read"]);
                var bad=candidate with { CatalogEntry=candidate.CatalogEntry with {
                    ArchiveSha256="sha256:"+new string('0',64)}};
                var workspace=Workspace(root,"HASH");
                try
                {
                    _=new AdapterTrialManager(manager).RunAsync(
                        Request(workspace,bad,"2026.1"),new TrialRunner("2026.1")).GetAwaiter().GetResult();
                    throw new InvalidOperationException("Bad archive hash trial unexpectedly ran.");
                }
                catch(InvalidDataException ex) when(ex.Message.Contains("archive hash",StringComparison.OrdinalIgnoreCase)){ }
                Check(manager.GetActive(PluginId)?.Manifest.Version=="1.0.0"
                    && registry.TryGet("plugin.echo",out var stable)
                    && stable.Provenance?.ProviderVersion=="1.0.0",
                    "Bad hash trial disturbed stable active adapter.");
            }));

        test("AR-071 failed declarative self-test rolls candidate back without final trial bytes", () =>
            Fixture(root =>
            {
                var (manager,_)=Stable(root);
                var candidate=BuildPackage(root,"2.0.0",false,["workspace.read"]);
                var workspace=Workspace(root,"SELFTEST");
                var task=Guid.NewGuid();
                try
                {
                    _=new AdapterTrialManager(manager).RunAsync(
                        Request(workspace,candidate,"2026.1",task),new TrialRunner("2026.1")).GetAwaiter().GetResult();
                    throw new InvalidOperationException("Failed self-test candidate unexpectedly ran.");
                }
                catch(InvalidDataException ex) when(ex.Message.Contains("self-test",StringComparison.OrdinalIgnoreCase)){ }
                var trialRoot=Path.Combine(root,"state","adapter-trials",task.ToString("N"));
                Check(manager.GetActive(PluginId)?.Manifest.Version=="1.0.0"
                    && (!Directory.Exists(trialRoot)||!Directory.EnumerateDirectories(trialRoot).Any()),
                    "Self-test failure left final trial package or changed stable adapter.");
            }));

        test("AR-071 execution failure discards candidate/copy and preserves stable plus original input", () =>
            Fixture(root =>
            {
                var (manager,registry)=Stable(root);
                var candidate=BuildPackage(root,"2.0.0",true,["workspace.read"]);
                var workspace=Workspace(root,"ROLLBACK");
                var task=Guid.NewGuid();
                var runner=new TrialRunner("2026.1"){FailAfterWrite=true};
                var report=new AdapterTrialManager(manager).RunAsync(
                    Request(workspace,candidate,"2026.1",task),runner).GetAwaiter().GetResult();
                Check(report.Status=="Failed"&&!report.Promoted
                    && report.Deltas.Any(x=>x.Path=="output/result.txt"),
                    "Failed candidate did not retain bounded hash diff evidence.");
                Check(File.ReadAllText(Path.Combine(workspace,"input","data.txt"))=="ROLLBACK"
                    && manager.GetActive(PluginId)?.Manifest.Version=="1.0.0"
                    && registry.TryGet("plugin.echo",out var stable)
                    && stable.Provenance?.ProviderVersion=="1.0.0",
                    "Failed trial modified original data or stable registry.");
                var rootTrial=Path.Combine(root,"state","adapter-trials",task.ToString("N"));
                Check(!Directory.Exists(rootTrial)||!Directory.EnumerateDirectories(rootTrial).Any(),
                    "Failed trial bytes were not rolled back.");
            }));
    }

    private static (PluginManager Manager,ToolRegistry Registry) Stable(string root)
    {
        var state=Path.Combine(root,"state");Directory.CreateDirectory(state);
        var registry=new ToolRegistry();
        var manager=new PluginManager(state,registry,new FixtureResolver());
        var stable=BuildPackage(root,"1.0.0",true,["workspace.read"]);
        _=manager.InstallFromArchive(stable.Path,stable.CatalogEntry,Policy(),true);
        return(manager,registry);
    }

    private static AdapterTrialRequest Request(
        string workspace,
        BuiltPackage candidate,
        string appVersion,
        Guid? taskId=null)
        =>new(taskId??Guid.NewGuid(),"AR-071 compatibility trial",candidate.Path,candidate.CatalogEntry,
            Policy(),true,workspace,["input/data.txt"],
            new("fixture-app",appVersion,"adapter-api-v1"),
            [new("fixture-sdk","5.4.3","sha256:"+new string('a',64))]);

    private static string Workspace(string root,string content)
    {
        var workspace=Path.Combine(root,"workspace-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace,"input"));
        File.WriteAllText(Path.Combine(workspace,"input","data.txt"),content);
        return workspace;
    }

    private static PluginInstallPolicy Policy()
        =>new(PluginInstallMode.DeveloperLocal,
            new HashSet<string>(StringComparer.Ordinal){Publisher},
            AllowNativeHelpers:false,AllowLifecycleHooks:false);

    private static BuiltPackage BuildPackage(
        string root,string version,bool selfTestOk,IReadOnlyList<string> permissions)
    {
        var packageDir=Path.Combine(root,"packages");Directory.CreateDirectory(packageDir);
        var path=Path.Combine(packageDir,version+"-"+Guid.NewGuid().ToString("N")+".zip");
        using var memory=new MemoryStream();
        using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true))
        {
            WriteZip(zip,"tools.json",JsonSerializer.Serialize(new[]{
                new{
                    name="plugin.echo",@namespace="adapter-fixture",
                    description="AR-071 task-local adapter fixture.",
                    access="ReadOnly",risk="Low",supportsParallel=true,
                    schemaVersion="v1",toolVersion=version,
                    resourceScope="workspace-a",serializationKey="adapter-fixture",
                    schema=new{type="function",function=new{name="plugin.echo",
                        description="Fixture echo.",
                        parameters=new{type="object",properties=new{text=new{type="string"}},
                            required=new[]{"text"},additionalProperties=false}}}
                }}));
            WriteZip(zip,"selftest.json",JsonSerializer.Serialize(new{
                ok=selfTestOk,requiredFiles=new[]{"tools.json"}}));
        }
        memory.Position=0;
        string payload;
        using(var read=new ZipArchive(memory,ZipArchiveMode.Read,true))
            payload=PluginManager.ComputePayloadHash(read);
        memory.Position=0;
        var manifest=new H2PluginManifest(
            PluginId,"Fixture Adapter",version,"2.0.0",Publisher,
            "sha256:"+payload,["plugin.echo"],[],[],permissions,[],[],"selftest.json");
        using(var update=new ZipArchive(memory,ZipArchiveMode.Update,true))
            WriteZip(update,"manifest.json",JsonSerializer.Serialize(manifest));
        var bytes=memory.ToArray();File.WriteAllBytes(path,bytes);
        var archiveHash=SafeWorkspace.Hash(bytes).ToLowerInvariant();
        var entry=new PluginCatalogEntry(
            manifest.Id,manifest.Name,manifest.Version,"AR-071 adapter trial candidate.",
            manifest.Publisher,["adapter","trial"],[],manifest.MinAgentVersion,
            PluginTrustState.LocalDeveloper,"sha256:"+archiveHash,path);
        return new(path,entry);
    }

    private static void WriteZip(ZipArchive zip,string path,string content)
    {
        var entry=zip.CreateEntry(path,CompressionLevel.NoCompression);
        using var stream=entry.Open();
        using var writer=new StreamWriter(stream,new UTF8Encoding(false),leaveOpen:false);
        writer.Write(content);
    }

    private static void Fixture(Action<string> action)
    {
        var root=Path.Combine(Path.GetTempPath(),"h2-ar071-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try{action(root);}finally{try{Directory.Delete(root,true);}catch{}}
    }

    private static void Check(bool value,string message)
    {
        if(!value)throw new InvalidOperationException(message);
    }

    private sealed record BuiltPackage(string Path,PluginCatalogEntry CatalogEntry);

    private sealed class FixtureResolver:IPluginToolExecutorResolver
    {
        public IAgentToolExecutor Resolve(H2PluginManifest manifest,PluginToolDefinition tool)
            =>new DelegatingToolExecutor("ar071-stable",(call,ct)=>
                ValueTask.FromResult(JsonSerializer.Serialize(new{
                    plugin=manifest.Id,version=manifest.Version,tool=tool.Name})));
    }

    private sealed class TrialRunner(string requiredApplicationVersion):IAdapterTrialRunner
    {
        public int Probes,Executions;
        public bool TryEscape,EscapeBlocked,FailAfterWrite;
        public string RequiredApplicationVersion=requiredApplicationVersion;

        public Task<AdapterTrialCapabilityProbe> ProbeAsync(
            AdapterTrialExecutionContext context,CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();Probes++;
            var compatible=context.Manifest.Host.ApplicationVersion==RequiredApplicationVersion;
            return Task.FromResult(new AdapterTrialCapabilityProbe(
                compatible,
                "fixture-app:"+context.Manifest.Host.ApplicationVersion
                    +":api:"+context.Manifest.Host.AdapterApiVersion,
                compatible?"compatible":"application_version_mismatch"));
        }

        public Task<AdapterTrialExecutionResult> ExecuteAsync(
            AdapterTrialExecutionContext context,CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();Executions++;
            if(TryEscape)
            {
                try{_=context.Workspace.Resolve("../outside.txt");}
                catch(AgentFaultException){EscapeBlocked=true;}
            }
            var input=context.Workspace.Resolve("input/data.txt");
            var output=context.Workspace.Resolve("output/result.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output,File.ReadAllText(input)+"|candidate="+context.Manifest.CandidatePluginVersion
                +"|cache="+context.ReusingPreparedAdapter);
            if(FailAfterWrite)throw new IOException("controlled adapter trial failure");
            return Task.FromResult(new AdapterTrialExecutionResult(
                true,"fixture candidate executed on copied input",["output/result.txt"]));
        }
    }
}

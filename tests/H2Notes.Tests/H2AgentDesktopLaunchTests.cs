using System.Text.Json;
using H2AgentLab;
using H2AgentLab.Tools;

internal static class H2AgentDesktopLaunchTests
{
    public static void Run(Action<string,Action> test)
    {
        test("AR-061 normal runtime exposes application lifecycle without a selected desktop window",()=>{
            Temp((root,state)=>{
                using var host=new AgentTools(new SafeWorkspace(root),state,(_,_)=>Task.FromResult(true),(_,_)=>{});
                host.ReadOnly=false;
                var registry=NormalRuntimeToolRegistry.Create(host);
                foreach(var name in new[]{"list_running_apps","launch_app","wait_for_app_window","activate_app"})
                    Check(registry.TryGet(name,out _),"Missing callable "+name);
                Check(host.Desktop is null,"Test unexpectedly has a selected desktop target.");
                Check(registry.GetNamespace("app").Count==4,"Application lifecycle namespace is incomplete.");
                Check(registry.TryGet("launch_app",out var launch)
                    && launch.IsMutating
                    && launch.Risk==AgentToolRisk.High
                    && launch.Preference?.InteractionFidelity==ToolInteractionFidelity.Accessibility,
                    "launch_app risk/preference metadata is wrong.");
            });
        });

        test("AR-061 application retry guard requires changed evidence for the same application",()=>{
            var supervisor=new RecoverySupervisor();
            var launch=new ToolCall("launch-1","launch_app",JsonSerializer.SerializeToElement(new{application="Word",mode="reuse_or_launch"}));
            supervisor.Observe(launch,JsonSerializer.Serialize(new{
                success=false,
                recovery=new{
                    code="launch_unverified",
                    message="Launch outcome requires observation.",
                    recoverable=true,
                    next="Observe the requested application before any repeat."
                }
            }));
            Check(supervisor.Block(launch) is not null,
                "Uncertain launch_app could repeat without a fresh observation.");

            var other=new ToolCall("wait-other","wait_for_app_window",JsonSerializer.SerializeToElement(new{application="Excel"}));
            supervisor.Observe(other,JsonSerializer.Serialize(new{success=true,observed=new{session_id="excel-1"}}));
            Check(supervisor.Block(launch) is not null,
                "Observing a different application incorrectly unlocked Word launch retry.");

            var same=new ToolCall("wait-word","wait_for_app_window",JsonSerializer.SerializeToElement(new{application="Word"}));
            supervisor.Observe(same,JsonSerializer.Serialize(new{success=true,observed=new{session_id="word-1"}}));
            Check(supervisor.Block(launch) is null,
                "Fresh observation of the same application did not unlock one reconciled retry.");
        });

        test("AR-061 tool search discovers launcher for real user phrasing",()=>{
            Temp((root,state)=>{
                using var host=new AgentTools(new SafeWorkspace(root),state,(_,_)=>Task.FromResult(true),(_,_)=>{});
                var registry=NormalRuntimeToolRegistry.Create(host);
                var discovery=new DeferredToolDiscovery(registry);
                foreach(var query in new[]{"open word app","open file explorer","mở file explore","mở app word","start excel application","launch autocad","open blank excel window","mở word trắng mới"})
                {
                    var results=discovery.Search(query,8);
                    Check(results.Count>0 && results[0].Descriptor.Name=="launch_app",
                        "Production deferred discovery did not rank launch_app first for: "+query);
                }
                Check(registry.TryGet("launch_app",out var launcher)
                    && launcher.Preference?.CapabilityFamily=="application-lifecycle",
                    "launch_app still shares the active-content preference family and can be displaced by document tools.");
            });
        });

        test("AR-061 missing DesktopHost is a no-effect launch preflight failure",()=>{
            Temp((root,state)=>{
                var previous=Environment.GetEnvironmentVariable(H2AgentLab.Desktop.DesktopHostLocator.EnvironmentVariable);
                try
                {
                    Environment.SetEnvironmentVariable(
                        H2AgentLab.Desktop.DesktopHostLocator.EnvironmentVariable,
                        Path.Combine(root,"missing-desktop-host.exe"));
                    using var host=new AgentTools(new SafeWorkspace(root),state,(_,_)=>Task.FromResult(true),(_,_)=>{});
                    host.ReadOnly=false;
                    var registry=NormalRuntimeToolRegistry.Create(host);
                    Check(registry.TryGet("launch_app",out var launch),"launch_app missing.");
                    var raw=launch.Executor.ExecuteAsync(
                        new ToolCall("preflight","launch_app",JsonSerializer.SerializeToElement(new{
                            application="notepad",
                            mode="reuse_or_launch"
                        })),
                        CancellationToken.None).AsTask().GetAwaiter().GetResult();
                    using var json=JsonDocument.Parse(raw);
                    var rootNode=json.RootElement;
                    Check(rootNode.TryGetProperty("success",out var success)
                        && success.ValueKind==JsonValueKind.False,
                        "Missing helper launch preflight did not fail.");
                    Check(rootNode.TryGetProperty("mutationApplied",out var applied)
                        && applied.ValueKind==JsonValueKind.False,
                        "Missing helper preflight was not classified as no-effect.");
                    Check(rootNode.GetProperty("recovery").GetProperty("code").GetString()=="app_preflight_unavailable",
                        "Missing helper preflight lost its typed recovery code.");
                }
                finally
                {
                    Environment.SetEnvironmentVariable(
                        H2AgentLab.Desktop.DesktopHostLocator.EnvironmentVariable,
                        previous);
                }
            });
        });

        test("AR-061 DesktopHost preflight failure never suggests an alternate backend",()=>{
            Check(ToolOutcomeBridge.NormalizeCode("app_preflight_unavailable")=="app_preflight_unavailable",
                "DesktopHost preflight identity was collapsed into a generic provider error.");
            var plan=ToolRecoveryPolicy.For("app_preflight_unavailable",ToolMutationEffect.None);
            Check(plan.RetryClass==ToolRetryClass.Configure
                && plan.RequiresChangedEvidence
                && plan.PreserveTargetIdentity
                && !plan.AllowsAlternateBackend
                && plan.RecoveryCandidates.Contains("repair_packaged_desktop_host",StringComparer.Ordinal),
                "DesktopHost preflight recovery can silently route to a different backend.");
        });

        test("AR-061 launch_app schema requires an application name and offers no executable path argument",()=>{
            Temp((root,state)=>{
                using var host=new AgentTools(new SafeWorkspace(root),state,(_,_)=>Task.FromResult(true),(_,_)=>{});
                var registry=NormalRuntimeToolRegistry.Create(host);
                Check(registry.TryGet("launch_app",out var launch),"launch_app missing.");
                var function=launch.CallableSchema.GetProperty("function");
                var parameters=function.GetProperty("parameters");
                var properties=parameters.GetProperty("properties");
                Check(properties.TryGetProperty("application",out _),"launch_app lacks application argument.");
                Check(properties.TryGetProperty("mode",out _),"launch_app lacks explicit launch mode.");
                Check(!properties.TryGetProperty("path",out _)
                    && !properties.TryGetProperty("command",out _)
                    && !properties.TryGetProperty("arguments",out _),
                    "launch_app exposed arbitrary executable/shell arguments.");
                Check(parameters.GetProperty("required").EnumerateArray()
                    .Select(x=>x.GetString()).SequenceEqual(new[]{"application","mode"}),
                    "launch_app application/mode arguments are not required.");
            });
        });
    }

    private static void Temp(Action<string,string> action)
    {
        var root=Path.Combine(Path.GetTempPath(),"h2-ar061-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root,Path.Combine(root,"state")); }
        finally { Directory.Delete(root,true); }
    }

    private static void Check(bool value,string message)
    { if(!value) throw new InvalidOperationException(message); }
}

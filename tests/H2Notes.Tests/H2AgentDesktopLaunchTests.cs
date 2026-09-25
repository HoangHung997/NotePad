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
            var launch=new ToolCall("launch-1","launch_app",JsonSerializer.SerializeToElement(new{application="Word"}));
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

        test("AR-061 launch_app schema requires an application name and offers no executable path argument",()=>{
            Temp((root,state)=>{
                using var host=new AgentTools(new SafeWorkspace(root),state,(_,_)=>Task.FromResult(true),(_,_)=>{});
                var registry=NormalRuntimeToolRegistry.Create(host);
                Check(registry.TryGet("launch_app",out var launch),"launch_app missing.");
                var function=launch.CallableSchema.GetProperty("function");
                var parameters=function.GetProperty("parameters");
                var properties=parameters.GetProperty("properties");
                Check(properties.TryGetProperty("application",out _),"launch_app lacks application argument.");
                Check(!properties.TryGetProperty("path",out _)
                    && !properties.TryGetProperty("command",out _)
                    && !properties.TryGetProperty("arguments",out _),
                    "launch_app exposed arbitrary executable/shell arguments.");
                Check(parameters.GetProperty("required").EnumerateArray()
                    .Select(x=>x.GetString()).SequenceEqual(new[]{"application"}),
                    "launch_app application argument is not required.");
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

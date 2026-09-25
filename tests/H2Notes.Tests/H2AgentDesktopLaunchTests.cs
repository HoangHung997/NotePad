using System.Text.Json;
using H2AgentLab;
using H2AgentLab.Tools;
using H2AgentLab.Integration;

internal static class H2AgentDesktopLaunchTests
{
    public static void Run(Action<string,Action> test)
    {
        test("AR-061 normal runtime exposes application lifecycle without a selected desktop window",()=>{
            Temp((root,state)=>{
                using var host=new AgentTools(new SafeWorkspace(root),state,(_,_)=>Task.FromResult(true),(_,_)=>{});
                host.ReadOnly=false;
                var registry=NormalRuntimeToolRegistry.Create(host);
                foreach(var name in new[]{"app.list_running_apps","app.launch","app.wait_for_window","app.activate"})
                    Check(registry.TryGet(name,out _),"Missing callable "+name);
                Check(host.Desktop is null,"Test unexpectedly has a selected desktop target.");
                Check(registry.GetNamespace("app").Count==4,"Application lifecycle namespace is incomplete.");
                Check(registry.TryGet("app.launch",out var launch)
                    && launch.IsMutating
                    && launch.Risk==AgentToolRisk.High
                    && launch.Preference?.InteractionFidelity==ToolInteractionFidelity.Accessibility,
                    "app.launch risk/preference metadata is wrong.");
            });
        });

        test("AR-061 app lifecycle verifier accepts only host-observed exact application window outcome",()=>{
            var verifier=new H2DesktopRuntimeVerifier();
            var launch=new ToolCall("launch","app.launch",JsonSerializer.SerializeToElement(new{application="notepad"}));
            var good=JsonSerializer.Serialize(new{
                application="notepad",
                process="notepad",
                newWindowObserved=true,
                reusedExistingWindow=false,
                window=new{session_id="win-1",hwnd=1001L,pid=22,process_started_utc_ticks=33L,process="notepad",title="Untitled",foreground=true,dpi=96},
                verifiedByHostObservation=true
            });
            var report=verifier.VerifyAsync(null!,launch,good,CancellationToken.None).GetAwaiter().GetResult();
            Check(report.Passed && report.EvidenceIds.Single()=="desktop-window:win-1",
                "Host-observed app launch was not verified.");

            var weak=JsonSerializer.Serialize(new{
                application="notepad",
                process="notepad",
                newWindowObserved=false,
                reusedExistingWindow=false,
                window=new{session_id="win-1",pid=22},
                verifiedByHostObservation=true
            });
            Check(!verifier.VerifyAsync(null!,launch,weak,CancellationToken.None).GetAwaiter().GetResult().Passed,
                "Unobserved/semantically weak app launch was incorrectly verified.");
        });

        test("AR-061 app.launch schema requires an application name and offers no executable path argument",()=>{
            Temp((root,state)=>{
                using var host=new AgentTools(new SafeWorkspace(root),state,(_,_)=>Task.FromResult(true),(_,_)=>{});
                var registry=NormalRuntimeToolRegistry.Create(host);
                Check(registry.TryGet("app.launch",out var launch),"app.launch missing.");
                var function=launch.CallableSchema.GetProperty("function");
                var parameters=function.GetProperty("parameters");
                var properties=parameters.GetProperty("properties");
                Check(properties.TryGetProperty("application",out _),"app.launch lacks application argument.");
                Check(!properties.TryGetProperty("path",out _)
                    && !properties.TryGetProperty("command",out _)
                    && !properties.TryGetProperty("arguments",out _),
                    "app.launch exposed arbitrary executable/shell arguments.");
                Check(parameters.GetProperty("required").EnumerateArray()
                    .Select(x=>x.GetString()).SequenceEqual(new[]{"application"}),
                    "app.launch application argument is not required.");
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

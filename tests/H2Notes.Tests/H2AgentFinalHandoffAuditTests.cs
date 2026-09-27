using System.Text.RegularExpressions;

internal static class H2AgentFinalHandoffAuditTests
{
    public static void Run(Action<string,Action> test)
    {
        var repo=FindRepoRoot();
        var tracker=File.ReadAllText(Path.Combine(repo,"docs","H2_AGENT_RELIABILITY_IMPLEMENTATION_TASKS.md"));
        var report=File.ReadAllText(Path.Combine(repo,"docs","agent-reliability","AR-090","FINAL_HANDOFF_REPORT.md"));
        var readme=File.ReadAllText(Path.Combine(repo,"README.md"));
        var historical=File.ReadAllText(Path.Combine(repo,"docs","H2_AGENT_FINAL_ARCHITECTURE_REPORT.md"));
        var activeMatch=Regex.Match(tracker,"\\\"active_task\\\"\\s*:\\s*\\\"(?<id>AR-\\d{3})\\\"");
        var activeTask=activeMatch.Success?activeMatch.Groups["id"].Value:null;

        test("AR-090 audit has no mandatory NOT_STARTED task before final handoff",()=>{
            var rows=tracker.Split('\n').Select(x=>x.Trim()).Where(x=>Regex.IsMatch(x,@"^\| AR-\d{3} \|")).ToArray();
            Check(rows.Length>=35,"AR tracker table is unexpectedly incomplete.");
            foreach(var row in rows)
            {
                var cells=row.Split('|').Select(x=>x.Trim()).Where(x=>x.Length>0).ToArray();
                var id=cells[0];var status=cells[^1];
                if(id is "AR-071" or "AR-072")
                {
                    if(id==activeTask)
                        Check(status.Contains("ACTIVE",StringComparison.Ordinal)||status.Contains("IMPLEMENTED",StringComparison.Ordinal),
                            id+" is selected but tracker status is not active/implemented.");
                    else
                        Check(status.Contains("NOT_SELECTED",StringComparison.Ordinal),id+" optional status changed without selection.");
                    continue;
                }
                if(id=="AR-083"){Check(status.Contains("DEFERRED_BY_USER",StringComparison.Ordinal),"AR-083 lost deferred status.");continue;}
                if(id=="AR-090"){Check(status.Contains("ACTIVE",StringComparison.Ordinal)||status.Contains("IMPLEMENTED",StringComparison.Ordinal),"AR-090 is neither active nor implemented.");continue;}
                Check(!status.Contains("NOT_STARTED",StringComparison.Ordinal),id+" is still mandatory NOT_STARTED.");
            }
        });

        test("AR-090 handoff makes deferred acceptance impossible to mistake for project completion",()=>{
            if(activeTask is "AR-071" or "AR-072")
                Check(report.Contains("STALE_AFTER_OPTIONAL_TASK_ACTIVATION",StringComparison.Ordinal)
                    && report.Contains(activeTask,StringComparison.Ordinal),
                    "Prior final handoff was not marked stale after optional task activation.");
            foreach(var required in new[]{"IMPLEMENTATION_READY_FOR_USER_TEST","AR-083","DEFERRED_BY_USER","E5 NOT PASSED",
                "not “project complete”","native Office","live AutoCAD","NOT_SELECTED"})
                Check(report.Contains(required,StringComparison.OrdinalIgnoreCase),"Final handoff missing limitation/status: "+required);
            foreach(var forbidden in new[]{"PROJECT_COMPLETE","E5 PASS","AR-083 PASS"})
                Check(!Regex.IsMatch(report,@"(?im)^\s*(?:status:\s*)?\*{0,2}"+Regex.Escape(forbidden)+@"\*{0,2}\s*$"),
                    "Final handoff contains prohibited completion claim: "+forbidden);
        });

        test("AR-090 handoff contains required capability status limitations reproduction and evidence sections",()=>{
            foreach(var heading in new[]{"## 2. Task status — two axes","## 3. Capability matrix","## 4. Dependency matrix",
                "## 5. Cleanup and architecture parity audit","## 6. Known limitations and deferred acceptance",
                "## 7. Reproduce the current source gate","## 8. Evidence retention","## 9. Handoff result"})
                Check(report.Contains(heading,StringComparison.Ordinal),"Final handoff missing section: "+heading);
            Check(Regex.IsMatch(report,@"AR-090 validated code SHA: `(?:PENDING_THIS_GATE|[0-9a-f]{40})`"),
                "AR-090 code SHA field is malformed.");
            Check(Regex.IsMatch(report,@"AR-090 final portable artifact: `(?:PENDING_THIS_GATE|\d+)`"),
                "AR-090 portable artifact field is malformed.");
        });

        test("AR-090 production path remains concrete while retained legacy code stays non-production",()=>{
            var app=File.ReadAllText(Path.Combine(repo,"src","H2Notes.Avalonia","App.axaml.cs"));
            var facade=File.ReadAllText(Path.Combine(repo,"experiments","H2AgentLab","Tasking","AgentOrchestratedRun.cs"));
            Check(app.Contains("H2ProductionAgentAdapter",StringComparison.Ordinal),"H2 app no longer composes the production Agent adapter.");
            Check(!facade.Contains("AgentRunner",StringComparison.Ordinal),"Normal Agent facade regained AgentRunner.");
            Check(File.Exists(Path.Combine(repo,"experiments","H2AgentLab","AgentRunner.cs")),"Historical AgentRunner baseline was deleted without parity evidence.");
            Check(report.Contains("No parity-safe runtime deletion was identified",StringComparison.Ordinal),
                "Cleanup disposition for retained compatibility code is missing.");
        });

        test("AR-090 historical MB report is explicitly separated from current reliability handoff",()=>{
            Check(historical.Contains("HISTORICAL MB-120 BASELINE",StringComparison.Ordinal),
                "Historical architecture report lacks a current-status warning.");
            Check(historical.Contains("AR-090/FINAL_HANDOFF_REPORT.md",StringComparison.Ordinal),
                "Historical architecture report does not point to current handoff.");
            Check(report.Contains("historical MB-120 baseline",StringComparison.OrdinalIgnoreCase),
                "Current handoff does not classify the old architecture report.");
        });

        test("AR-090 README points to current handoff and no longer labels 590/590 as latest",()=>{
            Check(readme.Contains("AR-090/FINAL_HANDOFF_REPORT.md",StringComparison.Ordinal),
                "README does not link the current AR handoff.");
            Check(!readme.Contains("Kiểm tra ứng dụng gần nhất: **590/590 đạt**",StringComparison.Ordinal),
                "README still labels the historical 590/590 snapshot as latest.");
        });

        test("AR-090 capability matrix preserves clean-runner and provider boundaries",()=>{
            foreach(var claim in new[]{"E3 clean Windows runner profile","physical clean machine","web search provider",
                "browser CDP/live tab","native Word/Excel","live AutoCAD drawing","No secret, API key"})
                Check(report.Contains(claim,StringComparison.OrdinalIgnoreCase),"Final capability matrix lost boundary: "+claim);
            Check(report.Contains("10917395115",StringComparison.Ordinal)
                && report.Contains("2f3a0c995307ee4615ceb647ff66322a795fc90f",StringComparison.Ordinal),
                "Pre-AR090 portable lineage is missing.");
        });
    }

    private static string FindRepoRoot()
    {
        var current=new DirectoryInfo(Directory.GetCurrentDirectory());
        while(current is not null)
        {
            if(File.Exists(Path.Combine(current.FullName,"AGENTS.md"))
                && Directory.Exists(Path.Combine(current.FullName,"experiments")))
                return current.FullName;
            current=current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}

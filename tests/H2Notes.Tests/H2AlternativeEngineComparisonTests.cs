using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using H2AgentLab;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Transport;
using H2Notes.Core;

internal static class H2AlternativeEngineComparisonTests
{
    private const string Answer = "AR072-OK";
    private const string Prompt = "AR-072 deterministic read-only corpus marker=ALPHA. Return exactly AR072-OK.";

    public static void Run(Action<string,Action> test)
    {
        var quick = new Lazy<ComparisonReport>(() => BuildReport(3));

        test("AR-072 E1 mandatory engine mapping rejects frozen AgentRunner as drop-in replacement",()=>{
            var matrix=ContractMatrix();
            Check(matrix.Count==7 && matrix.All(x=>x.Required),"AR-072 mandatory mapping corpus drifted.");
            Check(matrix.All(x=>x.CurrentEngine),"Current H2 AgentRuntime lost a mandatory public mapping.");
            Check(matrix.Count(x=>!x.CandidateEngine)>=5,
                "Frozen AgentRunner unexpectedly satisfies enough production mapping to be a drop-in engine.");
        });

        test("AR-072 E2 same read-only corpus completes on current and candidate baseline",()=>{
            var report=quick.Value;
            Check(report.Current.Completions==report.Iterations && report.Current.Errors==0,
                "Current engine did not complete the deterministic comparison corpus.");
            Check(report.Candidate.Completions==report.Iterations && report.Candidate.Errors==0,
                "Candidate baseline did not complete the deterministic direct-response corpus.");
            Check(report.Current.LastAnswer==Answer && report.Candidate.LastAnswer==Answer,
                "The compared engines did not receive/return the same deterministic corpus result.");
        });

        test("AR-072 E1 p50 p95 metrics are recorded but never decide replacement alone",()=>{
            var report=quick.Value;
            Check(report.Current.P50Milliseconds>=0 && report.Current.P95Milliseconds>=report.Current.P50Milliseconds,
                "Current latency percentiles are invalid.");
            Check(report.Candidate.P50Milliseconds>=0 && report.Candidate.P95Milliseconds>=report.Candidate.P50Milliseconds,
                "Candidate latency percentiles are invalid.");
            Check(report.Recommendation=="KEEP_CURRENT" && report.DecisionBasis.Contains("mandatory",StringComparison.OrdinalIgnoreCase),
                "Latency accidentally overrode mandatory contract parity.");
        });

        test("AR-072 E1 external alternative remains NotTested without authorized package or license review",()=>{
            var external=quick.Value.ExternalCandidate;
            Check(external.Status=="NotTested" && external.LicenseReview=="NotEvaluated"
                && external.Dependencies=="NotEvaluated",
                "External engine was treated as tested without an authorized package/environment.");
        });

        test("AR-072 E1 production composition still selects H2ProductionAgentAdapter not AgentRunner",()=>{
            var app=ReadRepoFile("src","H2Notes.Avalonia","App.axaml.cs");
            Check(app.Contains("H2ProductionAgentAdapter",StringComparison.Ordinal)
                && !app.Contains("new AgentRunner",StringComparison.Ordinal),
                "AR-072 comparison changed the production engine/composition root.");
        });

        test("AR-072 E2 comparison records adapter cost and prohibits two-engine task control",()=>{
            var report=quick.Value;
            Check(report.Candidate.MissingMandatoryMappings>0
                && report.Candidate.AdapterCostClass=="High",
                "Candidate adapter cost is not tied to missing mandatory mappings.");
            Check(!report.ProductionSwitchRequested && !report.ConcurrentPlannerControl,
                "Comparison requested production auto-switch or concurrent planner control.");
        });
    }

    public static int Benchmark(string outputDirectory,int iterations)
    {
        if(iterations is <5 or >100)throw new ArgumentOutOfRangeException(nameof(iterations));
        var output=Path.GetFullPath(outputDirectory);
        if(Directory.Exists(output))throw new IOException("AR-072 benchmark output directory must be new.");
        Directory.CreateDirectory(output);
        try
        {
            var report=BuildReport(iterations);
            File.WriteAllText(Path.Combine(output,"comparison.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            File.WriteAllText(Path.Combine(output,"comparison.md"),Markdown(report));
            Console.WriteLine(JsonSerializer.Serialize(new{
                report.CorpusId,report.Iterations,report.Recommendation,
                current=new{report.Current.P50Milliseconds,report.Current.P95Milliseconds,report.Current.Errors,report.Current.Completions},
                candidate=new{report.Candidate.P50Milliseconds,report.Candidate.P95Milliseconds,report.Candidate.Errors,report.Candidate.Completions,
                    report.Candidate.MissingMandatoryMappings,report.Candidate.AdapterCostClass},
                external=report.ExternalCandidate.Status,
                report.ProductionSwitchRequested,report.ConcurrentPlannerControl
            }));
            return report.Current.Errors==0
                && report.Candidate.Errors==0
                && report.Current.Completions==iterations
                && report.Candidate.Completions==iterations
                && report.Recommendation=="KEEP_CURRENT"
                && report.Candidate.MissingMandatoryMappings>0
                && !report.ProductionSwitchRequested
                && !report.ConcurrentPlannerControl
                && report.ExternalCandidate.Status=="NotTested" ? 0:1;
        }
        catch
        {
            try{Directory.Delete(output,true);}catch{}
            throw;
        }
    }

    private static ComparisonReport BuildReport(int iterations)
    {
        var root=Path.Combine(Path.GetTempPath(),"h2-ar072-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace=Path.Combine(root,"workspace");Directory.CreateDirectory(workspace);
            File.WriteAllText(Path.Combine(workspace,"dataset.txt"),"marker=ALPHA");
            var current=MeasureCurrent(Path.Combine(root,"current"),workspace,iterations);
            var candidate=MeasureCandidate(Path.Combine(root,"candidate"),workspace,iterations);
            var matrix=ContractMatrix();
            var missing=matrix.Count(x=>x.Required&&!x.CandidateEngine);
            candidate=candidate with{MissingMandatoryMappings=missing,AdapterCostClass=missing==0?"Low":missing<=2?"Medium":"High"};
            return new(
                SchemaVersion:1,
                CorpusId:"ar072-direct-readonly-v1",
                PromptSha256:Hash(Prompt),
                DatasetSha256:Hash(File.ReadAllText(Path.Combine(workspace,"dataset.txt"))),
                Permission:"read-only / no external side effects",
                Iterations:iterations,
                MeasurementScope:"Deterministic in-process control-plane overhead only; no real network, model-quality, native-app or external-engine claim.",
                Current:current,
                Candidate:candidate,
                ContractMatrix:matrix,
                ExternalCandidate:new("external-alternative-engine","NotTested",
                    "No user-authorized external engine package/endpoint was supplied for AR-072 E2; do not install or call one implicitly.",
                    "NotEvaluated","NotEvaluated"),
                Recommendation:missing>0?"KEEP_CURRENT":"FURTHER_TRIAL",
                DecisionBasis:missing>0
                    ? $"Candidate misses {missing} mandatory task/revision/job/approval/event mappings; latency cannot override compatibility."
                    : "Mandatory mappings are present; a separately authorized real-environment trial is still required before replacement.",
                ProductionSwitchRequested:false,
                ConcurrentPlannerControl:false);
        }
        finally{try{Directory.Delete(root,true);}catch{}}
    }

    private static EngineMeasurement MeasureCurrent(string stateRoot,string workspace,int iterations)
    {
        Directory.CreateDirectory(stateRoot);
        var factory=new DirectTransportFactory(Answer);
        using var adapter=new H2ProductionAgentAdapter(stateRoot,
            ()=>new(new AiProfile{Protocol=AiProtocol.OpenAiChat,BaseUrl="https://example.test/v1",Model="ar072-fixture"},""),factory);
        _=RunCurrent(adapter,workspace); // warm-up
        var samples=new List<double>();var errors=0;var completions=0;var last="";
        for(var i=0;i<iterations;i++)
        {
            var sw=Stopwatch.StartNew();
            try
            {
                var done=RunCurrent(adapter,workspace);sw.Stop();samples.Add(sw.Elapsed.TotalMilliseconds);
                last=done.FinalText??"";
                if(done.Status==H2AgentTaskStatus.Completed&&last==Answer&&done.TaskId!=Guid.Empty&&done.TurnId is not null
                    && done.GoalState is not null)completions++;else errors++;
            }
            catch{sw.Stop();samples.Add(sw.Elapsed.TotalMilliseconds);errors++;}
        }
        return Measurement("H2 AgentRuntime / production adapter","current",samples,errors,completions,last,0,"None",
            "Existing product/runtime dependencies only.",
            "Existing repository component; no new third-party package/license introduced by this benchmark.");
    }

    private static H2AgentTaskSummary RunCurrent(H2ProductionAgentAdapter adapter,string workspace)
    {
        var context=new H2AgentTaskContext(workspace,"AR-072 dataset marker ALPHA",
            ThreadId:Guid.NewGuid(),TurnId:Guid.NewGuid());
        var id=adapter.StartTaskAsync(null,Prompt,context,readOnly:true).GetAwaiter().GetResult();
        var deadline=DateTime.UtcNow.AddSeconds(8);
        while(DateTime.UtcNow<deadline)
        {
            var summary=adapter.GetTaskSummary(id);
            if(H2AgentActivity.IsTerminal(summary.Status))return summary;
            Thread.Sleep(2);
        }
        throw new TimeoutException("Current engine benchmark task did not terminate.");
    }

    private static EngineMeasurement MeasureCandidate(string stateRoot,string workspace,int iterations)
    {
        Directory.CreateDirectory(stateRoot);
        using var tools=new AgentTools(new SafeWorkspace(workspace),stateRoot,(_,_)=>Task.FromResult(false),(_,_)=>{}){ReadOnly=true};
        using var runner=new AgentRunner(new DirectOpenAiHandler(Answer));
        var profile=new AiProfile{Protocol=AiProtocol.OpenAiChat,BaseUrl="https://example.test/v1",Model="ar072-fixture"};
        _=RunCandidate(runner,profile,tools,workspace); // warm-up
        var samples=new List<double>();var errors=0;var completions=0;var last="";
        for(var i=0;i<iterations;i++)
        {
            var sw=Stopwatch.StartNew();
            try
            {
                last=RunCandidate(runner,profile,tools,workspace);sw.Stop();samples.Add(sw.Elapsed.TotalMilliseconds);
                if(last==Answer)completions++;else errors++;
            }
            catch{sw.Stop();samples.Add(sw.Elapsed.TotalMilliseconds);errors++;}
        }
        return Measurement("Frozen AgentRunner","candidate-baseline",samples,errors,completions,last,0,"Pending",
            "Existing frozen-v1 repository dependencies only; production adaptation would be additional work.",
            "Same-repository baseline in this E2 comparison; no external package license was evaluated.");
    }

    private static string RunCandidate(AgentRunner runner,AiProfile profile,AgentTools tools,string workspace)
    {
        var session=new LabSession{Workspace=workspace};var final="";
        runner.Run(profile,"",session,tools,Prompt,(kind,text)=>{if(kind=="final")final=text;},()=>{},CancellationToken.None)
            .GetAwaiter().GetResult();
        return final;
    }

    private static EngineMeasurement Measurement(string name,string role,List<double> samples,int errors,int completions,
        string last,int missing,string cost,string dependencies,string license)
    {
        var sorted=samples.Order().ToArray();
        return new(name,role,sorted.Length,Percentile(sorted,.50),Percentile(sorted,.95),errors,completions,
            sorted.Length==0?0:(double)errors/sorted.Length,
            sorted.Length==0?0:(double)completions/sorted.Length,last,missing,cost,dependencies,license);
    }

    private static double Percentile(double[] sorted,double p)
    {
        if(sorted.Length==0)return 0;
        var index=Math.Clamp((int)Math.Ceiling(p*sorted.Length)-1,0,sorted.Length-1);
        return Math.Round(sorted[index],3);
    }

    private static IReadOnlyList<ContractRequirement> ContractMatrix()
        =>[
            new("TaskId","Stable durable task identity",true,true,false,"IH2AgentAdapter/H2AgentTaskSummary vs AgentRunner.Run"),
            new("TurnId","Provider/user-turn identity and reconnect dedup",true,true,false,"H2AgentTaskSummary.TurnId; no AgentRunner turn contract"),
            new("GoalRevision","Durable goal revision/supersession",true,true,false,"H2AgentGoalSnapshot; no AgentRunner goal-revision contract"),
            new("Jobs","Typed job ownership/poll/cancel/reconcile",true,true,false,"H2AgentOperationRecord.Job/recovery; no AgentRunner job contract"),
            new("Approvals","Typed approval identity/event boundary",true,true,false,"H2AgentApproval/RespondToApproval; legacy callback is not durable mapping"),
            new("Events","Typed sequenced progress/events",true,true,false,"H2AgentProgress sequence; AgentRunner emit strings are not durable event identity"),
            new("RestartResume","Restart reconcile/rebase without blind replay",true,true,false,"ResumeTaskAsync/ReconcileInterruptedTask; no AgentRunner resume contract")
        ];

    private static string Markdown(ComparisonReport r)
    {
        var sb=new StringBuilder();
        sb.AppendLine("# AR-072 deterministic alternative-engine comparison");
        sb.AppendLine();
        sb.AppendLine($"Corpus: \`{r.CorpusId}\` · iterations/engine: **{r.Iterations}** · permission: **{r.Permission}**.");
        sb.AppendLine();
        sb.AppendLine("> "+r.MeasurementScope);
        sb.AppendLine();
        sb.AppendLine("| Engine | p50 ms | p95 ms | errors | completions | missing mandatory mappings | adapter cost |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---|");
        foreach(var e in new[]{r.Current,r.Candidate})
            sb.AppendLine($"| {e.Name} | {e.P50Milliseconds:F3} | {e.P95Milliseconds:F3} | {e.Errors} | {e.Completions}/{e.Samples} | {e.MissingMandatoryMappings} | {e.AdapterCostClass} |");
        sb.AppendLine();
        sb.AppendLine("## Mandatory mapping");
        foreach(var x in r.ContractMatrix)
            sb.AppendLine($"- {(x.CandidateEngine?"PASS":"MISSING")} **{x.Id}** — required={x.Required}; {x.Evidence}");
        sb.AppendLine();
        sb.AppendLine($"External engine: **{r.ExternalCandidate.Status}** — {r.ExternalCandidate.Reason}");
        sb.AppendLine();
        sb.AppendLine($"## Recommendation: {r.Recommendation}");
        sb.AppendLine(r.DecisionBasis);
        sb.AppendLine();
        sb.AppendLine("No production switch was requested and no two planners controlled the same task.");
        return sb.ToString();
    }

    private static string ReadRepoFile(params string[] parts)
    {
        for(var dir=new DirectoryInfo(AppContext.BaseDirectory);dir is not null;dir=dir.Parent)
        {
            var path=Path.Combine([dir.FullName,..parts]);
            if(File.Exists(path))return File.ReadAllText(path);
        }
        throw new FileNotFoundException("Repository source not found for AR-072 architecture guard.",Path.Combine(parts));
    }

    private static string Hash(string text)
        =>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}

    private sealed class DirectTransportFactory(string answer):IAgentTransportFactory
    {
        public IAgentTransport Create(AiProfile profile,string apiKey,AgentRunTelemetry telemetry)=>new DirectTransport(answer);
    }

    private sealed class DirectTransport(string answer):IAgentTransport
    {
        public AgentTransportCapabilities Capabilities=>AgentTransportCapabilities.ChatCompletionsFallback;
        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(AgentTransportStartRequest request,
            [EnumeratorCancellation]CancellationToken cancellationToken=default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return AgentTransportEvent.TextDeltaEvent(answer);
            yield return AgentTransportEvent.Complete();
            await Task.CompletedTask;
        }
        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(AgentTransportContinuationRequest request,
            [EnumeratorCancellation]CancellationToken cancellationToken=default)
        {
            await Task.Yield();
            throw new InvalidOperationException("AR-072 direct corpus does not require continuation.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
        public void Cancel(){}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }

    private sealed class DirectOpenAiHandler(string answer):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            var payload=JsonSerializer.Serialize(new{
                choices=new[]{new{delta=new{content=answer},finish_reason="stop"}}
            });
            var response=new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content=new StringContent("data: "+payload+"\n\n",Encoding.UTF8,"text/event-stream")
            };
            return Task.FromResult(response);
        }
    }

    internal sealed record ContractRequirement(string Id,string Requirement,bool Required,bool CurrentEngine,bool CandidateEngine,string Evidence);
    internal sealed record EngineMeasurement(string Name,string Role,int Samples,double P50Milliseconds,double P95Milliseconds,
        int Errors,int Completions,double ErrorRate,double CompletionRate,string LastAnswer,int MissingMandatoryMappings,
        string AdapterCostClass,string Dependencies,string LicenseReview);
    internal sealed record ExternalCandidate(string Name,string Status,string Reason,string Dependencies,string LicenseReview);
    internal sealed record ComparisonReport(int SchemaVersion,string CorpusId,string PromptSha256,string DatasetSha256,string Permission,
        int Iterations,string MeasurementScope,EngineMeasurement Current,EngineMeasurement Candidate,
        IReadOnlyList<ContractRequirement> ContractMatrix,ExternalCandidate ExternalCandidate,string Recommendation,string DecisionBasis,
        bool ProductionSwitchRequested,bool ConcurrentPlannerControl);
}

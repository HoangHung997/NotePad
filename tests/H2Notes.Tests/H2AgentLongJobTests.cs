using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using H2AgentLab;
using H2AgentLab.Computer;

internal static class H2AgentLongJobTests
{
    public static void Run(Action<string, Action> test)
    {
        void Case(string name, Func<Task> body) => test("AR-040 job " + name, () => Task.Run(body).GetAwaiter().GetResult());
        Case("one start identity survives repeated polls and identical requests", async () =>
        {
            using var f = new Fixture(); var job = f.Start("quiet");
            await f.WaitFor("quiet-ready");
            var duplicate = f.Start("quiet"); Check(job.JobId == duplicate.JobId && job.ProcessId == duplicate.ProcessId, "Duplicate process started.");
            var before = Stopwatch.StartNew(); var running = await f.Service.PollJobAsync(f.Owner, job.JobId, TimeSpan.FromMilliseconds(75));
            Check(!running.Terminal && before.Elapsed < TimeSpan.FromSeconds(2), "Poll became the job lifetime.");
            Throws(() => f.Service.JobResult(f.Owner, job.JobId));
            var end = await f.Service.CancelJobAsync(f.Owner, job.JobId); Check(end.Status == "Cancelled" && end.AllJobProcessesExited && end.StreamsDrained, "Cancel not observed.");
            Check(f.Start("quiet").JobId == job.JobId, "Terminal request was replayed."); f.Record("identity", end);
        });
        Case("poll request cancellation does not terminate the owned job", async () =>
        {
            using var f = new Fixture(); var job = f.Start("quiet"); await f.WaitFor("quiet-ready");
            using var stopPoll = new CancellationTokenSource(50);
            try { await f.Service.PollJobAsync(f.Owner, job.JobId, TimeSpan.FromSeconds(2), stopPoll.Token); throw new Exception("Expected cancelled poll."); }
            catch (OperationCanceledException ex) { Check(ex.CancellationToken == stopPoll.Token, "Poll token changed."); }
            Check(!(await f.Service.PollJobAsync(f.Owner, job.JobId, TimeSpan.Zero)).Terminal, "Poll cancellation killed process.");
            f.Record("poll-cancel", await f.Service.CancelJobAsync(f.Owner, job.JobId));
        });
        Case("owner cancellation stops the whole job without cancelling another owner", async () =>
        {
            using var f = new Fixture(); using var other = new Fixture(); using var stop = new CancellationTokenSource();
            var a = f.Start("tree", ownerToken: stop.Token); var b = other.Start("quiet");
            await f.WaitFor("tree-ready"); await other.WaitFor("quiet-ready"); stop.Cancel();
            var end = await f.Terminal(a.JobId); Check(end.Status == "Cancelled" && end.AllJobProcessesExited, "Owned descendants remained.");
            Check(!(await other.Service.PollJobAsync(other.Owner, b.JobId, TimeSpan.Zero)).Terminal, "Other job was killed.");
            f.AssertAllExited(); f.Record("owner-cancel", end);
        });
        Case("a root exit does not lose ownership of its inherited-pipe child", async () =>
        {
            using var f = new Fixture(); var job = f.Start("orphan"); await f.WaitFor("orphan-ready");
            await Task.Delay(150); var running = await f.Service.PollJobAsync(f.Owner, job.JobId, TimeSpan.Zero);
            Check(running.RootExited && !running.AllJobProcessesExited && !running.Terminal, "Root exit fabricated tree completion.");
            var end = await f.Service.CancelJobAsync(f.Owner, job.JobId); Check(end.AllJobProcessesExited && end.StreamsDrained, "Orphan child escaped its job.");
            f.AssertAllExited(); f.Record("orphan-cancel", end);
        });
        Case("output remains paged bounded and exact while both streams are drained", async () =>
        {
            using var f = new Fixture(cap: 8192); var job = f.Start("flood"); var end = await f.Terminal(job.JobId);
            Check(end.Status == "Succeeded" && end.StreamsDrained && !end.OutputComplete, "Flood did not drain or truncation hidden.");
            var collected = new StringBuilder(); string? cursor = null;
            do { var page = f.Service.ReadJobOutput(f.Owner, job.JobId, "stdout", cursor, 127); collected.Append(page.Text); cursor = page.NextCursor;
                Check(page.Text.Length <= 127 && page.Truncated, "Page bound/truncation mismatch."); } while (cursor is not null);
            Check(collected.ToString() == new string('x', 8192), "Paged retained prefix changed.");
            var error = f.Service.ReadJobOutput(f.Owner, job.JobId, "stderr"); Check(error.Text == new string('y',4096), "stderr missing.");
            f.Record("flood", end);
        });
        Case("stdin is opt-in and an input receipt cannot write twice", async () =>
        {
            using var f = new Fixture(); var job = f.Start("input", input: true); await f.WaitFor("input-ready");
            var one = await f.Service.WriteJobInputAsync(f.Owner, job.JobId, "first", "Hưng\n");
            var repeated = await f.Service.WriteJobInputAsync(f.Owner, job.JobId, "first", "Hưng\n"); Check(one == repeated, "Input receipt changed.");
            try { await f.Service.WriteJobInputAsync(f.Owner, job.JobId, "first", "different\n"); throw new Exception("Conflict accepted."); } catch (InvalidOperationException) { }
            await f.Service.WriteJobInputAsync(f.Owner, job.JobId, "second", "B\n", true);
            var end = await f.Terminal(job.JobId); Check(end.Status == "Succeeded", "Stdin job failed.");
            Check(f.Service.ReadJobOutput(f.Owner, job.JobId,"stdout").Text == "Hưng|B", "Duplicate stdin effect."); f.Record("stdin", end);
        });
        Case("foreign job and stream cursor never expand task authority", async () =>
        {
            using var f = new Fixture(); var job = f.Start("quiet"); await f.WaitFor("quiet-ready");
            Throws(() => f.Service.ReadJobOutput(Guid.NewGuid(), job.JobId,"stdout"));
            try { await f.Service.CancelJobAsync(Guid.NewGuid(), job.JobId); throw new Exception("Foreign cancel accepted."); } catch (KeyNotFoundException) { }
            Throws(() => f.Service.ReadJobOutput(f.Owner, job.JobId,"stderr", job.JobId+":stdout:0"));
            Throws(() => f.Service.ReadJobOutput(f.Owner, job.JobId,"stdout", job.JobId+":stdout:999999"));
            try { await f.Service.WriteJobInputAsync(f.Owner, job.JobId,"denied","x"); throw new Exception("Stdin opt-in missing."); } catch (UnauthorizedAccessException) { }
            f.Record("scope", await f.Service.CancelJobAsync(f.Owner, job.JobId));
        });
        Case("silent job has a finite no-output deadline", async () =>
        {
            using var f = new Fixture(); var job = f.Start("quiet", lifetime: 15, idle: 3); var end = await f.Terminal(job.JobId);
            Check(end.Status == "TimedOut" && end.Reason == "no_output_deadline" && end.AllJobProcessesExited, "Silent job lacked explicit finite deadline."); f.Record("idle", end);
        });
        Case("heartbeat cannot reset the absolute execution lifetime", async () =>
        {
            using var f = new Fixture(); var job = f.Start("heartbeat", lifetime: 4, idle: 3); var end = await f.Terminal(job.JobId);
            Check(end.Status == "TimedOut" && end.Reason == "lifetime_exceeded" && end.StdoutObservedCharacters > 0,
                "Liveness hid lifetime exhaustion."); f.Record("lifetime", end);
        });
        Case("pre-cancel and conflicting request are rejected without another dispatch", async () =>
        {
            using var f = new Fixture(); using var stop = new CancellationTokenSource(); stop.Cancel();
            try { f.Start("quiet", ownerToken:stop.Token); throw new Exception("Pre-cancel dispatched."); } catch (OperationCanceledException) { }
            Check(!Directory.EnumerateFiles(f.Root,"*.identity").Any(), "Pre-cancel effect.");
            var job = f.Start("quiet"); await f.WaitFor("quiet-ready"); Throws(() => f.Start("heartbeat"));
            Check(Directory.EnumerateFiles(f.Root,"*.identity").Count() == 1, "Conflicting request dispatched.");
            f.Record("preflight", await f.Service.CancelJobAsync(f.Owner, job.JobId));
        });
        Case("host exit policy closes the real job even after abrupt owner termination", async () =>
        {
            using var f = new Fixture(); using var host = Process.Start(f.StartInfo("host-crash")) ?? throw new IOException("Fixture host not started.");
            await f.WaitFor("host-ready"); host.Kill(); await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var clock = Stopwatch.StartNew(); while (f.AnyAlive() && clock.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(25);
            f.AssertAllExited(); f.Record("host-crash", new { HostPid=host.Id, HostExited=host.HasExited, AllObservedExited=true });
        });
        Case("journal admission failure cannot resume suspended application code", async () =>
        {
            using var f = new Fixture();
            Throws(() => f.Start("effect", admission: _ => throw new IOException("Injected journal admission failure.")));
            await Task.Delay(150); Check(!File.Exists(Path.Combine(f.Root,"effect")), "Failed admission executed application code.");
            var snapshot = f.Service.ObserveJobs(f.Owner).Single(); Check(snapshot.Terminal && snapshot.AllJobProcessesExited, "Unowned suspended process."); f.Record("admission", snapshot);
        });
    }

    public static int Probe(string mode, string root, string nonce)
    {
        if (!Guid.TryParseExact(nonce,"N",out _) || !new[]{"quiet","tree","orphan","flood","input","heartbeat","host-crash","effect"}.Contains(mode)) return 64;
        root = Path.GetFullPath(root);
        if (!root.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(root)!="H2-AR040-job-"+nonce || File.ReadAllText(Path.Combine(root,".owner"))!=nonce) return 65;
        using var self=Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(root,self.Id+".identity"),self.StartTime.ToUniversalTime().Ticks.ToString());
        Console.OutputEncoding=new UTF8Encoding(false); Console.InputEncoding=new UTF8Encoding(false);
        if(mode=="host-crash")
        {
            using var service=new ProcessShellCapabilities(new SafeWorkspace(root),new(new HashSet<string>{Environment.ProcessPath!},true,true,TimeSpan.FromSeconds(30)));
            service.StartJob(Guid.NewGuid(),"fixture-r1","child",Environment.ProcessPath!,Args("tree",root,nonce), lifetime:TimeSpan.FromSeconds(30));
            if(!SpinWait.SpinUntil(()=>File.Exists(Path.Combine(root,"tree-ready")),10000))return 66;
            File.WriteAllText(Path.Combine(root,"host-ready"),"ready");Thread.Sleep(30000);return 0;
        }
        if(mode is "tree" or "orphan")
        {
            using var child=Process.Start(MakeStart("quiet",root,nonce))!;
            if(!SpinWait.SpinUntil(()=>File.Exists(Path.Combine(root,"quiet-ready")),10000))return 66;
            File.WriteAllText(Path.Combine(root,mode+"-ready"),"ready");if(mode=="tree")Thread.Sleep(30000);return 0;
        }
        File.WriteAllText(Path.Combine(root,mode+"-ready"),"ready");
        switch(mode)
        {
            case "flood": for(var i=0;i<256;i++){Console.Write(new string('x',4096));Console.Error.Write(new string('y',4096));}return 0;
            case "input": var a=Console.ReadLine();var b=Console.ReadLine();Console.Write(a+"|"+b);return 0;
            case "heartbeat": for(var i=0;i<150;i++){Console.Write(".");Console.Out.Flush();Thread.Sleep(200);}return 0;
            case "effect": File.WriteAllText(Path.Combine(root,"effect"),"effect");return 0;
            default: Thread.Sleep(30000);return 0;
        }
    }
    private static string[] Args(string mode,string root,string nonce)
    {
        var values=new List<string>();if(Path.GetFileNameWithoutExtension(Environment.ProcessPath)=="dotnet")values.Add(Assembly.GetExecutingAssembly().Location);
        values.AddRange(["--ar040-job-probe",mode,root,nonce]);return values.ToArray();
    }
    private static ProcessStartInfo MakeStart(string mode,string root,string nonce)
    {var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};foreach(var a in Args(mode,root,nonce))start.ArgumentList.Add(a);return start;}
    private static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
    private static void Throws(Action action){try{action();}catch(Exception e)when(e is ArgumentException or InvalidOperationException or KeyNotFoundException or IOException){return;}throw new Exception("Expected rejection.");}
    private sealed class Fixture:IDisposable
    {
        public Guid Owner{get;}=Guid.NewGuid();public string Nonce{get;}=Guid.NewGuid().ToString("N");public string Root{get;}
        public ProcessShellCapabilities Service{get;}
        public Fixture(int cap=16384){Root=Path.Combine(Path.GetTempPath(),"H2-AR040-job-"+Nonce);Directory.CreateDirectory(Root);File.WriteAllText(Path.Combine(Root,".owner"),Nonce);Service=new(new SafeWorkspace(Root),new(new HashSet<string>{Environment.ProcessPath!},true,true,TimeSpan.FromSeconds(30),cap));}
        public ProcessJobSnapshot Start(string mode,bool input=false,int lifetime=30,int? idle=null,CancellationToken ownerToken=default,Action<ProcessJobSnapshot>? admission=null)
            =>Service.StartJob(Owner,"fixture-r1","request",Environment.ProcessPath!,Args(mode,Root,Nonce),lifetime:TimeSpan.FromSeconds(lifetime),idleTimeout:TimeSpan.FromSeconds(idle??lifetime),allowStdin:input,ownerCancellation:ownerToken,beforeResume:admission);
        public ProcessStartInfo StartInfo(string mode)=>MakeStart(mode,Root,Nonce);
        public async Task WaitFor(string name){var clock=Stopwatch.StartNew();while(!File.Exists(Path.Combine(Root,name))){if(clock.Elapsed>TimeSpan.FromSeconds(12))throw new TimeoutException("Missing marker "+name);await Task.Delay(25);}}
        public async Task<ProcessJobSnapshot> Terminal(string id){for(var i=0;i<20;i++){var s=await Service.PollJobAsync(Owner,id,TimeSpan.FromSeconds(1));if(s.Terminal)return s;}throw new TimeoutException("Job failed to become terminal.");}
        public bool AnyAlive()=>Directory.EnumerateFiles(Root,"*.identity").Any(p=>Live(int.Parse(Path.GetFileNameWithoutExtension(p)),long.Parse(File.ReadAllText(p))));
        private static bool Live(int id,long ticks){try{using var p=Process.GetProcessById(id);return !p.HasExited&&p.StartTime.ToUniversalTime().Ticks==ticks;}catch(ArgumentException){return false;}catch(InvalidOperationException){return false;}}
        public void AssertAllExited()=>Check(!AnyAlive(),"Observed child still alive after job stop.");
        public void Record(string name,object value){var folder=Environment.GetEnvironmentVariable("H2_AR040_EVIDENCE_DIR");if(folder is null)return;Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"job-"+name+".json"),JsonSerializer.Serialize(new{Case=name,Owner,Observation=value,IdentityFiles=Directory.EnumerateFiles(Root,"*.identity").Select(p=>new{Pid=Path.GetFileNameWithoutExtension(p),Start=File.ReadAllText(p)}).ToArray(),Boundary="Owned local subprocesses, not native Office or model acceptance."},new JsonSerializerOptions{WriteIndented=true}));}
        public void Dispose(){Service.Dispose();foreach(var file in Directory.EnumerateFiles(Root,"*.identity")){var id=int.Parse(Path.GetFileNameWithoutExtension(file));var ticks=long.Parse(File.ReadAllText(file));try{using var p=Process.GetProcessById(id);if(!p.HasExited&&p.StartTime.ToUniversalTime().Ticks==ticks){p.Kill(true);p.WaitForExit(5000);}}catch(ArgumentException){}catch(InvalidOperationException){}}Directory.Delete(Root,true);}
    }
}

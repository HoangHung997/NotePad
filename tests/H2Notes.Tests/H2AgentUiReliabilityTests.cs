using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using H2Notes.Avalonia.Controls;
using H2Notes.Core;

internal static class H2AgentUiReliabilityTests
{
    public static void Run(Action<string,Action> test)
    {
        test("AR-081 E1 typed UI projection distinguishes reliability states without model inference", () =>
        {
            var now=DateTime.UtcNow;
            var running=Summary(H2AgentTaskStatus.Running,now);
            Check(AgentUiProjector.Project(running,[Progress(0,"context","compacting","checkpoint")]).State==AgentUiState.Compacting,
                "Compacting state was not projected.");
            Check(AgentUiProjector.Project(running,[Progress(0,"transport","reconnecting","socket")]).State==AgentUiState.Reconnecting,
                "Reconnecting state was not projected.");

            var job=new H2AgentToolOutcome(Guid.NewGuid(),"op-job",H2ToolRunStatus.Running,
                H2ToolMutationEffect.None,H2ToolVerificationStatus.NotRun,false,null,null,"job-42",
                null,null,null,null);
            Check(AgentUiProjector.Project(running,[Progress(0,"tool","job-running","exec") with { ToolOutcome=job }]).State==AgentUiState.WaitingForJob,
                "WaitingForJob state was not projected.");

            var cancel=AgentUiProjector.Project(running,[Progress(0,"lifecycle","cancel-requested","stop")]);
            Check(cancel.State==AgentUiState.CancelRequested && cancel.NeedsAttention,
                "CancelRequested state was not projected.");

            var op=Operation();
            var reconcile=Summary(H2AgentTaskStatus.Blocked,now) with
            { Recovery=new H2AgentRecoverySnapshot(true,true,12,[op]) };
            Check(AgentUiProjector.Project(reconcile).State==AgentUiState.ReconcileRequired,
                "ReconcileRequired state was not projected.");

            var interrupted=Summary(H2AgentTaskStatus.Blocked,now) with
            { Recovery=new H2AgentRecoverySnapshot(true,false,13,[op with { ReconciliationState="Verified" }]) };
            Check(AgentUiProjector.Project(interrupted).State==AgentUiState.Interrupted,
                "Interrupted state was not projected.");

            var unverified=Summary(H2AgentTaskStatus.Completed,now) with
            { GoalState=new H2AgentGoalSnapshot("r2",[],[],["r1"]) };
            Check(AgentUiProjector.Project(unverified).State==AgentUiState.AppliedUnverified,
                "AppliedUnverified mutation was flattened into generic completion.");

            var verified=Summary(H2AgentTaskStatus.Completed,now) with
            { Evidence=[new H2AgentEvidence("ev","verification",new string('a',64),"verified",VerificationPassed:true)] };
            var verifiedProjection=AgentUiProjector.Project(verified);
            Check(verifiedProjection.State==AgentUiState.Verified && verifiedProjection.IsVerified,
                "Verified task was not visibly distinct.");
        });

        test("AR-081 E2 one thousand heartbeat events stay durable but collapse to bounded UI rows", () =>
        {
            var now=DateTime.UtcNow;
            var events=Enumerable.Range(0,1000)
                .Select(i=>Progress(i,"job","heartbeat","alive")).ToArray();
            var collapsed=AgentUiProjector.CollapseForDisplay(events,40);
            Check(collapsed.Count==1 && collapsed[0].Count==1000,
                "Heartbeat coalescing changed durable event identity or failed to collapse.");

            var turn=new AgentTurnView();
            var watch=Stopwatch.StartNew();
            Check(turn.Present(null,new H2AgentTaskObservation(Summary(H2AgentTaskStatus.Running,now),events)),
                "Initial 1000-event projection did not render.");
            watch.Stop();
            var window=new Window { Width=620,Height=420,Content=turn };window.Show();Pump(window);
            try
            {
                var rows=turn.GetVisualDescendants().Count(x=>x.Name=="AgentActivityRow");
                Check(turn.EventCount==1000,"Durable event count was truncated by UI collapse.");
                Check(rows<=2,"Heartbeat collapse created unbounded visual rows.");
                Check(watch.Elapsed<TimeSpan.FromSeconds(3),
                    "1000-event projection exceeded the bounded headless responsiveness proxy.");
            }
            finally{window.Close();}
        });

        test("AR-081 E2 reconnect and reopen reuse one turn and never duplicate final or progress", () =>
        {
            var now=DateTime.UtcNow;var id=Guid.NewGuid();
            var summary=Summary(H2AgentTaskStatus.Completed,now,id) with { FinalText="Final once" };
            var adapter=new UiAdapter(summary,[Progress(0,"tool","tool-ok","read_file")]);
            var surface=new AgentChatSurface();
            var window=new Window { Width=640,Height=420,Content=surface };window.Show();
            try
            {
                surface.PresentTasks(adapter,[summary],"thread-one");Pump(window);
                surface.PresentTasks(adapter,[summary],"thread-one");Pump(window);
                var turns=surface.Timeline.Children.OfType<AgentTurnView>().ToArray();
                Check(turns.Length==1&&turns[0].EventCount==1,
                    "Reconnect duplicated a turn or replayed progress.");
                Check(turns[0].GetVisualDescendants().OfType<MarkdownMessageView>().Count()==1,
                    "Reconnect duplicated final answer UI.");

                surface.PresentTasks(adapter,[summary],"thread-two");Pump(window);
                Check(surface.Timeline.Children.OfType<AgentTurnView>().Count()==1,
                    "Reopen into another identity leaked old thread UI.");
                Check(adapter.ToolExecutions==0,
                    "Opening/reconnecting the UI executed a tool.");
            }
            finally{window.Close();}
        });

        test("AR-081 E2 up-scroll does not jump and exposes keyboard-reachable latest activity action", () =>
        {
            var now=DateTime.UtcNow;
            var tasks=Enumerable.Range(0,45)
                .Select(i=>Summary(H2AgentTaskStatus.Completed,now.AddSeconds(i),Guid.NewGuid()) with { FinalText="Answer "+i }).ToArray();
            var surface=new AgentChatSurface();
            var window=new Window { Width=520,Height=300,Content=surface };window.Show();
            try
            {
                surface.PresentTasks(null,tasks,"scroll-fixture");Pump(window);
                surface.Scroll.Offset=new Vector(0,0);Pump(window);
                surface.NotifyActivity();Pump(window);
                var latest=surface.GetVisualDescendants().OfType<Button>().Single(x=>x.Name=="AgentLatestActivity");
                Check(latest.IsVisible&&latest.Focusable,
                    "Up-scroll did not preserve position with a reachable new-activity action.");
                Check(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(latest)),
                    "Latest-activity action has no automation name.");
                latest.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(window);
                Check(!latest.IsVisible,"Latest-activity action did not return to follow mode.");
            }
            finally{window.Close();}
        });

        test("AR-081 E2 approval card exposes allow deny and stale decision without fake success", () =>
        {
            var now=DateTime.UtcNow;var id=Guid.NewGuid();
            var stale=new UiAdapter(Summary(H2AgentTaskStatus.WaitingForApproval,now,id),[])
            { ApprovalResult=false };
            var panel=new AgentApprovalPanel();
            panel.Present(stale,id,new H2AgentApproval(Guid.NewGuid(),"Cập nhật tệp","fixture",now.AddMinutes(-30)));
            var window=new Window { Width=500,Height=250,Content=panel };window.Show();Pump(window);
            try
            {
                var approve=panel.GetVisualDescendants().OfType<Button>().Single(x=>x.Name=="AgentApprove");
                var deny=panel.GetVisualDescendants().OfType<Button>().Single(x=>x.Name=="AgentDeny");
                Check(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(approve))
                    && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(deny)),
                    "Approval actions lack accessibility names.");
                approve.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(window);
                Check(panel.IsVisible
                    && panel.GetVisualDescendants().OfType<TextBlock>().Any(x=>(x.Text??"").Contains("không còn hiệu lực",StringComparison.OrdinalIgnoreCase)),
                    "Stale approval was hidden as success.");

                var allowed=new AgentApprovalPanel();
                var acceptAdapter=new UiAdapter(Summary(H2AgentTaskStatus.WaitingForApproval,now,id),[])
                { ApprovalResult=true };
                allowed.Present(acceptAdapter,id,new H2AgentApproval(Guid.NewGuid(),"Cho phép","fixture",now));
                var second=new Window { Width=500,Height=250,Content=allowed };second.Show();Pump(second);
                try
                {
                    allowed.GetVisualDescendants().OfType<Button>().Single(x=>x.Name=="AgentDeny")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(second);
                    Check(!allowed.IsVisible&&acceptAdapter.ApprovalCalls==1,
                        "Approval deny/allow acknowledgement did not close the exact card.");
                }
                finally{second.Close();}
            }
            finally{window.Close();}
        });

        test("AR-081 E2 artifact inspector is accessible and large spreadsheet preview remains paged", () =>
        {
            var root=Path.Combine(Path.GetTempPath(),"h2-ar081-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try
            {
                var path=Path.Combine(root,"large.csv");
                File.WriteAllText(path,"Name,Value\n"+string.Join("\n",Enumerable.Range(0,1000).Select(i=>$"R{i},{i}")));
                var view=new AgentArtifactView(new H2AgentEvidence("artifact-ar081","artifact",null,
                    "Large spreadsheet fixture",LocalPath:path,VerificationPassed:true));
                var window=new Window { Width=700,Height=520,Content=view };window.Show();Pump(window);
                try
                {
                    Check(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(view)),
                        "Artifact inspector has no automation identity.");
                    var picker=view.GetVisualDescendants().OfType<ComboBox>().Single(x=>x.Name=="SpreadsheetSheetPicker");
                    var formula=view.GetVisualDescendants().OfType<CheckBox>().Single(x=>x.Name=="SpreadsheetFormulaMode");
                    Check(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(picker))
                        && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(formula)),
                        "Spreadsheet inspector controls lack automation names.");
                    Check(view.GetVisualDescendants().OfType<Button>().Count()<150,
                        "Large spreadsheet preview eagerly created controls for all 1000 rows.");
                    Check(view.GetVisualDescendants().OfType<TabItem>().Count()==3,
                        "Artifact inspector tabs changed unexpectedly.");
                }
                finally{window.Close();}
            }
            finally{try{Directory.Delete(root,true);}catch{}}
        });
    }

    private static H2AgentTaskSummary Summary(H2AgentTaskStatus status,DateTime now,Guid? id=null)
        => new(id??Guid.NewGuid(),null,"AR-081 fixture",status,null,[],null,null,now,now,Guid.NewGuid(),Guid.NewGuid());

    private static H2AgentProgress Progress(long sequence,string kind,string code,string message)
        => new(sequence,DateTime.UtcNow,kind,code,message);

    private static H2AgentOperationRecord Operation()
        => new(Guid.NewGuid(),"op",Guid.NewGuid(),"rev","call","fixture.write","Dispatched",
            "NotKnown","Unknown",new string('1',64),new string('2',64),null,null);

    private static void Pump(Window? window=null)
    {
        for(var i=0;i<6;i++){Dispatcher.UIThread.RunJobs();window?.UpdateLayout();Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();}
    }

    private static void Check(bool value,string message)
    {
        if(!value)throw new InvalidOperationException(message);
    }

    private sealed class UiAdapter(H2AgentTaskSummary summary,IReadOnlyList<H2AgentProgress> progress) : IH2AgentAdapter
    {
        public bool ApprovalResult{get;set;}
        public int ApprovalCalls{get;private set;}
        public int ToolExecutions{get;private set;}
        public H2AgentTaskObservation ObserveTask(Guid taskId,long afterSequence=-1)
            => new(summary,progress.Where(x=>x.Sequence>afterSequence).ToArray());
        public H2AgentTaskSummary GetTaskSummary(Guid taskId)=>summary;
        public IReadOnlyList<H2AgentTaskSummary> GetRecentTasks(Guid? projectId=null,int limit=50)=>[summary];
        public bool RespondToApproval(Guid taskId,Guid approvalId,bool approved){ApprovalCalls++;return ApprovalResult;}
        public Task<Guid> StartTaskAsync(Guid? projectId,string goal,H2AgentTaskContext? context=null,bool readOnly=true,CancellationToken cancellationToken=default)
            => throw new InvalidOperationException("UI projection must not start a task.");
        public void CancelTask(Guid taskId){}
        public bool AttachProject(Guid taskId,Guid projectId)=>false;
        public H2AgentEvidence? GetEvidence(string evidenceId)=>null;
    }
}

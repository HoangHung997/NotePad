using System.Reflection;
using System.Text.Json;
using H2AgentLab.Integration;
using H2AgentLab.Office;
using H2AgentLab.OfficeHost;
using H2AgentLab.OfficeProtocol;
using H2AgentLab.Tools;
using H2AgentLab.Verification;
using H2Notes.Core;

internal static class H2WordPagedReadTests
{
    public static void Run(Action<string,Action> test)
    {
        test("AR-023 E1 Word cursor and page limits are bounded and typed",()=>{
            Check(WordPagedReadLimits.ParagraphPageSize(0)==WordPagedReadLimits.DefaultParagraphs,"Default paragraph page changed.");
            Check(WordPagedReadRules.Start("p:64","p",0,0,100)==64,"Paragraph cursor decode failed.");
            Expect<ArgumentException>(()=>WordPagedReadRules.Start("t:2","p",0,0,100));
            Expect<ArgumentOutOfRangeException>(()=>WordPagedReadLimits.RangePageSize(WordPagedReadLimits.MaxRangeCharacters+1));
        });

        test("AR-023 E2 fixture reads beyond legacy 2000-paragraph snapshot cap by bounded pages",()=>{
            var backend=new FixtureOfficeBackend(extraWordParagraphs:2500);var reader=(IWordPagedReadBackend)backend;
            var session=backend.DiscoverWord().ActiveSessionId!;string? cursor=null;string? version=null;
            var seen=new List<WordParagraphPageItem>();var pages=0;
            do{
                var page=reader.ReadWordParagraphs(new(session,0,WordPagedReadLimits.MaxParagraphs,false,null,false,cursor,version));
                pages++;if(version is not null)Check(page.ContentVersion==version,"Content version drifted without mutation.");
                version=page.ContentVersion;seen.AddRange(page.Paragraphs);cursor=page.NextCursor;
                Check(page.Metrics.ItemsScanned is >0 and <=WordPagedReadLimits.MaxParagraphs,"Paragraph page exceeded bound.");
                Check(pages<20,"Paragraph paging failed to converge.");
            }while(cursor is not null);
            Check(seen.Count==2503&&seen[0].Text=="UNSAVED-WORD"&&seen[^1].Text=="WORD-LONG-02502","Long Word paging lost first/end markers.");
            Check(pages>1,"Long Word document collapsed into one snapshot.");
        });

        test("AR-023 E2 content version ignores selection but rejects changed content between pages",()=>{
            var backend=new FixtureOfficeBackend(extraWordParagraphs:300);var reader=(IWordPagedReadBackend)backend;var session=backend.DiscoverWord().ActiveSessionId!;
            var first=reader.ReadWordParagraphs(new(session,0,64));Check(first.NextCursor is not null,"Fixture did not page.");
            backend.MoveWordSelectionForFixture(1,1);
            var second=reader.ReadWordParagraphs(new(session,0,64,false,null,false,first.NextCursor,first.ContentVersion));
            Check(second.ContentVersion==first.ContentVersion,"Selection-only change invalidated Word content version.");
            backend.SetWordParagraphForFixture(150,"CHANGED-BETWEEN-PAGES");
            OfficeHostFaultException? stale=null;
            try{_=reader.ReadWordParagraphs(new(session,0,64,false,null,false,second.NextCursor,first.ContentVersion));}
            catch(OfficeHostFaultException ex){stale=ex;}
            Check(stale?.Code=="stale_content"&&stale.NoEffect,"Changed Word content was mixed into old continuation.");
        });

        test("AR-023 E2 continuation requires content version and formatting is lazy",()=>{
            var backend=new FixtureOfficeBackend(extraWordParagraphs:100);var reader=(IWordPagedReadBackend)backend;var session=backend.DiscoverWord().ActiveSessionId!;
            var plain=reader.ReadWordParagraphs(new(session,0,2));Check(plain.Paragraphs.All(p=>p.Runs.Count==0),"Plain paragraph page materialized formatting.");
            var formatted=reader.ReadWordParagraphs(new(session,0,2,true));Check(formatted.Paragraphs.All(p=>p.Runs.Count>0),"Formatting page omitted runs.");
            OfficeHostFaultException? missing=null;
            try{_=reader.ReadWordParagraphs(new(session,0,2,false,null,false,plain.NextCursor,null));}
            catch(OfficeHostFaultException ex){missing=ex;}
            Check(missing?.Code=="invalid_request"&&missing.NoEffect,"Continuation without content version was accepted.");
        });

        test("AR-023 E2 range pages are bounded and preserve exact requested extent",()=>{
            var backend=new FixtureOfficeBackend(extraWordParagraphs:400);var reader=(IWordPagedReadBackend)backend;var session=backend.DiscoverWord().ActiveSessionId!;
            var first=reader.ReadWordRange(new(session,0,9000,1024));Check(first.PageLength<=1024&&!first.Complete&&first.NextCursor is not null,"Word range page bound failed.");
            var second=reader.ReadWordRange(new(session,0,9000,1024,false,first.NextCursor,first.ContentVersion));
            Check(second.PageStart==first.PageStart+first.PageLength&&second.ContentVersion==first.ContentVersion,"Range continuation skipped/repeated content.");
            Check(first.StructuredContentComplete,"Plain fixture range unexpectedly claimed unsupported structure.");
        });

        test("AR-023 E2 table read stays structured instead of flattening table text",()=>{
            var backend=new FixtureOfficeBackend();var reader=(IWordPagedReadBackend)backend;var session=backend.DiscoverWord().ActiveSessionId!;
            var page=reader.ReadWordTables(new(session,0,1));Check(page.Complete&&page.Tables.Count==1,"Fixture table page missing.");
            Check(page.Tables[0].Rows.Count==1&&page.Tables[0].Rows[0].SequenceEqual(["A","B"]),"Table cells were flattened or reordered.");
        });

        test("AR-023 E2 content-version patch keeps multiline mapping and rejects stale indexes",()=>{
            var backend=new FixtureOfficeBackend();var session=backend.DiscoverWord().ActiveSessionId!;var before=backend.SnapshotWord(session);
            var patches=new[]{new WordParagraphPatch(0,Text:"ONE\nTWO",Bold:false)};
            var applied=backend.PatchWord(new(session,before.StateToken,true,patches){ContentVersion=before.ContentVersion});
            Check(applied.After.Paragraphs[0].Text=="ONE"&&applied.After.Paragraphs[1].Text=="TWO"
                &&applied.After.Paragraphs[2].Text=="Preserve me","Multiline index mapping/preservation regressed.");
            Check(OfficeMutationReadback.VerifyWordPatch(applied.Before,applied.After,patches),"Existing Word readback verifier rejected valid multiline mapping.");
            var staleVersion=applied.After.ContentVersion!;backend.SetWordParagraphForFixture(2,"EXTERNAL-CHANGE");
            OfficeHostFaultException? stale=null;
            try{_=backend.PatchWord(new(session,applied.After.StateToken,true,[new(2,Text:"NO")]){ContentVersion=staleVersion});}
            catch(OfficeHostFaultException ex){stale=ex;}
            Check(stale?.Code=="stale_content"&&stale.NoEffect,"Stale paragraph indexes reached mutation.");
        });

        test("AR-023 E1 mixed formatting text replacement remains rejected before write",()=>{
            var mixed=new WordParagraphState(0,"AB","Normal",[
                new WordRunState(0,"A","Default",true,false,false),
                new WordRunState(1,"B","Default",false,false,false)]);
            var snapshot=new WordLiveSnapshot("s","d","d",false,0,0,"",[mixed],[],[],[],[],"state");
            Check(WordPatchRules.ValidationError(snapshot,[new(0,Text:"replacement")]) is not null,"Mixed formatting rewrite lost preflight guard.");
        });

        test("AR-023 E2 production runtime dispatches Word page without legacy full snapshot",()=>{
            var root=Path.Combine(Path.GetTempPath(),"h2-ar023-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try{
                var client=new PagedClient();var observed=client.Backend.SnapshotWord(client.SessionId);
                IReadOnlyList<H2AgentTargetPath> targets=[new(observed.FullName,false,"user-path")];
                var type=typeof(H2ProductionAgentAdapter).Assembly.GetType("H2AgentLab.Integration.H2OfficeRuntimeTools",true)!;
                using var office=(IDisposable)Activator.CreateInstance(type,[new Func<bool>(()=>true),root,null,targets])!;
                type.GetField("_client",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(office,client);
                var registry=new ToolRegistry();type.GetMethod("Register")!.Invoke(office,[registry]);
                Check(registry.TryGet("word.read_paragraphs",out var descriptor),"Production registry lost word.read_paragraphs.");
                var props=descriptor.CallableSchema.GetProperty("function").GetProperty("parameters").GetProperty("properties");
                Check(props.GetProperty("page_size").GetProperty("maximum").GetInt32()==WordPagedReadLimits.MaxParagraphs,"Word page schema drifted from protocol.");
                var call=new global::H2AgentLab.ToolCall("ar023-page","word.read_paragraphs",
                    JsonSerializer.SerializeToElement(new{session_id=client.SessionId,page_size=2}));
                var raw=descriptor.Executor.ExecuteAsync(call,CancellationToken.None).AsTask().GetAwaiter().GetResult();
                using var json=JsonDocument.Parse(raw);Check(json.RootElement.GetProperty("Paragraphs").GetArrayLength()==2,"Production Word page returned wrong size.");
                Check(client.PageReads==1&&client.SnapshotReads==0,"Production paged read fell back to full Word snapshot.");
                Check(client.Discoveries>=2,"Production Word target was not revalidated after page read.");
                Check(registry.TryGet("word.replace_range",out var mutate),"Word mutation tool missing.");
                var required=mutate.CallableSchema.GetProperty("function").GetProperty("parameters").GetProperty("required")
                    .EnumerateArray().Select(x=>x.GetString()).ToArray();
                Check(required.Contains("content_version"),"Word mutation schema did not bind indexes to content_version.");
            }finally{try{Directory.Delete(root,true);}catch{}}
        });
    }

    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static void Expect<T>(Action action)where T:Exception{try{action();}catch(T){return;}throw new InvalidOperationException("Expected "+typeof(T).Name);}

    private sealed class PagedClient:IOfficeSessionClient,IWordPagedReadClient
    {
        public FixtureOfficeBackend Backend{get;}=new(extraWordParagraphs:100);
        public string SessionId=>Backend.DiscoverWord().ActiveSessionId!;
        public string InstanceIdentity=>"ar023-paged-client";
        public int Discoveries;public int SnapshotReads;public int PageReads;
        public Task<WordDiscovery> DiscoverWordAsync(CancellationToken c=default){Discoveries++;return Task.FromResult(Backend.DiscoverWord());}
        public Task<WordLiveSnapshot> SnapshotWordAsync(string s,CancellationToken c=default){SnapshotReads++;return Task.FromResult(Backend.SnapshotWord(s));}
        public Task<WordParagraphReadPage> ReadWordParagraphsAsync(WordParagraphReadRequest r,CancellationToken c=default){PageReads++;return Task.FromResult(Backend.ReadWordParagraphs(r));}
        public Task<WordRangeReadPage> ReadWordRangeAsync(WordRangeReadRequest r,CancellationToken c=default){PageReads++;return Task.FromResult(Backend.ReadWordRange(r));}
        public Task<WordTableReadPage> ReadWordTablesAsync(WordTableReadRequest r,CancellationToken c=default){PageReads++;return Task.FromResult(Backend.ReadWordTables(r));}
        public Task<WordPatchResult> PatchWordAsync(WordPatchRequest r,CancellationToken c=default)=>Task.FromResult(Backend.PatchWord(r));
        public Task<WordLanguageEvidenceResult> InspectWordLanguageAsync(WordLanguageEvidenceRequest r,CancellationToken c=default)=>Task.FromResult(Backend.InspectWordLanguage(r));
        public Task<OfficeSaveCopyResult> SaveWordCopyAsync(OfficeSaveCopyRequest r,CancellationToken c=default)=>Task.FromResult(Backend.SaveWordCopy(r));
        public Task<ExcelDiscovery> DiscoverExcelAsync(CancellationToken c=default)=>throw new NotSupportedException();
        public Task<ExcelLiveSnapshot> SnapshotExcelAsync(string s,CancellationToken c=default)=>throw new NotSupportedException();
        public Task<ExcelPatchResult> PatchExcelAsync(ExcelPatchRequest r,CancellationToken c=default)=>throw new NotSupportedException();
        public Task<ExcelLiveSnapshot> RecalculateExcelAsync(ExcelRecalculateRequest r,CancellationToken c=default)=>throw new NotSupportedException();
        public Task<OfficeSaveCopyResult> SaveExcelCopyAsync(OfficeSaveCopyRequest r,CancellationToken c=default)=>throw new NotSupportedException();
        public void Dispose(){}
    }
}

using System.Text.Json;

namespace H2AgentLab;

public static class SkillTests
{
    public static async Task<int> ConcurrencyProbe(string root)
    {
        root=Path.GetFullPath(root); if(Directory.Exists(root)) throw new IOException("Use a new test directory.");
        Directory.CreateDirectory(root); var lines=new List<string>(); var failed=0;
        var runtime=new ScriptWorkspace(new(root),Path.Combine(root,"state"),(_,_)=>Task.FromResult(true));
        for(var n=0;n<8;n++)
        {
            try
            {
                var r=JsonSerializer.SerializeToElement(await runtime.Run("import time,openpyxl,docx,reportlab\ntime.sleep(0.25)\nprint('RUNTIME_READY')","","",default));
                if(r.GetProperty("exitCode").GetInt32()!=0 || !r.GetProperty("stdout").GetString()!.Contains("RUNTIME_READY")) throw new IOException(r.ToString());
                lines.Add("PASS concurrent run "+n);
            }
            catch(Exception ex){failed++;lines.Add("FAIL "+ex);}
            File.WriteAllLines(Path.Combine(root,"probe.txt"),lines);
        }
        lines.Add($"RESULT: {8-failed} passed, {failed} failed.");File.WriteAllLines(Path.Combine(root,"probe.txt"),lines);return failed==0 ? 0 : 1;
    }
    public static async Task<int> Run(string root)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root)) throw new IOException("Use a new test directory.");
        Directory.CreateDirectory(root); var lines = new List<string>(); var failed = 0;
        async Task Test(string name, Func<Task> work) { try { await work(); lines.Add("PASS " + name); } catch (Exception e) { failed++; lines.Add("FAIL " + name + " " + e); } File.WriteAllLines(Path.Combine(root, "tests.txt"), lines); }
        void Check(bool ok, string message) { if (!ok) throw new IOException(message); }
        var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "outside.txt"), "private-sentinel-not-staged");
        var runtime = new ScriptWorkspace(new(workspace), Path.Combine(root, "state"), (_, _) => Task.FromResult(true));
        string RunId(object result) => JsonSerializer.SerializeToElement(result).GetProperty("runId").GetString()!;
        int Exit(object result) => JsonSerializer.SerializeToElement(result).GetProperty("exitCode").GetInt32();
        await Test("Skill discovery is metadata-only and path escape is denied", () =>
        {
            var catalog = new SkillCatalog(); Check(catalog.Skills.Count == 5, "Expected 5 skills");
            Check(catalog.Discovery.Contains("spreadsheets") && !catalog.Discovery.Contains("copy.copy"), "Discovery leaked full instructions");
            Check(catalog.Read("spreadsheets", "SKILL.md").Contains("copy"), "Missing body");
            Check(catalog.Read("documents", "references/runtime.md").Contains("run_python"), "Missing runtime reference");
            try { catalog.Read("spreadsheets", "../../AgentTools.cs"); throw new Exception("Traversal accepted"); } catch (IOException) { }
            return Task.CompletedTask;
        });
        await Test("AppContainer loads Office libraries; original files, network and child processes are denied", async () =>
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start(); var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            using (var hostProbe = new System.Net.Sockets.TcpClient())
            { await hostProbe.ConnectAsync(System.Net.IPAddress.Loopback, port); using var accepted = await listener.AcceptTcpClientAsync(); }
            Environment.SetEnvironmentVariable("H2_TEST_SECRET", "synthetic-secret-must-not-inherit");
            var code = """
                import os, json, socket, subprocess, sys
                import openpyxl, docx, pypdf, PIL, pypdfium2
                blocked=[]
                try:
                    open(OUTSIDE, encoding='utf8').read()
                except PermissionError:
                    blocked.append('private-file')
                try:
                    open(OUTSIDE,'w').write('MUTATED')
                except PermissionError:
                    blocked.append('original-write')
                try:
                    with socket.create_connection(('127.0.0.1',PROBE_PORT), timeout=2): pass
                except (PermissionError, TimeoutError):
                    blocked.append('network')
                try:
                    subprocess.run([sys.executable,'-c','print(1)'],check=True)
                except (PermissionError, OSError):
                    blocked.append('child-process')
                assert len(blocked)==4, blocked
                assert 'H2_TEST_SECRET' not in os.environ
                print('ISOLATION_PASS', blocked)
                wb=openpyxl.Workbook(); wb.active['A1']='Hello'; wb.save('output/sample.xlsx')
                d=docx.Document(); d.add_paragraph('Document created in sandbox'); d.save('output/sample.docx')
                print('LIBRARIES_PASS')
                """.Replace("OUTSIDE", JsonSerializer.Serialize(Path.Combine(workspace, "outside.txt"))).Replace("PROBE_PORT", port.ToString());
            var result = JsonSerializer.SerializeToElement(await runtime.Run(code, "", "", default));
            Check(result.GetProperty("exitCode").GetInt32() == 0, result.ToString());
            Check(result.GetProperty("stdout").GetString()!.Contains("ISOLATION_PASS"), result.ToString());
            Check(!listener.Pending(), "Sandbox unexpectedly reached the host listener.");
            Check(File.ReadAllText(Path.Combine(workspace, "outside.txt")) == "private-sentinel-not-staged", "Original changed");
        });
        string? bookRun = null;
        await Test("General Python composes range fonts, conditional fills and formulas; originals stay unchanged", async () =>
        {
            var seed = await runtime.Run("""
                from openpyxl import Workbook
                from openpyxl.styles import Font
                w=Workbook(); s=w.active; s.title='Tasks'
                s.append(['Task','State','Amount','Double','Keep'])
                for row in [('A','done',10),('B','todo',20),('C','done',30)]: s.append(row)
                for r in range(2,5):
                    s.cell(r,4,f'=C{r}*2'); s.cell(r,5,'untouched')
                    s.cell(r,1).font=Font(name='Calibri',size=11,italic=True)
                w.create_sheet('Archive')['A1']='preserve'
                w.save('output/input.xlsx')
                """, "", "", default);
            Check(Exit(seed) == 0, JsonSerializer.Serialize(seed));
            var bytes = runtime.Read(RunId(seed), "input.xlsx"); File.WriteAllBytes(Path.Combine(workspace, "input.xlsx"), bytes);
            var edit = await runtime.Run("""
                from openpyxl import load_workbook
                from openpyxl.styles import PatternFill
                from copy import copy
                w=load_workbook('input/input.xlsx'); s=w['Tasks']
                for row in s['A2:B4']:
                    for c in row:
                        f=copy(c.font); f.name='Arial'; f.sz=14; c.font=f
                    if row[1].value=='done':
                        for c in row: c.fill=PatternFill('solid',fgColor='C6EFD5')
                w.save('output/result.xlsx')
                v=load_workbook('output/result.xlsx'); assert v['Tasks']['D3'].value=='=C3*2'
                assert v['Tasks']['A2'].font.italic and v['Archive']['A1'].value=='preserve'
                assert v['Tasks']['A2'].fill.fgColor.rgb.endswith('C6EFD5')
                assert v['Tasks']['A3'].fill.patternType is None
                print('RANGE_VERIFIED')
                """, "input.xlsx", "", default);
            Check(Exit(edit) == 0, JsonSerializer.Serialize(edit)); bookRun = RunId(edit);
            Check(File.ReadAllBytes(Path.Combine(workspace, "input.xlsx")).SequenceEqual(bytes), "Original spreadsheet modified");
        });
        await Test("A different request on the same workbook needs only new Python, not new host tools", async () =>
        {
            var result = await runtime.Run("""
                from openpyxl import load_workbook
                w=load_workbook('input/previous/result.xlsx'); s=w['Tasks']; t=w.create_sheet('Summary')
                t.append(['State','Count'])
                for state in ['done','todo']: t.append([state,sum(s.cell(r,2).value==state for r in range(2,5))])
                s['E3']='Reviewed'; w.save('output/second.xlsx')
                v=load_workbook('output/second.xlsx'); assert v['Summary']['B2'].value==2
                assert v['Tasks']['A2'].font.name=='Arial'; assert v['Tasks']['D4'].value=='=C4*2'
                print('SECOND_REQUEST_VERIFIED')
                """, "", bookRun!, default);
            Check(Exit(result) == 0, JsonSerializer.Serialize(result));
        });
        await Test("Arbitrary Word tables and mixed run formatting survive readback", async () =>
        {
            var result = await runtime.Run("""
                from docx import Document
                from docx.shared import Pt,RGBColor
                d=Document(); d.add_heading('Work plan',0); p=d.add_paragraph()
                p.add_run('Owner: '); r=p.add_run('Minh'); r.bold=True; r.font.color.rgb=RGBColor.from_string('147D64')
                r.font.size=Pt(13); t=d.add_table(rows=1, cols=2); t.rows[0].cells[0].text='Task'; t.rows[0].cells[1].text='State'
                row=t.add_row().cells; row[0].text='Review'; row[1].text='Pending'; d.save('output/plan.docx')
                v=Document('output/plan.docx'); assert len(v.tables)==1 and len(v.tables[0].rows)==2
                assert v.paragraphs[1].runs[1].bold; assert str(v.paragraphs[1].runs[1].font.color.rgb)=='147D64'
                print('WORD_VERIFIED')
                """, "", "", default);
            Check(Exit(result) == 0, JsonSerializer.Serialize(result));
        });
        await Test("PDF generation and real page rendering work in the sandbox", async () =>
        {
            var result = await runtime.Run("""
                from reportlab.pdfgen import canvas
                from pypdf import PdfReader
                import pypdfium2 as pdfium
                c=canvas.Canvas('output/sample.pdf'); c.drawString(50,750,'H2 Agent Lab PDF sample'); c.save()
                assert 'H2 Agent Lab' in PdfReader('output/sample.pdf').pages[0].extract_text()
                pdf=pdfium.PdfDocument('output/sample.pdf'); page=pdf[0]; bitmap=page.render(scale=1)
                bitmap.to_pil().save('output/page.png'); bitmap.close(); page.close(); pdf.close()
                print('PDF_RENDER_VERIFIED')
                """, "", "", default);
            Check(Exit(result) == 0, JsonSerializer.Serialize(result));
        });
        await Test("Failed code returns traceback, a following run repairs it, and previous output cannot escape", async () =>
        {
            var bad = await runtime.Run("raise ValueError('expected diagnostic')", "", "", default);
            Check(Exit(bad) != 0 && JsonSerializer.Serialize(bad).Contains("expected diagnostic"), "Error swallowed");
            var good = await runtime.Run("from pathlib import Path\nPath('output/fixed.txt').write_text('fixed',encoding='utf8')\nassert 2+2==4", "", "", default);
            Check(Exit(good) == 0, "Repair failed");
            try { runtime.Read(RunId(good), "../../manifest.json"); throw new Exception("Traversal accepted"); } catch (IOException) { }
        });
        await Test("Publication denial and stale destination hash prevent overwrites; allowed publication has backup", async () =>
        {
            var result = await runtime.Run("from pathlib import Path\nPath('output/change.txt').write_text('new',encoding='utf8')", "", "", default);
            var id = RunId(result); File.WriteAllText(Path.Combine(workspace, "change.txt"), "old");
            var denied = new ScriptWorkspace(new(workspace), Path.Combine(root, "state"), (_, _) => Task.FromResult(false));
            var oldHash = SafeWorkspace.Hash(File.ReadAllBytes(Path.Combine(workspace,"change.txt")));
            try { await denied.Publish(id,"change.txt","change.txt",oldHash,default); throw new Exception("Denied publish ran"); } catch (IOException) { }
            try { await runtime.Publish(id,"change.txt","change.txt","wrong",default); throw new Exception("Stale publish ran"); } catch (IOException) { }
            Check(File.ReadAllText(Path.Combine(workspace,"change.txt"))=="old","Original overwritten");
            await runtime.Publish(id,"change.txt","change.txt",oldHash,default);
            Check(File.ReadAllText(Path.Combine(workspace,"change.txt"))=="new","Publication failed");
            Check(Directory.GetFiles(Path.Combine(root,"state","backups")).Length==1,"No backup");
        });
        await Test("Read-only blocks publishing and removed narrow/unsandboxed tools are not callable", async () =>
        {
            var tools = new AgentTools(new(workspace), Path.Combine(root,"state"), (_,_)=>Task.FromResult(true), (_,_)=>{});
            foreach (var name in new[]{"create_word","word_replace","dotnet_check"})
                Check((await tools.Execute(new("test",name,JsonSerializer.SerializeToElement(new{})),default)).Contains("false"),name);
            var result=await tools.Execute(new("test","publish_artifact",JsonSerializer.SerializeToElement(new{run_id=bookRun,path="result.xlsx",destination="result.xlsx",expected_hash=""})),default);
            Check(result.Contains("Read-only") && !File.Exists(Path.Combine(workspace,"result.xlsx")),"Read-only bypass");
        });
        await Test("Cancelled Python cannot write a delayed output after returning", async () =>
        {
            using var cancel=new CancellationTokenSource(800);
            try { await runtime.Run("import time\ntime.sleep(10)\nopen('output/late.txt','w').write('bad')","","",cancel.Token); throw new Exception("Not cancelled"); } catch(OperationCanceledException) { }
            Check(!Directory.EnumerateFiles(Path.Combine(root,"state"),"late.txt",SearchOption.AllDirectories).Any(),"Late output exists");
        });
        await Test("Script refusal persists for the turn without repeatedly asking", async () =>
        {
            var asks=0; var denied=new ScriptWorkspace(new(workspace),Path.Combine(root,"denied"),(_,_)=>{asks++;return Task.FromResult(false);});
            for(var n=0;n<2;n++)
                try { await denied.Run("print('not allowed')","","",default); throw new Exception("Refusal bypassed"); } catch(IOException) { }
            Check(asks==1 && !Directory.Exists(Path.Combine(root,"denied","runs")),"Refused run created files or asked again");
        });
        await Test("Recorded runs survive restart; tampered artifacts cannot be published", async () =>
        {
            var result=await runtime.Run("open('output/proof.txt','w').write('verified')","","",default); var id=RunId(result);
            var resumed=new ScriptWorkspace(new(workspace),Path.Combine(root,"state"),(_,_)=>Task.FromResult(true));
            Check(JsonSerializer.Serialize(resumed.InspectRun(id)).Contains("verified"),"Lost code/log after restart");
            File.WriteAllText(Path.Combine(root,"state","runs",id,"work","output","proof.txt"),"tampered");
            try { await resumed.Publish(id,"proof.txt","proof.txt","",default); throw new Exception("Tampered output published"); } catch(IOException) { }
            Check(!File.Exists(Path.Combine(workspace,"proof.txt")),"Tampered output reached workspace");
        });
        foreach(var ollama in new[]{true,false})
        await Test((ollama ? "Ollama" : "Compatible API")+" native loop loads skill, executes, inspects, supplies real image bytes and publishes", async () =>
        {
            var folder=Path.Combine(root,ollama ? "loop-ollama" : "loop-api"); Directory.CreateDirectory(folder);
            var agentTools=new AgentTools(new(folder),Path.Combine(root,"loop-state"),(_,_)=>Task.FromResult(true),(_,_)=>{}){ReadOnly=false};
            var handler=new SkillLoopFixture(ollama); using var runner=new AgentRunner(handler);
            await runner.Run(new(){Protocol=ollama ? H2Notes.Core.AiProtocol.Ollama : H2Notes.Core.AiProtocol.OpenAiChat,BaseUrl=ollama ? "http://localhost:11434" : "https://example.invalid/v1",Model="fixture"},"",new(),agentTools,"Synthetic skill loop",(_,_)=>{},()=>{},default);
            Check(handler.Requests==6 && handler.SawImage,"Multimodal payload missing");
            Check(File.ReadAllText(Path.Combine(folder,"verified.txt"))=="verified","No checked publication");
        });
        lines.Add($"RESULT: {lines.Count-failed} passed, {failed} failed."); File.WriteAllLines(Path.Combine(root, "tests.txt"), lines); return failed == 0 ? 0 : 1;
    }

    private sealed class SkillLoopFixture(bool ollama) : HttpMessageHandler
    {
        public int Requests; public bool SawImage; private string _run="";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Requests++;
            using var payload=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var messages=payload.RootElement.GetProperty("messages");
            if(Requests==3)
            {
                using var result=JsonDocument.Parse(messages.EnumerateArray().Last(m=>m.GetProperty("role").GetString()=="tool").GetProperty("content").GetString()!);
                if(result.RootElement.GetProperty("exitCode").GetInt32()!=0) throw new IOException(result.RootElement.ToString());
                _run=result.RootElement.GetProperty("runId").GetString()!;
            }
            if(Requests==5)
            {
                var image=messages.EnumerateArray().Last();
                var bytes=ollama ? image.GetProperty("images")[0].GetString()! : image.GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString()!.Split(',')[1];
                SawImage=Convert.FromBase64String(bytes).AsSpan().StartsWith(new byte[]{137,80,78,71,13,10,26,10});
            }
            var name=Requests switch{1=>"read_skill",2=>"run_python",3=>"inspect_artifact",4=>"view_artifact",5=>"publish_artifact",_=>""};
            object args=Requests switch
            {
                1=>new{name="pdf",path="SKILL.md"},
                2=>new{code="from PIL import Image\nfrom pathlib import Path\nImage.new('RGB',(10,10),'white').save('output/page.png')\nPath('output/verified.txt').write_text('verified')\nassert Path('output/verified.txt').read_text()=='verified'",inputs="",previous_run=""},
                3=>new{run_id=_run,path="verified.txt"},
                4=>new{run_id=_run,path="page.png"},
                5=>new{run_id=_run,path="verified.txt",destination="verified.txt",expected_hash=""},
                _=>new{}
            };
            object delta=name.Length==0 ? new{content="Verified fixture complete"} :
                new{tool_calls=new[]{new{index=0,id="call_"+Requests,type="function",function=new{name,arguments=ollama ? args : (object)JsonSerializer.Serialize(args)}}}};
            var body=ollama ? JsonSerializer.Serialize(new{message=delta,done=true})+"\n" :
                "data: "+JsonSerializer.Serialize(new{choices=new[]{new{delta,finish_reason=name.Length==0 ? "stop" : "tool_calls"}}})+"\n\n";
            return new(System.Net.HttpStatusCode.OK){Content=new StringContent(body)};
        }
    }
}

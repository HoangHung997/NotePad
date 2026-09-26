using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using H2AgentLab;
using H2AgentLab.Runtime;
using H2AgentLab.Tasking;
using H2Notes.Core;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

internal static class H2ArtifactPublicationTests
{
    public static void Run(Action<string,Action> test)
    {
        test("AR-062 DOCX staging inspection records structure and create-only publish preserves exact bytes",()=>WithRoot(root=>{
            var workspace=Path.Combine(root,"workspace");var state=Path.Combine(root,"state");
            Directory.CreateDirectory(workspace);Directory.CreateDirectory(Path.Combine(workspace,"out"));
            var bytes=Docx("AR-062 WORD");
            var run=SeedRun(workspace,state,("report.docx",bytes));
            var scripts=Scripts(workspace,state);
            var inspected=scripts.VerifyArtifact(run,"report.docx");
            Check(inspected.Verification.Format=="docx"
                && inspected.Verification.SignatureVerified
                && inspected.Verification.StructureVerified
                && inspected.Verification.ContentVerified
                && !inspected.Verification.LayoutVerified
                && inspected.Content.Contains("AR-062 WORD",StringComparison.Ordinal),
                "DOCX receipt did not independently classify exact content/structure.");
            var published=scripts.Publish(run,"report.docx","out/report.docx","",CancellationToken.None).GetAwaiter().GetResult();
            Check(File.ReadAllBytes(Path.Combine(workspace,"out","report.docx")).SequenceEqual(bytes),
                "DOCX publish did not preserve exact staged bytes.");
            using var json=JsonDocument.Parse(JsonSerializer.Serialize(published));
            Check(!json.RootElement.GetProperty("requiresFurtherVerification").GetBoolean()
                && json.RootElement.GetProperty("verificationEvidence").GetString()==inspected.Verification.EvidenceId,
                "DOCX publish lost verification receipt.");
            Check(VerifyPublish(workspace,"out/report.docx","",json.RootElement.GetRawText()),
                "Runtime file verifier rejected independently inspected DOCX.");
        }));

        test("AR-062 XLSX inspection classifies formulas values and overwrite requires current destination hash",()=>WithRoot(root=>{
            var workspace=Path.Combine(root,"workspace");var state=Path.Combine(root,"state");
            Directory.CreateDirectory(workspace);Directory.CreateDirectory(Path.Combine(workspace,"out"));
            var bytes=Xlsx();
            var run=SeedRun(workspace,state,("book.xlsx",bytes));
            var scripts=Scripts(workspace,state);
            var inspected=scripts.VerifyArtifact(run,"book.xlsx");
            Check(inspected.Verification.Format=="xlsx"
                && inspected.Verification.StructureVerified
                && inspected.Verification.ContentVerified
                && inspected.Verification.FormulaValueClassified
                && inspected.Verification.PrimaryItemCount>=2
                && inspected.Verification.SecondaryItemCount==1,
                "XLSX receipt did not classify stored values/formulas.");

            var old=Xlsx("OLD");
            File.WriteAllBytes(Path.Combine(workspace,"out","book.xlsx"),old);
            var stale=false;
            try{_=scripts.Publish(run,"book.xlsx","out/book.xlsx",new string('0',64),CancellationToken.None).GetAwaiter().GetResult();}
            catch(AgentFaultException ex){stale=ex.Code=="stale_state";}
            Check(stale&&File.ReadAllBytes(Path.Combine(workspace,"out","book.xlsx")).SequenceEqual(old),
                "Stale overwrite was not rejected before replacing destination.");

            var expected=SafeWorkspace.Hash(old);
            var published=scripts.Publish(run,"book.xlsx","out/book.xlsx",expected,CancellationToken.None).GetAwaiter().GetResult();
            Check(File.ReadAllBytes(Path.Combine(workspace,"out","book.xlsx")).SequenceEqual(bytes),
                "Verified XLSX did not replace exact expected destination.");
            using var json=JsonDocument.Parse(JsonSerializer.Serialize(published));
            Check(VerifyPublish(workspace,"out/book.xlsx",expected,json.RootElement.GetRawText()),
                "Runtime file verifier rejected verified XLSX overwrite.");
            var backups=Directory.Exists(Path.Combine(state,"backups"))
                ? Directory.GetFiles(Path.Combine(state,"backups")):[];
            Check(backups.Length==1&&File.ReadAllBytes(backups[0]).SequenceEqual(old),
                "Overwrite backup did not preserve prior destination bytes.");
        }));

        test("AR-062 PDF receipt is signature-only and completion verifier refuses content/layout PASS",()=>WithRoot(root=>{
            var workspace=Path.Combine(root,"workspace");var state=Path.Combine(root,"state");
            Directory.CreateDirectory(workspace);Directory.CreateDirectory(Path.Combine(workspace,"out"));
            var bytes=Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj\n%%EOF");
            var run=SeedRun(workspace,state,("document.pdf",bytes));
            var scripts=Scripts(workspace,state);
            var inspected=scripts.VerifyArtifact(run,"document.pdf");
            Check(inspected.Verification.Format=="pdf"
                && inspected.Verification.SignatureVerified
                && !inspected.Verification.ContentVerified
                && !inspected.Verification.LayoutVerified
                && inspected.Verification.RequiresFurtherVerification,
                "PDF signature-only classification was overstated.");
            var published=scripts.Publish(run,"document.pdf","out/document.pdf","",CancellationToken.None).GetAwaiter().GetResult();
            using var json=JsonDocument.Parse(JsonSerializer.Serialize(published));
            Check(json.RootElement.GetProperty("requiresFurtherVerification").GetBoolean(),
                "PDF publish hid content/layout verification debt.");
            Check(!VerifyPublish(workspace,"out/document.pdf","",json.RootElement.GetRawText()),
                "Signature-only PDF was incorrectly accepted as content-verified completion.");
        }));

        test("AR-062 publish rejects an output that was never independently inspected",()=>WithRoot(root=>{
            var workspace=Path.Combine(root,"workspace");var state=Path.Combine(root,"state");
            Directory.CreateDirectory(workspace);Directory.CreateDirectory(Path.Combine(workspace,"out"));
            var run=SeedRun(workspace,state,("draft.docx",Docx("UNVERIFIED")));
            var scripts=Scripts(workspace,state);
            var rejected=false;
            try{_=scripts.Publish(run,"draft.docx","out/draft.docx","",CancellationToken.None).GetAwaiter().GetResult();}
            catch(AgentFaultException ex){rejected=ex.Code=="verification_required";}
            Check(rejected&&!File.Exists(Path.Combine(workspace,"out","draft.docx")),
                "Uninspected output was published.");
        }));

        test("AR-062 exact-byte receipt is invalid after staged artifact changes",()=>WithRoot(root=>{
            var workspace=Path.Combine(root,"workspace");var state=Path.Combine(root,"state");
            Directory.CreateDirectory(workspace);Directory.CreateDirectory(Path.Combine(workspace,"out"));
            var run=SeedRun(workspace,state,("report.docx",Docx("BEFORE")));
            var scripts=Scripts(workspace,state);
            _=scripts.VerifyArtifact(run,"report.docx");
            var staged=Path.Combine(state,"runs",run,"work","output","report.docx");
            File.WriteAllBytes(staged,Docx("AFTER"));
            var rejected=false;
            try{_=scripts.Publish(run,"report.docx","out/report.docx","",CancellationToken.None).GetAwaiter().GetResult();}
            catch(IOException){rejected=true;}
            Check(rejected&&!File.Exists(Path.Combine(workspace,"out","report.docx")),
                "Changed staged bytes reused an old verification receipt.");
        }));

        test("AR-062 multi-output run cannot publish the missing uninspected output",()=>WithRoot(root=>{
            var workspace=Path.Combine(root,"workspace");var state=Path.Combine(root,"state");
            Directory.CreateDirectory(workspace);Directory.CreateDirectory(Path.Combine(workspace,"out"));
            var run=SeedRun(workspace,state,("one.docx",Docx("ONE")),("two.xlsx",Xlsx()));
            var scripts=Scripts(workspace,state);
            _=scripts.VerifyArtifact(run,"one.docx");
            _=scripts.Publish(run,"one.docx","out/one.docx","",CancellationToken.None).GetAwaiter().GetResult();
            var rejected=false;
            try{_=scripts.Publish(run,"two.xlsx","out/two.xlsx","",CancellationToken.None).GetAwaiter().GetResult();}
            catch(AgentFaultException ex){rejected=ex.Code=="verification_required";}
            Check(rejected&&File.Exists(Path.Combine(workspace,"out","one.docx"))
                && !File.Exists(Path.Combine(workspace,"out","two.xlsx")),
                "Multi-output publication silently treated an unverified missing output as complete.");
        }));
    }

    private static ScriptWorkspace Scripts(string workspace,string state)
        => new(new SafeWorkspace(workspace),state,(_,_)=>Task.FromResult(true));

    private static bool VerifyPublish(string workspace,string destination,string expectedHash,string rawOutput)
    {
        var verifier=new FileRuntimeDomainVerifier(new SafeWorkspace(workspace));
        var args=JsonSerializer.SerializeToElement(new{run_id="fixture",path=Path.GetFileName(destination),
            destination,expected_hash=expectedHash});
        var call=new ToolCall("publish","publish_artifact",args);
        var contract=new AgentTaskContract(Guid.NewGuid(),"publish","workspace",[],[],[],[],[],
            AgentTaskRiskClass.Medium,new AgentVerificationPolicy(),mutationAllowed:true);
        var context=new AgentRuntimeVerificationContext(contract,1,[],[]);
        return verifier.VerifyAsync(context,call,rawOutput,CancellationToken.None).GetAwaiter().GetResult().Passed;
    }

    private static string SeedRun(string workspace,string state,params (string Path,byte[] Bytes)[] outputs)
    {
        var id=Guid.NewGuid().ToString("N");
        var outputRoot=Path.Combine(state,"runs",id,"work","output");
        Directory.CreateDirectory(outputRoot);
        var artifacts=new List<ScriptArtifact>();
        foreach(var item in outputs)
        {
            var path=Path.Combine(outputRoot,item.Path.Replace('/',Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path,item.Bytes);
            artifacts.Add(new(item.Path,item.Bytes.Length,SafeWorkspace.Hash(item.Bytes)));
        }
        var run=new ScriptRun(id,new SafeWorkspace(workspace).Root,0,new Dictionary<string,string>(),artifacts)
        {CompletedUtc=DateTime.UtcNow};
        Directory.CreateDirectory(Path.Combine(state,"runs",id));
        File.WriteAllText(Path.Combine(state,"runs",id,"manifest.json"),JsonSerializer.Serialize(run));
        return id;
    }

    private static byte[] Docx(string text)
    {
        using var stream=new MemoryStream();
        using(var doc=WordprocessingDocument.Create(stream,WordprocessingDocumentType.Document,true))
        {
            var main=doc.AddMainDocumentPart();
            main.Document=new W.Document(new W.Body(
                new W.Paragraph(new W.Run(new W.Text(text))),
                new W.Table(new W.TableRow(new W.TableCell(new W.Paragraph(new W.Run(new W.Text("TABLE")))))),
                new W.SectionProperties()));
            main.Document.Save();
        }
        return stream.ToArray();
    }

    private static byte[] Xlsx(string? first="42")
    {
        using var stream=new MemoryStream();
        using(var doc=SpreadsheetDocument.Create(stream,SpreadsheetDocumentType.Workbook,true))
        {
            var workbook=doc.AddWorkbookPart();
            workbook.Workbook=new S.Workbook();
            var sheetPart=workbook.AddNewPart<WorksheetPart>();
            var data=new S.SheetData(
                new S.Row(
                    new S.Cell{CellReference="A1",CellValue=new S.CellValue(first!)},
                    new S.Cell{CellReference="A2",CellFormula=new S.CellFormula("A1*2"),CellValue=new S.CellValue("84")}));
            sheetPart.Worksheet=new S.Worksheet(data);
            workbook.Workbook.Append(new S.Sheets(new S.Sheet{
                Id=workbook.GetIdOfPart(sheetPart),SheetId=1,Name="Data"}));
            workbook.Workbook.Save();
        }
        return stream.ToArray();
    }

    private static void WithRoot(Action<string> action)
    {
        var root=Path.Combine(Path.GetTempPath(),"h2-ar062-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try{action(root);}finally{try{Directory.Delete(root,true);}catch{}}
    }

    private static void Check(bool value,string message)
    {
        if(!value)throw new InvalidOperationException(message);
    }
}

using System.Text.Json;
using H2AgentLab.Cad;
using H2AgentLab.Tools;

internal static class H2AutoCadLiveBridgeTests
{
    public static void Run(Action<string,Action> test)
    {
        test("AR-063 operation matrix keeps closed-file and live AutoCAD claims separate", () =>
        {
            var closed=AutoCadOperationMatrix.Entries.Where(x=>x.Mode==AutoCadOperationMode.ClosedFileCoreConsole).ToArray();
            var live=AutoCadOperationMatrix.Entries.Where(x=>x.Mode==AutoCadOperationMode.LiveExternalCom).ToArray();
            Check(closed.Select(x=>x.ToolName).Order().SequenceEqual(new[]{
                "autocad.create_file","autocad.export_dxf","autocad.inspect_file","autocad.update_entities"
            }.Order()),"Closed-file CAD matrix drifted.");
            Check(live.Single(x=>x.ToolName=="autocad.update_attribute").Implemented
                && live.Single(x=>x.ToolName=="autocad.update_attribute").Mutating,
                "Selected attribute edit is not declared as implemented live mutation.");
            foreach(var unsupported in new[]{"autocad.update_entity","autocad.plot","autocad.verify_plot"})
                Check(live.Single(x=>x.ToolName==unsupported).Implemented==false,
                    unsupported+" was over-claimed by the live provider.");
        });

        test("AR-063 live descriptor subset advertises external COM provenance and no general entity/plot mutation", () =>
        {
            var bridge=new Ar063LiveCadFixtureBridge();
            var executor=new AutoCadNativeToolExecutor(bridge,(_,_)=>ValueTask.FromResult(true));
            var descriptors=AutoCadProviderPolicy.BuildDescriptors(
                executor,"com-v1",AutoCadOperationMatrix.LiveComImplementedTools,
                "autocad-com-live","external-com-rot","Structured live AutoCAD external COM capability.");
            var names=descriptors.Select(x=>x.Name).Order(StringComparer.Ordinal).ToArray();
            Check(names.SequenceEqual(AutoCadOperationMatrix.LiveComImplementedTools.Order(StringComparer.Ordinal)),
                "Live AutoCAD descriptor surface does not match the implementation matrix.");
            Check(descriptors.All(x=>x.Provenance?.ProviderId=="autocad-com-live"
                && x.Provenance?.ServerId=="external-com-rot"
                && x.Preference?.InteractionFidelity==ToolInteractionFidelity.Structured),
                "Live AutoCAD descriptors lost provider/fidelity metadata.");
            Check(!names.Contains("autocad.update_entity")&&!names.Contains("autocad.plot"),
                "Unsupported live entity/plot mutation leaked into ToolRegistry.");
        });

        test("AR-063 external COM bridge reads current selection edits one block attribute and verifies readback", () =>
        {
            var fixture=FakeComApplication.Create();
            var bridge=new AutoCadComLiveBridge(()=>fixture);
            var document=bridge.GetActiveDocumentAsync(default).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("Fake AutoCAD active document missing.");
            var entities=bridge.QueryEntitiesAsync(document.SessionId,document.StateToken,
                JsonSerializer.SerializeToElement(new{selection="current",object_type="BlockReference",layer="OTC",max_results=10}),default)
                .GetAwaiter().GetResult();
            var entity=entities.Single();
            var beforeAttributes=bridge.ReadAttributesAsync(entity,default).GetAwaiter().GetResult();
            Check(beforeAttributes.GetProperty("attributes").GetProperty("KM").GetString()=="12+34",
                "Live bridge failed initial selected-block attribute read.");

            var mutation=bridge.MutateAsync(new(
                AutoCadOperationKind.UpdateAttribute,
                document.SessionId,document.StateToken,entity.Handle,entity.StateToken,
                JsonSerializer.SerializeToElement(new{attributeTag="KM",value="12+345"}),
                PermissionGranted:true),default).GetAwaiter().GetResult();

            Check(mutation.Before.StateToken==entity.StateToken
                && mutation.After.StateToken!=entity.StateToken
                && mutation.After.DocumentStateToken!=document.StateToken,
                "Live COM mutation did not advance document/entity state.");
            Check(fixture.ActiveDocument.Entity.Attributes.Single(x=>x.TagString=="KM").TextString=="12+345",
                "Live COM bridge edited the wrong attribute or failed to write.");

            var verification=JsonSerializer.SerializeToElement(new{
                kind="entity",
                entityHandle=mutation.After.Handle,
                entityStateToken=mutation.After.StateToken,
                expectation=new{attributes=new Dictionary<string,string>{{"KM","12+345"}}}
            });
            Check(bridge.VerifyAsync(mutation.After.DocumentSessionId,mutation.After.DocumentStateToken,verification,default)
                    .GetAwaiter().GetResult(),
                "Independent live COM attribute readback did not verify exact postcondition.");
        });

        test("AR-063 live COM bridge rejects stale token unsupported query and entity no longer selected before effect", () =>
        {
            var fixture=FakeComApplication.Create();
            var bridge=new AutoCadComLiveBridge(()=>fixture);
            var document=bridge.GetActiveDocumentAsync(default).GetAwaiter().GetResult()!;
            var selected=bridge.QueryEntitiesAsync(document.SessionId,document.StateToken,
                JsonSerializer.SerializeToElement(new{selection="current",max_results=1}),default)
                .GetAwaiter().GetResult().Single();

            var mutation=bridge.MutateAsync(new(
                AutoCadOperationKind.UpdateAttribute,
                document.SessionId,document.StateToken,selected.Handle,selected.StateToken,
                JsonSerializer.SerializeToElement(new{attributeTag="KM",value="FIRST"}),
                true),default).GetAwaiter().GetResult();

            Expect<InvalidOperationException>(()=>bridge.ReadAttributesAsync(selected,default).GetAwaiter().GetResult(),
                "Stale selected entity token remained readable after mutation.");
            Expect<NotSupportedException>(()=>bridge.QueryEntitiesAsync(
                mutation.After.DocumentSessionId,mutation.After.DocumentStateToken,
                JsonSerializer.SerializeToElement(new{handles=new[]{"ABCD"}}),default).GetAwaiter().GetResult(),
                "Live provider accepted non-selection query.");

            fixture.ActiveDocument.PickfirstSelectionSet=new FakeCollection<FakeEntity>([]);
            var before=fixture.ActiveDocument.Entity.Attributes.Single(x=>x.TagString=="KM").TextString;
            Expect<InvalidOperationException>(()=>bridge.MutateAsync(new(
                AutoCadOperationKind.UpdateAttribute,
                mutation.After.DocumentSessionId,mutation.After.DocumentStateToken,
                mutation.After.Handle,mutation.After.StateToken,
                JsonSerializer.SerializeToElement(new{attributeTag="KM",value="SHOULD-NOT-WRITE"}),true),default)
                .GetAwaiter().GetResult(),
                "Entity removed from PickFirst selection was still mutable.");
            Check(fixture.ActiveDocument.Entity.Attributes.Single(x=>x.TagString=="KM").TextString==before,
                "Selection rejection happened after an effect.");
        });
    }

    internal sealed class Ar063LiveCadFixtureBridge : IAutoCadNativeBridge
    {
        public const string Session="dwg-live-01";
        public const string Handle="ABCD";
        public string DocumentToken{get;private set;}="doc-v1";
        public string EntityToken{get;private set;}="ent-v1";
        public string AttributeValue{get;private set;}="12+34";
        public int MutationCount{get;private set;}

        public Task<IReadOnlyList<AutoCadDocumentRef>> ListDocumentsAsync(CancellationToken cancellationToken)
            =>Task.FromResult<IReadOnlyList<AutoCadDocumentRef>>([Document()]);
        public Task<AutoCadDocumentRef?> GetActiveDocumentAsync(CancellationToken cancellationToken)
            =>Task.FromResult<AutoCadDocumentRef?>(Document());
        public Task<IReadOnlyList<AutoCadEntityRef>> QueryEntitiesAsync(string documentSessionId,string documentStateToken,
            JsonElement query,CancellationToken cancellationToken)
        {
            RequireDocument(documentSessionId,documentStateToken);
            if(query.ValueKind!=JsonValueKind.Object||!query.TryGetProperty("selection",out var s)||s.GetString()!="current")
                throw new NotSupportedException("fixture live CAD requires current selection");
            return Task.FromResult<IReadOnlyList<AutoCadEntityRef>>([Entity()]);
        }
        public Task<JsonElement> ReadAttributesAsync(AutoCadEntityRef entity,CancellationToken cancellationToken)
        {
            RequireEntity(entity);
            return Task.FromResult(JsonSerializer.SerializeToElement(new{
                documentSessionId=Session,documentStateToken=DocumentToken,entityHandle=Handle,entityStateToken=EntityToken,
                attributes=new Dictionary<string,string>{{"KM",AttributeValue}}
            }));
        }
        public Task<JsonElement> ReadLayersAsync(string documentSessionId,string documentStateToken,CancellationToken cancellationToken)
        {
            RequireDocument(documentSessionId,documentStateToken);
            return Task.FromResult(JsonSerializer.SerializeToElement(new{layers=new[]{new{name="OTC",frozen=false,locked=false}}}));
        }
        public Task<AutoCadMutationResult> MutateAsync(AutoCadMutationRequest request,CancellationToken cancellationToken)
        {
            AutoCadProviderPolicy.ValidateMutation(request);
            if(request.Operation!=AutoCadOperationKind.UpdateAttribute)throw new NotSupportedException();
            RequireDocument(request.DocumentSessionId,request.DocumentStateToken);
            if(request.EntityHandle!=Handle||request.EntityStateToken!=EntityToken)throw new InvalidOperationException("stale_entity_state");
            var tag=request.Parameters.GetProperty("attributeTag").GetString();
            if(tag!="KM")throw new InvalidOperationException("attribute_missing");
            var before=Entity();
            AttributeValue=request.Parameters.GetProperty("value").GetString()!;
            MutationCount++;DocumentToken="doc-v"+(MutationCount+1);EntityToken="ent-v"+(MutationCount+1);
            var after=Entity();
            return Task.FromResult(new AutoCadMutationResult("fixture-live-"+MutationCount,before,after,
                "evidence:fixture-live:"+MutationCount));
        }
        public Task<JsonElement> PlotAsync(string documentSessionId,string documentStateToken,JsonElement request,CancellationToken cancellationToken)
            =>throw new NotSupportedException();
        public Task<bool> VerifyAsync(string documentSessionId,string currentDocumentStateToken,JsonElement verification,CancellationToken cancellationToken)
        {
            try
            {
                RequireDocument(documentSessionId,currentDocumentStateToken);
                if(verification.GetProperty("entityHandle").GetString()!=Handle
                    ||verification.GetProperty("entityStateToken").GetString()!=EntityToken)return Task.FromResult(false);
                var attrs=verification.GetProperty("expectation").GetProperty("attributes");
                foreach(var item in attrs.EnumerateObject())
                    if(item.Name!="KM"||item.Value.GetString()!=AttributeValue)return Task.FromResult(false);
                return Task.FromResult(true);
            }catch{return Task.FromResult(false);}
        }
        private AutoCadDocumentRef Document()=>new(Session,"Drawing1.dwg",@"C:\fixture\Drawing1.dwg",DocumentToken);
        private AutoCadEntityRef Entity()=>new(Session,DocumentToken,Handle,"AcDbBlockReference","OTC",EntityToken);
        private void RequireDocument(string session,string token)
        {if(session!=Session||token!=DocumentToken)throw new InvalidOperationException("stale_document_state");}
        private void RequireEntity(AutoCadEntityRef entity)
        {RequireDocument(entity.DocumentSessionId,entity.DocumentStateToken);if(entity.Handle!=Handle||entity.StateToken!=EntityToken)throw new InvalidOperationException("stale_entity_state");}
    }

    private static void Expect<T>(Action action,string message)where T:Exception
    {try{action();}catch(T){return;}throw new InvalidOperationException(message);}

    private static void Check(bool value,string message)
    {if(!value)throw new InvalidOperationException(message);}

    private sealed class FakeComApplication
    {
        public int HWND{get;}=777;
        public FakeDocument ActiveDocument{get;}
        public FakeCollection<FakeDocument> Documents{get;}
        private FakeComApplication(FakeDocument document){ActiveDocument=document;Documents=new([document]);}
        public static FakeComApplication Create()=>new(new FakeDocument());
    }
    private sealed class FakeDocument
    {
        public string Name{get;}="Drawing1.dwg";
        public string FullName{get;}=@"C:\fixture\Drawing1.dwg";
        public FakeEntity Entity{get;}=new();
        public FakeCollection<FakeEntity> ModelSpace{get;}
        public FakeCollection<FakeLayer> Layers{get;}=new([new("0"),new("OTC")]);
        public FakeCollection<FakeEntity> PickfirstSelectionSet{get;set;}
        public FakeDocument(){ModelSpace=new([Entity]);PickfirstSelectionSet=new([Entity]);}
        public FakeEntity HandleToObject(string handle)=>Entity.Handle.Equals(handle,StringComparison.OrdinalIgnoreCase)
            ?Entity:throw new KeyNotFoundException();
        public void Regen(int mode){}
    }
    private sealed class FakeEntity
    {
        public string Handle{get;}="ABCD";
        public string ObjectName{get;}="AcDbBlockReference";
        public string Layer{get;}="OTC";
        public bool HasAttributes=>true;
        public List<FakeAttribute> Attributes{get;}=[new("KM","12+34"),new("NAME","OTC")];
        public object[] GetAttributes()=>Attributes.Cast<object>().ToArray();
        public void Update(){}
    }
    private sealed class FakeAttribute(string tag,string value)
    {
        public string TagString{get;}=tag;
        public string TextString{get;set;}=value;
        public void Update(){}
    }
    private sealed class FakeLayer(string name)
    {
        public string Name{get;}=name;
        public bool Freeze{get;}=false;
        public bool Lock{get;}=false;
    }
    private sealed class FakeCollection<T>(IReadOnlyList<T> items)
    {
        public int Count=>items.Count;
        public T Item(int index)=>items[index];
    }
}

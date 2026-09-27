using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace H2AgentLab.Cad;

/// <summary>
/// External live-AutoCAD bridge over the version-independent COM automation object.
/// It never injects code into AutoCAD and never sends arbitrary command/LISP strings.
/// Every call reacquires the live application/document and validates document/entity state.
/// The intentionally narrow live mutation surface is selected block-attribute update + readback.
/// </summary>
public sealed class AutoCadComLiveBridge : IAutoCadNativeBridge
{
    internal const int MaxLiveDocumentEntities = 20_000;
    internal const int MaxSelectionEntities = 100;
    internal const int MaxLayers = 2_048;
    private readonly Func<object?> _applicationFactory;

    public AutoCadComLiveBridge(Func<object?> applicationFactory)
        => _applicationFactory = applicationFactory ?? throw new ArgumentNullException(nameof(applicationFactory));

    public static bool TryCreate(out IAutoCadNativeBridge? bridge, out string reason)
    {
        bridge = null;
        if (!OperatingSystem.IsWindows())
        {
            reason = "live_autocad_requires_windows";
            return false;
        }
        try
        {
            var candidate = new AutoCadComLiveBridge(GetActiveApplication);
            var active = candidate.GetActiveDocumentAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (active is null)
            {
                reason = "live_autocad_not_running";
                return false;
            }
            bridge = candidate;
            reason = "ready";
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            reason = "live_autocad_unavailable";
            return false;
        }
    }

    public Task<IReadOnlyList<AutoCadDocumentRef>> ListDocumentsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        dynamic app = RequireApplication();
        object? documents = null;
        try
        {
            documents = app.Documents;
            var result = new List<AutoCadDocumentRef>();
            foreach (var raw in Items(documents, 64))
            {
                dynamic document = raw;
                result.Add(DocumentRef(app, document));
                Release(raw);
            }
            return Task.FromResult<IReadOnlyList<AutoCadDocumentRef>>(result);
        }
        finally { Release(documents); Release(app); }
    }

    public Task<AutoCadDocumentRef?> GetActiveDocumentAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        dynamic app = RequireApplication();
        object? raw = null;
        try
        {
            try { raw = app.ActiveDocument; }
            catch (COMException) { return Task.FromResult<AutoCadDocumentRef?>(null); }
            if (raw is null) return Task.FromResult<AutoCadDocumentRef?>(null);
            dynamic document = raw;
            return Task.FromResult<AutoCadDocumentRef?>(DocumentRef(app, document));
        }
        finally { Release(raw); Release(app); }
    }

    public Task<IReadOnlyList<AutoCadEntityRef>> QueryEntitiesAsync(
        string documentSessionId,
        string documentStateToken,
        JsonElement query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateQuery(query);
        dynamic app = RequireApplication();
        object? rawDocument = null;
        object? selection = null;
        try
        {
            rawDocument = RequireDocument(app, documentSessionId);
            dynamic document = rawDocument;
            var snapshot = CaptureDocument(app, document);
            RequireToken(documentStateToken, snapshot.Reference.StateToken, "document");
            try { selection = document.PickfirstSelectionSet; }
            catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            { throw new InvalidOperationException("AutoCAD current selection is unavailable.", ex); }

            var max = query.TryGetProperty("max_results", out var maxNode) && maxNode.TryGetInt32(out var parsed)
                ? Math.Clamp(parsed, 1, MaxSelectionEntities) : MaxSelectionEntities;
            var objectType = query.TryGetProperty("object_type", out var typeNode) ? typeNode.GetString() : null;
            var layer = query.TryGetProperty("layer", out var layerNode) ? layerNode.GetString() : null;
            var result = new List<AutoCadEntityRef>();
            foreach (var raw in Items(selection, MaxSelectionEntities))
            {
                try
                {
                    var entity = EntityRef(documentSessionId, snapshot.Reference.StateToken, raw);
                    if (!string.IsNullOrWhiteSpace(objectType) && !ObjectTypeMatches(entity.ObjectType, objectType!)) continue;
                    if (!string.IsNullOrWhiteSpace(layer) && !string.Equals(entity.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(entity);
                    if (result.Count >= max) break;
                }
                finally { Release(raw); }
            }
            return Task.FromResult<IReadOnlyList<AutoCadEntityRef>>(result);
        }
        finally { Release(selection); Release(rawDocument); Release(app); }
    }

    public Task<JsonElement> ReadAttributesAsync(AutoCadEntityRef entity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entity);
        dynamic app = RequireApplication();
        object? rawDocument = null;
        object? rawEntity = null;
        try
        {
            rawDocument = RequireDocument(app, entity.DocumentSessionId);
            dynamic document = rawDocument;
            var documentSnapshot = CaptureDocument(app, document);
            RequireToken(entity.DocumentStateToken, documentSnapshot.Reference.StateToken, "document");
            rawEntity = RequireSelectedEntity(document, entity.Handle);
            var current = EntityRef(entity.DocumentSessionId, documentSnapshot.Reference.StateToken, rawEntity);
            RequireEntityIdentity(entity, current);
            var attributes = Attributes(rawEntity);
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                documentSessionId = current.DocumentSessionId,
                documentStateToken = current.DocumentStateToken,
                entityHandle = current.Handle,
                entityStateToken = current.StateToken,
                attributes
            }));
        }
        finally { Release(rawEntity); Release(rawDocument); Release(app); }
    }

    public Task<JsonElement> ReadLayersAsync(
        string documentSessionId,
        string documentStateToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        dynamic app = RequireApplication();
        object? rawDocument = null;
        object? layers = null;
        try
        {
            rawDocument = RequireDocument(app, documentSessionId);
            dynamic document = rawDocument;
            var snapshot = CaptureDocument(app, document);
            RequireToken(documentStateToken, snapshot.Reference.StateToken, "document");
            layers = document.Layers;
            var result = Items(layers, MaxLayers).Select(raw =>
            {
                try
                {
                    dynamic layer = raw;
                    return new { name = SafeString(() => layer.Name), frozen = SafeBool(() => layer.Freeze), locked = SafeBool(() => layer.Lock) };
                }
                finally { Release(raw); }
            }).ToArray();
            return Task.FromResult(JsonSerializer.SerializeToElement(new { layers = result }));
        }
        finally { Release(layers); Release(rawDocument); Release(app); }
    }

    public Task<AutoCadMutationResult> MutateAsync(
        AutoCadMutationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AutoCadProviderPolicy.ValidateMutation(request);
        if (request.Operation != AutoCadOperationKind.UpdateAttribute)
            throw new NotSupportedException("Live COM provider supports only bounded selected-block attribute update.");

        dynamic app = RequireApplication();
        object? rawDocument = null;
        object? rawEntity = null;
        try
        {
            rawDocument = RequireDocument(app, request.DocumentSessionId);
            dynamic document = rawDocument;
            var beforeDocument = CaptureDocument(app, document);
            RequireToken(request.DocumentStateToken, beforeDocument.Reference.StateToken, "document");
            rawEntity = RequireSelectedEntity(document, request.EntityHandle);
            var before = EntityRef(request.DocumentSessionId, beforeDocument.Reference.StateToken, rawEntity);
            RequireToken(request.EntityStateToken, before.StateToken, "entity");

            var tag = RequiredString(request.Parameters, "attributeTag", 256);
            var value = RequiredString(request.Parameters, "value", 2_000);
            var attributes = AttributeObjects(rawEntity);
            var matches = attributes.Where(item =>
            {
                dynamic attr = item;
                return string.Equals(SafeString(() => attr.TagString), tag, StringComparison.OrdinalIgnoreCase);
            }).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(matches.Length == 0
                    ? "Selected block does not contain the requested attribute tag."
                    : "Selected block contains duplicate attribute tags; mutation refused.");

            dynamic target = matches[0];
            target.TextString = value;
            try { target.Update(); } catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
            try { rawEntity.GetType().InvokeMember("Update", System.Reflection.BindingFlags.InvokeMethod, null, rawEntity, null); } catch { }
            try { document.Regen(1); } catch { }

            foreach (var item in attributes) Release(item);
            attributes.Clear();

            var afterDocument = CaptureDocument(app, document);
            object? reread = null;
            try
            {
                reread = HandleObject(document, request.EntityHandle);
                var after = EntityRef(request.DocumentSessionId, afterDocument.Reference.StateToken, reread);
                var readback = Attributes(reread);
                if (!readback.TryGetValue(tag, out var actual) || actual != value)
                    throw new IOException("AutoCAD live attribute readback did not match the requested value.");
                if (after.StateToken == before.StateToken || after.DocumentStateToken == before.DocumentStateToken)
                    throw new IOException("AutoCAD live mutation did not advance entity/document state tokens.");
                var mutationId = "cad-live-" + Guid.NewGuid().ToString("N");
                return Task.FromResult(new AutoCadMutationResult(
                    mutationId, before, after,
                    "evidence:autocad-live:" + mutationId));
            }
            finally { Release(reread); }
        }
        finally { Release(rawEntity); Release(rawDocument); Release(app); }
    }

    public Task<JsonElement> PlotAsync(
        string documentSessionId,
        string documentStateToken,
        JsonElement request,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("Live COM plot is intentionally not advertised by AR-063.");

    public Task<bool> VerifyAsync(
        string documentSessionId,
        string currentDocumentStateToken,
        JsonElement verification,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (verification.ValueKind != JsonValueKind.Object
            || !verification.TryGetProperty("kind", out var kind)
            || kind.GetString() != "entity")
            return Task.FromResult(false);

        dynamic app = RequireApplication();
        object? rawDocument = null;
        object? rawEntity = null;
        try
        {
            rawDocument = RequireDocument(app, documentSessionId);
            dynamic document = rawDocument;
            var documentSnapshot = CaptureDocument(app, document);
            if (documentSnapshot.Reference.StateToken != currentDocumentStateToken) return Task.FromResult(false);
            var handle = verification.GetProperty("entityHandle").GetString() ?? "";
            rawEntity = HandleObject(document, handle);
            var current = EntityRef(documentSessionId, currentDocumentStateToken, rawEntity);
            if (verification.TryGetProperty("entityStateToken", out var token)
                && token.ValueKind == JsonValueKind.String
                && current.StateToken != token.GetString()) return Task.FromResult(false);
            if (!verification.TryGetProperty("expectation", out var expectation)
                || expectation.ValueKind != JsonValueKind.Object) return Task.FromResult(false);
            if (expectation.TryGetProperty("attributes", out var expectedAttributes)
                && expectedAttributes.ValueKind == JsonValueKind.Object)
            {
                var actual = Attributes(rawEntity);
                foreach (var item in expectedAttributes.EnumerateObject())
                    if (!actual.TryGetValue(item.Name, out var value) || value != item.Value.GetString())
                        return Task.FromResult(false);
            }
            return Task.FromResult(true);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or KeyNotFoundException)
        { return Task.FromResult(false); }
        finally { Release(rawEntity); Release(rawDocument); Release(app); }
    }

    private object RequireApplication()
        => _applicationFactory() ?? throw new InvalidOperationException("No running AutoCAD COM application is available.");

    private static object RequireDocument(dynamic app, string sessionId)
    {
        object? documents = null;
        try
        {
            documents = app.Documents;
            foreach (var raw in Items(documents, 64))
            {
                dynamic document = raw;
                if (SessionId(app, document) == sessionId) return raw;
                Release(raw);
            }
            throw new KeyNotFoundException("AutoCAD document session is no longer open.");
        }
        finally { Release(documents); }
    }

    private static object RequireSelectedEntity(dynamic document, string handle)
    {
        object? selection = null;
        try
        {
            selection = document.PickfirstSelectionSet;
            foreach (var raw in Items(selection, MaxSelectionEntities))
            {
                if (string.Equals(SafeString(() => ((dynamic)raw).Handle), handle, StringComparison.OrdinalIgnoreCase))
                    return raw;
                Release(raw);
            }
            throw new InvalidOperationException("AutoCAD live mutation/read requires the entity to remain in the current PickFirst selection.");
        }
        finally { Release(selection); }
    }

    private static object HandleObject(dynamic document, string handle)
    {
        if (string.IsNullOrWhiteSpace(handle) || handle.Length > 32 || handle.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("AutoCAD entity handle is invalid.");
        try { return document.HandleToObject(handle); }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        { throw new KeyNotFoundException("AutoCAD entity handle is not present in the current document.", ex); }
    }

    private static DocumentSnapshot CaptureDocument(dynamic app, dynamic document)
    {
        var reference = new AutoCadDocumentRef(
            SessionId(app, document),
            SafeString(() => document.Name),
            SafeString(() => document.FullName),
            "");
        object? modelSpace = null;
        try
        {
            modelSpace = document.ModelSpace;
            var items = Items(modelSpace, MaxLiveDocumentEntities + 1);
            if (items.Count > MaxLiveDocumentEntities)
            {
                foreach (var raw in items) Release(raw);
                throw new InvalidOperationException("AutoCAD live document exceeds the bounded state-token entity limit.");
            }
            var entityTokens = new List<string>(items.Count);
            foreach (var raw in items)
            {
                try { entityTokens.Add(EntityToken(raw)); }
                finally { Release(raw); }
            }
            entityTokens.Sort(StringComparer.Ordinal);
            var state = Hash(reference.SessionId + "\n" + string.Join("\n", entityTokens));
            return new(reference with { StateToken = state });
        }
        finally { Release(modelSpace); }
    }

    private static AutoCadDocumentRef DocumentRef(dynamic app, dynamic document)
        => CaptureDocument(app, document).Reference;

    private static AutoCadEntityRef EntityRef(string sessionId, string documentStateToken, object raw)
    {
        dynamic entity = raw;
        return new(
            sessionId,
            documentStateToken,
            SafeString(() => entity.Handle),
            SafeString(() => entity.ObjectName),
            SafeString(() => entity.Layer),
            EntityToken(raw));
    }

    private static string EntityToken(object raw)
    {
        dynamic entity = raw;
        var attributes = Attributes(raw);
        var attr = string.Join("\n", attributes.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Key + "=" + x.Value));
        return Hash(SafeString(() => entity.Handle) + "\n"
            + SafeString(() => entity.ObjectName) + "\n"
            + SafeString(() => entity.Layer) + "\n" + attr);
    }

    private static Dictionary<string,string> Attributes(object raw)
    {
        var result = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in AttributeObjects(raw))
        {
            try
            {
                dynamic attribute = item;
                var tag = SafeString(() => attribute.TagString);
                if (tag.Length == 0 || result.ContainsKey(tag)) continue;
                result[tag] = SafeString(() => attribute.TextString);
            }
            finally { Release(item); }
        }
        return result;
    }

    private static List<object> AttributeObjects(object raw)
    {
        dynamic entity = raw;
        try
        {
            if (!SafeBool(() => entity.HasAttributes)) return [];
            var value = entity.GetAttributes();
            if (value is Array array) return array.Cast<object>().ToList();
            return value is null ? [] : [value];
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        { return []; }
    }

    private static List<object> Items(object collection, int max)
    {
        dynamic source = collection;
        var count = Convert.ToInt32(source.Count, CultureInfo.InvariantCulture);
        if (count < 0 || count > max) count = Math.Min(Math.Max(count, 0), max);
        var oneBased = false;
        if (count > 0)
        {
            try { _ = source.Item(0); }
            catch { oneBased = true; }
        }
        var result = new List<object>(count);
        for (var i = 0; i < count; i++)
            result.Add(source.Item(i + (oneBased ? 1 : 0)));
        return result;
    }

    private static void ValidateQuery(JsonElement query)
    {
        if (query.ValueKind != JsonValueKind.Object) throw new ArgumentException("AutoCAD live query must be an object.");
        var allowed = new HashSet<string>(["selection","object_type","layer","max_results"], StringComparer.Ordinal);
        foreach (var property in query.EnumerateObject())
            if (!allowed.Contains(property.Name))
                throw new NotSupportedException("AutoCAD live provider supports only current-selection query fields.");
        if (!query.TryGetProperty("selection", out var selection)
            || selection.ValueKind != JsonValueKind.String
            || !string.Equals(selection.GetString(), "current", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("AutoCAD live provider requires selection='current'.");
        if (query.TryGetProperty("max_results", out var max)
            && (!max.TryGetInt32(out var value) || value is < 1 or > MaxSelectionEntities))
            throw new ArgumentOutOfRangeException(nameof(query), "AutoCAD live max_results must be 1..100.");
    }

    private static bool ObjectTypeMatches(string actual, string requested)
    {
        if (string.Equals(actual, requested, StringComparison.OrdinalIgnoreCase)) return true;
        return requested.Equals("BlockReference", StringComparison.OrdinalIgnoreCase)
            && (actual.Contains("BlockReference", StringComparison.OrdinalIgnoreCase)
                || actual.Equals("INSERT", StringComparison.OrdinalIgnoreCase));
    }

    private static void RequireEntityIdentity(AutoCadEntityRef expected, AutoCadEntityRef actual)
    {
        if (!string.Equals(expected.Handle, actual.Handle, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.ObjectType, actual.ObjectType, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.Layer, actual.Layer, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("AutoCAD selected entity identity changed.");
        RequireToken(expected.StateToken, actual.StateToken, "entity");
    }

    private static void RequireToken(string expected, string actual, string label)
    {
        if (string.IsNullOrWhiteSpace(expected) || !string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException("stale_" + label + "_state");
    }

    private static string RequiredString(JsonElement value, string name, int max)
    {
        if (!value.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.String)
            throw new ArgumentException("Missing AutoCAD " + name + ".");
        var text = node.GetString() ?? "";
        if (text.Length is < 1 || text.Length > max || text.Any(char.IsControl))
            throw new ArgumentException("AutoCAD " + name + " is outside safe bounds.");
        return text;
    }

    private static string SessionId(dynamic app, dynamic document)
    {
        var hwnd = SafeString(() => Convert.ToString(app.HWND, CultureInfo.InvariantCulture));
        var full = SafeString(() => document.FullName);
        var name = SafeString(() => document.Name);
        return "acad-live-" + Hash(hwnd + "\n" + full + "\n" + name)[..32];
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string SafeString(Func<object?> value)
    {
        try { return Convert.ToString(value(), CultureInfo.InvariantCulture)?.Trim() ?? ""; }
        catch { return ""; }
    }

    private static bool SafeBool(Func<object?> value)
    {
        try { return Convert.ToBoolean(value(), CultureInfo.InvariantCulture); }
        catch { return false; }
    }

    private static void Release(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.FinalReleaseComObject(value); } catch { }
    }

    private static object? GetActiveApplication()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var progId in ProgIds())
        {
            if (CLSIDFromProgID(progId, out var clsid) < 0) continue;
            if (GetActiveObject(ref clsid, IntPtr.Zero, out var value) >= 0 && value is not null)
                return value;
        }
        return null;
    }

    private static IEnumerable<string> ProgIds()
    {
        yield return "AutoCAD.Application";
        using var classes = Registry.ClassesRoot;
        foreach (var name in classes.GetSubKeyNames()
            .Where(x => x.StartsWith("AutoCAD.Application.", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
            .Take(16))
            yield return name;
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string lpszProgID, out Guid lpclsid);

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(
        ref Guid rclsid,
        IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    private sealed record DocumentSnapshot(AutoCadDocumentRef Reference);
}

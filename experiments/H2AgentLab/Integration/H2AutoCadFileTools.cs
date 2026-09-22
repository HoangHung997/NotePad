using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using H2AgentLab.Runtime;
using H2AgentLab.Tools;

namespace H2AgentLab.Integration;

/// <summary>Typed, closed-file AutoCAD operations. Never attaches to the user's live drawing.
/// Available only with full access and an installed Autodesk Core Console. The CLI receives
/// host-generated AutoLISP, never arbitrary model-supplied scripts.</summary>
internal sealed class H2AutoCadFileTools(string executable, SafeWorkspace workspace, string stateRoot) : IAgentRuntimeDomainVerifier
{
    public string DomainId => "autocad-file-readback";
    private readonly Dictionary<string, AgentRuntimeDomainVerification> _verified = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    internal static string? FindExecutable() => Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk"))
        ? Directory.EnumerateDirectories(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk"), "AutoCAD 20*")
            .OrderDescending().Select(p => Path.Combine(p, "accoreconsole.exe")).FirstOrDefault(File.Exists) : null;

    public void Register(ToolRegistry registry)
    {
        foreach (var action in new[] { "create_file", "inspect_file", "update_entities", "export_dxf" })
        {
            var name = "autocad." + action;
            var description = action switch
            {
                "create_file" => "Create a new real DWG using installed AutoCAD Core Console. Supports LINE(start,end), CIRCLE(center,radius), TEXT(position,text,height). Readback returns entity handles and a file state token. Does not touch any open AutoCAD session.",
                "inspect_file" => "Open a DWG COPY in AutoCAD Core Console and read model-space entity handles, type, layer, geometry and text. Returns state_token for guarded edits. Does not modify the source drawing.",
                "update_entities" => "Edit a closed DWG using fresh state_token and exact entity handles. Each change may set end for LINE, text for TEXT, radius for CIRCLE or delete=true. Saves only after AutoCAD readback confirms target changes and preservation of other entities.",
                _ => "Export a closed DWG to a new ASCII DXF using AutoCAD Core Console. Source is unchanged. Destination must end in .dxf."
            };
            var point = new { type = "array", items = new { type = "number" }, minItems = 3, maxItems = 3 };
            var properties = new Dictionary<string, object> { ["path"] = new { type = "string", description = "DWG path in the selected workspace." } };
            var required = new List<string> { "path" };
            if (action == "create_file")
            {
                properties["entities"] = new { type = "array", minItems = 1, maxItems = 200, items = new { type = "object", properties = new {
                    kind = new { type = "string", @enum = new[] { "LINE", "CIRCLE", "TEXT" } }, start = point, end = point, center = point, position = point,
                    radius = new { type = "number" }, height = new { type = "number" }, text = new { type = "string" } }, required = new[] { "kind" }, additionalProperties = false } };
                required.Add("entities");
            }
            if (action == "update_entities")
            {
                properties["state_token"] = new { type = "string" }; required.Add("state_token");
                properties["changes"] = new { type = "array", minItems = 1, maxItems = 200, items = new { type = "object", properties = new {
                    handle = new { type = "string" }, end = point, text = new { type = "string" }, radius = new { type = "number" }, delete = new { type = "boolean" } },
                    required = new[] { "handle" }, additionalProperties = false } }; required.Add("changes");
            }
            if (action == "export_dxf") { properties["destination"] = new { type = "string" }; required.Add("destination"); }
            registry.Register(new ToolDescriptor(name, new("autocad", "Typed closed-file CAD operations using the installed Autodesk engine."), description,
                action == "inspect_file" ? AgentToolRisk.Low : AgentToolRisk.High,
                action == "inspect_file" ? AgentToolAccess.ReadOnly : AgentToolAccess.Mutating, false, "v1",
                JsonSerializer.SerializeToElement(new { type = "function", function = new { name, description,
                    parameters = new { type = "object", properties, required, additionalProperties = false } } }),
                new DelegatingToolExecutor("autocad-core-file", Execute), resourceScope: new("cad-file", workspace.Root),
                serializationKey: "autocad-core-file", canProvideVerificationEvidence: true));
        }
    }

    private async ValueTask<string> Execute(ToolCall call, CancellationToken ct)
    {
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        var scratch = Path.Combine(Path.GetTempPath(), "h2-cad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var path = workspace.Resolve(Arg(call.Arguments, "path"));
            if (!Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Path must end in .dwg.");
            var creating = call.Name.EndsWith("create_file", StringComparison.Ordinal);
            var beforeBytes = creating ? null : workspace.Read(path);
            var beforeHash = beforeBytes is null ? "" : SafeWorkspace.Hash(beforeBytes);
            if (creating && File.Exists(path)) throw new IOException("Destination exists; choose a new DWG file.");
            if (call.Name.EndsWith("update_entities") && Arg(call.Arguments, "state_token") != beforeHash)
                throw new IOException("stale_state: DWG changed; inspect_file again before editing.");
            var input = Path.Combine(scratch, "input.dwg");
            if (beforeBytes is not null) await File.WriteAllBytesAsync(input, beforeBytes, ct);
            var resultPath = Path.Combine(scratch, "result.dwg");
            var dxfPath = Path.Combine(scratch, "result.dxf");
            var beforeReport = Path.Combine(scratch, "before.txt"); var afterReport = Path.Combine(scratch, "after.txt");
            var script = new StringBuilder(DumpFunctions);
            if (!creating) script.AppendLine("(h2dump " + Quote(beforeReport) + ")");
            if (creating)
            {
                var entities = call.Arguments.GetProperty("entities").EnumerateArray().ToArray();
                if (entities.Length is < 1 or > 200) throw new ArgumentException("Create 1..200 entities.");
                foreach (var entity in entities) script.AppendLine(Create(entity));
            }
            if (call.Name.EndsWith("update_entities"))
            {
                var changes = call.Arguments.GetProperty("changes").EnumerateArray().ToArray();
                if (changes.Length is < 1 or > 200 || changes.Select(c => Arg(c, "handle")).Distinct().Count() != changes.Length)
                    throw new ArgumentException("Supply 1..200 unique handles.");
                foreach (var change in changes) script.AppendLine(Change(change));
            }
            if (creating || call.Name.EndsWith("update_entities")) script.AppendLine("(command \"_.SAVEAS\" \"2018\" " + Quote(resultPath) + ")");
            if (call.Name.EndsWith("export_dxf")) script.AppendLine("(command \"_.DXFOUT\" " + Quote(dxfPath) + " \"16\")");
            script.AppendLine("(h2dump " + Quote(afterReport) + ")");
            script.AppendLine("(command \"_.QUIT\" \"_Y\")");
            await Run(scratch, creating ? null : input, script.ToString(), ct);
            if (creating || call.Name.EndsWith("update_entities"))
                await Run(scratch, resultPath, DumpFunctions + "\n(h2dump " + Quote(afterReport) + ")\n(command \"_.QUIT\" \"_Y\")\n", ct);
            var after = ReadEntities(afterReport);
            var before = creating ? [] : ReadEntities(beforeReport);
            if (creating) VerifyCreated(after, call.Arguments.GetProperty("entities"));
            if (call.Name.EndsWith("update_entities")) VerifyChanges(before, after, call.Arguments.GetProperty("changes"));
            var output = path;
            if (creating || call.Name.EndsWith("update_entities"))
            {
                var bytes = await File.ReadAllBytesAsync(resultPath, ct);
                if (!Encoding.ASCII.GetString(bytes.AsSpan(0, Math.Min(6, bytes.Length))).StartsWith("AC10")) throw new IOException("AutoCAD returned an invalid DWG.");
                workspace.Write(path, bytes, beforeHash, stateRoot);
            }
            else if (call.Name.EndsWith("export_dxf"))
            {
                output = workspace.Resolve(Arg(call.Arguments, "destination"));
                if (!Path.GetExtension(output).Equals(".dxf", StringComparison.OrdinalIgnoreCase) || File.Exists(output)) throw new IOException("DXF must be a new .dxf file.");
                var bytes = await File.ReadAllBytesAsync(dxfPath, ct);
                if (!Encoding.ASCII.GetString(bytes).Contains("ENTITIES")) throw new IOException("AutoCAD returned an invalid DXF.");
                workspace.Write(output, bytes, "", stateRoot);
            }
            var hash = SafeWorkspace.Hash(workspace.Read(output));
            _verified[call.Id] = new(DomainId, true, ["autocad-file-sha256:" + hash]);
            return JsonSerializer.Serialize(new { path = output, state_token = hash, entities = after, verified_by = "Autodesk AutoCAD Core Console native readback", source_unchanged = call.Name is "autocad.inspect_file" or "autocad.export_dxf" });
        }
        finally
        {
            // Only this invocation's fresh temporary directory, never a model-provided path.
            try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _serial.Release();
        }
    }

    private async Task Run(string directory, string? input, string script, CancellationToken ct)
    {
        var file = Path.Combine(directory, "operation.scr"); await File.WriteAllTextAsync(file, script + "\n", new UTF8Encoding(false), ct);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, StandardOutputEncoding = Encoding.Unicode };
        if (input is not null) { start.ArgumentList.Add("/i"); start.ArgumentList.Add(input); }
        foreach (var argument in new[] { "/s", file, "/l", "en-US" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start AutoCAD Core Console.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); ct.ThrowIfCancellationRequested(); throw new TimeoutException("AutoCAD did not finish within 90 seconds; owned process stopped."); }
        var log = await stdout; var error = await stderr;
        if (process.ExitCode != 0) throw new IOException("AutoCAD exit " + process.ExitCode + ": " + (error + log)[..Math.Min(1600, error.Length + log.Length)]);
    }

    private sealed record Entity(string Handle, string Kind, string Layer, string Position, string End, string Radius, string Text);
    private static List<Entity> ReadEntities(string path) => File.ReadAllLines(path).Where(l => l.Length > 0).Take(2001).Select(l =>
    { var p = l.Split('\t'); if (p.Length != 7) throw new IOException("Malformed or over-limit AutoCAD entity evidence."); return new Entity(p[0], p[1], p[2], NormalizeNumbers(p[3]), NormalizeNumbers(p[4]), NormalizeNumbers(p[5]), p[6]); }).ToList();
    private static string NormalizeNumbers(string text) => text.Length == 0 ? "" : string.Join(",", text.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture).ToString("F8", CultureInfo.InvariantCulture)));
    private static void VerifyCreated(List<Entity> actual, JsonElement requested)
    {
        var remaining = actual.ToList();
        foreach (var item in requested.EnumerateArray())
        {
            var kind = Arg(item, "kind");
            var position = PointEvidence(item.GetProperty(kind == "LINE" ? "start" : kind == "CIRCLE" ? "center" : "position"));
            var found = remaining.FirstOrDefault(e => e.Kind == kind && e.Layer == "0" && e.Position == position
                && (kind != "LINE" || e.End == PointEvidence(item.GetProperty("end")))
                && (kind != "CIRCLE" || e.Radius == item.GetProperty("radius").GetDouble().ToString("F8", CultureInfo.InvariantCulture))
                && (kind != "TEXT" || e.Text == Arg(item, "text") && e.Radius == (item.TryGetProperty("height", out var h) ? h.GetDouble() : 2.5).ToString("F8", CultureInfo.InvariantCulture)));
            if (found is null) throw new IOException("Native CAD readback did not match requested geometry/text.");
            remaining.Remove(found);
        }
        if (remaining.Count != 0) throw new IOException("AutoCAD returned unexpected extra entities.");
    }
    private static void VerifyChanges(List<Entity> before, List<Entity> after, JsonElement changes)
    {
        var map = changes.EnumerateArray().ToDictionary(c => Arg(c, "handle"));
        foreach (var old in before)
        {
            var current = after.SingleOrDefault(e => e.Handle == old.Handle);
            if (!map.TryGetValue(old.Handle, out var patch)) { if (current != old) throw new IOException("Untargeted CAD entity changed."); continue; }
            if (patch.TryGetProperty("delete", out var deleted) && deleted.GetBoolean()) { if (current is not null) throw new IOException("CAD deletion was not verified."); continue; }
            if (current is null) throw new IOException("Edited CAD entity disappeared.");
            var expected = old;
            if (patch.TryGetProperty("end", out _) && old.Kind != "LINE" || patch.TryGetProperty("text", out _) && old.Kind != "TEXT"
                || patch.TryGetProperty("radius", out _) && old.Kind != "CIRCLE") throw new IOException("CAD property does not match entity type.");
            if (patch.TryGetProperty("end", out var end)) expected = expected with { End = PointEvidence(end) };
            if (patch.TryGetProperty("text", out var text)) expected = expected with { Text = Clean(text.GetString()!) };
            if (patch.TryGetProperty("radius", out var radius)) expected = expected with { Radius = Number(radius.GetDouble()).ToString("F8", CultureInfo.InvariantCulture) };
            if (expected != current) throw new IOException("CAD target readback did not match the requested patch.");
        }
        if (map.Keys.Any(h => before.All(e => e.Handle != h)) || after.Any(e => before.All(b => b.Handle != e.Handle))) throw new IOException("Unknown handle or unexpected added entity.");
    }
    private static string Create(JsonElement e) => Arg(e, "kind") switch
    {
        "LINE" => "(entmakex (list '(0 . \"LINE\") '(8 . \"0\") (cons 10 " + Point(e.GetProperty("start")) + ") (cons 11 " + Point(e.GetProperty("end")) + ")))",
        "CIRCLE" => "(entmakex (list '(0 . \"CIRCLE\") '(8 . \"0\") (cons 10 " + Point(e.GetProperty("center")) + ") (cons 40 " + Positive(e.GetProperty("radius").GetDouble()) + ")))",
        "TEXT" => "(entmakex (list '(0 . \"TEXT\") '(8 . \"0\") (cons 10 " + Point(e.GetProperty("position")) + ") (cons 40 " + Positive(e.TryGetProperty("height", out var h) ? h.GetDouble() : 2.5) + ") (cons 1 " + Quote(Clean(Arg(e, "text"))) + ")))",
        _ => throw new ArgumentException("Supported kinds: LINE, CIRCLE, TEXT.")
    };
    private static string Change(JsonElement e)
    {
        var handle = Arg(e, "handle"); if (!Regex.IsMatch(handle, "\\A[0-9A-Fa-f]{1,16}\\z")) throw new ArgumentException("Invalid CAD handle.");
        var code = "(setq h2e (handent " + Quote(handle) + "))\n";
        if (e.TryGetProperty("delete", out var d) && d.GetBoolean()) return code + "(if h2e (entdel h2e))";
        code += "(if h2e (progn (setq h2d (entget h2e)) ";
        if (e.TryGetProperty("end", out var end)) code += "(setq h2d (subst (cons 11 " + Point(end) + ") (assoc 11 h2d) h2d)) ";
        if (e.TryGetProperty("text", out var text)) code += "(setq h2d (subst (cons 1 " + Quote(Clean(text.GetString()!)) + ") (assoc 1 h2d) h2d)) ";
        if (e.TryGetProperty("radius", out var radius)) code += "(setq h2d (subst (cons 40 " + Positive(radius.GetDouble()) + ") (assoc 40 h2d) h2d)) ";
        return code + "(entmod h2d) (entupd h2e)))";
    }
    private static string Arg(JsonElement e, string name) => e.GetProperty(name).GetString() ?? throw new ArgumentException(name);
    private static double Number(double n) => double.IsFinite(n) && Math.Abs(n) <= 1e9 ? n : throw new ArgumentException("CAD number outside safe bounds.");
    private static string Positive(double n) => n > 0 ? Number(n).ToString("R", CultureInfo.InvariantCulture) : throw new ArgumentException("Radius/height must be positive.");
    private static string Point(JsonElement e) { var a = e.EnumerateArray().Select(v => Number(v.GetDouble())).ToArray(); if (a.Length != 3) throw new ArgumentException("A point needs three coordinates."); return "(list " + string.Join(" ", a.Select(n => n.ToString("R", CultureInfo.InvariantCulture))) + ")"; }
    private static string PointEvidence(JsonElement e) => string.Join(",", e.EnumerateArray().Select(v => Number(v.GetDouble()).ToString("F8", CultureInfo.InvariantCulture)));
    private static string Clean(string value) => value.Length <= 2000 && !value.Any(char.IsControl) ? value : throw new ArgumentException("CAD text is too long or has control characters.");
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    public bool CanVerify(ToolCall call, string output) => call.Name.StartsWith("autocad.") && call.Name != "autocad.inspect_file";
    public Task<AgentRuntimeDomainVerification> VerifyAsync(AgentRuntimeVerificationContext context, ToolCall call, string output, CancellationToken ct)
        => Task.FromResult(_verified.TryGetValue(call.Id, out var report) ? report : new(DomainId, false, [], "No successful native CAD readback."));
    private const string DumpFunctions = """
        (defun h2s (s) (if s (vl-string-translate (strcat (chr 9) (chr 10) (chr 13)) "   " s) ""))
        (defun h2n (n) (if n (rtos n 2 8) ""))
        (defun h2p (p) (if p (strcat (h2n (car p)) "," (h2n (cadr p)) "," (h2n (caddr p))) ""))
        (defun h2dump (path / f ss i d sep)
          (setq f (open path "w") ss (ssget "_X" '((410 . "Model"))) i 0 sep (chr 9))
          (if (and ss (> (sslength ss) 2000)) (write-line "OVER_LIMIT" f))
          (if ss (repeat (min 2000 (sslength ss))
            (setq d (entget (ssname ss i)) i (1+ i))
            (write-line (strcat (h2s (cdr (assoc 5 d))) sep (h2s (cdr (assoc 0 d))) sep (h2s (cdr (assoc 8 d))) sep (h2p (cdr (assoc 10 d))) sep (h2p (cdr (assoc 11 d))) sep (h2n (cdr (assoc 40 d))) sep (h2s (cdr (assoc 1 d)))) f)))
          (close f))

        """;
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace H2AgentLab;

public sealed class AgentFaultException(string code, string message, bool recoverable = true) : IOException(message)
{
    public string Code { get; } = code;
    public bool Recoverable { get; } = recoverable;
}

public sealed record RecoveryFault(string Code, string Message, bool Recoverable, string Next);

public static class RecoveryPolicy
{
    public const string Guidance = """
        Recovery protocol: a failed tool is evidence to investigate, not proof the user's task is impossible.
        Compare requested intent with actual arguments, exact names, paths, scope, runtime and returned evidence.
        Inspect real files/resources/environment before guessing a replacement. Choose the next diagnostic with
        the highest information value, revise the approach, then verify the result against the original request.
        If file lookup fails, list/search names INSIDE the approved workspace; use observed exact names. Similar
        names are candidates, not automatic permission to edit a different file; ask if identity is ambiguous.
        A limited/inaccessible scan cannot prove a file does not exist everywhere. Report the scope searched.
        For scripts inspect traceback, input/ versus output/ paths, available APIs, schema and saved run; revise
        code instead of repeating it unchanged. For wrong output use assertions/readback, not exit 0 as proof.
        For a wrong skill inspect actual catalog/resources and choose a better one. Missing capabilities are not
        granted by a skill. No package installation/network/permission bypass. Never retry a denied action.
        After an uncertain write/click, inspect actual state before any repeat; do not duplicate side effects.
        Keep brief factual progress (what failed, checked evidence, next check), not private reasoning transcripts.
        Persist until verified, a real permission/capability/ambiguity blocker, or the bounded work budget ends.
        Then report what was tried, what remains unknown, and the smallest input needed; never invent success.
        """;

    public static RecoveryFault Classify(Exception ex, string tool)
    {
        var code = ex switch
        {
            AgentFaultException fault => fault.Code,
            FileNotFoundException or DirectoryNotFoundException => "not_found",
            UnauthorizedAccessException => "access_denied",
            System.ComponentModel.Win32Exception win when win.NativeErrorCode == 5 => "access_denied",
            JsonException or ArgumentException => "invalid_arguments",
            IOException io when (io.HResult & 0xffff) is 32 or 33 => "file_busy",
            _ => "tool_error"
        };
        var canRepair = ex is AgentFaultException custom ? custom.Recoverable : code != "access_denied";
        var next = code switch
        {
            "not_found" => "Observe exact names with list_files/find_files inside the selected workspace; inspect scope/parent. Do not guess another spelling or create a replacement.",
            "skill_not_found" => "Read list_skills, then read_skill with the exact name and path SKILL.md. Load only relevant references.",
            "resource_not_found" => "Use the observed resource list and read the actual SKILL.md; do not invent a resource path.",
            "denied" or "boundary" or "access_denied" or "permission_required" => "Stop this scope. Do not retry or route around this restriction. Explain what needs user action without changing permissions.",
            "unknown_tool" => "Choose from the actual advertised tools or use list_skills. Do not invent a tool name; use a supported approach.",
            "unavailable" => "This capability is not installed/available. Consider another supported approach within the request, otherwise report the missing prerequisite. Do not claim it ran.",
            "stale_state" => "Read the current file/window again, compare what changed and obtain a fresh hash/token. Do not blindly overwrite or repeat an external action.",
            "file_busy" => "Do a read-only check of the current file state. Retry only after new evidence; do not kill apps or overwrite locks.",
            "invalid_arguments" => "Check the advertised tool schema and observed exact values, correct the arguments and retry.",
            _ when tool is "click_control" or "type_control" or "open_file" or "write_text" or "publish_artifact" => "Outcome may be partial/unknown. Inspect actual state first. Do not repeat side effects blindly; retain approval rules.",
            _ => "Inspect the error and actual inputs/resources using a different diagnostic. Revise the approach, verify by readback and report remaining uncertainty. Do not repeat the identical failed call."
        };
        return new(code, ex.Message, canRepair, next);
    }

    public static string Fingerprint(ToolCall call)
    {
        // Parameter ordering/whitespace must not evade the identical-call guard.
        var sorted = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in call.Arguments.EnumerateObject()) sorted[p.Name] = p.Value;
        return SafeWorkspace.Hash(Encoding.UTF8.GetBytes(call.Name + JsonSerializer.Serialize(sorted)));
    }

    public static JsonObject ToJson(RecoveryFault fault) => new()
    {
        ["code"] = fault.Code, ["message"] = fault.Message,
        ["recoverable"] = fault.Recoverable, ["next"] = fault.Next
    };
}

// This guard checks observable execution, not whether free-form model prose is true.
public sealed class RecoverySupervisor
{
    private readonly Dictionary<string, int> _failures = [];
    private readonly List<string> _observations = [];
    private readonly List<(ToolCall Call, RecoveryFault Fault)> _pending = [];
    private int _nudges;
    private readonly Dictionary<string, int> _observationVersions = [];
    private readonly Dictionary<string, int> _failureVersions = [];
    public bool HasPending => _pending.Count > 0;
    public bool NeedsContinuation => _pending.Any(p => p.Fault.Recoverable) && _nudges < 2;
    public string Continuation()
    {
        _nudges++;
        return "The host observed unresolved tool failures. Your proposed final answer is not verified. " +
            "Use a different diagnostic/corrected call now; do not conclude after one failure. " +
            "If there is genuine ambiguity or no permitted next step, state that with evidence, not a claim of success.\n" +
            string.Join("\n", _pending.TakeLast(4).Select(p => p.Call.Name + ": " + p.Fault.Code + " · " + p.Fault.Next));
    }
    public string? Block(ToolCall call)
    {
        var key = RecoveryPolicy.Fingerprint(call);
        if (_failures.GetValueOrDefault(key) > 0 && call.Name is ("write_text" or "publish_artifact" or "click_control" or "type_control" or "open_file") && _failureVersions.GetValueOrDefault(key) == _observationVersions.GetValueOrDefault(Subject(call)))
            return "The previous side-effecting call failed with an uncertain outcome. Observe the actual file/window state before retrying. This duplicate was NOT executed.";
        if (_failures.GetValueOrDefault(key) < 2) return null;
        return "Identical call already failed twice this turn. Inspect different evidence or change the approach; this repeated call was NOT executed.";
    }
    public void Observe(ToolCall call, string result)
    {
        using var document = JsonDocument.Parse(result); var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("recovery", out var r))
        {
            var fault = new RecoveryFault(r.GetProperty("code").GetString()!, r.GetProperty("message").GetString()!, r.GetProperty("recoverable").GetBoolean(), r.GetProperty("next").GetString()!);
            var key = RecoveryPolicy.Fingerprint(call); _failures[key] = _failures.GetValueOrDefault(key) + 1;
            _failureVersions[key] = _observationVersions.GetValueOrDefault(Subject(call));
            _pending.RemoveAll(p => RecoveryPolicy.Fingerprint(p.Call) == key);
            _pending.Add((call, fault)); return;
        }
        _observations.Add(call.Name);
        if (call.Name is "read_file" or "word_paragraphs" or "inspect_window")
        {
            var subject = Subject(call); _observationVersions[subject] = _observationVersions.GetValueOrDefault(subject) + 1;
        }
        // Corrected execution or artifact inspection is recovery evidence, not a
        // semantic proof of task completion. A plan/listing alone is insufficient.
        _pending.RemoveAll(p => p.Fault.Recoverable &&
            (p.Fault.Code == "unknown_tool" && call.Name is not ("update_plan" or "list_skills") ||
             call.Name is "inspect_artifact" or "view_artifact" ||
             p.Call.Name == call.Name && (p.Fault.Code == "skill_not_found" || EquivalentTarget(p.Call, call))));
    }
    private static string Subject(ToolCall call)
    {
        if (call.Name is "inspect_window" or "click_control" or "type_control") return "window";
        var field = call.Name == "publish_artifact" ? "destination" : "path";
        return "file:" + (call.Arguments.TryGetProperty(field, out var value) ? value.ToString().Replace('\\', '/').ToUpperInvariant() : "");
    }
    private static bool EquivalentTarget(ToolCall failed, ToolCall current)
    {
        string Field(ToolCall c, string key) => c.Arguments.TryGetProperty(key, out var x) ? x.ToString() : "";
        if (failed.Name == "run_python") return true; // Executed correction; still no proof of semantic correctness.
        if (failed.Name == "read_skill") return Field(failed, "name") == Field(current, "name");
        if (failed.Name is "read_file" or "word_paragraphs" or "check_word")
        {
            var a = Path.GetFileNameWithoutExtension(Field(failed, "path"));
            var b = Path.GetFileNameWithoutExtension(Field(current, "path"));
            return Field(failed, "path") == Field(current, "path") || FileDiscovery.Distance(a, b) <= 2;
        }
        return Field(failed, "path") == Field(current, "path") && Field(failed, "destination") == Field(current, "destination");
    }
    public string UnverifiedConclusion() => "Chưa xác minh hoàn tất yêu cầu.\n\n" +
        string.Join("\n", _pending.TakeLast(4).Select(p => "• " + p.Call.Name + ": " + p.Fault.Message)) +
        "\n\nCác bước đã có kết quả: " + (_observations.Count == 0 ? "chưa có" : string.Join(", ", _observations.Distinct())) +
        ". " + (_pending.Any(p => p.Fault.Code == "not_found") ? "Chưa đủ bằng chứng kết luận tệp không tồn tại ở mọi nơi; cần xác định đúng tệp/thư mục trong phạm vi cho phép. " : "Kết quả trên chưa đủ để xác nhận yêu cầu đã được giải quyết. ") +
        "Chi tiết và phần việc đã thực hiện còn trong nhật ký để tiếp tục.";
}

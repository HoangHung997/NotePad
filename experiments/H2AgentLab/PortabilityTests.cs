using System.Text.Json;

namespace H2AgentLab;

public static class PortabilityTests
{
    public static async Task<int> Run(string root)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root)) throw new IOException("Use a new test directory.");
        Directory.CreateDirectory(root);
        var lines = new List<string>(); var failures = 0;
        void Check(bool ok, string message) { if (!ok) throw new IOException(message); }
        async Task Test(string name, Func<Task> action)
        {
            try { await action(); lines.Add("PASS " + name); }
            catch (Exception ex) { failures++; lines.Add("FAIL " + name + " " + ex); }
            File.WriteAllLines(Path.Combine(root, "tests.txt"), lines);
        }
        var tools = new AgentTools(new(root), Path.Combine(root, "state"), (_, _) => Task.FromResult(false), (_, _) => { });
        async Task<JsonElement> Execute(string name, string json)
        {
            using var args = JsonDocument.Parse(json);
            using var result = JsonDocument.Parse(await tools.Execute(new("portability", name, args.RootElement.Clone()), default));
            return result.RootElement.Clone();
        }
        await Test("list_skills query is optional in schema and omitted query lists all five skills", async () =>
        {
            var schema = JsonSerializer.SerializeToElement(AgentTools.Definitions).EnumerateArray().Single(d => d.GetProperty("function").GetProperty("name").GetString() == "list_skills");
            Check(schema.GetProperty("function").GetProperty("parameters").GetProperty("required").GetArrayLength() == 0, "Query still required");
            Check((await Execute("list_skills", "{}")).GetArrayLength() == 5, "Omitted query failed");
        });
        await Test("Empty and whitespace queries enumerate skills; wrong types remain errors", async () =>
        {
            Check((await Execute("list_skills", "{\"query\":\"\"}")).GetArrayLength() == 5, "Empty query failed");
            Check((await Execute("list_skills", "{\"query\":\"  \"}")).GetArrayLength() == 5, "Whitespace query failed");
            Check((await Execute("list_skills", "{\"query\":4}")).GetProperty("recovery").GetProperty("code").GetString() == "invalid_arguments", "Invalid type accepted");
        });
        await Test("Unmatched topic returns actual available skills, not a misleading empty installation", async () =>
        {
            var result = await Execute("list_skills", "{\"query\":\"missing-topic-947\"}");
            Check(result.GetProperty("matches").GetArrayLength() == 0 && result.GetProperty("availableSkills").GetArrayLength() == 5, "No discovery recovery");
        });
        await Test("Unknown tool is rejected with actual tool names; no alias runs", async () =>
        {
            var result = await Execute("discover_available_skills", "{}");
            Check(result.GetProperty("recovery").GetProperty("code").GetString() == "unknown_tool", "Unknown alias accepted");
            Check(result.GetProperty("availableTools").EnumerateArray().Any(n => n.GetString() == "list_skills"), "Missing exact tool names");
        });
        await Test("Unknown skill gives real names and absent bundles report unavailable", async () =>
        {
            var result = await Execute("read_skill", "{\"name\":\"invented\",\"path\":\"SKILL.md\"}");
            Check(result.GetProperty("error").GetString()!.Contains("documents"), "No observed candidates");
            var catalog = new SkillCatalog(Path.Combine(root, "absent-skills"));
            try { catalog.Discover(""); throw new Exception("Missing skills silently accepted"); }
            catch (AgentFaultException e) { Check(e.Message.Contains("Portable"), "No repair instructions"); }
        });
        await Test("Required mutation arguments remain required", async () =>
        {
            var result = await Execute("write_text", "{\"path\":\"must-not-exist.txt\"}");
            Check(result.GetProperty("success").GetBoolean() == false && !File.Exists(Path.Combine(root, "must-not-exist.txt")), "Unexpected mutation");
        });
        await Test("Portable runtime chosen without any local installation; corrupt bundle never falls back", () =>
        {
            var app = Path.Combine(root, "portable app"); var local = Path.Combine(root, "new-user");
            Directory.CreateDirectory(Path.Combine(app, "python"));
            var resolved = WindowsPythonSandbox.ResolveRuntimeRoot(app, local);
            Check(resolved == Path.Combine(app, "python"), "Selected developer runtime");
            Check(WindowsPythonSandbox.RuntimeProblem(resolved) is not null, "Empty runtime marked ready");
            File.WriteAllText(Path.Combine(resolved, ".ready"), "");
            Check(WindowsPythonSandbox.RuntimeProblem(resolved)!.Contains("python.exe"), "Marker alone accepted");
            return Task.CompletedTask;
        });
        await Test("Development build retains explicit per-user runtime compatibility", () =>
        {
            var app = Path.Combine(root, "dev-app"); var local = Path.Combine(root, "local");
            Check(WindowsPythonSandbox.ResolveRuntimeRoot(app, local) == Path.Combine(local, "H2AgentLab", "runtime", "python"), "Legacy installation broken");
            return Task.CompletedTask;
        });
        lines.Add($"RESULT: {lines.Count - failures} passed, {failures} failed. Packaging/arguments only; not an AI quality test.");
        File.WriteAllLines(Path.Combine(root, "tests.txt"), lines);
        return failures == 0 ? 0 : 1;
    }
}

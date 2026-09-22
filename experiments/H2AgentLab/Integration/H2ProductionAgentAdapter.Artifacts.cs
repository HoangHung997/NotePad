using System.Text.Json;
using H2AgentLab.Session;
using H2AgentLab.Tasking;
using H2Notes.Core;

namespace H2AgentLab.Integration;

public sealed partial class H2ProductionAgentAdapter
{
    private string? EvidenceContentPath(Guid task, string reference)
    {
        if (reference.Length != 37 || !reference.StartsWith("h2a1_", StringComparison.Ordinal)
            || !reference.AsSpan(5).ToString().All(Uri.IsHexDigit)) return null;
        var path = Path.Combine(_stateRoot, "tasks", task.ToString("N"), "artifacts", "context", reference + ".txt");
        return File.Exists(path) ? path : null;
    }

    private IReadOnlyList<H2AgentEvidence> ProjectArtifacts(LiveTask task, IReadOnlyList<AgentEvidenceReference> evidence, AgentTools tools)
    {
        var files = new Dictionary<string, H2AgentEvidence>(StringComparer.OrdinalIgnoreCase);
        var store = new ArtifactStore(tools.StateRoot);
        void Register(string path, string? hash, string source)
        {
            if (!File.Exists(path)) return;
            files[path] = new("file:" + task.TaskId.ToString("N") + ":" + SafeWorkspace.Hash(System.Text.Encoding.UTF8.GetBytes(path)),
                "artifact", hash, Path.GetFileName(path), LocalPath: path, Provenance: source);
        }
        foreach (var item in evidence.Where(e => e.Kind == AgentEvidenceKind.ToolResult))
        {
            try
            {
                var raw = store.ReadText(item.ReferenceId);
                using var json = JsonDocument.Parse(raw);
                var root = json.RootElement;
                var tool = item.Summary?.Split(" observed:", 2)[0] ?? "";
                if (tool is "write_text" or "publish_artifact" or "read_file" or "read_document")
                {
                    if (root.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String)
                    {
                        var full = tools.Workspace.Resolve(path.GetString()!);
                        var hash = root.TryGetProperty("sha256", out var h) ? h.GetString() : null;
                        Register(full, hash, tool + " · " + item.ReferenceId);
                    }
                }
                if (tool is "run_python" or "inspect_run" && root.TryGetProperty("runId", out var runId))
                {
                    var run = tools.ObserveScriptRunEvidence(runId.GetString()!);
                    foreach (var artifact in run.Artifacts)
                    {
                        _ = tools.ObserveScriptArtifact(run.RunId, artifact.Path);
                        var output = new SafeWorkspace(Path.Combine(tools.StateRoot, "runs", run.RunId, "work", "output"));
                        Register(output.Resolve(artifact.Path), artifact.Sha256, artifact.EvidenceId);
                    }
                }
                if (tool is "excel.save_copy" or "word.save_copy")
                {
                    var path = root.GetProperty("DestinationPath").GetString();
                    var hash = root.GetProperty("SavedCopySha256").GetString();
                    if (path is not null) Register(tools.Workspace.Resolve(path), hash, item.ReferenceId);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException) { }
        }
        return files.Values.GroupBy(e => (e.Sha256 ?? e.LocalPath) + ":" + Path.GetFileName(e.LocalPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last()).ToArray();
    }
}

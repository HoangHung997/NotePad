using System.Text.Json;

namespace H2AgentLab;

public sealed record ScriptArtifact(string Path, long Bytes, string Sha256)
{
    public string ArtifactId { get; init; } = "";
    public string EvidenceId { get; init; } = "";
}

public sealed record ScriptRun(string Id, string Workspace, int ExitCode, Dictionary<string, string> Inputs, List<ScriptArtifact> Artifacts)
{
    public string EvidenceId { get; init; } = "";
    public DateTime CompletedUtc { get; init; }
    public string? PreviousRunId { get; init; }
}

public sealed record ScriptRunEvidence(
    string RunId,
    string EvidenceId,
    int ExitCode,
    IReadOnlyList<ScriptArtifact> Artifacts,
    DateTime CompletedUtc);
public sealed partial class ScriptWorkspace(SafeWorkspace workspace, string stateRoot, Func<Approval, CancellationToken, Task<bool>> approve)
{
    private bool _authorized, _denied;
    private string RunsRoot => Path.Combine(stateRoot, "runs");
    public async Task<object> Run(string code, string inputs, string previous, CancellationToken ct)
    {
        if (code.Length is < 1 or > 100000) throw new IOException("Python code must contain 1..100000 characters.");
        if (_denied) throw new AgentFaultException("denied", "Script execution was declined for this turn. Do not retry through another tool.", false);
        if (!WindowsPythonSandbox.IsReady) throw new AgentFaultException("unavailable", WindowsPythonSandbox.RuntimeProblem() + " Không có chế độ chạy ngoài sandbox thay thế.", false);
        var selected = inputs.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
        if (selected.Length > 20) throw new IOException("Select at most 20 input files per run.");
        var originals = new Dictionary<string, byte[]>(); long total = 0;
        foreach (var path in selected)
        {
            var data = workspace.Read(path); total += data.Length;
            if (total > 48 * 1024 * 1024) throw new IOException("Inputs exceed 48 MB.");
            originals[path] = data;
        }
        var prior = string.IsNullOrWhiteSpace(previous) ? null : Load(previous);
        if (!_authorized)
        {
            _authorized = await approve(new("Cho AI tự chạy mã trên bản sao trong lượt này", "Windows AppContainer, không cấp quyền mạng; không nhận API key, không đọc/ghi kho gốc. Chỉ chép tệp được chọn vào vùng thử. AI có thể chạy, kiểm và sửa mã nhiều lần trong lượt này. Mỗi lần tối đa 120 giây / 768 MB RAM. Kết quả chưa tự ghi vào thư mục gốc.\n\nThư mục nguồn: " + workspace.Root + "\nTệp lần đầu: " + string.Join(", ", selected) + "\n\nMÃ LẦN ĐẦU\n" + code), ct);
            if (!_authorized) { _denied = true; throw new AgentFaultException("denied", "Script execution declined.", false); }
        }
        ct.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N"); var root = Path.Combine(RunsRoot, id); var work = Path.Combine(root, "work");
        Directory.CreateDirectory(Path.Combine(work, "input")); Directory.CreateDirectory(Path.Combine(work, "output")); Directory.CreateDirectory(Path.Combine(work, "tmp"));
        var copyScope = new SafeWorkspace(Path.Combine(work, "input"));
        foreach (var (path, data) in originals)
        { var dest = copyScope.Resolve(path); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.WriteAllBytes(dest, data); }
        if (prior is not null)
        {
            foreach (var item in prior.Artifacts)
            {
                var data = Read(previous, item.Path); total += data.Length;
                if (total > 48 * 1024 * 1024) throw new IOException("Combined previous outputs and inputs exceed 48 MB.");
                var dest = copyScope.Resolve("previous/" + item.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.WriteAllBytes(dest, data);
            }
        }
        File.Copy(Path.Combine(AppContext.BaseDirectory, "runtime", "worker.py"), Path.Combine(work, "worker.py"));
        File.WriteAllText(Path.Combine(work, "task.py"), code);
        var exit = await WindowsPythonSandbox.Run(work, ct);
        var output = new SafeWorkspace(Path.Combine(work, "output"));
        var artifacts = new List<ScriptArtifact>();
        foreach (var file in Directory.EnumerateFiles(output.Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Take(101))
        {
            if (artifacts.Count == 100) throw new IOException("Too many output files.");
            var name = Path.GetRelativePath(output.Root, file).Replace('\\', '/'); var bytes = output.Read(name);
            var sha = SafeWorkspace.Hash(bytes);
            artifacts.Add(new ScriptArtifact(name, bytes.Length, sha)
            {
                ArtifactId = ArtifactId(id, name, sha),
                EvidenceId = ArtifactEvidenceId(id, name, sha)
            });
        }
        var completedUtc = DateTime.UtcNow;
        var run = new ScriptRun(id, workspace.Root, exit, originals.ToDictionary(x => x.Key, x => SafeWorkspace.Hash(x.Value)), artifacts)
        {
            EvidenceId = RunEvidenceId(id),
            CompletedUtc = completedUtc,
            PreviousRunId = prior?.Id
        };
        File.WriteAllText(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(run));
        string Log(string name)
        {
            var scope = new SafeWorkspace(work); var file = scope.Resolve(name);
            if (!File.Exists(file)) return "(No log file; process may have stopped before Python initialized.)";
            using var reader = new StreamReader(file); var buffer = new char[24000]; var count = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, count) + (reader.EndOfStream ? "" : "\n[truncated]");
        }
        var recovered = new List<string>();
        var ancestor = prior;
        while (exit == 0 && ancestor is not null && recovered.Count < 64
            && ancestor.Inputs.Count == run.Inputs.Count && ancestor.Inputs.All(p => run.Inputs.TryGetValue(p.Key, out var hash) && hash == p.Value))
        {
            if (ancestor.ExitCode != 0) recovered.Add(ancestor.Id);
            ancestor = ancestor.PreviousRunId is { Length: > 0 } parent ? Load(parent) : null;
        }
        return new { runId = id, evidenceId = run.EvidenceId, failureId = exit == 0 ? null : id,
            resolvedFailureIds = recovered.ToArray(),
            exitCode = exit, stdout = Log("stdout.log"), stderr = Log("stderr.log"), artifacts,
            stagedInputs = originals.Keys.Select(p => "input/" + p.Replace('\\', '/')).ToArray(), outputFolder = "output/",
            originalFilesChanged = false, execution = "Windows AppContainer; no network capability; staged copies only",
            next = exit == 0 ? "Inspect results. Publish only verified outputs via publish_artifact."
                : "Repair the script and rerun with the SAME inputs and previous_run='" + id + "' so the host can track recovery of this failed attempt. Check requested content and preservation before publishing." };
    }
    public ScriptRun Load(string id)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-f0-9]{32}$")) throw new IOException("Invalid run ID.");
        var scope = new SafeWorkspace(RunsRoot); var bytes = scope.Read(id + "/manifest.json");
        var run = JsonSerializer.Deserialize<ScriptRun>(bytes) ?? throw new IOException("Invalid run manifest.");
        if (run.Id != id || !string.Equals(run.Workspace, workspace.Root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Run belongs to a different workspace.");
        return NormalizeEvidence(run);
    }
    public byte[] Read(string id, string path)
    {
        var run = Load(id); var item = run.Artifacts.SingleOrDefault(a => a.Path == path) ?? throw new IOException("Not a recorded output artifact.");
        var scope = new SafeWorkspace(Path.Combine(RunsRoot, id, "work", "output")); var data = scope.Read(path);
        if (SafeWorkspace.Hash(data) != item.Sha256) throw new IOException("Output changed after execution. Run or inspect again; refusing stale artifact.");
        return data;
    }
    public object InspectRun(string id)
    {
        var run = Load(id); var scope = new SafeWorkspace(Path.Combine(RunsRoot, id, "work"));
        string Text(string file)
        {
            var path = scope.Resolve(file); if (!File.Exists(path)) return "";
            using var reader = new StreamReader(path); var buffer = new char[24000]; var count = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, count) + (reader.EndOfStream ? "" : "\n[truncated]");
        }
        return new { run, code = Text("task.py"), stdout = Text("stdout.log"), stderr = Text("stderr.log") };
    }
    public async Task<object> Publish(string id, string artifact, string target, string expectedHash, CancellationToken ct)
    {
        var run = Load(id); if (run.ExitCode != 0) throw new IOException("Cannot publish from a failed script. Fix and verify it first.");
        var data = Read(id, artifact);
        var verification = LoadVerification(id, artifact);
        var ext = Path.GetExtension(target).ToLowerInvariant();
        if (!SafeWorkspace.TextExtensions.Contains(ext) && ext is not (".docx" or ".xlsx" or ".pdf" or ".png" or ".jpg")) throw new IOException("Unsupported publication format.");
        if (ext != Path.GetExtension(artifact).ToLowerInvariant()) throw new IOException("Output and destination extensions must match.");
        var destination = workspace.Resolve(target);
        if (File.Exists(destination) && (expectedHash.Length == 0 || SafeWorkspace.Hash(workspace.Read(target)) != expectedHash)) throw new AgentFaultException("stale_state", "Destination changed or missing expected hash. Read the current file before replacement.");
        if (!await approve(new("Lưu kết quả: " + target, $"Tác vụ: {id}\nKết quả: {artifact}\nĐích: {destination}\n{data.Length:N0} bytes · SHA256 {SafeWorkspace.Hash(data)}\n" + (File.Exists(destination) ? "Thay bản hiện có, có backup. Chỉ duyệt sau khi đã xem/kiểm kết quả." : "Tạo tệp mới. Không suy từ exit code rằng nội dung đã đúng.")), ct)) throw new AgentFaultException("denied", "Publication declined.", false);
        ct.ThrowIfCancellationRequested();
        return new
        {
            path = target,
            sha256 = workspace.Write(target, data, expectedHash, stateRoot),
            sourceRun = id,
            sourceEvidence = run.EvidenceId,
            sourceArtifactId = run.Artifacts.Single(x => x.Path == artifact).ArtifactId,
            sourceArtifactEvidence = run.Artifacts.Single(x => x.Path == artifact).EvidenceId,
            artifactVerification = verification,
            verificationEvidence = verification.EvidenceId,
            requiresFurtherVerification = verification.RequiresFurtherVerification
        };
    }

    public ScriptRunEvidence Evidence(string id)
    {
        var run = Load(id);
        return new ScriptRunEvidence(
            run.Id,
            run.EvidenceId,
            run.ExitCode,
            run.Artifacts.ToArray(),
            run.CompletedUtc);
    }

    private static ScriptRun NormalizeEvidence(ScriptRun run)
    {
        var normalizedArtifacts = run.Artifacts
            .Select(artifact => artifact with
            {
                ArtifactId = string.IsNullOrWhiteSpace(artifact.ArtifactId)
                    ? ArtifactId(run.Id, artifact.Path, artifact.Sha256)
                    : artifact.ArtifactId,
                EvidenceId = string.IsNullOrWhiteSpace(artifact.EvidenceId)
                    ? ArtifactEvidenceId(run.Id, artifact.Path, artifact.Sha256)
                    : artifact.EvidenceId
            })
            .ToList();

        return run with
        {
            Artifacts = normalizedArtifacts,
            EvidenceId = string.IsNullOrWhiteSpace(run.EvidenceId)
                ? RunEvidenceId(run.Id)
                : run.EvidenceId,
            CompletedUtc = run.CompletedUtc == default
                ? DateTime.UnixEpoch
                : run.CompletedUtc.ToUniversalTime()
        };
    }

    private static string RunEvidenceId(string runId)
        => "evidence:python-run:" + runId;

    private static string ArtifactId(string runId, string path, string sha256)
        => "artifact:python:" + runId + ":" + StableSuffix(path, sha256);

    private static string ArtifactEvidenceId(string runId, string path, string sha256)
        => "evidence:python-artifact:" + runId + ":" + StableSuffix(path, sha256);

    private static string StableSuffix(string path, string sha256)
    {
        var material = System.Text.Encoding.UTF8.GetBytes(
            path.Replace('\\', '/') + "\n" + sha256.ToLowerInvariant());
        return SafeWorkspace.Hash(material)[..20];
    }
}

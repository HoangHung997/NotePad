using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using H2Notes.Core;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length >= 2 && args[0] == "--self-test")
                return await SelfTest(Path.GetFullPath(args[1]));
            if (args.Length >= 5 && args[0] == "--node")
            {
                var role = args[1].ToLowerInvariant();
                var root = Path.GetFullPath(args[2]);
                var session = SafeId(args[3]);
                var output = Path.GetFullPath(args[4]);
                return role switch
                {
                    "coordinator" => await RunCoordinator(root, session, output),
                    "peer" => await RunPeer(root, session, output),
                    _ => Usage("Role must be coordinator or peer.")
                };
            }
            if (args.Length >= 2 && args[0] == "--crash-save")
                return CrashSave(Path.GetFullPath(args[1]));
            return Usage();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int Usage(string? error = null)
    {
        if (!string.IsNullOrWhiteSpace(error)) Console.Error.WriteLine(error);
        Console.Error.WriteLine(
            """
            H2 Notes NAS/SMB acceptance probe

            Real two-PC run:
              PC1: H2Notes.NasAcceptance --node coordinator <shared-root> <session-id> <local-output-dir>
              PC2: H2Notes.NasAcceptance --node peer        <shared-root> <session-id> <local-output-dir>

            CI/local harness self-test:
              H2Notes.NasAcceptance --self-test <output-dir>

            Both PCs must point to the same physical NAS folder. The probe writes only below:
              <shared-root>/.h2-nas-acceptance/<session-id>/
            """);
        return 2;
    }

    private static async Task<int> SelfTest(string output)
    {
        Directory.CreateDirectory(output);
        var root = Path.Combine(Path.GetTempPath(), "h2-nas-acceptance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var session = "selftest-" + Guid.NewGuid().ToString("N");
        var peerOut = Path.Combine(output, "peer");
        var coordinatorOut = Path.Combine(output, "coordinator");
        Directory.CreateDirectory(peerOut);
        Directory.CreateDirectory(coordinatorOut);

        using var peer = StartSelf("--node", "peer", root, session, peerOut);
        var code = await RunCoordinator(root, session, coordinatorOut);
        if (!peer.WaitForExit(30_000))
        {
            try { peer.Kill(true); } catch { }
            throw new TimeoutException("Self-test peer did not exit.");
        }
        if (code != 0 || peer.ExitCode != 0)
            throw new InvalidOperationException($"Self-test failed: coordinator={code}, peer={peer.ExitCode}.");

        var marker = Path.Combine(output, "self-test-result.txt");
        File.WriteAllText(marker, "PASS H2 NAS acceptance harness self-test" + Environment.NewLine);
        Console.WriteLine("PASS H2 NAS acceptance harness self-test");
        try { Directory.Delete(root, true); } catch { }
        return 0;
    }

    private static async Task<int> RunCoordinator(string sharedRoot, string sessionId, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var session = SessionRoot(sharedRoot, sessionId);
        Directory.CreateDirectory(session);
        var evidence = NewReport("coordinator", sharedRoot, sessionId);
        WriteJson(Path.Combine(session, "coordinator.ready.json"), NodeInfo("coordinator"));

        await WaitFile(Path.Combine(session, "peer.ready.json"));
        var peerReady = ReadJson<NodeReady>(Path.Combine(session, "peer.ready.json"));
        evidence.PeerMachine = peerReady.Machine;
        if (!sessionId.StartsWith("selftest-", StringComparison.Ordinal) &&
            string.Equals(peerReady.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Real NAS acceptance requires two different physical machine names. Use --self-test for same-machine harness verification.");
        evidence.Cases.Add(Pass("TWO-PC-RENDEZVOUS",
            sessionId.StartsWith("selftest-", StringComparison.Ordinal)
                ? "Local harness self-test rendezvous completed."
                : $"Two distinct machines observed the same acceptance session: {Environment.MachineName} <-> {peerReady.Machine}."));

        await CoordinatorLockCase(session, evidence);
        await CoordinatorFlushCase(session, evidence);
        await CoordinatorRenameCase(session, evidence);
        await CoordinatorWorkspaceConcurrencyCase(session, evidence);
        await CoordinatorCrashRecoveryCase(session, evidence);

        evidence.CompletedUtc = DateTimeOffset.UtcNow;
        evidence.Overall = evidence.Cases.All(c => c.Status == "PASS") ? "PASS" : "FAIL";
        var local = Path.Combine(outputDir, "nas-acceptance-coordinator.json");
        WriteJson(local, evidence);
        WriteJson(Path.Combine(session, "coordinator.summary.json"), evidence);
        File.WriteAllText(Path.Combine(session, "session.complete"), evidence.Overall);
        Console.WriteLine(JsonSerializer.Serialize(new { evidence.Overall, Evidence = local, SharedSession = session }, Json));
        return evidence.Overall == "PASS" ? 0 : 1;
    }

    private static async Task<int> RunPeer(string sharedRoot, string sessionId, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var session = SessionRoot(sharedRoot, sessionId);
        Directory.CreateDirectory(session);
        var evidence = NewReport("peer", sharedRoot, sessionId);
        await WaitFile(Path.Combine(session, "coordinator.ready.json"));
        var coordinatorReady = ReadJson<NodeReady>(Path.Combine(session, "coordinator.ready.json"));
        evidence.PeerMachine = coordinatorReady.Machine;
        if (!sessionId.StartsWith("selftest-", StringComparison.Ordinal) &&
            string.Equals(coordinatorReady.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Real NAS acceptance requires two different physical machine names. Use --self-test for same-machine harness verification.");
        WriteJson(Path.Combine(session, "peer.ready.json"), NodeInfo("peer"));
        evidence.Cases.Add(Pass("TWO-PC-RENDEZVOUS",
            sessionId.StartsWith("selftest-", StringComparison.Ordinal)
                ? "Local harness self-test rendezvous completed."
                : $"Peer observed coordinator on distinct machine {coordinatorReady.Machine}."));

        await PeerLockCase(session, evidence);
        await PeerReadCase(session, "flush", "FLUSH-VISIBILITY", evidence);
        await PeerReadCase(session, "rename", "RENAME-REPLACE-VISIBILITY", evidence);
        await PeerWorkspaceConcurrencyCase(session, evidence);
        await PeerCrashRecoveryCase(session, evidence);

        await WaitFile(Path.Combine(session, "session.complete"));
        evidence.CompletedUtc = DateTimeOffset.UtcNow;
        evidence.Overall = evidence.Cases.All(c => c.Status == "PASS") ? "PASS" : "FAIL";
        var local = Path.Combine(outputDir, "nas-acceptance-peer.json");
        WriteJson(local, evidence);
        WriteJson(Path.Combine(session, "peer.summary.json"), evidence);
        Console.WriteLine(JsonSerializer.Serialize(new { evidence.Overall, Evidence = local, SharedSession = session }, Json));
        return evidence.Overall == "PASS" ? 0 : 1;
    }

    private static async Task CoordinatorLockCase(string session, ProbeReport evidence)
    {
        var path = Path.Combine(session, "exclusive.lock");
        await using (var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            var bytes = Encoding.UTF8.GetBytes("held-by=" + Environment.MachineName);
            held.SetLength(0);
            await held.WriteAsync(bytes);
            held.Flush(true);
            File.WriteAllText(Path.Combine(session, "lock.held"), DateTimeOffset.UtcNow.ToString("O"));
            await WaitFile(Path.Combine(session, "lock.peer-blocked.json"));
            var result = ReadJson<PeerResult>(Path.Combine(session, "lock.peer-blocked.json"));
            if (!result.Success) throw new InvalidOperationException("Peer acquired an exclusive lock while coordinator still held it.");
        }

        File.WriteAllText(Path.Combine(session, "lock.released"), DateTimeOffset.UtcNow.ToString("O"));
        await WaitFile(Path.Combine(session, "lock.peer-after-release.json"));
        var after = ReadJson<PeerResult>(Path.Combine(session, "lock.peer-after-release.json"));
        if (!after.Success) throw new InvalidOperationException("Peer could not acquire lock after release.");
        evidence.Cases.Add(Pass("EXCLUSIVE-LOCK", "FileShare.None was visible across nodes and acquisition succeeded after release."));
    }

    private static async Task PeerLockCase(string session, ProbeReport evidence)
    {
        await WaitFile(Path.Combine(session, "lock.held"));
        var path = Path.Combine(session, "exclusive.lock");
        var blocked = false;
        try
        {
            using var unexpected = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { blocked = true; }
        WriteJson(Path.Combine(session, "lock.peer-blocked.json"), new PeerResult(blocked, DateTimeOffset.UtcNow, Environment.MachineName));
        if (!blocked) throw new InvalidOperationException("Exclusive lock was not enforced across nodes.");

        await WaitFile(Path.Combine(session, "lock.released"));
        var acquired = false;
        var deadline = DateTime.UtcNow + DefaultTimeout;
        do
        {
            try
            {
                using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                acquired = true;
                break;
            }
            catch (IOException) { await Task.Delay(100); }
        } while (DateTime.UtcNow < deadline);
        WriteJson(Path.Combine(session, "lock.peer-after-release.json"), new PeerResult(acquired, DateTimeOffset.UtcNow, Environment.MachineName));
        if (!acquired) throw new IOException("Could not acquire released lock.");
        evidence.Cases.Add(Pass("EXCLUSIVE-LOCK", "Peer was blocked while held and acquired after release."));
    }

    private static async Task CoordinatorFlushCase(string session, ProbeReport evidence)
    {
        var payload = "flush-" + Guid.NewGuid().ToString("N") + "-" + new string('F', 8192);
        var path = Path.Combine(session, "flush.payload");
        WriteDurable(path, Encoding.UTF8.GetBytes(payload));
        WriteJson(Path.Combine(session, "flush.marker.json"), new PayloadMarker(Hash(path), new FileInfo(path).Length, DateTimeOffset.UtcNow));
        await WaitFile(Path.Combine(session, "flush.peer.json"));
        var peer = ReadJson<PayloadObservation>(Path.Combine(session, "flush.peer.json"));
        if (peer.Hash != Hash(path) || peer.Length != new FileInfo(path).Length)
            throw new InvalidDataException("Peer did not observe exact flushed bytes.");
        evidence.Cases.Add(Pass("FLUSH-VISIBILITY", $"Peer observed flushed bytes; delayMs={peer.DelayMs}."));
    }

    private static async Task CoordinatorRenameCase(string session, ProbeReport evidence)
    {
        var temp = Path.Combine(session, "rename.tmp");
        var final = Path.Combine(session, "rename.published");
        WriteDurable(final, Encoding.UTF8.GetBytes("old-generation-" + Guid.NewGuid().ToString("N")));
        var oldHash = Hash(final);

        var bytes = Encoding.UTF8.GetBytes("replacement-" + Guid.NewGuid().ToString("N") + "-" + new string('R', 8192));
        WriteDurable(temp, bytes);
        File.Move(temp, final, true);
        var newHash = Hash(final);
        if (newHash == oldHash) throw new InvalidDataException("Replacement payload did not change.");

        WriteJson(Path.Combine(session, "rename.marker.json"), new PayloadMarker(newHash, new FileInfo(final).Length, DateTimeOffset.UtcNow));
        await WaitFile(Path.Combine(session, "rename.peer.json"));
        var peer = ReadJson<PayloadObservation>(Path.Combine(session, "rename.peer.json"));
        if (peer.Hash != newHash || peer.Length != new FileInfo(final).Length)
            throw new InvalidDataException("Peer did not observe exact replacement bytes.");
        evidence.Cases.Add(Pass("RENAME-REPLACE-VISIBILITY", $"Peer observed exact replacement generation; delayMs={peer.DelayMs}."));
    }

    private static async Task PeerReadCase(string session, string prefix, string caseId, ProbeReport evidence)
    {
        var markerPath = Path.Combine(session, prefix + ".marker.json");
        await WaitFile(markerPath);
        var marker = ReadJson<PayloadMarker>(markerPath);
        var dataPath = prefix == "rename" ? Path.Combine(session, "rename.published") : Path.Combine(session, "flush.payload");
        var sw = Stopwatch.StartNew();
        string? hash = null;
        long length = -1;
        Exception? last = null;
        var deadline = DateTime.UtcNow + DefaultTimeout;
        do
        {
            try
            {
                if (File.Exists(dataPath))
                {
                    hash = Hash(dataPath);
                    length = new FileInfo(dataPath).Length;
                    if (hash == marker.Hash && length == marker.Length) break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { last = ex; }
            await Task.Delay(100);
        } while (DateTime.UtcNow < deadline);
        sw.Stop();
        if (hash != marker.Hash || length != marker.Length)
            throw new IOException($"Timed out observing {caseId}.", last);
        var observation = new PayloadObservation(hash, length, sw.ElapsedMilliseconds, Environment.MachineName, DateTimeOffset.UtcNow);
        WriteJson(Path.Combine(session, prefix + ".peer.json"), observation);
        evidence.Cases.Add(Pass(caseId, $"Observed exact payload; delayMs={sw.ElapsedMilliseconds}."));
    }

    private static async Task CoordinatorWorkspaceConcurrencyCase(string session, ProbeReport evidence)
    {
        var workspace = Path.Combine(session, "workspace");
        Directory.CreateDirectory(workspace);
        var seedStore = new ProjectWorkspaceStore(workspace, writerId: "probe-seed");
        seedStore.LoadOrImport();
        var seed = SheetStorage.Demo();
        seedStore.Save(seed);
        File.WriteAllText(Path.Combine(session, "workspace.seeded"), DateTimeOffset.UtcNow.ToString("O"));

        var store = new ProjectWorkspaceStore(workspace, writerId: "probe-coordinator-" + Environment.MachineName);
        var state = store.LoadOrImport();
        var project = state.Notes.First(n => n.IsBoard).Projects[0];
        var coordinatorText = "NAS coordinator " + Guid.NewGuid().ToString("N");
        project.ChecklistItems.Add(new TaskRecord { Text = coordinatorText, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow });
        WriteJson(Path.Combine(session, "workspace.coordinator-intent.json"), new Intent(project.Id, coordinatorText));
        File.WriteAllText(Path.Combine(session, "workspace.coordinator-loaded"), DateTimeOffset.UtcNow.ToString("O"));

        await WaitFile(Path.Combine(session, "workspace.peer-loaded"));
        File.WriteAllText(Path.Combine(session, "workspace.save-go"), DateTimeOffset.UtcNow.ToString("O"));
        store.SaveIncremental(state, new HashSet<Guid> { project.Id });
        File.WriteAllText(Path.Combine(session, "workspace.coordinator-saved"), DateTimeOffset.UtcNow.ToString("O"));
        await WaitFile(Path.Combine(session, "workspace.peer-saved"));

        var final = new ProjectWorkspaceStore(workspace, writerId: "probe-reader").LoadOrImport();
        var peerIntent = ReadJson<Intent>(Path.Combine(session, "workspace.peer-intent.json"));
        var all = final.Notes.Where(n => n.IsBoard).SelectMany(n => n.Projects).SelectMany(p => p.ChecklistItems).Select(t => t.Text).ToHashSet();
        if (!all.Contains(coordinatorText) || !all.Contains(peerIntent.Text))
            throw new InvalidDataException("Concurrent saves did not converge to both node edits.");
        evidence.Cases.Add(Pass("CONCURRENT-WRITERS-READ-AFTER-COMMIT", "Two preloaded writers saved concurrently and final reread retained both edits."));
    }

    private static async Task PeerWorkspaceConcurrencyCase(string session, ProbeReport evidence)
    {
        await WaitFile(Path.Combine(session, "workspace.seeded"));
        var workspace = Path.Combine(session, "workspace");
        var store = new ProjectWorkspaceStore(workspace, writerId: "probe-peer-" + Environment.MachineName);
        var state = store.LoadOrImport();
        var board = state.Notes.First(n => n.IsBoard);
        var project = board.Projects.Count > 1 ? board.Projects[1] : board.Projects[0];
        var peerText = "NAS peer " + Guid.NewGuid().ToString("N");
        project.ChecklistItems.Add(new TaskRecord { Text = peerText, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow });
        WriteJson(Path.Combine(session, "workspace.peer-intent.json"), new Intent(project.Id, peerText));
        File.WriteAllText(Path.Combine(session, "workspace.peer-loaded"), DateTimeOffset.UtcNow.ToString("O"));

        await WaitFile(Path.Combine(session, "workspace.coordinator-loaded"));
        await WaitFile(Path.Combine(session, "workspace.save-go"));
        store.SaveIncremental(state, new HashSet<Guid> { project.Id });
        File.WriteAllText(Path.Combine(session, "workspace.peer-saved"), DateTimeOffset.UtcNow.ToString("O"));
        await WaitFile(Path.Combine(session, "workspace.coordinator-saved"));

        var final = new ProjectWorkspaceStore(workspace, writerId: "probe-peer-reader").LoadOrImport();
        var coordinatorIntent = ReadJson<Intent>(Path.Combine(session, "workspace.coordinator-intent.json"));
        var all = final.Notes.Where(n => n.IsBoard).SelectMany(n => n.Projects).SelectMany(p => p.ChecklistItems).Select(t => t.Text).ToHashSet();
        if (!all.Contains(peerText) || !all.Contains(coordinatorIntent.Text))
            throw new InvalidDataException("Peer reread did not observe both committed edits.");
        evidence.Cases.Add(Pass("CONCURRENT-WRITERS-READ-AFTER-COMMIT", "Peer reread observed both serialized commits."));
    }

    private static async Task CoordinatorCrashRecoveryCase(string session, ProbeReport evidence)
    {
        var workspace = Path.Combine(session, "workspace");
        var before = new ProjectWorkspaceStore(workspace, writerId: "probe-before-crash").LoadOrImport();
        var project = before.Notes.First(n => n.IsBoard).Projects[0];
        var expected = project.DisplayName;
        WriteJson(Path.Combine(session, "crash.expected.json"), new CrashExpected(project.Id, expected));

        using var child = StartSelf("--crash-save", workspace);
        if (!child.WaitForExit(30_000))
        {
            try { child.Kill(true); } catch { }
            throw new TimeoutException("Crash helper did not terminate.");
        }
        if (child.ExitCode == 0) throw new InvalidOperationException("Crash helper unexpectedly exited successfully.");
        if (!File.Exists(Path.Combine(workspace, ".h2-transaction.json")))
            throw new InvalidDataException("Interrupted save did not leave a recovery journal.");

        var recovered = new ProjectWorkspaceStore(workspace, writerId: "probe-recovery").LoadOrImport();
        var restored = recovered.Notes.First(n => n.IsBoard).Projects.Single(p => p.Id == project.Id).DisplayName;
        if (restored != expected) throw new InvalidDataException("Recovery did not restore pre-crash generation.");

        File.WriteAllText(Path.Combine(session, "crash.recovered"), DateTimeOffset.UtcNow.ToString("O"));
        await WaitFile(Path.Combine(session, "crash.peer.json"));
        var peer = ReadJson<PeerResult>(Path.Combine(session, "crash.peer.json"));
        if (!peer.Success) throw new InvalidDataException("Peer could not reopen recovered workspace.");
        evidence.Cases.Add(Pass("INTERRUPTED-SAVE-RECOVERY", "Crash left a journal; recovery restored prior generation and peer reopened it."));
    }

    private static async Task PeerCrashRecoveryCase(string session, ProbeReport evidence)
    {
        await WaitFile(Path.Combine(session, "crash.recovered"));
        var workspace = Path.Combine(session, "workspace");
        var expected = ReadJson<CrashExpected>(Path.Combine(session, "crash.expected.json"));
        var success = false;
        try
        {
            var state = new ProjectWorkspaceStore(workspace, writerId: "probe-peer-after-crash").LoadOrImport();
            success = state.Notes.Where(n => n.IsBoard).SelectMany(n => n.Projects)
                .Any(p => p.Id == expected.ProjectId && p.DisplayName == expected.Name);
        }
        catch { success = false; }
        WriteJson(Path.Combine(session, "crash.peer.json"), new PeerResult(success, DateTimeOffset.UtcNow, Environment.MachineName));
        if (!success) throw new InvalidDataException("Peer did not observe restored generation after crash recovery.");
        evidence.Cases.Add(Pass("INTERRUPTED-SAVE-RECOVERY", "Peer reopened and observed the restored pre-crash generation."));
    }

    private static int CrashSave(string workspace)
    {
        var store = new ProjectWorkspaceStore(workspace, n =>
        {
            if (n == 1) Environment.FailFast("H2 NAS acceptance intentional crash after first published entry.");
        }, writerId: "probe-crash-" + Environment.MachineName);
        var state = store.LoadOrImport();
        var project = state.Notes.First(n => n.IsBoard).Projects[0];
        project.NameRich = RichDocument.Plain("UNCOMMITTED-CRASH-" + Guid.NewGuid().ToString("N"));
        store.SaveIncremental(state, new HashSet<Guid> { project.Id });
        return 0;
    }

    private static ProbeReport NewReport(string role, string root, string session) => new()
    {
        Schema = 1,
        Role = role,
        SessionId = session,
        Machine = Environment.MachineName,
        User = Environment.UserName,
        Os = Environment.OSVersion.VersionString,
        Runtime = Environment.Version.ToString(),
        SharedRoot = root,
        SourceStamp = ReadSourceStamp(),
        StartedUtc = DateTimeOffset.UtcNow
    };

    private static ProbeCase Pass(string id, string details) => new(id, "PASS", details, DateTimeOffset.UtcNow);

    private static NodeReady NodeInfo(string role) => new(
        role,
        Environment.MachineName,
        Environment.UserName,
        Environment.OSVersion.VersionString,
        Environment.Version.ToString(),
        ReadSourceStamp(),
        DateTimeOffset.UtcNow);

    private static string ReadSourceStamp()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "SOURCE.txt");
        if (!File.Exists(path)) return "unpackaged/local-build";
        var value = File.ReadAllText(path).Trim();
        return value.Length <= 4096 ? value : value[..4096];
    }

    private static string SessionRoot(string sharedRoot, string sessionId)
        => Path.Combine(sharedRoot, ".h2-nas-acceptance", SafeId(sessionId));

    private static string SafeId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("Session ID may contain only letters, digits, '-' and '_'.");
        return value;
    }

    private static async Task WaitFile(string path)
    {
        var deadline = DateTime.UtcNow + DefaultTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path)) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Timed out waiting for shared marker: " + path);
    }

    private static void WriteDurable(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(true);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    private static T ReadJson<T>(string path)
        => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
           ?? throw new InvalidDataException("Invalid probe JSON: " + path);

    private static Process StartSelf(params string[] args)
    {
        var arg0 = Path.GetFullPath(Environment.GetCommandLineArgs()[0]);
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate current process.");
        var info = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };

        if (Path.GetExtension(arg0).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (string.IsNullOrWhiteSpace(host))
                host = Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                    ? processPath
                    : "dotnet";
            info.FileName = host;
            info.ArgumentList.Add(arg0);
        }
        else
        {
            // Published/self-contained and normal apphost builds relaunch the executable directly.
            info.FileName = processPath;
        }

        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException("Could not start probe child process.");
    }

    private sealed class ProbeReport
    {
        public int Schema { get; set; }
        public string Role { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string Machine { get; set; } = "";
        public string User { get; set; } = "";
        public string Os { get; set; } = "";
        public string Runtime { get; set; } = "";
        public string SharedRoot { get; set; } = "";
        public string SourceStamp { get; set; } = "";
        public string? PeerMachine { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset? CompletedUtc { get; set; }
        public string Overall { get; set; } = "RUNNING";
        public List<ProbeCase> Cases { get; set; } = [];
    }

    private sealed record NodeReady(string Role, string Machine, string User, string Os, string Runtime, string SourceStamp, DateTimeOffset Utc);
    private sealed record ProbeCase(string CaseId, string Status, string Details, DateTimeOffset Utc);
    private sealed record PeerResult(bool Success, DateTimeOffset Utc, string Machine);
    private sealed record PayloadMarker(string Hash, long Length, DateTimeOffset PublishedUtc);
    private sealed record PayloadObservation(string Hash, long Length, long DelayMs, string Machine, DateTimeOffset Utc);
    private sealed record Intent(Guid ProjectId, string Text);
    private sealed record CrashExpected(Guid ProjectId, string Name);
}

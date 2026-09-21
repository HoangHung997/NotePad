using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using H2Notes.Core;

internal static class Program
{
    private const string CommitLeaseProtocol = "byte-range-file-lock-v1";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);
    private static TimeSpan _waitTimeout = DefaultTimeout;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--info")
                return PrintInfo();
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

            Show exact probe/build identity:
              H2Notes.NasAcceptance --info

            CI/local harness self-test:
              H2Notes.NasAcceptance --self-test <output-dir>

            Both PCs must point to the same physical NAS folder. The probe writes only below:
              <shared-root>/.h2-nas-acceptance/<session-id>/
            """);
        return 2;
    }

    private static int PrintInfo()
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ProbeSchema = 3,
            CommitLeaseProtocol,
            CommitLockFile = WorkspaceCommitLease.FileName,
            SourceStamp = ReadSourceStamp()
        }, Json));
        return 0;
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

        // CI validates the protocol state machine locally without depending on how dotnet/apphost
        // relaunches a second copy. Real acceptance still requires two separately launched physical PCs.
        var peerTask = RunPeer(root, session, peerOut);
        var coordinatorTask = RunCoordinator(root, session, coordinatorOut);
        var codes = await Task.WhenAll(coordinatorTask, peerTask);
        if (codes.Any(code => code != 0))
            throw new InvalidOperationException($"Self-test failed: coordinator={codes[0]}, peer={codes[1]}.");

        var marker = Path.Combine(output, "self-test-result.txt");
        File.WriteAllText(marker, "PASS H2 NAS acceptance harness self-test" + Environment.NewLine);
        Console.WriteLine("PASS H2 NAS acceptance harness self-test");
        try { Directory.Delete(root, true); } catch { }
        return 0;
    }

    private static async Task<int> RunCoordinator(string sharedRoot, string sessionId, string outputDir)
    {
        _waitTimeout = sessionId.StartsWith("selftest-", StringComparison.Ordinal) ? TimeSpan.FromSeconds(30) : DefaultTimeout;
        Directory.CreateDirectory(outputDir);
        var session = SessionRoot(sharedRoot, sessionId);
        Directory.CreateDirectory(session);
        var evidence = NewReport("coordinator", sharedRoot, sessionId);
        WriteJson(Path.Combine(session, "coordinator.ready.json"), NodeInfo("coordinator"));

        await WaitFile(Path.Combine(session, "peer.ready.json"));
        var peerReady = ReadJson<NodeReady>(Path.Combine(session, "peer.ready.json"));
        evidence.PeerMachine = peerReady.Machine;
        evidence.PeerNodeFingerprint = peerReady.NodeFingerprint;
        evidence.PeerSourceStamp = peerReady.SourceStamp;
        ValidatePeerBundle("Peer", peerReady);
        if (!sessionId.StartsWith("selftest-", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(peerReady.NodeFingerprint))
                throw new InvalidOperationException("Peer is using an older NAS acceptance probe without a node fingerprint. Use the same updated bundle on both PCs and a new Session ID.");
            if (string.Equals(peerReady.NodeFingerprint, NodeFingerprint(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Real NAS acceptance requires two distinct Windows node fingerprints. Two PCs may have the same computer name, but the probe must not be run twice on the same Windows installation.");
        }
        evidence.Cases.Add(Pass("TWO-PC-RENDEZVOUS",
            sessionId.StartsWith("selftest-", StringComparison.Ordinal)
                ? "Local harness self-test rendezvous completed."
                : $"Two distinct Windows node fingerprints observed the same acceptance session; computer names may match ({Environment.MachineName} <-> {peerReady.Machine})."));

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
        _waitTimeout = sessionId.StartsWith("selftest-", StringComparison.Ordinal) ? TimeSpan.FromSeconds(30) : DefaultTimeout;
        Directory.CreateDirectory(outputDir);
        var session = SessionRoot(sharedRoot, sessionId);
        Directory.CreateDirectory(session);
        var evidence = NewReport("peer", sharedRoot, sessionId);
        await WaitFile(Path.Combine(session, "coordinator.ready.json"));
        var coordinatorReady = ReadJson<NodeReady>(Path.Combine(session, "coordinator.ready.json"));
        evidence.PeerMachine = coordinatorReady.Machine;
        evidence.PeerNodeFingerprint = coordinatorReady.NodeFingerprint;
        evidence.PeerSourceStamp = coordinatorReady.SourceStamp;
        ValidatePeerBundle("Coordinator", coordinatorReady);
        if (!sessionId.StartsWith("selftest-", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(coordinatorReady.NodeFingerprint))
                throw new InvalidOperationException("Coordinator is using an older NAS acceptance probe without a node fingerprint. Use the same updated bundle on both PCs and a new Session ID.");
            if (string.Equals(coordinatorReady.NodeFingerprint, NodeFingerprint(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Real NAS acceptance requires two distinct Windows node fingerprints. Two PCs may have the same computer name, but the probe must not be run twice on the same Windows installation.");
        }
        WriteJson(Path.Combine(session, "peer.ready.json"), NodeInfo("peer"));
        evidence.Cases.Add(Pass("TWO-PC-RENDEZVOUS",
            sessionId.StartsWith("selftest-", StringComparison.Ordinal)
                ? "Local harness self-test rendezvous completed."
                : $"Peer observed coordinator with a distinct Windows node fingerprint; computer names may match ({coordinatorReady.Machine})."));

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
        using (var held = WorkspaceCommitLease.Acquire(session, "probe-coordinator", TimeSpan.FromSeconds(2)))
        {
            File.WriteAllText(Path.Combine(session, "lock.held"), DateTimeOffset.UtcNow.ToString("O"));
            await WaitFile(Path.Combine(session, "lock.peer-blocked.json"));
            var result = ReadJson<PeerResult>(Path.Combine(session, "lock.peer-blocked.json"));
            if (!result.Success) throw new InvalidOperationException("Peer acquired the byte-range commit lock while coordinator still held it.");
        }

        File.WriteAllText(Path.Combine(session, "lock.released"), DateTimeOffset.UtcNow.ToString("O"));
        await WaitFile(Path.Combine(session, "lock.peer-after-release.json"));
        var after = ReadJson<PeerResult>(Path.Combine(session, "lock.peer-after-release.json"));
        if (!after.Success) throw new InvalidOperationException("Peer could not acquire the byte-range commit lock after release.");
        evidence.Cases.Add(Pass("EXCLUSIVE-LOCK", "SMB byte-range commit lock excluded the peer and acquisition succeeded after release."));
    }

    private static async Task PeerLockCase(string session, ProbeReport evidence)
    {
        await WaitFile(Path.Combine(session, "lock.held"));
        var blocked = false;
        try
        {
            using var unexpected = WorkspaceCommitLease.Acquire(
                session, "probe-peer-blocked-check", TimeSpan.FromMilliseconds(500));
        }
        catch (IOException) { blocked = true; }
        WriteJson(Path.Combine(session, "lock.peer-blocked.json"), new PeerResult(blocked, DateTimeOffset.UtcNow, Environment.MachineName));
        if (!blocked) throw new InvalidOperationException("Byte-range commit lock was not enforced across nodes.");

        await WaitFile(Path.Combine(session, "lock.released"));
        var acquired = false;
        try
        {
            using var stream = WorkspaceCommitLease.Acquire(session, "probe-peer-after-release", _waitTimeout);
            acquired = true;
        }
        catch (IOException) { acquired = false; }
        WriteJson(Path.Combine(session, "lock.peer-after-release.json"), new PeerResult(acquired, DateTimeOffset.UtcNow, Environment.MachineName));
        if (!acquired) throw new IOException("Could not acquire released byte-range commit lock.");
        evidence.Cases.Add(Pass("EXCLUSIVE-LOCK", "Peer was blocked by byte-range locking while held and acquired after release."));
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
        var deadline = DateTime.UtcNow + _waitTimeout;
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
        Schema = 3,
        Role = role,
        SessionId = session,
        Machine = Environment.MachineName,
        NodeFingerprint = NodeFingerprint(),
        User = Environment.UserName,
        Os = Environment.OSVersion.VersionString,
        Runtime = Environment.Version.ToString(),
        SharedRoot = root,
        CommitLeaseProtocol = CommitLeaseProtocol,
        SourceStamp = ReadSourceStamp(),
        StartedUtc = DateTimeOffset.UtcNow
    };

    private static ProbeCase Pass(string id, string details) => new(id, "PASS", details, DateTimeOffset.UtcNow);

    private static void ValidatePeerBundle(string peerRole, NodeReady peer)
    {
        if (!string.Equals(peer.CommitLeaseProtocol, CommitLeaseProtocol, StringComparison.Ordinal))
        {
            var remote = string.IsNullOrWhiteSpace(peer.CommitLeaseProtocol) ? "<missing/legacy>" : peer.CommitLeaseProtocol;
            throw new InvalidOperationException(
                $"{peerRole} is using an incompatible NAS acceptance lock protocol. " +
                $"Local={CommitLeaseProtocol}, remote={remote}. Download the same current probe bundle on both PCs and use a new Session ID.");
        }

        var localSource = ReadSourceStamp();
        if (!string.Equals(peer.SourceStamp, localSource, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{peerRole} is not using the same NAS acceptance build as this PC. " +
                "Download/copy one current H2Notes-NasAcceptance-win-x64 artifact to both PCs and use a new Session ID.");
        }
    }

    private static string NodeFingerprint()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Real NAS acceptance node fingerprints are supported on Windows only.");

        object? raw = null;
        try
        {
            raw = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
                "MachineGuid",
                null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new InvalidOperationException("Cannot read the Windows machine identity required for real two-PC acceptance.", ex);
        }

        var machineGuid = raw?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(machineGuid))
            throw new InvalidOperationException("Windows MachineGuid is unavailable; the NAS probe cannot safely distinguish two physical Windows installations.");

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("h2-nas-node-v1|" + machineGuid.ToUpperInvariant()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static NodeReady NodeInfo(string role) => new(
        role,
        Environment.MachineName,
        NodeFingerprint(),
        Environment.UserName,
        Environment.OSVersion.VersionString,
        Environment.Version.ToString(),
        CommitLeaseProtocol,
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
        var deadline = DateTime.UtcNow + _waitTimeout;
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
        public string NodeFingerprint { get; set; } = "";
        public string User { get; set; } = "";
        public string Os { get; set; } = "";
        public string Runtime { get; set; } = "";
        public string SharedRoot { get; set; } = "";
        public string CommitLeaseProtocol { get; set; } = "";
        public string SourceStamp { get; set; } = "";
        public string? PeerMachine { get; set; }
        public string? PeerNodeFingerprint { get; set; }
        public string? PeerSourceStamp { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset? CompletedUtc { get; set; }
        public string Overall { get; set; } = "RUNNING";
        public List<ProbeCase> Cases { get; set; } = [];
    }

    private sealed record NodeReady(
        string Role,
        string Machine,
        string NodeFingerprint,
        string User,
        string Os,
        string Runtime,
        string? CommitLeaseProtocol,
        string SourceStamp,
        DateTimeOffset Utc);
    private sealed record ProbeCase(string CaseId, string Status, string Details, DateTimeOffset Utc);
    private sealed record PeerResult(bool Success, DateTimeOffset Utc, string Machine);
    private sealed record PayloadMarker(string Hash, long Length, DateTimeOffset PublishedUtc);
    private sealed record PayloadObservation(string Hash, long Length, long DelayMs, string Machine, DateTimeOffset Utc);
    private sealed record Intent(Guid ProjectId, string Text);
    private sealed record CrashExpected(Guid ProjectId, string Name);
}

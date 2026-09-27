using System.Runtime.CompilerServices;
using H2AgentLab.Integration;
using H2AgentLab.Metrics;
using H2AgentLab.Transport;
using H2Notes.Core;

/// <summary>AR-031: production cancellation with a disposable real local journal.
/// Transport is explicitly scripted and performs no network/model/Office operation.</summary>
internal static class H2AgentArchiveCancellationTests
{
    public static void Run(Action<string, Action> test)
    {
        test("AR-031 cancellation healthy archive records and stops the active transport", () =>
            InFixture((root, adapter, wire) =>
            {
                var id = Start(adapter, root, wire);
                adapter.CancelTask(id);
                Await(wire.CancellationObserved.Task);
                Drain(adapter);
                var observation = adapter.ObserveTask(id);
                Check(observation.Summary.Status == H2AgentTaskStatus.Cancelled,
                    "Healthy cancellation did not reach the cancelled state.");
                Check(observation.Progress.Count(p => p.Code == "cancel-requested") == 1,
                    "Healthy cancellation did not retain exactly one durable request.");
                Check(wire.Starts == 1 && wire.Continuations == 0, "Cancellation launched more model work.");
            }));

        test("AR-031 cancellation journal write failure cannot leave transport running", () =>
            InFixture((root, adapter, wire) =>
            {
                var id = Start(adapter, root, wire);
                var originals = JournalBytes(root);
                var blockedPath = BlockNextAppend(root, adapter);
                _ = StorageFailure(() => adapter.CancelTask(id));
                Await(wire.CancellationObserved.Task);
                Drain(adapter);
                AssertRecovery(adapter, id, wire);
                Check(!adapter.ObserveTask(id).Progress.Any(p => p.Code == "cancel-requested"),
                    "Failed journal write was presented as a durable cancel receipt.");
                Check(Directory.Exists(blockedPath), "Cancellation removed a conflicting journal object.");
                AssertUnchanged(originals);
            }));

        test("AR-031 cancellation still stops execution already marked Blocked by archive failure", () =>
            InFixture((root, adapter, wire) =>
            {
                var id = Start(adapter, root, wire);
                var originals = JournalBytes(root);
                _ = BlockNextAppend(root, adapter);
                _ = StorageFailure(() => adapter.SupplementTask(id, Guid.NewGuid(), "Include a greeting"));
                Check(adapter.GetTaskSummary(id).Status == H2AgentTaskStatus.Blocked,
                    "Fixture did not enter the persistence-blocked state.");
                adapter.CancelTask(id);
                Await(wire.CancellationObserved.Task);
                Drain(adapter);
                AssertRecovery(adapter, id, wire);
                AssertUnchanged(originals);
            }));

        test("AR-031 cancellation callback error does not replace the primary storage error", () =>
            InFixture((root, adapter, wire) =>
            {
                var id = Start(adapter, root, wire);
                _ = BlockNextAppend(root, adapter);
                var error = StorageFailure(() => adapter.CancelTask(id));
                Await(wire.CancellationObserved.Task);
                Drain(adapter);
                Check(error.Data["H2.CancellationFailureType"] is string,
                    "Cancellation callback failure was lost instead of retaining the original storage exception.");
                AssertRecovery(adapter, id, wire);
            }, throwOnCancellation: true));
    }

    private static Guid Start(H2ProductionAgentAdapter adapter, string root, HoldingWire wire)
    {
        var id = adapter.StartTaskAsync(null, "Hello", new H2AgentTaskContext(root, "Dedicated read-only fixture"),
            readOnly: true).GetAwaiter().GetResult();
        Await(wire.Entered.Task);
        return id;
    }

    private static string BlockNextAppend(string root, H2ProductionAgentAdapter adapter)
    {
        var status = adapter.GetArchiveStatus();
        Check(status.CanWrite, "Archive must be writable before the deliberate fixture fault.");
        var journal = Path.Combine(root, "integration", "journal-v2");
        var path = Path.Combine(journal, $"event-{status.LastSequence + 1:D12}.json");
        Check(!File.Exists(path) && !Directory.Exists(path), "Next event already exists; fault is not isolated.");
        // This directory is created only under the unique test root. Atomic create/move of
        // the next event must fail; no personal document or previously acknowledged event is edited.
        Directory.CreateDirectory(path);
        return path;
    }

    private static Dictionary<string, byte[]> JournalBytes(string root) =>
        Directory.GetFiles(Path.Combine(root, "integration", "journal-v2"), "event-*.json")
            .ToDictionary(path => path, File.ReadAllBytes);

    private static void AssertUnchanged(Dictionary<string, byte[]> originals)
    {
        foreach (var item in originals)
            Check(File.ReadAllBytes(item.Key).SequenceEqual(item.Value), "Cancellation rewrote an acknowledged journal event.");
    }

    private static Exception StorageFailure(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex; }
        throw new InvalidOperationException("The isolated journal fault did not preserve a storage exception.");
    }

    private static void AssertRecovery(H2ProductionAgentAdapter adapter, Guid id, HoldingWire wire)
    {
        var state = adapter.GetTaskSummary(id);
        Check(state.Status == H2AgentTaskStatus.Blocked && !string.IsNullOrWhiteSpace(state.Error),
            "An unsaved terminal state was presented as successful cancellation or completion.");
        Check(!adapter.GetArchiveStatus().CanWrite && adapter.GetArchiveStatus().State == "RecoveryRequired"
            && wire.Starts == 1 && wire.Continuations == 0,
            "Archive failure was hidden or cancellation started additional model work.");
    }

    private static void Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
    private static void Drain(H2ProductionAgentAdapter adapter) => Await(adapter.DisposeAsync().AsTask());
    private static void Check(bool ok, string message)
    { if (!ok) throw new InvalidOperationException(message); }

    private static void InFixture(Action<string, H2ProductionAgentAdapter, HoldingWire> body, bool throwOnCancellation = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "h2-ar031-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var wire = new HoldingWire(throwOnCancellation);
        H2ProductionAgentAdapter? adapter = null;
        Exception? primary = null;
        var drained = false;
        try
        {
            adapter = new(root, () => new(new AiProfile
            {
                Model = "scripted-no-network", Protocol = AiProtocol.OpenAiChat, BaseUrl = "https://example.test/v1"
            }, ""), transportFactory: wire);
            body(root, adapter, wire);
        }
        catch (Exception ex) { primary = ex; throw; }
        finally
        {
            // The failing-before-fix case must still stop its own held task. Never delete
            // a workspace while its provider teardown is running or mask the primary assertion.
            try { if (adapter is not null) Drain(adapter); drained = true; }
            catch (Exception ex) when (primary is not null) { primary.Data["fixture-shutdown"] = ex.GetType().Name; }
            finally
            {
                if (drained)
                {
                    try { Directory.Delete(root, recursive: true); }
                    catch (Exception ex) when (primary is not null) { primary.Data["fixture-cleanup"] = ex.GetType().Name; }
                }
            }
        }
    }

    private sealed class HoldingWire(bool throwOnCancellation) : IAgentTransport, IAgentTransportFactory
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Starts;
        public int Continuations;
        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.ChatCompletionsFallback;
        public IAgentTransport Create(AiProfile profile, string key, AgentRunTelemetry telemetry) => this;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Starts);
            using var registration = throwOnCancellation
                ? cancellationToken.Register(() => throw new InvalidOperationException("Controlled cancellation callback failure"))
                : default;
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false); }
            finally { if (cancellationToken.IsCancellationRequested) CancellationObserved.TrySetResult(); }
            yield return AgentTransportEvent.Complete();
        }

        public IAsyncEnumerable<AgentTransportEvent> ContinueAsync(AgentTransportContinuationRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Continuations);
            throw new InvalidOperationException("Unexpected continuation in a held read-only fixture.");
        }
        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

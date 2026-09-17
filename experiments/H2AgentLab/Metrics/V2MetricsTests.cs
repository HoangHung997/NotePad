using System.Net;
using System.Text.Json;
using H2Notes.Core;

namespace H2AgentLab.Metrics;

public static class V2MetricsTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root)) throw new IOException("Use a new v2 metrics test directory.");
        Directory.CreateDirectory(root);
        var lines = new List<string>();
        var failed = 0;

        async Task Test(string name, Func<Task> action)
        {
            try
            {
                await action();
                lines.Add("PASS " + name);
            }
            catch (Exception ex)
            {
                failed++;
                lines.Add("FAIL " + name + ": " + ex.Message);
            }
        }

        static void Check(bool value, string message)
        {
            if (!value) throw new Exception(message);
        }

        await Test("Trace sequence and monotonic elapsed time never go backwards", async () =>
        {
            var trace = new AgentTrace(Guid.NewGuid(), Guid.NewGuid());
            trace.Mark(AgentTraceKind.Send, "send");
            await Task.Delay(5);
            trace.Mark(AgentTraceKind.ContextReady, "context");
            await Task.Delay(5);
            trace.Mark(AgentTraceKind.RequestStart, "request");
            var events = trace.Snapshot();
            Check(events.Select(e => e.Sequence).SequenceEqual(new long[] { 1, 2, 3 }), "Trace sequence is not append-only.");
            Check(events.Zip(events.Skip(1)).All(pair => pair.First.ElapsedMilliseconds <= pair.Second.ElapsedMilliseconds), "Monotonic elapsed time went backwards.");
        });

        await Test("Metrics aggregate public usage, traffic and counts", () =>
        {
            var trace = new AgentTrace();
            trace.Mark(AgentTraceKind.RequestStart, "r1");
            trace.Mark(AgentTraceKind.FirstModelEvent, "first");
            var metrics = new AgentMetrics();
            metrics.AddUsage(inputTokens: 100, cachedInputTokens: 60, cacheWriteInputTokens: 10, outputTokens: 25, reasoningTokens: 5);
            metrics.AddUsage(inputTokens: 20, outputTokens: 10);
            metrics.AddTraffic(1500, 2500);
            metrics.IncrementModelCalls(2);
            metrics.IncrementToolCalls(3);
            metrics.IncrementRepairs();
            metrics.SetEstimatedCostUsd(0.0123m);
            var snapshot = metrics.Snapshot(trace);
            Check(snapshot.InputTokens == 120 && snapshot.CachedInputTokens == 60 && snapshot.CacheWriteInputTokens == 10, "Input/cache usage aggregation failed.");
            Check(snapshot.OutputTokens == 35 && snapshot.ReasoningTokens == 5, "Output usage aggregation failed.");
            Check(snapshot.BytesSent == 1500 && snapshot.BytesReceived == 2500, "Traffic aggregation failed.");
            Check(snapshot.ModelCalls == 2 && snapshot.ToolCalls == 3 && snapshot.RepairCount == 1, "Call counters failed.");
            Check(snapshot.EstimatedCostUsd == 0.0123m, "Cost field failed.");
            Check(snapshot.FirstModelEventMilliseconds is >= 0, "First-model-event latency missing.");
            return Task.CompletedTask;
        });

        await Test("Persisted turn telemetry omits detail and redacts credential-shaped labels", () =>
        {
            var state = Path.Combine(root, "persist");
            var trace = new AgentTrace();
            trace.Mark(AgentTraceKind.Send, "Bearer SUPERSECRET123456789", "detail contains sk-should-never-persist-123456789");
            trace.Mark(AgentTraceKind.RequestStart, "sk-testsecret123456789");
            var metrics = new AgentMetrics();
            metrics.IncrementModelCalls();
            var path = AgentTraceStore.Save(state, trace, metrics);
            var json = File.ReadAllText(path);
            Check(!json.Contains("SUPERSECRET123456789", StringComparison.Ordinal), "Bearer secret leaked into telemetry.");
            Check(!json.Contains("sk-testsecret123456789", StringComparison.Ordinal), "API-key-shaped label leaked into telemetry.");
            Check(!json.Contains("should-never-persist", StringComparison.Ordinal), "Trace detail was persisted.");
            using var parsed = JsonDocument.Parse(json);
            var events = parsed.RootElement.GetProperty("events");
            Check(events.GetArrayLength() == 2, "Persisted event count mismatch.");
            Check(!events[0].TryGetProperty("detail", out _), "Persisted trace unexpectedly contains detail field.");
            Check(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "Atomic trace temp file was left behind.");
            return Task.CompletedTask;
        });

        await Test("V2 trace persists policy safety and toolset versions for reproducibility", () =>
        {
            var state = Path.Combine(root, "versioned-trace");
            var versions = AgentVersions.Current;
            var telemetry = new AgentRunTelemetry(Guid.NewGuid(), Guid.NewGuid(), versions);
            telemetry.Trace.Mark(AgentTraceKind.ContextReady, "v2-context");
            var path = AgentTraceStore.Save(state, telemetry.Trace, telemetry.Metrics);

            using var parsed = JsonDocument.Parse(File.ReadAllText(path));
            var document = parsed.RootElement;
            Check(document.GetProperty("schemaVersion").GetInt32() == AgentTrace.SchemaVersion,
                "Persisted trace schema version did not advance with version metadata.");
            var persisted = document.GetProperty("versions");
            Check(persisted.GetProperty("agentPolicyVersion").GetString() == versions.AgentPolicyVersion,
                "Agent policy version was not persisted.");
            Check(persisted.GetProperty("safetyPolicyVersion").GetString() == versions.SafetyPolicyVersion,
                "Safety policy version was not persisted.");
            Check(persisted.GetProperty("toolsetVersion").GetString() == versions.ToolsetVersion,
                "Toolset version was not persisted.");
            Check(!File.ReadAllText(path).Contains("BASE AGENT POLICY", StringComparison.Ordinal),
                "Versioned trace unexpectedly stored prompt policy text.");
            return Task.CompletedTask;
        });

        await Test("AgentRunner records cancellation without retrying", async () =>
        {
            var workspace = Path.Combine(root, "cancel-workspace");
            Directory.CreateDirectory(workspace);
            var tools = new AgentTools(new SafeWorkspace(workspace), Path.Combine(root, "cancel-state"), (_, _) => Task.FromResult(true), (_, _) => { });
            var telemetry = new AgentRunTelemetry();
            using var cancel = new CancellationTokenSource(50);
            using var runner = new AgentRunner(new NeverHandler());
            try
            {
                await runner.Run(new AiProfile { Model = "fixture" }, "", new LabSession(), tools, "hello", (_, _) => { }, () => { }, telemetry, cancel.Token);
                throw new Exception("Run unexpectedly completed.");
            }
            catch (OperationCanceledException) { }
            Check(telemetry.Trace.Last(AgentTraceKind.Cancel) is not null, "Cancellation event missing.");
            Check(telemetry.Metrics.Snapshot(telemetry.Trace).ModelCalls == 1, "Cancelled request was unexpectedly retried.");
        });

        await Test("AgentRunner records provider errors without storing error text in persisted trace", async () =>
        {
            var workspace = Path.Combine(root, "error-workspace");
            Directory.CreateDirectory(workspace);
            var tools = new AgentTools(new SafeWorkspace(workspace), Path.Combine(root, "error-state"), (_, _) => Task.FromResult(true), (_, _) => { });
            var telemetry = new AgentRunTelemetry();
            using var runner = new AgentRunner(new ErrorHandler());
            try
            {
                await runner.Run(new AiProfile { Model = "fixture" }, "", new LabSession(), tools, "hello", (_, _) => { }, () => { }, telemetry, default);
                throw new Exception("Error response unexpectedly completed.");
            }
            catch (IOException) { }
            Check(telemetry.Trace.Last(AgentTraceKind.Error) is not null, "Error trace event missing.");
            var path = AgentTraceStore.Save(Path.Combine(root, "error-trace"), telemetry.Trace, telemetry.Metrics);
            var json = File.ReadAllText(path);
            Check(!json.Contains("provider fixture secret body", StringComparison.OrdinalIgnoreCase), "Provider error body leaked into persisted turn telemetry.");
        });

        await Test("Trace safety limit rejects unbounded telemetry", () =>
        {
            var trace = new AgentTrace();
            for (var i = 0; i < AgentTrace.MaxEvents; i++) trace.Mark(AgentTraceKind.ToolStart, "t");
            var blocked = false;
            try { trace.Mark(AgentTraceKind.ToolStart, "overflow"); }
            catch (InvalidOperationException) { blocked = true; }
            Check(blocked, "Trace accepted events beyond the safety limit.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        await File.WriteAllLinesAsync(Path.Combine(root, "v2-metrics-tests.txt"), lines);
        Console.WriteLine(string.Join("\n", lines));
        return failed == 0 ? 0 : 1;
    }

    private sealed class NeverHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class ErrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("provider fixture secret body")
            });
    }
}

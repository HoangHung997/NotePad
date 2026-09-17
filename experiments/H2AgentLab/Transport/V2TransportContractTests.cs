using System.Runtime.CompilerServices;
using System.Text.Json;
using H2AgentLab.Transport;

namespace H2AgentLab;

public static class V2TransportContractTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
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

        await Test("Transport contract streams typed start and continuation events", async () =>
        {
            await using var transport = new FakeTransport();
            using var schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}");
            var taskId = Guid.NewGuid();
            var turnId = Guid.NewGuid();
            var start = new AgentTransportStartRequest(
                taskId,
                turnId,
                [new(AgentTransportMessageRole.User, "hello")],
                [new("read_file", "Read a file", schema.RootElement.Clone())]);

            var first = await Collect(transport.StartAsync(start));
            if (first.Select(e => e.Kind).SequenceEqual([
                    AgentTransportEventKind.ResponseStarted,
                    AgentTransportEventKind.TextDelta,
                    AgentTransportEventKind.ToolCall,
                    AgentTransportEventKind.Usage,
                    AgentTransportEventKind.Completed]) == false)
                throw new InvalidOperationException("Unexpected typed start event sequence.");
            if (first.Single(e => e.Kind == AgentTransportEventKind.ToolCall).ToolCall?.Name != "read_file")
                throw new InvalidOperationException("Typed tool call was not preserved.");

            var continuation = new AgentTransportContinuationRequest(
                taskId,
                turnId,
                [new("call_1", "read_file", "{\"ok\":true}")]);
            var second = await Collect(transport.ContinueAsync(continuation));
            if (second.Count != 2 || second[0].Kind != AgentTransportEventKind.TextDelta || second[1].Kind != AgentTransportEventKind.Completed)
                throw new InvalidOperationException("Unexpected continuation event sequence.");
            if (transport.StartCount != 1 || transport.ContinueCount != 1)
                throw new InvalidOperationException("Transport start/continue call counts are wrong.");
        });

        await Test("Capabilities are explicit and independently addressable", () =>
        {
            var caps = new AgentTransportCapabilities(
                NativeTools: true,
                IncrementalContinuation: true,
                WebSocket: true,
                ProviderCompaction: false,
                PromptCacheControl: true,
                ParallelToolCalls: true,
                NativeImageInput: true,
                NativeFileInput: false,
                UsageMetrics: true);
            if (!caps.NativeTools || !caps.IncrementalContinuation || !caps.WebSocket || caps.ProviderCompaction
                || !caps.PromptCacheControl || !caps.ParallelToolCalls || !caps.NativeImageInput
                || caps.NativeFileInput || !caps.UsageMetrics)
                throw new InvalidOperationException("Capability flags were not preserved independently.");
            if (AgentTransportCapabilities.Minimal.NativeTools || AgentTransportCapabilities.Minimal.WebSocket)
                throw new InvalidOperationException("Minimal capability profile must not advertise optional features.");
            return Task.CompletedTask;
        });

        await Test("Cancel and async dispose are part of the transport lifecycle", async () =>
        {
            var transport = new FakeTransport();
            transport.Cancel();
            if (!transport.Cancelled) throw new InvalidOperationException("Cancel did not reach the transport.");
            await transport.DisposeAsync();
            if (!transport.Disposed) throw new InvalidOperationException("DisposeAsync did not reach the transport.");
        });

        await Test("Requests and events stay provider-neutral", () =>
        {
            var forbidden = new[] { "HttpRequestMessage", "JsonObject", "HttpResponseMessage", "WebSocket" };
            var exposed = typeof(IAgentTransport).GetMethods()
                .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType))
                .Select(t => t.FullName ?? t.Name)
                .ToArray();
            foreach (var name in forbidden)
                if (exposed.Any(t => t.Contains(name, StringComparison.Ordinal)))
                    throw new InvalidOperationException("Provider/network type leaked through IAgentTransport: " + name);
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(root, "v2-transport-contract-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join("\n", lines));
        return failed == 0 ? 0 : 1;
    }

    private static async Task<List<AgentTransportEvent>> Collect(IAsyncEnumerable<AgentTransportEvent> stream)
    {
        var result = new List<AgentTransportEvent>();
        await foreach (var item in stream) result.Add(item);
        return result;
    }

    private sealed class FakeTransport : IAgentTransport
    {
        public AgentTransportCapabilities Capabilities { get; } = new(
            NativeTools: true,
            IncrementalContinuation: true,
            WebSocket: false,
            ProviderCompaction: false,
            PromptCacheControl: false,
            ParallelToolCalls: true,
            NativeImageInput: true,
            NativeFileInput: true,
            UsageMetrics: true);

        public int StartCount { get; private set; }
        public int ContinueCount { get; private set; }
        public bool Cancelled { get; private set; }
        public bool Disposed { get; private set; }

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StartCount++;
            cancellationToken.ThrowIfCancellationRequested();
            yield return AgentTransportEvent.Started("resp_1");
            await Task.Yield();
            yield return AgentTransportEvent.Text("working");
            yield return AgentTransportEvent.Tool(new("call_1", "read_file", "{\"path\":\"brief.md\"}"));
            yield return AgentTransportEvent.Meter(new(InputTokens: 100, CachedInputTokens: 60, OutputTokens: 12, TotalTokens: 112));
            yield return AgentTransportEvent.Complete("resp_1", "tool_calls");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ContinueCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (request.ToolResults.Count != 1 || request.ToolResults[0].ToolCallId != "call_1")
                throw new InvalidOperationException("Continuation did not preserve typed tool result identity.");
            yield return AgentTransportEvent.Text("done");
            await Task.Yield();
            yield return AgentTransportEvent.Complete("resp_2", "stop");
        }

        public void Cancel() => Cancelled = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

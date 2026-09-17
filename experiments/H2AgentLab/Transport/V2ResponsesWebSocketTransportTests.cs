using System.Text.Json;
using H2Notes.Core;

namespace H2AgentLab.Transport;

public static class V2ResponsesWebSocketTransportTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var lines = new List<string>();
        var failed = 0;

        async Task Test(string name, Func<Task> action)
        {
            try { await action(); lines.Add("PASS " + name); }
            catch (Exception ex) { failed++; lines.Add("FAIL " + name + ": " + ex.Message); }
        }
        static void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }

        await Test("WebSocket reuses one turn connection and continues with only tool output", async () =>
        {
            var fake = new FakeConnection([
                [
                    Event(new { type = "response.created", response = new { id = "resp_1" } }),
                    Event(new { type = "response.output_item.done", item = new { type = "function_call", call_id = "call_1", name = "lookup", arguments = "{\"q\":\"abc\"}" } }),
                    Event(new { type = "response.completed", response = new { id = "resp_1", output = Array.Empty<object>(), usage = new { input_tokens = 12, output_tokens = 3, total_tokens = 15, input_tokens_details = new { cached_tokens = 5 } } } })
                ],
                [
                    Event(new { type = "response.created", response = new { id = "resp_2" } }),
                    Event(new { type = "response.output_text.delta", delta = "done" }),
                    Event(new { type = "response.output_item.done", item = new { type = "message", role = "assistant", content = Array.Empty<object>() } }),
                    Event(new { type = "response.completed", response = new { id = "resp_2", output = Array.Empty<object>(), usage = new { input_tokens = 4, output_tokens = 2, total_tokens = 6 } } })
                ]
            ]);
            var profile = OfficialProfile();
            await using var transport = new OpenAiResponsesWebSocketTransport(profile, "test-key", () => fake, null);
            var task = Guid.NewGuid(); var turn = Guid.NewGuid();
            var tool = Tool("lookup");
            var start = await Collect(transport.StartAsync(new(task, turn,
                [new(AgentTransportMessageRole.System, "policy"), new(AgentTransportMessageRole.User, "question")], [tool], "cache-a", false)));
            var call = start.Single(x => x.Kind == AgentTransportEventKind.ToolCall).ToolCall!;
            Check(call.Id == "call_1" && call.Name == "lookup");
            Check(transport.Capabilities.WebSocket && transport.Capabilities.IncrementalContinuation);

            var next = await Collect(transport.ContinueAsync(new(task, turn,
                [new AgentToolResult("call_1", "lookup", "{\"value\":42}")])));
            Check(next.Any(x => x.Kind == AgentTransportEventKind.TextDelta && x.Text == "done"));
            Check(fake.ConnectCount == 1, "Expected exactly one WebSocket connection for one turn.");
            Check(fake.Sent.Count == 2, "Expected start + continuation on same socket.");

            using var first = JsonDocument.Parse(fake.Sent[0]);
            Check(first.RootElement.GetProperty("type").GetString() == "response.create");
            Check(!first.RootElement.TryGetProperty("previous_response_id", out _));
            Check(first.RootElement.GetProperty("input").GetArrayLength() == 2);
            Check(first.RootElement.GetProperty("store").ValueKind == JsonValueKind.False);
            using var second = JsonDocument.Parse(fake.Sent[1]);
            Check(second.RootElement.GetProperty("previous_response_id").GetString() == "resp_1");
            var input = second.RootElement.GetProperty("input");
            Check(input.GetArrayLength() == 1, "Continuation must not resend full transcript.");
            Check(input[0].GetProperty("type").GetString() == "function_call_output");
            Check(input[0].GetProperty("call_id").GetString() == "call_1");
        });

        await Test("Connection failure before first write safely falls back to HTTP abstraction", async () =>
        {
            var socket = new FakeConnection([], failConnect: true);
            var fallback = new FakeFallbackTransport();
            await using var transport = new OpenAiResponsesWebSocketTransport(OfficialProfile(), "key", () => socket, () => fallback);
            var events = await Collect(transport.StartAsync(new(Guid.NewGuid(), Guid.NewGuid(), [new(AgentTransportMessageRole.User, "hello")], [])));
            Check(fallback.StartCalls == 1 && socket.Sent.Count == 0);
            Check(events.Any(x => x.Kind == AgentTransportEventKind.TextDelta && x.Text == "fallback"));
            Check(!transport.Capabilities.WebSocket, "Capabilities should reflect active HTTP fallback.");
        });

        await Test("Failure after WebSocket write is ambiguous and never auto-replayed", async () =>
        {
            var socket = new FakeConnection([[]], failReceive: true);
            var fallback = new FakeFallbackTransport();
            await using var transport = new OpenAiResponsesWebSocketTransport(OfficialProfile(), "key", () => socket, () => fallback);
            try
            {
                _ = await Collect(transport.StartAsync(new(Guid.NewGuid(), Guid.NewGuid(), [new(AgentTransportMessageRole.User, "hello")], [])));
                throw new Exception("Ambiguous send was silently accepted.");
            }
            catch (IOException) { }
            Check(socket.Sent.Count == 1 && fallback.StartCalls == 0, "Request written to WebSocket must never be replayed automatically.");
        });

        await Test("WebSocket rejects compatible and unsafe endpoints before sending data", async () =>
        {
            foreach (var url in new[] { "https://example.test/v1", "https://api.openai.com.evil.test/v1", "http://api.openai.com/v1" })
            {
                var profile = OfficialProfile(); profile.BaseUrl = url;
                var blocked = false;
                try { await using var _ = new OpenAiResponsesWebSocketTransport(profile, "key", () => new FakeConnection([]), null); }
                catch (InvalidOperationException) { blocked = true; }
                Check(blocked, "Endpoint should be blocked: " + url);
            }
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(outputDirectory, "v2-responses-websocket-transport-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join("\n", lines));
        return failed == 0 ? 0 : 1;
    }

    private static AiProfile OfficialProfile() => new()
    {
        Protocol = AiProtocol.OpenAiResponses,
        BaseUrl = "https://api.openai.com/v1",
        Model = "gpt-fixture"
    };

    private static AgentToolDefinition Tool(string name)
    {
        using var json = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"q\":{\"type\":\"string\"}},\"required\":[\"q\"],\"additionalProperties\":false}");
        return new(name, "fixture tool", json.RootElement.Clone());
    }

    private static string Event(object value) => JsonSerializer.Serialize(value);

    private static async Task<List<AgentTransportEvent>> Collect(IAsyncEnumerable<AgentTransportEvent> stream)
    {
        var result = new List<AgentTransportEvent>();
        await foreach (var item in stream) result.Add(item);
        return result;
    }

    private sealed class FakeConnection(
        IReadOnlyList<IReadOnlyList<string>> responses,
        bool failConnect = false,
        bool failReceive = false) : IResponsesWebSocketConnection
    {
        public int ConnectCount;
        public readonly List<string> Sent = [];
        public bool IsOpen { get; private set; }

        public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); ConnectCount++;
            if (failConnect) throw new IOException("fixture connect failure");
            if (uri.ToString() != "wss://api.openai.com/v1/responses") throw new IOException("Unexpected URI: " + uri);
            if (!headers.TryGetValue("Authorization", out var auth) || !auth.StartsWith("Bearer ", StringComparison.Ordinal))
                throw new IOException("Missing bearer header");
            IsOpen = true; return Task.CompletedTask;
        }

        public Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsOpen) throw new IOException("not open");
            Sent.Add(text); return Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> ReceiveTextAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (failReceive) throw new IOException("fixture receive failure after send");
            var index = Sent.Count - 1;
            if (index < 0 || index >= responses.Count) throw new IOException("Missing fixture response");
            foreach (var item in responses[index]) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        }

        public Task CloseAsync(CancellationToken cancellationToken) { IsOpen = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
    }

    private sealed class FakeFallbackTransport : IAgentTransport
    {
        public int StartCalls;
        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.OpenAiResponsesHttp;
        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(AgentTransportStartRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StartCalls++; await Task.Yield();
            yield return AgentTransportEvent.Started("fallback-1");
            yield return AgentTransportEvent.TextDeltaEvent("fallback");
            yield return AgentTransportEvent.Complete("fallback-1", "stop");
        }
        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(AgentTransportContinuationRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield(); yield return AgentTransportEvent.Complete("fallback-2", "stop");
        }
        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

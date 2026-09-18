using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using H2Notes.Core;

namespace H2AgentLab.Transport;

/// <summary>
/// Cross-provider resilience gate. Provider-specific suites own successful wire-shape/continuation
/// coverage; this suite applies the same cancellation, malformed-stream and premature-disconnect
/// expectations to every transport so future refactors cannot silently make one provider less safe.
/// </summary>
public static class V2ProviderTransportResilienceTests
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
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

        foreach (var fixture in HttpFixtures())
        {
            await Test(fixture.Name + " cancellation is terminal and sends only one request", async () =>
            {
                var handler = new BlockingHandler();
                await using var transport = fixture.Create(handler);
                using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
                try
                {
                    _ = await Collect(transport.StartAsync(Start(), cancel.Token));
                    throw new InvalidOperationException("Cancelled request completed unexpectedly.");
                }
                catch (OperationCanceledException) { }
                Check(handler.Requests == 1, "Cancellation triggered an unexpected retry/replay.");
            });

            await Test(fixture.Name + " malformed provider JSON is rejected before completion", async () =>
            {
                var handler = new StaticHandler(fixture.MalformedBody, fixture.ContentType);
                await using var transport = fixture.Create(handler);
                var completed = false;
                var tools = 0;
                try
                {
                    await foreach (var item in transport.StartAsync(Start()))
                    {
                        completed |= item.Kind == AgentTransportEventKind.Completed;
                        if (item.Kind == AgentTransportEventKind.ToolCall) tools++;
                    }
                    throw new InvalidOperationException("Malformed provider stream was accepted.");
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException) { }
                Check(!completed && tools == 0, "Malformed stream exposed completion/tool execution.");
                Check(handler.Requests == 1, "Malformed stream triggered an automatic retry.");
            });

            await Test(fixture.Name + " premature disconnect is not reported complete or retried", async () =>
            {
                var handler = new StaticHandler(fixture.PartialBody, fixture.ContentType);
                await using var transport = fixture.Create(handler);
                var completed = false;
                try
                {
                    await foreach (var item in transport.StartAsync(Start()))
                        completed |= item.Kind == AgentTransportEventKind.Completed;
                    throw new InvalidOperationException("Premature disconnect was accepted.");
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { }
                Check(!completed, "Premature disconnect surfaced Completed.");
                Check(handler.Requests == 1, "Premature disconnect triggered an automatic retry.");
            });
        }

        await Test("Responses WebSocket cancellation stops one written request without fallback replay", async () =>
        {
            var socket = new BlockingSocket();
            var fallback = new CountingFallback();
            await using var transport = new OpenAiResponsesWebSocketTransport(ResponsesProfile(), "key", () => socket, () => fallback);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
            try
            {
                _ = await Collect(transport.StartAsync(Start(), cancel.Token));
                throw new InvalidOperationException("Cancelled WebSocket request completed unexpectedly.");
            }
            catch (OperationCanceledException) { }
            Check(socket.Sends == 1, "Expected exactly one WebSocket write before cancellation.");
            Check(fallback.Starts == 0, "Written WebSocket request was replayed through HTTP fallback.");
        });

        await Test("Responses WebSocket malformed message is rejected without fallback replay", async () =>
        {
            var socket = new SequenceSocket(["not-json"]);
            var fallback = new CountingFallback();
            await using var transport = new OpenAiResponsesWebSocketTransport(ResponsesProfile(), "key", () => socket, () => fallback);
            try
            {
                _ = await Collect(transport.StartAsync(Start()));
                throw new InvalidOperationException("Malformed WebSocket message was accepted.");
            }
            catch (System.Text.Json.JsonException) { }
            Check(socket.Sends == 1 && fallback.Starts == 0, "Malformed written WebSocket request was replayed.");
        });

        await Test("Responses WebSocket premature end is ambiguous and never auto-replayed", async () =>
        {
            var socket = new SequenceSocket([
                "{\"type\":\"response.created\",\"response\":{\"id\":\"resp_partial\"}}",
                "{\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}"
            ]);
            var fallback = new CountingFallback();
            await using var transport = new OpenAiResponsesWebSocketTransport(ResponsesProfile(), "key", () => socket, () => fallback);
            var completed = false;
            try
            {
                await foreach (var item in transport.StartAsync(Start())) completed |= item.Kind == AgentTransportEventKind.Completed;
                throw new InvalidOperationException("Premature WebSocket end was accepted.");
            }
            catch (IOException) { }
            Check(!completed && socket.Sends == 1 && fallback.Starts == 0, "Ambiguous WebSocket end was replayed or marked complete.");
        });

        await Test("Capability matrix makes continuation behavior explicit", () =>
        {
            Check(!AgentTransportCapabilities.OllamaNative.IncrementalContinuation, "Ollama should advertise replay-based continuation.");
            Check(!AgentTransportCapabilities.ChatCompletionsFallback.IncrementalContinuation, "Chat fallback should advertise replay-based continuation.");
            Check(!AgentTransportCapabilities.OpenAiResponsesHttp.IncrementalContinuation, "Stateless Responses HTTP default should not claim incremental state.");
            Check(AgentTransportCapabilities.OpenAiResponsesWebSocket.IncrementalContinuation && AgentTransportCapabilities.OpenAiResponsesWebSocket.WebSocket,
                "Responses WebSocket must advertise incremental same-turn continuation.");
            return Task.CompletedTask;
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        var report = Path.Combine(outputDirectory, "v2-provider-transport-resilience-tests.txt");
        await File.WriteAllLinesAsync(report, lines);
        Console.WriteLine(string.Join("\n", lines));
        return failed == 0 ? 0 : 1;
    }

    private static AgentTransportStartRequest Start()
        => new(Guid.NewGuid(), Guid.NewGuid(), [new(AgentTransportMessageRole.User, "fixture")], []);

    private static AiProfile OllamaProfile() => new()
    {
        Protocol = AiProtocol.Ollama,
        BaseUrl = "http://localhost:11434",
        Model = "fixture"
    };

    private static AiProfile ChatProfile() => new()
    {
        Protocol = AiProtocol.OpenAiChat,
        BaseUrl = "https://example.invalid/v1",
        Model = "fixture"
    };

    private static AiProfile ResponsesProfile() => new()
    {
        Protocol = AiProtocol.OpenAiResponses,
        BaseUrl = "https://api.openai.com/v1",
        Model = "fixture"
    };

    private static IReadOnlyList<HttpFixture> HttpFixtures() =>
    [
        new("Ollama", handler => new OllamaTransport(OllamaProfile(), handler),
            "not-json\n", "application/x-ndjson",
            "{\"message\":{\"role\":\"assistant\",\"content\":\"partial\"},\"done\":false}\n"),
        new("Chat Completions", handler => new ChatCompletionsTransport(ChatProfile(), "", handler),
            "data: not-json\n", "text/event-stream",
            "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":null}]}\n"),
        new("Responses HTTP", handler => new OpenAiResponsesTransport(ResponsesProfile(), "", handler),
            "data: not-json\n", "text/event-stream",
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n")
    ];

    private static async Task<List<AgentTransportEvent>> Collect(IAsyncEnumerable<AgentTransportEvent> stream)
    {
        var result = new List<AgentTransportEvent>();
        await foreach (var item in stream) result.Add(item);
        return result;
    }

    private sealed record HttpFixture(
        string Name,
        Func<HttpMessageHandler, IAgentTransport> Create,
        string MalformedBody,
        string ContentType,
        string PartialBody);

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class StaticHandler(string body, string contentType) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            });
        }
    }

    private abstract class FakeSocketBase : IResponsesWebSocketConnection
    {
        public int Sends;
        public bool IsOpen { get; protected set; }
        public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); IsOpen = true; return Task.CompletedTask; }
        public Task SendTextAsync(string text, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); if (!IsOpen) throw new IOException("closed"); Sends++; return Task.CompletedTask; }
        public Task CloseAsync(CancellationToken cancellationToken) { IsOpen = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
        public abstract IAsyncEnumerable<string> ReceiveTextAsync(CancellationToken cancellationToken);
    }

    private sealed class BlockingSocket : FakeSocketBase
    {
        public override async IAsyncEnumerable<string> ReceiveTextAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class SequenceSocket(IReadOnlyList<string> events) : FakeSocketBase
    {
        public override async IAsyncEnumerable<string> ReceiveTextAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            foreach (var item in events) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        }
    }

    private sealed class CountingFallback : IAgentTransport
    {
        public int Starts;
        public AgentTransportCapabilities Capabilities => AgentTransportCapabilities.OpenAiResponsesHttp;
        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(AgentTransportStartRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { Starts++; await Task.Yield(); yield return AgentTransportEvent.Complete("fallback", "stop"); }
        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(AgentTransportContinuationRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.Yield(); yield return AgentTransportEvent.Complete("fallback-2", "stop"); }
        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

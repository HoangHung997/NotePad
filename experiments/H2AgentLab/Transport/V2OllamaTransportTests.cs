using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using H2AgentLab.Transport;
using H2Notes.Core;

namespace H2AgentLab;

public static class V2OllamaTransportTests
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

        await Test("Ollama transport preserves native tool call, thinking replay and usage", async () =>
        {
            var handler = new ToolLoopHandler();
            await using var transport = new OllamaTransport(new AiProfile
            {
                Model = "fixture",
                BaseUrl = "http://localhost:11434",
                OllamaThinking = true
            }, handler);
            var (taskId, turnId, tools) = Fixture();
            var first = await Collect(transport.StartAsync(new(
                taskId,
                turnId,
                [new(AgentTransportMessageRole.User, "Read a.txt")],
                tools)));

            var reasoning = first.Where(x => x.Kind == AgentTransportEventKind.ReasoningDelta).Select(x => x.Text).ToArray();
            if (reasoning.Length != 1 || reasoning[0] != "Fixture thinking field")
                throw new InvalidOperationException("Provider thinking was not streamed separately.");
            var tool = first.Single(x => x.Kind == AgentTransportEventKind.ToolCall).ToolCall
                ?? throw new InvalidOperationException("Missing typed tool call.");
            if (tool.Name != "read_file" || !tool.ArgumentsJson.Contains("a.txt", StringComparison.Ordinal))
                throw new InvalidOperationException("Tool call name/arguments changed.");
            var usage = first.Single(x => x.Kind == AgentTransportEventKind.Usage).Usage
                ?? throw new InvalidOperationException("Missing usage event.");
            if (usage.InputTokens != 120 || usage.CachedInputTokens != 80 || usage.OutputTokens != 12 || usage.TotalTokens != 132)
                throw new InvalidOperationException("Ollama public usage counters were not preserved.");
            if (first.Last().FinishReason != "tool_calls") throw new InvalidOperationException("Tool response did not finish as tool_calls.");

            var second = await Collect(transport.ContinueAsync(new(
                taskId,
                turnId,
                [new(tool.Id, tool.Name, "{\"success\":true,\"content\":\"hello\"}")])));
            if (string.Concat(second.Where(x => x.Kind == AgentTransportEventKind.TextDelta).Select(x => x.Text)) != "Verified answer")
                throw new InvalidOperationException("Continuation final answer was not streamed.");
            if (handler.RequestBodies.Count != 2) throw new InvalidOperationException("Expected exactly two Ollama requests.");

            using var secondPayload = JsonDocument.Parse(handler.RequestBodies[1]);
            var messages = secondPayload.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            var assistant = messages.Single(x => x.GetProperty("role").GetString() == "assistant");
            if (assistant.GetProperty("thinking").GetString() != "Fixture thinking field")
                throw new InvalidOperationException("Active tool-turn thinking was not replayed to Ollama.");
            if (assistant.GetProperty("tool_calls").GetArrayLength() != 1)
                throw new InvalidOperationException("Assistant tool call was not replayed.");
            var toolResult = messages.Single(x => x.GetProperty("role").GetString() == "tool");
            if (toolResult.GetProperty("tool_name").GetString() != "read_file"
                || !toolResult.GetProperty("content").GetString()!.Contains("hello", StringComparison.Ordinal))
                throw new InvalidOperationException("Ollama tool result shape changed.");
            if (!secondPayload.RootElement.GetProperty("think").GetBoolean())
                throw new InvalidOperationException("Explicit Ollama thinking=true was not preserved on continuation.");
        });

        await Test("Ollama transport honors explicit thinking false and streams direct answer", async () =>
        {
            var handler = new DirectHandler("ok");
            await using var transport = new OllamaTransport(new AiProfile
            {
                Model = "fixture",
                BaseUrl = "http://127.0.0.1:11434",
                OllamaThinking = false
            }, handler);
            var output = await Collect(transport.StartAsync(new(Guid.NewGuid(), Guid.NewGuid(),
                [new(AgentTransportMessageRole.User, "hello")], [])));
            if (string.Concat(output.Where(x => x.Kind == AgentTransportEventKind.TextDelta).Select(x => x.Text)) != "ok")
                throw new InvalidOperationException("Direct answer missing.");
            using var payload = JsonDocument.Parse(handler.RequestBodies.Single());
            if (payload.RootElement.GetProperty("think").GetBoolean())
                throw new InvalidOperationException("Explicit thinking=false was not preserved.");
            if (payload.RootElement.TryGetProperty("tools", out _))
                throw new InvalidOperationException("Empty tools should not inflate Ollama payload.");
        });

        await Test("Ollama transport sends native image bytes but rejects native files before network", async () =>
        {
            var imageHandler = new DirectHandler("seen");
            await using (var imageTransport = new OllamaTransport(new AiProfile { Model = "fixture" }, imageHandler))
            {
                var image = new AiImage("image/png", [1, 2, 3, 4]);
                _ = await Collect(imageTransport.StartAsync(new(Guid.NewGuid(), Guid.NewGuid(),
                    [new(AgentTransportMessageRole.User, "look", [image])], [])));
                using var payload = JsonDocument.Parse(imageHandler.RequestBodies.Single());
                var encoded = payload.RootElement.GetProperty("messages")[0].GetProperty("images")[0].GetString();
                if (encoded != Convert.ToBase64String(image.Data))
                    throw new InvalidOperationException("Native image bytes changed.");
            }

            var fileHandler = new DirectHandler("should-not-run");
            await using var fileTransport = new OllamaTransport(new AiProfile { Model = "fixture" }, fileHandler);
            var file = new AiFile("a.pdf", "application/pdf", [1, 2, 3]);
            try
            {
                _ = await Collect(fileTransport.StartAsync(new(Guid.NewGuid(), Guid.NewGuid(),
                    [new(AgentTransportMessageRole.User, "file", Files: [file])], [])));
                throw new InvalidOperationException("Native file was accepted unexpectedly.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("không hỗ trợ file", StringComparison.OrdinalIgnoreCase)) { }
            if (fileHandler.RequestBodies.Count != 0)
                throw new InvalidOperationException("Rejected native file reached the network handler.");
        });

        await Test("Ollama continuation rejects missing, duplicate and mismatched tool results", async () =>
        {
            var handler = new ToolLoopHandler();
            await using var transport = new OllamaTransport(new AiProfile { Model = "fixture" }, handler);
            var (taskId, turnId, tools) = Fixture();
            var first = await Collect(transport.StartAsync(new(taskId, turnId,
                [new(AgentTransportMessageRole.User, "Read")], tools)));
            var tool = first.Single(x => x.Kind == AgentTransportEventKind.ToolCall).ToolCall!;
            try
            {
                _ = await Collect(transport.ContinueAsync(new(taskId, turnId, [])));
                throw new InvalidOperationException("Missing tool result was accepted.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("khớp đúng", StringComparison.OrdinalIgnoreCase)) { }
            try
            {
                _ = await Collect(transport.ContinueAsync(new(taskId, turnId,
                    [new(tool.Id, "other_tool", "{}")])));
                throw new InvalidOperationException("Mismatched tool name was accepted.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("không khớp tên", StringComparison.OrdinalIgnoreCase)) { }
            if (handler.RequestBodies.Count != 1)
                throw new InvalidOperationException("Invalid continuation sent another model request.");
        });

        await Test("Ollama transport reuses H2 Core endpoint safety", async () =>
        {
            var handler = new DirectHandler("never");
            await using var transport = new OllamaTransport(new AiProfile
            {
                Model = "fixture",
                BaseUrl = "http://8.8.8.8:11434"
            }, handler);
            try
            {
                _ = await Collect(transport.StartAsync(new(Guid.NewGuid(), Guid.NewGuid(),
                    [new(AgentTransportMessageRole.User, "hello")], [])));
                throw new InvalidOperationException("Unsafe public HTTP endpoint was accepted.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("HTTPS", StringComparison.OrdinalIgnoreCase)) { }
            if (handler.RequestBodies.Count != 0)
                throw new InvalidOperationException("Unsafe endpoint reached network handler.");
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        await File.WriteAllLinesAsync(Path.Combine(root, "v2-ollama-transport-tests.txt"), lines);
        Console.WriteLine(string.Join("\n", lines));
        return failed == 0 ? 0 : 1;
    }

    private static (Guid TaskId, Guid TurnId, IReadOnlyList<AgentToolDefinition> Tools) Fixture()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"offset\":{\"type\":\"string\"}},\"required\":[\"path\",\"offset\"],\"additionalProperties\":false}");
        return (Guid.NewGuid(), Guid.NewGuid(), [new("read_file", "Read a file", schema.RootElement.Clone())]);
    }

    private static async Task<List<AgentTransportEvent>> Collect(IAsyncEnumerable<AgentTransportEvent> stream)
    {
        var output = new List<AgentTransportEvent>();
        await foreach (var item in stream) output.Add(item);
        return output;
    }

    private abstract class CaptureHandler : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];
        protected async Task<string> Capture(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            RequestBodies.Add(body);
            return body;
        }
        protected static HttpResponseMessage Ndjson(params object[] lines)
        {
            var body = string.Join("\n", lines.Select(line => JsonSerializer.Serialize(line))) + "\n";
            var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
            return new(HttpStatusCode.OK) { Content = content };
        }
    }

    private sealed class ToolLoopHandler : CaptureHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _ = await Capture(request, ct);
            if (RequestBodies.Count == 1)
            {
                return Ndjson(new
                {
                    message = new
                    {
                        role = "assistant",
                        thinking = "Fixture thinking field",
                        tool_calls = new[] { new { function = new { name = "read_file", arguments = new { path = "a.txt", offset = "0" } } } }
                    },
                    done = true,
                    done_reason = "stop",
                    prompt_eval_count = 120,
                    prompt_eval_cached_count = 80,
                    eval_count = 12
                });
            }
            return Ndjson(new
            {
                message = new { role = "assistant", content = "Verified answer" },
                done = true,
                done_reason = "stop",
                prompt_eval_count = 40,
                prompt_eval_cached_count = 20,
                eval_count = 4
            });
        }
    }

    private sealed class DirectHandler(string answer) : CaptureHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _ = await Capture(request, ct);
            return Ndjson(new { message = new { role = "assistant", content = answer }, done = true, done_reason = "stop" });
        }
    }
}

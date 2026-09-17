using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using H2Notes.Core;

internal static class AiStreamingTests
{
    public static void Run(Action<string, Action> test)
    {
        test("AI existing profiles wait by default and preserve wait choice in model copies", () =>
        {
            var p = JsonSerializer.Deserialize<AiProfile>("{\"TimeoutSeconds\":10}")!;
            Check(p.WaitForCompletion);
            p.WaitForCompletion = false;
            Check(!AiModelCapabilities.WithReasoning(p, null).WaitForCompletion);
        });
        test("AI weak-server wait and reasoning idle resets work for all four protocols", () => Task.Run(async () =>
        {
            await Task.WhenAll(Enum.GetValues<AiProtocol>().Select(async protocol =>
            {
                var profile = Profile(protocol); profile.TimeoutSeconds = 10;
                using (var client = new AiClient(new SlowHandler([(11200, Reasoning(protocol)), (11200, Answer(protocol) + Completion(protocol))])))
                {
                    var events = await Collect(client.StreamEvents(profile, "", [new("user", "synthetic fixture")]));
                    Check(events.Last().Text == "final answer");
                }
                profile.WaitForCompletion = false;
                using (var client = new AiClient(new SlowHandler([(4000, Reasoning(protocol)), (4000, Reasoning(protocol)), (4000, Answer(protocol) + Completion(protocol))])))
                    Check((await Collect(client.StreamEvents(profile, "", []))).Last().Text == "final answer");
            }));
        }).GetAwaiter().GetResult());
        test("AI wait can be canceled during silence without retry", () => Task.Run(async () =>
        {
            var handler = new SlowHandler([(30000, Reasoning(AiProtocol.Ollama))]);
            using var client = new AiClient(handler);
            using var stop = new CancellationTokenSource(150);
            try { await Collect(client.StreamEvents(Profile(AiProtocol.Ollama), "", [], stop.Token)); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { Check(handler.Calls == 1); }
        }).GetAwaiter().GetResult());
        test("AI optional idle timeout stops a silent server without retry", () => Task.Run(async () =>
        {
            var profile = Profile(AiProtocol.Ollama); profile.WaitForCompletion = false; profile.TimeoutSeconds = 10;
            var handler = new SlowHandler([(30000, Reasoning(AiProtocol.Ollama))]);
            using var client = new AiClient(handler);
            try { await Collect(client.StreamEvents(profile, "", [])); throw new Exception("Expected idle timeout"); }
            catch (OperationCanceledException) { Check(handler.Calls == 1); }
        }).GetAwaiter().GetResult());
        foreach (var protocol in Enum.GetValues<AiProtocol>())
        {
            test("AI typed stream separates provider reasoning and final text: " + protocol, () =>
            {
                var handler = new Stub(Reasoning(protocol) + Answer(protocol) + Completion(protocol));
                using var client = new AiClient(handler);
                var events = Collect(client.StreamEvents(Profile(protocol), "test-secret", [new("user", "approved context")])).GetAwaiter().GetResult();
                Check(events.SequenceEqual(new[] { new AiStreamEvent(ReasoningKind(protocol), "private trace"), new AiStreamEvent(AiStreamEventKind.Text, "final answer") }));
                Check(handler.Calls == 1 && handler.Body!.Contains("approved context") && !handler.Body.Contains("test-secret"));
                Check(protocol == AiProtocol.Ollama ? handler.Auth is null && handler.GoogleKey is null
                    : protocol == AiProtocol.Gemini ? handler.GoogleKey == "test-secret" && handler.Auth is null
                    : handler.Auth == "Bearer test-secret" && handler.GoogleKey is null);
                using var body = JsonDocument.Parse(handler.Body!);
                foreach (var setting in new[] { "think", "thinking", "reasoning", "reasoning_effort", "generationConfig", "include" })
                    Check(!body.RootElement.TryGetProperty(setting, out _));
                if (protocol == AiProtocol.OpenAiResponses) Check(body.RootElement.GetProperty("store").ValueKind == JsonValueKind.False);
            });
            test("AI legacy Stream excludes reasoning from saved and replayed turns: " + protocol, () =>
            {
                var handler = new Stub(Reasoning(protocol) + Answer(protocol) + Completion(protocol));
                using var client = new AiClient(handler);
                var profile = Profile(protocol);
                var answer = string.Concat(Collect(client.Stream(profile, "", [new("user", "hello")])).GetAwaiter().GetResult());
                Check(answer == "final answer");
                var history = new AiTurn[] { new("user", "hello"), new("assistant", answer) };
                Check(!JsonSerializer.Serialize(history).Contains("private trace"));
                Collect(client.StreamEvents(profile, "", history)).GetAwaiter().GetResult();
                Check(!handler.Body!.Contains("private trace") && handler.Body.Contains("final answer"));
            });
            test("AI reasoning-only completion is not a successful final answer: " + protocol, () =>
            {
                var handler = new Stub(Reasoning(protocol) + Completion(protocol));
                using var client = new AiClient(handler);
                var received = new List<AiStreamEvent>();
                var error = Throws<AiServiceException>(() => Collect(client.StreamEvents(Profile(protocol), "", []), received).GetAwaiter().GetResult());
                Check(error.Code == "empty_reply" && received.Count == 1 && received[0].Kind == ReasoningKind(protocol));
                var text = new List<string>();
                Throws<AiServiceException>(() => Collect(client.Stream(Profile(protocol), "", []), text).GetAwaiter().GetResult());
                Check(text.Count == 0 && handler.Calls == 2);
            });
            test("AI reasoning does not mask truncated streams or trigger retries: " + protocol, () =>
            {
                var handler = new Stub(Reasoning(protocol) + Answer(protocol));
                using var client = new AiClient(handler);
                var received = new List<AiStreamEvent>();
                Throws<IOException>(() => Collect(client.StreamEvents(Profile(protocol), "", []), received).GetAwaiter().GetResult());
                Check(received.Count == 2 && received[1].Text == "final answer" && handler.Calls == 1);
            });
            test("AI cancellation after reasoning stops buffered final tokens: " + protocol, () =>
            {
                var payload = protocol switch
                {
                    AiProtocol.Ollama => "{\"message\":{\"thinking\":\"private trace\",\"content\":\"final answer\"},\"done\":true}\n",
                    AiProtocol.OpenAiChat => Sse("{\"choices\":[{\"delta\":{\"reasoning_content\":\"private trace\",\"content\":\"final answer\"},\"finish_reason\":\"stop\"}]}"),
                    AiProtocol.Gemini => Sse("{\"candidates\":[{\"content\":{\"parts\":[{\"thought\":true,\"text\":\"private trace\"},{\"text\":\"final answer\"}]},\"finishReason\":\"STOP\"}]}"),
                    _ => Reasoning(protocol) + Answer(protocol) + Completion(protocol)
                };
                var handler = new Stub(payload);
                using var client = new AiClient(handler);
                using var cancel = new CancellationTokenSource();
                var received = new List<AiStreamEvent>();
                async Task Read()
                {
                    await foreach (var item in client.StreamEvents(Profile(protocol), "", []).WithCancellation(cancel.Token))
                    {
                        received.Add(item);
                        cancel.Cancel();
                    }
                }
                Throws<OperationCanceledException>(() => Read().GetAwaiter().GetResult());
                Check(received.Count == 1 && received[0].Kind == ReasoningKind(protocol) && handler.Calls == 1);
            });
            test("AI in-stream errors preserve prior events without exposing server secrets: " + protocol, () =>
            {
                var errorJson = "{\"error\":{\"message\":\"test-secret private prompt\"}}";
                var handler = new Stub(Reasoning(protocol) + Answer(protocol) + (protocol == AiProtocol.Ollama ? errorJson + "\n" : Sse(errorJson)));
                using var client = new AiClient(handler);
                var received = new List<AiStreamEvent>();
                var error = Throws<HttpRequestException>(() => Collect(client.StreamEvents(Profile(protocol), "test-secret", []), received).GetAwaiter().GetResult());
                Check(received.Count == 2 && !error.Message.Contains("test-secret") && !error.Message.Contains("private prompt") && handler.Calls == 1);
            });
        }

        test("AI Ollama emits thinking before content when both arrive together", () =>
        {
            using var client = new AiClient(new Stub("{\"message\":{\"content\":\"answer\",\"thinking\":\"trace\"},\"done\":true}\n"));
            var events = Collect(client.StreamEvents(Profile(AiProtocol.Ollama), "", [])).GetAwaiter().GetResult();
            Check(events.SequenceEqual(new[] { new AiStreamEvent(AiStreamEventKind.Reasoning, "trace"), new AiStreamEvent(AiStreamEventKind.Text, "answer") }));
        });
        foreach (var fields in new[]
        {
            "\"reasoning_content\":\"trace\"", "\"reasoning\":\"trace\"",
            "\"reasoning_content\":null,\"reasoning\":\"trace\"", "\"reasoning_content\":\"\",\"reasoning\":\"trace\"",
            "\"reasoning_content\":\"trace\",\"reasoning\":\"trace\""
        })
            test("AI Chat reasoning aliases remain separate from same-chunk content: " + fields, () =>
            {
                using var client = new AiClient(new Stub(Sse("{\"choices\":[{\"delta\":{\"content\":\"answer\"," + fields + "}}]}") + Completion(AiProtocol.OpenAiChat)));
                var events = Collect(client.StreamEvents(Profile(AiProtocol.OpenAiChat), "", [])).GetAwaiter().GetResult();
                Check(events.SequenceEqual(new[] { new AiStreamEvent(AiStreamEventKind.Reasoning, "trace"), new AiStreamEvent(AiStreamEventKind.Text, "answer") }));
            });
        test("AI Responses emits summary deltas only, not raw reasoning or duplicate done snapshots", () =>
        {
            var payload = Sse("{\"type\":\"response.reasoning_text.delta\",\"delta\":\"hidden text\"}")
                + Reasoning(AiProtocol.OpenAiResponses)
                + Sse("{\"type\":\"response.reasoning_summary_text.done\",\"text\":\"private trace\"}")
                + Sse("{\"type\":\"response.output_item.done\",\"item\":{\"type\":\"reasoning\",\"encrypted_content\":\"opaque secret\"}}")
                + Answer(AiProtocol.OpenAiResponses) + Completion(AiProtocol.OpenAiResponses);
            using var client = new AiClient(new Stub(payload));
            var events = Collect(client.StreamEvents(Profile(AiProtocol.OpenAiResponses), "", [])).GetAwaiter().GetResult();
            Check(events.Count == 2 && events[0] == new AiStreamEvent(AiStreamEventKind.ReasoningSummary, "private trace"));
            Check(events[1] == new AiStreamEvent(AiStreamEventKind.Text, "final answer"));
        });
        test("AI Gemini preserves mixed-part order and ignores opaque thought signatures and other candidates", () =>
        {
            var payload = Sse("{\"candidates\":[{\"content\":{\"parts\":[{\"thoughtSignature\":\"opaque secret\"},{\"thought\":true,\"text\":\"summary 1\"},{\"thought\":false,\"text\":\"answer 1\"},{\"thought\":true,\"text\":\"summary 2\"},{\"text\":\"answer 2\"}]},\"finishReason\":\"STOP\"},{\"content\":{\"parts\":[{\"text\":\"other candidate\"}]}}]}");
            using var client = new AiClient(new Stub(payload));
            var events = Collect(client.StreamEvents(Profile(AiProtocol.Gemini), "", [])).GetAwaiter().GetResult();
            Check(events.SequenceEqual(new[]
            {
                new AiStreamEvent(AiStreamEventKind.ReasoningSummary, "summary 1"), new AiStreamEvent(AiStreamEventKind.Text, "answer 1"),
                new AiStreamEvent(AiStreamEventKind.ReasoningSummary, "summary 2"), new AiStreamEvent(AiStreamEventKind.Text, "answer 2")
            }));
        });
        test("AI empty and non-string provider reasoning fields do not become text", () =>
        {
            using var client = new AiClient(new Stub(Sse("{\"choices\":[{\"delta\":null}]}")
                + Sse("{\"choices\":[{\"delta\":{\"reasoning_content\":{},\"reasoning\":[],\"content\":null}}]}")
                + Answer(AiProtocol.OpenAiChat) + Completion(AiProtocol.OpenAiChat)));
            var events = Collect(client.StreamEvents(Profile(AiProtocol.OpenAiChat), "", [])).GetAwaiter().GetResult();
            Check(events.Count == 1 && events[0].Kind == AiStreamEventKind.Text);
        });

        foreach (var protocol in Enum.GetValues<AiProtocol>())
            foreach (var thinking in new bool?[] { null, true, false })
                test("AI Ollama thinking is explicit and never sent to other protocols: " + protocol + " " + (thinking?.ToString() ?? "default"), () =>
                {
                    var profile = Profile(protocol); profile.OllamaThinking = thinking;
                    var handler = new Stub(Answer(protocol) + Completion(protocol));
                    using var client = new AiClient(handler);
                    Collect(client.StreamEvents(profile, "", [new("user", "approved context")])).GetAwaiter().GetResult();
                    using var body = JsonDocument.Parse(handler.Body!);
                    if (protocol == AiProtocol.Ollama && thinking.HasValue)
                    {
                        var think = body.RootElement.GetProperty("think");
                        Check(think.ValueKind == (thinking.Value ? JsonValueKind.True : JsonValueKind.False));
                    }
                    else Check(!body.RootElement.TryGetProperty("think", out _));
                    if (protocol == AiProtocol.Gemini)
                        Check(handler.Url!.AbsolutePath.EndsWith("/models/" + Uri.EscapeDataString(profile.Model) + ":streamGenerateContent", StringComparison.Ordinal));
                    else Check(body.RootElement.GetProperty("model").GetString() == profile.Model);
                    Check(handler.Body!.Contains("approved context"));
                    foreach (var setting in new[] { "thinking", "reasoning", "reasoning_effort", "generationConfig", "include", "OllamaThinking" })
                        Check(!body.RootElement.TryGetProperty(setting, out _));
                    Check(handler.Calls == 1);
                });
        test("AI Ollama thinking defaults to model behavior and preserves all three profile choices", () =>
        {
            Check(new AiProfile().OllamaThinking is null);
            Check(JsonSerializer.Deserialize<AiProfile>("{}")!.OllamaThinking is null);
            foreach (var thinking in new bool?[] { null, true, false })
            {
                var profile = new AiProfile { OllamaThinking = thinking };
                var restored = JsonSerializer.Deserialize<AiProfile>(JsonSerializer.Serialize(profile))!;
                Check(restored.OllamaThinking == thinking);
            }
        });

        foreach (var (protocol, url) in new[]
        {
            (AiProtocol.OpenAiResponses, "https://api.openai.com/v1"),
            (AiProtocol.Gemini, "https://generativelanguage.googleapis.com/v1beta"),
            (AiProtocol.Gemini, "https://generativelanguage.googleapis.com/v1")
        })
            test("AI public summary opt-in sets no hidden reasoning or effort settings: " + url, () =>
            {
                var profile = Profile(protocol); profile.BaseUrl = url;
                var handler = new Stub(Answer(protocol) + Completion(protocol));
                using var client = new AiClient(handler);
                Collect(client.StreamEvents(profile, "", [])).GetAwaiter().GetResult();
                using (var defaults = JsonDocument.Parse(handler.Body!))
                {
                    Check(!defaults.RootElement.TryGetProperty("reasoning", out _));
                    Check(!defaults.RootElement.TryGetProperty("generationConfig", out _));
                }
                profile.RequestReasoningSummary = true;
                Collect(client.StreamEvents(profile, "", [])).GetAwaiter().GetResult();
                using var body = JsonDocument.Parse(handler.Body!);
                if (protocol == AiProtocol.OpenAiResponses)
                {
                    var reasoning = body.RootElement.GetProperty("reasoning");
                    Check(reasoning.EnumerateObject().Count() == 1 && reasoning.GetProperty("summary").GetString() == "auto");
                }
                else
                {
                    var generation = body.RootElement.GetProperty("generationConfig");
                    var thinking = generation.GetProperty("thinkingConfig");
                    Check(generation.EnumerateObject().Count() == 1 && thinking.EnumerateObject().Count() == 1
                        && thinking.GetProperty("includeThoughts").ValueKind == JsonValueKind.True);
                }
                Check(!body.RootElement.TryGetProperty("include", out _) && !handler.Body!.Contains("encrypted") && !handler.Body.Contains("effort") && !handler.Body.Contains("Budget"));
            });
        foreach (var (protocol, url) in new[]
        {
            (AiProtocol.Ollama, "http://192.168.1.208:11434"),
            (AiProtocol.OpenAiChat, "https://api.openai.com/v1"),
            (AiProtocol.OpenAiResponses, "https://example.test/v1"),
            (AiProtocol.OpenAiResponses, "https://api.openai.com.evil.test/v1"),
            (AiProtocol.OpenAiResponses, "https://api.openai.com:8443/v1"),
            (AiProtocol.OpenAiResponses, "https://api.openai.com/other-api"),
            (AiProtocol.Gemini, "https://example.test/v1beta")
        })
            test("AI summary opt-in never imposes settings on unsupported endpoints: " + url + " " + protocol, () =>
            {
                var profile = Profile(protocol); profile.BaseUrl = url; profile.RequestReasoningSummary = true;
                var handler = new Stub(Answer(protocol) + Completion(protocol));
                using var client = new AiClient(handler);
                Throws<InvalidOperationException>(() => Collect(client.StreamEvents(profile, "test-secret", [])).GetAwaiter().GetResult());
                Check(handler.Calls == 0);
            });

        foreach (var host in new[] { "localhost", "LOCALHOST", "127.0.0.1", "127.254.1.2", "[::1]", "[::ffff:127.0.0.1]" })
            test("AI Ollama permits HTTP loopback and labels it local: " + host, () =>
            {
                var profile = new AiProfile { BaseUrl = "http://" + host + ":11434" };
                Check(AiClient.Endpoint(profile, "api/tags").AbsolutePath == "/api/tags");
                Check(profile.ProcessingLocation.Contains("chạy trên máy"));
            });
        foreach (var host in new[]
        {
            "192.168.1.208", "10.0.0.1", "10.255.255.254", "172.16.0.1", "172.31.255.254", "192.168.255.254",
            "[fc00::1]", "[fdff:ffff::208]", "[fe80::208]", "[fe80::208%2512]", "[::ffff:192.168.1.208]", "[::ffff:10.0.0.1]"
        })
            test("AI Ollama permits explicitly configured private LAN HTTP and labels remote processing: " + host, () =>
            {
                var profile = new AiProfile { BaseUrl = "http://" + host + ":11434" };
                Check(AiClient.Endpoint(profile, "api/chat").AbsolutePath == "/api/chat");
                Check(profile.ProcessingLocation.Contains("LAN") && !profile.ProcessingLocation.Contains("chạy trên máy"));
                profile.Model = "model:cloud";
                Check(profile.ProcessingLocation.Contains("Cloud") && !profile.ProcessingLocation.Contains("LAN"));
            });
        foreach (var host in new[]
        {
            "8.8.8.8", "192.167.1.208", "192.169.1.208", "172.15.255.254", "172.32.0.1", "11.0.0.1",
            "100.64.0.1", "169.254.169.254", "0.0.0.0", "255.255.255.255", "224.0.0.1",
            "[::]", "[2001:4860:4860::8888]", "[ff02::1]", "[fec0::1]", "[::ffff:8.8.8.8]", "[2002:0a00:0001::1]",
            "example.test", "ollama.local", "localhost.evil.test", "192.168.1.208.evil.test"
        })
            test("AI Ollama rejects public or non-approved HTTP addresses: " + host, () =>
                Throws<InvalidOperationException>(() => AiClient.Endpoint(new() { BaseUrl = "http://" + host + ":11434" }, "api/chat")));
        foreach (var protocol in new[] { AiProtocol.OpenAiResponses, AiProtocol.OpenAiChat, AiProtocol.Gemini })
            test("AI online protocols still require HTTPS even on private networks: " + protocol, () =>
            {
                foreach (var host in new[] { "localhost", "127.0.0.1", "192.168.1.208", "[::1]", "[fd00::208]", "example.test" })
                    Throws<InvalidOperationException>(() => AiClient.Endpoint(new() { Protocol = protocol, BaseUrl = "http://" + host }, "models"));
                Check(AiClient.Endpoint(new() { Protocol = protocol, BaseUrl = "https://example.test/v1" }, "models").AbsolutePath == "/v1/models");
            });
        test("AI URL credentials, secret queries, fragments, and cross-origin paths remain rejected", () =>
        {
            foreach (var url in new[]
            {
                "http://user:test-secret@192.168.1.208:11434", "http://192.168.1.208:11434?key=test-secret",
                "http://192.168.1.208:11434#test-secret", "https://user:test-secret@example.test", "file:///tmp", "ftp://192.168.1.208"
            })
            {
                var error = Throws<InvalidOperationException>(() => AiClient.Endpoint(new() { BaseUrl = url }, "api/chat"));
                Check(!error.Message.Contains("test-secret"));
            }
            var profile = new AiProfile { BaseUrl = "http://192.168.1.208:11434" };
            foreach (var path in new[] { "https://evil.test/api/chat", "//evil.test/api/chat", "http://192.168.1.208:80/api/chat", "http://user:test-secret@192.168.1.208:11434/api/chat", "api/chat#test-secret" })
                Throws<InvalidOperationException>(() => AiClient.Endpoint(profile, path));
        });
        test("AI Ollama LAN model operations send no project data or API secrets", () =>
        {
            var handler = new Stub("{\"models\":[{\"name\":\"local-model\"}]}");
            using var client = new AiClient(handler);
            var profile = new AiProfile { BaseUrl = "http://192.168.1.208:11434", Model = "local-model" };
            Check(client.ListModels(profile, "test-secret").GetAwaiter().GetResult().Single() == "local-model");
            Check(handler.Url!.AbsolutePath == "/api/tags" && handler.Auth is null && handler.GoogleKey is null && handler.Body is null);
            Check(client.LoadedOllamaModels(profile).GetAwaiter().GetResult().Single() == "local-model");
            Check(handler.Url!.AbsolutePath == "/api/ps" && handler.Body is null);
            client.SetOllamaLoaded(profile, true).GetAwaiter().GetResult();
            Check(handler.Url!.AbsolutePath == "/api/generate" && !handler.Body!.Contains("messages") && !handler.Body.Contains("test-secret"));
        });
        foreach (var status in new[] { HttpStatusCode.MovedPermanently, HttpStatusCode.Redirect, HttpStatusCode.TemporaryRedirect, HttpStatusCode.PermanentRedirect })
            test("AI rejects redirect responses without retrying or exposing secrets: " + status, () =>
            {
                var handler = new Stub("test-secret", status) { Redirect = new Uri("https://other.test/?key=test-secret") };
                using var client = new AiClient(handler);
                var error = Throws<HttpRequestException>(() => Collect(client.StreamEvents(Profile(AiProtocol.OpenAiChat), "test-secret", [])).GetAwaiter().GetResult());
                Check(handler.Calls == 1 && !error.Message.Contains("test-secret") && !error.Message.Contains("other.test"));
            });
        // The shared runner may block Avalonia's UI context; run both async TCP
        // endpoints and the client on the thread pool before synchronously waiting.
        test("AI default HTTP transport does not follow a LAN redirect", () => Task.Run(VerifyDefaultRedirect).GetAwaiter().GetResult());
    }

    private static AiProfile Profile(AiProtocol protocol) => new()
    {
        Protocol = protocol, Model = "test-model",
        BaseUrl = protocol == AiProtocol.Ollama ? "http://192.168.1.208:11434" : "https://example.test/v1"
    };

    private static AiStreamEventKind ReasoningKind(AiProtocol protocol) => protocol is AiProtocol.OpenAiResponses or AiProtocol.Gemini
        ? AiStreamEventKind.ReasoningSummary : AiStreamEventKind.Reasoning;

    private static string Sse(string json) => "data: " + json + "\n\n";

    private static string Reasoning(AiProtocol protocol) => protocol switch
    {
        AiProtocol.Ollama => "{\"message\":{\"thinking\":\"private trace\",\"content\":\"\"},\"done\":false}\n",
        AiProtocol.OpenAiResponses => Sse("{\"type\":\"response.reasoning_summary_text.delta\",\"delta\":\"private trace\"}"),
        AiProtocol.Gemini => Sse("{\"candidates\":[{\"content\":{\"parts\":[{\"thought\":true,\"text\":\"private trace\"}]}}]}"),
        _ => Sse("{\"choices\":[{\"delta\":{\"reasoning_content\":\"private trace\",\"content\":null}}]}")
    };

    private static string Answer(AiProtocol protocol) => protocol switch
    {
        AiProtocol.Ollama => "{\"message\":{\"content\":\"final answer\"},\"done\":false}\n",
        AiProtocol.OpenAiResponses => Sse("{\"type\":\"response.output_text.delta\",\"delta\":\"final answer\"}"),
        AiProtocol.Gemini => Sse("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"final answer\"}]}}]}"),
        _ => Sse("{\"choices\":[{\"delta\":{\"content\":\"final answer\"}}]}")
    };

    private static string Completion(AiProtocol protocol) => protocol switch
    {
        AiProtocol.Ollama => "{\"message\":{},\"done\":true}\n",
        AiProtocol.OpenAiResponses => Sse("{\"type\":\"response.completed\"}"),
        AiProtocol.Gemini => Sse("{\"candidates\":[{\"finishReason\":\"STOP\"}]}"),
        _ => "data: [DONE]\n\n"
    };

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source, List<T>? received = null)
    {
        received ??= [];
        await foreach (var item in source) received.Add(item);
        return received;
    }

    private static async Task VerifyDefaultRedirect()
    {
        // Ephemeral loopback servers only, never the user's configured Ollama service.
        using var source = new TcpListener(IPAddress.Loopback, 0);
        using var destination = new TcpListener(IPAddress.Loopback, 0);
        source.Start(); destination.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var target = (IPEndPoint)destination.LocalEndpoint;
        async Task ServeRedirect()
        {
            using var connection = await source.AcceptTcpClientAsync(deadline.Token);
            using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 }) { }
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{target.Port}/sink\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, deadline.Token);
        }
        var server = ServeRedirect();
        using var client = new AiClient();
        var profile = new AiProfile { BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)source.LocalEndpoint).Port };
        try
        {
            await client.ListModels(profile, "test-secret", deadline.Token);
            throw new Exception("Expected redirect rejection");
        }
        catch (HttpRequestException error) { Check(error.StatusCode == HttpStatusCode.TemporaryRedirect); }
        finally { await server; }
        Check(!destination.Pending());
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new Exception("AI streaming assertion failed");
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class Stub(string payload, HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls;
        public string? Body, Auth, GoogleKey;
        public Uri? Url, Redirect;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Url = request.RequestUri;
            Auth = request.Headers.Authorization?.ToString();
            GoogleKey = request.Headers.TryGetValues("x-goog-api-key", out var keys) ? keys.Single() : null;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(code) { Content = new StringContent(payload) };
            response.Headers.Location = Redirect;
            return response;
        }
    }

    private sealed class SlowHandler((int Delay, string Text)[] chunks) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SlowStream(chunks)) });
        }
    }

    private sealed class SlowStream((int Delay, string Text)[] chunks) : Stream
    {
        private int _chunk, _offset;
        private byte[]? _data;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_data is null)
            {
                // Keep the stream open after protocol completion: clients must stop at done,
                // not wait forever for the server to close its HTTP connection.
                if (_chunk == chunks.Length) { await Task.Delay(30000, token); return 0; }
                await Task.Delay(chunks[_chunk].Delay, token);
                _data = Encoding.UTF8.GetBytes(chunks[_chunk++].Text); _offset = 0;
            }
            var count = Math.Min(buffer.Length, _data.Length - _offset);
            _data.AsMemory(_offset, count).CopyTo(buffer); _offset += count;
            if (_offset == _data.Length) _data = null;
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}

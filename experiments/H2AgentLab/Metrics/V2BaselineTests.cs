using System.Net;
using System.Text;
using System.Text.Json;
using H2Notes.Core;

namespace H2AgentLab.Metrics;

public static class V2BaselineTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root)) throw new IOException("Use a new v2 baseline directory.");
        Directory.CreateDirectory(root);
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "a.txt"), "fixture evidence");
        var tools = new AgentTools(new SafeWorkspace(workspace), Path.Combine(root, "state"), (_, _) => Task.FromResult(true), (_, _) => { });
        var profile = new AiProfile { Model = "fixture", Protocol = AiProtocol.Ollama, BaseUrl = "http://localhost:11434" };
        var lines = new List<string>();
        var failed = 0;

        async Task Test(string name, Func<Task> action)
        {
            try { await action(); lines.Add("PASS " + name); }
            catch (Exception ex) { failed++; lines.Add("FAIL " + name + ": " + ex.Message); }
        }
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

        AgentMetricsSnapshot? rawMetrics = null;
        AgentMetricsSnapshot? v1DirectMetrics = null;
        AgentMetricsSnapshot? v1ToolMetrics = null;

        await Test("Raw no-tool probe measures the same profile and prompt without Agent Lab tool context", async () =>
        {
            var telemetry = new AgentRunTelemetry();
            using var probe = new RawModelBaselineProbe(new DirectAnswerHandler("Raw answer", promptTokens: 5, outputTokens: 2));
            var result = await probe.Run(profile, "", "Same prompt", telemetry, default);
            rawMetrics = telemetry.Metrics.Snapshot(telemetry.Trace);
            Check(result == "Raw answer", "Raw baseline answer mismatch.");
            Check(rawMetrics.ModelCalls == 1 && rawMetrics.ToolCalls == 0, "Raw baseline call counts are wrong.");
            Check(rawMetrics.InputTokens == 5 && rawMetrics.OutputTokens == 2, "Raw Ollama usage was not captured.");
            Check(telemetry.Trace.First(AgentTraceKind.RequestStart) is not null && telemetry.Trace.First(AgentTraceKind.FirstModelEvent) is not null, "Raw baseline TTFT events missing.");
        });

        await Test("Current v1 direct loop exposes its extra request payload overhead", async () =>
        {
            var telemetry = new AgentRunTelemetry();
            using var runner = new AgentRunner(new DirectAnswerHandler("Raw answer", promptTokens: 20, outputTokens: 2));
            var session = new LabSession();
            string? final = null;
            await runner.Run(profile, "", session, tools, "Same prompt", (k, t) => { if (k == "final") final = t; }, () => { }, telemetry, default);
            v1DirectMetrics = telemetry.Metrics.Snapshot(telemetry.Trace);
            Check(final == "Raw answer", "V1 direct answer mismatch.");
            Check(v1DirectMetrics.ModelCalls == 1 && v1DirectMetrics.ToolCalls == 0, "V1 direct call counts are wrong.");
            Check(rawMetrics is not null && v1DirectMetrics.BytesSent > rawMetrics.BytesSent, "V1 direct payload did not show expected full-harness overhead over raw prompt.");
        });

        await Test("Current v1 one-tool fixture measures continuation and tool overhead", async () =>
        {
            var telemetry = new AgentRunTelemetry();
            using var runner = new AgentRunner(new OneToolHandler());
            string? final = null;
            await runner.Run(profile, "", new LabSession(), tools, "Read a.txt", (k, t) => { if (k == "final") final = t; }, () => { }, telemetry, default);
            v1ToolMetrics = telemetry.Metrics.Snapshot(telemetry.Trace);
            Check(final == "Tool answer", "V1 tool answer mismatch.");
            Check(v1ToolMetrics.ModelCalls == 2 && v1ToolMetrics.ToolCalls == 1, "Tool-loop call counts are wrong.");
            Check(v1DirectMetrics is not null && v1ToolMetrics.BytesSent > v1DirectMetrics.BytesSent, "Tool continuation did not increase sent payload as expected in v1.");
            Check(telemetry.Trace.Snapshot().Count(e => e.Kind == AgentTraceKind.Continuation) >= 1, "Tool continuation trace missing.");
        });

        if (rawMetrics is not null && v1DirectMetrics is not null && v1ToolMetrics is not null)
        {
            var report = new StringBuilder();
            report.AppendLine("# H2 Agent Lab initial A/B harness baseline");
            report.AppendLine();
            report.AppendLine("Synthetic deterministic fixture. This compares harness structure and instrumentation, not real provider speed or model quality.");
            report.AppendLine();
            report.AppendLine("| Case | Model calls | Tool calls | Bytes sent | Input tokens reported | Output tokens reported | TTFT ms | Total ms |");
            report.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
            Row(report, "A — raw no-tool", rawMetrics);
            Row(report, "B — v1 direct", v1DirectMetrics);
            Row(report, "B — v1 one-tool", v1ToolMetrics);
            report.AppendLine();
            report.AppendLine("Interpretation: the raw probe sends only the user prompt. The v1 direct path adds the Lab system/recovery/environment context and every tool schema. The v1 tool path makes a second model call and resends the enlarged transcript/tool schema. Phase 02+ is intended to reduce these costs with provider-native continuation and deferred tools. Real API/local-model latency and cost must be measured separately later.");
            await File.WriteAllTextAsync(Path.Combine(root, "v2-initial-ab-baseline.md"), report.ToString());
        }
        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        await File.WriteAllLinesAsync(Path.Combine(root, "v2-baseline-tests.txt"), lines);
        Console.WriteLine(string.Join("\n", lines));
        return failed == 0 ? 0 : 1;
    }

    private static void Row(StringBuilder report, string name, AgentMetricsSnapshot metrics)
        => report.Append('|').Append(name).Append('|').Append(metrics.ModelCalls).Append('|').Append(metrics.ToolCalls).Append('|')
            .Append(metrics.BytesSent).Append('|').Append(metrics.InputTokens).Append('|').Append(metrics.OutputTokens).Append('|')
            .Append((metrics.FirstModelEventMilliseconds ?? 0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
            .Append(metrics.TotalMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).AppendLine("|");

    private sealed class DirectAnswerHandler(string answer, int promptTokens, int outputTokens) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = await request.Content!.ReadAsStringAsync(cancellationToken);
            var body = JsonSerializer.Serialize(new
            {
                message = new { role = "assistant", content = answer },
                done = true,
                prompt_eval_count = promptTokens,
                prompt_eval_cached_count = 0,
                eval_count = outputTokens
            }) + "\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson") };
        }
    }

    private sealed class OneToolHandler : HttpMessageHandler
    {
        private int _requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests++;
            _ = await request.Content!.ReadAsStringAsync(cancellationToken);
            string body;
            if (_requests == 1)
            {
                body = JsonSerializer.Serialize(new
                {
                    message = new
                    {
                        role = "assistant",
                        content = "",
                        tool_calls = new[] { new { function = new { name = "read_file", arguments = new { path = "a.txt", offset = "0" } } } }
                    },
                    done = true,
                    prompt_eval_count = 25,
                    prompt_eval_cached_count = 0,
                    eval_count = 3
                }) + "\n";
            }
            else
            {
                body = JsonSerializer.Serialize(new
                {
                    message = new { role = "assistant", content = "Tool answer" },
                    done = true,
                    prompt_eval_count = 40,
                    prompt_eval_cached_count = 0,
                    eval_count = 2
                }) + "\n";
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson") };
        }
    }
}

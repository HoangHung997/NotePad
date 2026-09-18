using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using H2AgentLab.Context;
using H2AgentLab.OfficeProtocol;
using H2AgentLab.Prompting;
using H2AgentLab.Session;
using H2AgentLab.Tasking;
using H2AgentLab.Tools;
using H2AgentLab.Transport;

namespace H2AgentLab.Runtime;

public static class MbVerificationRuntimeTests
{
    public static async Task<int> Run(string outputDirectory)
    {
        var root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root))
            throw new IOException("Use a new MB-42 test directory.");
        Directory.CreateDirectory(root);

        var lines = new List<string>();
        var failed = 0;

        async Task Test(string name, Func<Task> action)
        {
            try { await action(); lines.Add("PASS " + name); }
            catch (Exception ex)
            {
                failed++;
                lines.Add("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        await Test("MB-42 tool success alone cannot complete a mutation", async () =>
        {
            var registry = new ToolRegistry();
            registry.Register(MutationTool(
                "fixture.unverified_mutation",
                "Mutate fixture without a verifier.",
                "fixture:unverified",
                (call, ct) => ValueTask.FromResult("{\"ok\":true}")));

            await using var runtime = new AgentRuntime(
                new OneToolTransport(
                    "fixture.unverified_mutation",
                    "unverified mutation fixture",
                    "{}",
                    "model-says-done"),
                new AgentContextManager(),
                registry,
                evidenceProjector: new AgentRuntimeEvidenceProjector(
                    new ArtifactStore(Path.Combine(root, "unverified-state"))));

            try
            {
                _ = await runtime.RunAsync(
                    Request("Unverified mutation.", "fixture", verify: true),
                    CancellationToken.None);
                throw new InvalidOperationException(
                    "Mutating task completed from tool success without verifier.");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("no verifier report", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("verification", StringComparison.OrdinalIgnoreCase))
            {
            }
        });

        await Test("MB-42 file mutation re-reads workspace and passes FileScopeVerifier in real runtime", async () =>
        {
            var workspaceRoot = Path.Combine(root, "file-workspace");
            var stateRoot = Path.Combine(root, "file-state");
            Directory.CreateDirectory(workspaceRoot);
            Directory.CreateDirectory(stateRoot);
            var workspace = new global::H2AgentLab.SafeWorkspace(workspaceRoot);

            var registry = new ToolRegistry();
            registry.Register(MutationTool(
                "write_text",
                "Write verified text to a workspace file.",
                "workspace.write",
                (call, ct) =>
                {
                    var path = call.Arguments.GetProperty("path").GetString()!;
                    var text = call.Arguments.GetProperty("text").GetString()!;
                    var expected = call.Arguments.GetProperty("expectedHash").GetString()!;
                    var sha = workspace.Write(
                        path,
                        Encoding.UTF8.GetBytes(text),
                        expected,
                        stateRoot);
                    return ValueTask.FromResult(
                        JsonSerializer.Serialize(new { path, sha256 = sha }));
                }));

            var router = new AgentRuntimeDomainVerifierRouter(
            [
                new FileRuntimeDomainVerifier(workspace)
            ]);
            await using var runtime = Runtime(
                new OneToolTransport(
                    "write_text",
                    "write verified text workspace file",
                    JsonSerializer.Serialize(new
                    {
                        path = "verified.txt",
                        text = "verified file content",
                        expectedHash = ""
                    }),
                    "file-verified"),
                registry,
                router,
                Path.Combine(root, "file-evidence"));

            var result = await runtime.RunAsync(
                Request("Write verified.txt.", "workspace", verify: true),
                CancellationToken.None);

            Check(result.FinalText == "file-verified",
                "File runtime did not reach verified final.");
            Check(result.VerificationHistory.Last().Passed,
                "File runtime verifier did not pass.");
            Check(Encoding.UTF8.GetString(workspace.Read("verified.txt"))
                == "verified file content",
                "Verified file content is wrong.");
        });

        await Test("MB-42 Python durable run uses PythonResultVerifier rather than exit code alone", async () =>
        {
            var workspaceRoot = Path.Combine(root, "python-workspace");
            var stateRoot = Path.Combine(root, "python-state");
            Directory.CreateDirectory(workspaceRoot);
            Directory.CreateDirectory(stateRoot);
            var workspace = new global::H2AgentLab.SafeWorkspace(workspaceRoot);

            var runId = new string('a', 32);
            var artifactBytes = Encoding.UTF8.GetBytes("python verified artifact");
            var artifactSha = global::H2AgentLab.SafeWorkspace.Hash(artifactBytes);
            var runRoot = Path.Combine(stateRoot, "runs", runId);
            Directory.CreateDirectory(runRoot);
            var run = new global::H2AgentLab.ScriptRun(
                runId,
                workspace.Root,
                0,
                new Dictionary<string, string>(),
                [
                    new global::H2AgentLab.ScriptArtifact(
                        "result.txt",
                        artifactBytes.Length,
                        artifactSha)
                    {
                        ArtifactId = "artifact:python:" + runId + ":fixture",
                        EvidenceId = "evidence:python-artifact:" + runId + ":fixture"
                    }
                ])
            {
                EvidenceId = "evidence:python-run:" + runId,
                CompletedUtc = DateTime.UtcNow
            };
            File.WriteAllText(
                Path.Combine(runRoot, "manifest.json"),
                JsonSerializer.Serialize(run));

            var registry = new ToolRegistry();
            registry.Register(MutationTool(
                "run_python",
                "Use Python script for a custom transform.",
                "python.sandbox",
                (call, ct) => ValueTask.FromResult(
                    JsonSerializer.Serialize(new
                    {
                        runId,
                        evidenceId = run.EvidenceId,
                        exitCode = 0,
                        artifacts = run.Artifacts,
                        originalFilesChanged = false
                    }))));

            var router = new AgentRuntimeDomainVerifierRouter(
            [
                new PythonRuntimeDomainVerifier(workspace, stateRoot)
            ]);
            await using var runtime = Runtime(
                new OneToolTransport(
                    "run_python",
                    "use python script custom transform",
                    JsonSerializer.Serialize(new
                    {
                        code = "print('fixture')",
                        inputs = "",
                        previous_run = ""
                    }),
                    "python-verified"),
                registry,
                router,
                Path.Combine(root, "python-evidence"));

            var result = await runtime.RunAsync(
                Request("Run verified Python transform.", "python", verify: true),
                CancellationToken.None);

            Check(result.FinalText == "python-verified",
                "Python runtime did not reach verified final.");
            Check(result.VerificationHistory.Last().Passed,
                "PythonResultVerifier did not pass durable run.");
            Check(result.VerificationHistory.Last().ReportEvidenceIds.Any(x =>
                    x.Contains(runId, StringComparison.Ordinal)),
                "Python verifier report omitted durable run evidence.");
        });

        await Test("MB-42 structured Word mutation uses LiveWordVerifier and preserves unrelated state", async () =>
        {
            var before = WordSnapshot("old paragraph", "state-before");
            var after = WordSnapshot("new paragraph", "state-after");
            var patch = new WordPatchResult(
                before,
                after,
                [0]);

            var registry = new ToolRegistry();
            registry.Register(MutationTool(
                "word.patch",
                "Apply structured Word paragraph patch.",
                "office:word:active",
                (call, ct) => ValueTask.FromResult(
                    JsonSerializer.Serialize(patch))));

            var router = new AgentRuntimeDomainVerifierRouter(
            [
                new StructuredOfficeRuntimeDomainVerifier()
            ]);
            await using var runtime = Runtime(
                new OneToolTransport(
                    "word.patch",
                    "word paragraph structured patch",
                    JsonSerializer.Serialize(new
                    {
                        sessionId = before.SessionId,
                        stateToken = before.StateToken,
                        permissionGranted = true,
                        paragraphs = new[]
                        {
                            new { paragraphIndex = 0, text = "new paragraph" }
                        }
                    }),
                    "word-verified"),
                registry,
                router,
                Path.Combine(root, "word-evidence"));

            var result = await runtime.RunAsync(
                Request("Patch Word paragraph.", "office:word:active", verify: true),
                CancellationToken.None);

            Check(result.FinalText == "word-verified",
                "Structured Word runtime did not reach verified final.");
            Check(result.VerificationHistory.Last().Passed,
                "LiveWordVerifier did not pass structured mutation.");
        });

        lines.Add($"RESULT: {lines.Count - failed} passed, {failed} failed.");
        await File.WriteAllLinesAsync(
            Path.Combine(root, "mb-verification-runtime-tests.txt"),
            lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        return failed == 0 ? 0 : 1;
    }

    private static AgentRuntime Runtime(
        IAgentTransport transport,
        ToolRegistry registry,
        IAgentRuntimeVerifier verifier,
        string evidenceRoot)
        => new(
            transport,
            new AgentContextManager(),
            registry,
            verifier: verifier,
            evidenceProjector: new AgentRuntimeEvidenceProjector(
                new ArtifactStore(evidenceRoot)));

    private static AgentRuntimeRequest Request(
        string goal,
        string scope,
        bool verify)
    {
        var criteria = verify
            ? new[]
            {
                new AgentAcceptanceCriterion(
                    AgentRuntimeDomainVerifierRouter.MutationCriterionId,
                    "Mutation must be re-observed and deterministically verified.")
            }
            : Array.Empty<AgentAcceptanceCriterion>();

        var contract = new AgentTaskContract(
            Guid.NewGuid(),
            goal,
            scope,
            null,
            ["perform fixture mutation"],
            ["preserve unrelated state"],
            ["verified result"],
            criteria,
            AgentTaskRiskClass.Medium,
            new AgentVerificationPolicy(
                requireVerification: verify,
                requiredVerifierIds: verify
                    ? [AgentRuntimeDomainVerifierRouter.VerifierId]
                    : null));

        return new AgentRuntimeRequest(
            contract,
            goal,
            new AgentPromptStablePrefix(
                AgentVersions.Current,
                "BASE POLICY",
                "SECURITY POLICY",
                "MODEL POLICY",
                ""),
            new AgentContextInput(
                TaskContract: contract.UserGoal,
                CurrentState: "MB-42 fixture"),
            PromptCacheKey: "mb42",
            MaxToolRounds: 5);
    }

    private static ToolDescriptor MutationTool(
        string name,
        string description,
        string scope,
        Func<global::H2AgentLab.ToolCall, CancellationToken, ValueTask<string>> execute)
        => new(
            name,
            new ToolNamespace(
                name.StartsWith("word.", StringComparison.Ordinal) ? "word"
                    : name == "run_python" ? "python"
                    : "files",
                "MB-42 verification fixture namespace."),
            description,
            AgentToolRisk.Medium,
            AgentToolAccess.Mutating,
            supportsParallel: false,
            schemaVersion: "v1",
            callableSchema: JsonSerializer.SerializeToElement(new
            {
                type = "function",
                function = new
                {
                    name,
                    description,
                    parameters = new
                    {
                        type = "object",
                        properties = new { },
                        additionalProperties = true
                    }
                }
            }),
            executor: new DelegatingToolExecutor("mb42-fixture", execute),
            provenance: new ToolProvenance(
                "mb42-provider",
                "1.0.0",
                "fixture",
                "1.0.0"),
            resourceScope: new ToolResourceScope(scope, scope),
            serializationKey: scope,
            canProvideVerificationEvidence: true);

    private static WordLiveSnapshot WordSnapshot(
        string firstParagraph,
        string stateToken)
    {
        var paragraphs = new[]
        {
            new WordParagraphState(
                0,
                firstParagraph,
                "Normal",
                [
                    new WordRunState(
                        0,
                        firstParagraph,
                        "DefaultParagraphFont",
                        false,
                        false,
                        false)
                ]),
            new WordParagraphState(
                1,
                "preserve me",
                "Normal",
                [
                    new WordRunState(
                        0,
                        "preserve me",
                        "DefaultParagraphFont",
                        false,
                        true,
                        false)
                ])
        };

        return new WordLiveSnapshot(
            "word-fixture",
            "Fixture.docx",
            "C:\\Fixture.docx",
            Saved: false,
            SelectionStart: 0,
            SelectionEnd: firstParagraph.Length,
            SelectionText: firstParagraph,
            Paragraphs: paragraphs,
            Tables:
            [
                new WordTableState(
                    0,
                    [
                        (IReadOnlyList<string>)new[] { "A", "B" }
                    ])
            ],
            Sections:
            [
                new WordSectionState(
                    0,
                    612,
                    792,
                    72,
                    72,
                    72,
                    72)
            ],
            Headers:
            [
                new WordPartState(
                    "section:0:header:Default",
                    [
                        new WordParagraphState(
                            0,
                            "Header",
                            "Header",
                            [
                                new WordRunState(
                                    0,
                                    "Header",
                                    "DefaultParagraphFont",
                                    false,
                                    false,
                                    false)
                            ])
                    ])
            ],
            Footers:
            [
                new WordPartState(
                    "section:0:footer:Default",
                    [
                        new WordParagraphState(
                            0,
                            "Footer",
                            "Footer",
                            [
                                new WordRunState(
                                    0,
                                    "Footer",
                                    "DefaultParagraphFont",
                                    false,
                                    false,
                                    false)
                            ])
                    ])
            ],
            StateToken: stateToken);
    }

    private sealed class OneToolTransport : IAgentTransport
    {
        private readonly string _toolName;
        private readonly string _query;
        private readonly string _arguments;
        private readonly string _final;
        private int _continuations;

        public OneToolTransport(
            string toolName,
            string query,
            string arguments,
            string final)
        {
            _toolName = toolName;
            _query = query;
            _arguments = arguments;
            _final = final;
        }

        public AgentTransportCapabilities Capabilities
            => AgentTransportCapabilities.Minimal;

        public async IAsyncEnumerable<AgentTransportEvent> StartAsync(
            AgentTransportStartRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return AgentTransportEvent.Tool(new(
                "search-" + _toolName,
                DeferredToolDiscovery.SearchToolName,
                JsonSerializer.Serialize(new { query = _query })));
            await Task.Yield();
            yield return AgentTransportEvent.Complete("search-done", "tool_calls");
        }

        public async IAsyncEnumerable<AgentTransportEvent> ContinueAsync(
            AgentTransportContinuationRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (++_continuations == 1)
            {
                if (request.NewlyLoadedTools?.Any(x => x.Name == _toolName) != true)
                    throw new InvalidOperationException(
                        "Requested tool schema was not loaded: " + _toolName);
                yield return AgentTransportEvent.Tool(new(
                    "call-" + _toolName,
                    _toolName,
                    _arguments));
                yield return AgentTransportEvent.Complete("tool-done", "tool_calls");
                yield break;
            }

            if (_continuations == 2)
            {
                if (request.ToolResults.Count != 1)
                    throw new InvalidOperationException("Expected one tool result.");
                yield return AgentTransportEvent.TextDeltaEvent(_final);
                await Task.Yield();
                yield return AgentTransportEvent.Complete("final", "stop");
                yield break;
            }

            throw new InvalidOperationException("Unexpected MB-42 continuation.");
        }

        public void Cancel() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

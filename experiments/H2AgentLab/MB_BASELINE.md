# H2 Agent — MB-00 Accepted Baseline

Status: **FROZEN BASELINE BEFORE MASTER REFACTOR**  
Captured: 2026-09-18  
Canonical architecture: `docs/H2_AGENT_MASTER_SPEC.md`  
Canonical tracker: `docs/H2_AGENT_MASTER_TASKS.md`

## 1. Source identity

- Repository: `HoangHung997/NotePad`
- Working branch: `feature/nas-multi-device-sync`
- Exact pre-Master-refactor branch HEAD: `39e873dc6955de223f4a214bef9e5076a851fd81`
- PR #1 state at capture: **open / draft / unmerged**
- Last full runtime-code CI commit: `76af0524f582bb3520b1326dc3f71240461112a5`
- Full CI run: `35330936392` — **SUCCESS**
- Workflow run number: `451`
- CI job id: `105554873968`

The baseline HEAD is three commits ahead of the CI-tested runtime commit. A GitHub compare from
`76af0524…` to `39e873dc…` shows changes only in:

- `bin/H2Notes-Avalonia-Portable-win-x64.zip`;
- `bin/LATEST.txt`;
- `experiments/H2AgentLab/AGENTLAB_V2_TASKS.md`;
- `experiments/H2AgentLab/README.md`.

No Agent Lab C# runtime source file changed between the CI-tested commit and this frozen baseline HEAD.

## 2. Current interactive runtime call graph

The normal interactive UI path is **still the v1 compatibility execution path**, wrapped by V2 lifecycle state.

Observed source path:

```text
LabWindow.Send()
  -> new AgentOrchestratedRun()
  -> AgentOrchestratedRun.RunAsync(...)
  -> AgentOrchestrator.Receive/Ground/Plan/Execute(...)
  -> AgentOrchestrator.CreateCompatibilityRunner()
  -> AgentRunner.Run(...)
  -> direct HttpClient POST to AiClient.Endpoint(...)
  -> full AgentTools.Definitions serialized on every model request
  -> legacy LabSession.Context() included in the model context
```

Source evidence:

- `experiments/H2AgentLab/LabWindow.cs`
  - constructs `AgentOrchestratedRun`;
  - calls `orchestrated.RunAsync(...)`.
- `experiments/H2AgentLab/Tasking/AgentOrchestratedRun.cs`
  - calls `_orchestrator.CreateCompatibilityRunner()`;
  - then calls `runner.Run(...)`.
- `experiments/H2AgentLab/Tasking/AgentOrchestrator.cs`
  - `CreateCompatibilityRunner()` returns the preserved `AgentRunner`.
- `experiments/H2AgentLab/AgentRunner.cs`
  - owns an `HttpClient`;
  - posts directly through `AiClient.Endpoint(...)`;
  - serializes `AgentTools.Definitions` into the `tools` request field;
  - uses `session.Context()` in its primary system context.

Therefore the Master Spec audit statement is confirmed by current source:

```text
LabWindow
 -> AgentOrchestratedRun
 -> AgentOrchestrator
 -> AgentRunner compatibility path
```

The already-built V2 transports/context/tool registry/verification components are real and tested, but they are not yet the complete normal interactive model-tool runtime.

## 3. Full deterministic CI baseline

Full CI run `35330936392` completed successfully with the following unique suite counts.
Duplicate console echoes from `Get-Content` are counted once.

| Suite | Passed | Failed |
| --- | ---: | ---: |
| H2 Notes regression | 336 | 0 |
| DesktopHost safety self-test | 3 | 0 |
| Agent Lab v1 self-test | 18 | 0 |
| V2 architecture guard | 35 | 0 |
| V2 metrics | 7 | 0 |
| Initial A/B baseline harness | 3 | 0 |
| V2 session context | 3 | 0 |
| V2 artifact store | 3 | 0 |
| V2 compaction | 3 | 0 |
| V2 context cache | 4 | 0 |
| V2 orchestrator | 6 | 0 |
| V2 tool registry | 4 | 0 |
| V2 verification gate | 6 | 0 |
| V2 closed-file acceptance | 4 | 0 |
| Phase 10 acceptance | 15 | 0 |
| Phase 11 acceptance | 15 | 0 |
| Extensibility refinement acceptance | 7 | 0 |
| DesktopHost acceptance | 10 | 0 |
| OfficeHost acceptance | 15 | 0 |
| Transport contract | 4 | 0 |
| Ollama transport | 6 | 0 |
| Chat Completions transport | 5 | 0 |
| OpenAI Responses HTTP transport | 7 | 0 |
| OpenAI Responses WebSocket transport | 6 | 0 |
| Provider resilience matrix | 13 | 0 |
| **Total** | **538** | **0** |

Additional CI outcome:

- self-contained Windows x64 publish: **PASS**;
- portable build artifact upload: **PASS**.

Known baseline limitation retained from the v1 self-test:

> Native computer UI and visual Word rendering are not certified by the v1 synthetic self-test itself.

Separate DesktopHost and OfficeHost suites provide their own deterministic acceptance coverage.

## 4. Current v1 correctness baseline artifacts

The preserved v1 baseline remains intentionally frozen for A/B comparison.

Primary correctness artifact:

- CI temporary suite: `h2-agent-v1-self-test/tests.txt`
- Result: **18 passed, 0 failed**
- Source: `experiments/H2AgentLab/LabTests.cs`

The v1 suite currently covers, among other things:

- workspace traversal/path/secret protection;
- read-only/approval behavior;
- Word structural inspection and no blind overwrite;
- PDF failure safety;
- persistent history behavior;
- Ollama tool round-trip;
- Chat-Completions fragmented tool arguments;
- thinking handling;
- truncated/incomplete tool proposal rejection;
- cancellation without retry;
- literal search evidence;
- removal of unsandboxed build execution.

Initial A/B harness artifacts:

- `h2-agent-v2-baseline/v2-baseline-tests.txt`
- `h2-agent-v2-baseline/v2-initial-ab-baseline.md`
- Result: **3 passed, 0 failed**
- Source: `experiments/H2AgentLab/Metrics/V2BaselineTests.cs`

## 5. Current v1 performance / payload baseline

This is a **synthetic deterministic harness baseline**, not a measurement of real provider speed or model quality.

Recorded in CI run `35330936392`:

| Case | Model calls | Tool calls | Bytes sent | Input tokens reported | Output tokens reported | TTFT ms | Total ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| A — raw no-tool | 1 | 0 | 86 | 5 | 2 | 15.59 | 65.62 |
| B — v1 direct | 1 | 0 | 13,782 | 20 | 2 | 2.25 | 42.28 |
| B — v1 one-tool | 2 | 1 | 28,062 | 65 | 5 | 5.97 | 54.43 |

Interpretation frozen with the baseline:

- the raw probe sends only the user prompt;
- v1 direct adds Lab policy/recovery/environment/session context plus the entire tool schema surface;
- the v1 one-tool continuation makes a second model call and resends the enlarged transcript/tool schema;
- these payload characteristics are a key migration baseline for the Master Runtime work;
- real API/local-model latency and cost still require separate measurement.

## 6. Components present but not yet the real normal UI runtime

Already implemented/tested at this baseline:

- provider-neutral `IAgentTransport` implementations;
- `AgentContextManager`;
- compaction/checkpoints;
- `AgentPromptLayout` and cache identity;
- `ToolRegistry`, `ToolSearchIndex`, `DeferredToolDiscovery`;
- `ToolExecutionScheduler`;
- verification/repair/completion components;
- OfficeHost;
- DesktopHost;
- WebResearchHost;
- AutoCAD provider contract;
- MCP provider support;
- PluginManager;
- unified/extensible skill/plugin experiments.

MB-10+ must wire these into one real runtime rather than treating their existence/tests as proof that the normal UI path uses them.

## 7. Freeze rule

MB-00 changes **no functional behavior**.

Do not delete or rewrite compatibility components during this task.

In particular retain at this baseline:

- `AgentRunner.cs`;
- `AgentTools.cs`;
- `V1ToolRegistryAdapter.cs`;
- legacy `SkillCatalog.cs`;
- `DeferredSkillSession.cs`;
- `ComputerTools.cs`;
- legacy `LabSession.Context()` path.

They may only be retired later according to the Master Tasks parity/deletion sequence.

## 8. MB-00 acceptance result

- exact pre-refactor source SHA recorded: **PASS**;
- CI status and validated runtime-code SHA recorded: **PASS**;
- current test counts recorded: **PASS**;
- current interactive call graph recorded: **PASS**;
- v1 correctness artifacts recorded: **PASS**;
- v1 performance/payload baseline recorded: **PASS**;
- functional runtime behavior changed by MB-00: **NO**.

**MB-00 baseline is complete.**

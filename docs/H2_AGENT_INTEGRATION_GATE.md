# H2M-020 — Agent Integration Gate Verification

Status: **PASS**  
Date: **2026-09-20**

## Upstream acceptance

- Agent MB-121 froze `IAgentIntegrationBoundary`.
- Agent MB-122 was explicitly approved by the user.
- Agent MB-123 authorized Phase 13 preparation.
- Latest full regression evidence used for this verification: source `cc2b03ddec6e9fd3524e376c7013426308b70ef4`, GitHub Actions run `35476297321` SUCCESS.

## Boundary mapping

| H2 product need | Accepted Agent boundary primitive | Ownership |
|---|---|---|
| start task | `StartTaskAsync` | Agent boundary |
| project grounding | `LoadProjectContext` | Agent boundary |
| progress | `ObserveProgress` | Agent boundary |
| task summary/state | `InspectTask` | Agent boundary |
| cancel | `Cancel` | Agent boundary |
| approval | `ProvideApproval` | Agent boundary |
| terminal result | `WaitForFinalResultAsync` | Agent boundary |
| evidence list/hash/summary | `InspectTask(...).Evidence` / final result | Agent boundary |
| recent project runs | task index/query maintained by H2AgentAdapter | H2 integration layer |
| optional ProjectId / quick work | nullable H2 correlation maintained by H2AgentAdapter | H2 integration layer |
| attach previously unscoped run to project | H2AgentAdapter correlation update | H2 integration layer |

The last three are deliberately **not** reasons to add model/provider/runtime APIs to the Agent public boundary. They are product correlation/query responsibilities and are implemented in H2M-030/H2M-031.

## Forbidden dependencies

H2 product code must not depend directly on:

- `AiProfile`;
- `IAgentTransport`;
- `AgentTransportFactory`;
- `AgentRuntime`;
- `AgentOrchestrator`;
- `AgentTools`;
- `ToolRegistry`;
- provider/plugin implementation objects;
- raw prompt/cache/model request types.

## Decision

The accepted Agent boundary is sufficient to begin H2 integration through one H2AgentAdapter.

No Agent Core redesign is required for Phase 13.

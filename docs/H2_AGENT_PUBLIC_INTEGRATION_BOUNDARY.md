# H2 Agent — Public Integration Boundary

Status: **MB-121 frozen boundary; not yet integrated into H2 Notes**  
Date: **2026-09-19**

## Purpose

H2 Notes must interact with Agent through one small, stable contract:

`H2AgentLab.Integration.IAgentIntegrationBoundary`.

The H2 UI must not call model transports, provider factories, ToolRegistry, AgentRuntime,
AgentOrchestrator, AgentTools, or provider/model configuration APIs directly.

Construction/composition of the concrete Agent implementation is not part of the H2 UI contract.
Phase 13 may inject an implementation after MB-122 user acceptance.

## Allowed H2 Notes calls

The complete allowed operation set is:

1. `LoadProjectContext(...)`
2. `StartTaskAsync(...)`
3. `ObserveProgress(...)`
4. `InspectTask(...)`
5. `Cancel(...)`
6. `ProvideApproval(...)`
7. `WaitForFinalResultAsync(...)`

No provider/model/transport argument is present in these operations.

## Public DTOs

H2 Notes may use only the integration DTOs in
`experiments/H2AgentLab/Integration/AgentIntegrationBoundary.cs`:

- `AgentIntegrationProjectContext`
- `AgentIntegrationTaskRequest`
- `AgentIntegrationProgress`
- `AgentIntegrationApproval`
- `AgentIntegrationEvidence`
- `AgentIntegrationFinalResult`
- `AgentIntegrationTaskSnapshot`
- `AgentIntegrationTaskStatus`

These DTOs intentionally project host-facing state instead of exposing internal runtime objects.

## Lifecycle semantics

```text
load project context
  -> start task
  -> observe progress / inspect task
  -> optional approval request
       -> H2 host grants or denies
  -> optional cancel
  -> wait/receive terminal result
       -> completed | blocked | cancelled | failed
```

A running task captures the loaded project context version at task start. Updating project context
later does not silently rewrite an in-flight task.

Only one approval may be pending per task. Approval IDs are task-scoped and stale/wrong IDs fail
closed.

Cancellation is host-owned and must propagate to the internal executor. Terminal state is visible
through both `InspectTask` and `WaitForFinalResultAsync`.

Evidence is exposed as bounded references/summary/hash projections, not raw provider/runtime state.

## Explicitly outside the H2 UI boundary

The following are internal implementation details and must not become H2 UI dependencies:

- `AiProfile`
- `IAgentTransport` / transport implementations
- `AgentTransportFactory`
- `AgentRuntime`
- `AgentRuntimeFactory`
- `AgentOrchestrator`
- `AgentTools`
- `ToolRegistry`
- provider/plugin implementation objects
- raw prompt/cache/context internals
- model-specific request/response types

## Integration gate

MB-121 freezes the contract only.

There is **no H2 Notes production integration yet**. H2 Notes source/projects must not reference
`H2AgentLab.Integration` or the Agent Lab project before MB-122 is explicitly accepted by the
user.

MB-122 remains the user acceptance gate. MB-123 may prepare Phase 13 integration only after that.

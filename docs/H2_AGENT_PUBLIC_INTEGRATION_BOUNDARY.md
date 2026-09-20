# H2 Agent — Public Integration Boundary

Status: **MB-121 frozen boundary; MB-122 approved; production composition now implemented by H2M-093**  
Date: **2026-09-21**

## Purpose

H2 Notes must interact with Agent through one small, stable contract:

`H2AgentLab.Integration.IAgentIntegrationBoundary`.

The H2 UI must not call model transports, provider factories, ToolRegistry, AgentRuntime,
AgentOrchestrator, AgentTools, or provider/model configuration APIs directly.

Construction/composition of the concrete Agent implementation is not part of ordinary H2 UI code.
MB-122 was explicitly accepted by the user on 2026-09-19. H2M-093 may therefore compose a concrete bridge at the application composition root while normal product surfaces continue to depend on `IH2AgentAdapter` and bounded DTOs.

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

MB-121 froze the public boundary before product integration.

MB-122 was explicitly accepted by the user on **2026-09-19**, and MB-123 authorized Phase 13 preparation. H2M-093 therefore performs the approved production composition through `H2ProductionAgentAdapter`.

The post-approval rule is now:

- ordinary H2 product/UI surfaces depend on `IH2AgentAdapter`, not AgentRuntime/transport/ToolRegistry/provider internals;
- the Avalonia composition root may reference `H2AgentLab.Integration` to construct the concrete bridge;
- runtime/provider internals remain inside the Agent assembly;
- no second Agent runtime/task/evidence database may be created in H2 project state.

This supersedes the former pre-MB-122 “no integration yet” source guard while preserving the frozen public-boundary intent.

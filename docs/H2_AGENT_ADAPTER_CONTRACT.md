# H2AgentAdapter Contract

Status: **H2M-030 contract**  
Date: **2026-09-20**

## Purpose

`IH2AgentAdapter` is the only Agent-shaped service H2 product code should consume.

It is intentionally an H2 product boundary, not a second Agent runtime.

## Operations

```text
StartTaskAsync(projectId?, goal, context?, readOnly)
ObserveTask(taskId, afterSequence?)
CancelTask(taskId)
RespondToApproval(taskId, approvalId, approved)
GetTaskSummary(taskId)
GetRecentTasks(projectId?, limit)
GetEvidence(evidenceId)
AttachProject(taskId, projectId)
```

## Ownership

H2AgentAdapter owns product-facing correlation/query behavior:

- optional `ProjectId`;
- recent-task query;
- task-to-project attachment;
- H2 DTO projection;
- evidence reference lookup.

The accepted Agent public boundary remains responsible for execution lifecycle:

- start;
- progress;
- approval;
- cancel;
- inspection;
- final result;
- evidence produced by Agent work.

## Forbidden dependencies in H2 product code

H2 product code must not use:

- `AiProfile`;
- `IAgentTransport`;
- `AgentRuntime`;
- `AgentOrchestrator`;
- `AgentTools`;
- `ToolRegistry`;
- provider-specific HTTP request/response DTOs;
- raw prompt/cache internals.

A concrete bridge may translate between `IH2AgentAdapter` and the accepted Agent integration boundary behind this interface.

## Data boundary

The adapter DTOs are projections only.

They must not be embedded as authoritative arrays inside `ProjectRecord` or `TaskRecord`.

Project data remains H2-owned; Agent task/evidence truth remains Agent-owned; the adapter carries bounded correlation/projection between them.

## Quick work

`ProjectId = null` is valid for unscoped Work Assistant tasks.

A task may later be associated with a real H2 project through `AttachProject` without rewriting ProjectRecord into an Agent state store.

## Testability

The contract is deliberately implementable by a pure in-memory fake.

H2M-030 regression tests cover:

- provider-neutral public signatures;
- progress/recent/evidence projections;
- nullable ProjectId quick tasks;
- later project attachment;
- approval/cancellation;
- source guard against Agent runtime/provider/tool internals.

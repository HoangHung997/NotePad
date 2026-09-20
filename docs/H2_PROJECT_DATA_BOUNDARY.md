# H2 Project Data Boundary

Status: **H2M-040 normative guard**  
Date: **2026-09-20**

## ProjectRecord owns durable project truth

`ProjectRecord` may own:

- stable project identity;
- project name;
- project notes / rich notes;
- project/user checklist items;
- project links/resources;
- durable user/project content;
- compatibility extension data;
- transitional legacy conversation history until its later migration gate.

It must not become the Agent execution database.

## TaskRecord owns human/project checklist truth

`TaskRecord` represents a user/project checklist item:

- stable task identity;
- task text/comment;
- completion state;
- deterministic timestamps;
- compatibility extension data.

It is **not** an Agent plan step, tool call, verifier result, or runtime criterion.

## Forbidden durable embeddings

Do not add authoritative collections/properties such as:

```text
AgentTasks
AgentRuns
AgentSteps
VerificationReports
ToolRuns
Evidence
Trace
ResearchResults
ApprovalQueue
AgentProgressPercent
```

to `ProjectRecord` or `TaskRecord`.

Agent execution/evidence truth remains behind `IH2AgentAdapter`.

Cross-system association stays in the correlation/query layer such as
`H2AgentTaskCorrelationIndex`, not inside project JSON.

Command Center / Workspace UI reads `H2ProductProjectionService` projections instead of
copying Agent state into project records.

## Migration rule

The existing legacy `AiConversation` history is user data and remains readable until the later
Agent-chat migration/parity tasks. Its presence does not authorize adding new Agent execution state
to ProjectRecord.

## Architecture guard

`H2ProjectDataBoundaryTests` fails if:

- ProjectRecord/TaskRecord gain Agent task/run/tool/verification/evidence/trace properties;
- a property type introduces Agent runtime/integration execution DTOs;
- required project/task durable fields disappear;
- ProjectRecord becomes dependent on the H2 Agent task summary/progress/evidence types.

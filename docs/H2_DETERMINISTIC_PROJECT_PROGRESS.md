# H2 — Deterministic Project Progress

Status: **H2M-042 rule**  
Date: **2026-09-20**

## Canonical formula

Project progress is derived only from durable human/project checklist items:

```text
completed = ProjectRecord.ChecklistItems where IsCompleted == true
total     = ProjectRecord.ChecklistItems.Count

label   = completed / total
percent = total == 0 ? 0 : 100 * completed / total
```

Implementation authority:

`ProjectProgressCalculator.Calculate(ProjectRecord)`

## Consumers

The same calculator is used by:

- `ProjectRecord.Progress`;
- H2 product / Command Center projections;
- project navigator percentage.

Do not reimplement the formula independently in new UI code.

## Agent progress is different

Agent execution may report:

- running;
- waiting for approval;
- blocked;
- verification criteria such as 4/6;
- tool/task execution progress.

That state remains behind `IH2AgentAdapter` and its projections.

It does **not** write:

- `ProjectRecord.ProgressPercent`;
- an AI-generated percentage;
- Agent execution steps into `TaskRecord`.

Example:

```text
Project tasks: 3/5 complete
Agent run: Running
Verification: 4/6 criteria passed
```

These are separate facts.

## Guard

`H2DeterministicProgressTests` ensures:

- completed/total formula is canonical;
- reading progress does not mutate ProjectRecord;
- project progress is not writable;
- no free-form AI progress percentage field appears in ProjectRecord;
- Command Center counts use the same calculator;
- navigator percent delegates to the same calculator;
- Agent execution progress cannot alter project task completion.

# AR-072 deterministic comparison

Corpus: `ar072-direct-readonly-v1`  
Iterations per engine: **20**  
Permission: **read-only / no external side effects**  
Validated source: `5582e376c43ce7b4329ecf0e87a2261d0fb948c6`

> These numbers measure deterministic in-process control-plane overhead only. They do not measure model quality, network latency, native application behavior or a real external alternative engine.

| Engine | p50 ms | p95 ms | errors | completions | missing mandatory mappings | adapter cost |
|---|---:|---:|---:|---:|---:|---|
| H2 AgentRuntime / production adapter | 190.230 | 207.617 | 0 | 20/20 | 0 | None |
| Frozen AgentRunner | 1.566 | 2.690 | 0 | 20/20 | 7 | High |

## Mandatory mapping parity

The frozen AgentRunner baseline is missing all mandatory production mappings:

- **TaskId** — stable durable task identity.
- **TurnId** — provider/user-turn identity and reconnect dedup.
- **GoalRevision** — durable requirement revision/supersession.
- **Jobs** — typed owner/poll/cancel/reconcile semantics.
- **Approvals** — typed approval identity/event boundary.
- **Events** — typed sequenced progress and event identity.
- **RestartResume** — restart reconcile/rebase without blind replay.

## External candidate

Status: **NotTested**.

No explicit external engine package/version/endpoint was supplied for this E2 task. No dependency installation, license review, credentials, network request or production integration was performed.

## Recommendation

**KEEP_CURRENT**

The frozen baseline is much faster in this trivial direct-response control-plane corpus, but it does not preserve the mandatory product contract. Latency cannot override missing correctness/state semantics.

No production switch was requested and no two engines controlled one task.

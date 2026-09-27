# AR-072 — Alternative engine comparison

Validated source: `5582e376c43ce7b4329ecf0e87a2261d0fb948c6`.

## Scope

AR-072 was explicitly selected under the user's standing instruction that implementation decisions needed to keep work moving may be treated as approved.

The E2 comparison intentionally uses:
- current engine: `H2ProductionAgentAdapter -> AgentRuntime`;
- candidate baseline: frozen same-repository `AgentRunner`;
- one deterministic read-only corpus;
- no external engine install, endpoint, credential or external side effect.

This is a compatibility/control-plane comparison, not a model-quality benchmark.

## Result

Both current and frozen candidate complete 20/20 direct-response iterations with zero errors.

Measured in-process control-plane latency:
- H2 AgentRuntime: p50 190.230 ms; p95 207.617 ms.
- Frozen AgentRunner: p50 1.566 ms; p95 2.690 ms.

Latency does not decide replacement. Frozen AgentRunner lacks all seven mandatory mappings required by the H2 product contract:

1. TaskId — stable durable task identity.
2. TurnId — provider/user-turn identity plus reconnect dedup.
3. GoalRevision — durable goal revision/supersession.
4. Jobs — typed ownership/poll/cancel/reconcile.
5. Approvals — typed approval identity/event boundary.
6. Events — typed sequenced progress/event identity.
7. RestartResume — durable restart reconcile/rebase without blind replay.

Adapter cost is therefore High.

## Decision

**KEEP_CURRENT.**

The lower direct-response latency of the frozen baseline cannot compensate for missing mandatory task/revision/job/approval/event/restart semantics.

Production composition remains `H2ProductionAgentAdapter`; AR-072 performs no production switch and never allows two planners to control one task.

A real external alternative engine remains **NotTested**. No external candidate package/version/endpoint was supplied or authorized, therefore dependencies/license/model quality/native behavior were not evaluated.

## Validation

Dedicated AR-072 run `36296856593` / job `108557354501`: SUCCESS.

- focused AR-072: 6/6
- benchmark: 20/20 current, 20/20 candidate, zero errors
- retained AR-071: 6/6
- retained AR-090: 7/7
- retained AR-082: 5/5
- retained AR-080: 4/4
- retained AR-042: 6/6
- retained AR-041: 6/6
- full H2: 1382/1382
- required Agent suites: 75/75
- all 28 exact-SHA workflow identities: SUCCESS

Full Avalonia CI `36296856547` / job `108557192819`: SUCCESS, including self-contained Windows x64 publish and packaged helper IPC.

## Acceptance boundary

E1/E2 are PASS for the deterministic same-repository comparison.

E3/E4 are NOT_RUN for any external alternative engine. No external-engine quality, production parity, native-app reliability or license-compatibility claim is made.

AR-090 is intentionally stale after AR-072 activation and must be reissued in the next task/turn. AR-083 physical two-PC/NAS remains deferred.

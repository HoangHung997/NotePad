# AR-052 real model/provider resume acceptance — deferred

Use final portable artifact **10905665137**, source SHA `a2263e47a804d2f9f386296b1330b8a1c4231e2b`.

This E4 acceptance is deferred by the user. Do not record E4 PASS until run with user-approved configured model/provider combinations.

## Required cases

1. Start a long task that has durable current requirements, at least one verified outcome, and retrievable older exact facts.
2. Resume the same TaskId on another configured model/provider using a fresh turn. The new provider must receive canonical H2 state, not the old provider's opaque continuation/response ID.
3. Switch to a smaller-context model. Confirm current requirements/outcomes/pending state survive and older exact facts remain available through scoped history retrieval.
4. Cause one selected provider to fail. H2 must not silently switch endpoint/model. Explicitly select another approved provider and resume as a fresh turn on the same TaskId.
5. With an unresolved AR-041 mutation, attempt resume. Model/provider allocation must not occur until exact-resource reconciliation clears the blocker.
6. For mutation resume, expire/revoke the prior permission. A fresh active grant must be required before any new side effect.
7. Reconnect and resend the same steering InputId. It must be ACKed without reapplying the correction or creating another task/revision.
8. Confirm prior verified evidence/outcomes remain attached, and no historical mutation is executed again.
9. Confirm old transcript text is not wholesale replayed; exact historical details are retrieved only through scoped history when needed.
10. Record untested provider/model/platform combinations explicitly.

## Evidence to capture

Capture original/resumed TaskId, old/new TurnId, selected model/profile, protocol, request metadata without secrets, canonical goal revision, outcome/evidence IDs, recovery state, history retrieval evidence and final task state.

AR-052 E2 proves serializer/runtime compatibility with Ollama, OpenAI Chat Completions and OpenAI Responses using deterministic local handlers. It does not certify any external model endpoint.

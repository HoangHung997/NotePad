# AR-011 — typed execution outcome, readiness and bounded projection

Status: **IMPLEMENTED / E2_PASS / DONE** on `38a29aa6ed8b4a2d7d717b22fa7d6b93e22c22a7`. Full CI and acceptance details are recorded below and in [acceptance.json](acceptance.json). Earlier review sections preserve the implementation chronology; their pending statements are historical, not the current status. Native E3/E4 remain NOT_RUN / AWAITING_ENVIRONMENT.

## Scope and compatibility

Existing ToolDescriptor/ToolRegistry/scheduler/AgentRuntime/production permission wrappers and H2AgentProgress stay authoritative. A small typed ToolOutcome carries host InvocationId (fresh per attempt), task-scoped LogicalOperationId, status, effect, verification, completeness, job/error/resource references. No ProjectRecord, parallel engine, evidence store, new model endpoint or NAS protocol is introduced. Legacy domain strings remain exact raw verifier inputs and artifact contents. New consumers can serialize data inside the logical envelope; AgentToolResult carries metadata out-of-band during migration. Not every legacy external provider wire protocol supports these IDs; the optional identity-aware interface forwards IDs where implemented and does not certify durable idempotency.

Success is execution, not verification. Provider verification claims cannot self-award host reports. Existing VerificationObserver and completion gates remain authoritative. Tool activity at execution time correctly reports NotRun; a later separate host-verification activity is still the authority. Running with JobId, partial and unknown effects cannot complete the task. Unknown/pending effects fence further same-resource writes in the current scheduler/runtime; this is conservative in-memory protection, NOT AR-040 job polling or AR-041 restart/reconciliation. Starting a durable background job was not added.

Errors before any effect use explicit preflight proof. Exceptions after dispatch of a mutating executor conservatively carry Unknown; IOException after a real fixture file write cannot become a generic safe retry. Cancellation is propagated, with effect metadata, and never converted to an automatic retry. Recovery advice is bounded catalog data, not executable authority. Existing closed-file preflight faults retain their legacy structured payload and guarded recovery identity.

Readiness and non-callable unavailable capability notices are in the same registry. Search separates registered/ready/exposed; configured web fetch/feed is distinct from absent search/browser backends. Provider health reads only cached state; packaged Office helper checks do not launch Office. Packaged helper means Degraded/not-probed, never proof of native Office readiness. Limits use the existing output projector/artifact store and shared Excel cardinality; bounded model projections explicitly carry completeness=false and existing artifact references. Native Office discovery/paging/partial writes remain later tasks.

## Registered E1/E2 tests

H2AgentToolOutcomeTests is wired into the existing H2Notes.Tests runner. Concrete production adapter, actual permission wrapper/registry/scheduler/runtime and H2 activity projection run on disposable temporary files with a scripted no-network transport. Cases include success without verifier, job result/replay, no-effect preflight, real write then lost response with same-batch/next-round fencing, partial effects, typed paging, malformed JSON/metadata, unavailable tool discovery, missing web configuration, denied permission, cancellation during actual executor work, canonical identities and cached provider readiness. Test-only controlled executors are injected; no Office application, paid model or personal document is used. Preserve AR-010/001 and all mandatory suites.

## Baseline and evidence boundary

Remote main: 1283bc13e07c3cd47d04886166de3dfc595422c0. Existing PR #3, branch feature/h2-agent-reliability-ar-000. Source snapshot 4b2c3d138352191d4668a23df72f7b7d489d6820 came from read-only CI artifact 10682536313; hash and clean identity verified before local edits. Container has no .NET/PowerShell or direct GitHub DNS. Windows CI is required; local diff validation is not E1/E2.

E3 native Office/provider/model and E4 real H2 UI/model/tools: NOT_RUN / AWAITING_ENVIRONMENT. AR-083: DEFERRED_BY_USER, physical E5 not certified. MB-124–127 and remaining AR tasks are unchanged. No merge into main is authorized.

## First executed evidence and review repair (historical, not final acceptance)

Run 35703236866 tested application source 4459be5d0e2a0c22e466c1b5a52785a64ce2a2e5: build PASS; AR-011 18/18 in each of three iterations, retained AR-010/001 PASS; independent required Agent suites 72/74. Both failures trace to the unchanged MB-40 denial regression: the new conservative pending-effect completion message dropped the original denial code, and the legacy adapter prioritized human error text instead of the explicit code field. Preserve the code through the typed metadata and final blocked reason; do not weaken the permission test or infer no-effect from a denial word alone. Only explicit preflight proof supports None.

Review additionally found the Work Assistant desktop ticker still rendered generic tool-ok as completed for Running/unknown/unverified metadata. It now uses the same typed projection as chat; concrete AR-011 tests compare both on actual observations. Local command timeout advice no longer recommends retry; existing process regression additionally asserts Unknown/ReconcileRequired and no contradictory retry text. These repairs needed their own exact-source tests; the accepted result below includes those tests.

First downloaded fixture evidence artifact 10683975729: SHA256 00b6256e5d44975697d78ef7cb1d937b0e53b775807f0f10058170176bb2161e, ZIP CRC verified, identity and all result logs read. This remains failed full-corpus evidence, not acceptance.

## Additional negative-control compatibility review

The existing MCP fixtures return top-level isError, whose true value is a tool execution failure (official MCP tools specification 2026-07-28, https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/specification/2026-07-28/server/tools.mdx). The common legacy adapter must normalize this inverted boolean alongside ok/success, reject wrong types or conflicting flags, preserve original content blocks and prevent false completion. Three concrete production-runtime cases exercise those negative results; no external MCP server or network is contacted. This narrow error-flag compatibility does not certify the entire current MCP protocol or native lifecycle.

## Resumed review: cancellation fence and model-visible controls

Pre-review source `4eef57a1a6fc4c0014516c1215fd1d5fd22945b3` passed the existing 22 AR-011 tests x3, 11 AR-010, 13 AR-001 and all 74 Agent suites in run 35704689725. The evidence archive was downloaded and its hash/CRC/identity/results verified; it did not certify the following new changes.

Provider-local cancellation after a mutation may leave the batch token live. The scheduler now fences the resource while still holding its gate, before rethrowing the typed cancellation. A queued/future same-resource write must not execute; an unrelated resource is not globally fenced.

Concrete transports send `AgentToolResult.Content`, not out-of-band Outcome. Cursor-bearing results now include model-visible control metadata, reserve its space within the advertised per-tool limit, and retain the exact cursor when raw output is moved to an artifact. Oversized escaped metadata is stored in the existing ArtifactStore with an explicit bounded reference; no second store or restart/job semantics are added. Raw domain bytes remain unchanged for verifier/evidence. Tests feed actual production observations through Ollama and Chat Completions serialization with an injected no-network HTTP handler.

Three additional cases and a strengthened paging case are registered in the existing runner. The Windows negative control ran these tests against the three pre-repair runtime files, then restored the exact patched source and required clean-tree positive tests. Results are recorded below. Native E3/E4 and AR-083 deferral remain unchanged.

## Accepted exact-source result

**AR-011 = IMPLEMENTED / E2_PASS / DONE** on `38a29aa6ed8b4a2d7d717b22fa7d6b93e22c22a7`. Full CI **35709818021**: all required steps passed, including H2 tests **639 passed / 0 failed**, Windows publish and packaged Office/Desktop helper startup/IPC. Focused run **35709753515** reproduced the four old-runtime gaps (**21 passed / 4 expected failures**), restored clean repaired source, then passed **25/25 AR-011 x3**, **11/11 AR-010**, **13/13 AR-001**, and **74/74 Agent suites**. Archive hashes, raw result summaries, old failure messages and exact identities were read back. Actual Ollama/Chat Completions wire serialization is exercised with no-network handlers; native model/Office is not certified.

The downloaded portable package has **109,984,499 bytes / 482 ZIP entries** with verified SHA256/CRC/nonempty helpers. Detailed steps, code identity, artifact hashes, scope and limits are in [acceptance.json](acceptance.json). The canonical handoff advances only to **AR-012 NOT_STARTED** on the same branch/PR. Native E3/E4 remain AWAITING_ENVIRONMENT; AR-083 remains DEFERRED_BY_USER. No merge authorized.

Temporary resumed-review delivery payload and writer workflow were removed in `a6ab3eb31d3b250a48aebffea71a64375bdb3fd6`; retained AR-011 validation is read-only. This status-header clarification is documentation-only and does not transfer the exact-source CI result to an unobserved later SHA.

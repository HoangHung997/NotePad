# AR-066 — explicit live/disk source decisions in the existing H2 task

This continuation addresses the source-consent core follow-up recorded by checkpoint `3b3a2f31c7e353faef6187c194f0eeb4d6eee296`. Only AR-066 is in scope. The earlier foundation evidence remains historical at its own SHA in `evidence.json`; new execution results belong only to the SHA recorded in `source-consent-evidence.json`. This document is not native Office/model acceptance.

## Production route and ownership

Both Global and Project still use `H2ProductionAgentAdapter` and `H2ProductionToolSession`. Two discoverable host tools, `resource_sources` and `request_source_change`, reuse the existing deferred tool registry, pending approval card, `RespondToApproval`, trusted user-revision identity and Agent progress journal. `H2AgentSourceDecision` is an additive typed historical projection on `H2AgentProgress`; it is not a second state/evidence database, a verifier verdict or a restored execution grant.

The model may propose a role. Only a response to the exact pending host approval can authorize that source decision, including under Full Access. The card names the task/revision, original live resource/session, previous input, exact disk path, external status, role, unsaved-state consequence, finite lifetime and separation from execution permission. Oversized cards are rejected before presenting a truncated decision. The decision must be persisted before the task-local source policy is relaxed.

## Exact-file source roles

| Role | What consent permits | What it does not permit |
|---|---|---|
| `reference` | Read the exact grounded disk file as reference material. Multiple reference files are separate decisions. | Mutation of a protected reference or replacement of the live input/completion requirement. |
| `output` | Create one exact previously absent output through a supported create-only publisher, then read that task-created file back. | Overwriting a pre-existing/newly-created file, treating the output as a replacement input, or bypassing normal mutation permission. |
| `replace_live` | Explicitly select a disk snapshot instead of the exact known original live input. The card warns that unsaved changes may be missing. | A guessed live target, arbitrary external scope, auto-saving/synchronizing the live document, clearing failed/uncertain operations, or reporting completion without reading the selected source. |
| `live` | Explicitly return to the same original live input after a disk choice. | Reusing old live/disk observations or silently selecting a new foreground document. |

The source ID is host-generated and tied to the task, original identity and current trusted authority stamp. Requests with an unknown/ambiguous original cannot replace it. Existing path grounding, secret exclusions, reparse checks, schema, ordinary read/write permission, expected-hash checks and mutation verification still run. Source consent is not a permission escalation.

## Invalidation and observation

A decision is bounded to the current task and authority revision, expires within ten minutes or the earlier existing permission expiry, and is invalidated by new accepted/queued user input. The host rechecks identity, file metadata, cancellation, deadline and authority after the approval response. Repeated identical proposals reuse the exact current receipt; denied or expired decisions do not create automatic prompt/retry loops. Decision storage is bounded to 32 entries per task. Historical decisions remain readable after restart but are not loaded as active grants.

Choosing a disk input blocks a silent return to native Office, including after expiry. Returning to live requires another explicit decision and fresh selected-session observation. Full source content reads must provide a matching actual file hash; mere approval, existence, a PDF metadata result, a later page or `check_word` structure-only result is not source proof. Completion rechecks the observed disk hash. The existing goal/effect verification gate remains authoritative: source observation alone does not prove all edits/output obligations complete.

A pending/partial/unknown effect prevents changing the input source; approval cannot erase an uncertain mutation or release the existing scheduler fence. Existing unresolved failure/recovery behavior is preserved rather than rewritten as AR-067. No automatic provider retry or native process restart is introduced.

## Evidence boundaries and remaining native gate

The new consent corpus uses actual production adapter/runtime, pending-approval API, disposable file IO and archive reopen. Model transport, native client and capture validation are declared fixtures. It checks explicit allow/deny/cancel, foreign/duplicate approval IDs, source identity, scope/secrets, changed files, trusted revisions, expiry, read-only permission, reference/output roles, input reselection, incomplete observations and a single lost-response fixture effect. These are E2, not actual H2 UI/model/native acceptance. Existing AR-012/020/065 and full-suite regression must also pass on the exact integrated SHA.

Source resolution remains conservative for ambiguous or multiple live application intents. The consent route handles an exactly host-bound original live input plus separately approved disk references/outputs; it does not claim a general natural-language resolver or implement new live CAD/browser providers. Unsupported formats/partial readers cannot manufacture full-source proof. Ambiguity and unsupported native routes remain explicit blockers, not permission to choose an arbitrary source.

Real Word unsaved state, Excel selection/multiple views, same-object transient native reconnect, actual approval rendering and user-visible H2/model behavior still require the authorized isolated E3/E4 corpus in `real-native-acceptance.md`. No live model call, personal document mutation or physical two-PC/NAS test was performed. AR-065 E4, AR-064 PARTIAL and all earlier acceptance debts remain; AR-083 is DEFERRED_BY_USER. No AR-067 work is included.

## Delivery history

The first exact-source delivery run `35902491484` stopped before commit/push: Git rename detection represented three staged text-to-source moves as renames, causing a complete-path inventory assertion to fail. Local reproduction confirmed 13 displayed paths versus 16 additions/deletions, with every source postimage hash correct. The repaired inventory uses `--no-renames`, keeps the exact source/preimage hashes and verifies the explicit repair parent. Run `35903048903` then integrated runtime/test commit `4c6a96ee39116c431dcd7d69a620c6c4f5bb94e2` and removed its own staging files/writer through a normal fast-forward push. The first failure is retained, not relabelled PASS. The earlier blocked old-source differential experiment was not retried.


## Fixture reconciliation repair

The first integrated Windows source `6ac81cb8cd18b6d8124bdd97fc3b9bc541e7b78a` compiled with 38 warnings and no errors. Full run `35903224947`, job `107324343119`, failed only the two new Global/Project `unknown-effect` cases at their restart assertion: 1,264 passed / 2 failed. The test incorrectly required the original runtime error text to equal the archive restart error. Existing `AgentIntegrationTaskArchive.Get` deliberately presents durable Unknown/PartiallyApplied/dispatched effects as Blocked with `ReconcileRequired`; the original live error is not restart authority.

Commit `2f2dda5f6207f948ed56e30ca5cd2c50cb811948` changes only this test, not the archive/runtime. It retains exact status, decision receipts, provider allocation/request counts and every file/hash assertion. Unknown-effect cases additionally require the existing authoritative reconciliation projection, the exact original invocation/logical operation identity, result state, unknown effect/error code, argument/output hashes and unchanged one-effect fixture marker. The original typed failure must still exist in replayed progress. Per-case receipts now expose original/reopened errors, recovery records and effect hashes rather than concealing the difference in a combined assertion. Other terminal cases still require exact error equality. New results must be verified on this commit, not inferred from the first run.

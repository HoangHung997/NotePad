# AR-080 — Long-work recall/restart production corpus

Validated code: `3ec3ef86f99e796b0ee3dbb5f1de4e061dbec73c`.

## Scope

AR-080 is a production-path E2 corpus/gate. It does not add another runtime, recovery store, model transport or Office implementation.

The corpus uses the real H2 production adapter, Agent journal/history, typed goal revisions, restart/reconcile/resume, RuntimeCompactionCoordinator, request budgeting and the real ChatCompletions serializer with intercepted HTTP. Native Office and model behavior are intentionally synthetic for E2 and are labelled accordingly.

## Boundary groups

Each group runs three independent repetitions.

1. RC-33 golden crash-after-write restart:
   - interrupted task is ReconcileRequired;
   - resume is blocked before reconciliation;
   - exact resource postcondition is reconciled;
   - fresh resume keeps the same TaskId/current revision;
   - already-applied DOC-A title is not duplicated;
   - near-name DOC-B remains byte-identical;
   - missing PDF keeps the task blocked.

2. Changed requirement + compaction recall:
   - old title requirement is superseded;
   - new requirement remains verified;
   - exact early-source marker is retrievable after compaction/restart;
   - one compaction and five request-budget receipts are retained.

3. Missing PDF completion gate:
   - model final text cannot bypass the pending PDF outcome;
   - task remains Blocked with 2/3 outcomes verified;
   - no PDF is fabricated.

4. UNSAVED-only live source:
   - exact live Word session/current revision is retained;
   - resource_sources plus word.get_active_document reobserve the intended live source;
   - near-name DOC-A control remains unchanged;
   - all five request-budget receipts are retained.

## Validation

Dedicated AR-080 run `36260548933` / job `108455390604`: SUCCESS.

- AR-080 focused: 4/4.
- Corpus JSON files: 12.
- AR-063: 5/5.
- AR-062: 6/6.
- AR-060: 6/6.
- AR-052: 8/8.
- AR-051: 37/37.
- AR-042: 6/6.
- AR-041: 6/6.
- AR-024: 2/2.
- AR-033: 42/42.
- Full H2: 1352/1352.
- Required Agent suites: 75/75.

Full Avalonia CI `36260548852` / job `108455418228`: SUCCESS, including self-contained Windows x64 publish and packaged DesktopHost/OfficeHost startup+IPC.

All 23 pull-request workflow identities on the exact code SHA completed SUCCESS.

## Acceptance boundary

E1/E2 are PASS. Real E4 remains DEFERRED_BY_USER / AWAITING_ENVIRONMENT until the user tests the final build with the H2 UI, configured allowed local/cloud model(s), native Word/Office and genuinely long work. The deterministic three-repeat corpus is not a native Office, live-model or multi-hour-session certification.

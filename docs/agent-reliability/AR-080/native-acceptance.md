# AR-080 E4 long-work/native acceptance — deferred

Use final portable artifact **10912351776**, source SHA `3ec3ef86f99e796b0ee3dbb5f1de4e061dbec73c`.

This acceptance is deferred by the user. Do not record E4 PASS until the following is run on an authorized Windows machine with the real H2 UI, configured allowed model/provider and native Office. Use disposable documents only.

## Real long-work corpus

Run local and cloud providers separately if both are configured/allowed. Do not merge their results into one success percentage.

1. Prepare two near-name Word documents: DOC-A and DOC-B. DOC-B is the untouchable control.
2. Open DOC-A in Word and create at least one UNSAVED-only change that does not exist on disk.
3. Ask H2 to perform a multi-step job containing:
   - a title/content change on DOC-A;
   - preservation of DOC-B;
   - a PDF output that must be independently inspected before completion.
4. During the task, submit a user correction that supersedes an earlier requirement. Confirm the active goal revision changes once and the old requirement is not revived.
5. Drive enough conversation/tool history to trigger real compaction/retrieval. Confirm an exact early fact/source remains recoverable, not merely a semantic guess.
6. Interrupt H2 after a write may have happened but before the response/result is safely persisted. Restart H2.
7. Confirm the task becomes Interrupted/ReconcileRequired; do not replay the mutation.
8. Reobserve/reconcile the exact live resource and then choose Continue. Confirm:
   - same TaskId/canonical goal state;
   - already-applied work is not duplicated;
   - DOC-B remains unchanged;
   - UNSAVED live source remains live-only and is not silently replaced by an older disk file;
   - pending PDF outcome still blocks completion until produced and verified.
9. If the model says "done" while PDF/preservation/other required outcomes are missing, host completion must remain Blocked/PartiallyCompleted.
10. Continue for a genuinely long working session appropriate to the user's normal usage; record real request usage, compaction/retrieval overhead, blockers, duplicate/unintended side effects and repeatability.

## Evidence

For each provider/run retain: build SHA, provider/model identity without secrets, task/revision IDs, request budget receipts, compaction/retrieval receipts, resource identities, before/after hashes, PDF artifact/readback evidence, restart boundary, reconciliation receipts, completion state and any blocker.

The E2 corpus repeats each synthetic boundary three times, but that is not evidence of multi-hour native/model stability.

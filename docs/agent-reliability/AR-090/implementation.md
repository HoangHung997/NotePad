# AR-090 — Final reliability audit and handoff (post-AR-072 reissue)

Validated code: `ed9096e92898ddc29b50aaf7ce2f50b96586ff35`.

AR-090 is a parity/audit/handoff task. It does not replace the AR specification, add a new runtime, convert deferred native/live acceptance into PASS, or start AR-083.

## Audit result

- focused final handoff audit: 7/7
- retained AR-072: 6/6
- retained AR-071: 6/6
- retained AR-082: 5/5
- retained AR-081: 6/6
- retained AR-080: 4/4
- retained AR-042: 6/6
- retained AR-041: 6/6
- retained AR-062: 6/6
- retained AR-060: 6/6
- full H2: 1382/1382
- required Agent suites: 75/75
- exact-SHA workflows: 28/28 SUCCESS
- full Avalonia CI + self-contained publish + bundled Python + clean local-profile package verification + helper IPC: PASS

AR-072 remains **KEEP_CURRENT**: the frozen AgentRunner baseline misses seven mandatory task/revision/job/approval/event/restart mappings. A real external alternative engine is **NotTested** because no user-authorized external candidate/package/version/endpoint was supplied.

AR-083 remains **DEFERRED_BY_USER / E5 NOT PASSED**. Per-task native/live acceptance debts remain exactly as labelled in the canonical tracker.

## Final package

Artifact: `10924104516`.

- bytes: 175,504,139
- SHA256: `957b25f9c700b3b8bc2efec22036058f8e50525ff176b9f22b951f11aa0c2086`
- ZIP entries: 2,970
- manifest source SHA: `ed9096e92898ddc29b50aaf7ce2f50b96586ff35`
- manifest files: 2,969
- manifest total bytes: 415,263,768
- manifest content digest: `cf63cb2e4f7f9be4ec625119b03627e4cb3070e7b03a4391d4d9f494d64ceef8`
- every manifest file size/SHA256 independently checked: PASS
- no listed manifest file missing or extra: PASS
- H2Notes/DesktopHost/OfficeHost executables present: PASS
- bundled Python runtime present: PASS

Evidence artifact: `10924793729`, 432,426 bytes, SHA256 `91396518fd0845c1c2dabb24ce6011eb005bc4ca0844935ae4c37d50f7c56eff`, ZIP integrity PASS with 653 entries.

## Handoff boundary

Allowed label: **IMPLEMENTATION_READY_FOR_USER_TEST**.

Not claimed: PROJECT_COMPLETE, E5 PASS, AR-083 PASS, universal native Office/CAD/model/search/browser E4, generic exactly-once GUI/COM semantics, distributed NAS/resource lock certification, real external alternative engine parity, or universal provider compatibility.

No new implementation task is started by this handoff. If the user reports a regression, reopen the owning AR. AR-083 starts only on explicit user request.

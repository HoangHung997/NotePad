# AR-066 — real native/H2 acceptance (NOT EXECUTED)

This runbook does not certify acceptance. Use the existing AR tracker, AR specification and issue 2 of `../../H2_AGENT_CRITICAL_USER_REPORTED_ISSUES_2026-09-23.md`. A scripted `IOfficeWindowProbe` or production adapter with scripted model/native client remains E1/E2, not E3/E4.

## Environment and authority

Use an authorized isolated Windows/H2 instance and a new disposable local workspace, separate from the user's working install, profile, project and NAS. Do not close or restart the user's applications, alter the user's working tree, deploy to main, export credentials, or run against personal documents. Record exact source SHA, binary hash, Windows/Office version and process identity. Real model calls require an already authorized profile and an explicit finite operator budget; no credentials in chat. No authorized environment means `AWAITING_ENVIRONMENT`, not a new provider or a simulated pass.

Do not start a second execution when the first one's result is uncertain. Inspect the existing journal and the exact bound resource/postconditions first. Preserve all failures and record skipped cases.

## Disposable native corpus

Prepare two same/similar named Word documents and Excel workbooks using test-only data. Give each source, control paragraph/cell and view a distinct marker. Include both saved bytes and a distinct `UNSAVED-ONLY-AR066` live edit that deliberately does not exist in the saved disk file. Store initial disk hashes and observed native content versions without personal content. Keep unsaved and disk resources separate in the evidence.

| Case | Required native observation |
| --- | --- |
| Word live unsaved | Actual Word native attachment observes the unsaved marker, pins the same document and view, applies one bounded permitted edit, and reads it back. Disk-only evidence is insufficient. |
| Excel live selection | Capture exact workbook, sheet and range; read the unsaved cell, mutate only the granted range, and read back both target and untouched control cells. |
| Multiple instances/views | Similar names and multiple windows must not produce guessed uniqueness. Ambiguous discovery blocks until an exact host target is selected. |
| Focus change | Switching focus to another document cannot redirect a pinned read, mutation or verification. Keep PID/start/root/view/document IDs in the receipt. |
| Transient same-object loss | Under an operator-controlled harmless busy/modal condition, a failed probe returns unavailable/incomplete, not cached content. After the condition genuinely changes, an explicit reprobe recovers the same native object/session or reports it cannot. No blind automatic write retry. |
| Changed identity | Save As, close/reopen, document replacement, PID/start reuse or changed view must not resurrect an old binding. Rebind explicitly; do not use old version/selection tokens. |
| Forbidden disk substitution | After an actual native attach failure, a `read_file`/file write or Python input targeting the live source is rejected before any file body/effect. Permission escalation alone does not change source semantics. |
| Trusted user source revision | A later user request for live content must restrict subsequent admission and completion, including a queued batch whose last message is ordinary text. Tool/file text must not change the requirement. Source IDs/revisions must survive reopen without replay. |
| New explicit saved-file request | A separate request explicitly choosing the saved disk snapshot works through ordinary file permissions/verification, without describing it as the unsaved live resource. |
| CAD/browser readiness | In the current composition, absent live drawing/tab providers must be reported honestly. Core Console/disk DWG or HTTP fetch cannot count as current SelectionSet/logged-in tab access. |
| Restart/no replay | Reopen the same completed or blocked test task and inspect state/error/evidence. Observation must allocate no provider and dispatch no file/native write. |

Exercise the corpus in both Global and disposable Project surfaces. A helper ping, pure COM probe, or successful file-only export does not substitute for the full user-visible H2/model route required by E4. If a real transient fault cannot be safely induced, mark that case unrun; do not manipulate the user's Office processes or fabricate a recovery receipt.

## Evidence and exit

Keep source and control hashes, exact expected/observed native markers, provider/session/PID/process-start/root/view/document identity, source kind, selection and content version, dispatched invocation IDs, mutation effect and readback verdict in the existing local journal/artifact convention. Record whether the data came from live native state, a disk file, or partial accessibility. Screenshot only the isolated fixture, never credentials/personal content. A safe error with `live_resource_required` is a refusal, not successful completion of the original live edit.

The core implementation deliberately does not add a second provider/store, unrestricted command fallback, or same-task semantic-consent workflow. Natural-language source admission is conservative, not a complete per-resource role resolver. Native save-copy is an output, not permission to substitute the input. Trusted user revisions can add restrictions through the existing source journal; they cannot silently waive a live requirement. Per-resource mixed-source consent and same-task live-to-disk approval are still not implemented. Keep these limitations and any in-turn target change explicit instead of silently broadening scope.

AR-066 stays open until implementation gaps and the required native E3/real H2 E4 corpus are resolved with exact-SHA evidence. AR-020 E3 and AR-065 E4 remain open independently; AR-064 remains PARTIAL; AR-083 remains DEFERRED_BY_USER.

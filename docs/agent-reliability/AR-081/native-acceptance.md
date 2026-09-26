# AR-081 native UI / accessibility acceptance — deferred

Use final portable artifact **10913361711**, source SHA `46ec1218d535fa0def938f9393e13e40a705605d`.

This E4 acceptance is deferred by the user. Do not record E4 PASS until it is run on an authorized native Windows desktop with the final build.

## Required real-UI checks

1. Open both Global Work Assistant and Project Agent; confirm the approved layout remains intact.
2. Run/observe examples of Queued, Running, WaitingForApproval, Compacting, WaitingForJob, Reconnecting, ReconcileRequired, Interrupted, AppliedUnverified, Verified, CancelRequested and Blocked states where available. State text must match host facts, not model prose.
3. Generate long activity with many heartbeat/job-poll events. UI must stay responsive and grouped; reopening the thread must show the same durable event identity/count without creating duplicate visual rows.
4. Scroll upward while activity streams. The viewport must not jump to bottom; the latest-activity action must be reachable with keyboard and return to follow mode.
5. Reopen/reconnect the same thread repeatedly. No tool may run because the UI was opened; no duplicate final answer/progress/task card may appear.
6. Exercise approval allow, deny and an expired/stale approval. Stale approval must remain visibly invalid and must never look like success.
7. Open a large CSV/XLSX artifact. Page/sheet/formula controls must remain responsive and must not eagerly create controls for the entire file.
8. Verify Word/PDF artifact preview zoom/page controls on a native window. This is UI inspection only; it does not upgrade document semantic verification.
9. Test keyboard-only navigation and visible focus through task state, approval, latest activity and artifact controls.
10. Test Vietnamese/Unicode IME composition in the real composer; composition must not duplicate text, lose focus or send prematurely.
11. Test at representative Windows scaling/DPI values and small/large window sizes. Record any clipping, overlap or unreadable controls.
12. Use a supported screen reader or Windows accessibility inspection client to verify meaningful names/states on key Agent controls.
13. With a configured allowed model/provider and a real long-running task, verify UI liveness during streaming, compaction, job wait, reconnect and recovery.
14. Confirm no hidden chain-of-thought is shown. Public commentary, tool activity, progress summaries and host evidence are allowed.

## Evidence

Capture exact build/source SHA, Windows version, scaling/DPI, input method, accessibility client if used, screenshots where relevant, task IDs/status transitions, reconnect count, observed duplicate count, and any responsiveness measurements.

Headless CI timing is an E2 proxy only; it is not native FPS or native accessibility certification.

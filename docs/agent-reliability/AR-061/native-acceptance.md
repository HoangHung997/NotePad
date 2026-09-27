# AR-061 native acceptance — File Explorer / Word / Excel / AutoCAD

Status: **AWAITING_ENVIRONMENT**. This checklist is for the final portable built from `779a92bc0027be8c6194edf1fd00c1c132ae8d2e`.

## Build identity

- Artifact: `10894338034`
- Name: `H2Notes-Avalonia-Portable-win-x64`
- SHA256: `12d0997324ebbf1fadc206a5874bae861ec4172a5869f4cfdafa7547d1426109`

Do not use an older AR-021 portable when evaluating AR-061.

## Required native checks

1. Start H2 Notes from the final portable on the authorized Windows PC.
2. Use a mutation-capable Work Assistant permission preset. For `AskBeforeChanges`, confirm H2 asks before launching/activating; `ObserveOnly` must block mutation.
3. Ask H2 to open File Explorer. Confirm the requested safe window is actually observed and H2 does not claim success before it exists.
4. Ask H2 to open a blank Word application and a blank Excel application. Confirm H2 can start or reuse one exact safe window without needing a document file path.
5. If AutoCAD is installed, ask H2 to open/activate AutoCAD. A slow cold start may take longer than Word/Excel but remains bounded.
6. With exactly one matching app window already open, repeat the open request. H2 may reuse that exact observed target without a false failure.
7. With multiple matching windows open, confirm H2 does not silently choose a different target. It must use exact observed identity or report ambiguity.
8. Close/reopen the application and retry. A stale prior session must not be treated as the new window.
9. If a requested app is not installed/registered, confirm H2 reports a bounded app-not-found blocker rather than launching an arbitrary substitute.
10. Do not weaken safety policy or test credential/password-manager targets just to obtain a green result.

## Evidence to retain

For each failure retain: exact user prompt, permission preset, app name, visible H2 error/progress, source/build SHA, and whether the app was closed, single-window or multi-window before the request.

Passing this checklist may establish the native portion of AR-061 E3/E4. Until that evidence exists, AR-061 stays `[~]`.

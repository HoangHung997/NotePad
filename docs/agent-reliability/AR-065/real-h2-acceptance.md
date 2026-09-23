# AR-065 — real H2/OpenAI acceptance still required

This is a runbook, **not executed acceptance**. Canonical requirements: `../../H2_AGENT_CRITICAL_USER_REPORTED_ISSUES_2026-09-23.md`, issue 1, and the AR tracker. The original user HTTP 400 response has not been captured with the new diagnostics. Wire-contract defects reproduced by scripted providers do not prove the sole cause of that original incident.

## Environment and permission gate

Use the existing work branch and the exact successfully tested source SHA recorded in `evidence.json`; inspect HEAD/diff first. Do not deploy to main, replace the user's working install, close their apps, or modify their working tree. A separate portable build and a disposable local H2 workspace are preferred. Use only the user's already configured and authorized OpenAI Responses profile with exact model `gpt-5.6-luna`. Keep endpoint/model/reasoning settings unchanged and record their non-secret values. Never export a key, auth header, complete profile, personal chat history or screenshots containing private content.

Before any real provider call, obtain a finite test budget and operator permission for the isolated directory. No provider credentials are requested in chat. If no authorized Windows/H2 environment or profile is available, leave `AWAITING_ENVIRONMENT` and execute no live request. A hosted Windows CI runner with scripted HTTP is E2, not E4.

A suggested bounded session is two tasks per surface (Global and disposable Project), at most six model requests per task and a five-minute operator stop deadline per task, subject to the user's lower limit. These are test-run limits, not a claim that a new app budget control exists. Do not auto-retry failures. If the operator cannot observe/enforce the selected budget, do not start the session.

## Dedicated fixture and cases

Choose a new empty local folder with an exact unique run ID. Place only `source.txt` containing `AR065-LIVE-READ-ĐÚNG` and `control.txt` containing `UNCHANGED` there. Record both files' hashes before the test. Never use an existing personal/project/NAS folder. Record app/binary hash, OS, selected protocol/model/effort, source SHA, surface, thread/task IDs and UTC boundaries without secrets.

| Case | Request through the real H2 UI | Required observation |
|---|---|---|
| Read-only | Read the exact `source.txt` in the chosen directory and return its content; do not modify anything. | `tool_search` and a deferred schema load followed by actual tool execution, matching function-call output and final continuation; observed text equals the file, both hashes unchanged. A text-only model answer is insufficient. |
| Harmless creation | Create a new uniquely named text file only in the chosen directory with content `AR065-LIVE-CREATED-ĐÚNG`, then read it back. Do not overwrite anything. | Real host permission and one creation, independent readback, observed evidence and final UI result; source/control hashes unchanged and no unrelated output. |

Repeat both cases in Global and a disposable Project. If the model chooses a valid route without deferred tool discovery, record that route but do not mark the deferred-load requirement passed; ask for a bounded discovery-based read as a separate authorized case. To reproduce the specific reported Excel example, add a small workbook creation case only after the minimum cases pass and within the remaining authorized budget; unsupported export is not an AR-065 transport PASS.

## Failure, restart and evidence

On HTTP failure retain the bounded host error fields (status, request phase, allow-listed code/type/parameter), exact request phase and available request/tool IDs. Do not enable raw payload/auth-body logging. Keep request/tool evidence under the existing local Agent journal; do not add another evidence engine. Do not call a new request to retry an uncertain mutation. Inspect its existing journal, output existence/hash and postconditions first.

After a terminal result, close only the disposable H2 test instance with operator permission, reopen the same test workspace and inspect that task. Confirm status/error/final/evidence persisted and no provider call or file write happened merely from reopening. Do not interrupt or kill the user's actual Office/H2 sessions.

Store a sanitized per-case manifest and UI observations under the existing AR artifact convention. Record observed values, not just `ok=true`. Include the exact test SHA, dirty/clean status, expected/observed outputs and hashes, provider/model/protocol, budgets, actual request count, effect/verification status, screenshots limited to fixture content, and each skipped case.

## Exit criteria

AR-065 may be DONE only after the required E2/full CI plus the real-H2 read, harmless creation and deferred-load requirements succeed with auditable evidence. Any missing environment or unverified provider rejection keeps it open. A fixture, helper ping, portable publish, curl-only request or concrete adapter with a fake provider is not the real H2/model gate. AR-064 remains PARTIAL; AR-020/033 native and AR-051 model acceptance debt remains unchanged; AR-083 is `DEFERRED_BY_USER`.

## Official contract references checked 2026-09-23

- OpenAI function-calling guide: explicitly opting out with `strict: false` preserves non-strict optional-field semantics; Responses reasoning items must be included in stateless tool continuation. https://developers.openai.com/api/docs/guides/function-calling
- Exact Luna model capabilities and supported effort values: https://developers.openai.com/api/docs/models/gpt-5.6-luna and https://developers.openai.com/api/docs/guides/deployment-checklist

The fixed wire mapping is provider-boundary behavior, not a registry rename, permission relaxation or change of endpoint/engine.

# Saved AI connections: live diagnosis and fixes

Date: 2026-09-15. User explicitly requested live testing of the saved local and
online connections after exiting H2 Notes. Tests used only the canned prompt
`Reply with exactly OK. No explanation.` No project content, existing conversation
or document was loaded/sent. No subscription, billing or credential changes.

## Live results

| Connection/model | Models API | Chat result |
| --- | --- | --- |
| Saved Ollama localhost / minimax-m3:cloud | 200; model listed | HTTP 402, twice: original client and improved error classification. This is cloud, not local inference. |
| Saved Google / gemini-2.5-flash-lite | 200; model listed; saved DPAPI key usable | HTTP 404 NOT_FOUND, twice. Provider message matches "no longer available to new users". |
| Same Ollama connection / installed gemma3:4b, temporary override only | 200 | HTTP 200, NDJSON stream completed, reply matched OK; 12.8 s including initial model load. |
| Same Google connection/key / gemini-3.1-flash-lite, temporary override only | 200; model listed | HTTP 200, SSE stream completed with 3 text characters; 4.2 s. Exact-OK check was false; connectivity/stream completion passed. |

Six total short generation attempts; failed saved-model calls were deliberately
retested once after improving classification. No automated retries, project-data
fallback, or broad model sweep. Existing installed Ollama models only; no download.

Google staff also describe the 2.5 access restriction for new users and recommend
newer models in this [official forum response](https://discuss.ai.google.dev/t/auth-key-can-list-models-but-generatecontent-returns-http-404-not-found-for-gemini-2-5-flash/180197/2).
The distinction between Ollama local and cloud follows the [Ollama documentation](https://docs.ollama.com/cloud).
HTTP 402 alone does not establish the account balance or purchased plan; no account
or billing page was accessed and no exact balance is claimed.

Saved defaults remain `minimax-m3:cloud` and `gemini-2.5-flash-lite`. The user was
asked whether to switch to working alternatives; no answer had arrived when this
report was written. Temporary probes never save their model overrides. Therefore
the original defaults can still receive the same provider-side errors until the
user chooses an accessible model or resolves the provider restriction.

## App fixes

- HTTP errors now have safe, actionable categories for 401/402/403/404/429/5xx.
  Known Gemini new-user and extra-usage restrictions are recognized. Bounded
  parsing never exposes raw server bodies, keys or echoed prompts in the UI/log.
- The failure explanation is saved with the failed message and displayed in its
  bubble on reopening. Failed answers remain excluded from AI request history.
- A completed response with no text is no longer reported as an empty success.
- Chat labels identify Ollama Cloud separately from local inference and remote
  Ollama hosts. Cloud models cannot be "loaded into local RAM" by the load action.
- AI settings include an explicitly confirmed short test, separate from listing
  models, with a warning about API/cloud charges and no project/history transfer.
- Fixed stale connection references: saving a model/profile now updates open chat
  panels. Active requests are stopped before applying changes, preserving partial
  replies and drafts. Saved timeout is retained by the settings editor.

## Verification and rollout

- Release solution build: **0 warnings, 0 errors**.
- Full automated suite: **113 passed, 0 failed**.
- Test evidence: `C:/Users/hoang/AppData/Local/Temp/H2Notes-tests-189f6c0139364c6e9ff089c9dbf9f9e6`.
- Live diagnostics opt-in: `tests/H2Notes.Tests/LiveAiProbe.cs`. Never runs during
  normal tests; requires explicit saved profile IDs and `--allow-live-ai`.
- Three actual Release control renders here use fabricated failures and random
  credential-free profiles. Inspected the Gemini failure and settings captures:
  message is legible, send remains inline, failure persists in the bubble, and
  the new test action fits. Rendering was 96 DPI, native scaling 1.25.
- These captures are RenderTargetBitmap, not native screenshots. The documented
  computer-use window listing again returned no H2 Notes target after launch;
  native mouse/tray/IME interaction remains unverified. Live tests used the same
  production AiClient, not native clicking of the Send button.
- Owned isolated demo process 24860 was stopped. Correct regular app restarted
  from `src/H2Notes.Avalonia/bin/Release/net10.0/H2Notes.Avalonia.exe --show --show-ai`.
  PID **16564**, responding; exactly one H2 Notes process at the final check.
- After restart: schema 3, 21 indexed data files, all hashes valid. Configuration
  and encrypted credential file hashes are unchanged across the restart. Tests
  did not write canned messages into the user's projects or old standalone chat.

The project-bound independent window correction from the previous turn is now
deployed as well. Provider access/payment restrictions are not claimed fixed by
this app update; the alternative local and online models were tested separately.

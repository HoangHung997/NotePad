# Image OCR and thinking follow-up

2026-09-15, codex/project-sheet. Scope: the two reported failures, not a redesign.
WPF and _ver2 are untouched. All ten baseline PNG hashes remain unchanged.

## Diagnosis

- The attachment in the reported failed chat is PNG, not PDF. The existing PDF
  selector only prepared AiTurn.Files; PNGs travelled through AiTurn.Images and
  never reached MinerU. This was a missing image-OCR path, not proof of a PDF failure.
- Local Ollama 0.34.0 advertises vision for gemma4:latest (8B Q4_K_M). A bounded,
  direct /api/chat request included all 23,594 original image bytes in base64.
  It completed but returned unrelated text, not OCR. This reproduces a vision
  failure independently of H2's chat UI; it does not establish the exact backend cause.
- An analogous upstream report exists in [Ollama issue 16532](https://github.com/ollama/ollama/issues/16532).
  The payload follows [Ollama's vision API](https://docs.ollama.com/capabilities/vision).
  No model, server installation or online provider was changed as a workaround.
- Thinking had an inner ScrollViewer capped at 150 DIP with no follow logic;
  following the outer conversation did not advance that inner scroll position.

## Implementation

- Explicit machine-local OcrImages toggle in AI settings; old settings default off.
  The user selected enabling it. After the old process exited, the local config was
  backed up and this flag enabled, retaining MinerU and the existing 600-second limit.
- Validated PNG/JPEG/WebP is wrapped in a temporary lossless one-page PDF for the
  installed engine. No new downloads. Single-frame only, 20M pixel/14,400-per-axis
  bound, input/output size limits, source untouched, temporary PDF cleaned on failure.
- New image attachments retain original bytes/name/hash alongside extracted text.
  Their requests and later history use OCR text, not the failing vision path.
  Image preview has a separate OCR text button. Original older images in the same
  conversation are converted per request without rewriting stored history.
- Failure/cancel prevents an incomplete request and preserves draft/attachments.
  Direct-image mode is still available. No provider fallback or API secrets in data.
- Thinking follows after measurement/extent changes and after updated same-height
  rolling text, expansion and resize. It remains transient and clears on first answer,
  error or cancellation; manual scrolling of old conversation history is unchanged.

## Verification

- Release build: 0 warnings, 0 errors (build.txt).
- Release apphost test run: 313 passed, 0 failed (tests.txt).
  An earlier DLL invocation had one harness failure because a restart test uses
  Environment.ProcessPath and relaunched dotnet without its DLL; preserved in
  tests-dll-invocation.txt. Running the intended test apphost passed unchanged.
- Bridge contracts: 11 passed, including image orientation/pixels, source bytes,
  animation/corruption/oversize rejection, cleanup, offline policy and PDF limits.
- Real Core pipeline, installed CPU MinerU: user's PNG -> 263 Markdown characters,
  14.5 seconds; original bytes preserved, Markdown present in request, binary absent.
- Real Core pipeline, installed CPU MinerU: synthetic Vietnamese scanned PDF ->
  300 characters, 15.0 seconds; provenance present, original PDF not sent.
- Real Core pipeline, installed CPU Docling: same PNG -> 274 characters, 17.3 seconds.
- Local Gemma4 with MinerU Markdown (no image bytes) returned the supplied extracted
  text instead of asking for an image, completing in 26.6 seconds. No paid/online API
  was called. Live workspace chat/history was not modified for these probes.

Accuracy is NOT certified: MinerU lost many Vietnamese accents in this screenshot;
Docling retained more accents but misplaced some words. The app does not silently
correct/guess missing text or claim that every document was extracted perfectly.
Original and converted documents used in local probes remain outside this report.

## Visual Evidence

render/ contains actual Release control renders, not generated mockups. The native
renderer reports 1.25 scaling; RenderTargetBitmap files use 96 DPI. Synthetic demo
fixtures run with a separate --data file and exit automatically before live reopening.

- UI-07: thinking-docked-1440x860-render96.png shows line 80/80 at the end.
  Inner offset and maximum are both 2404 DIP.
- UI-04: thinking-compact-560x820-render96.png shows line 160/160 after another
  streaming update and resize. Offset and maximum are both 1127.2 DIP.
  thinking-scroll.txt's hardcoded latestLine label says 80/80 for both captures;
  the compact image itself correctly shows 160/160. Offset/end numbers are measured.
- pdf-image-settings-mineru-620x840-render96.png shows the explicit checkbox,
  its mode explanation, engine/runtime/limits and Save button, without clipping.
- Compared the real wide/narrow renders with original UI-07/UI-04: warm palette,
  narrow AI page and wide docked shell retained. Composer/provider controls reflect
  later user-approved changes; typography/spacing is not claimed pixel-identical.

Computer Use returned no targetable H2 Notes window. Native pointer interaction,
image-preview modal, other DPI levels and actual live thinking-stream screenshots
remain unverified; headless interaction tests and these renders do not certify them.
Final normal Release reopened as the sole live workspace writer, PID 19324, responsive.

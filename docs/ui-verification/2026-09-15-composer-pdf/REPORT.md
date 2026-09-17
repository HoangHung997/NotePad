# Composer, permissions, model effort and PDF

Date: 2026-09-15. Branch: codex/project-sheet. WPF and _ver2 unchanged.

## Reference and actual evidence

User reference: `C:/Users/hoang/AppData/Local/Temp/codex-clipboard-83577dca-d113-44f5-a66e-d2588c57b743.png`.
This explicitly updates the chat composer, not the approved warm responsive shell.
All ten original PNG SHA-256 values still match BASELINE.json.

`final/` contains the actual Release app controls rendered with isolated fabricated data,
not another mockup or a live user's project. RenderTargetBitmap uses 96 DPI; the native
window renderer reported 1.25 scale. `layout-sizes.txt` records checked actual bounds.
No AI requests, microphone capture, or live settings writes were used to make images.

| State | Evidence | Result |
| --- | --- | --- |
| UI-07, 1440x860 | final/files-docked-1440x860-render96.png | Warm shell preserved; chat-only composer update |
| UI-04, 560x820 | final/files-compact-560x820-render96.png | Input/options fit narrow project chat |
| Detached, 420x700 | final/files-desktop-420x700-render96.png | Same editor, chat and attachments |
| Minimum, 340x420 | final/composer-minimum-340x420-render96.png | Two option rows; history remains visible |
| Wide, 900x620 | final/composer-wide-900x620-render96.png | One bottom row like the user reference |
| Settings, 620x840 | final/pdf-settings-docling-620x840-render96.png | Four PDF choices, local path, limits and readiness |

Pass1 exposed a real compact-layout issue: attachment/options/marker controls left no
history space. Corrected by moving compact-only options into + and reducing the input
minimum from 76 to 44 DIP. Tests require at least 90 DIP of history with one attachment.
Pass1 PDF settings image also cut off the bottom due to a diagnostic scroll timing issue;
the final capture waits for layout before scrolling. Do not use pass1 as acceptance evidence.

Native mouse/IME/clipboard and voice typing remain unverified. Computer Use did not return
the off-taskbar H2 window as a target after launch; no guessed window handles were used.
The diagnostic process completed with empty stderr. No certification of pixel parity at
100/150/200% DPI is implied by bitmap renders or headless tests.

## Verification

- Release build: zero warnings, zero errors.
- Full test runner: 304 passed, zero failed; see test-results.txt.
- Includes provider payloads, PDF limits/missing runtimes, no text-only substitution,
  cancellation, history preservation, stored permission defaults and local grant checks.
- Includes reasoning allowlists/wrong endpoints, request profile cloning, responsive input,
  retained draft/selection, compact + actions, default-deny permission prompt and speech API mocks.
- Full project mode allows only append-note/add-task; parses and validates whole batches before
  dispatch. Read-only blocks applying/saving AI output. Applied changes have an idempotent audit.
- Saved API keys and selected live profile were not changed; no paid API test was run in this stage.

Main Release app was reopened after the user exited it normally. Only one live workspace
writer was started. OCR package/model installation readiness is a separate check, recorded
by the installer manifest and smoke reports, not by these 304 app tests.

## Installed OCR and end-to-end checks

All three CPU engines and their pipeline models were installed locally. Each completed
three synthetic fixtures: scanned English, native-text Vietnamese, and scanned Vietnamese.
Runtime footprint is about 7.27 GiB. [Setup and use](../../ocr/README.md) and
[source/license inventory](../../ocr/LICENSES.md) describe the separate local installation.
The runtime uses bundled Python, not a cloud OCR service. MinerU uses its supported Latin
alias mapped to the multilingual model; the invalid `vi` code was corrected. GOT's formatted
output is normalized to Markdown; MinerU preserves tables rather than discarding them.

The separate C# `H2Notes.OcrProbe` then called the same `AiPdfProcessor` as the app against
the released bridge, with the real cleared/offline subprocess environment and a synthetic
Vietnamese scan. No LLM request or user document was used.

| Engine | C# result | Elapsed | Markdown characters | Evidence |
| --- | --- | --- | --- | --- |
| Docling | PASS | 25.0 s | 149 | ocr-docling-core.txt |
| GOT-OCR 2.0 | PASS | 40.6 s | 130 | ocr-got-core.txt |
| MinerU | PASS | 21.3 s | 300 | ocr-mineru-core.txt |

All three checks verified nonempty Markdown, source provenance, Markdown included in the
prepared chat request, and no original PDF left in that OCR-mode request. Released bridge
SHA-256 at verification: `615A8D727EFF86A393AF34F0471CD71B363F4593DC9C89667D6062E56FC68C6C`.
These are execution/integration checks, not accuracy benchmarks: Vietnamese scan phrases
were imperfect in all three engines. GOT used roughly 8-9.5 GB peak process-tree RAM in
installer samples, versus about 2.7 GB Docling and 1.1 GB MinerU. Larger real PDFs may need
more time and memory, particularly when a local LLM is already loaded. Never infer exact
Vietnamese transcription from a PASS. Native PDF provider payloads were tested with mocks,
not a paid live PDF request. The saved provider/model and default PDF mode were not changed.
Nine additional Python bridge contract tests passed, including no overwrite, page/character
limits, source receipt validation, offline guards and formatted-table preservation.

## Boundaries

The three H2 permissions are not Codex's OS sandbox. They do not enable terminal commands,
arbitrary filesystem tools, deletes, background surveillance or unrestricted computer use.
External file save/overwrite still uses explicit user selection/confirmation.

The microphone launches Windows voice typing (Win+H). It is not H2 audio recording or an
OpenAI transcription adapter; Windows handles language/mic and online recognition. No mic
was activated during development tests. [Microsoft documentation](https://support.microsoft.com/en-us/accessibility/windows/use-voice-typing-to-talk-instead-of-type-on-your-pc).

Native PDF is available only for verified model/endpoint combinations on OpenAI/Gemini.
Ollama/unknown compatible endpoints fail before sending rather than silently omitting PDFs.
OCR is local, bounded and cancelable; no model downloads occur from a chat request. New OCR
attachments store Markdown plus source name/hash; the original user file is untouched. Raw
PDFs already in history are converted per request when an OCR mode is selected.

Sources: [OpenAI file inputs](https://developers.openai.com/api/docs/guides/file-inputs),
[Codex permissions](https://learn.chatgpt.com/docs/agent-approvals-security),
[Ollama thinking](https://docs.ollama.com/capabilities/thinking),
[Gemini thinking](https://ai.google.dev/gemini-api/docs/generate-content/thinking),
[GOT Transformers](https://huggingface.co/docs/transformers/model_doc/got_ocr2),
[MinerU installation](https://github.com/opendatalab/MinerU/blob/master/docs/en/quick_start/index.md),
[Docling](https://github.com/docling-project/docling).

# Chat input, provider progress and Ollama LAN

Date: 2026-09-15. Branch: codex/project-sheet. .NET 10 / Avalonia 12.1.2. WPF and _ver2 unchanged.

## Implementation and tests

- Release build: zero warnings/errors. Full automated suite: **255 passed, 0 failed** (`test-results.txt`). Initial failures in stale confirmation tests and UI/TCP fixtures were corrected; the native file ancestor walk also needed a drive-root fix found by the drop tests.
- Real desktop fixture exposed an Avalonia old-layout-root arrange exception when reparenting AI. Both transfer directions now detach the visual presenter and drain the old root before reattaching the same editor. Added repeated pending-layout transfer regression. Final actual Release fixture completed normally (exit code 0, empty stderr), including correct 1440x860/560x820 bounds.
- `StreamEvents` separates final text from Ollama thinking, compatible Chat Completions reasoning fields, Responses public summary events and Gemini thought summary parts. Existing `Stream` callers receive final text only. Cancellation, failures, same-chunk reasoning/text, empty/truncated streams and no retry are covered.
- Expander shows provider progress, or an honest waiting placeholder when absent. First progress/answer paint immediately; later paints remain throttled. Progress is cleared on first answer, cancel/error/finish; never written into project JSON or replayed. Visible transient tail bounded at 24,000 characters with a truncation label.
- Model-default / enabled / disabled Ollama thinking is explicit and persisted locally. Official Responses/Gemini public-summary requests are opt-in, never added to arbitrary endpoints; no raw/opaque hidden reasoning requested.
- Ollama accepts configured private IPv4/IPv6 LAN HTTP as well as loopback; public HTTP, credentials in URLs, query secrets and cross-origin endpoint changes remain rejected. Real default HTTP transport redirect test confirms no redirect following.
- Send flushes the selected project editor and sends immediately, with no modal confirmation. Optional data preview remains. Private timeline markers remain local. Applying AI text, deleting history, and saving/overwriting external artifacts still require their existing user actions.
- Composer plus menu, clipboard image/file/text, text selection/undo, deferred paste races, project/conversation switches, file URI drops, remote URL text-only behavior and attachment size/count limits are tested. Plus is inside the TextBox and send remains beside it. At-sign suggestions filter Vietnamese with/without accents, navigate via arrows/Enter/Tab/Esc and insert multiple task/context hints without sending. These are existing app actions/prompt shortcuts, not an autonomous computer agent.

## Live connection checks

- User-provided `http://192.168.1.208:11434/api/tags`: reachable, 13 installed models.
- Two opt-in synthetic-only probes through the compiled AiClient to `gemma4:latest`: default and explicit `think=true`. Both completed with 7 final-answer characters, **0 reasoning characters**. This proves LAN chat round-trip, not real-model reasoning disclosure. No retries/fallbacks, private project text, attachments or cloud calls; no saved profile/model/credentials changed.
- Online protocol progress parsing verified with mock streams, not live paid-provider requests. A model may not return progress even when thinking is requested; UI does not fabricate it.

## Visual checks

- Approved ten PNG hashes rechecked: **10 matched, 0 mismatches**. Baseline JSON/images unchanged.
- Actual Release app run in isolated demo stores. RenderTargetBitmap output at 96 DPI; native render scaling reported as 1.25. These are control renders, **not native screenshots or mouse/DPI certification**.
- Use `final/` renders: full 1440x860 three-pane default chat corresponds to UI-07; 560x820 compact composer/progress corresponds to UI-04; detached 420x700 and minimum 340x420 show plus and send without clipping; AI settings at 620x840 show LAN guidance/thinking mode. Popup rendered separately because it is a separate presentation surface.
- Warm ivory/terracotta, project selection, toolbar placement, user-right/AI-left chat, timestamps and send adjacency retained. Existing typography/density/profile-selector differences from the raster baseline remain; no pixel-parity claim.
- `renders/`, `revised/` and `verified-layout/` retain development attempts, not acceptance evidence. Some wide thinking captures had stale native resize bounds and are invalid; the old-root transfer issue was subsequently fixed. A synthetic artifact fixture briefly showed raw file JSON after direct state mutation. The final diagnostic skips mismatched sizes and uses plain response text for the progress fixture instead of presenting those as product parity.
- Native Computer Use did not expose the running off-taskbar fixture window. Real screenshot paste/drag interaction, native popup placement, IME, and 100/150/200% DPI remain **unverified directly**; headless tests are not a substitute.

## Protocol references

- [Ollama thinking](https://docs.ollama.com/capabilities/thinking)
- [OpenAI public reasoning summaries](https://developers.openai.com/api/docs/guides/reasoning)
- [Gemini public thought summaries](https://ai.google.dev/gemini-api/docs/generate-content/thinking)

The app does not upgrade Ollama or fix the previously reproduced Gemma4 Windows image-runtime issue.

## Delivery

After all diagnostic processes exited, opened the normal Release app with `--show --show-ai` (PID 12336, confirmed running). No second live workspace writer and no saved AI profile/model changes.

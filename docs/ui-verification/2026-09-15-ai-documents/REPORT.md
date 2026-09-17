# AI documents verification

Date: 2026-09-15. Branch: codex/project-sheet. Windows, .NET 10 / Avalonia.

## Results

- Release build: 0 warnings/errors. Final automated suite: **131 passed, 0 failed**; full output in `test-results.txt`.
- Full/fresh project snapshot; all tasks, notes, links and related chats; excludes private markers and other chat drafts. Old snapshots are audit records rather than duplicated future context.
- Native image payloads verified with mock transports for Ollama, Responses, Chat Completions and Gemini; no credentials in payloads.
- DOCX/XLSX generated as real Open XML packages, validated using OpenXmlValidator and reopened for Vietnamese text, table, multi-sheet, shared-string and cached-formula extraction tests. CSV/formula/path safety and ZIP/XML size/XXE/macro checks pass.
- Attachments/drafts/export audit persist in the same project file, IDs remap on copy; schema 3 upgrades to 4 with backup. Existing v2 migration/rollback tests also pass.
- UI tests cover draft scope, local-only marker attachments, file cards, editor flush before preview, confirmation before network and project switching while confirmation is open. Existing session/window/tray/editor/AI regression tests retained.

## Visual Evidence

- `revised/files-docked-1440x860-render96.png`: compared with baseline UI-07. Ivory/terracotta, rail, project list, tasks/notes center and right AI retained; added attachments/context/file cards; send beside composer. Existing differences in profile selector, header controls and type density remain; no claim of pixel parity.
- `revised/files-compact-560x820-render96.png`: compact AI page, scope/composer visible, corresponding to UI-04.
- `revised/files-desktop-420x700-render96.png`: detached project AI, artifact preview/save and draft attachment.
- `revised/files-minimum-340x420-render96.png`: fixed near-zero transcript area from the first render by moving profile/history to a compact options menu. Transcript scrolls, scope and send remain accessible. Small height prioritizes composing over large history.
- Actual Release app controls, isolated demo-only fixtures, RenderTargetBitmap at 96 DPI. These are **not native screenshots**. Diagnostic process exits itself after capture; no real project or model request used. Approved baseline PNGs/BASELINE.json unchanged.

## Not Verified / Limits

Native Windows mouse/IME, file dialogs, Word/Excel opening, multi-monitor DPI and real-model image/artifact quality were not tested this turn. No live API calls, credentials read for AI testing, or saved provider/model changes. Mock transport tests establish payload handling, not every model's vision capability.

See `docs/AI_DOCUMENTS.md` for limits: PDF/legacy Office, embedded Office graphics/OCR, full layout fidelity, arbitrary file formats and autonomous filesystem/computer tools are not implemented. Linked folders are not scanned. Files are saved only on explicit user action.

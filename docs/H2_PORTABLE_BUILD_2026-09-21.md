# H2 Notes — full Windows x64 package, 21/09/2026

User request: a full build that can be copied to another computer. This package
contains the current working-tree repairs, including the bottom Work Assistant
composer and enforced permission modes. It is not a GitHub release or a claim
that every open product/Agent acceptance item has been completed.

## Contents and startup

Output: `C:\Users\hoang\Downloads\H2Notes-Full-2026-09-21-win-x64.zip`.
Extract the complete archive into a writable local folder and run
`MO H2 NOTES.cmd`. Enable Work Assistant in Settings on a fresh machine before
using `MO ASSISTANT.cmd`. `DOC TOI TRUOC.txt` contains the Vietnamese guide.

Included: self-contained .NET 10 Windows x64 app, Agent and both helper hosts,
five Agent skills, Agent Python with DOCX/XLSX/PDF/rendering libraries, separate
OCR Python and GOT-OCR 2.0, Docling and MinerU CPU models. All four app/helper
runtimeconfig files declare included frameworks rather than a shared runtime
requirement. Original third-party notices and model receipts are retained.

Not included: personal projects, history, drafts, API keys, machine settings,
Ollama/chat LLM weights, Microsoft Office or AutoCAD. Configure the destination
AI provider separately. Live control of external apps requires those apps to be
installed. This is an application package, not a private-data migration.

## Packaging corrections

- A normal publish omitted the separate Agent Python runtime. The full package
  includes it beside the application, where the Agent selects it explicitly.
- MinerU stored an absolute model path from installation and rejected relocation.
  The bridge now creates a temporary local configuration for the current verified
  model directory. It retains model checksums, leaves original receipts/config
  unchanged, restores the environment and removes the temporary config on exit.
- The pinned native Docling PDF renderer produced a Windows access violation on
  the synthetic scan. The adapter now selects Docling's PDFium backend, keeping
  the same offline OCR/layout/table models. Actual conversion then passed.
- `--verify-portable <new-output-directory>` verifies helper IPC, bundled Agent
  Python creation/readback/rendering, venv relocation and OCR inventory without
  reading user settings/data or calling an LLM. `KIEM TRA GOI.cmd` exposes this
  diagnostic on the destination PC. Inventory alone does not establish OCR quality.
- `tools/package_portable.py` creates ZIP64 plus SHA-256 per file and independently
  extracts/reopens every output file to check hashes. Python bytecode caches are
  omitted; no installed global Python or .NET SDK is needed to run the application.

## Validation

Evidence folder: `C:\Users\hoang\Downloads\H2Notes-Package-Checks-2026-09-21`.

- Self-contained Release publish succeeded; existing nullable/obsolete API warnings
  remain in `publish.txt`.
- `initial-check/portable-check.json`: helpers, sandboxed Python DOCX/XLSX/PDF
  create/read/render and bundled OCR inventory passed. Both Python roots are in
  the output package, and the OCR venv launcher was repaired after relocation.
- `ocr-bridge-tests-final.log`: 14/14 passed, including relocation to a path with
  spaces, preserving original config and cleanup/restoration after an exception.
- Actual one-page English scan conversion passed with all three engines:
  `mineru.md`, `docling-final.md`, `got-ocr-final.md`. Each output preserves the
  heading, invoice, total and Alpha/Beta counts. Timings: approximately 55/14/44 s.
  Model file sizes and SHA-256 were checked by the bridge before each conversion.
- `native-package-assistant.jpg`: actual packaged Assistant opened with the new
  bottom composer. Main window also opened using isolated demo/settings; no live
  workspace or credentials were included. This is launch verification, not a new
  full UI acceptance claim.
- Earlier repair regression evidence remains in
  [the composer report](H2_ASSISTANT_COMPOSER_2026-09-21.md): 554/554 checks. It was
  not rerun for a Python-only adapter correction; the relevant bridge checks and
  actual OCR conversions were run for this correction.

Archive completed: **4,308,954,418 bytes** (4.31 GB / 4.01 GiB), **36,298 files**,
**7,895,484,946 uncompressed file bytes** plus the manifest. ZIP SHA-256:
`e6be5204e78f9693ec6774d6a0dc6d56b730a081677c899a481803d82fad34ef`.
The adjacent `.zip.sha256` file contains the same checksum. USB transfers of
this ZIP require a filesystem supporting files larger than 4 GiB, e.g. exFAT
or NTFS; FAT32 cannot hold this individual file.

Use a short extraction directory such as `C:\H2Notes`. A deliberately deeply
nested verification folder exceeded Windows' default 260-character path limit
for a ModelScope library filename. No machine-wide long-path policy was changed.
The extraction utility now checks all paths before parallel writes and rejects
an overly deep destination up front. The final check uses a short folder with a
space in its name. Incomplete diagnostic copies from the failed attempts remain
separate from the completed ZIP and the primary runnable package folder.

- [Independent extraction check](audit-evidence/2026-09-21/portable/extraction-check.json):
  **36,298/36,298 files matched SHA-256 and size after reopening each extracted file**.
  Actual root: `C:\Users\hoang\Downloads\H2 QA\H2Notes-Full-2026-09-21-win-x64`.
- [Relocated runtime check](audit-evidence/2026-09-21/portable/relocated-portable-check.json):
  exit 0, `passed=true`. Launched with PATH limited to Windows System32,
  DOTNET_ROOT pointing at an absent test location, multi-level lookup disabled,
  and PYTHONHOME/PYTHONPATH removed. Bundled Agent Python, helper IPC, document
  creation/readback/rendering and OCR inventory passed. OCR venv home/executable
  were automatically rewritten to the new package path.
- [Relocated Assistant](audit-evidence/2026-09-21/portable/native-relocated-assistant.jpg):
  both main app and Assistant opened from the extracted executable with isolated
  demo/settings. The new bottom composer was visible. Only the diagnostic GUI
  process was stopped afterward; the user's live data was not opened.
- [Relocated MinerU conversion](audit-evidence/2026-09-21/portable/mineru-relocated.log):
  exit 0, offline, 1 page / 281 characters, approximately 115 seconds on its first
  launch from the extracted runtime. Heading, invoice total and table counts
  matched the synthetic input. No reinstall or model download was needed after
  moving the package, including the space in its new path.

## Limits

No second physical PC, live Office/AutoCAD session, destination AI provider,
full Vietnamese OCR accuracy, microphone recognition or two-PC NAS behavior is
established by this packaging check. The existing product acceptance limits
remain applicable. The package can run without a .NET/Python installation;
that does not turn an external Ollama/API service into a bundled chat model.

## Bản cập nhật chat surface cùng ngày

Gói mới: C:\Users\hoang\Downloads\H2Notes-Chat-Full-2026-09-21-win-x64.zip.
Kích thước 4.309.010.025 byte; 36.299 tệp, 7.895.605.709 byte trước nén.
SHA-256: a275f2e92dc312e77e13a289127503648bafde3540e050ea0ad5f2788ea1c1d5.
Có bản chat mới theo [báo cáo triển khai](H2_AGENT_CHAT_SURFACE_IMPLEMENTATION_2026-09-21.md). Bản ZIP trước đó được giữ nguyên.
Portable diagnostics đạt cả tại thư mục gốc và bản sao có dấu cách. Bản sao tái sử dụng Python/OCR đã giải nén từ gói cũ và cập nhật các tệp chương trình mới; không tuyên bố đã giải nén toàn bộ ZIP mới sang máy thứ hai.
Gói lớn hơn giới hạn tệp FAT32: dùng USB exFAT/NTFS hoặc truyền qua mạng. Giải nén toàn bộ vào đường dẫn ngắn trên máy đích, cấu hình kết nối AI riêng.


ZIP mới đã đọc lại toàn bộ 36.299 tệp từ luồng nén, kiểm CRC và SHA-256 khớp manifest. Bằng chứng: docs/audit-evidence/2026-09-21-chat-surface/zip-verification.log.

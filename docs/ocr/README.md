# Local PDF preprocessing

Installed and verified on 2026-09-15 for H2 Notes Avalonia. The WPF app is unchanged.

## Use in H2 Notes

Open AI settings, find **Tai lieu PDF**, choose an engine and save PDF settings.
The default sends the original PDF to a supported provider/model. Ollama cannot accept
the raw PDF through this adapter; select a local OCR engine to send Markdown instead.
No model download happens when sending a chat message. Cancel stops preprocessing and
keeps the draft. Source documents are not modified. Converted attachments retain the
source name and SHA-256. AI still receives the extracted document when you press Send.

All three installed CPU engines are ready:

| Engine | Implementation | Actual app-core check, one synthetic scan |
| --- | --- | --- |
| GOT-OCR 2.0 | Transformers 4.57.6, pinned HF safetensors checkpoint | PASS, 40.6 seconds |
| MinerU | 3.4.5 local CPU pipeline, layout/formula/table/OCR models | PASS, 21.3 seconds |
| Docling | docling-slim 2.127.0, local layout/TableFormer/EasyOCR | PASS, 25.0 seconds |

These are small execution checks, not performance or accuracy benchmarks. All three
made mistakes in scanned Vietnamese phrases. Compare the extracted result with the
source, particularly names, accents, amounts and tables. GOT used up to about 9.5 GB
process-tree RAM in installer samples; Docling about 2.7 GB, MinerU about 1.1 GB.
Larger documents and an already-loaded Ollama model may require more memory/time.
The installation is the complete selected CPU pipeline, not every optional GPU/VLM backend.

## Runtime location

Default app setting: `%LOCALAPPDATA%/H2Notes/ocr-runtime`.
On this machine the installer was virtualized into:

`C:/Users/hoang/AppData/Local/Packages/OpenAI.Codex_2p2nqsd0c76g0/LocalCache/Local/H2Notes/ocr-runtime`

The app discovers this fixed fallback when the default directory is missing. You can also
select that directory explicitly. It contains `runtime.json`, bundled `python/`, `venv/`,
three `models/` subdirectories, pinned package information and synthetic `smoke/` evidence.
Size at completion: approximately 7.27 GiB. Python is included; installation is not tied
to the development Python executable. This Windows x64 environment is not a portable
copy-and-run package: moving it requires rebuilding venv launchers and MinerU local paths.
Do not put it in shared project storage or move API credentials into it.

## Reproducibility and safety

- `tools/ocr/models.lock.json` records exact model repositories/revisions. Each model
  folder's `models.json` records file sizes and SHA-256, rechecked before conversion.
- `tools/ocr/requirements*.in` and runtime `requirements.lock.txt` record package versions.
- `tools/ocr/install.py --help` documents explicit installation/download switches. Reinstall
  only deliberately; it checks a size budget and keeps at least 10 GiB free on the drive.
- `tools/ocr/smoke.py` uses fabricated native-text and scanned PDFs, never live projects.
- `tools/ocr/test_bridge.py` contains nine small contract tests, independent of model inference.
- The released `tools/ocr/convert.py` runs offline with bounded input/pages/output and a
  runtime lock. No remote model code, service endpoint or engine fallback is enabled.
  Python audit guards and local checks are not an operating-system sandbox.
- App limits: 8 MB per PDF, up to 100 pages in OCR mode, 120,000 extracted characters,
  timeout configurable up to 600 seconds. Rejects incomplete/oversized output rather than
  silently sending a truncated extraction. Native PDF limits also depend on the provider.
- MinerU uses the supported Latin alias and its multilingual model, not an invented `vi`
  code or an English-only fallback. Tables are retained in Markdown/HTML. Temporary figure
  files are not embedded; figure placeholders explicitly say so.
- GOT simple formatted tables become Markdown. Complex unsupported table syntax is kept
  as a fenced text block, never executed or silently removed.

Actual app renders, 304 app tests and three C# integration probes are recorded in
[the acceptance report](../ui-verification/2026-09-15-composer-pdf/REPORT.md).
Native voice typing/IME interaction and paid live PDF API requests remain unverified.
See [licenses and source inventory](LICENSES.md) before distributing a bundled runtime.

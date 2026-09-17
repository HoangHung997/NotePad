---
name: documents
description: Author or edit Word DOCX documents, including paragraphs, run formatting, tables, page settings and source-based content. Use for document work rather than a fixed template-only operation.
---

# Word documents

Adapted from the installed OpenAI Codex documents skill. Read `references/runtime.md` for the Lab environment. The original source is preserved separately for provenance; Codex-only tool names and paths are not available here.

Inspect source facts and existing DOCX structure first. Choose suitable Python using python-docx, lxml or direct OOXML within `run_python`. You are not restricted to replacing one paragraph or using a hard-coded layout. For complex requests, decide how to preserve runs, relationships, tables, headers, footers, sections and styles before editing.

Match the user's reference/template and scope. For a new document use a descriptive title and clear hierarchy; do not invent dates, signatures or business facts. For a narrow change preserve unrelated content and mixed formatting. Replacing `paragraph.text` destroys run-level formatting; edit specific runs/OOXML when appropriate. Direct XML edits need relationship/namespace awareness.

Save a new working result under `output/`, reopen, compare content and properties, and check document structure. `check_word` checks OpenXML for a published file, not typography or facts. Use explicit assertions for requested formatting and untouched parts.

Visual review is separate and important. Lab's isolated Python runtime cannot launch Word/LibreOffice; no DOCX page renderer is currently bundled in the sandbox. Do not claim visual fidelity based on text extraction or a hand-drawn preview. State that actual Word rendering is unverified if unavailable. Exact recreation from PDF/image requires source layout evidence, suitable OCR/layout libraries, fonts and rendered comparison; plain OCR text is insufficient.

When a reusable script materially improves reliability, read it and adapt its parameters. Otherwise write the task-specific code. Fix observable failures, then verify again before requesting publication. Do not claim success merely from a created filename.

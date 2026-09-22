---
name: pdf
description: Inspect, extract, render or generate PDF files and analyze page images. Use when PDF text or page layout matters; scanned OCR needs a configured OCR engine.
---

# PDF and page images

Adapted from the installed OpenAI Codex PDF skill. Read `references/runtime.md`. Lab provides pypdf, pypdfium2 and Pillow in isolated Python; reportlab may be inspected with importlib before use. Do not invoke unavailable Codex tools or install packages implicitly.

Inspect page count, page boxes and extractable text. Distinguish embedded text from scans. With pypdfium2, open a PDF, select a page and render to Pillow/PNG, close resources, and put images in `output/`. Call `view_artifact` when visual evidence is needed; the selected model must actually support vision. Tool errors or only a filename do not mean the model saw the page.

Use pypdf for structural operations/text extraction and pypdfium2 for rendered inspection. Text extraction alone does not preserve reading order, tables or exact fonts. No OCR engine is installed in Lab's isolated runtime yet; a scanned page may require a vision-capable model or a separately implemented OCR adapter. State this limitation rather than inventing recognized text.

For generated/modified PDFs inspect the final page count/text and render relevant pages to detect clipped/overlapping content. For PDF-to-Word, combine this skill with documents; do not promise identical editable layout from plain text. Preserve the user's reference instead of redesigning the document without permission.

For an append/edit request, never replace the original PDF with a new ReportLab page. Use `pypdf.PdfReader` and `PdfWriter`: append all original pages first, then append the new page. The installed module is `pypdf`, not `PyPDF2`. Before publishing, assert the requested page count and compare each untouched page's extracted text and page box against the staged source. Keep the source staged until all preservation assertions pass. Read existing files through Python or `read_file`; opening Word/Excel is unnecessary for an offline edit and changes the user's visible workspace.

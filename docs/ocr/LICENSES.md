# OCR source and license inventory

Recorded 2026-09-15. This inventory is not a replacement for the upstream license text,
nor a declaration that all dependencies/models can be redistributed on the same terms.
The app source does not include downloaded model weights. The runtime is installed
separately for local use. Review all applicable terms before distributing a bundled app,
model archive, or commercial hosted service.

| Component | Pinned source / declared terms |
| --- | --- |
| GOT checkpoint used here | [stepfun-ai/GOT-OCR-2.0-hf](https://huggingface.co/stepfun-ai/GOT-OCR-2.0-hf/tree/d3017ef2c2c1395888c8d635c5e0508bcb0ac78d), card declares Apache-2.0 |
| Original GOT project | [GOT-OCR2.0](https://github.com/Ucas-HaoranWei/GOT-OCR2.0), original checkpoint terms are separate; do not assume the converted HF card settles those terms |
| Docling | [docling-project/docling](https://github.com/docling-project/docling), MIT project; dependencies/models have separate terms |
| Heron layout | [docling-layout-heron](https://huggingface.co/docling-project/docling-layout-heron/tree/8f39ad3c0b4c58e9c2d2c84a38465abf757272d8), Apache-2.0 |
| TableFormer models | [docling-models](https://huggingface.co/docling-project/docling-models/tree/fc0f2d45e2218ea24bce5045f58a389aed16dc23), model card includes CDLA-Permissive-2.0; inspect per-model declarations |
| EasyOCR | [JaidedAI/EasyOCR](https://github.com/JaidedAI/EasyOCR), Apache-2.0 project; detector/recognizer source and checksums recorded in the installed receipt |
| MinerU 3.4.5 | [opendatalab/MinerU](https://github.com/opendatalab/MinerU), installed release has Apache-2.0 plus additional commercial/attribution terms; do not summarize as unrestricted Apache alone |
| MinerU model repository | [PDF-Extract-Kit-1.0](https://huggingface.co/opendatalab/PDF-Extract-Kit-1.0/tree/ed6b654c018d742e65a17671e379c5e6ecc87ec9), repository declares AGPL-3.0; component terms may differ |

Model revision/selection details: `tools/ocr/models.lock.json`.
Downloaded files, hashes and source receipts: `models/<engine>/models.json` in the local
runtime. Package versions: runtime `requirements.lock.txt`; installed package license
files/metadata remain in `venv/Lib/site-packages`.

This is a source inventory, not a completed release-compliance audit of transitive
dependencies. Preserve original notices and review additional terms before redistribution.

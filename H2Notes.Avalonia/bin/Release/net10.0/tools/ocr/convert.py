"""Offline, CPU-only PDF/document-image-to-Markdown subprocess bridge for H2 Notes."""
from __future__ import annotations

import argparse
import contextlib
import hashlib
import json
import math
import os
from pathlib import Path
import re
import sys
import tempfile
import time


class BridgeError(Exception):
    def __init__(self, message, code=4):
        super().__init__(message)
        self.code = code


def offline_policy(models):
    # Defense in depth, not an OS sandbox. Applies to this child only.
    env = {
        "HF_HUB_OFFLINE": "1", "TRANSFORMERS_OFFLINE": "1", "HF_DATASETS_OFFLINE": "1",
        "HF_HUB_DISABLE_TELEMETRY": "1", "DO_NOT_TRACK": "1", "HF_HUB_DISABLE_XET": "1",
        "TOKENIZERS_PARALLELISM": "false", "CUDA_VISIBLE_DEVICES": "",
        "OMP_NUM_THREADS": "2", "MKL_NUM_THREADS": "2", "OPENBLAS_NUM_THREADS": "2",
        "TORCH_FORCE_WEIGHTS_ONLY_LOAD": "1", "MINERU_MODEL_SOURCE": "local",
        "MINERU_DEVICE_MODE": "cpu", "MINERU_INTRA_OP_NUM_THREADS": "2",
        "MINERU_INTER_OP_NUM_THREADS": "1", "PYTHONNOUSERSITE": "1",
        "MINERU_PROCESSING_WINDOW_SIZE": "1", "MINERU_FORMULA_CH_SUPPORT": "False",
        "HF_HOME": str(models / ".cache/huggingface"),
        "TORCH_HOME": str(models / ".cache/torch"),
        "EASYOCR_MODULE_PATH": str(models / "easyocr"),
    }
    os.environ.update(env)
    for name in list(os.environ):
        if name in {"HF_TOKEN", "HUGGING_FACE_HUB_TOKEN", "OPENAI_API_KEY", "ANTHROPIC_API_KEY",
                    "GEMINI_API_KEY", "GOOGLE_API_KEY", "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY"}:
            os.environ.pop(name, None)

    def audit(event, args):
        if event in {"socket.connect", "socket.connect_ex", "socket.getaddrinfo",
                     "socket.gethostbyname", "socket.sendto", "socket.bind",
                     "subprocess.Popen", "_winapi.CreateProcess", "os.system", "os.posix_spawn", "os.exec"}:
            raise BridgeError("Offline policy blocked network or child-process access", 5)
    sys.addaudithook(audit)


def verified_models(models, engine):
    receipt = models / "models.json"
    if not receipt.is_file():
        raise BridgeError(f"{engine}: model installation is incomplete; run explicit installer first", 3)
    data = json.loads(receipt.read_text(encoding="utf-8"))
    if data.get("schemaVersion") != 1 or data.get("engine") != engine or not data.get("files"):
        raise BridgeError("Invalid model installation receipt", 3)
    for item in data["files"]:
        path = (models / item["path"]).resolve()
        if not path.is_relative_to(models) or not path.is_file() or path.suffix == ".py":
            raise BridgeError("Missing or unsafe model file", 3)
        if path.stat().st_size != item["size"]:
            raise BridgeError("Model file size mismatch; repair installation explicitly", 3)
        with path.open("rb") as stream:
            if hashlib.file_digest(stream, "sha256").hexdigest() != item["sha256"]:
                raise BridgeError("Model checksum mismatch; repair installation explicitly", 3)


def pdf_pages(source, limit):
    import pypdfium2 as pdfium
    try:
        with pdfium.PdfDocument(source) as pdf:
            count = len(pdf)
            if not 1 <= count <= limit:
                raise BridgeError(f"PDF has {count} pages; allowed range is 1..{limit}", 2)
            for index in range(count):
                with contextlib.closing(pdf[index]) as page:
                    width, height = page.get_size()
                    if not all(math.isfinite(v) and 0 < v <= 14400 for v in (width, height)):
                        raise BridgeError("Invalid or oversized PDF page", 2)
                    if width * height * 4 > 20_000_000:
                        raise BridgeError("PDF page exceeds safe render pixel limit", 2)
            return count
    except BridgeError:
        raise
    except Exception as exc:
        raise BridgeError(f"Cannot open PDF: {type(exc).__name__}", 2) from exc


@contextlib.contextmanager
def document_pdf(source, directory):
    """Wrap a validated single image in a lossless one-page PDF; never alter its source."""
    if source.suffix.lower() == ".pdf":
        yield source
        return
    from PIL import Image, ImageOps, UnidentifiedImageError
    import pypdfium2 as pdfium
    target = None
    try:
        with Image.open(source) as original:
            expected = {".png": "PNG", ".jpg": "JPEG", ".jpeg": "JPEG", ".webp": "WEBP"}[source.suffix.lower()]
            if original.format != expected or getattr(original, "n_frames", 1) != 1:
                raise BridgeError("Image format mismatch or animated image; use a single PNG/JPEG/WebP", 2)
            width, height = original.size
            if not 1 <= width <= 14400 or not 1 <= height <= 14400 or width * height > 20_000_000:
                raise BridgeError("Image exceeds safe OCR pixel limit (20 million pixels)", 2)
            with ImageOps.exif_transpose(original) as oriented, oriented.convert("RGBA") as rgba:
                with Image.new("RGBA", rgba.size, "white") as background:
                    background.alpha_composite(rgba)
                    with background.convert("RGB") as rgb, contextlib.closing(pdfium.PdfBitmap.from_pil(rgb)) as bitmap:
                        with pdfium.PdfDocument.new() as pdf:
                            width, height = rgb.size
                            # 144 DPI: the common 2x OCR renderer reproduces the original pixels.
                            with contextlib.closing(pdf.new_page(width / 2, height / 2)) as page:
                                picture = pdfium.PdfImage.new(pdf)
                                picture.set_bitmap(bitmap)
                                picture.set_matrix(pdfium.PdfMatrix().scale(width / 2, height / 2))
                                page.insert_obj(picture)
                                page.gen_content()
                                fd, name = tempfile.mkstemp(prefix="h2-image-", suffix=".pdf", dir=directory)
                                os.close(fd)
                                target = Path(name)
                                pdf.save(target)
        if target.stat().st_size > 8 * 1024 ** 2:
            raise BridgeError("Image PDF exceeds 8 MiB after lossless conversion; use a smaller image", 2)
    except (UnidentifiedImageError, OSError, ValueError, Image.DecompressionBombError) as exc:
        if target is not None:
            target.unlink(missing_ok=True)
        raise BridgeError(f"Cannot decode document image: {type(exc).__name__}", 2) from exc
    except Exception:
        if target is not None:
            target.unlink(missing_ok=True)
        raise
    try:
        yield target
    finally:
        target.unlink(missing_ok=True)


@contextlib.contextmanager
def runtime_lock(models):
    lock = models.parent.parent / ".conversion.lock"
    lock.parent.mkdir(parents=True, exist_ok=True)
    stream = lock.open("a+b")
    acquired = False
    try:
        stream.seek(0)
        if os.name == "nt":
            import msvcrt
            try:
                msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
            except OSError as exc:
                raise BridgeError("Another OCR conversion is already running", 3) from exc
        else:
            import fcntl
            try:
                fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except OSError as exc:
                raise BridgeError("Another OCR conversion is already running", 3) from exc
        acquired = True
        yield
    finally:
        if acquired and os.name == "nt":
            stream.seek(0)
            msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
        stream.close()


def docling_convert(source, models, pages, max_chars):
    import torch
    from docling.datamodel.base_models import InputFormat, ConversionStatus
    from docling.datamodel.accelerator_options import AcceleratorDevice, AcceleratorOptions
    from docling.datamodel.pipeline_options import PdfPipelineOptions, EasyOcrOptions, TableFormerMode
    from docling.document_converter import DocumentConverter, PdfFormatOption
    torch.set_num_threads(2)
    options = PdfPipelineOptions(
        artifacts_path=models, enable_remote_services=False, allow_external_plugins=False,
        do_ocr=True, do_table_structure=True,
        do_picture_description=False, do_picture_classification=False,
        do_code_enrichment=False, do_formula_enrichment=False,
        accelerator_options=AcceleratorOptions(device=AcceleratorDevice.CPU, num_threads=2),
        ocr_options=EasyOcrOptions(lang=["vi", "en"], use_gpu=False,
                                  model_storage_directory=str(models / "easyocr"),
                                  download_enabled=False),
    )
    options.table_structure_options.mode = TableFormerMode.ACCURATE
    converter = DocumentConverter(allowed_formats=[InputFormat.PDF], format_options={
        InputFormat.PDF: PdfFormatOption(pipeline_options=options)})
    result = converter.convert(source, max_num_pages=pages, max_file_size=8 * 1024 ** 2,
                               raises_on_error=True)
    if result.status != ConversionStatus.SUCCESS or result.errors or len(result.pages) != pages:
        raise BridgeError("Docling returned a failed or partial document")
    return result.document.export_to_markdown()


def got_convert(source, models, pages, max_chars):
    import pypdfium2 as pdfium
    import torch
    from transformers import GotOcr2ForConditionalGeneration, GotOcr2Processor
    torch.set_num_threads(2)
    model = GotOcr2ForConditionalGeneration.from_pretrained(
        str(models), local_files_only=True, trust_remote_code=False,
        use_safetensors=True, torch_dtype=torch.float32, attn_implementation="eager").to("cpu").eval()
    processor = GotOcr2Processor.from_pretrained(str(models), local_files_only=True, trust_remote_code=False)
    output = []
    characters = 0
    with pdfium.PdfDocument(source) as pdf, torch.inference_mode():
        for index in range(pages):
            print(f"GOT OCR page {index + 1}/{pages}", file=sys.stderr, flush=True)
            with contextlib.closing(pdf[index]) as page:
                bitmap = page.render(scale=2)
                try:
                    with bitmap.to_pil().convert("RGB") as image:
                        inputs = processor(image, return_tensors="pt", format=True)
                finally:
                    bitmap.close()
            ids = model.generate(**inputs, do_sample=False, max_new_tokens=4096,
                                 tokenizer=processor.tokenizer, stop_strings="<|im_end|>")
            generated = ids[0, inputs["input_ids"].shape[1]:]
            eos = processor.tokenizer.convert_tokens_to_ids("<|im_end|>")
            if len(generated) >= 4096 and int(generated[-1]) != eos:
                raise BridgeError(f"GOT page {index + 1} reached token limit; refusing truncated output")
            text = got_markdown(processor.decode(generated, skip_special_tokens=True)).strip()
            if not text:
                raise BridgeError(f"GOT page {index + 1} returned no text")
            if pages > 1:
                text = f"<!-- Page {index + 1} -->\n\n{text}"
            characters += len(text) + 2
            if characters > max_chars:
                raise BridgeError("Markdown character limit exceeded; no partial output saved", 2)
            output.append(text)
    return "\n\n".join(output)


def got_markdown(text):
    """Render simple GOT tabular output safely; never execute generated LaTeX."""
    def table(match):
        body = match.group(1)
        # Preserve complex structures verbatim instead of silently losing cells.
        if any(token in body for token in (r"\multicolumn", r"\multirow", r"\begin")):
            return "\n```latex\n" + match.group(0) + "\n```\n"
        rows = []
        for row in re.split(r"\\\\", body):
            row = row.replace(r"\hline", "").strip()
            if not row:
                continue
            cells = [cell.strip().replace(r"\&", "&").replace("|", r"\|")
                     for cell in re.split(r"(?<!\\)&", row)]
            rows.append(cells)
        if not rows or any(len(row) != len(rows[0]) for row in rows):
            return "\n```latex\n" + match.group(0) + "\n```\n"
        lines = ["| " + " | ".join(rows[0]) + " |", "| " + " | ".join(["---"] * len(rows[0])) + " |"]
        lines.extend("| " + " | ".join(row) + " |" for row in rows[1:])
        return "\n\n" + "\n".join(lines) + "\n\n"
    text = re.sub(r"\\begin\{tabular\}\{[^{}]*\}(.*?)\\end\{tabular\}", table, text, flags=re.S)
    text = re.sub(r"(?m)^\s*\\\\\s*", "", text)
    text = re.sub(r"\\title\{([^{}]*)\}", r"# \1", text)
    return text


def mineru_convert(source, models, pages, max_chars, layout=False):
    import torch
    from importlib.metadata import version
    if version("mineru") != "3.4.5":
        raise BridgeError("MinerU adapter requires pinned version 3.4.5", 3)
    config = models / "mineru.json"
    if not config.is_file():
        raise BridgeError("MinerU local configuration is missing", 3)
    settings = json.loads(config.read_text(encoding="utf-8"))
    if Path(settings.get("models-dir", {}).get("pipeline", "")).resolve() != models:
        raise BridgeError("MinerU model root changed; rerun explicit installer after relocating", 3)
    os.environ["MINERU_TOOLS_CONFIG_JSON"] = str(config)
    torch.set_num_threads(2)
    # Direct in-process API: never launch MinerU's service-oriented CLI.
    from mineru.backend.pipeline.pipeline_analyze import doc_analyze_streaming
    from mineru.backend.pipeline.pipeline_middle_json_mkcontent import union_make
    from mineru.data.data_reader_writer import FileBasedDataWriter
    from mineru.utils.enum_class import MakeMode
    from mineru.utils.ocr_language import validate_public_ocr_lang
    from mineru.utils import pdf_image_tools
    # The pinned release explicitly maps its Latin alias to its multilingual
    # "ch" model. It has no public "vi" code; do not substitute English.
    language = validate_public_ocr_lang("latin")
    print(f"MinerU Vietnamese/English: public latin alias -> {language} multilingual model", file=sys.stderr)

    def render_serial(pdf_bytes, dpi=pdf_image_tools.DEFAULT_PDF_IMAGE_DPI,
                      start_page_id=0, end_page_id=0,
                      image_type=pdf_image_tools.ImageType.PIL, timeout=None, threads=None):
        return pdf_image_tools._load_images_from_pdf_worker(
            pdf_bytes, dpi, start_page_id, end_page_id, image_type)

    # Keep PDFium rendering inside this guarded process. Windows multiprocessing
    # otherwise bypasses Python's subprocess audit event and loses the audit hook.
    pdf_image_tools._load_images_from_pdf_bytes_range = render_serial
    documents = []

    def completed(index, model_list, middle_json, ocr_enabled):
        if index != 0:
            raise BridgeError("Unexpected MinerU document index")
        documents.append(middle_json)

    with tempfile.TemporaryDirectory(prefix="h2-mineru-") as scratch:
        doc_analyze_streaming([source.read_bytes()], [FileBasedDataWriter(scratch)], [language],
                              completed, parse_method="auto", formula_enable=True,
                              table_enable=True, client_side_output_generation=False)
        if len(documents) != 1:
            raise BridgeError("MinerU returned no complete document")
        info = documents[0].get("pdf_info", [])
        if len(info) != pages or [p.get("page_idx") for p in info] != list(range(pages)):
            raise BridgeError("MinerU returned a partial document")
        if layout:
            import runpy
            builder = runpy.run_path(str(Path(__file__).with_name("layout.py")))
            vi_models = models.parent / "docling"
            if (vi_models / "easyocr").is_dir():
                verified_models(vi_models, "docling")
                vi_models = vi_models / "easyocr"
            else:
                vi_models = None
            return json.dumps(builder["reconstruct"](source, info, vi_models), ensure_ascii=False)
        markdown = union_make(info, MakeMode.MM_MD, "")
        # The bridge returns one Markdown file, not temporary raster assets.
        # MM_MD preserves HTML tables; NLP_MD silently removes them.
        return re.sub(r"!\[([^\]]*)\]\([^)]+\)", r"[Figure: \1; image not embedded]", markdown)


def validate_text(text, maximum):
    text = text.strip()
    if not text:
        raise BridgeError("Engine returned empty Markdown")
    if len(text.encode("utf-16-le")) // 2 + 1 > maximum:
        raise BridgeError("Markdown character limit exceeded; no partial output saved", 2)
    return text + "\n"


def atomic_output(destination, text):
    fd, name = tempfile.mkstemp(prefix=".h2-ocr-", suffix=".tmp", dir=destination.parent)
    temp = Path(name)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as stream:
            stream.write(text)
            stream.flush()
            os.fsync(stream.fileno())
        # A hard link publishes atomically and refuses a racing existing destination.
        os.link(temp, destination)
    finally:
        temp.unlink(missing_ok=True)


def main(argv=None):
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--engine", required=True, choices=["docling", "got-ocr", "mineru"])
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--models", type=Path, required=True)
    parser.add_argument("--max-pages", type=int, default=int(os.getenv("H2NOTES_PDF_MAX_PAGES", "100")))
    parser.add_argument("--max-chars", type=int, default=int(os.getenv("H2NOTES_PDF_MAX_CHARACTERS", "120000")))
    parser.add_argument("--layout", action="store_true", help="Experimental editable Word page layout JSON (MinerU only)")
    args = parser.parse_args(argv)
    start = time.monotonic()
    try:
        for path in (args.input, args.output, args.models):
            if not path.is_absolute() or str(path).startswith(("\\\\", "//")):
                raise BridgeError("Only absolute local filesystem paths are accepted", 2)
        source, output, models = (p.resolve() for p in (args.input, args.output, args.models))
        if not 1 <= args.max_pages <= 100 or not 1 <= args.max_chars <= 120000:
            raise BridgeError("Limits must be 1..100 pages and 1..120000 characters", 2)
        if source == output or output.exists() or not output.parent.is_dir():
            raise BridgeError("Output must be a new file in an existing local directory", 2)
        if not source.is_file() or source.suffix.lower() not in {".pdf", ".png", ".jpg", ".jpeg", ".webp"} or not 0 < source.stat().st_size <= 8 * 1024 ** 2:
            raise BridgeError("Input must be a local PDF/PNG/JPEG/WebP no larger than 8 MiB", 2)
        offline_policy(models)
        with contextlib.redirect_stdout(sys.stderr), document_pdf(source, output.parent) as pdf_source:
            pages = pdf_pages(pdf_source, args.max_pages)
            if args.layout and (args.engine != "mineru" or pages > 10):
                raise BridgeError("Layout mode requires MinerU and at most 10 pages", 2)
            verified_models(models, args.engine)
            with runtime_lock(models):
                convert = {"docling": docling_convert, "got-ocr": got_convert, "mineru": mineru_convert}[args.engine]
                if args.layout:
                    text = mineru_convert(pdf_source, models, pages, args.max_chars, layout=True)
                    if len(text.encode("utf-8")) > 32 * 1024 ** 2:
                        raise BridgeError("Layout output exceeds 32 MiB", 2)
                else:
                    text = validate_text(convert(pdf_source, models, pages, args.max_chars), args.max_chars)
                atomic_output(output, text)
        print(json.dumps({"ok": True, "engine": args.engine, "pages": pages,
                          "characters": len(text), "seconds": round(time.monotonic() - start, 3),
                          "offline": True, "output": str(output)}, ensure_ascii=True))
        return 0
    except KeyboardInterrupt:
        print(json.dumps({"ok": False, "code": 130, "error": "Canceled"}))
        return 130
    except Exception as exc:
        code = exc.code if isinstance(exc, BridgeError) else (3 if isinstance(exc, ImportError) else 4)
        message = str(exc)[:1200]
        print(json.dumps({"ok": False, "engine": args.engine, "code": code,
                          "error": message}, ensure_ascii=True))
        print(f"{type(exc).__name__}: {message}", file=sys.stderr)
        return code


if __name__ == "__main__":
    raise SystemExit(main())

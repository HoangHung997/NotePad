"""Bounded, data-only page reconstruction. No document content is executable."""
import base64
import contextlib
import io
import math
import os
from pathlib import Path


def reconstruct(source, pages, vietnamese_models=None):
    import numpy as np
    import pypdfium2 as pdfium
    from PIL import Image, ImageDraw, ImageFont

    result = {"schemaVersion": 1, "pages": [], "warnings": [
        "Bản thử giữ bố cục bằng các hộp chữ sửa được. Bảng, dấu, chữ ký và vùng không chắc chắn giữ dạng ảnh.",
        "Phông/cỡ/kiểu chữ được ước lượng, không phải phông gốc đã xác minh. Đối chiếu dấu tiếng Việt, số tiền, ngày và tài khoản trước khi dùng."
    ]}
    if not 1 <= len(pages) <= 10:
        raise ValueError("Layout export supports 1..10 pages; split larger documents first")
    fonts = Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts"
    candidates = [("Times New Roman", "times.ttf", False, False),
                  ("Times New Roman", "timesbd.ttf", True, False),
                  ("Times New Roman", "timesi.ttf", False, True),
                  ("Times New Roman", "timesbi.ttf", True, True),
                  ("Arial", "arial.ttf", False, False),
                  ("Arial", "arialbd.ttf", True, False),
                  ("Arial", "ariali.ttf", False, True)]
    candidates = [c for c in candidates if (fonts / c[1]).is_file()]
    if not candidates:
        raise ValueError("Install Times New Roman or Arial before layout export")
    reader = None
    if vietnamese_models is not None:
        import easyocr
        reader = easyocr.Reader(["vi", "en"], gpu=False, model_storage_directory=str(vietnamese_models), download_enabled=False, verbose=False)
    with pdfium.PdfDocument(source) as pdf:
        for index, data in enumerate(pages):
            if data.get("page_idx") != index:
                raise ValueError("Incomplete page layout")
            width, height = data["page_size"]
            with contextlib.closing(pdf[index]) as page, contextlib.closing(page.render(scale=2)) as bitmap:
                background = bitmap.to_pil().convert("RGB")
            sx, sy = background.width / width, background.height / height
            blocks = data.get("para_blocks", []) + data.get("discarded_blocks", [])
            graphics = [b["bbox"] for b in blocks if b.get("type") in ("image", "table", "interline_equation")]
            items = []
            for block in sorted(blocks, key=lambda b: b.get("index", 0)):
                if block.get("type") in ("image", "table", "interline_equation"):
                    continue
                for line in block.get("lines", []):
                    spans = line.get("spans", [])
                    if not spans or any(s.get("type") != "text" for s in spans):
                        continue
                    text = " ".join(s.get("content", "") for s in spans).strip()
                    x0, y0, x1, y1 = line["bbox"]
                    if not text or len(text) > 4000 or not all(math.isfinite(v) for v in (x0, y0, x1, y1)):
                        continue
                    if x0 < 0 or y0 < 0 or x1 > width or y1 > height or x1 <= x0 or y1 <= y0:
                        continue
                    if any(x0 < g[2] and x1 > g[0] and y0 < g[3] and y1 > g[1] for g in graphics):
                        continue
                    box = (max(0, int(x0 * sx)), max(0, int(y0 * sy)), min(background.width, math.ceil(x1 * sx)), min(background.height, math.ceil(y1 * sy)))
                    crop = background.crop(box)
                    pixels = np.asarray(crop)
                    # Colored print/signatures remain original pixels rather than fabricated text.
                    colored = pixels.max(axis=2).astype(int) - pixels.min(axis=2).astype(int) > 55
                    if colored.mean() > .012:
                        continue
                    ink = pixels.mean(axis=2) < 130
                    ys, xs = np.where(ink)
                    if len(xs) < 8:
                        continue
                    tight = (int(xs.min()), int(ys.min()), int(xs.max()) + 1, int(ys.max()) + 1)
                    if reader is not None:
                        # MinerU supplies geometry; the installed vi/en recognizer restores
                        # Vietnamese diacritics instead of asking a language model to invent them.
                        gray = np.asarray(crop.convert("L"))
                        recognized = reader.recognize(gray, horizontal_list=[[0, crop.width, 0, crop.height]], free_list=[], detail=1, paragraph=False)
                        if recognized and recognized[0][1].strip():
                            text = recognized[0][1].strip()
                    ink_width, ink_height = tight[2] - tight[0], tight[3] - tight[1]
                    source_ink = Image.fromarray((ink[tight[1]:tight[3], tight[0]:tight[2]] * 255).astype("uint8"))
                    observed = np.asarray(source_ink.resize((max(32, min(900, ink_width)), 40))).astype(float) / 255
                    best = None
                    for family, file, bold, italic in candidates:
                        font = ImageFont.truetype(str(fonts / file), 60)
                        b = font.getbbox(text)
                        if b[2] <= b[0] or b[3] <= b[1]:
                            continue
                        sample = Image.new("L", (b[2] - b[0] + 2, b[3] - b[1] + 2))
                        ImageDraw.Draw(sample).text((-b[0], -b[1]), text, font=font, fill=255)
                        sample = sample.crop(sample.getbbox())
                        expected = np.asarray(sample.resize((observed.shape[1], 40))).astype(float) / 255
                        score = float(np.mean((observed - expected) ** 2)) + (.012 if italic else 0)
                        if block.get("type") == "title" and not bold:
                            score += .025
                        size = max(6, min(48, 60 * ink_height / (sy * sample.height)))
                        candidate = (score, family, size, bold, italic)
                        if best is None or candidate[0] < best[0]:
                            best = candidate
                    _, family, size, bold, italic = best
                    size = round(size * 2) / 2
                    file = next(c[1] for c in candidates if c[0] == family and c[2] == bold and c[3] == italic)
                    measured = ImageFont.truetype(str(fonts / file), int(size * 4)).getlength(text) / 4
                    items.append({"x": (box[0] + tight[0]) / sx, "y": (box[1] + tight[1]) / sy,
                                  "width": ink_width / sx, "height": ink_height / sy,
                                  "text": text, "font": family, "fontSize": size,
                                  "horizontalScale": max(10, min(400, math.floor(100 * ink_width / sx / max(1, measured)))),
                                  "bold": bold, "italic": italic,
                                  "confidence": min(s.get("score", 0) for s in spans)})
                    # Remove only the recognized dark ink, retaining rules, graphics and everything
                    # not represented as editable text. The original source is never modified.
                    paper = tuple(int(v) for v in np.median(pixels.reshape(-1, 3), axis=0))
                    ImageDraw.Draw(background).rectangle(box, fill=paper)
            if not items:
                raise ValueError("No safely editable text detected; refusing image-only Word output")
            output = io.BytesIO()
            background.save(output, format="PNG")
            result["pages"].append({"width": width, "height": height,
                                    "backgroundPng": base64.b64encode(output.getvalue()).decode("ascii"), "items": items})
            background.close()
    if sum(len(i["text"]) for p in result["pages"] for i in p["items"]) > 120000:
        raise ValueError("Layout text exceeds safe limit")
    return result

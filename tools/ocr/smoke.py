"""Generate local synthetic fixtures and run the real offline bridge, never user PDFs."""
from __future__ import annotations

import argparse
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import unicodedata

HERE = Path(__file__).resolve().parent
VI = "B\u00e1o c\u00e1o ti\u1ebfn \u0111\u1ed9 d\u1ef1 \u00e1n"
VI2 = "C\u00f4ng vi\u1ec7c ho\u00e0n th\u00e0nh ng\u00e0y 15 th\u00e1ng 9."


def fixtures(directory):
    from reportlab.pdfgen.canvas import Canvas
    from reportlab.pdfbase import pdfmetrics
    from reportlab.pdfbase.ttfonts import TTFont
    from reportlab.lib.utils import ImageReader
    import pypdfium2 as pdfium
    directory.mkdir(parents=True, exist_ok=True)
    font = Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts/arial.ttf"
    pdfmetrics.registerFont(TTFont("FixtureFont", str(font)))
    for name, vietnamese in (("english", False), ("vietnamese", True)):
        native = directory / f"{name}-native.pdf"
        c = Canvas(str(native), pagesize=(420, 420), invariant=1)
        c.setFont("FixtureFont", 22)
        c.drawString(35, 372, VI if vietnamese else "H2 OCR SMOKE")
        c.setFont("FixtureFont", 14)
        c.drawString(35, 338, VI2 if vietnamese else "Invoice 2026. Total 42.")
        for x in (35, 220, 385):
            c.line(x, 185, x, 305)
        for y in (185, 225, 265, 305):
            c.line(35, y, 385, y)
        for y, left, right in ((278, "Item", "Count"), (238, "Alpha", "12"), (198, "Beta", "30")):
            c.drawString(47, y, left)
            c.drawString(235, y, right)
        c.save()
        png = directory / f"{name}-scan.png"
        with pdfium.PdfDocument(native) as pdf, closing(pdf[0]) as page:
            bitmap = page.render(scale=2)
            try:
                bitmap.to_pil().save(png)
            finally:
                bitmap.close()
        scanned = directory / f"{name}-scan.pdf"
        c = Canvas(str(scanned), pagesize=(420, 420), invariant=1)
        c.drawImage(ImageReader(str(png)), 0, 0, 420, 420)
        c.save()
        with pdfium.PdfDocument(scanned) as pdf, closing(pdf[0]) as page, closing(page.get_textpage()) as text:
            assert not text.get_text_range().strip(), "Scanned fixture must have no embedded text"


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(os.environ["LOCALAPPDATA"]) / "H2Notes/ocr-runtime")
    parser.add_argument("--engine", choices=["docling", "got-ocr", "mineru"])
    parser.add_argument("--timeout", type=int, default=180)
    parser.add_argument("--fixtures-only", action="store_true")
    args = parser.parse_args()
    root = args.root.resolve()
    directory = root / "smoke"
    fixtures(directory)
    if args.fixtures_only:
        print(directory)
        return 0
    if not args.engine:
        parser.error("--engine required")
    import psutil
    cases = []
    for name in ("english-scan", "vietnamese-native", "vietnamese-scan"):
        source = directory / (name + ".pdf")
        output = directory / (args.engine + "-" + name + ".md")
        output.unlink(missing_ok=True)
        log = directory / (args.engine + "-" + name + ".stderr.txt")
        status_log = directory / (args.engine + "-" + name + ".stdout.txt")
        command = [str(root / "venv/Scripts/python.exe"), "-I", "-X", "utf8", str(HERE / "convert.py"),
                   "--engine", args.engine, "--input", str(source), "--output", str(output),
                   "--models", str(root / "models" / args.engine)]
        started = time.monotonic()
        peak_rss = 0
        timed_out = False
        with log.open("w", encoding="utf-8") as err, status_log.open("w", encoding="utf-8") as out:
            process = subprocess.Popen(command, stdout=out, stderr=err)
            monitor = psutil.Process(process.pid)
            while process.poll() is None:
                try:
                    peak_rss = max(peak_rss, monitor.memory_info().rss + sum(
                        child.memory_info().rss for child in monitor.children(recursive=True)))
                except psutil.Error:
                    pass
                if time.monotonic() - started > args.timeout:
                    process.kill()
                    timed_out = True
                    break
                time.sleep(0.25)
            process.wait()
        text = output.read_text(encoding="utf-8") if output.exists() else ""
        tokens = ["Invoice", "2026", "42", "Alpha", "12", "Beta", "30"] if name == "english-scan" else [VI, VI2, "Alpha", "12", "Beta", "30"]
        normalized = unicodedata.normalize("NFC", text).casefold()
        matches = {token: unicodedata.normalize("NFC", token).casefold() in normalized for token in tokens}
        case = {"fixture": name, "exitCode": process.returncode, "timedOut": timed_out,
                "seconds": round(time.monotonic() - started, 2), "peakRssBytes": peak_rss,
                "inputSha256": sha(source), "characters": len(text), "matches": matches,
                "completeConversion": process.returncode == 0 and bool(text.strip()),
                "outputSha256": sha(output) if output.exists() else None,
                "stdout": status_log.read_text(encoding="utf-8")[-1600:]}
        cases.append(case)
        print(json.dumps(case, ensure_ascii=True), flush=True)
        if process.returncode != 0:
            break
    ready = len(cases) == 3 and all(c["completeConversion"] for c in cases) and all(cases[0]["matches"].values())
    evidence = {"engine": args.engine, "passed": ready,
                "definition": "All 3 real conversions complete, all English scanned anchors present. Vietnamese matches reported separately; not a benchmark or accuracy guarantee.",
                "bridgeSha256": sha(HERE / "convert.py"), "cases": cases}
    (directory / (args.engine + "-results.json")).write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    manifest_file = root / "runtime.json"
    manifest = json.loads(manifest_file.read_text(encoding="utf-8"))
    manifest["engines"][args.engine].update(ready=ready, status="ready" if ready else "smoke_failed",
                                           smoke=evidence)
    manifest["engines"][args.engine].pop("reason", None)
    if args.engine == "mineru":
        manifest["engines"][args.engine]["ocrLanguage"] = {
            "requested": ["vi", "en"], "publicAlias": "latin", "effectiveModelKey": "ch",
            "note": "MinerU 3.4.5 maps Latin to its multilingual ch model; no dedicated vi code or English-only fallback."}
    temporary = manifest_file.with_suffix(".json.tmp")
    temporary.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    temporary.replace(manifest_file)
    return 0 if ready else 1


if __name__ == "__main__":
    raise SystemExit(main())

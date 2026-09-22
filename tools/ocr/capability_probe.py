"""Repeated real OCR acceptance on synthetic inputs; never changes runtime readiness."""
import argparse
import json
import subprocess
import time
import unicodedata
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("--runtime", type=Path, required=True)
parser.add_argument("--fixtures", type=Path, required=True)
parser.add_argument("--output", type=Path, required=True)
parser.add_argument("--engines", nargs="+", choices=["docling", "got-ocr", "mineru"], default=["docling", "got-ocr", "mineru"])
args = parser.parse_args()
args.runtime = args.runtime.resolve()
args.fixtures = args.fixtures.resolve()
args.output = args.output.resolve()
args.output.mkdir(parents=True, exist_ok=False)
cases = []
for engine in args.engines:
    for source in ["english-scan.pdf", "vietnamese-scan.pdf", "vietnamese-scan.png"]:
        name = engine + "-" + source.replace(".", "-")
        output = args.output / (name + ".md")
        started = time.monotonic()
        command = [str(args.runtime / "venv/Scripts/python.exe"), "-I", "-X", "utf8",
                   str(Path(__file__).with_name("convert.py")), "--engine", engine,
                   "--input", str(args.fixtures / source), "--output", str(output),
                   "--models", str(args.runtime / "models" / engine)]
        with (args.output / (name + ".log")).open("w", encoding="utf-8") as log:
            process = subprocess.Popen(command, stdout=log, stderr=log, creationflags=subprocess.CREATE_NO_WINDOW)
            try: code = process.wait(timeout=600)
            except subprocess.TimeoutExpired:
                # Kill this owned conversion's descendants, never other OCR or user apps.
                import psutil
                parent = psutil.Process(process.pid)
                for child in parent.children(recursive=True):
                    try: child.kill()
                    except psutil.NoSuchProcess: pass
                parent.kill(); process.wait(); code = -1
        text = output.read_text(encoding="utf-8") if output.exists() else ""
        normal = "".join(c for c in unicodedata.normalize("NFD", text).lower() if not unicodedata.combining(c)).replace("đ", "d")
        anchors = ["alpha", "beta", "12", "30"] + (["invoice", "42"] if source.startswith("english") else ["bao cao", "tien do", "cong viec"])
        missing = [a for a in anchors if a not in normal]
        case = dict(engine=engine, source=source, exit_code=code, seconds=round(time.monotonic()-started, 1),
                    characters=len(text), missing=missing, passed=code == 0 and not missing)
        cases.append(case)
        print(json.dumps(case), flush=True)
        (args.output / "results.json").write_text(json.dumps(cases, ensure_ascii=False, indent=2), encoding="utf-8")
raise SystemExit(0 if all(c["passed"] for c in cases) else 1)

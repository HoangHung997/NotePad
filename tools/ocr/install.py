"""Explicit, isolated CPU installer. Model downloads require a separate switch."""
from __future__ import annotations

import argparse
import fnmatch
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import urllib.parse
import urllib.request
import zipfile

HERE = Path(__file__).resolve().parent
GIB = 1024 ** 3
LOCK = json.loads((HERE / "models.lock.json").read_text(encoding="utf-8"))


def dump(path, data):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(json.dumps(data, indent=2, ensure_ascii=True) + "\n", encoding="utf-8")
    tmp.replace(path)


def size(root):
    return sum(p.stat().st_size for p in root.rglob("*") if p.is_file())


def guard(root, incoming=0, budget=6 * GIB):
    used = size(root)
    free = shutil.disk_usage(root).free
    if used + incoming > budget or free - incoming < 10 * GIB:
        raise RuntimeError(f"Disk guard: used={used}, incoming={incoming}, free={free}; "
                           f"budget={budget}; must leave 10 GiB free")


def digest(path, algorithm="sha256"):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, algorithm).hexdigest()


def fetch(url, destination, root, budget, expected_size=None):
    destination.parent.mkdir(parents=True, exist_ok=True)
    tmp = destination.with_suffix(destination.suffix + ".part")
    offset = tmp.stat().st_size if tmp.exists() else 0
    headers = {"User-Agent": "H2Notes-OCR-Installer/1"}
    if offset:
        headers["Range"] = f"bytes={offset}-"
    request = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(request, timeout=90) as response:
        if urllib.parse.urlparse(response.url).scheme != "https":
            raise RuntimeError("Refusing non-HTTPS download redirect")
        length = int(response.headers.get("Content-Length") or expected_size or 0)
        if response.status != 206:
            offset = 0
        elif not response.headers.get("Content-Range", "").startswith(f"bytes {offset}-"):
            raise RuntimeError("Invalid resume response")
        if expected_size and length and length + offset != expected_size:
            raise RuntimeError("Unexpected download length")
        guard(root, length + 64 * 1024 ** 2, budget)
        initial_used = size(root)
        received = offset
        next_progress = (offset // (128 * 1024 ** 2) + 1) * 128 * 1024 ** 2
        with tmp.open("ab" if offset else "wb") as stream:
            while chunk := response.read(4 * 1024 ** 2):
                received += len(chunk)
                if expected_size and received > expected_size:
                    raise RuntimeError("Download exceeds pinned size")
                if initial_used + received - offset > budget or shutil.disk_usage(root).free < 10 * GIB + len(chunk):
                    raise RuntimeError("Disk reserve reached during model download")
                stream.write(chunk)
                if received >= next_progress:
                    print(f"Downloaded {destination.name}: {received // 1024 ** 2} MiB", flush=True)
                    next_progress += 128 * 1024 ** 2
        if length and received != length + offset:
            raise RuntimeError("Incomplete download")
    tmp.replace(destination)


def run_guarded(command, root, budget, log_name):
    env = dict(os.environ, PIP_NO_CACHE_DIR="1", PIP_DISABLE_PIP_VERSION_CHECK="1",
               PYTHONNOUSERSITE="1", PYTHONUTF8="1", TEMP=str(root / "tmp"), TMP=str(root / "tmp"))
    # Installation never uses credentials from an inherited pip config.
    env["PIP_CONFIG_FILE"] = os.devnull
    for key in list(env):
        if key.startswith("PIP_") and key not in {
            "PIP_NO_CACHE_DIR", "PIP_DISABLE_PIP_VERSION_CHECK", "PIP_CONFIG_FILE"
        }:
            del env[key]
    with (root / log_name).open("w", encoding="utf-8") as log:
        process = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT, env=env)
        try:
            while process.poll() is None:
                guard(root, 512 * 1024 ** 2, budget)
                time.sleep(10)
        except BaseException:
            process.kill()
            process.wait()
            raise
    if process.returncode:
        raise RuntimeError(f"Command failed ({process.returncode}); see {root / log_name}")


def manifest(root):
    target = root / "runtime.json"
    if target.exists():
        data = json.loads(target.read_text(encoding="utf-8"))
    else:
        data = {"schemaVersion": 1, "pythonExecutable": "venv/Scripts/python.exe",
                "modelsRoot": "models", "device": "cpu", "offlineOnly": True,
                "portable": False,
                "portabilityNote": "Manifest/model paths are relative; recreate venv on another machine using a compatible Python 3.12. Base Python is not bundled.",
                "basePython": str(Path(sys.base_prefix) / "python.exe"),
                "engines": {name: {"ready": False, "status": "not_installed",
                                    "modelsPath": "models/" + name}
                            for name in LOCK["engines"]}}
    data["runtimeRoot"] = str(root)
    data["updatedUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    data["installedBytes"] = size(root)
    data["freeBytes"] = shutil.disk_usage(root).free
    dump(target, data)
    return data


def dependencies(root, budget):
    python = root / "venv/Scripts/python.exe"
    if not python.exists():
        run_guarded([sys.executable, "-m", "venv", str(root / "venv")], root, budget, "venv.log")
    pip = [str(python), "-m", "pip", "install", "--only-binary=:all:", "--no-cache-dir", "--no-compile"]
    cpu = ["--index-url", "https://download.pytorch.org/whl/cpu",
           "torch==2.8.0+cpu", "torchvision==0.23.0+cpu"]
    run_guarded(pip + cpu, root, budget, "install-cpu.log")
    run_guarded(pip + ["--index-url", "https://pypi.org/simple", "--report", str(root / "pip-report.json"),
                       "-r", str(HERE / "requirements.in")], root, budget, "install-deps.log")
    run_guarded([str(python), "-m", "pip", "check"], root, budget, "pip-check.log")
    frozen = subprocess.check_output([str(python), "-m", "pip", "freeze", "--all"], text=True)
    (root / "requirements.lock.txt").write_text(frozen, encoding="utf-8")
    data = manifest(root)
    for name in ("docling", "got-ocr"):
        data["engines"][name].update(ready=False, status="dependencies_installed_models_pending")
    data["engines"]["mineru"]["reason"] = "Run --mineru-dependencies and explicit model download, then offline smoke."
    dump(root / "runtime.json", data)


def bundle_python(root, budget):
    """Copy the already available PSF-licensed interpreter, not its site-packages."""
    target = root / "python"
    if target.exists():
        raise RuntimeError("Bundled Python already exists; refusing to merge interpreters")
    base = Path(sys.base_prefix)
    guard(root, 300 * 1024 ** 2, budget)
    target.mkdir()
    for item in base.iterdir():
        if item.is_file() and item.suffix.lower() in {".exe", ".dll", ".txt"}:
            shutil.copy2(item, target / item.name)
    for name in ("DLLs", "Lib"):
        shutil.copytree(base / name, target / name,
                        ignore=shutil.ignore_patterns("site-packages", "__pycache__"))
    subprocess.run([str(target / "python.exe"), "-I", "-c", "import ssl,venv,sys; print(sys.version)"], check=True)
    data = manifest(root)
    data["basePython"] = "python/python.exe"
    data["pythonBundled"] = True
    data["portabilityNote"] = "Self-contained Python included. After moving this installation, recreate venv launchers with python/python.exe -m venv --upgrade --without-pip venv; existing packages remain. Windows x64 only."
    dump(root / "runtime.json", data)


def mineru_dependencies(root, budget):
    python = root / "venv/Scripts/python.exe"
    guard(root, 2 * GIB, budget)
    command = [str(python), "-m", "pip", "install", "--only-binary=:all:", "--no-cache-dir",
               "--no-compile", "--index-url", "https://pypi.org/simple",
               "--report", str(root / "mineru-pip-report.json"),
               "-r", str(HERE / "requirements-mineru.in")]
    run_guarded(command, root, budget, "install-mineru.log")
    run_guarded([str(python), "-m", "pip", "check"], root, budget, "pip-check.log")
    frozen = subprocess.check_output([str(python), "-m", "pip", "freeze", "--all"], text=True)
    (root / "requirements.lock.txt").write_text(frozen, encoding="utf-8")
    data = manifest(root)
    data["engines"]["mineru"].update(ready=False, status="dependencies_installed_models_pending")
    # A shared dependency changed (PDFium), so all adapters must be retested.
    for entry in data["engines"].values():
        entry["ready"] = False
        if entry.get("status") == "ready":
            entry["status"] = "shared_dependencies_changed_smoke_pending"
    data["engines"]["mineru"].pop("reason", None)
    dump(root / "runtime.json", data)


def easyocr_catalog(root):
    """Read EasyOCR's pinned download metadata from the isolated runtime, not host Python."""
    python = root / "venv/Scripts/python.exe"
    if not python.exists():
        raise RuntimeError("OCR venv is missing before EasyOCR model download")
    code = (
        "import json; from easyocr.config import detection_models, recognition_models; "
        "print(json.dumps({'detection': detection_models, 'recognition': recognition_models}))"
    )
    env = dict(os.environ, PYTHONNOUSERSITE="1", PYTHONUTF8="1")
    result = subprocess.run([str(python), "-I", "-c", code], capture_output=True, text=True, env=env, timeout=30)
    if result.returncode:
        raise RuntimeError("EasyOCR is not importable from the isolated OCR runtime: " + result.stderr[-500:])
    return json.loads(result.stdout)


def download_models(root, engine, budget):
    spec = LOCK["engines"][engine]
    if spec.get("blocked"):
        raise RuntimeError(spec["blocked"])
    model_root = root / "models" / engine
    model_root.mkdir(parents=True, exist_ok=True)
    receipt = {"schemaVersion": 1, "engine": engine, "files": [], "sources": spec}
    for repo in spec["repositories"]:
        url = f"https://huggingface.co/api/models/{repo['repo']}/revision/{repo['revision']}?blobs=true"
        with urllib.request.urlopen(url, timeout=45) as response:
            metadata = json.load(response)
        if metadata["sha"] != repo["revision"]:
            raise RuntimeError("Model revision mismatch")
        selected = [item for item in metadata["siblings"] if any(
            fnmatch.fnmatchcase(item["rfilename"], pattern) for pattern in repo["patterns"])]
        if not selected:
            raise RuntimeError("Pinned model selection empty")
        for item in selected:
            name = item["rfilename"]
            path = (model_root / repo["directory"] / name).resolve()
            if not path.is_relative_to(model_root.resolve()) or path.suffix == ".py":
                raise RuntimeError("Unsafe model path")
            if not path.exists():
                print(f"Downloading {engine}: {name} ({item['size']} bytes)", flush=True)
                fetch(f"https://huggingface.co/{repo['repo']}/resolve/{repo['revision']}/{name}",
                      path, root, budget, item["size"])
            if path.stat().st_size != item["size"]:
                raise RuntimeError(f"Model size mismatch: {name}")
            sha = digest(path)
            if item.get("lfs"):
                valid = sha == item["lfs"]["sha256"]
            else:
                valid = hashlib.sha1(f"blob {item['size']}\0".encode() + path.read_bytes()).hexdigest() == item["blobId"]
            if not valid:
                raise RuntimeError(f"Model hash mismatch: {name}")
            receipt["files"].append({"path": path.relative_to(model_root).as_posix(),
                                     "size": path.stat().st_size, "sha256": sha})
    if spec.get("easyocr"):
        catalog = easyocr_catalog(root)
        detection_models = catalog["detection"]
        recognition_models = catalog["recognition"]
        for key in spec["easyocr"]:
            item = detection_models[key] if key in detection_models else recognition_models["gen2"][key]
            path = model_root / "easyocr" / item["filename"]
            if not path.exists():
                archive = root / "tmp" / (key + ".zip")
                print(f"Downloading EasyOCR {key}", flush=True)
                fetch(item["url"], archive, root, budget)
                with zipfile.ZipFile(archive) as zf:
                    member = zf.getinfo(item["filename"])
                    guard(root, member.file_size, budget)
                    path.parent.mkdir(parents=True, exist_ok=True)
                    with zf.open(member) as src, path.open("wb") as dst:
                        shutil.copyfileobj(src, dst)
                archive.unlink()
            if digest(path, "md5") != item["md5sum"]:
                raise RuntimeError(f"EasyOCR pinned checksum mismatch: {key}")
            receipt["files"].append({"path": path.relative_to(model_root).as_posix(),
                                     "size": path.stat().st_size, "sha256": digest(path),
                                     "source": item["url"], "upstreamMd5": item["md5sum"]})
    dump(model_root / "models.json", receipt)
    if engine == "mineru":
        dump(model_root / "mineru.json", {"config_version": "1.3.2", "model-source": "local",
                                           "models-dir": {"pipeline": str(model_root)}})
    data = manifest(root)
    data["engines"][engine].update(ready=False, status="models_installed_smoke_pending")
    dump(root / "runtime.json", data)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(os.environ["LOCALAPPDATA"]) / "H2Notes/ocr-runtime")
    parser.add_argument("--dependencies", action="store_true")
    parser.add_argument("--bundle-python", action="store_true")
    parser.add_argument("--mineru-dependencies", action="store_true")
    parser.add_argument("--download-models", choices=list(LOCK["engines"]))
    parser.add_argument("--approve-model-download", action="store_true")
    parser.add_argument("--budget-gib", type=float, default=18)
    args = parser.parse_args()
    root = args.root.resolve()
    root.mkdir(parents=True, exist_ok=True)
    (root / "tmp").mkdir(exist_ok=True)
    guard(root, budget=args.budget_gib * GIB)
    if args.download_models and not args.approve_model_download:
        parser.error("Model download requires explicit --approve-model-download")
    if args.dependencies:
        dependencies(root, args.budget_gib * GIB)
    if args.bundle_python:
        bundle_python(root, args.budget_gib * GIB)
    if args.mineru_dependencies:
        mineru_dependencies(root, args.budget_gib * GIB)
    if args.download_models:
        download_models(root, args.download_models, args.budget_gib * GIB)
    print(json.dumps(manifest(root), indent=2))


if __name__ == "__main__":
    main()

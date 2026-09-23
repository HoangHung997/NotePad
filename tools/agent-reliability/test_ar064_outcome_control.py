#!/usr/bin/env python3
"""Windows CI-only old-wrapper control; restore exact bytes before rebuilding current code.

This never switches a branch, resets the index, commits, pushes or sends model requests.
A temporary build/test failure is a failure, not a successful negative control.
"""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys

BASE = "1aef7d3386ea22ae1f8dd30e6ff1ad4948a6016e"
FILES = (
    "experiments/H2AgentLab/Plugins/PluginManager.cs",
    "experiments/H2AgentLab/Plugins/PluginManager.Lifecycle.cs",
)
FILTER = "AR-064 outcome wrapper preserves typed metadata before host validation"
MESSAGE = "Version wrapper downgraded the typed executor to its legacy string method."


def run(root: Path, args: list[str], timeout: int = 30) -> subprocess.CompletedProcess[bytes]:
    return subprocess.run(args, cwd=root, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                          timeout=timeout, check=False)


def git(root: Path, *args: str) -> bytes:
    result = run(root, ["git", *args])
    if result.returncode:
        raise RuntimeError("Git source check failed: " + " ".join(args))
    return result.stdout


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def main() -> int:
    if os.name != "nt" or os.environ.get("GITHUB_ACTIONS") != "true" \
            or os.environ.get("GITHUB_REPOSITORY") != "HoangHung997/NotePad":
        raise RuntimeError("This control is limited to an isolated Windows Actions checkout.")
    root = Path(os.environ["GITHUB_WORKSPACE"]).resolve(strict=True)
    if Path(git(root, "rev-parse", "--show-toplevel").decode().strip()).resolve() != root:
        raise RuntimeError("Workspace is not the repository root.")
    if git(root, "status", "--porcelain", "--untracked-files=all").strip():
        raise RuntimeError("Control requires a clean checkout; preserve unrelated changes.")
    code_sha = git(root, "rev-parse", "HEAD").decode().strip()
    if not re.fullmatch(r"[0-9a-f]{40}", code_sha):
        raise RuntimeError("Missing exact source identity.")
    original = {name: (root / name).read_bytes() for name in FILES}
    previous = {name: git(root, "show", BASE + ":" + name) for name in FILES}
    if "WrapVersionExecutor" not in original[FILES[0]].decode("utf-8-sig"):
        raise RuntimeError("Current source does not contain the reviewed wrapper fix.")
    if "VersionOutcomeExecutor" in previous[FILES[1]].decode("utf-8-sig"):
        raise RuntimeError("The declared old control is not the legacy-only wrapper.")
    evidence = root / "artifacts" / "ar064" / "outcome-control"
    evidence.mkdir(parents=True, exist_ok=True)
    status: dict = {"code_sha": code_sha, "old_source_sha": BASE,
                    "level": "C# counterfactual component control; not native E3/E4",
                    "expected_failures": 7, "result": "NOT_RUN",
                    "restoration": "NOT_RUN", "current_rebuild": "NOT_RUN",
                    "working_bytes_before": {name: digest(data) for name, data in original.items()}}
    build = ["dotnet", "build", "H2Notes.Avalonia.slnx", "-c", "Release", "--no-restore"]
    failure: BaseException | None = None
    try:
        for name, data in previous.items():
            (root / name).write_bytes(data)
        built = run(root, build, timeout=240)
        (evidence / "old-build.log").write_bytes(built.stdout)
        status["old_build_exit"] = built.returncode
        if built.returncode:
            raise RuntimeError("Old source did not build; this is not a behavioral control pass.")
        tested = run(root, ["dotnet", "run", "--project", "tests/H2Notes.Tests/H2Notes.Tests.csproj",
                           "-c", "Release", "--no-build", "--", "--filter", FILTER], timeout=90)
        (evidence / "old-wrapper.log").write_bytes(tested.stdout)
        text = tested.stdout.decode("utf-8-sig", errors="replace")
        status["old_test_exit"] = tested.returncode
        status["expected_failure_messages"] = text.count(MESSAGE)
        status["result_lines"] = re.findall(r"RESULT: (\d+) passed, (\d+) failed", text)
        if tested.returncode != 1 or status["result_lines"] != [("0", "7")] \
                or status["expected_failure_messages"] != 7:
            raise RuntimeError("Old wrapper did not reproduce exactly seven declared metadata failures.")
        status["result"] = "EXPECTED_BEHAVIOR_REPRODUCED"
    except BaseException as exc:
        failure = exc
        status["result"] = "CONTROL_FAILED"
        status["error_type"] = type(exc).__name__
    finally:
        for name, data in original.items():
            (root / name).write_bytes(data)
        restored = {name: digest((root / name).read_bytes()) for name in FILES}
        status["working_bytes_after"] = restored
        status["restoration"] = "PASS" if restored == status["working_bytes_before"] else "FAIL"
        status["clean_after_restore"] = not bool(git(root, "status", "--porcelain", "--untracked-files=all").strip())
        (evidence / "control.json").write_text(json.dumps(status, indent=2), encoding="utf-8")
    if status["restoration"] != "PASS" or not status["clean_after_restore"]:
        raise RuntimeError("Source restoration was not clean. Inspect without reset.")
    # Even when the control failed, never leave old binaries for a later step to mistake as current.
    rebuilt = run(root, build, timeout=240)
    (evidence / "current-rebuild.log").write_bytes(rebuilt.stdout)
    status["current_rebuild"] = "PASS" if rebuilt.returncode == 0 else "FAIL"
    (evidence / "control.json").write_text(json.dumps(status, indent=2), encoding="utf-8")
    if rebuilt.returncode:
        raise RuntimeError("Current-source rebuild failed after exact restoration.")
    if failure is not None:
        raise failure
    print("OLD WRAPPER: 0 passed / 7 expected failures; exact current source restored and rebuilt.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print("CONTROL FAILED: " + str(error), file=sys.stderr)
        raise SystemExit(1)

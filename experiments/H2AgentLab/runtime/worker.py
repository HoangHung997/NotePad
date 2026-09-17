"""Trusted bootstrap; generated code runs only inside the OS AppContainer."""
import os
import runpy
import sys
import traceback

root = os.getcwd()
os.makedirs("output", exist_ok=True)
os.makedirs("tmp", exist_ok=True)
with open("stdout.log", "w", encoding="utf-8", buffering=1) as out, open(
    "stderr.log", "w", encoding="utf-8", buffering=1
) as err:
    sys.stdout, sys.stderr = out, err
    try:
        runpy.run_path(os.path.join(root, "task.py"), run_name="__main__")
    except BaseException:
        traceback.print_exc()
        sys.exit(1)

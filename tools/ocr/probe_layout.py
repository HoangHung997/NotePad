"""Explicit local-only layout inspection; never sends the supplied document to AI."""
import argparse
import json
import os
from pathlib import Path
import runpy

parser = argparse.ArgumentParser()
parser.add_argument("--runtime", type=Path, required=True)
parser.add_argument("--input", type=Path, required=True)
parser.add_argument("--output", type=Path, required=True)
args = parser.parse_args()
bridge = runpy.run_path(str(Path(__file__).with_name("convert.py")))
models = args.runtime.resolve() / "models/mineru"
bridge["offline_policy"](models)
bridge["verified_models"](models, "mineru")
os.environ["MINERU_TOOLS_CONFIG_JSON"] = str(models / "mineru.json")
from mineru.backend.pipeline import pipeline_middle_json_mkcontent
pipeline_middle_json_mkcontent.union_make = lambda info, *_: json.dumps(info, ensure_ascii=False)
with bridge["runtime_lock"](models):
    result = bridge["mineru_convert"](args.input, models, bridge["pdf_pages"](args.input, 10), 120000)
bridge["atomic_output"](args.output, result)

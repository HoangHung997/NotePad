#!/usr/bin/env python3
"""AR-000 only: source/CI inventory and additive documentation checkpoint.

No application, provider, Office process, model, or personal-data test is run.
This tool refuses other branches, source changes, dirty trees and changed masters.
A successful capture is E0, never a green Agent or multi-PC acceptance result.
"""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import urllib.request
import unittest

BASE = "1283bc13e07c3cd47d04886166de3dfc595422c0"
REPO = "HoangHung997/NotePad"
BRANCH = "feature/h2-agent-reliability-ar-000"
CI_RUN = 35687637186
TRACKER = "docs/H2_AGENT_RELIABILITY_IMPLEMENTATION_TASKS.md"
SCRIPT = "tools/agent-reliability/capture_baseline.py"
WORKFLOW = ".github/workflows/h2-ar000-baseline.yml"
OUT = "docs/agent-reliability/AR-000"
MASTER = ["README.md", "docs/H2_AGENT_MASTER_SPEC.md", "docs/H2_AGENT_MASTER_TASKS.md",
          "docs/H2_PRODUCT_MASTER_SPEC.md", "docs/H2_PRODUCT_MASTER_TASKS.md"]
DOCS = MASTER + [TRACKER, "docs/H2_AGENT_RELIABILITY_IMPLEMENTATION_SPEC.md",
    "docs/H2_AGENT_CONSOLIDATED_REVIEW_AND_SOLUTION_2026-09-22.md",
    "docs/H2_AGENT_CHAT_SURFACE_SPEC.md", "docs/H2_NOTES_NON_AI_BUG_LEDGER.md",
    "docs/H2_SYNC_COORDINATOR_EVENT_ARCHITECTURE.md"]
OUTPUTS = MASTER + [TRACKER, OUT + "/baseline.json", OUT + "/baseline.md"]
ALLOWED = set(OUTPUTS + [SCRIPT, WORKFLOW])
LAB = "experiments/H2AgentLab/"
ADAPTER = LAB + "Integration/H2ProductionAgentAdapter.cs"
CONTEXT = LAB + "Integration/H2ProductionAgentAdapter.Context.cs"
OFFICE = LAB + "Integration/H2OfficeRuntimeTools.cs"
COM = "experiments/H2AgentLab.OfficeHost/ComOfficeBackend.cs"
REGISTRY = LAB + "Tools/NormalRuntimeToolRegistry.cs"
GUARD = LAB + "V2ArchitectureTests.cs"
ORCHESTRATED = LAB + "Tasking/AgentOrchestratedRun.cs"
DOMAINS = LAB + "Integration/H2ProductionToolSession.Domains.cs"


def git(*args: str) -> str:
    return subprocess.check_output(["git", *args], text=True).strip()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def rewrite_tracker(text: str, handoff: dict) -> tuple[str, str]:
    row = "| AR-000 | Chốt baseline, ownership và traceability | Không | E0 + execution inventory | NOT_STARTED |"
    heading = "### [ ] AR-000 — Baseline và hợp nhất quyền điều hành đợt AR"
    require(text.count(row) == text.count(heading) == 1, "AR-000 is no longer the unstarted task; reconcile instead of overwriting.")
    pattern = r"```yaml\r?\n(schema_version: 1\r?\nspec_version: H2-AR-SPEC-1\.0\r?\n.*?)\r?\n```"
    matches = list(re.finditer(pattern, text, re.S))
    require(len(matches) == 1, "Expected exactly one canonical initial SESSION HANDOFF.")
    previous = matches[0].group(1)
    require("active_task: AR-000" in previous and "phase: NOT_STARTED" in previous,
            "An active checkpoint appeared; refusing to take it over automatically.")
    # JSON is a strict YAML-1.2 subset. No optional YAML dependency is needed on the runner.
    replacement = "```yaml\n" + json.dumps(handoff, ensure_ascii=False, indent=2) + "\n```"
    text = text[:matches[0].start()] + replacement + text[matches[0].end():]
    text = text.replace(row, row.replace("NOT_STARTED", "DONE"), 1)
    text = text.replace(heading, heading.replace("[ ]", "[x]"), 1)
    anchor = "### [ ] AR-001 — Đồng bộ tên/giới hạn công cụ và full CI"
    require(text.count(anchor) == 1, "AR-001 anchor changed.")
    evidence = ("**AR-000 evidence — 2026-09-22:** [baseline and source owners](agent-reliability/AR-000/baseline.md) "
                "and [machine-readable manifest](agent-reliability/AR-000/baseline.json). "
                "E0 baseline accepted with existing CI RED; no runtime repair or E3/E4/E5 PASS is claimed. "
                "Only AR-000 is closed. AR-001 is next, NOT_STARTED; MB-124–127 and all old acceptance debts remain unchanged.\n\n")
    return text.replace(anchor, evidence + anchor, 1), previous


def api(path: str) -> dict:
    require(path.startswith("/actions/runs/"), "Only same-repository workflow inventory is permitted.")
    token = os.environ.get("GH_TOKEN", "")
    require(bool(token), "Existing GitHub Actions token is required for bounded CI metadata reads.")
    request = urllib.request.Request("https://api.github.com/repos/" + REPO + path,
        headers={"Authorization": "Bearer " + token, "Accept": "application/vnd.github+json",
                 "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "H2-AR000-baseline"})
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def capture() -> None:
    require(os.environ.get("GITHUB_REPOSITORY") == REPO, "Wrong repository.")
    require(os.environ.get("AR_BRANCH") == BRANCH, "Only the approved implementation branch may be used.")
    head = git("rev-parse", "HEAD")
    require(head == os.environ.get("AR_HEAD_SHA"), "Branch advanced during capture; reconcile first.")
    require(not git("status", "--porcelain"), "Working tree is not clean; refusing to overwrite work.")
    require(git("rev-parse", "origin/main") == BASE, "Main advanced; re-audit the new main before capture.")
    git("merge-base", "--is-ancestor", BASE, head)
    changed = set(git("diff", "--name-only", BASE, head).splitlines())
    require(changed <= {SCRIPT, WORKFLOW}, "Unexpected existing branch work; do not replace its checkpoint.")
    require(not git("diff", BASE, head, "--", "src", "experiments", "tests"), "Runtime/test source changed; AR-000 is baseline only.")
    original = {path: Path(path).read_bytes().decode("utf-8") for path in DOCS}
    for path in DOCS:
        require(git("rev-parse", f"HEAD:{path}") == git("rev-parse", f"{BASE}:{path}"),
                "Required document changed: " + path)
    tracked = git("ls-files").splitlines()
    sources: dict[str, dict] = {}

    def anchor(path: str, needle: str) -> dict:
        require(path in tracked, "Source owner missing: " + path)
        text = Path(path).read_text(encoding="utf-8-sig")
        index = text.find(needle)
        require(index >= 0, "Baseline observation changed; review before closing AR-000: " + path + " / " + needle)
        line = text.count("\n", 0, index) + 1
        blob = git("rev-parse", f"HEAD:{path}")
        sources[path] = {"blob_sha": blob, "content_sha256": hashlib.sha256(Path(path).read_bytes()).hexdigest()}
        return {"path": path, "line": line, "needle": needle, "blob_sha": blob}

    definitions = [
        ("B01", "CURRENT_CONTRACT_DRIFT", ["AR-001"],
         "Production StablePrefix requests search_skills; the canonical callable is list_skills.",
         [(ADAPTER, "discover skills with search_skills"), (LAB + "Tools/SkillRuntimeTools.cs", 'SearchToolName = "list_skills"')]),
        ("B02", "CURRENT_CONTRACT_DRIFT", ["AR-001"],
         "Excel schema and adapter permit 200 patches; COM backend rejects above 128. No boundary fix is included here.",
         [(OFFICE, "maxItems = 200"), (OFFICE, "cells.Length is < 1 or > 200"), (COM, "request.Cells.Count is < 1 or > 128")]),
        ("B03", "CURRENT_CI_FAILURE", ["AR-001"],
         "Registry exposes read_tool_output but the exact-name architecture guard omits it; existing CI fails at this guard.",
         [(REGISTRY, 'new("read_tool_output"'), (GUARD, "Canonical normal runtime registry preserves callable metadata")]),
        ("B04", "CURRENT_CAPABILITY_LIMITATION", ["AR-021"],
         "Excel read operations fall back to a whole-session snapshot, not explicit range paging; cumulative UsedRange exceeds a 5000-cell bound.",
         [(OFFICE, "else result = await Client.SnapshotExcelAsync"), (COM, "MaxExcelCells = 5_000"), (COM, "rowCount * colCount + totalCells > MaxExcelCells")]),
        ("B05", "CURRENT_STATE_TOKEN_RISK", ["AR-020", "AR-021"],
         "Excel token basis includes active sheet and selection; Word token basis includes selection. UI movement and content revision are not separated.",
         [(COM, "activeSheetName,\n            selectionAddress,\n            sheets"), (COM, "selectionStart,\n            selectionEnd,\n            selectionText,\n            paragraphs")]),
        ("B06", "CURRENT_PARTIAL_MUTATION_RISK", ["AR-022", "AR-041"],
         "Excel validates each cell address inside the mutating loop. A later invalid cell can follow an earlier applied write; no destructive live reproduction was performed.",
         [(COM, "foreach (var patch in request.Cells)"), (COM, "ValidateSingleCellAddress(patch.Address)"), (COM, "cell.Value2 = patch.Value")]),
        ("B07", "CURRENT_DIAGNOSTIC_LIMITATION", ["AR-011", "AR-020"],
         "Foreground Office enrichment has a three-second timeout and returns null on selected failures; execution OfficeHost timeout is sixty seconds.",
         [(CONTEXT, "defaultTimeout: TimeSpan.FromSeconds(3)"), (CONTEXT, "{ return null; }"), (OFFICE, "defaultTimeout: TimeSpan.FromSeconds(60)")]),
        ("B08", "CURRENT_PRODUCTION_CONTEXT_GAP", ["AR-010", "AR-050", "AR-051", "AR-052"],
         "Lab uses LabSessionContextAdapter and RuntimeCompactionCoordinator; H2 builds a separate AgentContextInput and snapshots the last 32 turns. Shared primitives do not prove equal production compaction/full-wire budgeting.",
         [(ORCHESTRATED, "new LabSessionContextAdapter"), (ORCHESTRATED, "new RuntimeCompactionCoordinator"), (ADAPTER, "new AgentContextInput"), (CONTEXT, "TakeLast(32)")]),
        ("B09", "CURRENT_RECOVERY_LIMITATION", ["AR-031", "AR-040", "AR-041", "AR-052"],
         "Recent-task archive Load converts interrupted nonterminal records to Failed. Durable summaries/replay are not proof of resumable in-flight work or uncertain-write reconciliation.",
         [(ADAPTER, "Agent task was interrupted when the previous H2 process ended."), (ADAPTER, "Status = H2AgentTaskStatus.Failed")]),
        ("B10", "CURRENT_CAPABILITY_LIMITATION", ["AR-060"],
         "Production registers HTTP fetch/download/extract/metadata/feed only. Search and a real browser backend are deliberately not advertised without configuration.",
         [(DOMAINS, "new HttpWebResearchBackend(http)"), (DOMAINS, '["web.fetch", "web.download", "web.extract", "web.get_metadata", "web.read_feed"]')]),
    ]
    findings = [{"id": number, "classification": classification, "owners": owners,
                 "observation": observation, "evidence_level": "E0", "live_reproduction": "NOT_RUN",
                 "anchors": [anchor(path, needle) for path, needle in needles]}
                for number, classification, owners, observation, needles in definitions]
    guard = Path(GUARD).read_text(encoding="utf-8-sig")
    guard_block = guard.split('Test("Canonical normal runtime registry preserves callable metadata"', 1)[1].split('\n        Test(', 1)[0]
    require('"read_tool_output"' not in guard_block, "The registry guard already changed; reclassify B03.")

    owner_names = ["App.axaml.cs", "AiChatPanel.Agent.cs", "WorkAssistantCompactWindow.Conversation.cs",
        "LabWindow.cs", "AgentRuntimeFactory.cs", "AgentRuntime.cs", "AgentOrchestrator.cs",
        "AgentTransportFactory.cs", "H2ProductionToolSession.cs", "H2ProductionToolSession.Desktop.cs",
        "AgentRuntimeDomainVerification.cs", "ArtifactStore.cs", "RuntimeCompactionCoordinator.cs",
        "H2ProductionAgentBridgeTests.cs", "H2SyncCoordinatorContracts.cs", "H2CoordinatorSqliteStore.cs"]
    owners = {}
    for name in owner_names:
        matches = [p for p in tracked if p.endswith("/" + name) and p.startswith(("src/", "experiments/", "tests/"))]
        require(bool(matches), "Call-graph owner not found: " + name)
        owners[name] = [{"path": p, "blob_sha": git("rev-parse", f"HEAD:{p}")} for p in matches]

    run = api(f"/actions/runs/{CI_RUN}")
    require(run.get("head_sha") == BASE and run.get("conclusion") == "failure", "Historical CI metadata differs; re-audit it.")
    jobs = api(f"/actions/runs/{CI_RUN}/jobs?per_page=100")
    require(jobs.get("total_count") == len(jobs.get("jobs", [])), "CI jobs are paged; do not record partial evidence.")
    artifacts = api(f"/actions/runs/{CI_RUN}/artifacts?per_page=100")
    steps = [{"job_id": job["id"], "number": step["number"], "name": step["name"], "conclusion": step.get("conclusion")}
             for job in jobs["jobs"] for step in job.get("steps", [])]
    require(any("v2 architecture guard" in s["name"] and s["conclusion"] == "failure" for s in steps), "Expected baseline failure changed.")
    require(artifacts.get("total_count") == 0, "Artifact inventory changed; do not claim no artifacts.")
    now = dt.datetime.now(dt.timezone.utc).isoformat()
    pr = int(os.environ["AR_PR_NUMBER"])
    capture_url = f"https://github.com/{REPO}/actions/runs/{os.environ['GITHUB_RUN_ID']}"
    ci = {"id": CI_RUN, "head_sha": BASE, "url": run["html_url"], "conclusion": "failure",
          "created_at": run["created_at"], "artifact_count": 0, "steps": steps,
          "log_inspection": {"job_id": 106617792240, "source": "GitHub connector decoded job log inspected in this AR-000 session",
              "h2_tests": {"passed": 590, "failed": 0}, "solution_build": {"warnings": 31, "errors": 0},
              "desktop_safety": {"passed": 4, "failed": 0}, "agent_v1": {"passed": 18, "failed": 0},
              "architecture_guard": {"passed": 34, "failed": 1},
              "failure": "Canonical normal runtime registry deleted or invented callable tools.",
              "downstream_suites": "SKIPPED_AFTER_GUARD", "publish": "NOT_RUN_NO_ARTIFACT",
              "nas_self_test": "LOCAL_PROTOCOL_ONLY_NOT_E5"}}
    commands = ["dotnet restore H2Notes.Avalonia.slnx", "dotnet build H2Notes.Avalonia.slnx -c Release --no-restore",
        "dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build",
        "dotnet run --project experiments/H2AgentLab/H2AgentLab.csproj -c Release --no-build -- --v2-guard-test .artifacts/agent-reliability/AR-001/guard"]
    next_action = ("On existing branch " + BRANCH + ", first inspect git status --short --branch and fetch origin without reset; "
        "reconcile newer main/checkpoint/PR work, then run the v2 guard command recorded in this manifest to reproduce B03. "
        "Execute AR-001 only: canonical skill name, read_tool_output exact-set guard, shared Excel 128/129/200 verdict, "
        "RC-02 and full CI/publish smoke. Do not start paging/memory or create a duplicate branch.")
    handoff = {"schema_version": 1, "spec_version": "H2-AR-SPEC-1.0", "repository": REPO,
        "canonical_branch": "main", "research_baseline_sha": "ad8c1082e722546a997b4e78b687980d4766622f",
        "phase": "AR-000_BASELINE_ACCEPTED_NEXT_AR-001", "active_task": "AR-001",
        "implementation_status": "NOT_STARTED", "acceptance_status": "NOT_RUN", "implementation_branch": BRANCH,
        "active_pr": pr, "owner_session": "chatgpt-ar000-2026-09-22", "last_code_commit": BASE,
        "last_validated_code_commit": BASE, "last_validation_result": "CI_RED_BASELINE_NOT_FULL_PASS",
        "capture_checked_head": head, "checkpoint_commit_lookup": "git log -1 --format=%H -- " + TRACKER,
        "working_tree": "CI checkout clean before capture; docs-only commit/push step must finish clean; user-PC tree NOT_ACCESSIBLE",
        "uncommitted_files": [], "checkpoint_saved_at_utc": now,
        "completed_this_session": [{"task": "AR-000", "implementation_status": "IMPLEMENTED", "evidence": "E0 + execution inventory", "baseline_acceptance": "ACCEPTED_WITH_CI_RED"}],
        "remaining_in_active_task": ["AR-001 has not been implemented; fix B01/B02/B03 and run its required corpus/full CI."],
        "last_test_commands": [{"source": ".github/workflows/avalonia-ci.yml at " + BASE,
            "run": CI_RUN, "note": "Exact executed commands live in that workflow and job log; commands_for_AR001 are proposed reproduction commands, not newly executed .NET commands."},
            {"command": "python3 " + SCRIPT + " --self-test", "execution": "AR000_DOCUMENTATION_TESTS_ONLY"},
            {"command": "python3 " + SCRIPT, "execution": "E0_SOURCE_CI_INVENTORY_ONLY"}],
        "ci_runs": [{"id": CI_RUN, "code_sha": BASE, "result": "FAILURE", "owner": "AR-001"},
                    {"url": capture_url, "checked_head": head, "source_checks": "E0_CAPTURED", "job_final_status": "Consult run; checkpoint written before commit/push"}],
        "evidence_locations": [OUT + "/baseline.json", OUT + "/baseline.md"],
        "known_failures": ["B01 skill-name drift", "B02 200/128 Excel contract drift", "B03 architecture guard failure; downstream suites and publish did not run", "B04-B10 remain open source-observed limitations/risks; see baseline"],
        "external_blockers": ["No local checkout/.NET/PowerShell in assistant container; GitHub CI is the executable environment", "User model/Office/CAD/session not accessible: E3/E4 NOT_RUN / AWAITING_ENVIRONMENT; one-PC tests still required"],
        "deferred_acceptance": [{"id": "AR-083", "status": "DEFERRED_BY_USER", "scope": "Physical two-PC/NAS acceptance", "blocks_independent_implementation": False, "blocks_claim_of_certified_multi_pc": True}],
        "pending_user_decisions": [], "next_exact_action": next_action, "next_task_if_active_done": "AR-010"}
    revised, old_handoff = rewrite_tracker(original[TRACKER], handoff)
    manifest = {"task": "AR-000", "captured_at_utc": now, "code_sha": BASE, "checked_head": head,
        "branch": BRANCH, "pr": pr, "capture_run": capture_url, "runtime_diff": "EMPTY_VERIFIED",
        "initial_repo_inventory": {"branches": ["main"], "open_prs": 0, "checked_again_before_branch_creation": True,
            "local_checkout": "NONE_IN_ASSISTANT_CONTAINER", "user_working_tree": "NOT_ACCESSIBLE"},
        "documents_read": [{"path": p, "blob_sha": git("rev-parse", f"{BASE}:{p}")} for p in DOCS],
        "source_owners": owners, "source_files": sources, "findings": findings, "baseline_ci": ci,
        "environment": {"E0": "SOURCE_AND_CI_INVENTORY_CAPTURED", "E1": "EXISTING_CI_PARTIAL_PASS_FULL_GUARD_FAIL",
            "E2": "EXISTING_CONCRETE_BRIDGE_WITH_SCRIPTED_TRANSPORT_FIXTURES_NOT_NATIVE_ACCEPTANCE",
            "E3": "NOT_RUN_AWAITING_ENVIRONMENT", "E4": "NOT_RUN_AWAITING_ENVIRONMENT", "E5": "DEFERRED_BY_USER_AR-083",
            "configured_user_model": "NOT_INSPECTED_NOT_CHANGED", "native_office_cad": "NOT_ACCESSIBLE_NOT_LAUNCHED",
            "new_endpoints_credentials_external_messages": "NONE", "model_budget_used": 0},
        "commands_for_AR001": commands, "next_exact_action": next_action,
        "previous_handoff_archived_not_active": old_handoff,
        "historical_gates_unchanged": ["MB-124", "MB-125", "MB-126", "MB-127", "H2M-133F", "H2M-133G..K", "H2-NONAI-001/004/006", "AR-083"]}
    Path(OUT).mkdir(parents=True, exist_ok=True)
    Path(OUT + "/baseline.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    rows = "\n".join("| " + f["id"] + " | " + f["observation"] + " | " + ", ".join(f["owners"]) + " |" for f in findings)
    source_rows = "\n".join("- `" + name + "`: " + ", ".join("`" + entry["path"] + "`" for entry in entries) for name, entries in owners.items())
    report = f"""# AR-000 — actual baseline / traceability

Status: **E0 BASELINE ACCEPTED WITH CI RED**. Not a new specification and not product acceptance.

Code: `{BASE}`. Capture checkout: `{head}`. Branch: `{BRANCH}`. PR: #{pr}.
[Capture run]({capture_url}) · [Baseline CI]({run['html_url']}) · [Manifest](baseline.json).
Only AR-000 is closed; the canonical [SESSION HANDOFF](../../H2_AGENT_RELIABILITY_IMPLEMENTATION_TASKS.md) starts AR-001 as NOT_STARTED.

## Ownership and non-overwrite checks

Remote was inspected twice: only main, no open PR or active AR owner. Branch was created from current main, not a research SHA.
The capture refuses dirty worktrees, changed master/tracker blobs, advanced main, unexpected branch changes and any runtime/test diff.
No user-PC working tree is accessible. The assistant container had no checkout, dotnet or PowerShell; no local .NET PASS is claimed.
The CI checkout is the available executable repository environment. Script/workflow are AR-000 documentation tooling only.
All existing Master/README bytes are retained as a prefix; only AR cross-references are appended. Historical checkpoints and checked tasks are not re-awarded.

## Current call graph (E0 source map, not a new execution trace)

```text
Project AiChatPanel.Agent / Global WorkAssistant
 -> H2 application composition -> same IH2AgentAdapter / H2ProductionAgentAdapter
 -> SnapshotContext + custom AgentContextInput
 -> AgentOrchestrator -> AgentRuntimeFactory -> AgentRuntime
 -> AgentTransportFactory -> selected configured provider transport
 -> deferred NormalRuntimeToolRegistry + H2ProductionToolSession
 -> host permission/resource validation -> concrete domain executor
 -> domain verifier/router + bounded evidence/artifacts
 -> Agent-owned task/thread archive -> H2 UI projections

LabWindow -> AgentOrchestratedRun
 -> LabSessionContextAdapter + RuntimeCompactionCoordinator
 -> same AgentOrchestrator / AgentRuntimeFactory / AgentRuntime primitives
 -> transport -> registry/scheduler -> verifier/repair
 -> LabSession + ArtifactStore (not ProjectRecord)
```

Shared runtime does **not** yet prove identical context, compaction or all-request payload budgeting: B08 remains open.
Coordinator owns shared H2 event order/queue/lease/barriers; it is not an Agent engine or evidence database.
Production Coordinator cutover/authentication and physical acceptance remain open in H2M-133. No queue/storage runtime changes were made.

## Ten SPEC section 2.2 observations rechecked on current code

Every row has exact file/blob/line/needle anchors in the manifest. A matched observation is E0 evidence, not a passing regression for the defective behavior.

| ID | Current observation | Owning AR task(s) |
|---|---|---|
{rows}

## Capability and execution inventory

| Surface | Present source / available execution | Acceptance boundary |
|---|---|---|
| Global and Project | Same production adapter; nullable ProjectId, exact target paths, scoped permission checks | Existing concrete bridge tests use scripted transport; not E4 |
| Core files / evidence / skills | Canonical registry, deferred discovery, read_tool_output and progressive skill reader | E1/E2 fixtures are not native document acceptance; B01/B03 open |
| Live Word / Excel | Concrete isolated OfficeHost COM backend and readback verifier; no fixture backend constructed by Domains | Helper build is not installed Office or live workbook proof; E3/E4 NOT_RUN |
| Closed-file Python/artifacts | Registered core tools for copied input, inspection and publication | No new run or personal document was used; current packaged capability must be preflighted |
| Shell/process | Existing local command capability; no durable-job acceptance asserted | AR-040/041 remain; no command/model executed by this audit |
| Desktop | Existing selected-window/DesktopHost path and verifier | Actual target/permission/capture needed; E3/E4 NOT_RUN |
| Web | Concrete fetch/download/extract/metadata/feed wiring | Real search/browser not configured in composition; B10 / AR-060 |
| CAD | Closed-file tools conditionally registered only with FullAccess and detected executable | No blanket live-CAD readiness; E3/E4 NOT_RUN |
| Plugins/MCP | Existing extension/provider architecture and historical fixture tests | No new provider, endpoint, credential, package install or native lifecycle acceptance |
| Shared storage/queue | Existing Coordinator contracts/store; H2M-133F and production cutover debts remain | AR-083 DEFERRED_BY_USER; local NAS self-test is not E5 |

User model/profile, Office installation, CAD installation and real UI session cannot be inspected here. They were not changed.
No paid model call, new endpoint, outbound user message or destructive personal-data test was performed. One-PC Office/model acceptance is still mandatory.

## Actual CI baseline — not green

Run `{CI_RUN}`, code `{BASE}`: solution build succeeds (31 warnings, 0 errors); H2 tests **590/590**;
DesktopHost safety **4/4**; Agent v1 **18/18**; architecture guard **34 passed, 1 failed**.
Counts were read from decoded job `106617792240`; step conclusions and zero artifacts are re-read from GitHub API during capture.
Failure: `Canonical normal runtime registry deleted or invented callable tools.`
Later Agent/Office/transport/MB suites and publish were skipped. NAS artifact upload fails because no package exists;
a portable upload step reporting success with no files is not publish success. Artifact inventory is **0**.
The local NAS protocol self-test passes only its fixture protocol, not the user's mixed transport topology.
AR-001 owns B01/B02/B03, any necessary CI restoration and nonempty publish smoke; no guard was disabled here.

## First AR-001 action and retained debt

{next_action}

```powershell
{chr(10).join(commands)}
```

Use the existing full Avalonia CI after fixes. Check exact checkout SHA, every required suite and actual nonempty publish/smoke evidence.
Run RC-02, canonical name/registry tests, large-output/foreign-handle cases, and 128/129/200-cell consistent preflight verdicts without truncation.
MB-124–127 stay open. AR-083 stays DEFERRED_BY_USER and blocks certified multi-PC claims, not independent one-PC implementation.

## Source owners

{source_rows}

This report records one baseline; the AR tracker alone is the active execution checkpoint.
"""
    Path(OUT + "/baseline.md").write_text(report, encoding="utf-8")
    Path(TRACKER).write_bytes(revised.encode("utf-8"))
    for path in MASTER:
        prefix = "docs/" if path == "README.md" else ""
        addition = ("\n\n## Agent reliability implementation — AR checkpoint (2026-09-22)\n\n"
            f"The approved [AR specification]({prefix}H2_AGENT_RELIABILITY_IMPLEMENTATION_SPEC.md) and "
            f"[AR tracker / SESSION HANDOFF]({prefix}H2_AGENT_RELIABILITY_IMPLEMENTATION_TASKS.md) govern this reliability pass. "
            f"[AR-000 baseline]({prefix}agent-reliability/AR-000/baseline.md) records current code and CI, not renewed acceptance of historical tasks. "
            "MB-124–127 retain their evidence gates. AR-083 is DEFERRED_BY_USER; one-PC Office/model gates are not waived.\n")
        Path(path).write_bytes((original[path] + addition).encode("utf-8"))
        require(Path(path).read_bytes().startswith(original[path].encode("utf-8")), "Historical master content was altered.")
    require(set(git("diff", "--name-only").splitlines()) == set(MASTER + [TRACKER]), "Unexpected tracked edits.")
    require(not git("diff", "--", "src", "experiments", "tests"), "Runtime changes are forbidden in AR-000.")
    require('| AR-083 |' in revised and '| E5 | DEFERRED_BY_USER |' in revised, "AR-083 deferral was lost.")
    for path in OUTPUTS:
        text = Path(path).read_text(encoding="utf-8")
        require("\x00" not in text and Path(path).stat().st_size < 512_000, "Invalid/oversized evidence file.")
    # Check only links added by AR-000; historical links are left untouched.
    for path in MASTER:
        added = Path(path).read_bytes().decode("utf-8")[len(original[path]):]
        for target in re.findall(r"\]\(([^)]+)\)", added):
            require((Path(path).parent / target).is_file(), "Broken AR cross-reference: " + target)
    Path(os.environ.get("RUNNER_TEMP", "/tmp"), "ar000-outputs.txt").write_text("\n".join(OUTPUTS) + "\n", encoding="utf-8")
    print("E0_CAPTURED: 10 current source observations; CI baseline RED; no runtime changes; docs links valid.")
    print("NEXT: AR-001 on existing branch. E3/E4 NOT_RUN; AR-083 DEFERRED_BY_USER.")


class DocumentationSafetyTests(unittest.TestCase):
    def template(self) -> str:
        return ("Historical prefix\n| AR-000 | Chốt baseline, ownership và traceability | Không | E0 + execution inventory | NOT_STARTED |\n"
            "### [ ] AR-000 — Baseline và hợp nhất quyền điều hành đợt AR\nKeep scope\n"
            "### [ ] AR-001 — Đồng bộ tên/giới hạn công cụ và full CI\nDo not modify AR-001\n"
            "## SESSION HANDOFF\n```yaml\nschema_version: 1\nspec_version: H2-AR-SPEC-1.0\n"
            "phase: NOT_STARTED\nactive_task: AR-000\n```\nHistorical suffix\n")
    def test_only_ar000_is_closed_and_history_survives(self) -> None:
        result, old = rewrite_tracker(self.template(), {"active_task": "AR-001", "implementation_status": "NOT_STARTED"})
        self.assertTrue(result.startswith("Historical prefix"))
        self.assertTrue(result.endswith("Historical suffix\n"))
        self.assertIn("### [ ] AR-001", result)
        self.assertIn("### [x] AR-000", result)
        self.assertIn("active_task: AR-000", old)
    def test_changed_active_task_is_rejected(self) -> None:
        with self.assertRaises(RuntimeError):
            rewrite_tracker(self.template().replace("active_task: AR-000", "active_task: AR-020"), {})
    def test_repeated_capture_is_rejected(self) -> None:
        result, _ = rewrite_tracker(self.template(), {})
        with self.assertRaises(RuntimeError):
            rewrite_tracker(result, {})
    def test_duplicate_checkpoint_is_rejected(self) -> None:
        with self.assertRaises(RuntimeError):
            rewrite_tracker(self.template() + self.template(), {})
    def test_claims_remain_separate(self) -> None:
        result, _ = rewrite_tracker(self.template(), {})
        self.assertIn("E0 baseline accepted with existing CI RED", result)
        self.assertIn("no runtime repair or E3/E4/E5 PASS", result)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        suite = unittest.defaultTestLoader.loadTestsFromTestCase(DocumentationSafetyTests)
        sys.exit(0 if unittest.TextTestRunner(verbosity=2).run(suite).wasSuccessful() else 1)
    try:
        capture()
    except Exception as exc:
        # Do not print HTTP response bodies, headers, environment values or credentials.
        print(f"AR-000 capture stopped: {type(exc).__name__}: {exc}", file=sys.stderr)
        sys.exit(1)

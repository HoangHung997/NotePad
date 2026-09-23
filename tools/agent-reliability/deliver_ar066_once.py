"""One reviewed delivery. Exact HEAD/preimages, no reset/force/retry, canonical tracker retained."""
import datetime, hashlib, json, os, pathlib, subprocess
R=pathlib.Path.cwd(); B='feature/h2-agent-reliability-ar-000'; BASE='05fe85d07083862943b2d60e0e299b1de09fd0f9'
def git(*a): return subprocess.check_output(['git',*a],text=True).strip()
def sha(p): return hashlib.sha256((R/p).read_bytes()).hexdigest()
head=git('rev-parse','HEAD')
assert head==os.environ['GITHUB_SHA'] and git('rev-parse','HEAD^')==BASE, 'Reconcile HEAD before delivery'
assert not git('status','--porcelain'), 'Preserve dirty source; delivery refused'
assert git('ls-remote','origin','refs/heads/'+B).split()[0]==head, 'Branch advanced; do not overwrite'
rows=json.loads((R/'tools/agent-reliability/ar066-source-hashes.json').read_text())
assert len(rows)==8 and len({r['path'] for r in rows})==8
for row in rows: assert sha(row['path'])==row['before'], 'Source preimage mismatch: '+row['path']
patches={'ar066-reviewed.patch':'b8e09a784744915f9238e785bae57c9100556aa5e96c8383a7401c54449a3dec',
         'ar066-staged-contract.patch':'f0b7f7b4684785c6b90305402b08d1c417de9142d78fa1b3ab4181075721d512'}
for name,digest in patches.items():
    path='tools/agent-reliability/'+name
    assert sha(path)==digest,'Patch transfer integrity failed'
    subprocess.run(['git','apply','--check',path],check=True)
tracker='docs/H2_AGENT_RELIABILITY_IMPLEMENTATION_TASKS.md'
assert sha(tracker)=='7eb347372dab75320005d724d651732202c5579b7e3ab63ed58eacefb5a908ad', 'Tracker changed; reconcile first'
text=(R/tracker).read_text(); start=text.index('```yaml',text.index('## 7.'))+len('```yaml\n'); end=text.index('\n```',start)
h=json.loads(text[start:end]); assert h['active_task']=='AR-065' and h['implementation_status']=='IMPLEMENTED'
h['parked_acceptance'].append({'id':'AR-065','implementation_status':'IMPLEMENTED','acceptance_status':'AWAITING_ENVIRONMENT','completed_level':'E2','required_level':'E4','done':False,'code_sha':'26d79367392a1f7f02d6acaef116e420ac9125dc','evidence':'docs/agent-reliability/AR-065/evidence.json','blocks_independent_implementation':False})
h.update(phase='AR-066_CORE_ACTIVE_PENDING_CI',active_task='AR-066',implementation_status='ACTIVE',acceptance_status='NOT_RUN',
    owner_session='chatgpt-ar066-live-resource-2026-09-23',last_code_commit=None,last_runtime_source_commit=None,
    last_validation_result='AR066_NOT_RUN; prior AR065 E2 retained separately, not reused as AR066 acceptance',capture_checked_head=head,
    checkpoint_saved_at_utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),completed_this_session=[],implemented_this_session=['AR-066'],
    remaining_in_active_task=['Build/test the exact integrated AR066 source, repair all regressions, then persist exact-SHA evidence.','Real Office native recovery and H2 UI/model E3/E4 remain AWAITING_ENVIRONMENT; no live acceptance claimed.'],
    last_test_commands=[],working_tree='Isolated CI clean checkout before bounded reviewed source delivery. User-PC working tree NOT_ACCESSIBLE.',uncommitted_files=[],
    next_exact_action='Inspect completed delivery receipt and exact branch SHA; run only AR066 Windows validation and retained/full regressions. Never rerun a delivery with an uncertain push result: inspect branch/files first. Keep AR065 E4 and prior debt open.',
    code_commit_lookup='git log -1 --format=%H -- experiments/H2AgentLab.OfficeHost/OfficeWindowCatalog.cs experiments/H2AgentLab/Integration/H2ProductionToolSession.cs',
    next_ready_independent_task='Do not start another task this turn; AR067 remains NOT_STARTED.',resolved_in_this_session=[],known_failures=[],
    independent_dependency_assessment={'id':'AR-066','rule':'Tracker section 2.2: independent core may proceed with implemented dependencies and passed regressions while real E3/E4 gates await environment.',
      'dependencies':['AR-012 DONE','AR-020 IMPLEMENTED with passed E2 regression; native E3 remains open'],
      'reconciled_head':BASE,'basis':'AR065 has passing E1/E2/full CI and no unresolved code failure. Its E4 is parked, not waived. AR066 core has no AR065 E4 dependency. Latest user continue requests exactly one task.',
      'limits':'This is only AR066. No real native/model acceptance, no new engine/store, no personal side effects.'})
for item in h['critical_user_reported_repairs']:
    if item['id']=='AR-066': item['status']='ACTIVE / E1-E2_NOT_RUN / E3-E4_AWAITING_ENVIRONMENT'
text=text[:start]+json.dumps(h,ensure_ascii=False,indent=2)+text[end:]
old='| AR-066 | LiveResource/native app semantics + no silent live→disk fallback | 012/020 | E3/E4 | NOT_STARTED — CRITICAL |'
assert text.count(old)==1
text=text.replace(old,old.replace('NOT_STARTED — CRITICAL','ACTIVE — core validation pending; E3/E4 AWAITING_ENVIRONMENT'))
old='**AR-064 is checkpointed PARTIAL, not DONE. Current task is AR-065, implemented with E1/E2 and full CI; real H2/model E4 remains AWAITING_ENVIRONMENT.**'
assert text.count(old)==1
text=text.replace(old,'**Current task is AR-066 core, selected under section 2.2 with implemented AR-012/020 dependencies. AR-065 stays IMPLEMENTED/E2_PASS with E4 AWAITING_ENVIRONMENT; AR-064 stays PARTIAL. No acceptance is waived.**')
for name in patches: subprocess.run(['git','apply','tools/agent-reliability/'+name],check=True)
for row in rows: assert sha(row['path'])==row['after'],'Postimage mismatch; do not push'
(R/tracker).write_text(text,encoding='utf-8')
allowed={r['path'] for r in rows}|{tracker}
assert set(git('diff','--name-only').splitlines())==allowed, 'Unexpected mutation; do not commit'
subprocess.run(['git','diff','--check'],check=True)
assert git('ls-remote','origin','refs/heads/'+B).split()[0]==head, 'Concurrent branch change; do not push'
git('config','user.name','H2 AR-066 implementation');git('config','user.email','41898282+github-actions[bot]@users.noreply.github.com')
subprocess.run(['git','add','--',*sorted(allowed)],check=True)
subprocess.run(['git','commit','-m','fix(AR-066): separate live source semantics and retain bounded native identity across transient loss'],check=True)
code=git('rev-parse','HEAD')
subprocess.run(['git','push','origin','HEAD:refs/heads/'+B],check=True)
assert git('ls-remote','origin','refs/heads/'+B).split()[0]==code,'Push result requires reconciliation'
out=R/'artifacts/ar066-delivery';out.mkdir(parents=True,exist_ok=True)
(out/'saved.json').write_text(json.dumps({'task':'AR-066','staging_sha':head,'code_sha':code,'branch':B,'paths':sorted(allowed),'hashes':rows,'E1':'NOT_RUN','E2':'NOT_RUN','E3':'AWAITING_ENVIRONMENT','E4':'AWAITING_ENVIRONMENT','E5':'DEFERRED_BY_USER'},indent=2),encoding='utf-8')
(out/'SESSION-HANDOFF.json').write_text(json.dumps(h,ensure_ascii=False,indent=2),encoding='utf-8')
print('Saved bounded AR066 delivery:',code,'; acceptance not upgraded.')

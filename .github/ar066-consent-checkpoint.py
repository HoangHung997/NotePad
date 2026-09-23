"""One-use AR-066 documentation checkpoint; never executes application or provider work."""
from pathlib import Path
import collections, datetime, hashlib, io, json, os, re, subprocess, time, urllib.request, urllib.error, urllib.parse, zipfile

REPO = 'HoangHung997/NotePad'
BRANCH = 'feature/h2-agent-reliability-ar-000'
CODE = '17af5676bcfb397441a4a8aae264c6088a5701e1'
RUNTIME = '4c6a96ee39116c431dcd7d69a620c6c4f5bb94e2'
MAIN = '1283bc13e07c3cd47d04886166de3dfc595422c0'
FOCUS = None  # Resolve the existing exact-SHA/path run, never dispatch or rerun.
FULL = 35908755287
TRACKER = Path('docs/H2_AGENT_RELIABILITY_IMPLEMENTATION_TASKS.md')
DOCROOT = Path('docs/agent-reliability/AR-066')
STAGED = ['.github/ar066-consent-checkpoint.py', '.github/ar066-consent-review.md', '.github/workflows/h2-ar066-consent-checkpoint.yml']
OUT = Path(os.environ['RUNNER_TEMP'])/'ar066-consent-checkpoint'
OUT.mkdir(exist_ok=True)

def git(*args):
    return subprocess.check_output(['git', *args], text=True).strip()

def dump(path, obj):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(obj, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')

def sha(data): return hashlib.sha256(data).hexdigest()

def blob(data): return hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl): return None

opener = urllib.request.build_opener(NoRedirect())
def download(path):
    # Authentication goes to this repository's GitHub API only. Follow its signed artifact
    # redirect WITHOUT forwarding authorization; never print signed URLs or headers.
    url = 'https://api.github.com/repos/'+REPO+'/'+path
    req = urllib.request.Request(url, headers={'Authorization':'Bearer '+os.environ['GH_TOKEN'],
        'Accept':'application/vnd.github+json', 'User-Agent':'H2-AR066-checkpoint'})
    try:
        with opener.open(req, timeout=60) as r: return r.read()
    except urllib.error.HTTPError as e:
        if e.code not in (301,302,303,307,308): raise RuntimeError('GitHub evidence fetch failed: HTTP '+str(e.code)) from None
        location=e.headers['Location']; target=urllib.parse.urlsplit(location)
        assert target.scheme=='https' and not target.username and not target.password
        assert target.hostname and (target.hostname.endswith('.blob.core.windows.net') or target.hostname.endswith('.githubusercontent.com') or target.hostname.endswith('.actions.githubusercontent.com')), 'Unrecognized evidence redirect'
        with urllib.request.urlopen(location, timeout=60) as r: return r.read()

def api(path): return json.loads(download(path))

def zjson(z, path): return json.loads(z.read(path).decode('utf-8-sig'))

def pass_inventory(text):
    matches=re.findall(r'RESULT: (\d+) passed, (\d+) failed',text)
    names=[n.strip() for n in re.findall(r'^PASS (.+)$',text,re.M)]
    assert len(matches)==1 and int(matches[0][1])==0 and int(matches[0][0])==len(names)>0
    assert not re.findall(r'^FAIL ',text,re.M) and len(set(names))==len(names)
    return sorted(names)

before=git('rev-parse','HEAD')
assert before==os.environ['GITHUB_SHA'] and git('rev-parse','HEAD^')==CODE
assert not git('status','--porcelain')
assert set(git('diff','--no-renames','--name-only',CODE,before).splitlines())==set(STAGED)
assert git('rev-parse','HEAD:'+str(TRACKER))=='0687989839bb148e2d181d8006e159f2f518569b'

# Read the already-started runs; do not rerun/cancel an unknown job or start model/native work.
runs_initial=api('actions/runs?head_sha='+CODE+'&per_page=100')['workflow_runs']
focused=[r for r in runs_initial if r['head_sha']==CODE and r['event']=='push' and r['path']=='.github/workflows/h2-ar066-validation.yml']
assert len(focused)==1 and focused[0]['run_attempt']==1
FOCUS=focused[0]['id']
dump(OUT/'observed-runs.json',{'code_sha':CODE,'focused':FOCUS,'full':FULL,'runs':[
    {'id':r['id'],'path':r['path'],'event':r['event'],'status':r['status'],'conclusion':r['conclusion']} for r in runs_initial]})
deadline=time.monotonic()+1200
while True:
    focus=api(f'actions/runs/{FOCUS}'); full=api(f'actions/runs/{FULL}')
    for r in (focus,full):
        assert r['head_sha']==CODE and r['run_attempt']==1 and r['head_branch']==BRANCH
        if r['status']=='completed': assert r['conclusion']=='success', 'Required exact-source run failed; no checkpoint writes'
    if focus['status']==full['status']=='completed': break
    assert time.monotonic()<deadline, 'Existing validation not complete; no checkpoint writes'
    time.sleep(15)

focus_jobs=api(f'actions/runs/{FOCUS}/jobs')['jobs']; full_jobs=api(f'actions/runs/{FULL}/jobs')['jobs']
assert len(focus_jobs)==len(full_jobs)==1
for j in focus_jobs+full_jobs:
    assert j['status']=='completed' and j['conclusion']=='success'
    assert all(s['status']=='completed' and s['conclusion']=='success' for s in j['steps'] if not s['name'].startswith('Post '))
fj=focus_jobs[0]['id']; wj=full_jobs[0]['id']
artifacts=api(f'actions/runs/{FOCUS}/artifacts')['artifacts']
selected=[a for a in artifacts if a['name']=='AR066-LiveResource-Evidence' and not a['expired']]
assert len(selected)==1
artifact=selected[0]; raw=download(f"actions/artifacts/{artifact['id']}/zip")
assert len(raw)==artifact['size_in_bytes'] and 'sha256:'+sha(raw)==artifact['digest']
z=zipfile.ZipFile(io.BytesIO(raw)); assert z.testzip() is None
identity=zjson(z,'identity.json'); validation=zjson(z,'validation.json')
assert identity['code_sha']==validation['code_sha']==CODE and identity['working_tree']=='CLEAN' and validation['clean_end'] is True
assert str(identity['run'])==str(FOCUS) and str(identity['attempt'])=='1'
assert not validation['failed'] and validation['agent_exit']==0
inventories={}
for item in validation['results']:
    names=pass_inventory(z.read(item['name']+'.log').decode('utf-8-sig'))
    assert item['exit']==0 and item['pass_lines']==len(names)
    inventories[item['name']]=names
assert inventories['AR-066-1']==inventories['AR-066-2']==inventories['AR-066-3']
assert inventories['AR-066-1']==[n for n in inventories['FULL'] if n.startswith('AR-066 ')]
assert len(inventories['AR-066-1'])==134 and len(inventories['FULL'])==1266
consent_cases=[n for n in inventories['AR-066-1'] if n.startswith('AR-066 source consent ')]
assert len(consent_cases)==58
suite=zjson(z,'agent-suites/suite-results.json')['suites']
assert len(suite)==74 and len({s['flag'] for s in suite})==74 and all(s['exit_code']==0 for s in suite)
suite_receipts=[]
for s in suite:
    log='agent-suites/'+s['flag'].removeprefix('--')+'.log'; content=z.read(log); assert content
    suite_receipts.append({'flag':s['flag'],'exit':0,'log_sha256':sha(content)})
resilience=pass_inventory(z.read('agent-suites/v2-provider-resilience-test.log').decode('utf-8-sig'))
assert len(resilience)==17 and sum('pre-cancelled' in name for name in resilience)==4

source=zipfile.ZipFile(io.BytesIO(z.read('committed-source.zip'))); sr=zjson(z,'source-receipt.json'); assert source.testzip() is None
assert len(sr)==len(source.namelist()) and len({r['path'] for r in sr})==len(sr)
source_bytes=0
for r in sr:
    content=source.read(r['path']);source_bytes+=len(content)
    assert sha(content)==r['sha256'] and blob(content)==r['blob']==git('rev-parse',CODE+':'+r['path'])
receipts=[zjson(z,n) for n in z.namelist() if re.fullmatch(r'receipts-(AR-066-[123]|FULL)/source-consent-[^/]+\.json',n)]
assert len(receipts)==232
counts=collections.Counter((r['mode'],r['project']) for r in receipts)
assert len(counts)==58 and all(n==4 for n in counts.values())
positive={'approve-read','duplicate','reference-with-live','two-references','output-create','return-live'}
noapproval={'wrong-source','ungrounded','secret','unknown-binding','output-existing','unknown-effect'}
refused={'deny','file-changed','revision-pending','capture-stale','expired-pending'}
for r in receipts:
    assert r['level']=='E2' and 'scripted' in r['injected'] and r['reopenedWithoutReplay'] is True and r['providerAllocations']==1
    assert r['controlBefore']==r['controlAfter'] and r['referenceBefore']==r['referenceAfter']
    assert r['status']==r['reopenedStatus']==('Cancelled' if r['mode']=='cancel' else 'Completed' if r['mode'] in positive else 'Blocked')
    expected=0 if r['mode'] in noapproval else 2 if r['mode'] in {'two-references','return-live','return-live-no-read'} else 1
    assert len(r['approvals'])==expected and len(r['decisions'])==(0 if r['mode'] in noapproval|{'cancel'} else expected)
    for d in r['decisions']:
        assert d['TaskId']==r['task'] and d['ApprovalId'] in r['approvals'] and d['Approved']==(r['mode'] not in refused)
        assert d['OriginalSource'] and d['SourceId'].startswith('source-') and d['GoalRevisionId'] and d['AuthorityStamp']
    if r['mode'] not in {'file-changed','post-read-change'}: assert r['sourceBefore']==r['sourceAfter']
    assert r['Patches']==(1 if r['mode']=='unknown-effect' else 0) and r['effectHash']==r['reopenedEffectHash']
    if r['mode']=='unknown-effect':
        recovery=r['recovery']; original=r['uncertainBeforeReopen']
        assert recovery['ReconcileRequired'] and not recovery['Interrupted'] and r['effectHash']
        assert r['reopenedError']=='Interrupted / ReconcileRequired: tác động cần đối soát; không tự lặp lệnh ghi.'
        operations=[o for o in recovery['Operations'] if o['ToolCallId']=='uncertain']; assert len(operations)==1
        o=operations[0]
        assert o['InvocationId']==original['InvocationId'] and o['LogicalOperationId']==original['LogicalOperationId']
        assert o['ToolName']=='word.replace_range' and o['State']=='Result' and o['Effect']=='Unknown' and o['ErrorCode']=='connection_lost'
        assert len(o['ArgumentsSha256'])==len(o['OutputSha256'])==64
    else: assert r['originalError']==r['reopenedError']
    assert r['E3']==r['E4']=='AWAITING_ENVIRONMENT' and r['E5']=='DEFERRED_BY_USER'
all_receipts=[n for n in z.namelist() if re.fullmatch(r'receipts-(AR-066-[123]|FULL)/[^/]+\.json',n)]
assert len(all_receipts)==432

full_log=download(f'actions/jobs/{wj}/logs'); (OUT/'full-job.log').write_bytes(full_log)
text=re.sub(r'(?m)^\d{4}-\d\d-\d\dT\S+Z ', '', full_log.decode('utf-8-sig'))
marker='##[group]Run dotnet run --project .\\tests\\H2Notes.Tests\\H2Notes.Tests.csproj -c Release --no-build'
start=text.index(marker); terminal=re.search(r'RESULT: \d+ passed, \d+ failed',text[start:]); assert terminal
h2=text[start:start+terminal.end()]
assert pass_inventory(h2)==inventories['FULL']
checkouts=re.findall(r'(?m)^([0-9a-f]{40})\r?$',text);assert len(checkouts)==1
checkout=checkouts[0]
assert 'Merge '+CODE+' into '+MAIN in text
subprocess.run(['git','fetch','--no-tags','origin',checkout],check=True)
assert git('rev-parse',checkout+'^{tree}')==git('rev-parse',CODE+'^{tree}'), 'Full merge tree differs from exact focused source'
warnings=re.findall(r'(\d+) Warning\(s\)',text[:start]);errors=re.findall(r'(\d+) Error\(s\)',text[:start]);assert warnings and errors and int(errors[-1])==0
full_artifacts=api(f'actions/runs/{FULL}/artifacts')['artifacts']
for name in ['H2Notes-Avalonia-Portable-win-x64','H2Notes-NasAcceptance-win-x64']:
    a=[x for x in full_artifacts if x['name']==name and not x['expired']];assert len(a)==1 and a[0]['size_in_bytes']>0 and a[0]['digest']

# Capture every current-SHA workflow status, including push-only retained validation. A red or
# unfinished peer run is never rewritten as acceptance merely because the focused job passed.
while True:
    runs=api('actions/runs?head_sha='+CODE+'&per_page=100')['workflow_runs']
    assert runs and all(r['head_sha']==CODE for r in runs)
    assert not [r for r in runs if r['status']=='completed' and r['conclusion']!='success'], 'Another exact-SHA CI run failed; reconcile before saving acceptance'
    if all(r['status']=='completed' for r in runs): break
    assert time.monotonic()<deadline, 'Peer CI incomplete; no checkpoint writes'
    time.sleep(15)

# Inspect the previously failing peer workflows, not only their green result flags.
peer_evidence=[]
for path,name in [('.github/workflows/h2-ar033-consistency.yml','AR033-Consistency-Evidence'),
                  ('.github/workflows/h2-ar040-validation.yml','AR040-Process-Job-Evidence')]:
    selected_runs=[r for r in runs if r['path']==path and r['event']=='push']
    assert len(selected_runs)==1 and selected_runs[0]['run_attempt']==1
    run=selected_runs[0]; items=api(f"actions/runs/{run['id']}/artifacts")['artifacts']
    # The AR040 artifact's established name may carry a prefix; require exactly one evidence
    # archive and record its real name, rather than treating a source-only artifact as a pass.
    chosen=[a for a in items if a['name']==name] if path.endswith('consistency.yml') else [a for a in items if a['name'].startswith('AR040-') and 'Evidence' in a['name']]
    assert len(chosen)==1 and not chosen[0]['expired']
    a=chosen[0]; data=download(f"actions/artifacts/{a['id']}/zip")
    assert len(data)==a['size_in_bytes'] and 'sha256:'+sha(data)==a['digest']
    peer=zipfile.ZipFile(io.BytesIO(data));assert peer.testzip() is None
    item={'run':run['id'],'path':path,'attempt':1,'artifact_id':a['id'],'name':a['name'],'bytes':len(data),'sha256':sha(data)}
    if path.endswith('consistency.yml'):
        control=zjson(peer,'retained-control.json')
        assert control['current_sha']==CODE and control['current_status']=='CURRENT42_PASS_X3'
        assert control['source_substitution_performed'] is False and control['new_negative_control_executed'] is False
        assert control['historical_run']==35799370449 and control['evidence_refusal_checks']==8
        for n in range(1,4):
            log=peer.read(f'AR-033-{n}.log');names=pass_inventory(log.decode('utf-8-sig'))
            assert len(names)==42 and set(control['historical_expected_failures']).issubset(names)
        item['retained_historical_control']=control
        assert pass_inventory(peer.read('all-agent-suites/v2-provider-resilience-test.log').decode('utf-8-sig'))==resilience
    else:
        assert pass_inventory(peer.read('agent-suites/v2-provider-resilience-test.log').decode('utf-8-sig'))==resilience
        assert not zjson(peer,'corpora-result.json')['failed']
    peer_evidence.append(item)
dump(OUT/'repaired-peer-evidence.json',peer_evidence)

failure={'sha':'6ac81cb8cd18b6d8124bdd97fc3b9bc541e7b78a','focused_run':35903219588,'full_run':35903224947,
 'focused_each':{'passed':132,'failed':2,'repetitions':3},'full':{'passed':1264,'failed':2},
 'artifact_id':10770393061,'artifact_sha256':'982ae4ce14870e21691050f9ef89e9f30b4d7938cff231fac9185161373c6241',
 'cause':'Two new fixtures demanded unchanged error text after unknown effect; existing archive projects ReconcileRequired. Test-only repair retains and strengthens exact receipt/no-replay checks. Historical run remains FAILURE.'}
peer_history={'sha':'2f2dda5f6207f948ed56e30ca5cd2c50cb811948',
 'focused_run':35904844689,'focused_result':'134/134 x3; full1266; Agent74 PASS',
 'full_run':35904851797,'full_result':'SUCCESS',
 'focused_artifact':{'id':10771238823,'bytes':3744736,'sha256':'850edd77e59935d3474484a1ec9b043ac9d54fb8df116b0b047ff71c0a30deaa'},
 'failed_peer_runs':[
 {'id':35904844588,'artifact_id':10770523381,'artifact_sha256':'2dd9f9ce3e7a4add426adf76cd9bfad0ed89a1c0767e27f49ee908ce882f5d00',
  'reason':'Historical AR033 control pinned an obsolete current-assessment hash and stopped before source substitution. All current AR03342 x3 and Agent74 passed. Original executed control artifact10725382259 was independently CRC/hash checked and preserved read-only; identical six contradiction regression sources still run in every current42 corpus. No new negative-control execution claimed.'},
 {'id':35904844594,'artifact_id':10770753527,'artifact_sha256':'895c42e286a39ec0faa460252abe23735e2e3e4c44ab8c444a066855fd553318',
  'reason':'Resilience cancellation assumed a dispatch before a prearmed40ms timer; actual count was not logged. Replaced timing assumption with a bounded observed-dispatch handshake and four separate precancel-zero-send tests; exact one-send/no-fallback assertions retained. Runtime transport unchanged.'}],
 'failed_checkpoint':{'id':35906863619,'sha':'9e1dddd1cf9f957b6a29e90a974bbd112a0e984c','result':'FAILURE_BEFORE_DOCUMENT_WRITE_OR_COMMIT_OR_PUSH','reason':'Refused to checkpoint while exact-SHA peer workflows were red.'},
 'repair_commit':CODE,'classification':'AR066 regression maintenance only; all historical failures remain failures'}
evidence={'task':'AR-066' ,'scope':'Exact-bound live input plus separately approved disk reference/output/replacement/reselection; not a general multi-live resolver',
 'implementation':'SOURCE_CONSENT_CORE_IMPLEMENTED','acceptance':'E1/E2_PASS; NATIVE_E3_AND_REAL_H2_E4_AWAITING_ENVIRONMENT; NOT_DONE',
 'code_sha':CODE,'runtime_sha':RUNTIME,'branch':BRANCH,'pr':3,'focused_run':FOCUS,'focused_job':fj,'full_run':FULL,'full_job':wj,'attempt':1,
 'full_checkout_sha':checkout,'full_tree_equals_focused':True,'full_log_sha256':sha(full_log),
 'artifact':{'id':artifact['id'],'bytes':len(raw),'sha256':sha(raw),'crc_verified':True},
 'source':{'files':len(sr),'bytes':source_bytes,'all_git_blobs_verified':True},
 'focused_each':{'passed':len(consent_cases)+76,'failed':0,'repetitions':3},'new_source_consent_cases':len(consent_cases),
 'full_h2':{'passed':len(inventories['FULL']),'failed':0},'retained':{n:len(inventories[n]) for n in ['AR-012','AR-020','AR-065']},
 'independent_agent_suites':len(suite),'build_warnings':int(warnings[-1]),'build_errors':0,
 'consent_receipts':len(receipts),'all_AR066_receipts':len(all_receipts),'injected':'scripted model/native client/capture; actual host approval API/file IO/archive',
 'old_source_control':validation['old_source_control'],'historical_failure':failure,'peer_failure_history':peer_history,
 'repaired_peer_evidence':peer_evidence,'resilience_cases':resilience,
 'full_artifacts':[{'id':a['id'],'name':a['name'],'bytes':a['size_in_bytes'],'digest':a['digest'],'binary_download_verified':False} for a in full_artifacts],
 'all_peer_CI_success':True,
 'ci_inventory':[{'id':r['id'],'name':r['name'],'event':r['event'],'status':r['status'],'conclusion':r['conclusion'],'attempt':r['run_attempt']} for r in runs],
 'case_inventory':inventories['AR-066-1'],'agent_suite_logs':suite_receipts,
 'limits':['No real native Office/H2/model acceptance; no new provider, credentials or personal-document test.',
 'Source consent changes meaning only, not file grounding, mutation permission, goal verification or unknown-effect fences.',
 'Unknown/ambiguous original live resources remain blocked; source approvals are exact-file, task-local, revision-bound and never restored as grants.',
 'AR064 PARTIAL, AR020/033 E3, AR051/065 E4, MB124-127 and AR083 DEFERRED_BY_USER remain unchanged.']}
dump(OUT/'evidence.json',evidence);dump(OUT/'uncertain-restart-receipts.json',[r for r in receipts if r['mode']=='unknown-effect'])
dump(OUT/'full-step-inventory.json',full_jobs[0]);dump(OUT/'source-receipt.json',sr)

# Only now prepare five documentation changes. Preserve every prior task/debt/history source.
s=TRACKER.read_text(encoding='utf-8'); old=s
row='PARTIAL — E1/E2 PASS; mixed-source consent follow-up; E3/E4 AWAITING_ENVIRONMENT'
assert s.count(row)==1;s=s.replace(row,'IMPLEMENTED (exact-bound source roles) / E1-E2_PASS / E3-E4_AWAITING_ENVIRONMENT — NOT_DONE')
intro='**Current task remains AR-066 PARTIAL: core source/native identity, completion and trusted-revision foundation passed E1/E2/full CI; mixed-source consent follow-up and real native E3/H2 E4 remain open. AR-065 E4, AR-064 PARTIAL and all prior debts are retained.**'
assert s.count(intro)==1
s=s.replace(intro,'**Current task remains AR-066: exact-bound mixed live/disk source roles and same-task source approval are now implemented with E1/E2/full CI. Real native E3/H2 E4 remain AWAITING_ENVIRONMENT; AR-066 is NOT DONE. AR-065 E4, AR-064 PARTIAL and all prior debts remain unchanged.**')
start=s.index('```yaml\n',s.index('## 7. SESSION HANDOFF'))+8;end=s.index('\n```',start);handoff=json.loads(s[start:end]);prior=json.loads(s[start:end])
now=datetime.datetime.now(datetime.timezone.utc).isoformat()
next_action='Reconcile refs/checkpoint and continue ONLY AR-066 native E3 and real H2 E4 using real-native-acceptance.md: Word unsaved, Excel selection/multiple views, same-object reconnect, no silent disk fallback, and real source approval allow/deny/reference/output/replacement/reselection on both surfaces. Use an authorized isolated Windows/H2/Office environment and existing authorized model with a finite operator budget; never paste keys into chat. Inspect journal/postconditions before repeating uncertain work. Repair any observed regression in AR-066 and rerun exact-SHA CI. Do not start AR-067 in this continuation.'
handoff.update(phase='AR-066_SOURCE_CONSENT_E2_PASS_AWAITING_NATIVE_E3_REAL_H2_E4',active_task='AR-066',implementation_status='IMPLEMENTED',acceptance_status='AWAITING_ENVIRONMENT',
 owner_session='chatgpt-ar066-source-consent-2026-09-24',last_code_commit=CODE,last_validated_code_commit=CODE,last_runtime_source_commit=RUNTIME,last_test_commit=CODE,
 last_validation_result='AR066134_PASS_X3; NEW_CONSENT58; FULL1266_PASS; AGENT74_PASS; CONSENT232_AND_TOTAL432_E2_RECEIPTS; ALL_EXACT_SHA_CI_PASS; NATIVE_E3_E4_AWAITING; NOT_DONE',
 capture_checked_head=before,finalization_checked_head=before,checkpoint_saved_at_utc=now,previous_saved_checkpoint_commit='3b3a2f31c7e353faef6187c194f0eeb4d6eee296',
 working_tree='Exact focused CI clean start/end and every source Git blob verified; full test-merge tree identical. GitHub code normal-pushed. User-PC working tree NOT_ACCESSIBLE. This checkpoint changes documentation only.',
 uncommitted_files=[],completed_this_session=[],implemented_this_session=['AR-066 exact-bound source-consent core only'],
 remaining_in_active_task=['Execute authorized real native E3 and real H2/model/UI E4 corpus including source-approval rendering and live/disk differing content. No real environment available in this session.',
 'Retain conservative exact-resource limitations; unknown/multiple ambiguous live sources are not guessed. Any observed native/production regression stays in AR-066.'],
 known_failures=[],last_full_ci_checkout_sha=checkout,core_followup_status='EXACT_BOUND_MIXED_SOURCE_CONSENT_IMPLEMENTED_E2_PASS',native_acceptance_status='AWAITING_ENVIRONMENT',
 ar066_source_consent_evidence=str(DOCROOT/'source-consent-evidence.json'),next_exact_action=next_action,
 next_task_if_active_done='AR-067 after AR-066 acceptance or a separately recorded section 2.2 independent-core dependency decision; no AR-067 implementation this turn.',
 next_ready_independent_task='AR-067 NOT_STARTED; assess separately under tracker 2.2 after this checkpoint. Native AR-066 debt is not waived.',
 pending_user_decisions=['Provide/connect an authorized isolated Windows/H2/Office test environment and finite permitted model-test budget. Credentials remain on that machine.'],
 ar066_core_limits=evidence['limits'],resolved_in_this_session=['Exact-file reference/output/replacement/live-reselection via existing approval and journal, with no execution permission expansion.',
 'Current source decisions invalidated by queued/accepted user revision, expiry, cancellation, stale capture or changed pending file.',
 'Fresh actual content/hash observation required after source selection; metadata, earlier pages and old native proof do not complete the source gate.',
 'Unknown-effect restart fixture follows existing ReconcileRequired authority and proves exact effect/receipt/no replay; archive runtime unchanged.',
 'AR066 retained-CI maintenance: bounded dispatch cancellation with four precancel regressions; read-only original AR033 control provenance plus unchanged current42 x3, no historical source replacement.'])
handoff['last_test_commands']=[{'command':'tools/agent-reliability/validate_ar066.ps1','sha':CODE,'run_id':FOCUS,'job_id':fj,'attempt':1,'result':'134/134 x3; retained44/36/55; full1266/1266; Agent74/74;432E2 receipts including232 consent'},
 {'command':'Avalonia CI: full build/test/independent gates/win-x64 publish/packaged helper IPC','sha':checkout,'head_sha':CODE,'run_id':FULL,'job_id':wj,'attempt':1,'result':'All mandatory stages SUCCESS; full1266/1266; test-merge tree equals focused; not main merge'}]
handoff['ci_runs'] += [{'id':FOCUS,'code_sha':CODE,'result':'SUCCESS_VERIFIED','owner':'AR-066 source consent'}, {'id':FULL,'code_sha':CODE,'tested_checkout_sha':checkout,'result':'SUCCESS_VERIFIED','owner':'AR-066 full regression'}]
handoff['historical_failure_notes_retained'].extend([failure,peer_history])
handoff['evidence_locations'].append(str(DOCROOT/'source-consent-evidence.json'))
for item in handoff['critical_user_reported_repairs']:
    if item['id']=='AR-066': item['status']='IMPLEMENTED_EXACT_BOUND_SOURCE_ROLES / E1-E2_PASS / E3-E4_AWAITING_ENVIRONMENT / NOT_DONE'
assert all(x==y for x,y in zip(prior['critical_user_reported_repairs'],handoff['critical_user_reported_repairs']) if x['id']!='AR-066')
assert handoff['parked_ar064']==prior['parked_ar064'] and handoff['parked_acceptance']==prior['parked_acceptance'] and handoff['deferred_acceptance']==prior['deferred_acceptance']
s=s[:start]+json.dumps(handoff,ensure_ascii=False,indent=2)+s[end:]
review=DOCROOT/'implementation-review.md'; review_text=review.read_text(encoding='utf-8')
note='\n> Historical foundation at `95d9aa2d0566a8e93d2b2be17a052fbb1182014f` is retained below. Its statements that same-task source consent was unimplemented describe that older SHA, not the current implementation. The source-consent follow-up is now recorded in [source-consent-review.md](source-consent-review.md) and [source-consent-evidence.json](source-consent-evidence.json). Native E3/real H2 E4 still remain unexecuted.\n'
review_text=review_text.replace('\n',note+'\n',1)
native=DOCROOT/'real-native-acceptance.md';native_text=native.read_text(encoding='utf-8')
old_limit='The core implementation deliberately does not add a second provider/store, unrestricted command fallback, or same-task semantic-consent workflow. Natural-language source admission is conservative, not a complete per-resource role resolver. Native save-copy is an output, not permission to substitute the input. Trusted user revisions can add restrictions through the existing source journal; they cannot silently waive a live requirement. Per-resource mixed-source consent and same-task live-to-disk approval are still not implemented. Keep these limitations and any in-turn target change explicit instead of silently broadening scope.'
assert native_text.count(old_limit)==1
native_text=native_text.replace(old_limit,'The exact-bound source-consent implementation now reuses resource_sources, request_source_change, the existing pending approval card and Agent journal. Read source-consent-review.md and source-consent-evidence.json for scope and executed E2. A known original live input may have separately approved disk reference/output roles, or explicitly switch to DiskSnapshot and back to the same live input in one task. Consent grants no file scope or mutation permission and cannot clear unknown effects. New user input/expiry/restart invalidate active decisions; history remains. Unknown/ambiguous live identities and unsupported CAD/browser providers stay blocked. Native save-copy is an output, not an input substitute.')
insert='''\n## Additional same-task source-consent cases — real UI/model NOT EXECUTED\n\nIn the same isolated task, use real tool discovery and host approval, not a direct test API call. Record exact pending approval ID, task/revision, source ID, original live identity, selected path, role, response and final observed source. Use fixtures whose live and saved content differ.\n\n| Case | Required observation |\n| --- | --- |\n| Reference beside live input | Approve one exact DOCX/reference read; read its saved marker and the original unsaved live marker. The reference alone must not satisfy the live goal. Repeat with two explicitly distinct reference files. |\n| Output beside live input | Approve a new exact output, separately obey normal mutation permission, create once and read back. Existing files and later overwrite attempts remain protected. The live input is unchanged. |\n| Replace live with saved snapshot | The card must clearly warn about missing unsaved changes and name the original/selected inputs. After Allow, read the exact saved marker without silently saving/changing live state. No read or metadata-only output must not complete. Deny/Cancel must not grant the route. |\n| Return to live | A second explicit source decision selects the same original live identity. A fresh live observation is required; prior native/disk proof is insufficient. |\n| Invalidated decision | New user input, expiry, changed pending file/capture or wrong approval/task/source ID must not activate stale authority. Reapproval does not expand file scope. |\n| Restart and uncertain effects | Reopen without provider/file/native replay. Source decisions remain historical only. Unknown effects must retain exact operation receipts and ReconcileRequired; source approval cannot erase or replay them. |\n\nThese are new E3/E4 execution requirements, not passes inferred from the scripted E2 corpus. Do not deliberately cause an unsafe native failure; record unrun cases when a safe isolated condition is unavailable.\n'''
native_text += insert

assert git('ls-remote','origin','refs/heads/'+BRANCH).split()[0]==before, 'Branch moved before documentation write'
TRACKER.write_text(s,encoding='utf-8');review.write_text(review_text,encoding='utf-8');native.write_text(native_text,encoding='utf-8')
(DOCROOT/'source-consent-review.md').write_bytes(Path('.github/ar066-consent-review.md').read_bytes())
dump(DOCROOT/'source-consent-evidence.json',evidence)
paths=[str(TRACKER),str(review),str(native),str(DOCROOT/'source-consent-review.md'),str(DOCROOT/'source-consent-evidence.json')]
for name in paths:
    target=OUT/name;target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(Path(name).read_bytes())
dump(OUT/'SESSION-HANDOFF.json',handoff)
git('add','--',*paths);git('rm','--',*STAGED);git('diff','--cached','--check')
assert set(git('diff','--cached','--no-renames','--name-only').splitlines())==set(paths+STAGED)
assert git('ls-remote','origin','refs/heads/'+BRANCH).split()[0]==before, 'Branch moved before checkpoint commit'
git('config','user.name','H2 AR-066 checkpoint');git('config','user.email','41898282+github-actions[bot]@users.noreply.github.com')
git('commit','-m','docs(AR-066): save verified exact-source consent E2 checkpoint; native E3/E4 still required')
after=git('rev-parse','HEAD')
dump(OUT/'saved.json',{'before':before,'checkpoint_commit':after,'code_sha':CODE,'branch':BRANCH,'pr':3,
 'documents':[{'path':p,'sha256':sha(Path(p).read_bytes()),'git_blob':blob(Path(p).read_bytes())} for p in paths],
 'source_unchanged_after_validation':True,'self_removed':STAGED,'native_E3_E4':'AWAITING_ENVIRONMENT','E5':'DEFERRED_BY_USER'})
subprocess.run(['git','push','origin','HEAD:refs/heads/'+BRANCH],check=True)
assert git('ls-remote','origin','refs/heads/'+BRANCH).split()[0]==after and not git('status','--porcelain')
assert set(git('diff','--no-renames','--name-only',CODE,after).splitlines())==set(paths)
print('AR-066 documentation checkpoint saved:',after,'; source consent E2 verified; native/H2 E3/E4 still AWAITING_ENVIRONMENT')

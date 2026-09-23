"""Verify exact completed CI and persist four docs. No product/source mutation or acceptance waiver."""
import datetime, hashlib, io, json, os, pathlib, re, subprocess, urllib.request, urllib.parse, zipfile
ROOT=pathlib.Path.cwd(); REPO='HoangHung997/NotePad'; BRANCH='feature/h2-agent-reliability-ar-000'
CODE='95d9aa2d0566a8e93d2b2be17a052fbb1182014f'; RUNTIME='f55f466537deda7fe49cbe8f12b1663d555291ad'
FOCUS=35895544765; FULL=35895550449
TRACKER='docs/H2_AGENT_RELIABILITY_IMPLEMENTATION_TASKS.md'
BASE_TRACKER='2027e3017f68f3e60d54e39c3e6ed6a4bee0e3362b3dc0c268382b1f12ad295b'
OUT=ROOT/'artifacts/ar066-checkpoint';OUT.mkdir(parents=True,exist_ok=True)
API='https://api.github.com/repos/'+REPO
TOKEN=os.environ['GH_TOKEN']
class SafeRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        destination=urllib.parse.urlsplit(newurl)
        assert destination.scheme=='https' and not destination.username and not destination.password,'Unsafe artifact redirect'
        redirected=super().redirect_request(req,fp,code,msg,headers,newurl)
        if redirected is not None and destination.netloc!=urllib.parse.urlsplit(req.full_url).netloc:
            redirected.remove_header('Authorization')
        return redirected
OPENER=urllib.request.build_opener(SafeRedirect())
def get(url,binary=False):
    assert urllib.parse.urlsplit(url).netloc=='api.github.com' and urllib.parse.urlsplit(url).scheme=='https'
    request=urllib.request.Request(url,headers={'Authorization':'Bearer '+TOKEN,'Accept':'application/vnd.github+json','User-Agent':'H2-AR066-exact-evidence'})
    with OPENER.open(request,timeout=90) as r: data=r.read(20_000_001)
    assert len(data)<=20_000_000,'Unexpected oversized GitHub response'
    return data if binary else json.loads(data)
def api(path):return get(API+path)
def git(*args):return subprocess.check_output(['git',*args],text=True).strip()
def digest(data):return hashlib.sha256(data).hexdigest()
def zjson(z,name):return json.loads(z.read(name).decode('utf-8-sig'))
def run_info(identifier,name):
    run=api('/actions/runs/'+str(identifier))
    assert run['head_sha']==CODE and run['run_attempt']==1 and run['status']=='completed' and run['conclusion']=='success',name+' is not exact completed success'
    jobs=api('/actions/runs/'+str(identifier)+'/jobs?per_page=100')['jobs']
    assert len(jobs)==1 and jobs[0]['status']=='completed' and jobs[0]['conclusion']=='success'
    assert all(s['conclusion']=='success' for s in jobs[0]['steps']),name+' has a skipped or failed mandatory step'
    return run,jobs[0]
def inventory(raw):
    text=raw.decode('utf-8-sig'); results=re.findall(r'RESULT: (\d+) passed, (\d+) failed',text)
    passes=[s.rstrip('\r') for s in re.findall(r'^PASS (.+)$',text,re.M)]
    failures=re.findall(r'^FAIL (.+)$',text,re.M)
    assert len(results)==1 and int(results[0][0])==len(passes)>0 and int(results[0][1])==len(failures)==0
    return passes
head=git('rev-parse','HEAD')
assert head==os.environ['GITHUB_SHA'] and git('rev-parse','HEAD^')==CODE,'Reconcile concurrent checkpoint before writing'
assert not git('status','--porcelain') and git('ls-remote','origin','refs/heads/'+BRANCH).split()[0]==head
assert digest((ROOT/TRACKER).read_bytes())==BASE_TRACKER,'Tracker changed; preserve and reconcile'
focus,focus_job=run_info(FOCUS,'AR066'); full,full_job=run_info(FULL,'Full CI')
artifacts=api('/actions/runs/'+str(FOCUS)+'/artifacts?per_page=100')['artifacts']
selected=[a for a in artifacts if a['name']=='AR066-LiveResource-Evidence' and not a['expired']]
assert len(selected)==1
artifact=selected[0]; binary=get(artifact['archive_download_url'],True)
assert len(binary)==artifact['size_in_bytes'] and 'sha256:'+digest(binary)==artifact['digest']
with zipfile.ZipFile(io.BytesIO(binary)) as z:
    assert z.testzip() is None
    identity=zjson(z,'identity.json'); validation=zjson(z,'validation.json')
    assert identity['code_sha']==validation['code_sha']==CODE and str(identity['run'])==str(FOCUS) and str(identity['attempt'])=='1'
    assert identity['task']=='AR-066' and identity['working_tree']=='CLEAN' and validation['clean_end'] and validation['agent_exit']==0 and not validation['failed']
    assert validation['old_source_control'].startswith('NOT_RUN_TOOL_SAFETY_BLOCKED')
    manifest=zjson(z,'source-receipt.json')
    blobs={line.split('\t',1)[1]:line.split('\t',1)[0].split()[2] for line in git('ls-tree','-r',CODE).splitlines()}
    source_bytes=0
    with zipfile.ZipFile(io.BytesIO(z.read('committed-source.zip'))) as source:
        assert source.testzip() is None and set(source.namelist())=={r['path'] for r in manifest}
        assert len(manifest)==len(source.namelist())
        for row in manifest:
            content=source.read(row['path']);source_bytes+=len(content)
            assert digest(content)==row['sha256'] and row['blob']==blobs[row['path']]
            assert hashlib.sha1(b'blob '+str(len(content)).encode()+b'\0'+content).hexdigest()==row['blob']
    groups=['AR-066-1','AR-066-2','AR-066-3','AR-012','AR-020','AR-065','FULL']
    inventories={name:inventory(z.read(name+'.log')) for name in groups}
    cases=sorted(inventories['AR-066-1']);assert len(cases)==len(set(cases))==76
    assert all(sorted(inventories[g])==cases for g in groups[1:3])
    assert sorted(n for n in inventories['FULL'] if n.startswith('AR-066 '))==cases
    assert len(inventories['FULL'])==1208
    suites=zjson(z,'agent-suites/suite-results.json')
    assert suites['code_sha']==CODE and len(suites['suites'])==74 and len({s['flag'] for s in suites['suites']})==74
    assert all(s['exit_code']==0 for s in suites['suites'])
    expected={(mode,project) for mode in ['read','write','python','command','reference','disk-only','browser-notice','cad-notice'] for project in [True,False]}
    expected|={(tool+'-'+kind,project) for tool in ['word_paragraphs','check_word'] for kind in ['live','disk'] for project in [True,False]}
    expected|={('completion-'+mode,project) for mode in ['no-tools','discovery-only','word-read','excel-read','wrong-kind','wrong-session','incomplete-after-read'] for project in [True,False]}
    expected|={('revision-'+mode,project) for mode in ['upgrade-read','upgrade-final','upgrade-batch','retain-live','ordinary-supplement','tool-data'] for project in [True,False]}
    receipts=[]
    for group in groups[:3]+['FULL']:
        names=[n for n in z.namelist() if n.startswith('receipts-'+group+'/') and n.endswith('.json')]
        batch=[zjson(z,n) for n in names]
        assert len(batch)==len(expected)==50 and {(r['mode'],r['project']) for r in batch}==expected
        for r in batch:
            assert r['level']=='E2' and r['Created']==1 and r['Rounds']>0 and r['forbiddenMarkerAbsent'] and r['reopenedWithoutReplay']
            assert r['E3']==r['E4']=='AWAITING_ENVIRONMENT' and r['E5']=='DEFERRED_BY_USER'
            assert all(re.fullmatch('[a-f0-9]{64}',r[k]) for k in ['sourceHash','wordHash'])
            positive=r['mode']=='disk-only' or r['mode'].endswith('-disk') or r['mode'] in ['completion-word-read','completion-excel-read','revision-ordinary-supplement','revision-tool-data']
            assert (r['status']=='Completed')==positive
            if r['mode'].startswith('completion-'):
                assert r['Reads']==(0 if r['mode'] in ['completion-no-tools','completion-discovery-only'] else 1)
            if r['mode'].startswith('revision-'):
                inputs=0 if r['mode']=='revision-tool-data' else 2 if r['mode']=='revision-upgrade-batch' else 1
                assert r['acceptedUserInputs']==inputs and r['retainedRevisions']==inputs+1
                assert len(r['sourceIds'])==len(set(r['sourceIds']))==inputs+1
            receipts.append({'group':group,**r})
    assert len(receipts)==200
    build=z.read('build.log').decode('utf-8-sig')
    errors=re.findall(r'(\d+) Error\(s\)',build);warnings=re.findall(r'(\d+) Warning\(s\)',build)
    assert errors and all(int(n)==0 for n in errors)
    log_hashes={g:digest(z.read(g+'.log')) for g in groups}
raw=get(API+'/actions/jobs/'+str(full_job['id'])+'/logs',True)
(OUT/'full-job.log').write_bytes(raw)
text=re.sub(r'\x1b\[[0-9;]*[A-Za-z]','',raw.decode('utf-8-sig'))
lines=[re.sub(r'^\d{4}-\d{2}-\d{2}T\S+Z\s?','',line) for line in text.splitlines()]
checkout={m for line in lines for m in re.findall(r'origin \+([a-f0-9]{40}):refs/remotes/pull/3/merge',line)}
assert len(checkout)==1, 'Cannot identify actual full CI checkout'
checkout=checkout.pop()
try:git('cat-file','-e',checkout)
except subprocess.CalledProcessError:subprocess.run(['git','fetch','--no-tags','origin',checkout],check=True)
subprocess.run(['git','diff','--exit-code',CODE,checkout,'--','src','experiments','tests','tools','H2Notes.Avalonia.slnx','.github/workflows/avalonia-ci.yml'],check=True)
subprocess.run(['git','diff','--exit-code',CODE,head,'--','src','experiments','tests','H2Notes.Avalonia.slnx','.github/workflows/avalonia-ci.yml','tools/agent-reliability/validate_ar066.ps1','tools/agent-reliability/run_agent_suites.ps1'],check=True)
starts=[i for i,l in enumerate(lines) if l.startswith('##[group]Run dotnet run --project ') and 'tests\\H2Notes.Tests\\H2Notes.Tests.csproj' in l]
assert len(starts)==1
begin=starts[0]; end=next((i for i in range(begin+1,len(lines)) if lines[i].startswith('##[group]')),len(lines))
full_h2=inventory(('\n'.join(lines[begin:end])).encode())
assert sorted(full_h2)==sorted(inventories['FULL']) and len(full_h2)==1208
full_artifacts=api('/actions/runs/'+str(FULL)+'/artifacts?per_page=100')['artifacts']
assert any(a['name']=='H2Notes-Avalonia-Portable-win-x64' and a['size_in_bytes']>0 and not a['expired'] for a in full_artifacts)
assert any(a['name']=='H2Notes-NasAcceptance-win-x64' and a['size_in_bytes']>0 and not a['expired'] for a in full_artifacts)
evidence={'task':'AR-066','implementation':'PARTIAL_CORE_FOUNDATION','acceptance':'E1/E2_PASS; E3/E4_AWAITING_ENVIRONMENT; NOT_DONE',
 'code_sha':CODE,'runtime_sha':RUNTIME,'branch':BRANCH,'pr':3,'focused_run':FOCUS,'focused_job':focus_job['id'],'full_run':FULL,'full_job':full_job['id'],'attempt':1,
 'full_checkout_sha':checkout,'full_log_sha256':digest(raw),'artifact':{'id':artifact['id'],'bytes':len(binary),'sha256':digest(binary)},
 'source':{'files':len(manifest),'bytes':source_bytes,'all_git_blobs_verified':True},'focused_each':{'passed':76,'failed':0,'repetitions':3},
 'full_h2':{'passed':1208,'failed':0},'retained':{g:len(inventories[g]) for g in ['AR-012','AR-020','AR-065']},
 'independent_agent_suites':74,'build_warnings':int(warnings[-1]),'build_errors':0,'production_receipt_count':200,
 'old_source_control':validation['old_source_control'],'core_followup_status':'MIXED_SOURCE_CONSENT_NOT_IMPLEMENTED','native_acceptance_status':'AWAITING_ENVIRONMENT','inventory':cases,'logs':log_hashes,'production_receipts':receipts,
 'full_artifacts':[{'id':a['id'],'name':a['name'],'bytes':a['size_in_bytes'],'digest':a.get('digest'),'binary_download_verified':False} for a in full_artifacts],
 'historical_failures':[{'run':35889363009,'sha':'ad1cec00c8c8cebd2cf15c7634eaec9e33af1734','result':'40/42 AR066 x3; full1172/1174; wrong write_file fixture; Agent74/74','artifact':10765670041,'artifact_sha256':'df82f172bdb1b993c8f83681db7d2bb662b916e67670833d6bb999daee968d45'},
 {'run':35889368390,'sha':'ad1cec00c8c8cebd2cf15c7634eaec9e33af1734','result':'Full1172/1174; two incorrect write_file fixtures'},
 {'run':35889368749,'sha':'ad1cec00c8c8cebd2cf15c7634eaec9e33af1734','result':'Retained MB94 DesktopHost timeout; separate same-SHA Agent74 corpus passed; no timeout/assertion weakened','artifact':10764367112,'artifact_sha256':'d123e31c226e3764344ce3012ab93772b4fda2f352d9a414bd0f8e678e0188d3'}],
 'previous_verified_checkpoint':{'code_sha':'e8392999218375ee717c21f077a4ba144e38f696','focus_run':35893304107,'full_run':35893309305,'artifact_id':10767115617,'artifact_sha256':'91e694d69ff8b20eec026613d735ba15f0031f433ded2fbfacdc76ef3d4a368f','focused_each':64,'repetitions':3,'full_h2_passed':1196,'agent_suites':74,'production_receipts':152,'scope':'Before trusted-revision integration; not reused as final-code acceptance'},
 'limits':['Trusted source revisions are now enforced, including complete queued batches; per-resource mixed-source consent and same-task approval of a live-to-disk semantic change remain unimplemented conservative restrictions, not environment-only debt.',
 'Native Office original attach incident and real unsaved/reconnect/view-selection behavior not certified by scripted E2.',
 'Native CAD/browser/UIA equivalence is not implemented here; unavailable proof remains blocked.',
 'Source observation does not award semantic goal or mutation verification. No user-PC working tree access.']}
tracker=(ROOT/TRACKER).read_text();start=tracker.index('```yaml',tracker.index('## 7.'))+len('```yaml\n');end=tracker.index('\n```',start)
h=json.loads(tracker[start:end]);assert h['active_task']=='AR-066' and h['implementation_status']=='ACTIVE'
h.update(phase='AR-066_PARTIAL_E2_SOURCE_REVISIONS_PASS_NATIVE_AND_CONSENT_PENDING',active_task='AR-066',implementation_status='PARTIAL',acceptance_status='AWAITING_ENVIRONMENT',
 last_code_commit=CODE,last_runtime_source_commit=RUNTIME,last_validated_code_commit=CODE,last_validation_result='AR066_76_PASS_X3; TRUSTED_SOURCE_REVISION12_PASS; FULL1208_PASS; AGENT74_PASS; 200_E2_RECEIPTS; OLD_SOURCE_CONTROL_NOT_RUN_TOOL_SAFETY_BLOCKED; NATIVE_E3_E4_AWAITING; NOT_DONE',
 capture_checked_head=head,checkpoint_saved_at_utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),completed_this_session=[],implemented_this_session=['AR-066 core foundation'],
 working_tree='Exact focused CI checkout CLEAN; source manifest matches Git blobs; full test-merge source equal. User-PC working tree NOT_ACCESSIBLE. This checkpoint writes reviewed documentation only.',uncommitted_files=[],
 remaining_in_active_task=['Review/complete per-resource mixed live/disk source roles and explicit same-task approval of semantic source changes using existing goal/session/approval contracts. Current policy preserves restrictions and requires a new task for disk semantics; no hidden waiver. Trusted source revision tightening and queued-batch provenance are already implemented/tested.',
 'Execute real native Word unsaved/Excel selection/multiple-view/transient-reconnect/no-disk-fallback corpus and real H2 UI/model cases only in an authorized isolated environment. E3/E4 remain AWAITING_ENVIRONMENT.'],
 last_test_commands=[{'command':'tools/agent-reliability/validate_ar066.ps1','sha':CODE,'run_id':FOCUS,'job_id':focus_job['id'],'attempt':1,'result':'AR06676/76 x3; trusted-revision12 included; retained pass; full1208/1208; Agent74/74;200declaredE2receipts'},
 {'command':'Avalonia CI: build/full H2/all mandatory suites/win-x64 publish/packaged helper IPC','sha':checkout,'head_sha':CODE,'run_id':FULL,'job_id':full_job['id'],'attempt':1,'result':'Every mandatory step success; full1208/1208; test merge NOT main merge'}],
 next_exact_action='Reconcile latest refs and this handoff. Continue ONLY AR066: complete/review per-resource mixed-source consent and the explicit semantic-change approval route using the existing goal/session/approval contract; keep the tested monotonic trusted-revision guard, exact native identity and no-replay behavior. Then run exact-SHA regression. Real native/H2 gates require the authorized environment; no acceptance waiver or uncertain mutation replay. Do not start AR067.',
 next_ready_independent_task='AR067 NOT_STARTED; do not advance while AR066 core follow-up remains.',known_failures=[],resolved_in_this_session=['Wrong write_file fixture replaced with canonical write_text without relaxed assertions','Disk Office namespace admission gap guarded with valid DOCX paired tests','Metadata/prose-only live completion now blocked through existing hooks','Incomplete native post-observation catalog now refused before read body exposure','Trusted user source revision batches now tighten admission and completion without promoting tool data or replaying work'],
 core_followup_status='MIXED_SOURCE_CONSENT_NOT_IMPLEMENTED',native_acceptance_status='AWAITING_ENVIRONMENT',user_pc_working_tree='NOT_ACCESSIBLE',ar066_evidence='docs/agent-reliability/AR-066/evidence.json',ar066_core_limits=evidence['limits'])
h['ci_runs'].extend([{'id':FOCUS,'code_sha':CODE,'attempt':1,'result':'SUCCESS_VERIFIED','task':'AR-066'},{'id':FULL,'head_sha':CODE,'checkout_sha':checkout,'attempt':1,'result':'SUCCESS_VERIFIED','task':'AR-066'}])
h['ci_runs'].extend(evidence['historical_failures'])
for item in h['critical_user_reported_repairs']:
 if item['id']=='AR-066':item['status']='PARTIAL / E1-E2_PASS / MIXED_SOURCE_CONSENT_CORE_FOLLOWUP / E3-E4_AWAITING_ENVIRONMENT'
tracker=tracker[:start]+json.dumps(h,ensure_ascii=False,indent=2)+tracker[end:]
old='| AR-066 | LiveResource/native app semantics + no silent live→disk fallback | 012/020 | E3/E4 | ACTIVE — core validation pending; E3/E4 AWAITING_ENVIRONMENT |'
assert tracker.count(old)==1
tracker=tracker.replace(old,old.replace('ACTIVE — core validation pending; E3/E4 AWAITING_ENVIRONMENT','PARTIAL — E1/E2 PASS; mixed-source consent follow-up; E3/E4 AWAITING_ENVIRONMENT'))
old='**Current task is AR-066 core, selected under section 2.2 with implemented AR-012/020 dependencies. AR-065 stays IMPLEMENTED/E2_PASS with E4 AWAITING_ENVIRONMENT; AR-064 stays PARTIAL. No acceptance is waived.**'
assert tracker.count(old)==1
tracker=tracker.replace(old,'**Current task remains AR-066 PARTIAL: core source/native identity, completion and trusted-revision foundation passed E1/E2/full CI; mixed-source consent follow-up and real native E3/H2 E4 remain open. AR-065 E4, AR-064 PARTIAL and all prior debts are retained.**')
allowed=[TRACKER,'docs/agent-reliability/AR-066/evidence.json','docs/agent-reliability/AR-066/implementation-review.md','docs/agent-reliability/AR-066/real-native-acceptance.md']
assert git('ls-remote','origin','refs/heads/'+BRANCH).split()[0]==head,'Concurrent HEAD changed before write'
(ROOT/TRACKER).write_text(tracker,encoding='utf-8')
(ROOT/allowed[1]).write_text(json.dumps(evidence,ensure_ascii=False,indent=2),encoding='utf-8')
changed=set(git('status','--porcelain').splitlines())
assert changed and set(git('diff','--name-only').splitlines())<={TRACKER},'Unexpected tracked document changes'
subprocess.run(['git','diff','--check'],check=True)
subprocess.run(['git','add','--',*allowed],check=True)
assert set(git('diff','--cached','--name-only').splitlines())<set(allowed) or set(git('diff','--cached','--name-only').splitlines())==set(allowed)
git('config','user.name','H2 AR-066 checkpoint');git('config','user.email','41898282+github-actions[bot]@users.noreply.github.com')
subprocess.run(['git','commit','-m','docs(AR-066): persist verified partial E2 checkpoint and exact native/mixed-source consent debt'],check=True)
saved=git('rev-parse','HEAD');subprocess.run(['git','push','origin','HEAD:refs/heads/'+BRANCH],check=True)
assert git('ls-remote','origin','refs/heads/'+BRANCH).split()[0]==saved,'Reconcile uncertain checkpoint push'
for path in allowed:
    target=OUT/path;target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes((ROOT/path).read_bytes())
(OUT/'SESSION-HANDOFF.json').write_text(json.dumps(h,ensure_ascii=False,indent=2),encoding='utf-8')
(OUT/'saved.json').write_text(json.dumps({'checkpoint_commit':saved,'checked_head':head,'code_sha':CODE,'branch':BRANCH,'paths':allowed,'hashes':{p:digest((ROOT/p).read_bytes()) for p in allowed}},indent=2))
print('Saved verified PARTIAL AR066 checkpoint:',saved,'; no E3/E4 or task DONE claim.')

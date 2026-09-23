"""Docs-only checkpoint guard; not dispatched until exact referenced CI runs finish."""
import datetime,hashlib,io,json,os,pathlib,re,subprocess,urllib.request,urllib.error,zipfile
REPO='HoangHung997/NotePad'; BRANCH='feature/h2-agent-reliability-ar-000'
TESTED='26d79367392a1f7f02d6acaef116e420ac9125dc'
FOCUSED=35881691347; FULL=35881698306
ARTIFACT_ID=10761303971; EXPECTED_DIGEST='c7480e019584e702d89ef18398f9651e6d75bb994f5b1f18837845ec764e55c2'
TRACKER='docs/H2_AGENT_RELIABILITY_IMPLEMENTATION_TASKS.md'
TRACKER_BLOB='36af94e7f2df37cd5a93cd46e5ff8c7459620bc6'
DOCROOT=pathlib.Path('docs/agent-reliability/AR-065')
OUT=pathlib.Path('artifacts/ar065-checkpoint'); OUT.mkdir(parents=True,exist_ok=True)
def git(*args): return subprocess.check_output(['git',*args],text=True).strip()
def h(data):return hashlib.sha256(data).hexdigest()
def save(name,obj):
 p=OUT/name;p.parent.mkdir(parents=True,exist_ok=True);p.write_text(json.dumps(obj,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
class NoRedirect(urllib.request.HTTPRedirectHandler):
 def redirect_request(self,*args,**kwargs): return None
def get(path,binary=False):
 url='https://api.github.com/repos/'+REPO+'/'+path
 req=urllib.request.Request(url,headers={'Authorization':'Bearer '+os.environ['GH_TOKEN'],'Accept':'application/vnd.github+json','User-Agent':'H2-AR065-checkpoint'})
 try:
  with urllib.request.build_opener(NoRedirect).open(req,timeout=45) as r:data=r.read(60*1024*1024+1)
 except urllib.error.HTTPError as e:
  if e.code not in (301,302,303,307,308):raise RuntimeError('GitHub GET failed: '+str(e.code)) from None
  url=e.headers['Location'];assert url.startswith('https://')
  # Never forward the authorization header to a signed artifact/log download origin.
  with urllib.request.urlopen(urllib.request.Request(url,headers={'User-Agent':'H2-AR065-checkpoint'}),timeout=45) as r:data=r.read(60*1024*1024+1)
 assert len(data)<=60*1024*1024
 return data if binary else json.loads(data)
def completed(run):
 r=get(f'actions/runs/{run}');assert r['head_sha']==TESTED and r['run_attempt']==1 and r['status']=='completed' and r['conclusion']=='success'
 j=get(f'actions/runs/{run}/jobs?per_page=100');assert j['total_count']==1
 job=j['jobs'][0];assert job['status']=='completed' and job['conclusion']=='success'
 assert all(s['status']=='completed' and s['conclusion']=='success' for s in job['steps'] if not s['name'].startswith('Post '))
 save(f'run-{run}.json',{'id':run,'head_sha':r['head_sha'],'attempt':r['run_attempt'],'status':r['status'],'conclusion':r['conclusion'],'job_id':job['id'],'steps':job['steps']})
 return job
head=git('rev-parse','HEAD');assert head==os.environ['EXPECTED_HEAD'] and not git('status','--porcelain')
assert git('ls-remote','origin','refs/heads/'+BRANCH).split()[0]==head
old=pathlib.Path(TRACKER).read_bytes();assert hashlib.sha1(b'blob '+str(len(old)).encode()+b'\0'+old).hexdigest()==TRACKER_BLOB
pr=get('pulls/3');assert pr['head']['ref']==BRANCH and pr['head']['sha']==head and pr['base']['sha']=='1283bc13e07c3cd47d04886166de3dfc595422c0' and pr['state']=='open' and pr['draft'] and not pr['merged']
focusjob=completed(FOCUSED);fulljob=completed(FULL)
full_artifacts=get(f'actions/runs/{FULL}/artifacts?per_page=100')['artifacts']
for name in ('H2Notes-Avalonia-Portable-win-x64','H2Notes-NasAcceptance-win-x64'):
 matches=[x for x in full_artifacts if x['name']==name];assert len(matches)==1 and not matches[0]['expired'] and matches[0]['size_in_bytes']>0
full_artifacts=[{'id':x['id'],'name':x['name'],'bytes':x['size_in_bytes'],'digest':x.get('digest'),'archive_download_verified':False} for x in full_artifacts]
meta=get(f'actions/artifacts/{ARTIFACT_ID}');assert meta['workflow_run']['id']==FOCUSED and not meta['expired'] and meta['digest']=='sha256:'+EXPECTED_DIGEST
raw=get(f'actions/artifacts/{ARTIFACT_ID}/zip',True);assert h(raw)==EXPECTED_DIGEST and len(raw)==meta['size_in_bytes']
z=zipfile.ZipFile(io.BytesIO(raw));assert z.testzip() is None
def js(name):return json.loads(z.read(name).decode('utf-8-sig'))
identity=js('identity.json');validation=js('validation.json');control=js('control.json')
assert identity['code_sha']==validation['code_sha']==control['source_sha']==TESTED and identity['working_tree']=='CLEAN'
assert validation['clean_end'] and validation['control_exit']==validation['agent_exit']==0 and not validation['failed']
assert control['results']==[['0','4']] and control['exact_restoration'] and control['clean_restoration'] and control['restored_build_exit']==0
inventory={}
def parse(text,expected):
 p=re.findall(r'^PASS (.*)$',text,re.M);m=re.findall(r'RESULT: (\d+) passed, (\d+) failed',text)
 assert m==[(str(expected),'0')] and len(p)==expected and not re.search(r'^FAIL ',text,re.M)
 names=sorted(x.strip() for x in p if x.startswith('AR-065 '));assert len(names)==55
 return names
for case in ('AR-065-1','AR-065-2','AR-065-3','FULL'):
 text=z.read(case+'.log').decode('utf-8-sig');inventory[case]=parse(text,1132 if case=='FULL' else 55)
assert all(names==inventory['AR-065-1'] for names in inventory.values())
agent=js('agent-suites/suite-results.json');assert agent['code_sha']==TESTED and len(agent['suites'])==74 and all(s['exit_code']==0 for s in agent['suites'])
source=zipfile.ZipFile(io.BytesIO(z.read('committed-source.zip')));assert source.testzip() is None
receipts=js('source-receipt.json');assert set(source.namelist())=={r['path'] for r in receipts}
blobs={line.split('\t',1)[1]:line.split('\t',1)[0].split()[-1] for line in git('ls-tree','-r',TESTED).splitlines()}
for r in receipts:
 data=source.read(r['path']);assert h(data)==r['sha256'] and r['blob']==blobs[r['path']]
 assert hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()==r['blob']
eol=[]
for path,digest in control['restored_sha256'].items():
 data=source.read(path);mode='EXACT_GIT_BYTES'
 if h(data)!=digest:
  assert b'\r' not in data and h(data.replace(b'\n',b'\r\n'))==digest, 'Unexpected restored-byte difference'
  mode='GIT_LF_TO_WINDOWS_CRLF_ONLY'
 eol.append({'path':path,'git_source_sha256':h(data),'restored_checkout_sha256':digest,'comparison':mode,'original_checkout_restored_exactly':control['exact_restoration'],'git_clean_after_restore':control['clean_restoration']})
production=[]
for name in z.namelist():
 if not name.startswith('receipts-') or not name.endswith('.json'):continue
 r=js(name);assert r['level']=='E2' and r['E4']=='AWAITING_ENVIRONMENT' and r['reopenedWithoutReplay']
 assert r['Created']==1 and r['Sends']==(1 if r['mode'] in ('initial-400','unknown-tool') else 3)
 assert r['status']==('Completed' if r['mode']=='read-final' else 'Failed')
 assert r['sourceHash']==h('AR065-EXACT-ĐÚNG-😀'.encode()) and r['controlHash']==h(b'UNCHANGED')
 assert r['mutationCount']==(1 if r['mode']=='write-400' else 0)
 if r['mode']=='write-400':assert r['evidenceCount']>0 and r['outputHash']==r['sourceHash']
 else:assert r['outputHash'] is None
 production.append({'artifact_path':name,'sha256':h(z.read(name)),'observed':r})
assert len(production)==40 and len({x['observed']['task'] for x in production})==40
fullraw=get(f"actions/jobs/{fulljob['id']}/logs",True);(OUT/'full-job.log').write_bytes(fullraw)
text=fullraw.decode('utf-8-sig').replace('\r\n','\n');text=re.sub(r'\x1b\[[0-9;]*[A-Za-z]','',text)
text=re.sub(r'(?m)^\d{4}-\d{2}-\d{2}T\S+Z ','',text)
start=text.index('##[group]Run dotnet run --project .\\tests\\H2Notes.Tests\\H2Notes.Tests.csproj')
end=text.index('##[group]',start+12);fullnames=parse(text[start:end],1132);assert fullnames==inventory['AR-065-1']
merge=re.search(r'log -1 --format=%H\n([0-9a-f]{40})',text)[1]
subprocess.run(['git','fetch','--no-tags','origin',merge],check=True,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
paths=['src','experiments','tests','tools','H2Notes.Avalonia.slnx','.github/workflows/avalonia-ci.yml']
for ref in (merge,head):subprocess.run(['git','diff','--exit-code',TESTED,ref,'--',*paths],check=True,stdout=subprocess.PIPE)
assert '"ok":true' in text and '"FixtureMode":false' in text and '"StaThread":true' in text
runtime=git('log','-1','--format=%H',TESTED,'--','experiments/H2AgentLab/Transport/OpenAiResponsesWireContract.cs','experiments/H2AgentLab/Transport/OpenAiResponsesTransport.cs','experiments/H2AgentLab/Transport/OpenAiResponsesWebSocketTransport.cs','src/H2Notes.Core/AiModelCapabilities.cs')
build_summary=re.search(r'(\d+) Warning\(s\)\n\s*(\d+) Error\(s\)',text[:start]);assert build_summary and build_summary[2]=='0'
evidence={'build_warnings':int(build_summary[1]),'build_errors':0,'task':'AR-065','implementation':'IMPLEMENTED','acceptance':'E1/E2_PASS; E4_AWAITING_ENVIRONMENT; NOT_DONE','code_sha':TESTED,'runtime_commit':runtime,'branch':BRANCH,'pr':3,'focused_run':FOCUSED,'focused_job':focusjob['id'],'full_run':FULL,'full_job':fulljob['id'],'attempt':1,'full_checkout_sha':merge,'full_log_sha256':h(fullraw),'full_artifact_inventory':full_artifacts,'full_h2':{'passed':1132,'failed':0},'focused_each':{'passed':55,'failed':0,'repetitions':3},'independent_agent_suites':74,'artifact':{'id':ARTIFACT_ID,'bytes':len(raw),'sha256':EXPECTED_DIGEST},'source':{'files':len(receipts),'bytes':sum(len(source.read(n)) for n in source.namelist()),'all_git_blobs_verified':True},'negative_control':control,'restoration_eol_reconciliation':eol,'inventory':inventory['AR-065-1'],'production_receipts':production,'scope':'Scripted handler/socket; actual serializers, production adapter/runtime, disposable file IO, archive. Not actual model/UI/Office acceptance.','original_http400':'Exact original user provider reason still not captured; E2 wire defects are not proof of the sole original cause.','E3':'AWAITING_ENVIRONMENT','E4':'AWAITING_ENVIRONMENT','E5':'AR-083 DEFERRED_BY_USER'}
# No writes above this point altered tracked source or the canonical tracker.
assert not git('status','--porcelain') and git('ls-remote','origin','refs/heads/'+BRANCH).split()[0]==head
text=old.decode();section=text.index('## 7. SESSION HANDOFF');m=re.search(r'```yaml\n(.*?)\n```',text[section:],re.S);d=json.loads(m[1])
d.update(phase='AR-065_IMPLEMENTED_E2_PASS_AWAITING_REAL_H2_E4',active_task='AR-065',implementation_status='IMPLEMENTED',acceptance_status='AWAITING_ENVIRONMENT',owner_session='chatgpt-ar065-reconcile-2026-09-23',last_code_commit=TESTED,last_validated_code_commit=TESTED,last_runtime_source_commit=runtime,last_test_commit=git('log','-1','--format=%H',TESTED,'--','tests'),capture_checked_head=head,finalization_checked_head=head,previous_saved_checkpoint_commit=git('log','-1','--format=%H','--',TRACKER),checkpoint_saved_at_utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),last_full_ci_checkout_sha=merge,last_validation_result='AR065_55_PASS_X3; PRODUCTION10_X3_AND_FULL; OLD_TRANSPORT4_EXPECTED_FAILURES; FULL1132_PASS; AGENT74_PASS; E4_AWAITING_ENVIRONMENT; NOT_DONE',working_tree='Exact focused checkout CLEAN, control bytes restored and rebuilt, final CI checkout CLEAN. Full CI test-merge application/test/tools trees equal. User-PC working tree NOT_ACCESSIBLE. This checkpoint changes documentation only.',uncommitted_files=[],completed_this_session=[],implemented_this_session=['AR-065'],last_native_acceptance='AR-020/033 E3 and AR-065 E4 AWAITING_ENVIRONMENT; AR-083 DEFERRED_BY_USER',code_commit_lookup='git log -1 --format=%H -- experiments/H2AgentLab/Transport/OpenAiResponsesWireContract.cs',next_task_if_active_done='AR-066 after AR-065 gates/checkpoint and explicit independent dependency assessment; then AR-067/068 and AR-069',next_ready_independent_task='AR-066 core is the next critical candidate; NOT_STARTED in this session.')
d['ar065_reconciled_base']='6d6fd8427498b00ba50a2273ded1af8d9467f5e5'
if 'foundation_source_parent' in d:d['parked_ar064'].setdefault('historical_foundation_source_parent',d.pop('foundation_source_parent'))
if d['external_blockers'] and isinstance(d['external_blockers'][0],str):d['external_blockers'][0]='AR-020/033 native E3 and AR-051 real-model E4 remain AWAITING_ENVIRONMENT; AR-083 stays DEFERRED_BY_USER. No acceptance waiver.'
d['last_test_commands']=[{'command':'tools/agent-reliability/validate_ar065.ps1 (old source control; AR-065 x3; full H2; all Agent flags)','sha':TESTED,'run_id':FOCUSED,'job_id':focusjob['id'],'attempt':1,'result':'55/55 x3; full1132/1132; Agent74/74; exact old-source4 expected failures'},{'command':'Avalonia CI (build, full H2, all required suites, win-x64 publish, packaged helper IPC)','sha':merge,'head_sha':TESTED,'run_id':FULL,'job_id':fulljob['id'],'attempt':1,'result':'All mandatory steps PASS; H2 1132/1132; test merge NOT main merge'}]
d['remaining_in_active_task']=['Real H2 UI with existing authorized exact Luna Responses profile: read-only task, harmless creation/readback and deferred tool loading, plus reopen/no replay, under a finite operator budget.','Capture exact sanitized original provider rejection if it remains. No claim of real incident resolution from scripted E2 alone.']
d['known_failures']=[]
d['external_blockers'].append({'id':'AR-065-E4','status':'AWAITING_ENVIRONMENT','missing_evidence':'Authorized Windows/H2/model profile and finite live-test budget; Remote Desktop Commander was not installed when checked. No new credential, endpoint or personal-document test authorized.'})
d['pending_user_decisions']=['Provide an authorized isolated Windows/H2 test environment and a finite live-model test budget; keep credentials on that machine, never paste keys into chat.']
d['next_exact_action']='Reconcile latest branch/PR/checkpoint first. Continue only AR-065 real H2/Luna E4 using docs/agent-reliability/AR-065/real-h2-acceptance.md and an authorized existing profile with finite budget. Read journal and verify postconditions before any uncertain operation. Do not reapply either old ZIP or corrupt transfer payload, rename registry tools, change provider/engine, waive E4, or close AR064/MB124-127. If no authorized environment exists, retain AWAITING_ENVIRONMENT; the next independent critical core candidate is AR066 after a separately recorded dependency decision.'
d['resolved_in_this_session']=['Reconciled worker AR065 runtime wire repair without overwriting it or reapplying the stale local ZIP.','Fixed two AR050 fixtures to advertise their scripted lookup callable while retaining unadvertised-tool and budget guards.','Added ten Global/Project production HTTP/runtime/file/archive cases; fixed missing test namespace and required read offset, then verified exact leading JSON content/hash.','Replaced a timing-dependent two-second child sleep in retained AR040 roundtrip with a bounded same-job two-observed-poll handshake; preserved effect, identity, terminal, output and archive assertions.','Ran and independently verified exact-SHA focused/full Windows CI, old-source negative control, source/artifact checksums and per-case receipts; no real provider calls.']
for r in d['critical_user_reported_repairs']:
 if r['id']=='AR-065':r['status']='IMPLEMENTED / E2_PASS / AWAITING_ENVIRONMENT / NOT_DONE'
d['independent_dependency_assessment']={'id':'AR-065','dependencies':['AR-010 accepted','AR-011 accepted','AR-050 accepted with repaired stale fixture declarations'],'basis':'Critical priority after saved AR064 partial checkpoint. No E3/E4 waiver and no other AR implementation this session.'}
for item in [{'id':35874131889,'sha':'0a75675e73ad7567e458b41bafe0e57b91363f87','result':'FAILURE before source mutation','reason':'Damaged transfer zlib bytes; other worker later removed payload/workflow.'},{'id':35875731324,'sha':'6d6fd8427498b00ba50a2273ded1af8d9467f5e5','result':'1120 passed / 2 failed; later stages skipped','reason':'Retained AR050 scripted lookup omitted from advertised tools.'},{'id':35878985021,'sha':'8ba51247dc74522a752acf434317e8c053171018','result':'BUILD_FAILURE; tests NOT_RUN','reason':'New test missing H2Notes.Avalonia namespace import.'},{'id':35879547779,'sha':'d5be56c91039bdd4ec0696acccd41c91c12d34f9','result':'AR065 51/4 x3; full1127/5; Agent74 passed; NOT acceptance','artifact_id':10760388238,'digest':'a061fe29e18e602f1ec6cdedf47dacb0131605d2c6e1801731a64b692aa23c2d','reason':'Four new read fixtures omitted offset; one retained AR040 two-poll timing assumption.'}]:
 item['attempt']=1;d['ci_runs'].append(item)
d['ci_runs'] += [{'id':FOCUSED,'sha':TESTED,'attempt':1,'job_id':focusjob['id'],'result':'SUCCESS; E1/E2 only','artifact_id':ARTIFACT_ID,'artifact_sha256':EXPECTED_DIGEST},{'id':FULL,'head_sha':TESTED,'actual_checkout_sha':merge,'attempt':1,'job_id':fulljob['id'],'result':'SUCCESS; H2 1132/0, all mandatory stages and publish/helper IPC'}]
d['evidence_locations'] += ['docs/agent-reliability/AR-065/evidence.json','docs/agent-reliability/AR-065/implementation-review.md','docs/agent-reliability/AR-065/real-h2-acceptance.md',f'Actions run {FOCUSED} artifact {ARTIFACT_ID} SHA256 {EXPECTED_DIGEST}']
a=section+m.start(1);b=section+m.end(1);text=text[:a]+json.dumps(d,ensure_ascii=False,indent=2)+text[b:]
status='| AR-065 | OpenAI/Luna Agent tool-call HTTP 400 | 010/011/050 | E2 + E4 real OpenAI | NOT_STARTED — CRITICAL |'
assert text.count(status)==1;text=text.replace(status,status.replace('NOT_STARTED — CRITICAL','IMPLEMENTED / E2_PASS / AWAITING_ENVIRONMENT — NOT_DONE'))
oldline='**Current active work remains AR-064. Do not discard it.** After AR-064 is checkpointed, the next implementation sequence is **AR-065 → AR-066 → AR-067 → AR-068 → AR-069**, before selecting another ordinary roadmap task.'
assert text.count(oldline)==1;text=text.replace(oldline,'**AR-064 is checkpointed PARTIAL, not DONE. Current task is AR-065, implemented with E1/E2 and full CI; real H2/model E4 remains AWAITING_ENVIRONMENT.** Keep critical sequence **AR-065 → AR-066 → AR-067 → AR-068 → AR-069**, with explicit dependency/evidence assessment before any next task. Do not discard AR-064 lifecycle/pin/RC-28 debt.')
DOCROOT.mkdir(parents=True,exist_ok=True)
(DOCROOT/'evidence.json').write_text(json.dumps(evidence,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
# Publish reviewed templates only after all evidence guards pass; they are not runtime state.
for name in ('implementation-review.md','real-h2-acceptance.md'):
 target=DOCROOT/name;assert not target.exists(), 'Do not overwrite a concurrent AR065 document'
 target.write_bytes((pathlib.Path('.github/ar065-checkpoint')/name).read_bytes())
pathlib.Path(TRACKER).write_text(text,encoding='utf-8')
save('SESSION-HANDOFF.json',d);save('evidence.json',evidence)
allowed=[TRACKER,str(DOCROOT/'evidence.json'),str(DOCROOT/'implementation-review.md'),str(DOCROOT/'real-h2-acceptance.md')]
assert set(git('diff','--name-only').splitlines())<={TRACKER} and not git('diff','--cached','--name-only')
subprocess.run(['git','diff','--check'],check=True)
subprocess.run(['git','config','user.name','H2 AR-065 checkpoint'],check=True);subprocess.run(['git','config','user.email','41898282+github-actions[bot]@users.noreply.github.com'],check=True)
subprocess.run(['git','add','--',*allowed],check=True)
assert set(git('diff','--cached','--name-only').splitlines())==set(allowed)
subprocess.run(['git','commit','-m','docs(AR-065): save verified exact-source E2 checkpoint and retain real H2 E4 gate'],check=True)
assert git('ls-remote','origin','refs/heads/'+BRANCH).split()[0]==head
subprocess.run(['git','push','origin','HEAD:refs/heads/'+BRANCH],check=True)
commit=git('rev-parse','HEAD');assert git('ls-remote','origin','refs/heads/'+BRANCH).split()[0]==commit
saved={'checkpoint_commit':commit,'parent':head,'code_sha':TESTED,'branch':BRANCH,'files':[]}
for path in allowed:
 data=pathlib.Path(path).read_bytes();saved['files'].append({'path':path,'sha256':h(data)});target=OUT/path;target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(data)
save('saved.json',saved);print('Verified docs-only checkpoint saved:',commit)

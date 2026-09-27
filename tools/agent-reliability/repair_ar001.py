#!/usr/bin/env python3
"""Finalize AR-001 only after independently re-reading its successful GitHub CI metadata.
Writes four bounded documentation files; no runtime, settings, model or user data changes.
"""
from pathlib import Path
import datetime,json,os,re,subprocess,urllib.request
CODE='f3ebc4d336b8d6752436412840675fb2e7204e1d'
MAIN='1283bc13e07c3cd47d04886166de3dfc595422c0'
BRANCH='feature/h2-agent-reliability-ar-000'
TRACKER='docs/H2_AGENT_RELIABILITY_IMPLEMENTATION_TASKS.md'
ROOT='docs/agent-reliability/AR-001/'
FILES=[TRACKER,ROOT+'implementation.md',ROOT+'ci-attempts.json',ROOT+'acceptance.json']
def git(*args): return subprocess.check_output(['git',*args],text=True).strip()
def require(ok,message):
 if not ok: raise RuntimeError(message)
def api(path):
 require(path.startswith('/actions/runs/'),'Only same-repository workflow reads are allowed')
 request=urllib.request.Request('https://api.github.com/repos/HoangHung997/NotePad'+path,
  headers={'Accept':'application/vnd.github+json','User-Agent':'H2-AR001-acceptance','X-GitHub-Api-Version':'2022-11-28'})
 with urllib.request.urlopen(request,timeout=30) as response: return json.load(response)
require(os.environ['AR_BRANCH']==BRANCH and git('rev-parse','HEAD')==os.environ['AR_HEAD'],'Wrong branch/head')
require(not git('status','--porcelain'),'Dirty checkout; preserve unrelated work')
if Path(FILES[3]).exists():
 print('AR-001 acceptance already frozen; do not rewrite a subsequent task checkpoint.');raise SystemExit(0)
require(git('rev-parse','origin/main')==MAIN,'Main advanced; reconcile before finalization')
require(not git('diff',CODE,'HEAD','--','src','experiments','tests','.github/workflows/avalonia-ci.yml'),'Runtime/test/full-workflow changed after validation')
for path in FILES[:3]: require(git('rev-parse','HEAD:'+path)==git('rev-parse',CODE+':'+path),'Documentation owner changed: '+path)
full=api('/actions/runs/35694774117')
require(full['head_sha']==CODE and full['status']=='completed' and full['conclusion']=='success','Exact-source full CI is not successful')
jobs=api('/actions/runs/35694774117/jobs?per_page=100')
require(jobs['total_count']==len(jobs['jobs'])==1,'Incomplete full-CI job inventory')
job=jobs['jobs'][0];require(job['id']==106639128613,'Unexpected full-CI attempt')
steps=[{'number':s['number'],'name':s['name'],'status':s['status'],'conclusion':s['conclusion']} for s in job['steps']]
require(len(steps)>90 and all(s['status']=='completed' and s['conclusion']=='success' for s in steps),'A mandatory CI step did not pass')
for name in ['Publish self-contained Windows x64','Verify packaged Agent helpers start and answer IPC','Publish H2 NAS acceptance probe Windows x64']:
 require(any(s['name']==name and s['conclusion']=='success' for s in steps),'Required publish/smoke step is absent')
artifacts=api('/actions/runs/35694774117/artifacts?per_page=100')
require(artifacts['total_count']==len(artifacts['artifacts'])==2,'Publish artifact inventory changed')
expected={10680490207:(109941950,'sha256:fac6f92f42fbdb4da55c1e8fb92ea40e57177e8350211ac72f356fc7dfcb640c'),10679742893:(39380124,'sha256:aad1b3684a6af442b5489dc978b42b87b8209125d78a8213ec570f894ebbde69')}
packages=[]
for a in artifacts['artifacts']:
 require(a['id'] in expected and (a['size_in_bytes'],a['digest'])==expected[a['id']] and not a['expired'],'Package digest/size/availability changed')
 packages.append({k:a[k] for k in ['id','name','size_in_bytes','digest','created_at','expires_at']})
focused=api('/actions/runs/35694729061')
require(focused['status']=='completed' and focused['conclusion']=='success','Focused all-suite acceptance is not successful')
focused_artifacts=api('/actions/runs/35694729061/artifacts?per_page=100')
fa=next(a for a in focused_artifacts['artifacts'] if a['id']==10680096876)
require(fa['digest']=='sha256:5d08cd125ad6163e9bf491f36281794d0a14840284a8ff1cd8f9f8aa7fb7c66a' and fa['size_in_bytes']==382667 and not fa['expired'],'Focused evidence identity changed')
now=datetime.datetime.now(datetime.timezone.utc).isoformat()
acceptance={'task':'AR-001','spec_version':'H2-AR-SPEC-1.0','status':'DONE','implementation_status':'IMPLEMENTED','acceptance_status':'E2_PASS',
 'code_sha_tested':CODE,'branch':BRANCH,'pr':3,'main_unchanged':MAIN,'recorded_at_utc':now,
 'full_ci':{'run_id':35694774117,'job_id':job['id'],'url':full['html_url'],'attempt':full['run_attempt'],'conclusion':'success','actual_checked_head':CODE,'all_steps':steps},
 'focused_ci':{'run_id':35694729061,'url':focused['html_url'],'workflow_trigger_sha':focused['head_sha'],'actual_checked_head':CODE,
  'artifact_id':fa['id'],'artifact_sha256':fa['digest'],'archive_bytes':fa['size_in_bytes'],
  'evidence_readback':'Artifact ZIP downloaded and hash-verified by implementation session; code-sha.txt and all-agent-suites/suite-results.json both identify the tested code above.',
  'repetitions':[{'passed':13,'failed':0} for _ in range(3)],'required_agent_suites':74,'passed_suites':74,'failed_suites':0,
  'minimum_bootable_agent_cases':{'passed':12,'failed':0},'supported_core_corpus':{'passed':14,'total':14,'false_completion_violations':0},'permission_boundary_violations':0},
 'packages':packages,
 'package_content_inspection':{'artifact_id':10680490207,'downloaded_archive_bytes':109941950,
  'downloaded_sha256':'fac6f92f42fbdb4da55c1e8fb92ea40e57177e8350211ac72f356fc7dfcb640c',
  'file_count':482,'uncompressed_bytes':297834536,
  'nonempty_entries_checked':['H2Notes.Avalonia.exe','H2AgentLab.OfficeHost.dll','H2AgentLab.OfficeHost.runtimeconfig.json','H2AgentLab.OfficeHost.deps.json','H2AgentLab.DesktopHost.dll','H2AgentLab.DesktopHost.runtimeconfig.json','H2AgentLab.DesktopHost.deps.json'],
  'exe_dll_headers':'MZ verified in downloaded bytes; no Linux execution claimed',
  'app_exe_sha256':'86cb8d21e288b047f20be7d1e6ab78209b0b767265c766ad559ed8ed7e2070f9',
  'office_helper_dll_sha256':'a931343fd2037cb1f2f2ff57057ecd7600026b687957601219bea3b503c97c1c',
  'desktop_helper_dll_sha256':'166398d1654df37e53a85849e6153db230ffbccc772887650b2061cdc75e0ddc',
  'execution_evidence':'Windows full CI packaged-helper startup/IPC smoke, not installed Office application acceptance'},
 'resolved_scope':[
  'B01 canonical list_skills/read_skill constants in production prompt',
  'B02 shared 1..128 batch verdict in schema adapter client server native/fixture backend; 129/200 rejected before effects, no truncation',
  'B03 exact tool/executor guards recognize already-shipped read_tool_output while initial exposure remains two schemas',
  'B11 existing task teardown is awaitable; scheduler/provider awaits do not depend on UI pumping; cleanup preserves primary errors; owned-process forced-timeout regression',
  'Full-CI fixture drift: Phase-11 read_feed contract; MB-33 actual side-effect readback; MB-74 actual activation/payload readback with tamper negative; MB-100 candidate-bound recovery plus original unrelated-error negative',
  'Diagnostic runner now passes every required helper argument and reports all 74 suite exits without suppressing failures'],
 'scope_qualification':'Historical B11 primary failure was masked by cleanup and cannot be reconstructed; the observed deterministic teardown defects and bounded regression are fixed. No claim that every old failure was caused by slow startup.',
 'evidence_levels':{'E1':'PASS for AR-001 unit/contract guards','E2':'PASS for concrete production runtime with scripted transport, fixture Office backend, owned process and helper IPC','E3':'NOT_RUN / AWAITING_ENVIRONMENT for real Office/provider/model acceptance','E4':'NOT_RUN / AWAITING_ENVIRONMENT for real H2 UI + model + tools','E5':'DEFERRED_BY_USER — AR-083, not certified'},
 'remaining_outside_AR001':['B04-B10 reliability/Office/context/restart/Web capability debts remain owned by later AR tasks','MB-124–127 are not re-awarded','H2M-133 and native/physical acceptance stay open'],
 'environment':{'CI_configuration':'windows-latest / .NET 10.0.x','user_PC_working_tree':'NOT_ACCESSIBLE','model_cost_used':0,'new_model_endpoint_or_credential':'NONE','personal_document_mutations':'NONE'},
 'finalization_delta':'Documentation plus delivery-script only after the tested code; no full CI PASS is inferred for the self-referencing documentation commit.',
 'next_task':'AR-010','next_exact_action':'Keep this branch and PR #3. Reconcile current main/checkpoint/working tree; read AR-010 and current production/Lab context composition; implement shared runtime/context hooks with concrete Global/Project execution traces, preserving the AR-001 regression corpus. Do not create a new engine or branch.'}
Path(FILES[3]).write_text(json.dumps(acceptance,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
p=Path(FILES[1]);p.write_text(p.read_text()+f'''

## AR-001 accepted on {CODE}

Full Avalonia CI [35694774117]({full['html_url']}) passed **every required build/test/Agent/helper/publish step**. The independently executed **74/74 Agent suites** and **13/13 AR-001 cases repeated three times** passed on the same exact code in run 35694729061. Downloaded evidence archive SHA256: `5d08cd125ad6163e9bf491f36281794d0a14840284a8ff1cd8f9f8aa7fb7c66a`. Detailed machine-readable steps, artifact identities and limitations: [acceptance.json](acceptance.json).

The full-CI follow-up repairs retain strict completion rules: MB-74 independently checks active version, exact installed bytes and registry/skill contributions, and rejects a tampered payload; MB-100 proves recovery only through a host-selected candidate and preserves the original completely invented tool as a fail-closed negative. Six independent-runner invocation failures were corrected by preserving the required DesktopHost/OfficeHost arguments; they were harness errors, not native Office certification.

Portable Windows archive: 109,941,950 bytes, SHA256 `fac6f92f42fbdb4da55c1e8fb92ea40e57177e8350211ac72f356fc7dfcb640c`. It was downloaded, hash-verified and inspected for the nonempty application and both helpers plus runtime/dependency files. The full CI actually executed packaged-helper IPC smoke. NAS probe archive exists (39,380,124 bytes); this is only a package, not physical E5 acceptance.

**AR-001 = IMPLEMENTED / E2_PASS / DONE.** Native Office/model and real H2 UI acceptance remain NOT_RUN / AWAITING_ENVIRONMENT; AR-083 remains DEFERRED_BY_USER. MB-124–127 and other historical task acceptance are not re-awarded. Remaining baseline B04–B10 are not declared fixed. The exact cause of the historical masked B11 failure is not invented.

Next: **AR-010 NOT_STARTED on the same branch and PR**. The canonical SESSION HANDOFF contains the exact next action. This finalization is documentation-only relative to the tested source; any CI triggered by the documentation commit must be distinguished from the completed source run above.
''',encoding='utf-8')
p=Path(FILES[2]);history=json.loads(p.read_text());history['status']='AR-001_ACCEPTED_AFTER_REPAIR';history['final_accepted_code_sha']=CODE;history['final_acceptance']='acceptance.json';history['attempts'].extend([
 {'run_id':35693115516,'code_sha':'f023b611b0d03d55c11c257babcfa5529893f5bb','kind':'full','conclusion':'failure','failure':'MB-33 serialized mutation fixture lacked verifier; 3 passed / 1 failed','h2_tests':'603/603','later_suites_and_publish':'NOT_RUN'},
 {'run_id':35693829220,'code_sha':'efe274cf8aaea1af870aea18c81b8c180f01f4c0','kind':'full','conclusion':'failure','failure':'MB-74 mutation fixture lacked verifier','earlier_required_steps':'PASS through MB-73','later_suites_and_publish':'NOT_RUN'},
 {'run_id':35693929260,'code_sha':'74418bee6c3b12c2be6fffa68a121a4ce36aa294','kind':'all-agent-diagnostic','suites':74,'passed_suites':66,'failed_suites':8,'actual_fixture_failures':['MB-74 missing verifier','MBA-04 unrelated unknown operation stayed unresolved'],'harness_invocation_errors':6,'artifact_id':10679995582,'artifact_sha256':'9a83822aab1f870d2bafd25842787abc76ea79ce00ac6dc0022318b90859cce7'},
 {'run_id':35694729061,'code_sha':CODE,'kind':'focused-plus-all-Agent','repetitions':[{'passed':13,'failed':0} for _ in range(3)],'passed_suites':74,'failed_suites':0,'artifact_id':10680096876,'conclusion':'success'},
 {'run_id':35694774117,'code_sha':CODE,'kind':'full-including-publish-helper-smoke','conclusion':'success','artifact_ids':[10680490207,10679742893]}])
p.write_text(json.dumps(history,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
p=Path(TRACKER);text=p.read_text();m=re.search(r'```yaml\n(\{\n.*?\n\})\n```',text,re.S);require(m is not None,'Missing canonical handoff');state=json.loads(m[1]);require(state['active_task']=='AR-001' and state['implementation_status']=='ACTIVE','Another worker owns task')
state.update(phase='AR-001_ACCEPTED_NEXT_AR-010',active_task='AR-010',implementation_status='NOT_STARTED',acceptance_status='NOT_RUN',last_code_commit=CODE,last_validated_code_commit=CODE,last_runtime_source_commit=CODE,last_validation_result='FULL_REQUIRED_CI_AND_E2_PASS',checkpoint_saved_at_utc=now)
state['last_completed_task']={'id':'AR-001','implementation_status':'IMPLEMENTED','acceptance_status':'E2_PASS','code_sha':CODE,'full_ci_run':35694774117}
state['completed_this_session']=[state['last_completed_task']]
state['ci_runs'].extend([{'id':35694729061,'tested_code_sha':CODE,'result':'SUCCESS','AR001':'13/13 x3','Agent_suites':'74/74'}, {'id':35694774117,'tested_code_sha':CODE,'result':'SUCCESS','all_required_steps':'PASS','packaged_helper_IPC':'PASS','nonempty_artifacts':2}])
state['historical_failure_notes_retained']=state.get('historical_failure_notes_retained',[])+state.get('known_failures',[])
state['known_failures']=['No remaining failure observed in the AR-001 required corpus/full CI at the accepted SHA.','B04-B10 remain open source-observed debts owned by later AR tasks.','Native E3/E4 and physical E5 are not certified by these fixtures.']
state['remaining_in_active_task']=['AR-010 has not started: shared production/Lab context/runtime hooks and concrete Global/Project execution trace coverage.']
state['external_blockers']=['User-PC working tree and configured real Office/model/UI are not accessible; E3/E4 remain NOT_RUN.','AR-083 physical two-PC/NAS is DEFERRED_BY_USER, not waived or passed.']
state['evidence_locations']=list(dict.fromkeys(state.get('evidence_locations',[])+FILES[1:]))
state['working_tree']='Exact-source CI checkout and repair-delivery artifact verified clean. This finalization stages only four documentation files, and the save step verifies non-force push/remote HEAD. User-PC working tree NOT_ACCESSIBLE.'
state['uncommitted_files']=[]
state['last_test_commands']=[{'command':'dotnet restore H2Notes.Avalonia.slnx; dotnet build H2Notes.Avalonia.slnx -c Release --no-restore','run':35694774117,'code_sha':CODE,'result':'PASS'},
 {'command':'dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build','run':35694774117,'code_sha':CODE,'result':'PASS'},
 {'command':'dotnet run --project tests/H2Notes.Tests/H2Notes.Tests.csproj -c Release --no-build -- --filter AR-001','run':35694729061,'code_sha':CODE,'result':'13/13 x3'},
 {'command':'tools/agent-reliability/run_agent_suites.ps1','run':35694729061,'code_sha':CODE,'result':'74/74; helper arguments preserved'},
 {'command':'Existing .github/workflows/avalonia-ci.yml: all required suite, publish and packaged-helper smoke commands','run':35694774117,'code_sha':CODE,'result':'PASS'}]
state.pop('pending_code_change',None)
state['next_exact_action']=acceptance['next_exact_action'];state['next_task_if_active_done']='AR-011'
state['additional_ci_after_recorded_run']='Final docs/delivery-script commits may trigger further CI; no result is claimed for those unobserved runs. The accepted code and package are pinned above.'
text=text[:m.start(1)]+json.dumps(state,ensure_ascii=False,indent=2)+text[m.end(1):]
old='| AR-001 | Sửa contract drift và khôi phục full CI | 000 | E1/E2 + CI | ACTIVE |';require(text.count(old)==1,'AR-001 table changed');text=text.replace(old,old.replace('ACTIVE','DONE'),1)
old='### [~] AR-001 — Đồng bộ tên/giới hạn công cụ và full CI';require(text.count(old)==1,'AR-001 heading changed');text=text.replace(old,old.replace('[~]','[x]'),1)
anchor='### [ ] AR-010 — Chung điểm nối production context và runtime';require(text.count(anchor)==1,'AR-010 heading changed')
text=text.replace(anchor,'**AR-001 acceptance — 2026-09-22:** code `'+CODE+'`; full CI `35694774117` SUCCESS including nonempty publish/helper IPC; focused `35694729061` 13/13 x3 and 74/74 Agent suites. [Exact evidence](agent-reliability/AR-001/acceptance.json). E1/E2 only; E3/E4 NOT_RUN and AR-083 DEFERRED_BY_USER. No other AR task or old native gate is closed.\n\n'+anchor,1)
require('| E5 | DEFERRED_BY_USER |' in text,'AR-083 deferral was lost')
p.write_text(text,encoding='utf-8')
changed=set(git('diff','--name-only').splitlines())|set(git('ls-files','--others','--exclude-standard').splitlines());require(changed==set(FILES),'Unexpected finalization edits')
for path in FILES:require(Path(path).stat().st_size<512000,'Oversized checkpoint')
Path(os.environ['RUNNER_TEMP'],'ar001-outputs.txt').write_text('\n'.join(FILES)+'\n')
print('AR-001 exact-source full CI/publish and E2 accepted. Four documentation files ready to save; AR-010 NOT_STARTED.')

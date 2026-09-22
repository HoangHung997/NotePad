#!/usr/bin/env python3
"""Correct the observed test-only init-property compile error; preserve all CI evidence.
The provider contract remains unchanged. Only AR-001 owns this checkpoint.
"""
from pathlib import Path
import datetime
import json
import os
import re
import subprocess
BASE='82af106655358fa4f27d60f3da2a53e35341f1cb'
BRANCH='feature/h2-agent-reliability-ar-000'
FILES=['experiments/H2AgentLab/Phase11/V2Phase11Tests.cs',
       'docs/agent-reliability/AR-001/implementation.md',
       'docs/H2_AGENT_RELIABILITY_IMPLEMENTATION_TASKS.md',
       'docs/agent-reliability/AR-001/ci-attempts.json']
def git(*args): return subprocess.check_output(['git',*args],text=True).strip()
def require(ok,message):
    if not ok: raise RuntimeError(message)
require(os.environ['AR_BRANCH']==BRANCH and git('rev-parse','HEAD')==os.environ['AR_HEAD'],'Wrong branch/head')
require(not git('status','--porcelain'),'Dirty checkout; preserve work')
if 'AR-001 feed callback is bound at construction' in Path(FILES[0]).read_text():
    print('Init-only observer correction already applied; no replay.')
    raise SystemExit(0)
for path in FILES[:3]:
    require(git('rev-parse','HEAD:'+path)==git('rev-parse',BASE+':'+path),'Source changed: '+path)
require(not Path(FILES[3]).exists(),'CI attempts file already exists; reconcile')
p=Path(FILES[0]);text=p.read_text();start=text.index('await Test("1108 ');end=text.index('await Test("1109 ',start);part=text[start:end]
old='            await using var host = new WebResearchHost(new FixtureWebBackend());'
require(part.count(old)==1,'Changed host construction')
part=part.replace(old,'''            // AR-001 feed callback is bound at construction, as the provider contract requires.
            WebFeedPage? observedFeed = null;
            await using var host = new WebResearchHost(new FixtureWebBackend())
            { FeedObserved = page => observedFeed = page };''',1)
old='            WebFeedPage? observedFeed = null;\n            host.FeedObserved = page => observedFeed = page;\n'
require(part.count(old)==1,'Changed observer assignment')
part=part.replace(old,'',1)
p.write_text(text[:start]+part+text[end:],encoding='utf-8')
attempts={'task':'AR-001','status':'REPAIR_REQUIRED','recorded_at_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
 'attempts':[
  {'run_id':35691748431,'code_sha':'88cc6241d9488e26e0652751a7c14c26df4aecb3','kind':'focused','passed':9,'failed':4,'artifact_id':10679446309,'artifact_sha256':'a96c330edf9746d1a36f9f3cd662f973c058ba2a7dc1ac263c05e307d1ffa2bd','issues':['UI-context-dependent executor/provider disposal','Test parsed evidence footer as raw JSON']},
  {'run_id':35692162013,'code_sha':'c26b3521ce4b7d60e69f3942dda123c23fd53cf3','kind':'focused','repetitions':[{'passed':13,'failed':0}]*3,'artifact_id':10679422208,'artifact_sha256':'b26040954154ed315355978edcad3e09e8267c85be2855a7cb863f8735cf79f8'},
  {'run_id':35692347728,'job_id':106631823690,'code_sha':'c26b3521ce4b7d60e69f3942dda123c23fd53cf3','kind':'full','conclusion':'failure','h2_tests':{'passed':603,'failed':0},'architecture':{'passed':35,'failed':0},'phase11':{'passed':14,'failed':1},'failure':'1108 exact WebResearchHost tool set omitted existing web.read_feed','later_suites_and_publish':'NOT_RUN'},
  {'run_id':35692802913,'code_sha':BASE,'kind':'focused-build','conclusion':'failure','error':'CS8852 at V2Phase11Tests.cs:251: FeedObserved is init-only; test assigned it after construction','tests':'NOT_RUN','artifact_id':10678948191,'artifact_sha256':'e5264e73d4f255e9afa06e3e04b0e5e88d24ba7e862b22b827ad785ec07c7b74'}],
 'evidence_level':'E1/E2 fixtures and disposable process only; E3/E4 Office-model NOT_RUN; AR-083 DEFERRED_BY_USER',
 'next_action':'Validate the init-only test correction with repeated focused corpus and all mandatory full CI/publish smoke on its actual new code SHA.'}
Path(FILES[3]).write_text(json.dumps(attempts,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
p=Path(FILES[1]);p.write_text(p.read_text()+'''

## Test-only compile correction

Run 35692802913 on 82af106655358fa4f27d60f3da2a53e35341f1cb failed compilation (CS8852) because the added Phase-11 test assigned the init-only FeedObserved property after construction. The fixture now binds that callback in an object initializer, preserving the provider contract and all feed assertions. The failed build is not counted as a test pass. Detailed failed/passing attempts are retained in ci-attempts.json. Full CI still requires a clean run on the corrected source.
''',encoding='utf-8')
p=Path(FILES[2]);text=p.read_text();m=re.search(r'```yaml\n(\{\n.*?\n\})\n```',text,re.S);require(m is not None,'Checkpoint missing');state=json.loads(m[1]);require(state['active_task']=='AR-001' and state['implementation_status']=='ACTIVE','Another task owns checkpoint')
state['last_code_commit']=BASE
state['last_validated_code_commit']=BASE
state['last_validation_result']='BUILD_FAILED_CS8852_TEST_FIX_PENDING_VALIDATION'
state['checkpoint_saved_at_utc']=attempts['recorded_at_utc']
state['ci_runs'].extend(attempts['attempts'])
state['pending_code_change']='Init-only observer test correction in this checkpoint commit; exact new SHA is recorded by the CI checkout, not inferred from the previous tested SHA.'
state['evidence_locations']=list(dict.fromkeys(state['evidence_locations']+[FILES[1],FILES[3]]))
state['remaining_in_active_task']=['Run repeated AR-001 corpus and all full CI suites/publish after the test-only init-property fix; do not close from the earlier c26 13/13 result.']
state['next_exact_action']='Check actual newest branch SHA and its dispatched full Avalonia CI plus focused AR-001 logs. Repair remaining failures on AR-001, preserve exact-set/safety assertions, require nonempty publish and helper IPC smoke. AR-010 is not started.'
p.write_text(text[:m.start(1)]+json.dumps(state,ensure_ascii=False,indent=2)+text[m.end(1):],encoding='utf-8')
changed=set(git('diff','--name-only').splitlines())|set(git('ls-files','--others','--exclude-standard').splitlines())
require(changed==set(FILES),'Unexpected changed files')
Path(os.environ['RUNNER_TEMP'],'ar001-outputs.txt').write_text('\n'.join(FILES)+'\n')
print('Corrected init-only test and saved actual CI attempts/checkpoint; acceptance pending.')

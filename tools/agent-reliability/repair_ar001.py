#!/usr/bin/env python3
"""AR-001 repair for the actual MB-33 failure. The host completion gate is unchanged."""
from pathlib import Path
import datetime
import json
import os
import re
import subprocess
BASE='f023b611b0d03d55c11c257babcfa5529893f5bb'
BRANCH='feature/h2-agent-reliability-ar-000'
FILES=['experiments/H2AgentLab/Runtime/MbSchedulerRuntimeTests.cs',
       'docs/agent-reliability/AR-001/implementation.md',
       'docs/H2_AGENT_RELIABILITY_IMPLEMENTATION_TASKS.md',
       'tools/agent-reliability/run_agent_suites.ps1']
def git(*args): return subprocess.check_output(['git',*args],text=True).strip()
def require(ok,message):
    if not ok: raise RuntimeError(message)
def replace(path,old,new):
    p=Path(path);text=p.read_text();require(text.count(old)==1,'Changed anchor: '+path+' / '+old[:60]);p.write_text(text.replace(old,new,1),encoding='utf-8')
require(os.environ['AR_BRANCH']==BRANCH and git('rev-parse','HEAD')==os.environ['AR_HEAD'],'Wrong branch/head')
require(not git('status','--porcelain'),'Dirty checkout')
if 'class SharedWriteReadbackVerifier' in Path(FILES[0]).read_text():
    print('MB-33 state verification already applied; no replay.')
    raise SystemExit(0)
for path in FILES[:3]:
    require(git('rev-parse','HEAD:'+path)==git('rev-parse',BASE+':'+path),'Changed source needs reconciliation: '+path)
require(not Path(FILES[3]).exists(),'Diagnostic runner already exists; reconcile')
p=FILES[0]
replace(p,'using H2AgentLab.Transport;','using H2AgentLab.Transport;\nusing H2AgentLab.Verification;')
replace(p,'            var executions = 0;\n\n            async ValueTask<string> Write','''            var executions = 0;
            var stateFile = Path.Combine(root, "shared-mutation.txt");
            var verifier = new SharedWriteReadbackVerifier(stateFile);
            Check(!verifier.Observe().Passed, "Absent side effects were reported verified.");

            async ValueTask<string> Write''')
replace(p,'                    await Task.Delay(100, ct);\n                    return JsonSerializer.Serialize(new { ok = true, id = call.Id });','''                    await Task.Delay(100, ct);
                    await File.AppendAllTextAsync(stateFile, call.Id + "\\n", ct);
                    return JsonSerializer.Serialize(new { ok = true, id = call.Id });''')
replace(p,'''                new SameResourceMutationTransport(),
                new AgentContextManager(),
                registry);''','''                new SameResourceMutationTransport(),
                new AgentContextManager(),
                registry,
                verifier: verifier);''')
replace(p,'Request("Write shared fixture twice.", "fixture:shared"),','Request("Write shared fixture twice.", "fixture:shared", requireReadback: true),')
replace(p,'''            Check(maxActive == 1,
                $"Same-resource mutations overlapped; max concurrency was {maxActive}.");''','''            Check(maxActive == 1,
                $"Same-resource mutations overlapped; max concurrency was {maxActive}.");
            Check(verifier.Observations == 1 && result.VerificationHistory.Single().Passed,
                "Serialized writes did not receive an independent readback report.");
            Check(File.ReadAllLines(stateFile).SequenceEqual(new[] { "write-1", "write-2" }),
                "Serialized side effects were missing, duplicated or reordered.");
            await File.WriteAllTextAsync(stateFile, "write-1\\n");
            Check(!verifier.Observe().Passed, "Missing second write was falsely verified.");''')
replace(p,'private static AgentRuntimeRequest Request(string userInput, string scope)','private static AgentRuntimeRequest Request(string userInput, string scope, bool requireReadback = false)')
replace(p,'''            [],
            AgentTaskRiskClass.Medium,
            new AgentVerificationPolicy(requireVerification: false));''','''            requireReadback ? [new AgentAcceptanceCriterion("mb33.shared-readback", "Both ordered fixture writes are present.")] : [],
            AgentTaskRiskClass.Medium,
            new AgentVerificationPolicy(requireVerification: requireReadback,
                requiredVerifierIds: requireReadback ? ["mb33-shared-readback"] : null));''')
replace(p,'    private sealed class ParallelReadTransport : IAgentTransport','''    private sealed class SharedWriteReadbackVerifier(string path) : IAgentRuntimeVerifier
    {
        public int Observations { get; private set; }
        public VerificationReport Observe()
        {
            var matches = File.Exists(path)
                && File.ReadAllLines(path).SequenceEqual(new[] { "write-1", "write-2" });
            const string criterion = "mb33.shared-readback";
            return new("mb33-shared-readback", [new VerificationCriterionResult(criterion,
                matches ? VerificationCriterionStatus.Passed : VerificationCriterionStatus.Failed,
                ["fixture:shared-mutation.txt"], matches ? null : new VerificationFailure(criterion,
                    "Readback does not contain exactly both ordered writes.", ["fixture:shared-mutation.txt"]))]);
        }
        public Task<VerificationReport?> VerifyAsync(AgentRuntimeVerificationContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.Calls.Any(call => call.Name == "fixture.write_shared"))
                return Task.FromResult<VerificationReport?>(null);
            Observations++;
            return Task.FromResult<VerificationReport?>(Observe());
        }
    }

    private sealed class ParallelReadTransport : IAgentTransport''')
Path(FILES[3]).write_text('''param([string]$OutputDirectory = "artifacts/ar001/all-agent-suites")
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$workflow = Get-Content .github/workflows/avalonia-ci.yml -Raw
# Reuse each actual Agent suite command from the existing required workflow.
$matches = [regex]::Matches($workflow, 'dotnet run --project \\.\\\\experiments\\\\H2AgentLab\\\\H2AgentLab.csproj -c Release --no-build -- (?<flag>--[a-z0-9-]+) \\$out')
$flags = @($matches | ForEach-Object { $_.Groups['flag'].Value } | Select-Object -Unique)
if ($flags.Count -lt 60 -or $flags -notcontains '--mb-scheduler-runtime-test' -or $flags -notcontains '--v2-provider-resilience-test') { throw 'Incomplete workflow suite inventory.' }
$results = @()
foreach ($flag in $flags) {
    $name = $flag.TrimStart('-')
    $directory = Join-Path $OutputDirectory $name
    $log = Join-Path $OutputDirectory ($name + '.log')
    Write-Host "Running independent required suite $flag"
    & dotnet run --project experiments/H2AgentLab/H2AgentLab.csproj -c Release --no-build -- $flag $directory 2>&1 | Tee-Object -FilePath $log
    $exit = $LASTEXITCODE
    $results += @{flag=$flag; exit_code=$exit; log=$log}
}
@{code_sha=(git rev-parse HEAD); evidence='E1/E2 fixtures only'; suites=$results} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputDirectory 'suite-results.json')
$failures = @($results | Where-Object {$_.exit_code -ne 0})
if ($failures.Count -gt 0) { throw ('Required suites failed: ' + (($failures | ForEach-Object {$_.flag}) -join ', ')) }
Write-Host "All $($flags.Count) independently invoked required Agent suites passed. This does not replace full CI/publish."
''',encoding='utf-8')
p=Path(FILES[1]);p.write_text(p.read_text()+'''

## MB-33 completion-contract drift exposed by full CI

Full run 35693115516 / job 106634126497 on f023b611b0d03d55c11c257babcfa5529893f5bb passed H2 603/603, architecture 35/35, Phase-11 15/15 and all suites through MB-32, then MB-33 was 3 passed / 1 failed: the serialized-mutation fixture had no verifier report. The completion gate correctly refused it. The fixture now writes both call IDs to its own temporary file, independently reads exact ordered effects, requires the readback verifier in the task contract, checks max concurrency stays one, and rejects missing/partial side effects. No runtime completion rule is weakened. An independent diagnostic runner reuses every Agent suite flag from the existing full workflow and aggregates failures without hiding later failures behind an early guard; its final exit is nonzero when any required suite fails. Full CI/publish still remain required. Latest focused AR-001 on f023 passed 13/13 in each of three repetitions (artifact 10679343679, SHA256 60abc903fd33a6354646770a2826f61adc63592d0e18a3edbd6e9a482f2a4407).
''',encoding='utf-8')
p=Path(FILES[2]);text=p.read_text();m=re.search(r'```yaml\n(\{\n.*?\n\})\n```',text,re.S);require(m is not None,'Missing checkpoint');state=json.loads(m[1]);require(state['active_task']=='AR-001','Other task active')
state['last_code_commit']=BASE;state['last_validated_code_commit']=BASE
state['last_validation_result']='FULL_CI_FAILED_MB33_FIXTURE_MISSING_VERIFIER'
state['checkpoint_saved_at_utc']=datetime.datetime.now(datetime.timezone.utc).isoformat()
state['ci_runs'].append({'id':35693115516,'code_sha':BASE,'result':'FAILURE','h2_tests':'603/603','architecture':'35/35','phase11':'15/15','mb33':'3 passed / 1 failed: fixture missing verifier','publish':'NOT_RUN'})
state['remaining_in_active_task']=['Validate MB-33 fixture readback correction; inspect all remaining Agent suites and full CI/publish on corrected source.']
state['next_exact_action']='Run exact-head required full CI and independent Agent suite diagnostic runner; fix remaining AR-001 baseline contract drift without weakening safety. Do not start AR-010.'
p.write_text(text[:m.start(1)]+json.dumps(state,ensure_ascii=False,indent=2)+text[m.end(1):],encoding='utf-8')
changed=set(git('diff','--name-only').splitlines())|set(git('ls-files','--others','--exclude-standard').splitlines())
require(changed==set(FILES),'Unexpected changed files')
Path(os.environ['RUNNER_TEMP'],'ar001-outputs.txt').write_text('\n'.join(FILES)+'\n')
print('MB-33 fixture readback repair applied; all required acceptance remains pending.')

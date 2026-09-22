#!/usr/bin/env python3
"""Apply the first observed AR-001 CI repair to exact known source blobs only."""
from pathlib import Path
import os
import subprocess

BASE = '88cc6241d9488e26e0652751a7c14c26df4aecb3'
BRANCH = 'feature/h2-agent-reliability-ar-000'
FILES = ['experiments/H2AgentLab/Integration/H2ProductionAgentAdapter.cs',
         'experiments/H2AgentLab/Tools/ToolExecutionScheduler.cs',
         'tests/H2Notes.Tests/H2AgentReliabilityContractTests.cs',
         'docs/agent-reliability/AR-001/implementation.md']
def git(*args): return subprocess.check_output(['git', *args], text=True).strip()
def require(value, text):
    if not value: raise RuntimeError(text)
def replace(path, old, new):
    file=Path(path); text=file.read_text(encoding='utf-8')
    require(text.count(old)==1, 'Source anchor changed: '+path)
    file.write_text(text.replace(old,new,1),encoding='utf-8')
require(os.environ['AR_BRANCH']==BRANCH and git('rev-parse','HEAD')==os.environ['AR_HEAD'],'Wrong branch/head')
require(not git('status','--porcelain'),'Dirty checkout; preserve unrelated work')
if 'runtimeDisposal = runtime.ConfigureAwait(false)' in Path(FILES[0]).read_text():
    print('Observed AR-001 context repair already applied; no replay.')
    raise SystemExit(0)
for path in FILES:
    require(git('rev-parse','HEAD:'+path)==git('rev-parse',BASE+':'+path),'Changed file requires reconciliation: '+path)
replace(FILES[0], '            await using var runtime = orchestrator.CreateRuntime(', '            var runtime = orchestrator.CreateRuntime(')
replace(FILES[0], '''                telemetry);

            var result = await orchestrator.RunRuntimeAsync(''', '''                telemetry);
            // Provider teardown is engine work, not a continuation on the caller's UI loop.
            await using var runtimeDisposal = runtime.ConfigureAwait(false);

            var result = await orchestrator.RunRuntimeAsync(''')
replace(FILES[1], '        await Task.WhenAll(tasks);','        await Task.WhenAll(tasks).ConfigureAwait(false);')
replace(FILES[1], '                await _serialGate.WaitAsync(cancellationToken);', '                await _serialGate.WaitAsync(cancellationToken).ConfigureAwait(false);')
replace(FILES[1], '                await resourceGate.WaitAsync(cancellationToken);', '                await resourceGate.WaitAsync(cancellationToken).ConfigureAwait(false);')
replace(FILES[1], '''                request.Call,
                cancellationToken);
            results[index]''', '''                request.Call,
                cancellationToken).ConfigureAwait(false);
            results[index]''')
replace(FILES[2], '                    using var result = JsonDocument.Parse(output.Content);', '''                    // Runtime projects a JSON body plus an evidence footer; verify both,
                    // rather than pretending the complete model projection is raw JSON.
                    var marker = output.Content.IndexOf("\\n[evidence:", StringComparison.Ordinal);
                    Check(marker > 0, "Rejected mutating call lost its evidence footer.");
                    using var result = JsonDocument.Parse(output.Content[..marker]);''')
report=Path(FILES[3])
report.write_text(report.read_text()+'''

## First actual CI repair — not yet accepted

Focused run 35691748431 tested source 88cc6241d9488e26e0652751a7c14c26df4aecb3: build succeeds; 9 RC-02 cases pass and 4 fail. The two oversized production-runtime cases already reject correctly but their tests incorrectly parse the evidence-footer projection as raw JSON; the assertions now explicitly require the footer and parse the preceding JSON body. The forced-timeout and held-transport-disposal tests expose UI synchronization-context capture in ToolExecutionScheduler awaits and implicit await-using disposal. Engine continuations now use ConfigureAwait(false), preserving gate/cancellation semantics while no longer depending on the caller pumping UI during shutdown. Both deterministic regression cases remain mandatory; no pass is claimed until rerun. Original primary errors and failed-run evidence remain preserved. This does not prove the exact cause of the earlier AR-000 sporadic cleanup failure.
''',encoding='utf-8')
require(set(git('diff','--name-only').splitlines())==set(FILES),'Unexpected changed files')
Path(os.environ['RUNNER_TEMP'],'ar001-outputs.txt').write_text('\n'.join(FILES)+'\n')
print('Applied observed AR-001 repair to four files; awaiting tests.')

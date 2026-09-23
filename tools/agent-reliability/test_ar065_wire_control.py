"""AR-065 old-transport negative control in an isolated CI checkout; never reset git history."""
import hashlib, json, os, pathlib, re, subprocess
ROOT = pathlib.Path.cwd()
OUT = ROOT / 'artifacts/ar065'; OUT.mkdir(parents=True, exist_ok=True)
BASE = 'aaf06a6b1b9e3981a699672ba141a5a875aa40c6'
PATHS = [f'experiments/H2AgentLab/Transport/{name}.cs' for name in ('OpenAiResponsesTransport', 'OpenAiResponsesWebSocketTransport')]
def run(args, log):
    p = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    (OUT/log).write_bytes(p.stdout)
    return p.returncode, p.stdout.decode('utf-8', errors='replace')
if subprocess.check_output(['git','status','--porcelain']).strip():
    raise SystemExit('Control requires clean source checkout; refusing to overwrite changes.')
original = {p: (ROOT/p).read_bytes() for p in PATHS}
record = {'kind':'negative control', 'source_sha': subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(), 'old_transport_sha': BASE}
error = None
try:
    for p in PATHS: (ROOT/p).write_bytes(subprocess.check_output(['git','show',BASE+':'+p]))
    status, text = run(['dotnet','build','H2Notes.Avalonia.slnx','-c','Release','--no-restore'], 'control-build.log')
    if status: raise RuntimeError('Old transport did not compile; not a semantic reproduction.')
    status, text = run(['dotnet','run','--project','tests/H2Notes.Tests/H2Notes.Tests.csproj','-c','Release','--no-build','--','--filter','AR-065 WIRE CONTROL'], 'control.log')
    matches = re.findall(r'RESULT: (\d+) passed, (\d+) failed', text)
    record.update(exit_code=status, results=matches)
    if status == 0 or matches != [('0','4')]: raise RuntimeError('Expected exactly four legacy wire failures.')
    if text.count('Legacy dotted wire name reproduced.') != 2 or text.count('Legacy implicit strict normalization reproduced.') != 2:
        raise RuntimeError('Control failed for an unexpected reason.')
except Exception as ex:
    error = ex
finally:
    for p, data in original.items(): (ROOT/p).write_bytes(data)
    record['restored_sha256'] = {p:hashlib.sha256((ROOT/p).read_bytes()).hexdigest() for p in PATHS}
    record['exact_restoration'] = all((ROOT/p).read_bytes() == data for p,data in original.items())
    record['clean_restoration'] = not subprocess.check_output(['git','status','--porcelain']).strip()
    status, _ = run(['dotnet','build','H2Notes.Avalonia.slnx','-c','Release','--no-restore'], 'restored-build.log')
    record['restored_build_exit'] = status
    (OUT/'control.json').write_text(json.dumps(record,indent=2),encoding='utf-8')
    if not record['exact_restoration'] or not record['clean_restoration'] or status:
        raise RuntimeError('Current-source restoration/build failed.')
if error: raise error
print('Old source: four expected semantic failures; exact current source restored and rebuilt.')

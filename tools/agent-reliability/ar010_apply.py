#!/usr/bin/env python3
"""Deliver the reviewed AR-010 factory ownership repair; no resets or unbounded retries."""
from pathlib import Path
import hashlib
import json
import os
import subprocess

BRANCH = 'feature/h2-agent-reliability-ar-000'
MAIN = '1283bc13e07c3cd47d04886166de3dfc595422c0'
FILES = {
 'experiments/H2AgentLab/Runtime/AgentRuntimeFactory.cs': ('6fd06e975dd6a1aaa29d3870f26f4b9d744071b6b820061b25dc34580ab331f5', '0f81a87f10efdd4429f911d8a43c93a6414d153f262aa9be7e9188e99dd54b03'),
 'tests/H2Notes.Tests/H2AgentRuntimeHookTests.cs': ('71296a63da0eb5afa05e9ffef298f5f7aad2e97f82c63022389fe390347c405d', 'f772d2e0daca5ab4484a0a8145fa7a840766334e81fa21d6bf8c016ab472fb99'),
 'docs/agent-reliability/AR-010/implementation.md': ('3de99f812b0e7faa4da558f28e8f18853e2c8b56a038730e703af905b451fbf8', 'fecdac8f129c10a634efbbacde1e295fa046e9ab1d04430effec35272ba1ad44')
}
def git(*args): return subprocess.check_output(['git', *args], text=True).strip()
def require(value, message):
    if not value: raise RuntimeError(message)
def digest(path): return hashlib.sha256(Path(path).read_bytes()).hexdigest()
def replace(path, old, new):
    file = Path(path); text = file.read_text()
    require(text.count(old) == 1, 'Source anchor drifted: ' + path)
    file.write_text(text.replace(old, new, 1))

def main():
    head = git('rev-parse', 'HEAD')
    require(os.environ['AR_BRANCH'] == BRANCH and os.environ['AR_HEAD'] == head, 'Wrong branch/checkout')
    require(not git('status', '--porcelain'), 'Dirty checkout: preserve work')
    require(git('ls-remote', 'origin', 'refs/heads/main').split()[0] == MAIN, 'Main advanced: reconcile')
    if all(digest(path) == pair[1] for path, pair in FILES.items()):
        with open(os.environ['GITHUB_OUTPUT'], 'a') as out: out.write('changed=false\ncode_sha=' + head + '\n')
        print('Exact factory repair already present; no replay.')
        return
    require(all(digest(path) == pair[0] for path, pair in FILES.items()), 'Source changed: refusing overwrite')
    require(git('ls-remote', 'origin', 'refs/heads/' + BRANCH).split()[0] == head, 'Branch advanced')
    factory = 'experiments/H2AgentLab/Runtime/AgentRuntimeFactory.cs'
    replace(factory, '        var registry = NormalRuntimeToolRegistry.Create(tools);', '        // Validate the host hook factory before allocating a transport or provider resources.\n        var hooks = _hooksFactory(telemetry) ?? throw new InvalidOperationException("Runtime hook factory returned null.");\n        var registry = NormalRuntimeToolRegistry.Create(tools);')
    replace(factory, '            hooks: _hooksFactory(telemetry) ?? throw new InvalidOperationException("Runtime hook factory returned null."));', '            hooks: hooks);')
    test = 'tests/H2Notes.Tests/H2AgentRuntimeHookTests.cs'
    anchor = '        foreach (var kind in Enum.GetValues<AgentRuntimeHookKind>())'
    inserted = '''        foreach (var returnNull in new[] { false, true })
        {
            var nullHook = returnNull;
            test("AR-010 invalid hook factory allocates no transport: " + (nullHook ? "null" : "exception"), () => InWorkspace(root =>
            {
                var traces = new ConcurrentQueue<AgentRuntimeHookEvent>();
                var transport = new ScriptFactory([], traces);
                var factory = new AgentRuntimeFactory(transport, _ => nullHook ? null! : throw new InvalidOperationException("hook-factory-failure"));
                using var tools = new AgentTools(new SafeWorkspace(root), Path.Combine(root, "lab-state"),
                    (_, _) => Task.FromResult(false), (_, _) => { }) { ReadOnly = true };
                try
                {
                    _ = factory.Create(Profile(), "", tools, new H2AgentLab.Context.AgentContextManager(), new());
                    throw new Exception("Invalid hook factory was accepted.");
                }
                catch (InvalidOperationException error)
                {
                    Check(error.Message.Contains(nullHook ? "returned null" : "hook-factory-failure"), "The original hook factory error was hidden.");
                }
                Check(transport.Sessions.Count == 0, "Hook factory failure leaked a newly allocated transport.");
            }));
        }

'''
    replace(test, anchor, inserted + anchor)
    doc = Path('docs/agent-reliability/AR-010/implementation.md')
    doc.write_text(doc.read_text() + '''
## Review repair before acceptance

The initial exact-source build and three focused iterations passed. Further code review found that a throwing/null host-hook factory could leave a newly constructed transport without an owner. Hook construction/validation now precedes registry/provider/transport allocation. Two registered regressions require the original hook error and zero transport allocations for exception/null cases. This change needs its own exact-source test results; no earlier PASS is transferred to it.
''')
    require(all(digest(path) == pair[1] for path, pair in FILES.items()), 'Repair output differs from reviewed source')
    require(set(git('diff', '--name-only').splitlines()) == set(FILES), 'Unexpected files changed')
    evidence = Path(os.environ['RUNNER_TEMP']) / 'ar010-delivery'; evidence.mkdir(exist_ok=True)
    (evidence / 'factory-repair.patch').write_bytes(subprocess.check_output(['git', 'diff', '--binary']))
    subprocess.run(['git', 'add', '--', *FILES], check=True)
    subprocess.run(['git', 'diff', '--cached', '--check'], check=True)
    require(git('ls-remote', 'origin', 'refs/heads/' + BRANCH).split()[0] == head, 'Concurrent branch update')
    git('config', 'user.name', 'github-actions[bot]')
    git('config', 'user.email', '41898282+github-actions[bot]@users.noreply.github.com')
    subprocess.run(['git', 'commit', '-m', 'fix(AR-010): reject invalid hooks before transport allocation'], check=True)
    subprocess.run(['git', 'push', 'origin', 'HEAD:refs/heads/' + BRANCH], check=True)
    saved = git('rev-parse', 'HEAD')
    require(not git('status', '--porcelain'), 'Dirty delivery after save')
    require(git('ls-remote', 'origin', 'refs/heads/' + BRANCH).split()[0] == saved, 'Remote checkpoint not verified')
    (evidence / 'identity.json').write_text(json.dumps({'code_sha': saved, 'working_tree': 'CLEAN', 'files': list(FILES), 'acceptance': 'NOT_RUN'}, indent=2))
    with open(os.environ['GITHUB_OUTPUT'], 'a') as out: out.write('changed=true\ncode_sha=' + saved + '\n')
    print('Factory repair pushed:', saved, '; exact-source validation required.')

if __name__ == '__main__': main()

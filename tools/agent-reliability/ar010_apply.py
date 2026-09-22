#!/usr/bin/env python3
"""One-time delivery of the reviewed AR-010 text patch, not an application subsystem.
Never reset or overwrite drifted source. Runtime acceptance is a separate Windows job.
"""
import base64
import gzip
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess

BRANCH = 'feature/h2-agent-reliability-ar-000'
MAIN = '1283bc13e07c3cd47d04886166de3dfc595422c0'
PATCH_HASH = 'ad7b67bb05aacaf467bff37689e38b033d80c0b42361f89fb95b4870049f2ed3'
FILES = {
 'docs/H2_AGENT_RELIABILITY_IMPLEMENTATION_TASKS.md': ('fa32ea3cc2cfba5e10d824a365fcde477c1519fd69cd93ee0badce54bc002816', '88a4f2ff35f07f41d5669d8f4bb814cb0dc67c7edcd29602dd19503b176b527d'),
 'docs/agent-reliability/AR-010/implementation.md': (None, '3de99f812b0e7faa4da558f28e8f18853e2c8b56a038730e703af905b451fbf8'),
 'experiments/H2AgentLab/Integration/H2ProductionAgentAdapter.cs': ('91a6d1f3ed5b26d83d12e84954f71a358e751fef3b920aaa159f618a81bb8e2d', '70360921098202f8c245d35667710e8dfb0b6e3e8658782a291831156667ff7d'),
 'experiments/H2AgentLab/Metrics/AgentTrace.cs': ('e00cf25b0cbf718dee7b251949e45c49bf1440536b32fbde7895e10bab331044', 'd758ce49555fa2f92f03e1cb8c69c12aa443ba7776c518f9ea9521803809b6ab'),
 'experiments/H2AgentLab/Runtime/AgentRuntime.cs': ('d11e91c6e5be7314958365b26d26f71c787246679a6cfcff46e3bacc12d7508e', '189e69787e70ea2a42d8319c3ca402de57163042119e6703902578d0edd56ff8'),
 'experiments/H2AgentLab/Runtime/AgentRuntimeFactory.cs': ('858428a1764ce7073052431da6d030da55d3fbed29bc763256fd233a1381a6fd', '6fd06e975dd6a1aaa29d3870f26f4b9d744071b6b820061b25dc34580ab331f5'),
 'experiments/H2AgentLab/Runtime/AgentRuntimeHooks.cs': (None, 'f963b48128f8e2774e06b46ea590bfdea391276e80a93d01568d37446be4a6e0'),
 'experiments/H2AgentLab/Tasking/AgentOrchestratedRun.cs': ('a9d8de8616de153e9e9f3ffe200bf77f466da73171fa529554dbd35b9169e1e8', '4f43b9ae6f172dfcc75e6bbd198fa6f1ed1132d6dd2e1d382aed97a3f1954a8b'),
 'tests/H2Notes.Tests/H2AgentRuntimeHookTests.cs': (None, '71296a63da0eb5afa05e9ffef298f5f7aad2e97f82c63022389fe390347c405d'),
 'tests/H2Notes.Tests/Program.cs': ('a159064b8836a7d4e44ff86f391a2b9243fd866ff2c2fae25b5d57407f4a84c2', '276099415d1e9998f14130e5c149f96f656f48ac87dee96411eb4785516f2f89')
}

def git(*args):
    return subprocess.check_output(['git', *args], text=True).strip()

def require(condition, message):
    if not condition:
        raise RuntimeError(message)

def digest(name):
    path = Path(name)
    require(not any(p.is_symlink() for p in (path, *path.parents)), 'Symlink in source path: ' + name)
    return hashlib.sha256(path.read_bytes()).hexdigest() if path.exists() else None

def main():
    require(os.environ['AR_BRANCH'] == BRANCH, 'Wrong implementation branch')
    head = git('rev-parse', 'HEAD')
    require(head == os.environ['AR_HEAD'], 'Unexpected checkout SHA')
    require(not git('status', '--porcelain'), 'Dirty checkout: preserve uncommitted work')
    require(git('ls-remote', 'origin', 'refs/heads/main').split()[0] == MAIN, 'Main advanced: reconcile first')
    current = {name: digest(name) for name in FILES}
    if all(current[name] == pair[1] for name, pair in FILES.items()):
        print('Exact AR-010 implementation already present; do not replay delivery.')
        with open(os.environ['GITHUB_OUTPUT'], 'a') as out:
            out.write('changed=false\ncode_sha=' + head + '\n')
        return
    require(git('ls-remote', 'origin', 'refs/heads/' + BRANCH).split()[0] == head, 'Branch advanced: reconcile first')
    require(all(current[name] == pair[0] for name, pair in FILES.items()), 'Source/checkpoint drift: refusing to replace work')
    encoded = ''.join(Path('tools/agent-reliability/ar010_patch.b64').read_text().split())
    require(len(encoded) == 19044, 'Text patch transport length mismatch')
    with gzip.GzipFile(fileobj=io.BytesIO(base64.b64decode(encoded, validate=True))) as archive:
        patch = archive.read(200001)
    require(len(patch) == 57943 and hashlib.sha256(patch).hexdigest() == PATCH_HASH, 'Text patch integrity mismatch')
    evidence = Path(os.environ['RUNNER_TEMP']) / 'ar010-delivery'
    evidence.mkdir(exist_ok=True)
    patch_path = evidence / 'implementation.patch'
    patch_path.write_bytes(patch)
    subprocess.run(['git', 'apply', '--check', str(patch_path)], check=True)
    subprocess.run(['git', 'apply', '--index', str(patch_path)], check=True)
    require(set(git('diff', '--cached', '--name-only').splitlines()) == set(FILES), 'Unexpected staged paths')
    require(all(digest(name) == pair[1] for name, pair in FILES.items()), 'Applied source differs from reviewed patch')
    subprocess.run(['git', 'diff', '--cached', '--check'], check=True)
    require(git('ls-remote', 'origin', 'refs/heads/' + BRANCH).split()[0] == head, 'Concurrent branch update; preserve patch instead')
    git('config', 'user.name', 'github-actions[bot]')
    git('config', 'user.email', '41898282+github-actions[bot]@users.noreply.github.com')
    subprocess.run(['git', 'commit', '-m', 'feat(AR-010): share awaited runtime hooks across Global Project and Lab'], check=True)
    subprocess.run(['git', 'push', 'origin', 'HEAD:refs/heads/' + BRANCH], check=True)
    saved = git('rev-parse', 'HEAD')
    require(not git('status', '--porcelain'), 'Delivery tree is not clean')
    require(git('ls-remote', 'origin', 'refs/heads/' + BRANCH).split()[0] == saved, 'Remote head was not verified')
    (evidence / 'identity.json').write_text(json.dumps({'source_sha': saved, 'parent': head, 'branch': BRANCH, 'working_tree': 'CLEAN', 'patch_sha256': PATCH_HASH, 'files': list(FILES), 'acceptance': 'NOT_RUN'}, indent=2))
    with open(os.environ['GITHUB_OUTPUT'], 'a') as out:
        out.write('changed=true\ncode_sha=' + saved + '\n')
    print('Saved source:', saved, 'Acceptance NOT_RUN until Windows tests finish.')

if __name__ == '__main__':
    main()

"""Read-only provenance for an executed AR-033 control; current tests still run independently.

Never replaces source or executes archived text. The six contradiction regressions and all
42 current cases must still pass at the checkout being tested. Historical failures are not
reported as a newly executed differential experiment on current code.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]
DATA = Path(__file__).with_name('retained-ar033-control')
EXPECTED_TEST_BLOB = 'f50eb5424d9da3116bc09e702fef8333eb19eea6'
EXPECTED_ARTIFACT = '2f9f36c51b61eb383bbd3100d239152f6131712edc67212a85c66896996a6574'


def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT)


def blob(data):
    return hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()


def inspect_prior(text, control):
    text = text.removeprefix("\ufeff").replace("\r\n", "\n")
    assert re.findall(r'RESULT: (\d+) passed, (\d+) failed', text) == [('36', '6')], 'Historical result missing or changed'
    passes = re.findall(r'^PASS (.+)$', text, re.M)
    failures = re.findall(r'^FAIL (.+)$', text, re.M)
    assert len(passes) == len(set(passes)) == 36 and len(failures) == 6
    assert failures == control['failures'], 'Historical failure reasons differ'
    rejected = [f.split(': System.InvalidOperationException:')[0] for f in failures]
    assert len(set(rejected)) == 6 and sum(f.startswith('AR-033 verdict consistency rejects ') for f in rejected) == 4
    assert any('False completion: FailedSummary' in f for f in failures)
    assert any('False completion: PendingSummary' in f for f in failures)
    assert set(passes).isdisjoint(rejected)
    return sorted(passes + rejected), sorted(rejected)


def inspect_current(text, inventory, rejected):
    assert re.findall(r'RESULT: (\d+) passed, (\d+) failed', text) == [('42', '0')], 'Current AR-033 count/failures differ'
    passes = [x.strip() for x in re.findall(r'^PASS (.+)$', text, re.M)]
    assert len(passes) == len(set(passes)) == 42 and sorted(passes) == inventory, 'Current regression inventory differs'
    assert not re.findall(r'^FAIL ', text, re.M) and set(rejected).issubset(passes)
    return passes


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--current-logs', type=Path)
    args = parser.parse_args()
    receipt = json.loads((DATA/'receipt.json').read_text(encoding='utf-8-sig'))
    text = json.loads((DATA/'prior-tests.json').read_text(encoding='utf-8-sig'))['utf8_log']
    accepted = json.loads((ROOT/'docs/agent-reliability/AR-033/acceptance.json').read_text(encoding='utf-8-sig'))
    assert receipt['artifact']['sha256'] == accepted['focused_artifact']['sha256'] == EXPECTED_ARTIFACT
    assert receipt['artifact']['id'] == accepted['focused_artifact']['id'] == 10725382259
    assert receipt['artifact']['bytes'] == accepted['focused_artifact']['bytes'] == 2896326
    assert receipt['code_sha'] == accepted['focused_tested_sha'] == 'f5ede2d0e24896be3bd329ea8cce34e20d7f5ee1'
    run = next(r for r in accepted['runs'] if r['id'] == receipt['run'])
    assert run['id'] == 35799370449 and run['job_id'] == receipt['job'] == 106985933885 and run['result'] == 'SUCCESS'
    assert any(s['name'] == 'Reproduce contradiction with the prior assessment and unchanged tests'
               and s['conclusion'] == 'success' for s in run['steps'])
    assert receipt['control'] == accepted['negative_control']
    assert receipt['checkout_format'] == accepted['checkout_format']
    assert receipt['restoration']['working_tree'] == 'CLEAN'
    assert receipt['restoration']['blob'] == receipt['checkout_format']['checkout_blob']
    assert hashlib.sha256(text.encode('utf-8')).hexdigest() == receipt['prior_log_sha256']
    assert receipt['tests_path'] == 'tests/H2Notes.Tests/H2AgentCompletionTests.cs'
    assert blob(git('show', 'HEAD:'+receipt['tests_path'])) == receipt['tests_blob'] == EXPECTED_TEST_BLOB, 'Regression source changed; review provenance, do not bypass'
    inventory, rejected = inspect_prior(text, receipt['control'])
    assert inventory == receipt['positive_inventory']
    assert receipt['classification'] == 'HISTORICAL_EXECUTED_CONTROL_READ_ONLY; NOT_A_NEW_NEGATIVE_CONTROL_RUN'
    # Refusal checks for malformed evidence. No file/source mutations, process or provider work.
    refused = 0
    for bad in [text.replace('36 passed, 6 failed', '42 passed, 0 failed'),
                text.replace('FAIL ', 'PASS ', 1), text + '\nRESULT: 36 passed, 6 failed',
                text.replace('False completion: FailedSummary', 'Some unrelated failure')]:
        try: inspect_prior(bad, receipt['control'])
        except AssertionError: refused += 1
        else: raise AssertionError('Malformed historical evidence was accepted')
    good = '\n'.join('PASS '+n for n in inventory)+'\nRESULT: 42 passed, 0 failed'
    inspect_current(good, inventory, rejected)
    for bad in [good.replace('42 passed, 0 failed','41 passed, 1 failed'),
                good.replace('PASS '+rejected[0], 'PASS unrelated fixture', 1),
                good+'\nPASS '+inventory[0], good+'\nFAIL stale control']:
        try: inspect_current(bad, inventory, rejected)
        except AssertionError: refused += 1
        else: raise AssertionError('Malformed current evidence was accepted')
    current = []
    if args.current_logs:
        for number in range(1,4):
            log = args.current_logs/f'AR-033-{number}.log'
            raw = log.read_bytes()
            inspect_current(raw.decode('utf-8-sig'), inventory, rejected)
            current.append({'log':log.name,'sha256':hashlib.sha256(raw).hexdigest(),'passed':42,'failed':0})
    report = {'classification':receipt['classification'], 'historical_run':receipt['run'],
              'historical_code_sha':receipt['code_sha'], 'historical_artifact':receipt['artifact'],
              'historical_expected_failures':rejected, 'historical_prior_log_sha256':receipt['prior_log_sha256'],
              'current_sha':git('rev-parse','HEAD').decode().strip(), 'unchanged_test_blob':EXPECTED_TEST_BLOB,
              'source_substitution_performed':False, 'new_negative_control_executed':False,
              'evidence_refusal_checks':refused, 'current_repeated_regression':current,
              'current_status':'CURRENT42_PASS_X3' if current else 'AWAITING_CURRENT_TEST_EXECUTION'}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    print('AR033 retained control verified read-only; refusal checks='+str(refused)+'; '+report['current_status'])


if __name__ == '__main__':
    main()

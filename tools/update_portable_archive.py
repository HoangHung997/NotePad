"""Update a generated portable ZIP without another multi-GB copy; preserve rollback footer.

Unchanged compressed payloads remain byte-for-byte. Superseded local records are not in
the new central directory, so extraction has one unambiguous entry per manifest path.
The original directory/footer is backed up before any write and restored on failure.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import time
import zipfile


def update(archive, root, paths_file):
    paths = json.loads(paths_file.read_text(encoding='utf-8-sig'))
    paths = sorted(set(paths))
    original_size = archive.stat().st_size
    with zipfile.ZipFile(archive) as old:
        names = old.namelist()
        if len(names) != len(set(names)): raise ValueError('Existing ZIP has duplicate names')
        manifests = [n for n in names if n.count('/') == 1 and n.endswith('/package-manifest.json')]
        if len(manifests) != 1: raise ValueError('Expected one package manifest')
        manifest_name = manifests[0]; prefix = manifest_name.split('/')[0]
        original_manifest = old.read(manifest_name)
        manifest = json.loads(original_manifest)
        start = old.start_dir
    records = {item['path']: item for item in manifest['files']}
    for path in paths:
        rel = PurePosixPath(path)
        source = (root / path).resolve()
        if rel.is_absolute() or '..' in rel.parts or not source.is_relative_to(root.resolve()) or source.is_symlink() or not source.is_file():
            raise ValueError('Invalid package path: ' + path)
        with source.open('rb') as data: digest = hashlib.file_digest(data, 'sha256').hexdigest()
        records[path] = dict(path=path, size=source.stat().st_size, sha256=digest)
    manifest['files'] = sorted(records.values(), key=lambda x: x['path'])
    manifest['totalBytes'] = sum(item['size'] for item in manifest['files'])
    encoded = json.dumps(manifest, ensure_ascii=False, indent=2).encode('utf-8')
    backup = archive.with_suffix('.zip.rollback-footer')
    if backup.exists(): raise FileExistsError(backup)
    with archive.open('rb') as source:
        source.seek(start); footer = source.read()
    with backup.open('xb') as target:
        target.write(footer); target.flush(); os.fsync(target.fileno())
    backup.with_suffix('.json').write_text(json.dumps(dict(archive=str(archive), offset=start, size=original_size)), encoding='utf-8')
    replacements = {prefix + '/' + path for path in paths} | {manifest_name}
    try:
        with zipfile.ZipFile(archive, 'a', compression=zipfile.ZIP_DEFLATED, compresslevel=1, allowZip64=True) as bundle:
            bundle.filelist = [item for item in bundle.filelist if item.filename not in replacements]
            bundle.NameToInfo = {item.filename: item for item in bundle.filelist}
            for path in paths: bundle.write(root/path, prefix+'/'+path)
            bundle.writestr(manifest_name, encoded)
        with zipfile.ZipFile(archive) as bundle:
            names = bundle.namelist()
            expected = {prefix+'/'+path for path in records} | {manifest_name}
            if len(names) != len(set(names)) or set(names) != expected: raise ValueError('ZIP inventory mismatch')
            for path in paths:
                data = bundle.read(prefix+'/'+path)
                if hashlib.sha256(data).hexdigest() != records[path]['sha256']: raise ValueError('Changed file hash mismatch: '+path)
        (root/'package-manifest.json').write_bytes(encoded)
    except BaseException:
        with archive.open('r+b') as target:
            target.seek(start); target.write(footer); target.truncate(original_size); target.flush(); os.fsync(target.fileno())
        raise
    print(json.dumps(dict(updated=len(paths), files=len(records), archive=str(archive), rollback_footer=str(backup))), flush=True)


def verify(archive, report):
    checked = 0; checked_bytes = 0; last = time.monotonic()
    with zipfile.ZipFile(archive) as bundle:
        names = bundle.namelist()
        manifest_name = next(n for n in names if n.count('/') == 1 and n.endswith('/package-manifest.json'))
        prefix = manifest_name.split('/')[0]
        manifest = json.loads(bundle.read(manifest_name))
        expected = {prefix+'/'+item['path'] for item in manifest['files']} | {manifest_name}
        if len(names) != len(set(names)) or set(names) != expected: raise ValueError('ZIP inventory mismatch')
        for item in manifest['files']:
            digest = hashlib.sha256(); size = 0
            with bundle.open(prefix+'/'+item['path']) as source:
                while block := source.read(1024*1024): digest.update(block); size += len(block)
            if size != item['size'] or digest.hexdigest() != item['sha256']: raise ValueError('Hash mismatch: '+item['path'])
            checked += 1; checked_bytes += size
            if time.monotonic()-last >= 20:
                print(json.dumps(dict(checked=checked, total=len(manifest['files']))), flush=True); last=time.monotonic()
    with archive.open('rb') as source: sha = hashlib.file_digest(source, 'sha256').hexdigest()
    result = dict(passed=True, checkedFiles=checked, checkedBytes=checked_bytes, archiveBytes=archive.stat().st_size, sha256=sha,
                  mode='All ZIP entries streamed and CRC/SHA-256 checked; no second full extraction')
    report.write_text(json.dumps(result, indent=2), encoding='utf-8')
    archive.with_suffix('.zip.sha256').write_text(sha+'  '+archive.name+'\n', encoding='ascii')
    print(json.dumps(result), flush=True)


if __name__ == '__main__':
    parser=argparse.ArgumentParser(); parser.add_argument('mode', choices=['update','verify']); parser.add_argument('archive',type=Path)
    parser.add_argument('root_or_report',type=Path); parser.add_argument('--paths',type=Path)
    args=parser.parse_args()
    if args.mode=='update': update(args.archive.resolve(), args.root_or_report.resolve(), args.paths)
    else: verify(args.archive.resolve(), args.root_or_report.resolve())

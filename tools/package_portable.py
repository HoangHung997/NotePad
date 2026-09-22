"""ZIP64 packaging and independent extraction/hash check; Python standard library only."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path
import time
import zipfile


def pack(root: Path, archive: Path):
    if archive.exists():
        raise FileExistsError(archive)
    records = []
    files = sorted(p for p in root.rglob('*') if p.is_file() and p.name != 'package-manifest.json'
                   and '__pycache__' not in p.parts and p.suffix != '.pyc')
    total = sum(p.stat().st_size for p in files)
    done = 0
    reported = time.monotonic()
    with zipfile.ZipFile(archive, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=1, allowZip64=True) as bundle:
        for path in files:
            if path.is_symlink():
                raise ValueError(f'Link is not a portable file: {path}')
            relative = path.relative_to(root).as_posix()
            info = zipfile.ZipInfo.from_file(path, root.name + '/' + relative)
            info.compress_type = zipfile.ZIP_DEFLATED
            info._compresslevel = 1
            digest = hashlib.sha256()
            size = 0
            with path.open('rb') as source, bundle.open(info, 'w', force_zip64=True) as target:
                while block := source.read(1024 * 1024):
                    digest.update(block)
                    target.write(block)
                    size += len(block)
            records.append(dict(path=relative, size=size, sha256=digest.hexdigest()))
            done += size
            if time.monotonic() - reported >= 20:
                print(json.dumps(dict(phase='pack', files=len(records), bytes=done, totalBytes=total)), flush=True)
                reported = time.monotonic()
        manifest = json.dumps(dict(schema=1, files=records, totalBytes=done), indent=2, ensure_ascii=False)
        (root / 'package-manifest.json').write_text(manifest, encoding='utf-8')
        bundle.writestr(root.name + '/package-manifest.json', manifest)
    with archive.open('rb') as source:
        digest = hashlib.file_digest(source, 'sha256').hexdigest()
    archive.with_suffix('.zip.sha256').write_text(digest + '  ' + archive.name + '\n', encoding='ascii')
    print(json.dumps(dict(phase='packed', files=len(records), size=archive.stat().st_size, sha256=digest)), flush=True)


def verify(archive: Path, destination: Path):
    if destination.exists():
        raise FileExistsError(destination)
    destination.mkdir(parents=True)
    reported = time.monotonic()
    with zipfile.ZipFile(archive) as bundle:
        manifests = [n for n in bundle.namelist() if n.count('/') == 1 and n.endswith('/package-manifest.json')]
        if len(manifests) != 1:
            raise ValueError('Expected one package manifest')
        prefix = manifests[0].split('/')[0]
        manifest = json.loads(bundle.read(manifests[0]))
        targets = []
        # Resolve paths before worker threads create their shared parent folders.
        # Keep canonical path validation independent of concurrent directory creation.
        resolved_destination = destination.resolve()
        for record in manifest['files']:
            target = (destination / prefix / record['path']).resolve()
            if not target.is_relative_to(resolved_destination):
                raise ValueError(f'Unsafe archive path: {record["path"]!r}')
            if os.name == 'nt' and len(str(target)) >= 260:
                raise ValueError('Destination is too deep for default Windows path limits; choose a short folder such as C:/H2Notes')
            targets.append((record, target))

        def extract_and_check(item):
            record, target = item
            target.parent.mkdir(parents=True, exist_ok=True)
            with bundle.open(prefix + '/' + record['path']) as source, target.open('xb') as output:
                while block := source.read(1024 * 1024):
                    output.write(block)
            # Reopen the extracted file: validate bytes actually written, not only the ZIP stream.
            with target.open('rb') as source:
                actual = hashlib.file_digest(source, 'sha256').hexdigest()
            if target.stat().st_size != record['size'] or actual != record['sha256']:
                raise ValueError(f'Hash mismatch: {record["path"]}')
        # ZipFile serializes shared archive reads; independent writes/hash checks
        # can overlap to avoid a long queue of tiny Python-library files.
        with ThreadPoolExecutor(max_workers=4) as pool:
            for number, _ in enumerate(pool.map(extract_and_check, targets), 1):
                if time.monotonic() - reported >= 20:
                    print(json.dumps(dict(phase='extract-verify', files=number, totalFiles=len(manifest['files']))), flush=True)
                    reported = time.monotonic()
        (destination / prefix / 'package-manifest.json').write_bytes(bundle.read(manifests[0]))
    result = dict(passed=True, checkedFiles=len(manifest['files']), checkedBytes=manifest['totalBytes'], root=str(destination / prefix))
    (destination / 'extraction-check.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('mode', choices=['pack', 'verify'])
    parser.add_argument('source', type=Path)
    parser.add_argument('target', type=Path)
    args = parser.parse_args()
    (pack if args.mode == 'pack' else verify)(args.source.resolve(), args.target.resolve())

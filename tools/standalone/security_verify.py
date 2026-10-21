"""Fail closed on antivirus detections, pending reviews or changed release bytes.

This is a release check, not a guarantee that every antivirus will accept a file.
It never executes the payload, restores quarantine or changes antivirus settings.
"""
from pathlib import Path
import argparse
import hashlib
import json
import re
import subprocess
import urllib.request
import zipfile
from datetime import datetime, timezone, timedelta

POLICY = Path(__file__).with_name('security-policy.json')
RELEASE = Path(__file__).resolve().parents[2] / 'release.json'
VERSION = '1.5.4'
ARCHIVE = 'clamav-' + VERSION + '.win.x64.zip'
URL = 'https://github.com/Cisco-Talos/clamav/releases/download/clamav-' + VERSION + '/' + ARCHIVE
TOOL_SHA = '0d9e0228b2674137ea1a2853566c98a0278ad52ab2582c3d6dbd75373848c395'
RUNTIME_SUFFIXES = {'.exe', '.dll', '.pyd', '.node', '.ps1', '.bat', '.cmd', '.py', '.lua', '.vbs', '.js'}


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def dump(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')


def inventory(app):
    result = {}
    for path in sorted(app.rglob('*')):
        if path.is_symlink():
            raise RuntimeError('Symlink in release payload: ' + str(path))
        if not path.is_file():
            continue
        with path.open('rb') as stream:
            pe = stream.read(2) == b'MZ'
        if pe or path.suffix.lower() in RUNTIME_SUFFIXES:
            result[path.relative_to(app).as_posix()] = sha(path)
    if not result:
        raise RuntimeError('No release executables or scripts to verify')
    return result


def owner_accepted(name, digest, vendor, policy):
    tag = json.loads(RELEASE.read_text(encoding='utf-8'))['tag']
    return any(row.get('path') == name and row.get('sha256') == digest
               and re.fullmatch(r'[0-9a-f]{64}', digest or '')
               and row.get('vendor') == vendor and row.get('tag') == tag
               and row.get('scope') == 'public-release'
               and row.get('decision') == 'publish-despite-unresolved-detection'
               and isinstance(row.get('reason'), str) and row['reason'].strip()
               for row in policy.get('owner_acceptances', []))


def policy_issues(files, policy):
    if policy.get('schema') != 1:
        raise RuntimeError('Invalid security policy schema')
    issues = []
    reported = {row['sha256']: row for row in policy['reported_files']}
    for name, digest in files.items():
        if digest in reported:
            row = reported[digest]
            if not owner_accepted(name, digest, row['vendor'], policy):
                issues.append({'path': name, 'sha256': digest, 'reason': 'antivirus-report-unresolved',
                               'vendor': row['vendor'], 'detection': row['detection']})
    for required in policy['required_reviews']:
        name, vendor = required['path'], required['vendor']
        accepted = any(row.get('path') == name and row.get('vendor') == vendor
                       and row.get('sha256') == files.get(name) and row.get('verdict') == 'clean'
                       and isinstance(row.get('review_id'), str) and row['review_id'].strip()
                       for row in policy['reviews'])
        if name not in files or not (accepted or owner_accepted(name, files.get(name), vendor, policy)):
            issues.append({'path': name, 'reason': 'official-review-required', 'vendor': vendor})
    return issues


def preflight(policy_path=POLICY):
    policy = json.loads(policy_path.read_text(encoding='utf-8'))
    if policy.get('schema') != 1:
        raise RuntimeError('Invalid security policy schema')
    reported = {row['sha256'] for row in policy['reported_files']}
    for required in policy['required_reviews']:
        approved = [row for row in policy['reviews'] if row.get('path') == required['path']
                    and row.get('vendor') == required['vendor'] and row.get('verdict') == 'clean'
                    and re.fullmatch(r'[0-9a-f]{64}', row.get('sha256', ''))
                    and isinstance(row.get('review_id'), str) and row['review_id'].strip()
                    and row['sha256'] not in reported]
        accepted = any(owner_accepted(required['path'], row.get('sha256'), required['vendor'], policy)
                       for row in policy.get('owner_acceptances', []))
        if not approved and not accepted:
            raise RuntimeError('Release on security hold: official review pending for ' + required['path'])
    print('PASS release-security policy preflight')


def require_result(app, evidence, policy_path=POLICY, payload=None):
    policy = json.loads(policy_path.read_text(encoding='utf-8'))
    files = inventory(app)
    payload_files = inventory(payload) if payload is not None else None
    if policy_issues(payload_files if payload_files is not None else files, policy):
        raise RuntimeError('Antivirus report or official review blocks publication')
    result = json.loads(evidence.read_text(encoding='utf-8'))
    if (result.get('schema') != 1 or result.get('passed') is not True
            or result.get('scanner_exit_code') != 0 or result.get('infected_files') != 0
            or result.get('engine_version') != VERSION or result.get('known_viruses', 0) <= 0
            or result.get('scanned_files') != len(files)
            or result.get('files') != files or result.get('policy_sha256') != sha(policy_path)
            or result.get('payload_files') != payload_files):
        raise RuntimeError('Missing, incomplete or changed antivirus evidence')
    checked = datetime.fromisoformat(result['checked_at'])
    if checked.tzinfo is None:
        raise RuntimeError('Antivirus evidence timestamp needs a timezone')
    age = datetime.now(timezone.utc) - checked
    if not timedelta(0) <= age <= timedelta(hours=24):
        raise RuntimeError('Antivirus evidence is older than 24 hours or dated in the future')


def scanner(cache):
    cache.mkdir(parents=True, exist_ok=True)
    archive = cache / ARCHIVE
    if not archive.exists() or sha(archive) != TOOL_SHA:
        urllib.request.urlretrieve(URL, archive)
    if sha(archive) != TOOL_SHA:
        raise RuntimeError('Antivirus scanner archive hash mismatch')
    root = cache / 'clamav'
    with zipfile.ZipFile(archive) as source:
        for entry in source.infolist():
            if not (root / entry.filename).resolve().is_relative_to(root.resolve()):
                raise RuntimeError('Unsafe scanner archive path')
        source.extractall(root)
    return root / ('clamav-' + VERSION + '.win.x64')


def scan(app, out, cache, policy_path=POLICY, payload=None):
    app, out, cache = app.resolve(), out.resolve(), cache.resolve()
    if out.is_relative_to(app) or cache.is_relative_to(app):
        raise RuntimeError('Antivirus tools and evidence must be outside the release payload')
    out.mkdir(parents=True, exist_ok=True)
    files = inventory(app)
    policy = json.loads(policy_path.read_text(encoding='utf-8'))
    payload_files = inventory(payload) if payload is not None else None
    issues = policy_issues(payload_files if payload_files is not None else files, policy)
    result = {'schema': 1, 'passed': False, 'files': files, 'policy_sha256': sha(policy_path),
              'checked_at': datetime.now(timezone.utc).isoformat(), 'issues': issues,
              'payload_files': payload_files}
    dump(out / 'results.json', result)
    if issues:
        raise RuntimeError('Publication blocked: unresolved antivirus report or missing official review')
    tool = scanner(cache)
    database = cache / 'database'
    database.mkdir(exist_ok=True)
    config = out / 'freshclam.conf'
    config.write_text('DatabaseDirectory ' + str(database) + '\nDatabaseMirror database.clamav.net\n',
                      encoding='utf-8')
    with (out / 'database-update.log').open('wb') as log:
        subprocess.run([str(tool / 'freshclam.exe'), '--config-file=' + str(config), '--quiet'],
                       stdout=log, stderr=subprocess.STDOUT, timeout=600, check=True)
    file_list = out / 'scan-files.txt'
    file_list.write_text(''.join(str(app / name) + '\n' for name in files), encoding='utf-8')
    with (out / 'scan.log').open('wb') as log:
        completed = subprocess.run([str(tool / 'clamscan.exe'), '--database=' + str(database),
            '--file-list=' + str(file_list), '--scan-pe=yes', '--allmatch', '--alert-exceeds-max=yes',
            '--fail-if-cvd-older-than=2', '--max-filesize=2048M', '--max-scansize=4096M'],
            stdout=log, stderr=subprocess.STDOUT, timeout=1800)
    log_text = (out / 'scan.log').read_text(encoding='utf-8', errors='replace')
    result['scanner_exit_code'] = completed.returncode
    result['engine_version'] = VERSION
    for label, key in [('Scanned files', 'scanned_files'), ('Infected files', 'infected_files'),
                       ('Known viruses', 'known_viruses')]:
        match = re.search(r'^' + label + r':\s+(\d+)\s*$', log_text, re.MULTILINE)
        if match:
            result[key] = int(match[1])
    result['passed'] = (completed.returncode == 0 and result.get('infected_files') == 0
                        and result.get('scanned_files') == len(files) and result.get('known_viruses', 0) > 0
                        and inventory(app) == files)
    result['checked_at'] = datetime.now(timezone.utc).isoformat()
    dump(out / 'results.json', result)
    require_result(app, out / 'results.json', policy_path, payload)
    print('PASS antivirus release check:', len(files), 'files; release policy verified')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=('preflight', 'scan'))
    parser.add_argument('app', type=Path, nargs='?')
    parser.add_argument('out', type=Path, nargs='?')
    parser.add_argument('--cache', type=Path, default=Path('downloads/antivirus'))
    parser.add_argument('--installer-payload', type=Path)
    args = parser.parse_args()
    if args.command == 'preflight':
        preflight()
    elif args.app is None or args.out is None:
        parser.error('scan requires app and out directories')
    else:
        scan(args.app, args.out, args.cache, payload=args.installer_payload)

"""Compile the existing Inno Setup definition from an assembled release.

Build tools are unpacked into the dependency cache, never installed system-wide.
"""
from pathlib import Path
import argparse
import hashlib
import json
import subprocess
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'src/player/src/Setup/Inno/inno-setup.iss'
TOOLS = (
    ('innosetup-6.7.3.exe',
     'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe',
     '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732'),
    ('innoextract670.zip',
     'https://github.com/UserUnknownFactor/innoextract_win/releases/download/670/innoextract670.zip',
     '79b69b9b1fcd98f42ccd4b245efdf6a03bcfb674ba6af482f5a46891c9ed4d14'),
)


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def compiler(cache):
    cache.mkdir(parents=True, exist_ok=True)
    for name, url, digest in TOOLS:
        path = cache / name
        if not path.exists() or sha(path) != digest:
            urllib.request.urlretrieve(url, path)
        if sha(path) != digest:
            raise RuntimeError('Installer build-tool checksum mismatch: ' + name)
    extractor = cache / 'extractor'
    with zipfile.ZipFile(cache / TOOLS[1][0]) as source:
        for entry in source.infolist():
            if not (extractor / entry.filename).resolve().is_relative_to(extractor.resolve()):
                raise RuntimeError('Unsafe build-tool archive path')
        source.extractall(extractor)
    subprocess.run([str(extractor / 'innoextract.exe'), '--silent', '--output-dir',
                    str(cache / 'compiler'), str(cache / TOOLS[0][0])], check=True)
    return cache / 'compiler/app/ISCC.exe'


def build(payload, output, evidence):
    payload, output, evidence = payload.resolve(), output.resolve(), evidence.resolve()
    metadata = json.loads((ROOT / 'release.json').read_text(encoding='utf-8'))
    manifest = json.loads((payload / 'app/manifest.json').read_text(encoding='utf-8'))
    if manifest['name'] != metadata['name'] or manifest['version'] != metadata['version']:
        raise RuntimeError('Installer payload identity does not match release.json')
    # This exact layout is owned by build.py. Fail if new top-level data is added
    # without an installation policy, rather than silently omitting it.
    expected = {'app', 'docs', 'animejanai', 'portable_config',
                'mpv-AnimeFusion.exe', 'mpv-AnimeFusionManager.exe'}
    if {p.name for p in payload.iterdir()} != expected:
        raise RuntimeError('Unexpected installer payload layout')
    portable = payload / 'portable_config'
    if any(p.is_file() and p.suffix != '.conf' for p in portable.iterdir()):
        raise RuntimeError('Unexpected user data in installer payload')
    if {p.name for p in portable.iterdir() if p.is_dir()} != {
            'scripts', 'script-modules', 'script-opts', 'shaders'}:
        raise RuntimeError('Unexpected portable configuration directory')
    compiler_path = compiler(ROOT / 'downloads/inno-setup')
    if sha(compiler_path.parent / 'license.txt') != sha(
            payload / 'docs/THIRD_PARTY_LICENSES/Inno-Setup.txt'):
        raise RuntimeError('Installer license does not match the pinned compiler')
    output.mkdir(parents=True, exist_ok=True)
    evidence.mkdir(parents=True, exist_ok=True)
    with (evidence / 'compile.log').open('wb') as log:
        subprocess.run([str(compiler_path), '/Qp', '/DMyAppSourceDir=' + str(payload),
                        '/DMyAppVersion=' + metadata['version'],
                        '/DMyAppOutputDir=' + str(output), str(SOURCE)],
                       stdout=log, stderr=subprocess.STDOUT, check=True)
    installer = output / f'{metadata["name"]}-{metadata["version"]}-setup-x64.exe'
    result = {'version': metadata['version'], 'installer': installer.name,
              'sha256': sha(installer), 'bytes': installer.stat().st_size,
              'definition_sha256': sha(SOURCE),
              'translation_sha256': sha(SOURCE.with_name('ChineseSimplified.isl')),
              'tools': [{'name': name, 'url': url, 'sha256': digest}
                        for name, url, digest in TOOLS]}
    (evidence / 'build.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print('INSTALLER BUILT', installer.name, installer.stat().st_size, flush=True)
    return installer


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('payload', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('evidence', type=Path)
    args = parser.parse_args()
    build(args.payload, args.output, args.evidence)

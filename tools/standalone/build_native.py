"""Build patched mpv locally from a pinned checkout and an audited runtime."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys

from gpu_target import pe_imports

ROOT = Path(__file__).resolve().parents[2]
COMMIT = 'd4c06dd3424dc06d90d4ea15b0fa92fa3ceeb62f'
GENERATORS = ('apply_native_r2.py', 'apply_secondary_sub_sync.py',
              'apply_subtitle_track_cache.py', 'apply_stats_header.py',
              'apply_watch_history.py', 'apply_animejanai_pre_resize.py')


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def stage_runtime(build, runtime, output):
    """Retain audited dependency DLLs instead of replacing them from the compiler."""
    output.mkdir(parents=True, exist_ok=False)
    available = {path.name.lower(): path for path in runtime.glob('*.dll')}
    pending = []
    for name in ('libmpv-2.dll', 'mpv.exe'):
        target = output / name
        shutil.copy2(build / name, target)
        pending.append(target)
    seen = {path.name.lower() for path in pending}
    system = Path(os.environ['SystemRoot']) / 'System32'
    while pending:
        for name in pe_imports(pending.pop()):
            key = name.lower()
            if key in seen:
                continue
            seen.add(key)
            if key in available:
                target = output / available[key].name
                shutil.copy2(available[key], target)
                pending.append(target)
            elif not (key.startswith(('api-ms-', 'ext-ms-')) or (system / name).exists()):
                raise RuntimeError('Audited native dependency is missing: ' + name)


def build(args):
    work = args.work.resolve()
    if work.exists():
        raise RuntimeError('Choose a new build directory to preserve prior sources and evidence')
    work.mkdir(parents=True)
    source = work / 'mpv-source'
    compiled = work / 'build'
    output = work / 'native-output'
    environment = os.environ.copy()
    environment['PATH'] = os.pathsep.join((str(args.python_packages / 'bin'),
                                          str(args.toolchain / 'bin'), environment['PATH']))
    environment['PYTHONPATH'] = str(args.python_packages)
    environment['CC'] = str(args.toolchain / 'bin/gcc.exe')
    environment['CXX'] = str(args.toolchain / 'bin/g++.exe')
    environment['PKG_CONFIG'] = str(args.toolchain / 'bin/pkgconf.exe')
    environment['PKG_CONFIG_PATH'] = os.pathsep.join((str(args.prefix / 'lib/pkgconfig'),
                                                     str(args.toolchain / 'lib/pkgconfig')))

    def run(*command):
        subprocess.run([str(item) for item in command], cwd=ROOT, env=environment, check=True)

    # Clone only the supplied local repository; no cloud job or network fetch.
    run('git', 'clone', '--shared', '--no-checkout', args.repository, source)
    run('git', '-C', source, 'checkout', '--detach', COMMIT)
    run(sys.executable, ROOT / 'tools/apply_native_r2.py', source)
    meson = [sys.executable, '-m', 'mesonbuild.mesonmain']
    run(*meson, 'setup', compiled, source, '--prefix=' + str(args.prefix),
        '--buildtype=release', '--wrap-mode=nodownload', '-Dlibmpv=true', '-Dcplayer=true',
        '-Dtests=true', '-Ddebug=true', '-Dcuda-hwaccel=enabled', '-Dcuda-interop=enabled',
        '-Dlua=luajit', '-Djavascript=enabled', '-Dd3d11=enabled', '-Dvulkan=enabled',
        '-Dmanpage-build=disabled', '-Dhtml-build=disabled', '-Dpdf-build=disabled')
    run(*meson, 'compile', '-C', compiled, '-j', args.jobs)
    stage_runtime(compiled, args.runtime, output)
    run(sys.executable, ROOT / 'tests/test_native_watch_history.py', output, work / 'history-tests')
    run(sys.executable, ROOT / 'tests/test_native_runtime.py', output, work / 'runtime-tests')

    info = output / 'build-info'
    info.mkdir()
    patch = subprocess.check_output(['git', '-C', str(source), 'diff', '--binary'], env=environment)
    (info / 'native-r2.patch').write_bytes(patch)
    generators = {'tools/' + name: sha(ROOT / 'tools' / name) for name in GENERATORS}
    record = {'mpv_source_commit': COMMIT, 'patch_generators': generators,
              'native_patch_sha256': sha(info / 'native-r2.patch'),
              'files': {path.name: sha(path) for path in output.iterdir() if path.is_file()},
              'tests': ['native-watch-history', 'native-runtime'], 'gpu_tested': False}
    (info / 'local-native-build.json').write_text(json.dumps(record, indent=2), encoding='utf-8')
    shutil.copy2(work / 'runtime-tests/results.json', info / 'native-runtime-results.json')
    shutil.copy2(work / 'history-tests/results.json', info / 'native-history-results.json')
    print('MPV_NATIVE_OUTPUT=' + str(output))
    return output


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repository', type=Path, required=True,
                        help='Existing local mpv Git repository containing the pinned commit')
    parser.add_argument('--work', type=Path, required=True)
    parser.add_argument('--toolchain', type=Path, required=True, help='MSYS2 UCRT64 prefix')
    parser.add_argument('--python-packages', type=Path, required=True, help='Meson and Ninja package directory')
    parser.add_argument('--prefix', type=Path, required=True, help='Build prefix containing reviewed libass headers/import library')
    parser.add_argument('--runtime', type=Path, required=True, help='Reviewed native runtime DLL directory')
    parser.add_argument('--jobs', type=int, default=4)
    arguments = parser.parse_args()
    for name in ('repository', 'toolchain', 'python_packages', 'prefix', 'runtime'):
        setattr(arguments, name, getattr(arguments, name).resolve())
    build(arguments)

"""Build patched mpv locally from a pinned checkout and an audited runtime."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys

from gpu_target import pe_imports

ROOT = Path(__file__).resolve().parents[2]
COMMIT = '41868e1757bea768b07b0e020aa11b9654772c22'
GENERATORS = ('apply_native_r2.py', 'apply_secondary_sub_sync.py',
              'apply_subtitle_track_cache.py', 'apply_stats_header.py',
              'apply_watch_history.py', 'apply_animejanai_pre_resize.py',
              'standalone/subtitle-stability-test.patch', 'standalone/scene-reply-timeout.patch')
ASS_FEATURES = {'ASS_RENDER_THREAD_COUNT', 'ASS_BLUR_DEFERRED', 'ASS_COMPOSITE_DEFERRED',
                'ASS_OUTLINE_DEFERRED', 'ASS_SHADOW_SHIFT'}


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def check_ass_features(config):
    features = set(re.findall(r'^#define HAVE_(\w+) 1$', config.read_text(), re.M))
    missing = ASS_FEATURES - features
    if missing:
        raise RuntimeError('mpv was built without AnimeJaNai libass features: ' + ', '.join(sorted(missing)))


def check_placebo(directory, receipt):
    patches = ROOT / 'third_party/libplacebo-patches'
    expected = json.loads((patches / 'UPSTREAM.json').read_text(encoding='utf-8'))
    record = json.loads(receipt.read_text(encoding='utf-8'))
    if (record['source_commit'] != expected['source_commit'] or record['patches'] != expected['patches'] or
            sha(directory / 'libplacebo-387.dll') != record['sha256'] or
            any(sha(patches / name) != digest for name, digest in expected['patches'].items())):
        raise RuntimeError('libplacebo does not match the reviewed presentation patches')


def check_ggml(directory, receipt):
    from build_ggml import VERSION, COMMIT as GGML_COMMIT, ARCHIVE_SHA256, PATCH
    record = json.loads(receipt.read_text(encoding='utf-8'))
    if (record['version'] != VERSION or record['sourceCommit'] != GGML_COMMIT or
            record['sourceSha256'] != ARCHIVE_SHA256 or record['patchSha256'] != sha(PATCH) or
            record['patchedDllSha256'] != sha(directory / 'ggml-base-1.dll') or
            record['exportsPreserved'] is not True or record['applicationHandlerAndReloadTestPassed'] is not True):
        raise RuntimeError('ggml does not match the verified Windows DLL unload fix')


def stage_runtime(build, runtime, output, ass_dll, placebo, ggml):
    """Retain audited dependency DLLs instead of replacing them from the compiler."""
    output.mkdir(parents=True, exist_ok=False)
    available = {path.name.lower(): path for path in runtime.glob('*.dll')}
    available[ass_dll.name.lower()] = ass_dll
    available['libplacebo-387.dll'] = placebo / 'libplacebo-387.dll'
    available['ggml-base-1.dll'] = ggml / 'ggml-base-1.dll'
    pending = []
    # mpv loads the optional scene detector at runtime; PE imports cannot discover it.
    for binary in (build / 'libmpv-2.dll', build / 'mpv.exe',
                   build / 'video/filter/mvtools/ajn-mvtools.dll'):
        target = output / binary.name
        shutil.copy2(binary, target)
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
    ass_prefix = work / 'ass-prefix'
    environment = os.environ.copy()
    environment['PATH'] = os.pathsep.join((str(ass_prefix / 'bin'), str(args.python_packages / 'bin'),
                                          str(args.toolchain / 'bin'), environment['PATH']))
    environment['PYTHONPATH'] = str(args.python_packages)
    environment['CC'] = str(args.toolchain / 'bin/gcc.exe')
    environment['CXX'] = str(args.toolchain / 'bin/g++.exe')
    environment['PKG_CONFIG'] = str(args.toolchain / 'bin/pkgconf.exe')
    environment['PKG_CONFIG_PATH'] = os.pathsep.join((str(ass_prefix / 'lib/pkgconfig'),
                                                     str(args.prefix / 'lib/pkgconfig'),
                                                     str(args.toolchain / 'lib/pkgconfig')))

    def run(*command):
        subprocess.run([str(item) for item in command], cwd=ROOT, env=environment, check=True)

    # Clone only the supplied local repository; no cloud job or network fetch.
    run('git', 'clone', '--shared', '--no-checkout', args.repository, source)
    run('git', '-C', source, 'checkout', '--detach', COMMIT)
    run(sys.executable, ROOT / 'tools/apply_native_r2.py', source)
    meson = [sys.executable, '-m', 'mesonbuild.mesonmain']
    ass_build = work / 'build-ass'
    run(*meson, 'setup', ass_build, ROOT / 'third_party/libass', '--prefix=' + str(ass_prefix),
        '--libdir=lib', '--buildtype=release', '--default-library=shared', '--wrap-mode=nodownload',
        '-Dasm=enabled', '-Dthreads=enabled', '-Ddirectwrite=enabled', '-Dfontconfig=disabled',
        '-Dlibunibreak=enabled', '-Dtest=disabled', '-Dcompare=disabled', '-Dprofile=disabled',
        '-Dcheckasm=disabled')
    run(*meson, 'compile', '-C', ass_build, '-j', args.jobs)
    run(*meson, 'install', '-C', ass_build, '--no-rebuild')
    run(*meson, 'setup', compiled, source, '--prefix=' + str(args.prefix),
        '--buildtype=release', '--wrap-mode=nodownload', '-Dlibmpv=true', '-Dcplayer=true',
        '-Dtests=true', '-Ddebug=true', '-Dcuda-hwaccel=enabled', '-Dcuda-interop=enabled', '-Dajn-mvtools=true',
        '-Dlua=luajit', '-Djavascript=enabled', '-Dd3d11=enabled', '-Dvulkan=enabled',
        '-Dmanpage-build=disabled', '-Dhtml-build=disabled', '-Dpdf-build=disabled')
    check_ass_features(compiled / 'config.h')
    run(*meson, 'compile', '-C', compiled, '-j', args.jobs)
    run(sys.executable, source / 'TOOLS/subtest/test_stability.py', '--cc', environment['CC'],
        '--out', work / 'subtitle-stability')
    return verify_and_stage(work, args.runtime, args.libplacebo_dir, args.ggml_dir)


def verify_and_stage(work, runtime, placebo_dir=None, ggml_dir=None):
    compiled = work / 'build'
    source = work / 'mpv-source'
    output = work / 'native-output'
    placebo = placebo_dir or runtime
    placebo_receipt = placebo / 'build-info/libplacebo-source.json'
    ggml = ggml_dir or runtime
    ggml_receipt = ggml / ('ggml-reload.json' if ggml_dir else 'build-info/ggml-reload.json')
    check_ass_features(compiled / 'config.h')
    check_placebo(placebo, placebo_receipt)
    check_ggml(ggml, ggml_receipt)
    stage_runtime(compiled, runtime, output, work / 'ass-prefix/bin/libass-9.dll', placebo, ggml)
    subprocess.run([sys.executable, ROOT / 'tests/test_native_watch_history.py', output, work / 'history-tests'], check=True)
    subprocess.run([sys.executable, ROOT / 'tests/test_native_runtime.py', output, work / 'runtime-tests'], check=True)

    info = output / 'build-info'
    info.mkdir()
    shutil.copy2(compiled / 'config.h', info / 'mpv-config.h')
    shutil.copy2(placebo_receipt, info / 'libplacebo-source.json')
    shutil.copy2(ggml_receipt, info / 'ggml-reload.json')
    shutil.copy2(ROOT / 'third_party/libass/UPSTREAM.json', info / 'libass-source.json')
    shutil.copy2(source / 'Copyright', info / 'mpv-Copyright.txt')
    license_dir = info / 'ajn-mvtools'
    license_dir.mkdir()
    for name in ('LICENSE', 'UPSTREAM.md'):
        shutil.copy2(source / 'video/filter/mvtools' / name, license_dir / name)
    (info / 'mpv-commit.txt').write_text(COMMIT + '\n', encoding='ascii')
    version = subprocess.check_output([str(output / 'mpv.exe'), '--version'], cwd=output)
    (info / 'version.txt').write_bytes(version)
    shutil.copy2(work / 'runtime-tests/results.json', info / 'native-runtime-results.json')
    shutil.copy2(work / 'history-tests/results.json', info / 'native-history-results.json')
    stability = work / 'subtitle-stability/results.json'
    report = json.loads(stability.read_bytes())
    if report['build_exit'] or not report['cases'] or any(case['exit'] for case in report['cases']):
        raise RuntimeError('Native subtitle stability regression failed')
    shutil.copy2(stability, info / 'native-subtitle-stability.json')
    write_native_record(source, output)
    print('MPV_NATIVE_OUTPUT=' + str(output))
    return output


def write_native_record(source, output):
    info = output / 'build-info'
    patch = subprocess.check_output(['git', '-C', str(source), 'diff', '--binary'])
    (info / 'native-r2.patch').write_bytes(patch)
    generators = {'tools/' + name: sha(ROOT / 'tools' / name) for name in GENERATORS}
    record = {'mpv_source_commit': COMMIT, 'patch_generators': generators,
              'configuration_sha256': sha(info / 'mpv-config.h'),
              'native_patch_sha256': sha(info / 'native-r2.patch'),
              'files': {path.name: sha(path) for path in output.iterdir() if path.is_file()},
              'tests': ['native-watch-history', 'native-runtime', 'native-subtitle-stability'], 'gpu_tested': False}
    (info / 'local-native-build.json').write_text(json.dumps(record, indent=2), encoding='utf-8')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repository', type=Path, required=True,
                        help='Existing local mpv Git repository containing the pinned commit')
    parser.add_argument('--work', type=Path, required=True)
    parser.add_argument('--toolchain', type=Path, required=True, help='MSYS2 UCRT64 prefix')
    parser.add_argument('--python-packages', type=Path, required=True, help='Meson and Ninja package directory')
    parser.add_argument('--prefix', type=Path, required=True, help='Dependency prefix for the native mpv build')
    parser.add_argument('--runtime', type=Path, required=True, help='Reviewed native runtime DLL directory')
    parser.add_argument('--libplacebo-dir', type=Path, help='Patched DLL directory with build-info/libplacebo-source.json')
    parser.add_argument('--ggml-dir', type=Path, help='Patched ggml DLL directory with ggml-reload.json')
    parser.add_argument('--jobs', type=int, default=4)
    arguments = parser.parse_args()
    for name in ('repository', 'toolchain', 'python_packages', 'prefix', 'runtime'):
        setattr(arguments, name, getattr(arguments, name).resolve())
    build(arguments)

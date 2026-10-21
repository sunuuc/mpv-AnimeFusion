"""Run the real Inno uninstall code on isolated, non-executable fixture files.

The generated test definition omits shortcuts and the uninstall registry entry,
redirects user data, and supplies the three checkbox choices without opening UI.
No production test flags or alternate cleanup implementation are installed.
"""
from pathlib import Path
import argparse
import ctypes
import hashlib
import itertools
import json
import os
import re
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'src/player/src/Setup/Inno/inno-setup.iss'
MUTEXES = ('mpv-AnimeFusion_Player', 'mpv-AnimeFusionManager_SingleInstance',
           'mpv-AnimeFusion_Components')


def write(path, contents=b'Harmless uninstall fixture; not executable.\n'):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(contents)


def inventory(root):
    return {p.relative_to(root).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in root.rglob('*') if p.is_file()}


def run(command, log):
    with log.open('wb') as stream:
        return subprocess.run([str(x) for x in command], stdout=stream,
                              stderr=subprocess.STDOUT, timeout=60).returncode


def check(condition, message):
    if not condition:
        raise AssertionError(message)


def definition(work, mutexes):
    text = SOURCE.read_text(encoding='utf-8')
    check('AppMutex=' + ','.join(MUTEXES) in text, 'All application owners must block uninstall')
    for path, name in (
        ('src/player/src/MpvNet.Windows/Program.cs', MUTEXES[0]),
        ('src/manager/AnimeJaNaiConfEditor/Program.cs', MUTEXES[1]),
        ('tools/standalone/Updater.cs', MUTEXES[2]),
    ):
        check('"' + name + '"' in (ROOT / path).read_text(encoding='utf-8'),
              'Application must create its declared lifetime mutex: ' + name)
    text = text.replace('AppMutex=' + ','.join(MUTEXES), 'AppMutex=' + ','.join(mutexes))
    text = text.replace('AppId={{F5D22654-9F97-480D-973C-586627E6E509}}',
                        'AppId={{' + str(uuid.uuid4()).upper() + '}}')
    text = text.replace('UsePreviousAppDir=yes',
                        'UsePreviousAppDir=no\nCreateUninstallRegKey=no')
    text = re.sub(r'^SetupIconFile=.*$', 'SetupIconFile="' +
                  (ROOT / 'src/player/src/MpvNet.Windows/mpv-icon.ico').as_posix() + '"',
                  text, flags=re.MULTILINE)
    text = text.replace('MessagesFile: "ChineseSimplified.isl"',
                        'MessagesFile: "' + SOURCE.with_name('ChineseSimplified.isl').as_posix() + '"')
    for section in ('Icons', 'Run'):
        text, count = re.subn(r'^\[' + section + r'\]\n.*?(?=^\[)', '', text,
                             flags=re.MULTILINE | re.DOTALL)
        check(count == 1, 'Fixture must remove only the ' + section + ' section')
    text = text.replace('{localappdata}\\mpv-AnimeFusion', str(work / 'user-data'))
    old = 'if UninstallSilent() then\n    Result := True'
    choices = '\n'.join(f"    DeleteUserData[{i}] := ExpandConstant('{{param:test-{i}|0}}') = '1';"
                        for i in range(3))
    check(text.count(old) == 1, 'Fixture must use the existing uninstall entry point')
    text = text.replace(old, 'if UninstallSilent() then begin\n' + choices +
                        '\n    Result := True;\n  end')
    target = work / 'fixture.iss'
    target.write_text(text, encoding='utf-8')
    return target


def hold_mutex(name):
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.CreateMutexW.argtypes = [ctypes.c_void_p, ctypes.c_bool, ctypes.c_wchar_p]
    kernel.CreateMutexW.restype = ctypes.c_void_p
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel.CreateMutexW(None, False, name)
    check(handle and ctypes.get_last_error() != 183, 'Cannot isolate application mutex: ' + name)
    return kernel, handle


def main(compiler, work):
    check(os.name == 'nt', 'Native Inno checks require Windows')
    work = work.resolve()
    check(not work.is_relative_to(ROOT) and not ROOT.is_relative_to(work),
          'Evidence and fixture must live outside the source checkout')
    check(not work.exists(), 'Use a new fixture directory')
    work.mkdir(parents=True)
    payload = work / 'payload'
    for name in ('mpv-AnimeFusion.exe', 'mpv-AnimeFusionManager.exe', 'app/runtime.dll',
                 'docs/LICENSE', 'animejanai/danmaku/converter.exe',
                 'animejanai/inference/engine.dll', 'animejanai/animejanai.conf',
                 'portable_config/scripts/script.lua', 'portable_config/script-modules/module.lua',
                 'portable_config/shaders/shader.glsl', 'portable_config/mpv.conf',
                 'portable_config/script-opts/options.conf'):
        write(payload / name)
    mutexes = tuple(name + '_Fixture_' + uuid.uuid4().hex for name in MUTEXES)
    source = definition(work, mutexes)
    version = json.loads((ROOT / 'release.json').read_text(encoding='utf-8'))['version']
    output = work / 'output'
    output.mkdir()
    check(run([compiler, '/Qp', '/DMyAppSourceDir=' + str(payload), '/DMyAppVersion=' + version,
               '/DMyAppOutputDir=' + str(output), source], work / 'compile.log') == 0,
          'Fixture must compile with the pinned Inno compiler')
    installer = output / ('mpv-AnimeFusion-' + version + '-setup-x64.exe')
    silent = ['/SP-', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART']
    install = work / 'installed'
    for i, name in enumerate(mutexes):
        kernel, handle = hold_mutex(name)
        try:
            code = run([installer, *silent, '/DIR=' + str(install),
                        '/LOG=' + str(work / f'install-busy-{i}.log')], work / f'install-busy-{i}.stdout')
            check(code != 0 and not install.exists(), 'Running application must block installation')
        finally:
            kernel.CloseHandle(handle)
    cases = []
    for selected in itertools.product((False, True), repeat=3):
        tag = ''.join(str(int(x)) for x in selected)
        check(run([installer, *silent, '/DIR=' + str(install),
                   '/LOG=' + str(work / f'install-{tag}.log')], work / f'install-{tag}.stdout') == 0,
              'Fixture install must succeed')
        model_names = ['animejanai/onnx/model.onnx', 'animejanai/onnx/model.engine',
                       'animejanai/onnx/model.timing.cache', 'animejanai/rife/model.onnx',
                       'animejanai/rife/model.engine', 'animejanai/inference/engine.dll']
        personal_names = ['portable_config/settings.xml', 'portable_config/custom-data.json',
                          'portable_config/watch_later/progress', 'portable_config/user-script.lua',
                          'animejanai/animejanai.conf', 'animejanai/backups/config.bak']
        disposable_names = ['app/untracked-runtime.dll', 'docs/untracked.txt',
                            'build-info/untracked.json', 'portable_config/cache/cache.bin',
                            'portable_config/logs/diagnostic.jsonl', 'portable_config/startup-error.json',
                            'portable_config/startup-error.json.tmp', 'portable_config/MpvNet-debug.log',
                            'animejanai/currentanimejanai.log', 'animejanai/onnx/model.engine.build.log',
                            'animejanai/rife/model.engine.build.log']
        for name in model_names + personal_names + disposable_names:
            write(install / name)
        userdata = work / 'user-data'
        personal_data = ['Bangumi/account.json', 'Trakt/account.json',
                         'MyAnimeList/account.json', 'watch_history.jsonl']
        subtitle_data = ['Subtitles/title/sub.ass', 'Danmaku/title/comments.xml']
        for name in personal_data + subtitle_data:
            write(userdata / name)
        expected_models = {name: hashlib.sha256((install / name).read_bytes()).hexdigest()
                           for name in model_names}
        options = [f'/test-{i}={int(value)}' for i, value in enumerate(selected)]
        uninstaller = install / 'unins000.exe'
        if tag == '000':
            for i, name in enumerate(mutexes):
                before = inventory(install), inventory(userdata)
                kernel, handle = hold_mutex(name)
                try:
                    code = run([uninstaller, *silent, *options,
                                '/LOG=' + str(work / f'uninstall-busy-{i}.log')],
                               work / f'uninstall-busy-{i}.stdout')
                    check(code != 0 and before == (inventory(install), inventory(userdata)),
                          'Blocked uninstall must preserve every program and data file')
                finally:
                    kernel.CloseHandle(handle)
        check(run([uninstaller, *silent, *options, '/LOG=' + str(work / f'uninstall-{tag}.log')],
                  work / f'uninstall-{tag}.stdout') == 0, 'Uninstall must succeed')
        deadline = time.monotonic() + 20
        while uninstaller.exists() and time.monotonic() < deadline:
            time.sleep(.1)
        check(not uninstaller.exists(), 'Native uninstall cleanup must finish before the next install')
        for name in disposable_names + ['mpv-AnimeFusion.exe', 'mpv-AnimeFusionManager.exe',
                                        'app/runtime.dll', 'animejanai/danmaku/converter.exe']:
            check(not (install / name).exists(), 'Program or diagnostic residue: ' + name)
        for name in personal_names:
            check((install / name).exists() != selected[0], 'Personal data choice ignored: ' + name)
        for name in model_names:
            check((install / name).exists() != selected[2], 'Model choice ignored: ' + name)
            if not selected[2]:
                check(hashlib.sha256((install / name).read_bytes()).hexdigest() == expected_models[name],
                      'Retained model bytes changed')
        for names, remove in ((personal_data, selected[0]), (subtitle_data, selected[1])):
            for name in names:
                check((userdata / name).exists() != remove, 'User data choice ignored: ' + name)
        cases.append({'personal': selected[0], 'subtitles': selected[1], 'models': selected[2], 'passed': True})
    (work / 'results.json').write_text(json.dumps({'cases': cases, 'busy_checks': 6,
        'desktop_interaction': False, 'definition_sha256': hashlib.sha256(SOURCE.read_bytes()).hexdigest()},
        indent=2) + '\n', encoding='utf-8')
    print('PASS 8 uninstall combinations and 6 native application-in-use checks')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--compiler', type=Path, required=True)
    parser.add_argument('--work', type=Path, required=True)
    args = parser.parse_args()
    main(args.compiler.resolve(), args.work)

"""Exercise the player's UTF-8 manifest, native Lua and Chinese data directories."""
import argparse
import os
import subprocess
import tempfile
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser()
parser.add_argument('--install',type=Path,required=True)
parser.add_argument('--test-exe',type=Path,default=ROOT/'tests/danmaku-storage/bin/Release/net10.0-windows7.0/DanmakuStorageChecks.exe')
args=parser.parse_args()
with tempfile.TemporaryDirectory(prefix='AnimeFusion-DanmakuStorage-') as folder:
    env=os.environ.copy()
    env['LOCALAPPDATA']=folder
    result=subprocess.run([str(args.test_exe),str(args.install/'app'),str(args.install/'portable_config/scripts/player_ui_danmaku.lua'),folder],
                          env=env,capture_output=True,timeout=20)
    print(result.stdout.decode('utf-8','replace'),end='')
    if result.returncode:
        print(result.stderr.decode('utf-8','replace'),end='')
    raise SystemExit(result.returncode)

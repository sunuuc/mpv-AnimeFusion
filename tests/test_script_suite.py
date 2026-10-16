"""Test controller state machines plus the packaged mpv Lua runtime."""
from pathlib import Path
import json,subprocess,sys
ROOT=Path(__file__).resolve().parents[1]
bundle=Path(sys.argv[1]).resolve();out=Path(sys.argv[2]).resolve();out.mkdir(parents=True,exist_ok=True)
results=[]
def run(name,args,marker):
    cp=subprocess.run([str(bundle/'app/mpv.exe'),'--no-config','--load-scripts=no','--osc=no','--vo=null','--ao=null',*args],capture_output=True,timeout=30)
    log=cp.stdout+cp.stderr;(out/(name+'.log')).write_bytes(log)
    passed=cp.returncode==0 and marker in log and b'stack traceback' not in log.lower()
    results.append({'case':name,'passed':passed,'exit_code':cp.returncode})
run('control-tests',['--idle=yes','--scripts='+str(ROOT/'tests/test_controls.lua'),'--script-opts=r2root='+str(bundle)],b'PASS controls r5:')
sample=out/'script-smoke.y4m';sample.write_bytes(b'YUV4MPEG2 W16 H16 F24:1 Ip A1:1 C420jpeg\n'+(b'FRAME\n'+bytes([100])*256+bytes([128])*128)*72)
script_dir=bundle/'portable_config/scripts'
assert not (script_dir/'animejanai_update.lua').exists(), 'obsolete release-page script was packaged'
scripts=[script_dir/n for n in ['animejanaistats.lua','animejanai_session.lua','animejanai_engine_monitor.lua']]
scripts.append(ROOT/'tests/test_smoke.lua')
run('script-smoke',['--config-dir='+str(bundle/'portable_config'),'--scripts='+';'.join(map(str,scripts)),str(sample)],b'PASS packaged scripts:')
sample.unlink()
(out/'scripts-results.json').write_text(json.dumps(results,indent=2),encoding='utf-8')
print(json.dumps(results,indent=2))
if not all(r['passed'] for r in results):raise SystemExit(1)

"""Exercise packaged controls with real mpv input events and D3D11 WARP."""
from pathlib import Path
import json,os,shutil,subprocess,sys,wave
ROOT=Path(__file__).resolve().parents[1]
APP=Path(sys.argv[1]).resolve();OUT=Path(sys.argv[2]).resolve();OUT.mkdir(parents=True,exist_ok=True)
CONFIG=OUT/'config';CONFIG.mkdir(parents=True,exist_ok=True)
PRIVATE=OUT/'private';PRIVATE.mkdir(parents=True,exist_ok=True)
PRIVATE.joinpath('AnimeVE-danmaku.conf').write_text(
    '# Isolated test-only source; .invalid can never resolve.\n'
    'api_servers=https://danmaku.example.invalid|Fixture\n',encoding='utf-8')
(CONFIG/'input.conf').write_text('',encoding='utf-8')
PROFILE_ROOT=OUT/'animejanai';PROFILE_ROOT.mkdir(parents=True,exist_ok=True)
converter=PROFILE_ROOT/'danmaku';converter.mkdir(parents=True,exist_ok=True)
shutil.copy2(APP/'animejanai/danmaku/DanmakuFactory.exe',converter/'DanmakuFactory.exe')
(PROFILE_ROOT/'animejanai.conf').write_text(
    '[global]\ndefault_slot=1\n'+''.join(
        f'\n[slot_{index}]\nprofile_name=Fixture {index}\n'
        for index in range(1,10)),encoding='utf-8')
comments_fixture=OUT/'comments.xml'
comments_fixture.write_text(
    '<i><d p="0,5,26,16777215">弹幕与字幕独立显示</d>'
    '<d p="0,1,26,16777215">滚动弹幕</d>'
    '<d p="0,4,26,16777215">固定弹幕</d></i>',encoding='utf-8')
test_env=os.environ.copy();test_env['LOCALAPPDATA']=str(PRIVATE)
results=[]
def run(name,args,marker,timeout=90):
    try:
        p=subprocess.run([str(APP/'app/mpv.exe'),*args],capture_output=True,timeout=timeout,env=test_env)
        log=p.stdout+p.stderr
        passed=p.returncode==0 and marker in log and b'stack traceback' not in log.lower()
        result={'case':name,'passed':passed,'exit_code':p.returncode}
    except subprocess.TimeoutExpired as e:
        log=(e.stdout or b'')+(e.stderr or b'');result={'case':name,'passed':False,'error':'timeout'}
    (OUT/(name+'.log')).write_bytes(log);results.append(result)
    if not result['passed']:print(log[-8000:].decode('utf-8',errors='replace'))
base=['--load-scripts=no','--osc=no','--ao=null','--hwdec=no','--vf=','--idle=yes']
run('player_ui-logic',['--no-config',*base,'--vo=null','--script='+str(ROOT/'tests/test_player_ui_logic.lua'),
    '--script-opts=playeruiroot='+str(ROOT)+',playeruiout='+str(OUT)],b'PASS Player UI logic:')
header=b'YUV4MPEG2 W320 H180 F24:1 Ip A1:1 C420jpeg\n'
for name,value in [('first.y4m',65),('second.y4m',85)]:
    frame=b'FRAME\n'+bytes([value])*(320*180)+bytes([128])*(320*180//2)
    with (OUT/name).open('wb') as f:
        f.write(header)
        for _ in range(24*15):f.write(frame)
with wave.open(str(OUT/'audio.wav'),'wb') as f:
    f.setnchannels(1);f.setsampwidth(2);f.setframerate(8000);f.writeframes(b'\0\0'*8000*15)
(OUT/'subtitle.srt').write_text('1\n00:00:00,000 --> 00:00:12,000\n中文字幕测试\n',encoding='utf-8')
scripts=[APP/'portable_config/scripts'/x for x in ['player_ui.lua','player_ui_danmaku.lua','animejanaistats.lua','animejanai_slot.lua']]
scripts.append(ROOT/'tests/test_player_ui_runtime.lua')
for p in scripts:assert p.is_file(),p
player_ui_source=(APP/'portable_config/scripts/player_ui.lua').read_text(encoding='utf-8')
danmaku_source=(APP/'portable_config/scripts/player_ui_danmaku.lua').read_text(encoding='utf-8')
assert 'mp.input.get' not in danmaku_source and 'console_opt_overrides' not in danmaku_source
assert "id='close'" not in player_ui_source and "id=='close'" not in player_ui_source
player_config=(APP/'portable_config/mpv.conf').read_text(encoding='utf-8')
assert 'osc=yes' in player_config
assert 'border=yes' in player_config
assert 'title-bar=yes' in player_config
assert 'fullscreen=no' in player_config
assert 'window-maximized=no' in player_config
assert 'osd-on-seek=no' in player_config
osc_config=(APP/'portable_config/script-opts/osc.conf').read_text(encoding='utf-8')
assert all(option in osc_config for option in ['idlescreen=yes','showwindowed=no','showfullscreen=no','windowcontrols=no'])
assert not (APP/'portable_config/scripts/modernx.lua').exists(),'Two control bars packaged'
# Exercise the installed scripts with a fresh configuration root and isolated
# private-source directory. The only source is a reserved .invalid test endpoint.
run('player_ui-windows-ui',base+['--vo=gpu','--gpu-api=d3d11','--gpu-context=d3d11','--d3d11-warp=yes',
    '--hidpi-window-scale=yes','--geometry=1280x720','--border=no','--pause=yes','--keep-open=yes',
    '--config-dir='+str(CONFIG),'--input-conf='+str(CONFIG/'input.conf'),
    '--scripts='+';'.join(map(str,scripts)),
    '--script-opts=playeruiout='+str(OUT)+',player_ui_danmaku-autoload_danmaku=no,player_ui_danmaku-autoload_danmaku_matches=no,player_ui_danmaku-dandanplay_app_id=,player_ui_danmaku-dandanplay_app_secret=',
    '--force-media-title=播放器界面测试','--sub-auto=no','--sub-file='+str(OUT/'subtitle.srt'),
    '--audio-file='+str(OUT/'audio.wav'),str(OUT/'first.y4m')],b'PASS Player UI Windows UI:')
(OUT/'results.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8')
for p in OUT.glob('*.y4m'):p.unlink()
(OUT/'audio.wav').unlink(missing_ok=True)
print(json.dumps(results,ensure_ascii=False,indent=2))
if not all(r['passed'] for r in results):raise SystemExit(1)

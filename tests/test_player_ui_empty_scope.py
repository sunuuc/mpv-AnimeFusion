"""Reproduce the external handoff shapes seen in Player UI diagnostics.

The startup script is global while its media-related options are inside an
otherwise empty mpv --{ ... --} scope. Media is served only from loopback.
The playlist case deliberately puts the wrong episode first and verifies that
--playlist-start selects the requested item before any media request is made.
"""
from pathlib import Path
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit
import json, os, shutil, subprocess, sys, threading, time

SOURCE=Path(sys.argv[1]).resolve();OUT=Path(sys.argv[2]).resolve();OUT.mkdir(parents=True,exist_ok=True)
APP=OUT/'独立 Player UI 播放器';shutil.copytree(SOURCE,APP)
CALLER=OUT/'Player UI 调用目录';CALLER.mkdir()
ENV=os.environ.copy()
for key in ('ANIMEVE_HOME','MPV_HOME','ANIMEJANAI_ROOT','ANIMEJANAI_DATA_DIR','_started_from_console'):
    ENV.pop(key,None)
ENV['PATH']=os.environ['SystemRoot']+r'\System32'
ENV['ANIMEVE_HOME']=str(OUT/'已删除的旧版目录'/'portable_config')
ENV['DOTNET_ROOT']=str(OUT/'不存在的 dotnet');ENV['DOTNET_ROOT_X64']=ENV['DOTNET_ROOT']
ENV['DOTNET_MULTILEVEL_LOOKUP']='0';ENV['DOTNET_BUNDLE_EXTRACT_BASE_DIR']=str(OUT/'bundles')
MEDIA=b'YUV4MPEG2 W160 H90 F24:1 Ip A1:1 C420jpeg\n'+(b'FRAME\n'+b'\x60'*(160*90)+b'\x80'*(160*90//2))*24*8
REQUESTS=[];PLAYLISTS={};LOCK=threading.Lock();RESULTS=[]

class Handler(BaseHTTPRequestHandler):
    protocol_version='HTTP/1.1'
    def log_message(self,*args):pass
    def do_GET(self):
        path=urlsplit(self.path).path
        with LOCK:REQUESTS.append({'path':path,'method':'GET'})
        body=PLAYLISTS.get(path,MEDIA)
        content_type='audio/x-mpegurl' if path in PLAYLISTS else 'application/octet-stream'
        self.send_response(200);self.send_header('Content-Type',content_type)
        self.send_header('Content-Length',str(len(body)));self.end_headers()
        try:self.wfile.write(body)
        except (BrokenPipeError,ConnectionResetError,ConnectionAbortedError,OSError):pass

server=ThreadingHTTPServer(('127.0.0.1',0),Handler);server.daemon_threads=True
threading.Thread(target=server.serve_forever,daemon=True).start()
BASE=f'http://127.0.0.1:{server.server_port}'
BOOT=CALLER/'Player UI外部启动.lua'
BOOT.write_text("""local mp=require 'mp'
local options=require 'mp.options'
local utils=require 'mp.utils'
local o={url='',tag='playlist'}
options.read_options(o,'handoff')
local marker=io.open('script-started.txt','a');if marker then marker:write(o.tag..'\\n');marker:close() end
mp.register_event('file-loaded',function()
    local f=assert(io.open('handoff-result-'..o.tag..'.json','w'))
    f:write(utils.format_json({
        loaded=true,tag=o.tag,path=mp.get_property('path'),
        title=mp.get_property('media-title'),
        playlist_pos=mp.get_property_number('playlist-pos',-1),
        playlist_count=mp.get_property_number('playlist-count',0),
        frames=mp.get_property_number('vo-presented-frame-count',0)
    }));f:close()
    mp.add_timeout(.35,function()mp.commandv('quit')end)
end)
if o.url~='' then mp.commandv('loadfile',o.url,'replace') end
mp.add_timeout(8,function()
    local f=io.open('handoff-timeout.json','w');if f then f:write(utils.format_json({tag=o.tag,has_url=o.url~=''}));f:close() end
    mp.commandv('quit')
end)
""",encoding='utf-8')
FLAGS=['--vo=gpu','--gpu-api=d3d11','--gpu-context=d3d11','--d3d11-warp=yes','--hwdec=no','--vf=',
       '--ao=null','--idle=yes','--keep-open=no','--force-window=yes','--geometry=960x540',
       '--terminal=yes','--save-position-on-quit=no','--resume-playback=no']

def count(path,method=None):
    with LOCK:return sum(r['path']==path and (method is None or r['method']==method) for r in REQUESTS)

def read_diagnostic(tag):
    diagnostic=APP/'portable_config/startup-diagnostic.json'
    assert diagnostic.is_file(),(tag,'missing safe diagnostic')
    report=json.loads(diagnostic.read_text(encoding='utf-8-sig'))
    assert report['version']>=3,report
    assert report['scoped_playlist'] and report['media_arguments']==0,report
    assert report['empty_scope_count']==1 and report['legacy_script_handoff'],report
    assert report['configuration']=='portable' and report['file_loaded'] and report['has_media'],report
    assert BASE not in json.dumps(report),(tag,'diagnostic leaked URL')
    (OUT/f'{tag}-diagnostic.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    return report

def run_process(tag,args):
    for p in CALLER.glob('handoff-result-*.json'):p.unlink()
    (CALLER/'handoff-timeout.json').unlink(missing_ok=True)
    (APP/'portable_config/startup-diagnostic.json').unlink(missing_ok=True)
    started=time.monotonic()
    log_path=OUT/f'{tag}.log'
    with log_path.open('wb') as log:
        cp=subprocess.run([str(APP/'mpv-AnimeFusion.exe'),*FLAGS,*args],cwd=CALLER,env=ENV,
                          stdout=log,stderr=subprocess.STDOUT,timeout=15)
    log_text=log_path.read_text(encoding='utf-8',errors='replace')
    assert 'Lua error' not in log_text,(tag,'production Lua error',log_text[-2000:])
    assert 'script-modules' not in log_text,(tag,'runtime attempted external module load',log_text[-2000:])
    assert cp.returncode==0,(tag,cp.returncode)
    return round(time.monotonic()-started,3)

def run_script_case(tag,option_name):
    path=f'/{tag}.y4m';uri=BASE+path
    payload=f'handoff-url={uri},handoff-tag={tag}'
    elapsed=run_process(tag,['--script='+str(BOOT),'--{',f'--{option_name}='+payload,'--}'])
    result=CALLER/f'handoff-result-{tag}.json'
    assert result.is_file(),(tag,'startup script did not receive its URL options')
    data=json.loads(result.read_text(encoding='utf-8-sig'))
    assert data['loaded'] and data['tag']==tag and data['path']==uri,data
    assert count(path)==1,(tag,'media request count',count(path))
    report=read_diagnostic(tag)
    assert report['empty_scope_options_promoted']==1 and report['script_payload_options']==1,report
    assert report['script_options']==1 and not report['playlist_option'],report
    return {'case':tag,'passed':True,'option':option_name,'requests':count(path),
            'first_playback_seconds':elapsed,'previous_installation':False,'system_only_path':True,
            'unicode_portable_path':True,'production_lua_errors':0}

def run_playlist_case():
    tag='player_ui-playlist-selected'
    playlist_path='/player_ui/episodes.m3u'
    wrong_path='/never/wrong-episode.y4m'
    selected_path='/media/selected-episode.y4m'
    wrong=BASE+wrong_path;selected=BASE+selected_path
    PLAYLISTS[playlist_path]=('#EXTM3U\n'+wrong+'\n'+selected+'\n').encode()
    elapsed=run_process(tag,[
        '--script='+str(BOOT),'--{',
        '--playlist='+BASE+playlist_path,
        '--playlist-start=1',
        '--force-media-title=已选择的第二集',
        '--}'
    ])
    result=CALLER/'handoff-result-playlist.json'
    assert result.is_file(),(tag,'playlist did not start')
    data=json.loads(result.read_text(encoding='utf-8-sig'))
    assert data['loaded'] and data['path']==selected,data
    assert data['playlist_pos']==1 and data['playlist_count']==2,data
    assert data['title']=='已选择的第二集',data
    assert count(wrong_path)==0,(tag,'wrong episode was contacted',count(wrong_path))
    assert count(selected_path)==1,(tag,'selected media request count',count(selected_path))
    assert count(playlist_path)==1,(tag,'playlist request count',count(playlist_path))
    report=read_diagnostic(tag)
    assert report['empty_scope_options_promoted']==3,report
    assert report['playlist_option_count']==1 and report['playlist_start_option_count']==1,report
    assert report['playlist_start_kind']=='index' and report['playlist_start_index']==1,report
    assert report['native_playlist_handoff'] and report['playlist_current_pos']==1,report
    assert report['playlist_count']==2 and report['script_payload_options']==0,report
    assert report['promoted_option_names']==['force-media-title','playlist','playlist-start'],report
    return {'case':tag,'passed':True,'option':'playlist','playlist_start':1,
            'wrong_episode_requests':count(wrong_path),'selected_episode_requests':count(selected_path),
            'playlist_requests':count(playlist_path),'first_playback_seconds':elapsed,
            'previous_installation':False,'system_only_path':True,
            'unicode_portable_path':True,'production_lua_errors':0}

try:
    for tag,option in [('player_ui-legacy-script-opt','script-opt'),('player_ui-base-script-opts','script-opts')]:
        try:
            item=run_script_case(tag,option);RESULTS.append(item);print('PASS',tag,flush=True)
        except Exception as e:
            RESULTS.append({'case':tag,'passed':False,'error':repr(e)});print('FAIL',tag,repr(e),flush=True)
    try:
        item=run_playlist_case();RESULTS.append(item);print('PASS',item['case'],flush=True)
    except Exception as e:
        RESULTS.append({'case':'player_ui-playlist-selected','passed':False,'error':repr(e)})
        print('FAIL player_ui-playlist-selected',repr(e),flush=True)
finally:
    server.shutdown();server.server_close()
    (OUT/'results.json').write_text(json.dumps(RESULTS,ensure_ascii=False,indent=2),encoding='utf-8')
    (OUT/'requests.json').write_text(json.dumps(REQUESTS,ensure_ascii=False,indent=2),encoding='utf-8')
    shutil.rmtree(APP,ignore_errors=True);shutil.rmtree(OUT/'bundles',ignore_errors=True);shutil.rmtree(CALLER,ignore_errors=True)
print(json.dumps(RESULTS,ensure_ascii=False,indent=2))
if len(RESULTS)!=3 or not all(x['passed'] for x in RESULTS):raise SystemExit(1)

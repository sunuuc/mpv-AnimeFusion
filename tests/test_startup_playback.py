"""Production startup regression: no previous installation, PATH or config-dir override.
Media fixtures are local. The HTTP server binds loopback only.
Rendering is software-only; this does not claim to test GPU inference.
"""
from pathlib import Path
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit
import ctypes as C
from ctypes import wintypes as W
import json, os, shutil, subprocess, sys, threading, time, uuid, msvcrt

SOURCE=Path(sys.argv[1]).resolve();OUT=Path(sys.argv[2]).resolve();OUT.mkdir(parents=True,exist_ok=True)
APP=OUT/'独立 player';shutil.copytree(SOURCE,APP)
CALLER=OUT/'caller 工作目录';CALLER.mkdir()
ENV=os.environ.copy()
for key in ('ANIMEVE_HOME','MPV_HOME','ANIMEJANAI_ROOT','ANIMEJANAI_DATA_DIR','_started_from_console'):
    ENV.pop(key,None)
ENV['PATH']=os.environ['SystemRoot']+r'\System32'
ENV['ANIMEVE_HOME']=str(OUT/'deleted-original'/'portable_config')
ENV['DOTNET_ROOT']=str(OUT/'no-dotnet');ENV['DOTNET_ROOT_X64']=ENV['DOTNET_ROOT']
ENV['DOTNET_MULTILEVEL_LOOKUP']='0';ENV['DOTNET_BUNDLE_EXTRACT_BASE_DIR']=str(OUT/'bundles')
MEDIA=b'YUV4MPEG2 W160 H90 F24:1 Ip A1:1 C420jpeg\n'+(b'FRAME\n'+b'\x60'*(160*90)+b'\x80'*(160*90//2))*24*12
REQUESTS=[];RESULTS=[];LOCK=threading.Lock()
class Handler(BaseHTTPRequestHandler):
    protocol_version='HTTP/1.1'
    def log_message(self,*args):pass
    def do_GET(self):
        with LOCK: REQUESTS.append({'path':urlsplit(self.path).path,'method':'GET'})
        self.send_response(200);self.send_header('Content-Type','application/octet-stream')
        self.send_header('Content-Length',str(len(MEDIA)));self.end_headers()
        try:self.wfile.write(MEDIA)
        except (BrokenPipeError,ConnectionResetError,ConnectionAbortedError,OSError):pass
server=ThreadingHTTPServer(('127.0.0.1',0),Handler);server.daemon_threads=True
threading.Thread(target=server.serve_forever,daemon=True).start()
BASE=f'http://127.0.0.1:{server.server_port}'
FLAGS=['--vo=null','--hwdec=no','--vf=',
       '--ao=null','--idle=yes','--keep-open=yes','--pause=no',
       '--terminal=yes','--save-position-on-quit=no','--resume-playback=no']
BOOT=CALLER/'启动脚本.lua'
BOOT.write_text("""local mp=require 'mp'
local options=require 'mp.options'
local o={url='',tag=''}
options.read_options(o,'handoff')
mp.set_property_native('user-data/startup-test/options',{
    tag=o.tag,has_url=o.url~='',vo=mp.get_property('vo'),
    hwdec=mp.get_property('hwdec'),filters=mp.get_property_native('vf')})
mp.register_event('file-loaded',function()
    -- Publish the fixture result only after the complete JSON has been closed.
    local f=assert(io.open('handoff-result.json.tmp','w'))
    f:write(require('mp.utils').format_json({loaded=true,tag=o.tag,path=mp.get_property('path')}));f:close()
    os.remove('handoff-result.json')
    assert(os.rename('handoff-result.json.tmp','handoff-result.json'))
end)
if o.url~='' then mp.commandv('loadfile',o.url,'replace') end
""",encoding='utf-8')
EXTRA=CALLER/'observer.lua'
EXTRA.write_text("require('mp').set_property_bool('user-data/startup-test/observer',true)\n",encoding='utf-8')
SUB1=CALLER/'字幕 一.srt';SUB2=CALLER/'字幕 二.srt'
for p in (SUB1,SUB2):p.write_text('1\n00:00:00,000 --> 00:00:10,000\n'+p.stem+'\n',encoding='utf-8')

def stop(p):
    if p.poll() is None:
        p.terminate()
        try:p.wait(5)
        except subprocess.TimeoutExpired:p.kill();p.wait(5)
class Player:
    def __init__(self,args,name,exe='mpv-AnimeFusion.exe',ipc=True):
        self.fp=None;self.proc=None;self.seq=0;self.buffer=b'';self.log=(OUT/(name+'.log')).open('wb')
        self.name=name;self.pipe=r'\\.\pipe\startup-'+uuid.uuid4().hex
        flags=FLAGS+(['--input-ipc-server='+self.pipe] if ipc else [])
        self.started=time.monotonic()
        self.proc=subprocess.Popen([str(APP/('app/'+exe if exe=='mpv.exe' else exe)),*flags,*args],cwd=CALLER,env=ENV,stdout=self.log,stderr=self.log)
        if ipc:
            try:
                deadline=time.monotonic()+15
                while time.monotonic()<deadline:
                    if self.proc.poll() is not None:raise RuntimeError('process exited before IPC')
                    try:self.fp=open(self.pipe,'r+b',buffering=0);break
                    except OSError:time.sleep(.005)
                if self.fp is None:raise TimeoutError('no IPC listener')
                self.handle=W.HANDLE(msvcrt.get_osfhandle(self.fp.fileno()))
                self.peek=C.WinDLL('kernel32',use_last_error=True).PeekNamedPipe
                self.peek.argtypes=[W.HANDLE,C.c_void_p,W.DWORD,C.POINTER(W.DWORD),C.POINTER(W.DWORD),C.POINTER(W.DWORD)]
                self.peek.restype=W.BOOL
            except Exception:self.close();raise
    def command(self,*args):
        self.seq+=1
        self.fp.write((json.dumps({'command':args,'request_id':self.seq},ensure_ascii=False)+'\n').encode())
        deadline=time.monotonic()+6
        while time.monotonic()<deadline:
            while b'\n' in self.buffer:
                raw,self.buffer=self.buffer.split(b'\n',1);reply=json.loads(raw)
                if reply.get('request_id')==self.seq:
                    if reply.get('error')!='success':raise RuntimeError(str(reply))
                    return reply.get('data')
            available=W.DWORD()
            if not self.peek(self.handle,None,0,None,C.byref(available),None):raise C.WinError(C.get_last_error())
            if available.value:self.buffer+=self.fp.read(min(available.value,65536))
            elif self.proc.poll() is not None:raise RuntimeError('process exited during IPC')
            else:time.sleep(.005)
        raise TimeoutError('IPC timeout')
    def get(self,name,default=None):
        try:return self.command('get_property',name)
        except RuntimeError:return default
    def wait(self,fn,label,timeout=10):
        end=time.monotonic()+timeout
        while time.monotonic()<end:
            if fn():return
            time.sleep(.02)
        raise AssertionError(label)
    def playing(self,uri):
        self.wait(lambda:self.get('path')==uri and self.get('time-pos') is not None and (self.get('vo-presented-frame-count',0) or 0)>0,'handoff never started video')
        start=self.get('time-pos',0)
        self.wait(lambda:(self.get('time-pos',0) or 0)>start+.15,'handoff did not advance frames')
        return round(time.monotonic()-self.started,3)
    def close(self):
        if self.fp:self.fp.close();self.fp=None
        if self.proc:stop(self.proc)
        self.log.close()
    def __enter__(self):return self
    def __exit__(self,*args):self.close()

def count(path):
    with LOCK:return sum(r['path']==path for r in REQUESTS)
def case(name,fn):
    try:detail=fn() or {};RESULTS.append({'case':name,'passed':True,**detail});print('PASS',name,flush=True)
    except Exception as e:RESULTS.append({'case':name,'passed':False,'error':repr(e)});print('FAIL',name,repr(e),flush=True)
def options(uri,native=False):
    tail=['--script-opts-append=handoff-tag=ready'] if native else ['--script-opts-append','handoff-tag=ready']
    return ['--script-opts=handoff-url='+uri,'--script-opt=handoff-tag=first',*tail]

def script_launch(exe,flag,name,repeated=False):
    path='/'+name+'.y4m';uri=BASE+path
    args=[flag+BOOT.name,*options(uri,exe=='mpv.exe')]
    if repeated:args+=['--script='+EXTRA.name,'--sub-file='+SUB1.name,'--sub-file='+SUB2.name]
    with Player(args,name,exe) as p:
        elapsed=p.playing(uri)
        if exe=='mpv-AnimeFusion.exe':
            p.wait(lambda:p.get('user-data/player_ui/ui',{}).get('version')=='1.3.0',
                   'production scripts were not automatically loaded')
            assert p.get('file-local-options/cache-pause-initial') is False, \
                'production network guard was not applied'
        assert p.get('user-data/startup-test/options',{}).get('tag')=='ready','script options arrived after script startup'
        startup=p.get('user-data/startup-test/options',{})
        assert startup.get('vo')=='null' and startup.get('hwdec')=='no' and startup.get('filters')==[], \
            'portable configuration overrode caller options before startup scripts'
        if repeated:
            assert p.get('user-data/startup-test/observer') is True,'one of repeated script options was lost'
            assert len([t for t in p.get('track-list',[]) if t['type']=='sub'])==2,'repeated external subtitles were lost'
        assert p.get('prefetch-playlist') is False,'network guard was not loaded from portable config'
        assert count(path)==1,'handoff reopened the media'
        if exe=='mpv-AnimeFusion.exe':
            report=json.loads((APP/'portable_config/startup-diagnostic.json').read_text(encoding='utf-8-sig'))
            assert report['media_arguments']==0 and report['script_options']>=1 and report['missing_scripts']==0
            assert report['configuration']=='portable' and report['file_loaded']
            assert BASE not in json.dumps(report),'startup diagnostic leaked a URL'
            (OUT/(name+'-diagnostic.json')).write_text(json.dumps(report,indent=2),encoding='utf-8')
        return {'first_playback_seconds':elapsed,'requests':count(path),'previous_installation':False,'config_dir_override':False,'system_only_path':True}

def immediate_ipc():
    counts=[]
    for i in range(3):
        path=f'/ipc-{i}.y4m';uri=BASE+path
        with Player([],f'immediate-ipc-{i}') as p:
            p.command('loadfile',uri,'replace');p.playing(uri)
            assert count(path)==1
            counts.append(count(path))
    return {'connection_resets':0,'requests_each':counts}

def dedicated_script():
    result=CALLER/'handoff-result.json';result.unlink(missing_ok=True)
    path='/dedicated.y4m';uri=BASE+path
    with Player([], 'existing-idle') as keeper:
        keeper.wait(lambda:keeper.get('idle-active') is True,'idle instance did not initialize')
        with Player(['--script='+BOOT.name,*options(uri)],'new-script-no-ipc',ipc=False) as child:
            end=time.monotonic()+10
            while time.monotonic()<end and not result.exists():
                assert child.proc.poll() is None,'external script request was forwarded away'
                time.sleep(.02)
            assert result.exists(),'second process did not execute its startup script'
            data=json.loads(result.read_text(encoding='utf-8-sig'));assert data['loaded'] and data['path']==uri and data['tag']=='ready'
            assert keeper.get('idle-active') is True,'unrelated idle instance stole media'
            assert count(path)==1
    return {'script_kept_in_own_process':True,'requests':count(path)}

def direct_cli():
    path='/direct.y4m';uri=BASE+path
    with Player([uri,'--volume=63','--mute=yes'],'direct-cli') as p:
        p.playing(uri);assert p.get('volume')==63 and p.get('mute') is True,'remembered settings overrode caller'
        assert count(path)==1
    return {'requests':count(path)}

try:
    case('native-script',lambda:script_launch('mpv.exe','--script=','native-script'))
    case('frontend-script',lambda:script_launch('mpv-AnimeFusion.exe','--script=','frontend-script'))
    case('frontend-scripts',lambda:script_launch('mpv-AnimeFusion.exe','--scripts=','frontend-scripts'))
    case('repeated-scripts-subtitles',lambda:script_launch('mpv-AnimeFusion.exe','--script=','repeated-scripts',True))
    case('immediate-ipc',immediate_ipc)
    case('script-with-existing-window',dedicated_script)
    case('direct-cli',direct_cli)
finally:
    server.shutdown();server.server_close()
    (OUT/'results.json').write_text(json.dumps(RESULTS,ensure_ascii=False,indent=2),encoding='utf-8')
    (OUT/'requests.json').write_text(json.dumps(REQUESTS,ensure_ascii=False,indent=2),encoding='utf-8')
    shutil.rmtree(APP,ignore_errors=True);shutil.rmtree(OUT/'bundles',ignore_errors=True)
    shutil.rmtree(CALLER,ignore_errors=True)
print(json.dumps(RESULTS,ensure_ascii=False,indent=2))
if len(RESULTS)!=7 or not all(r['passed'] for r in RESULTS):raise SystemExit(1)

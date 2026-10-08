"""Validate exact unpacked deliverables without using a previous player/.NET install."""
from pathlib import Path
import ctypes as C
from ctypes import wintypes as W
import json, os, subprocess, sys, time, traceback, uuid
from runtime_probe import verify_process

APP=Path(sys.argv[1]).resolve();OUT=Path(sys.argv[2]).resolve();OUT.mkdir(parents=True,exist_ok=True)
ROOT=Path(__file__).resolve().parents[2]
results=[]
def record(name,fn):
    try:detail=fn() or {};results.append({'case':name,'passed':True,**detail})
    except Exception as e:
        (OUT/(name+'-failure.log')).write_text(traceback.format_exc(),encoding='utf-8')
        results.append({'case':name,'passed':False,'error':repr(e)})
    print(json.dumps(results[-1],ensure_ascii=False),flush=True)
ENV=os.environ.copy();empty=OUT/'no-dotnet';empty.mkdir(exist_ok=True)
ENV.update({'DOTNET_ROOT':str(empty),'DOTNET_ROOT_X64':str(empty),'DOTNET_MULTILEVEL_LOOKUP':'0',
    'DOTNET_BUNDLE_EXTRACT_BASE_DIR':str(OUT/'bundles')})
for key in ('ANIMEVE_HOME','MPV_HOME','ANIMEJANAI_ROOT','ANIMEJANAI_DATA_DIR'):ENV.pop(key,None)
ENV['PATH']=str(APP)+os.pathsep+str(APP/'animejanai/inference')+os.pathsep+os.environ.get('SystemRoot',r'C:\Windows')+r'\System32'

def stop(proc):
    if proc.poll() is None:
        proc.terminate()
        try:proc.wait(5)
        except subprocess.TimeoutExpired:proc.kill();proc.wait(5)

def components():
    cp=subprocess.run([str(APP/'app/mpv-AnimeFusionUpdater.exe'),'--components','--json'],env=ENV,cwd=OUT,capture_output=True,timeout=40)
    (OUT/'components.log').write_bytes(cp.stdout+cp.stderr)
    assert cp.returncode==0,cp.stderr
    data=json.loads(cp.stdout)
    assert data['offline'] and {'trt-runtime','trt-sm120','rife'} <= {x['name'] for x in data['packs']},data
    assert not any(x['installed'] for x in data['packs']),data
    assert any(x['name'].startswith('upscale-model-') for x in data['packs']),data
    return {'packs':[x['name'] for x in data['packs']]}

def no_release_update_logic():
    script=APP/'portable_config/scripts/animejanai_update.lua'
    assert not script.exists(),script
    for rel in ('portable_config/input.conf','portable_config/input-animejanai.conf'):
        text=(APP/rel).read_text(encoding='utf-8-sig').lower()
        assert 'animejanai-update' not in text,rel
        assert 'ctrl+u' not in text,rel
    checks={}
    for arg in ('--check','--open-releases'):
        cp=subprocess.run([str(APP/'app/mpv-AnimeFusionUpdater.exe'),arg],env=ENV,cwd=OUT,capture_output=True,timeout=30)
        log=cp.stdout+cp.stderr
        (OUT/('unsupported-'+arg[2:]+'.log')).write_bytes(log)
        assert cp.returncode==2,(arg,cp.returncode,log)
        assert b'github.com' not in log.lower(),(arg,log)
        checks[arg]=cp.returncode
    return {'release_page_shortcut':False,'application_update_command':False,'unsupported':checks}

sample=OUT/'blank.y4m';sample.write_bytes(b'YUV4MPEG2 W16 H16 F24:1 Ip A1:1 C420jpeg\n'+(b'FRAME\n'+bytes([100])*256+bytes([128])*128)*24*20)
def production_scripts():
    cp=subprocess.run([str(APP/'app/mpv.exe'),'--config-dir='+str(APP/'portable_config'),
        '--vo=null','--ao=null','--hwdec=no','--vf=','--idle=yes','--osc=no',
        '--scripts='+str(ROOT/'tools/standalone/full_smoke.lua'),str(sample)],env=ENV,cwd=OUT,capture_output=True,timeout=30)
    log=cp.stdout+cp.stderr;(OUT/'production-scripts.log').write_bytes(log)
    assert cp.returncode==0 and b'PASS empty-directory full install:' in log,log[-7000:]
    assert b'stack traceback' not in log.lower(),log[-7000:]
    return {'cpu_playback':True,'gpu_inference':False}

class JsonPipe:
    """Bounded IPC reads: a dead frontend must fail rather than hang the job."""
    def __init__(self,fp,proc):
        import msvcrt
        self.fp,self.proc,self.buffer,self.seq=fp,proc,b'',0
        self.handle=W.HANDLE(msvcrt.get_osfhandle(fp.fileno()))
        self.peek=C.WinDLL('kernel32',use_last_error=True).PeekNamedPipe
        self.peek.argtypes=[W.HANDLE,C.c_void_p,W.DWORD,C.POINTER(W.DWORD),C.POINTER(W.DWORD),C.POINTER(W.DWORD)]
        self.peek.restype=W.BOOL
    def get(self,name,allow_unavailable=False):
        self.seq+=1
        self.fp.write((json.dumps({'command':['get_property',name],'request_id':self.seq})+'\n').encode())
        until=time.monotonic()+10
        while time.monotonic()<until:
            while b'\n' in self.buffer:
                line,self.buffer=self.buffer.split(b'\n',1)
                data=json.loads(line)
                if data.get('request_id')==self.seq:
                    error=data.get('error')
                    if error=='success':return data.get('data')
                    if allow_unavailable and error=='property unavailable':return None
                    raise AssertionError(data)
            if self.proc.poll() is not None:raise RuntimeError('Player exited during IPC')
            available=W.DWORD()
            if not self.peek(self.handle,None,0,None,C.byref(available),None):
                raise C.WinError(C.get_last_error())
            if available.value:
                self.buffer+=self.fp.read(min(available.value,65536))
                if len(self.buffer)>1024*1024:raise RuntimeError('Oversized IPC response')
            else:time.sleep(.02)
        raise TimeoutError('No IPC response for '+name)

def frontend():
    pipe=r'\\.\pipe\ajn-full-'+uuid.uuid4().hex
    args=[str(APP/'AnimeVE.exe'),'--config-dir='+str(APP/'portable_config'),'--vo=null','--ao=null',
        '--hwdec=no','--vf=','--idle=yes','--input-ipc-server='+pipe,'--log-file='+str(OUT/'frontend.mpv.log'),str(sample)]
    with (OUT/'frontend.console.log').open('wb') as console:
        proc=subprocess.Popen(args,env=ENV,cwd=OUT,stdout=console,stderr=subprocess.STDOUT)
        f=None
        try:
            end=time.monotonic()+35
            while time.monotonic()<end:
                assert proc.poll() is None,('Player exited',proc.returncode)
                try:f=open(pipe,'r+b',buffering=0);break
                except OSError:time.sleep(.15)
            assert f is not None,'No IPC from self-contained player'
            ipc=JsonPipe(f,proc)
            end=time.monotonic()+12
            n0=None
            while time.monotonic()<end:
                value=ipc.get('vo-presented-frame-count',allow_unavailable=True)
                if isinstance(value,(int,float)) and value>0:
                    n0=value;break
                time.sleep(.1)
            if n0 is None:raise RuntimeError('Video output property never became available or no frames were presented')
            end=time.monotonic()+5
            path=None
            while time.monotonic()<end:
                path=ipc.get('path',allow_unavailable=True)
                if path:break
                time.sleep(.05)
            assert path and Path(path).name==sample.name,path
            end=time.monotonic()+3
            advanced=False
            while time.monotonic()<end:
                value=ipc.get('vo-presented-frame-count',allow_unavailable=True)
                if isinstance(value,(int,float)) and value>n0:
                    advanced=True;break
                time.sleep(.1)
            assert advanced,'Video stopped advancing'
            detail=verify_process(proc.pid,APP/'AnimeVE.exe',APP,OUT/'bundles',OUT/'self-contained-player-modules.json')
            return {**detail,'started_outside_install_directory':True,'video_frames_advancing':True}
        finally:
            if f:f.close()
            stop(proc)

def windows_for(pid):
    user=C.WinDLL('user32',use_last_error=True)
    callback_type=C.WINFUNCTYPE(W.BOOL,W.HWND,W.LPARAM)
    user.EnumWindows.argtypes=[callback_type,W.LPARAM];user.EnumWindows.restype=W.BOOL
    user.GetWindowThreadProcessId.argtypes=[W.HWND,C.POINTER(W.DWORD)];user.GetWindowThreadProcessId.restype=W.DWORD
    user.IsWindowVisible.argtypes=[W.HWND];user.IsWindowVisible.restype=W.BOOL
    user.GetWindowTextW.argtypes=[W.HWND,W.LPWSTR,C.c_int];user.GetWindowTextW.restype=C.c_int
    windows=[]
    @callback_type
    def visit(hwnd,param):
        owner=W.DWORD();user.GetWindowThreadProcessId(hwnd,C.byref(owner))
        if owner.value==pid and user.IsWindowVisible(hwnd):
            text=C.create_unicode_buffer(2048);user.GetWindowTextW(hwnd,text,len(text))
            windows.append(text.value)
        return True
    if not user.EnumWindows(visit,0):raise C.WinError(C.get_last_error())
    return windows

def manager():
    with (OUT/'manager.console.log').open('wb') as console:
        proc=subprocess.Popen([str(APP/'AnimeVEManager.exe')],env=ENV,cwd=OUT,stdout=console,stderr=subprocess.STDOUT)
        try:
            end=time.monotonic()+20;windows=[]
            while time.monotonic()<end:
                assert proc.poll() is None,('Manager exited',proc.returncode)
                windows=windows_for(proc.pid)
                if any(title in ('AnimeVE Manager', 'AnimeVE 管理器') for title in windows):break
                time.sleep(.2)
            else:raise RuntimeError('Manager did not create its visible main window: '+repr(windows))
            detail=verify_process(proc.pid,APP/'AnimeVEManager.exe',APP,OUT/'bundles',OUT/'self-contained-manager-modules.json')
            return {**detail,'launched':True,'visible_windows':windows,'external_dotnet_disabled':True}
        finally:stop(proc)

record('offline-included-components',components)
record('no-release-update-logic',no_release_update_logic)
record('all-production-scripts-on-local-video',production_scripts)
record('self-contained-player',frontend)
record('self-contained-manager',manager)
(OUT/'results.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8')
if not all(r['passed'] for r in results):raise SystemExit(1)

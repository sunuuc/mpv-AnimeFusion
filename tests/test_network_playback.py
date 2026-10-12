"""Loopback-only production frontend test. No user accounts or external media servers."""
from pathlib import Path
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit
from capture_window import capture_client
import concurrent.futures, json, os, subprocess, sys, threading, time, uuid
ROOT=Path(__file__).resolve().parents[1]
APP=Path(sys.argv[1]).resolve();OUT=Path(sys.argv[2]).resolve();OUT.mkdir(parents=True,exist_ok=True)
RESULTS=[];REQUESTS=[];LOCK=threading.Lock();ACTIVE={};MAX_ACTIVE={}
FRAME=b'FRAME\n'+b'\x60'*(160*90)+b'\x80'*(160*90//2)
MEDIA=b'YUV4MPEG2 W160 H90 F24:1 Ip A1:1 C420jpeg\n'+FRAME*(24*30)
class Handler(BaseHTTPRequestHandler):
    protocol_version='HTTP/1.1'
    def log_message(self,*args): pass
    def do_HEAD(self): self.serve(True)
    def do_GET(self): self.serve(False)
    def serve(self,head):
        path=urlsplit(self.path).path
        with LOCK:
            REQUESTS.append({'path':path,'method':self.command,'range':self.headers.get('Range'),
                             'authorization':self.headers.get('Authorization'),'time':time.monotonic()})
        if path.startswith('/denied/') or path.startswith('/never/') or (path.startswith('/auth/') and self.headers.get('Authorization')!='LocalTest sample'):
            self.send_response(403);self.send_header('Content-Length','0');self.end_headers();return
        start=0
        if self.headers.get('Range','').startswith('bytes='):
            try:start=int(self.headers['Range'][6:].split('-')[0])
            except ValueError:pass
        if start>=len(MEDIA):
            self.send_response(416);self.send_header('Content-Length','0');self.end_headers();return
        self.send_response(206 if start else 200)
        self.send_header('Content-Type','application/octet-stream');self.send_header('Accept-Ranges','bytes')
        self.send_header('Content-Length',str(len(MEDIA)-start))
        if start:self.send_header('Content-Range',f'bytes {start}-{len(MEDIA)-1}/{len(MEDIA)}')
        self.end_headers()
        if head:return
        key=path
        with LOCK:
            ACTIVE[key]=ACTIVE.get(key,0)+1;MAX_ACTIVE[key]=max(MAX_ACTIVE.get(key,0),ACTIVE[key])
        try:
            for pos in range(start,len(MEDIA),65536):
                self.wfile.write(MEDIA[pos:pos+65536]);self.wfile.flush();time.sleep(.003)
        except (BrokenPipeError,ConnectionResetError,ConnectionAbortedError,OSError):pass
        finally:
            with LOCK:ACTIVE[key]-=1
server=ThreadingHTTPServer(('127.0.0.1',0),Handler);server.daemon_threads=True
thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
BASE=f'http://127.0.0.1:{server.server_port}'
def check(condition,label):
    if not condition:raise AssertionError(label)
    print('PASS',label,flush=True)
def requests(prefix):
    with LOCK:return [r for r in REQUESTS if r['path'].startswith(prefix)]
class Frontend:
    def __init__(self,args,name):
        self.name=name;self.fp=None;self.request=0
        self.pool=concurrent.futures.ThreadPoolExecutor(max_workers=1)
        self.pipe=r'\\.\pipe\animejanai-network-'+uuid.uuid4().hex
        self.logpath=OUT/(name+'.log');self.log=self.logpath.open('wb')
        flags=['--config-dir='+str(APP/'portable_config'),'--process-instance=multi',
            '--input-ipc-server='+self.pipe,'--vo=gpu','--gpu-api=d3d11','--gpu-context=d3d11',
            '--d3d11-warp=yes','--hwdec=no','--vf=','--ao=null','--idle=yes','--keep-open=yes',
            '--save-position-on-quit=no','--resume-playback=no','--geometry=1280x720',
            '--terminal=yes','--msg-level=all=warn','--log-file='+str(OUT/(name+'-mpv.log'))]
        self.proc=subprocess.Popen([str(APP/'mpv-AnimeFusion.exe'),*flags,*args],cwd=APP,stdout=self.log,stderr=self.log)
        end=time.monotonic()+15
        while time.monotonic()<end:
            if self.proc.poll() is not None:
                self.close();raise RuntimeError('frontend exited: '+name)
            try:self.fp=open(self.pipe,'r+b',buffering=0);break
            except OSError:time.sleep(.1)
        if self.fp is None:self.close();raise RuntimeError('no frontend IPC: '+name)
    def command(self,*args):
        def request():
            self.request+=1
            self.fp.write((json.dumps({'command':args,'request_id':self.request},ensure_ascii=False)+'\n').encode())
            while True:
                line=self.fp.readline()
                if not line:raise RuntimeError('IPC closed')
                data=json.loads(line)
                if data.get('request_id')==self.request:
                    if data.get('error')!='success':raise RuntimeError(str(data))
                    return data.get('data')
        return self.pool.submit(request).result(timeout=6)
    def get(self,name,default=None):
        try:return self.command('get_property',name)
        except RuntimeError:return default
    def wait(self,predicate,label,seconds=12):
        deadline=time.monotonic()+seconds
        while time.monotonic()<deadline:
            if predicate():return
            time.sleep(.05)
        raise AssertionError(label+'; '+json.dumps(self.get('user-data/player_ui/playback',{}),ensure_ascii=False))
    def loaded(self,path):
        self.wait(lambda:self.get('path')==path and self.get('time-pos') is not None and (self.get('vo-presented-frame-count',0) or 0)>0,'video did not begin')
    def shot(self,name):
        target=OUT/(name+'.png')
        if self.get('idle-active'):
            ui=self.get('user-data/player_ui/ui',{})
            check(not ui.get('visible') and not ui.get('controls'),'idle/error does not show playback controls')
            evidence=capture_client(self.proc.pid,target)
            failed=self.get('user-data/player_ui/playback',{}).get('phase')=='failed'
            if failed:
                check(evidence['bright_pixels']>=100,'failed playback displays the error text')
            else:
                check(evidence['bright_pixels']>=100,'idle player displays the upstream OSC logo')
            (OUT/(name+'-capture.json')).write_text(json.dumps(evidence,indent=2),encoding='utf-8')
        else:
            self.wait(lambda:self.get('user-data/player_ui/ui',{}).get('overlay_ok') is True,'overlay not rendered')
            self.command('screenshot-to-file',str(target),'window')
        check(target.is_file() and target.stat().st_size>1024,'captured visible client '+name)
    def close(self):
        if hasattr(self,'proc') and self.proc.poll() is None:
            self.proc.terminate()
            try:self.proc.wait(5)
            except subprocess.TimeoutExpired:self.proc.kill();self.proc.wait(5)
        if self.fp:self.fp.close();self.fp=None
        self.pool.shutdown(wait=False,cancel_futures=True);self.log.close()
    def __enter__(self):return self
    def __exit__(self,*args):self.close()
def case(name,fn):
    try:fn();RESULTS.append({'case':name,'passed':True})
    except Exception as e:
        RESULTS.append({'case':name,'passed':False,'error':str(e)});print('FAIL',name,str(e),flush=True)
def idle_and_ipc():
    with Frontend([], 'idle-ipc') as p:
        p.wait(lambda:p.get('user-data/player_ui/ui',{}).get('version')=='1.3.0','UI missing')
        check(p.get('idle-active') is True,'idle without media is not fake playback')
        p.shot('idle-small-ui')
        uri=BASE+'/media/ipc.y4m'
        p.command('loadfile',uri,'replace');p.loaded(uri)
        ui=p.get('user-data/player_ui/ui',{});scale=ui.get('scale',0)
        # Controls must be comfortably readable, not shrunk to a token size, and
        # must still fit inside the client area whatever the window is.
        check(0.5<=scale<=1.5,'player_ui controls sized in a readable range: %r'%scale)
        check(all(b['x1']*scale<=ui.get('width',0)+1 and b['y1']*scale<=ui.get('height',0)+1
                  for b in ui.get('controls',[])),'controls fit the client area')
        p.shot('network-small-ui')
        check(p.get('prefetch-playlist') is False,'native playlist prefetch disabled')
def direct_and_ui():
    uri=BASE+'/auth/direct.y4m?api_key=a|b&MediaSourceId=test'
    with Frontend([uri,'--http-header-fields=Authorization: LocalTest sample'],'direct') as p:
        p.loaded(uri)
        check(p.get('file-local-options/cache-pause-initial') is False,
              'network playback does not wait for the configured initial cache')
        initial=p.get('time-pos',0)
        p.wait(lambda:(p.get('time-pos',0) or 0)>initial+.5,'real playback does not advance')
        p.command('set_property','pause',True)
        p.wait(lambda:p.get('pause') is True,'pause not applied')
        # A bounded paused cache may retain one blocked HTTP response. Requiring
        # that response to finish would force whole-file downloading and defeat
        # the production cache limit. Test request counts and playback instead.
        time.sleep(.4);before=len(requests('/auth/direct.y4m'))
        check(before==1,'direct playback starts one media request')
        p.command('loadfile',BASE+'/never/next.y4m','append')
        check(not (APP/'portable_config/scripts/thumbfast.lua').exists(),'hover-preview decoder is not packaged')
        for kind in ('speed','audio','sub','settings','stats'):
            p.command('script-message','player_ui-menu',kind);time.sleep(.12)
        p.command('script-message','player_ui-hide');p.command('script-message','player_ui-show');time.sleep(.15)
        ui=p.get('user-data/player_ui/ui',{});seek=next(b for b in ui['controls'] if b['id']=='seek')
        for fraction in (.2,.4,.8,.5):
            p.command('mouse',int((seek['x0']+(seek['x1']-seek['x0'])*fraction)*ui['scale']),int((seek['y0']+seek['y1'])*ui['scale']/2));time.sleep(.2)
        time.sleep(1)
        after=len(requests('/auth/direct.y4m'))
        check(after==before,'menus, network speed and timeline hovering cause no extra media requests')
        check(not requests('/never/next.y4m'),'queued next video was not prefetched')
        check(all(r['authorization']=='LocalTest sample' for r in requests('/auth/direct.y4m')),'caller authorization retained')
        check(MAX_ACTIVE.get('/auth/direct.y4m')==1,'direct video has no concurrent readers')
        check(p.get('path')==uri,'pipe and signed query preserved')
        paused_position=p.get('time-pos',0)
        p.command('set_property','pause',False)
        p.wait(lambda:(p.get('time-pos',0) or 0)>paused_position+.5,'bounded cache does not resume playback')
        check(len(requests('/auth/direct.y4m'))==before,'resume reuses the original media request')
        RESULTS.append({'case':'direct-request-count','passed':True,'before_ui':before,'after_ui':after,'max_concurrent':MAX_ACTIVE.get('/auth/direct.y4m')})
def grouped():
    first=BASE+'/never/group-first.y4m';second=BASE+'/media/group-selected.y4m'
    args=['--{',first,'--force-media-title=第一集','--}', '--{',second,'--force-media-title=第二集','--}','--playlist-start=1']
    with Frontend(args,'grouped') as p:
        p.loaded(second);check(p.get('media-title')=='第二集','per-episode title retained')
        check(not requests('/never/group-first.y4m'),'start at selected episode without opening first')
def playlist_file():
    first=BASE+'/never/list-first.y4m';second=BASE+'/media/list-selected.y4m'
    path=OUT/'两集.m3u';path.write_text('#EXTM3U\n'+first+'\n'+second+'\n',encoding='utf-8')
    with Frontend(['--playlist',str(path),'--playlist-start=1'],'playlist-file') as p:
        p.loaded(second);check(not requests('/never/list-first.y4m'),'playlist option starts only selected item')
def refused():
    with Frontend([BASE+'/denied/video.y4m'],'denied') as p:
        p.wait(lambda:p.get('user-data/player_ui/playback',{}).get('phase')=='failed','failed URL not reported')
        check(p.get('user-data/player_ui/playback',{}).get('http_status')==403,'HTTP refusal code retained')
        count=len(requests('/denied/video.y4m'));time.sleep(2)
        check(count>0 and len(requests('/denied/video.y4m'))==count,'403 has no scripted retry loop')
        p.shot('http-error')
        source=APP/'portable_config/playback-diagnostic.json';content=source.read_text(encoding='utf-8')
        check('http://' not in content and 'api_key' not in content and 'LocalTest' not in content,'diagnostic excludes URL and credentials')
        (OUT/'safe-diagnostic.json').write_text(content,encoding='utf-8')
try:
    source=(APP/'portable_config/scripts/player_ui.lua').read_text(encoding='utf-8-sig')
    check('拖入视频或链接开始播放' not in source,'idle prompt removed from executable UI script')
    for name,fn in [('idle-and-ipc',idle_and_ipc),('direct-and-ui',direct_and_ui),('scoped-selected',grouped),('playlist-option',playlist_file),('http-refusal',refused)]:case(name,fn)
finally:
    server.shutdown();server.server_close()
    (OUT/'results.json').write_text(json.dumps(RESULTS,ensure_ascii=False,indent=2),encoding='utf-8')
    (OUT/'request-counts.json').write_text(json.dumps({'requests':REQUESTS,'max_concurrent':MAX_ACTIVE},ensure_ascii=False,indent=2),encoding='utf-8')
    (APP/'portable_config/playback-diagnostic.json').unlink(missing_ok=True)
print(json.dumps(RESULTS,ensure_ascii=False,indent=2))
if not RESULTS or not all(x['passed'] for x in RESULTS):raise SystemExit(1)

"""Isolated CPU, Lua and real frontend/IPC regressions. No user media or network."""
from pathlib import Path
import ctypes as C
import json, os, subprocess, sys, time, threading, uuid
BUNDLE = Path(sys.argv[1]).resolve()
OUT = Path(sys.argv[2]).resolve(); OUT.mkdir(parents=True, exist_ok=True)
ROOT = Path(__file__).resolve().parents[1]

def fixture(name, value=100):
    path = OUT / name
    path.write_bytes(b'YUV4MPEG2 W16 H16 F24:1 Ip A1:1 C420jpeg\n' +
                     (b'FRAME\n' + bytes([value])*256 + bytes([128])*128)*24*60)
    return path

class Player:
    def __init__(self):
        self.directory = os.add_dll_directory(str(BUNDLE))
        self.lib = C.CDLL(str(BUNDLE/'app/libmpv-2.dll'))
        specs = {'mpv_create':(C.c_void_p,[]), 'mpv_initialize':(C.c_int,[C.c_void_p]),
          'mpv_set_option_string':(C.c_int,[C.c_void_p,C.c_char_p,C.c_char_p]),
          'mpv_set_property_string':(C.c_int,[C.c_void_p,C.c_char_p,C.c_char_p]),
          'mpv_get_property':(C.c_int,[C.c_void_p,C.c_char_p,C.c_int,C.c_void_p]),
          'mpv_get_property_string':(C.c_void_p,[C.c_void_p,C.c_char_p]),
          'mpv_free':(None,[C.c_void_p]), 'mpv_command':(C.c_int,[C.c_void_p,C.POINTER(C.c_char_p)]),
          'mpv_terminate_destroy':(None,[C.c_void_p])}
        for n,(ret,args) in specs.items():
            f=getattr(self.lib,n);f.restype=ret;f.argtypes=args
        self.h=self.lib.mpv_create(); assert self.h
        for n,v in {'config':'no','vo':'null','ao':'null','hwdec':'no','idle':'yes','terminal':'no',
                    'load-scripts':'no','osc':'no','pause':'yes'}.items():
            assert self.lib.mpv_set_option_string(self.h,n.encode(),v.encode())>=0,n
        assert self.lib.mpv_initialize(self.h)>=0
    def command(self,*args):
        a=(C.c_char_p*(len(args)+1))(*[str(x).encode('utf-8') for x in args],None)
        r=self.lib.mpv_command(self.h,a)
        if r<0: raise RuntimeError((args,r))
    def set(self,n,v):
        r=self.lib.mpv_set_property_string(self.h,n.encode(),str(v).encode())
        if r<0: raise RuntimeError((n,v,r))
    def count(self):
        n=C.c_int64();r=self.lib.mpv_get_property(self.h,b'vo-presented-frame-count',4,C.byref(n))
        if r<0:raise RuntimeError(('missing native counter',r))
        return n.value
    def string(self,n):
        p=self.lib.mpv_get_property_string(self.h,n.encode())
        if not p:return None
        try:return C.string_at(p).decode('utf-8')
        finally:self.lib.mpv_free(p)
    def __enter__(self):return self
    def __exit__(self,*exc):
        if self.h:self.lib.mpv_terminate_destroy(self.h);self.h=None
        self.directory.close()

def measure(case):
    with Player() as p:
        if case=='audio-thread':
            errors=[]
            def read():
                try:assert p.string('audio-device-list') is not None
                except Exception as e:errors.append(repr(e))
            t=threading.Thread(target=read);t.start();t.join(10)
            assert not t.is_alive() and not errors,errors
            return {'case':case}
        if case=='48fps':p.set('vf','lavfi=[fps=48]')
        if case in ('slow-no-drop','slow-default'):p.set('vf','lavfi=[realtime=speed=0.5]')
        if case=='slow-no-drop':p.set('framedrop','no')
        if case=='half-speed':p.set('speed','0.5')
        p.command('loadfile',str(fixture('sample.y4m')));time.sleep(.6)
        p.set('pause','no');time.sleep(2)
        t0=time.perf_counter();n0=p.count();time.sleep(3);n1=p.count();dt=time.perf_counter()-t0
        fps=(n1-n0)/dt
        if case=='slow-default':
            # Dropping already-late frames need not equal the filter ceiling.
            assert 0 < fps < 16,(case,fps)
        else:
            expect=48 if case=='48fps' else 12 if case in ('half-speed','slow-no-drop') else 24
            assert abs(fps-expect)<2.5,(case,fps,expect)
        p.set('pause','yes');time.sleep(.3);n=p.count()
        for i in range(10):p.command('show-text',str(i),100);time.sleep(.03)
        assert p.count()==n,'OSD redraw changed frame counter'
        p.command('seek','0','absolute+exact');time.sleep(.3)
        assert p.count()>=n,'seek decreased native lifetime count'
        return {'case':case,'fps':fps,'frames':n1-n0,'seconds':dt}

class Frontend:
    def __init__(self,args,tag):
        self.pipe=r'\\.\pipe\ajn-r2-'+uuid.uuid4().hex
        self.log=open(OUT/(tag+'.console.log'),'wb')
        self.proc=subprocess.Popen([str(BUNDLE/'mpv-AnimeFusion.exe'),'--no-config','--load-scripts=no',
            '--vo=null','--ao=null','--hwdec=no','--pause=yes','--idle=yes',
            '--input-ipc-server='+self.pipe,'--log-file='+str(OUT/(tag+'.mpv.log')),*args],
            cwd=str(BUNDLE),stdout=self.log,stderr=subprocess.STDOUT)
        self.fp=None;self.request_id=0
        until=time.monotonic()+15
        while time.monotonic()<until:
            if self.proc.poll() is not None:raise RuntimeError(('frontend exited',tag,self.proc.returncode))
            try:self.fp=open(self.pipe,'r+b',buffering=0);break
            except OSError:time.sleep(.1)
        if self.fp is None:self.close();raise RuntimeError(('no IPC',tag))
    def command(self,*args):
        self.request_id+=1
        self.fp.write((json.dumps({'command':list(args),'request_id':self.request_id},ensure_ascii=False)+'\n').encode())
        while True:
            line=self.fp.readline()
            if not line:raise RuntimeError('IPC closed')
            data=json.loads(line)
            if data.get('request_id')==self.request_id:
                assert data.get('error')=='success',data
                return data.get('data')
    def get(self,n):return self.command('get_property',n)
    def wait(self,expected,subtitles):
        until=time.monotonic()+12
        while time.monotonic()<until:
            try:
                path=self.get('path');tracks=self.get('track-list')
                actual=[Path(t['external-filename']).name for t in tracks if t.get('external') and t.get('type')=='sub']
                if Path(path).name==expected and sorted(actual)==sorted(subtitles):return
            except (AssertionError,TypeError,KeyError):pass
            time.sleep(.1)
        raise AssertionError({'expected':expected,'subtitles':subtitles,'path':self.get('path'),'tracks':self.get('track-list')})
    def close(self):
        if self.fp:self.fp.close();self.fp=None
        if self.proc.poll() is None:
            self.proc.terminate()
            try:self.proc.wait(5)
            except subprocess.TimeoutExpired:self.proc.kill();self.proc.wait(5)
        self.log.close()

def frontend():
    files=[fixture('第 %d 集.y4m'%i,50+i*20) for i in (1,2,3)]
    subs=[]
    for name in ('第1集,甲.srt','第1集;乙.srt','第2集.srt'):
        p=OUT/name;p.write_text('1\n00:00:00,000 --> 00:00:59,000\n'+name+'\n',encoding='utf-8');subs.append(p)
    def group(i, ss):
        return ['--{',str(files[i]),'--force-media-title=测试 第%d集'%(i+1),
            *['--sub-file='+str(p) for p in ss],'--sub-auto=no','--}']
    a=None;b=None
    try:
        a=Frontend([*group(0,subs[:2]),*group(1,subs[2:]),*group(2,[]),'--playlist-start=1'],'scoped')
        a.wait(files[1].name,[subs[2].name]);assert a.get('media-title')=='测试 第2集'
        a.command('set_property','playlist-pos',0);a.wait(files[0].name,[p.name for p in subs[:2]])
        a.command('set_property','playlist-pos',2);a.wait(files[2].name,[])
        b=Frontend([str(files[1]),'--sub-file='+str(subs[2]),'--sub-auto=no','--force-media-title=新请求'],'second')
        b.wait(files[1].name,[subs[2].name]);assert b.get('media-title')=='新请求'
        a.wait(files[2].name,[])
        return {'case':'frontend','checks':['selected-index','episode-1-two-subtitles','unicode-comma-semicolon',
            'episode-3-no-subtitle-leak','second-invocation-keeps-arguments','old-instance-unchanged']}
    finally:
        if b:b.close()
        if a:a.close()

def run(case):
    if case=='frontend':return frontend()
    if case=='lua':
        args=[str(BUNDLE/'app/mpv.exe'),'--no-config','--vo=null','--ao=null','--idle=yes',
            '--scripts='+str(ROOT/'tests/test_stats.lua'),'--script-opts=r2root='+str(ROOT)]
        cp=subprocess.run(args,capture_output=True,timeout=20)
        (OUT/'lua-output.log').write_bytes(cp.stdout+cp.stderr)
        assert cp.returncode==0 and b'PASS native FPS Lua' in cp.stdout+cp.stderr,cp.stdout+cp.stderr
        return {'case':case}
    if case.startswith('close-'):
        cp=subprocess.run([str(BUNDLE/'app/mpv.exe'),'--no-config','--vo=null','--ao=null','--frames=2',
                           str(fixture('close.y4m'))],capture_output=True,timeout=20)
        assert cp.returncode==0,cp.stderr
        return {'case':case}
    return measure(case)

if len(sys.argv)>3:
    result=run(sys.argv[3]);result['passed']=True;print(json.dumps(result,ensure_ascii=False));sys.exit(0)
results=[]
for case in ['24fps','48fps','half-speed','slow-no-drop','slow-default','audio-thread','lua','frontend',
             *['close-'+str(i) for i in range(6)]]:
    try:
        cp=subprocess.run([sys.executable,__file__,str(BUNDLE),str(OUT),case],capture_output=True,timeout=65)
        (OUT/(case+'.log')).write_bytes(cp.stdout+cp.stderr)
        item={'case':case,'passed':cp.returncode==0,'exit_code':cp.returncode}
        if cp.returncode==0:item.update(json.loads(cp.stdout.decode('utf-8').strip().splitlines()[-1]))
    except (subprocess.TimeoutExpired,ValueError) as e:item={'case':case,'passed':False,'error':str(e)}
    results.append(item);print(json.dumps(item),flush=True)
(OUT/'results.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8')
if not all(r['passed'] for r in results):raise SystemExit(1)

"""Windows CPU-playback regressions for the built native library (not GPU tests)."""
from pathlib import Path
import ctypes as C
import json
import os
import subprocess
import sys
import threading
import time
bundle=Path(sys.argv[1]).resolve()
output=Path(sys.argv[2]).resolve();output.mkdir(parents=True,exist_ok=True)
media=output/'sample.y4m'
if not media.exists():
    media.write_bytes(b'YUV4MPEG2 W16 H16 F24:1 Ip A1:1 C420jpeg\n'+(b'FRAME\n'+bytes([100])*256+bytes([128])*128)*24*30)
class Player:
    def __init__(self):
        self.directory=os.add_dll_directory(str(bundle)) if hasattr(os,'add_dll_directory') else None
        self.lib=C.CDLL(str(bundle/'libmpv-2.dll'))
        specs={'mpv_create':(C.c_void_p,[]),'mpv_initialize':(C.c_int,[C.c_void_p]),
          'mpv_set_option_string':(C.c_int,[C.c_void_p,C.c_char_p,C.c_char_p]),
          'mpv_set_property_string':(C.c_int,[C.c_void_p,C.c_char_p,C.c_char_p]),
          'mpv_get_property':(C.c_int,[C.c_void_p,C.c_char_p,C.c_int,C.c_void_p]),
          'mpv_get_property_string':(C.c_void_p,[C.c_void_p,C.c_char_p]),
          'mpv_free':(None,[C.c_void_p]),'mpv_command':(C.c_int,[C.c_void_p,C.POINTER(C.c_char_p)]),
          'mpv_terminate_destroy':(None,[C.c_void_p])}
        for n,(res,args) in specs.items():
            f=getattr(self.lib,n);f.restype=res;f.argtypes=args
        self.h=self.lib.mpv_create();assert self.h
        for n,v in {'config':'no','vo':'null','ao':'null','hwdec':'no','idle':'yes','terminal':'no','load-scripts':'no','osc':'no','pause':'yes'}.items():
            assert self.lib.mpv_set_option_string(self.h,n.encode(),v.encode())>=0,n
        assert self.lib.mpv_initialize(self.h)>=0
    def command(self,*args):
        a=(C.c_char_p*(len(args)+1))(*[str(x).encode('utf-8') for x in args],None)
        r=self.lib.mpv_command(self.h,a)
        if r<0:raise RuntimeError('command failed '+repr(args)+' / '+str(r))
    def set(self,n,v):
        r=self.lib.mpv_set_property_string(self.h,n.encode(),str(v).encode())
        if r<0:raise RuntimeError('property failed '+n+' / '+str(r))
    def count(self):
        n=C.c_int64();r=self.lib.mpv_get_property(self.h,b'vo-presented-frame-count',4,C.byref(n))
        if r<0:raise RuntimeError('native counter unavailable '+str(r))
        return n.value
    def string(self,n):
        p=self.lib.mpv_get_property_string(self.h,n.encode())
        if not p:return None
        try:return C.string_at(p).decode('utf-8')
        finally:self.lib.mpv_free(p)
    def close(self):
        if self.h:self.lib.mpv_terminate_destroy(self.h);self.h=None
    def __enter__(self):return self
    def __exit__(self,*exc):self.close()
def run_case(name):
    if name=='audio-thread-lifetime':
        with Player() as p:
            errors=[]
            def query():
                try:assert p.string('audio-device-list') is not None
                except BaseException as e:errors.append(str(e))
            t=threading.Thread(target=query);t.start();t.join(10)
            assert not t.is_alive() and not errors,errors
            time.sleep(.2)
        return {'case':name,'passed':True}
    with Player() as p:
        if name in ('48fps','pause-redraw'):p.set('vf','lavfi=[fps=48]')
        if name=='72fps':p.set('vf','lavfi=[fps=72]')
        if name=='slow-filter':
            # Measure the filter's output cadence, without additionally dropping
            # its intentionally late frames in the VO.
            p.set('framedrop','no')
            p.set('vf','lavfi=[realtime=speed=0.5]')
        if name=='half-speed':p.set('speed','0.5')
        p.command('loadfile',str(media));time.sleep(.5);p.count()
        p.set('pause','no');time.sleep(1.5)
        t0=time.perf_counter();n0=p.count();time.sleep(2);n1=p.count();elapsed=time.perf_counter()-t0
        rate=(n1-n0)/elapsed
        expected=72 if name=='72fps' else 48 if name in ('48fps','pause-redraw') else 12 if name in ('half-speed','slow-filter') else 24
        assert abs(rate-expected)<3,(name,rate,expected)
        if name=='pause-redraw':
            p.set('pause','yes');time.sleep(.3);paused=p.count()
            for i in range(20):p.command('show-text','redraw '+str(i),100);time.sleep(.03)
            assert p.count()==paused,'redraw counted as a new video frame'
            p.command('seek','0','absolute+exact');time.sleep(.3);after=p.count()
            assert after>=paused,'counter unexpectedly decreased on seek'
        return {'case':name,'passed':True,'frames':n1-n0,'elapsed':elapsed,'fps':rate,'expected':expected}
if len(sys.argv)>3:
    result=run_case(sys.argv[3]);print(json.dumps(result));sys.exit(0)
results=[]
for name in ['24fps','48fps','72fps','half-speed','slow-filter','pause-redraw','audio-thread-lifetime']:
    cp=subprocess.run([sys.executable,__file__,str(bundle),str(output),name],capture_output=True,timeout=35)
    (output/(name+'.log')).write_bytes(cp.stdout+cp.stderr)
    result={'case':name,'exit_code':cp.returncode,'passed':cp.returncode==0}
    if cp.returncode==0:
        try:result.update(json.loads(cp.stdout.decode().strip().splitlines()[-1]))
        except (ValueError,IndexError):result['passed']=False
    results.append(result)
for index in range(6):
    cp=subprocess.run([str(bundle/'mpv.exe'),'--no-config','--vo=null','--ao=null','--frames=2',str(media)],capture_output=True,timeout=20)
    (output/f'default-close-{index}.log').write_bytes(cp.stdout+cp.stderr)
    results.append({'case':f'default-close-{index}','exit_code':cp.returncode,'passed':cp.returncode==0})
(output/'results.json').write_text(json.dumps(results,indent=2))
print(json.dumps(results,indent=2))
if not all(r['passed'] for r in results):raise SystemExit(1)

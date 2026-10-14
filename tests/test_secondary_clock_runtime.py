"""Headless ASS timing check using generated media and a gated loopback server.

No desktop window, user stream, configuration, account or danmaku source record
is opened. libmpv's software renderer composes the real secondary ASS in RAM.
"""
import ctypes as C
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import threading
import time

ROOT = Path(__file__).resolve().parents[1]


class Param(C.Structure):
    _fields_ = [('type', C.c_int), ('data', C.c_void_p)]


class Event(C.Structure):
    _fields_ = [('event_id', C.c_int), ('error', C.c_int),
                ('reply_userdata', C.c_uint64), ('data', C.c_void_p)]


class Log(C.Structure):
    _fields_ = [('prefix', C.c_char_p), ('level', C.c_char_p),
                ('text', C.c_char_p), ('log_level', C.c_int)]


class MemoryPlayer:
    def __init__(self, bundle, output):
        self.directory = os.add_dll_directory(str(bundle))
        self.dll = C.CDLL(str(bundle/'libmpv-2.dll'))
        specs = {
            'mpv_create': (C.c_void_p, []),
            'mpv_set_option_string': (C.c_int, [C.c_void_p, C.c_char_p, C.c_char_p]),
            'mpv_initialize': (C.c_int, [C.c_void_p]),
            'mpv_command': (C.c_int, [C.c_void_p, C.POINTER(C.c_char_p)]),
            'mpv_get_property_string': (C.c_void_p, [C.c_void_p, C.c_char_p]),
            'mpv_free': (None, [C.c_void_p]),
            'mpv_request_log_messages': (C.c_int, [C.c_void_p, C.c_char_p]),
            'mpv_wait_event': (C.POINTER(Event), [C.c_void_p, C.c_double]),
            'mpv_terminate_destroy': (None, [C.c_void_p]),
            'mpv_render_context_create': (C.c_int, [C.POINTER(C.c_void_p), C.c_void_p, C.POINTER(Param)]),
            'mpv_render_context_render': (C.c_int, [C.c_void_p, C.POINTER(Param)]),
            'mpv_render_context_free': (None, [C.c_void_p]),
        }
        for name, (restype, args) in specs.items():
            fn = getattr(self.dll, name)
            fn.restype = restype
            fn.argtypes = args
        self.handle = self.dll.mpv_create()
        assert self.handle
        options = {
            'config': 'no', 'vo': 'libmpv', 'ao': 'null', 'hwdec': 'no',
            'load-scripts': 'no', 'osc': 'no', 'idle': 'yes', 'terminal': 'no',
            'osd-level': '0', 'pause': 'yes', 'sub-auto': 'no',
            'secondary-sub-display-sync': 'yes', 'secondary-sub-render-fps': '90',
            'cache': 'yes', 'cache-secs': '0.5', 'cache-pause-wait': '0.25',
            'demuxer-max-bytes': '128KiB', 'demuxer-max-back-bytes': '0',
            'log-file': str(output/'mpv.log'), 'msg-level': 'all=warn,osd=trace',
        }
        for name, value in options.items():
            assert self.dll.mpv_set_option_string(self.handle, name.encode(), value.encode()) >= 0, name
        assert self.dll.mpv_initialize(self.handle) >= 0
        self.context = C.c_void_p()
        api = C.create_string_buffer(b'sw')
        params = (Param*2)(Param(1, C.cast(api, C.c_void_p)), Param(0, None))
        assert self.dll.mpv_render_context_create(C.byref(self.context), self.handle, params) >= 0
        self.stop = threading.Event()
        self.samples = []
        self.events = []
        self.errors = []
        self.dll.mpv_request_log_messages(self.handle, b'trace')
        self.threads = [threading.Thread(target=self.render), threading.Thread(target=self.receive)]
        for thread in self.threads:
            thread.start()

    def command(self, *args):
        values = (C.c_char_p*(len(args)+1))(*[str(a).encode('utf-8') for a in args], None)
        result = self.dll.mpv_command(self.handle, values)
        assert result >= 0, (args, result)

    def property(self, name):
        pointer = self.dll.mpv_get_property_string(self.handle, name.encode())
        if not pointer:
            return None
        try:
            return C.string_at(pointer).decode('utf-8')
        finally:
            self.dll.mpv_free(pointer)

    def receive(self):
        while not self.stop.is_set():
            event = self.dll.mpv_wait_event(self.handle, .05).contents
            now = time.monotonic()
            if event.event_id == 2:
                log = C.cast(event.data, C.POINTER(Log)).contents
                text = log.text.decode('utf-8', errors='replace')
                match = re.search(r'Secondary ASS pts=([\d.-]+)', text)
                if match:
                    self.samples.append((now, float(match.group(1))))
            elif event.event_id:
                self.events.append((now, event.event_id))

    def render(self):
        size = (C.c_int*2)(320, 180)
        stride = C.c_size_t(320*4)
        fmt = C.create_string_buffer(b'rgb0')
        storage = C.create_string_buffer(320*180*4+64)
        address = (C.addressof(storage)+63) & ~63
        params = (Param*5)(Param(17, C.cast(size, C.c_void_p)), Param(18, C.cast(fmt, C.c_void_p)),
                           Param(19, C.cast(C.byref(stride), C.c_void_p)), Param(20, address), Param(0, None))
        while not self.stop.is_set():
            result = self.dll.mpv_render_context_render(self.context, params)
            if result < 0:
                self.errors.append(result)
                return
            self.stop.wait(.01)

    def wait(self, predicate, label, seconds=15):
        deadline = time.monotonic()+seconds
        while time.monotonic() < deadline:
            assert not self.errors, self.errors
            if predicate():
                return
            time.sleep(.025)
        raise AssertionError(label + ': ' + str({name: self.property(name) for name in
                             ('time-pos', 'paused-for-cache', 'seeking', 'core-idle', 'secondary-sid')}))

    def check_aligned(self, label):
        # Compare the latest real ASS render and the current media position.
        # Differences up to one 24 fps frame plus the client sampling interval
        # are expected; seconds of error after buffering are not.
        self.wait(lambda: self.samples and time.monotonic()-self.samples[-1][0] < .15,
                  'fresh ASS render')
        media = float(self.property('time-pos'))
        ass = self.samples[-1][1]
        error = abs(media-ass)
        assert error < .15, (label, media, ass, error)
        return {'case': label, 'media_pts': media, 'ass_pts': ass, 'error_seconds': error}

    def close(self):
        self.stop.set()
        for thread in self.threads:
            thread.join(10)
            assert not thread.is_alive(), 'headless thread failed to stop'
        self.dll.mpv_render_context_free(self.context)
        self.dll.mpv_terminate_destroy(self.handle)
        self.directory.close()


def run(bundle, output):
    output.mkdir(parents=True, exist_ok=True)
    clip = output/'generated.mkv'
    if not clip.exists():
        ffmpeg = Path(os.environ.get('FFMPEG') or shutil.which('ffmpeg') or
                      ROOT/'_probe/build-tools/msys/ucrt64/bin/ffmpeg.exe')
        env = dict(os.environ)
        env['PATH'] = str(ffmpeg.parent)+os.pathsep+env.get('PATH', '')
        cp = subprocess.run([str(ffmpeg), '-hide_banner', '-loglevel', 'error', '-y',
                             '-f', 'lavfi', '-i', 'testsrc2=size=320x180:rate=24',
                             '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000',
                             '-t', '45', '-c:v', 'mpeg4', '-q:v', '2', '-g', '24',
                             '-c:a', 'pcm_s16le', str(clip)], capture_output=True, env=env, timeout=60)
        assert cp.returncode == 0, cp.stderr.decode(errors='replace')
    data = clip.read_bytes()
    gate = threading.Event()
    gate.set()

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def do_GET(self):
            match = re.match(r'bytes=(\d+)-', self.headers.get('Range', ''))
            start = int(match.group(1)) if match else 0
            self.send_response(206 if match else 200)
            self.send_header('Content-Type', 'video/x-matroska')
            self.send_header('Accept-Ranges', 'bytes')
            self.send_header('Content-Length', str(len(data)-start))
            if match:
                self.send_header('Content-Range', f'bytes {start}-{len(data)-1}/{len(data)}')
            self.end_headers()
            try:
                for offset in range(start, len(data), 4096):
                    if not gate.wait(30):
                        return
                    self.wfile.write(data[offset:offset+4096])
                    self.wfile.flush()
                    time.sleep(.006)
            except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                pass

    server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
    server.daemon_threads = True
    thread = threading.Thread(target=server.serve_forever)
    thread.start()
    ass = output/'secondary.ass'
    ass.write_text('''[Script Info]
ScriptType: v4.00+
PlayResX: 320
PlayResY: 180
[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Arial,20,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,0,7,0,0,0,1
[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.00,0:01:00.00,Default,,0,0,0,,{\\move(150,20,30,20,0,60000)}CLOCK TEST
''', encoding='utf-8')
    player = None
    results = []
    try:
        player = MemoryPlayer(bundle, output)
        player.command('loadfile', f'http://127.0.0.1:{server.server_port}/generated.mkv')
        player.wait(lambda: player.property('time-pos') is not None, 'initial picture')
        player.command('sub-add', ass, 'auto')
        player.command('set', 'sid', 'no')
        player.command('set', 'secondary-sid', '1')
        player.wait(lambda: len(player.samples) > 0, 'secondary ASS attached')
        player.command('set', 'pause', 'no')
        player.wait(lambda: float(player.property('time-pos') or 0) > 1, 'normal playback')
        results.append(player.check_aligned('normal-playback'))

        # Block all loopback reads, seek, then wait until the existing cache is
        # exhausted. Force OSD redraws during the wait just like controls do.
        for target in (26, 6):
            gate.clear()
            player.command('seek', str(target), 'absolute+exact')
            player.wait(lambda: player.property('paused-for-cache') == 'yes' or
                        player.property('seeking') == 'yes', 'seek buffering')
            time.sleep(.5)
            frozen_start = time.monotonic()
            for index in range(15):
                player.command('show-text', 'buffering', 100)
                time.sleep(.08)
            frozen = [pts for stamp, pts in player.samples if stamp >= frozen_start]
            if frozen:
                assert max(frozen)-min(frozen) < .001, ('buffer clock moved', target, frozen)
            results.append({'case': f'seek-{target}-waiting', 'ass_samples': len(frozen),
                            'ass_range_seconds': max(frozen)-min(frozen) if frozen else 0})
            gate.set()
            player.wait(lambda: player.property('seeking') == 'no' and
                        player.property('paused-for-cache') == 'no' and
                        player.property('core-idle') == 'no', 'automatic resume')
            player.wait(lambda: abs(float(player.property('time-pos') or 0)-target) < 2,
                        'target video frame')
            time.sleep(.25)
            results.append(player.check_aligned(f'seek-{target}-resumed-without-pause'))

        # A cache pause without a seek must also freeze and resume aligned.
        gate.clear()
        player.wait(lambda: player.property('paused-for-cache') == 'yes', 'midplay cache pause')
        time.sleep(.3)
        frozen_start = time.monotonic()
        for index in range(15):
            player.command('show-text', 'cache pause', 100)
            time.sleep(.08)
        frozen = [pts for stamp, pts in player.samples if stamp >= frozen_start]
        assert frozen and max(frozen)-min(frozen) < .001, ('cache pause moved', frozen)
        results.append({'case': 'cache-pause', 'ass_samples': len(frozen),
                        'ass_range_seconds': max(frozen)-min(frozen)})
        gate.set()
        player.wait(lambda: player.property('paused-for-cache') == 'no' and
                    player.property('core-idle') == 'no', 'cache automatically resumed')
        time.sleep(.25)
        results.append(player.check_aligned('cache-resumed-without-pause'))
        player.command('set', 'pause', 'yes')
        time.sleep(.2)
        before = player.samples[-1][1]
        for index in range(10):
            player.command('show-text', 'user pause', 100)
            time.sleep(.08)
        assert abs(player.samples[-1][1]-before) < .001, 'user pause moved'
        player.command('set', 'pause', 'no')
        time.sleep(.25)
        results.append(player.check_aligned('user-pause-resume'))
    finally:
        gate.set()
        if player:
            (output/'samples.json').write_text(json.dumps(player.samples))
            player.close()
        server.shutdown()
        server.server_close()
        thread.join(5)
    (output/'results.json').write_text(json.dumps(results, indent=2))
    print(json.dumps(results, indent=2))


if __name__ == '__main__':
    run(Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve())

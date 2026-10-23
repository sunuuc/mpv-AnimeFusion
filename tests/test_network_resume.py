"""Exercise the production HTTP policy with native mpv and loopback media only.

No desktop window, user configuration, accounts or external video requests.
Usage: python test_network_resume.py NATIVE_DIRECTORY OUTPUT_DIRECTORY [POLICY]
"""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit
import ctypes as C
import json
import os
import socket
import subprocess
import sys
import threading
import time

ROOT = Path(__file__).resolve().parents[1]
BUNDLE = Path(sys.argv[1]).resolve()
OUT = Path(sys.argv[2]).resolve()
POLICY = Path(sys.argv[3]).resolve() if len(sys.argv) > 3 else ROOT / 'portable_config/scripts/network_playback.lua'
OUT.mkdir(parents=True, exist_ok=True)
HEADER = b'YUV4MPEG2 W160 H90 F24:1 Ip A1:1 C420jpeg\n'
FRAME = b'FRAME\n' + bytes([96]) * (160 * 90) + bytes([128]) * (160 * 90 // 2)
MEDIA = HEADER + FRAME * (24 * 12)
CUT = len(HEADER) + len(FRAME) * (24 * 8)
LOCAL = OUT / 'sample.y4m'
LOCAL.write_bytes(MEDIA)
FIXTURES = {}
LOCK = threading.Lock()
RESULTS = []


class Fixture:
    def __init__(self, mode, media=MEDIA, cut=CUT):
        self.mode = mode
        self.media, self.cut = media, cut
        self.requests = []
        self.active = self.max_active = 0
        self.release = threading.Event()


class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def log_message(self, *args):
        pass

    def do_GET(self):
        fixture = FIXTURES[urlsplit(self.path).path]
        start = int(self.headers.get('Range', 'bytes=0-')[6:].split('-')[0])
        with LOCK:
            number = len(fixture.requests)
            fixture.requests.append({'offset': start, 'authorization': self.headers.get('Authorization'),
                                     'query': urlsplit(self.path).query})
            fixture.active += 1
            fixture.max_active = max(fixture.max_active, fixture.active)
        try:
            if fixture.mode == 'denied':
                self.send_response(403)
                self.send_header('Content-Length', '0')
                self.end_headers()
                return
            ignore = number > 0 and 'ignore' in fixture.mode
            actual = 0 if ignore else start
            status = 200 if fixture.mode == 'nonseekable' or (ignore and fixture.mode.endswith('ignore-200')) else 206
            self.send_response(status)
            self.send_header('Content-Type', 'application/octet-stream')
            self.send_header('Content-Length', str(len(fixture.media) - actual))
            if fixture.mode != 'nonseekable':
                self.send_header('Accept-Ranges', 'bytes')
                if status == 206:
                    self.send_header('Content-Range', f'bytes {actual}-{len(fixture.media)-1}/{len(fixture.media)}')
            self.end_headers()
            end = fixture.cut if number == 0 and fixture.mode != 'complete' else len(fixture.media)
            self.wfile.write(fixture.media[actual:end])
            self.wfile.flush()
            if fixture.mode.startswith('stall') and number == 0:
                fixture.release.wait(15)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError, OSError):
            pass
        finally:
            with LOCK:
                fixture.active -= 1
            self.close_connection = True
            try:
                self.connection.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass


SERVER = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
SERVER.daemon_threads = True
threading.Thread(target=SERVER.serve_forever, daemon=True).start()


def fixture(name, mode, media=MEDIA, cut=CUT):
    value = Fixture(mode, media, cut)
    FIXTURES['/' + name + '.y4m'] = value
    return value, f'http://127.0.0.1:{SERVER.server_port}/{name}.y4m?test_signature=preserved'


def check(condition, message):
    if not condition:
        raise AssertionError(message)


class NativePlayer:
    class Event(C.Structure):
        _fields_ = [('id', C.c_int), ('error', C.c_int), ('reply', C.c_uint64), ('data', C.c_void_p)]

    class Log(C.Structure):
        _fields_ = [('prefix', C.c_char_p), ('level', C.c_char_p), ('text', C.c_char_p)]

    def __init__(self):
        self.directory = os.add_dll_directory(str(BUNDLE))
        self.lib = C.CDLL(str(BUNDLE / 'libmpv-2.dll'))
        specs = {'mpv_create': (C.c_void_p, []), 'mpv_initialize': (C.c_int, [C.c_void_p]),
                 'mpv_set_option_string': (C.c_int, [C.c_void_p, C.c_char_p, C.c_char_p]),
                 'mpv_set_property_string': (C.c_int, [C.c_void_p, C.c_char_p, C.c_char_p]),
                 'mpv_get_property_string': (C.c_void_p, [C.c_void_p, C.c_char_p]),
                 'mpv_command': (C.c_int, [C.c_void_p, C.POINTER(C.c_char_p)]),
                 'mpv_wait_event': (C.POINTER(self.Event), [C.c_void_p, C.c_double]),
                 'mpv_request_log_messages': (C.c_int, [C.c_void_p, C.c_char_p]),
                 'mpv_free': (None, [C.c_void_p]), 'mpv_terminate_destroy': (None, [C.c_void_p])}
        for name, (result, args) in specs.items():
            fn = getattr(self.lib, name)
            fn.restype, fn.argtypes = result, args
        self.h = self.lib.mpv_create()
        self.events = []
        self.positions = []
        self.logs = []
        for name, value in {'config': 'no', 'load-scripts': 'no', 'terminal': 'no', 'osc': 'no',
                            'vo': 'null', 'ao': 'null', 'force-window': 'no', 'hwdec': 'no',
                            'idle': 'yes', 'keep-open': 'always', 'cache': 'yes', 'cache-pause': 'yes',
                            'speed': '4', 'pause': 'no', 'resume-playback': 'no',
                            'save-position-on-quit': 'no', 'http-header-fields': 'Authorization: LocalResumeTest'}.items():
            check(self.lib.mpv_set_option_string(self.h, name.encode(), value.encode()) >= 0, name)
        check(self.lib.mpv_initialize(self.h) >= 0, 'native initialization')
        self.lib.mpv_request_log_messages(self.h, b'warn')
        self.command('load-script', str(POLICY))
        try:
            self.wait(lambda: json.loads(self.get('user-data') or '{}').get('player_ui', {}).get('playback', {}).get('phase') == 'idle',
                      'production policy did not start')
        except BaseException:
            self.__exit__()
            raise

    def get(self, name):
        ptr = self.lib.mpv_get_property_string(self.h, name.encode())
        if not ptr:
            return None
        try:
            return C.string_at(ptr).decode('utf-8')
        finally:
            self.lib.mpv_free(ptr)

    def command(self, *args):
        array = (C.c_char_p * (len(args) + 1))(*[str(v).encode('utf-8') for v in args], None)
        check(self.lib.mpv_command(self.h, array) >= 0, 'native command ' + str(args[0]))

    def pump(self):
        while True:
            event = self.lib.mpv_wait_event(self.h, 0).contents
            if not event.id:
                break
            self.events.append(event.id)
            if event.id == 2:
                log = C.cast(event.data, C.POINTER(self.Log)).contents
                self.logs.append((log.prefix or b'').decode() + ': ' + (log.text or b'').decode('utf-8', 'replace'))
        position = self.get('time-pos')
        if position is not None:
            self.positions.append(float(position))

    def wait(self, predicate, message, seconds=10):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            self.pump()
            if predicate():
                return
            time.sleep(.025)
        raise AssertionError(message + '; user-data=' + str(self.get('user-data')) + '; ' + ''.join(self.logs[-5:]))

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.lib.mpv_terminate_destroy(self.h)
        self.directory.close()


def dump_case(mode):
    data, url = fixture('dump-' + mode, mode)
    target = OUT / (mode + '.dump')
    process = subprocess.run([str(BUNDLE / 'mpv.exe'), '--no-config', '--load-scripts=no',
        '--vo=null', '--ao=null', '--script=' + str(POLICY), '--msg-level=all=warn',
        '--stream-dump=' + str(target), url], cwd=BUNDLE, capture_output=True, timeout=25,
        creationflags=subprocess.CREATE_NO_WINDOW)
    (OUT / (mode + '.log')).write_bytes(process.stdout + process.stderr)
    saved = target.read_bytes() if target.exists() else b''
    if mode in ('complete', 'resume'):
        check(saved == MEDIA, mode + ': resumed bytes differ from the original')
        check(len(data.requests) == (1 if mode == 'complete' else 2), mode + ': unnecessary requests')
    elif mode.startswith('ignore'):
        check(saved == MEDIA[:CUT], mode + ': accepted data from the wrong offset')
        check(2 <= len(data.requests) <= 4, mode + ': retries not bounded')
        check(all(item['offset'] == CUT for item in data.requests[1:]), mode + ': retry started at the beginning')
    elif mode == 'nonseekable':
        check(saved == MEDIA[:CUT] and len(data.requests) == 1, 'nonseekable stream was restarted')
    else:
        check(not saved and len(data.requests) == 1, 'authorization refusal was retried')
    check(data.max_active == 1, mode + ': concurrent media readers')
    check(all(item['query'] == 'test_signature=preserved' for item in data.requests), 'signed query changed')
    return {'bytes': len(saved), 'requests': data.requests, 'max_concurrent': data.max_active}


def cache_stall():
    data, url = fixture('cache-stall', 'stall')
    try:
        with NativePlayer() as player:
            player.command('loadfile', url)
            player.wait(lambda: player.get('paused-for-cache') == 'yes' and
                        float(player.get('time-pos') or 0) > 6, 'did not reach a real cache underrun')
            position = float(player.get('time-pos'))
            check(player.get('curl-enabled') == 'no', 'production transport was not applied')
            check(player.get('eof-reached') == 'no', 'cache underrun was treated as EOF')
            player.pump()
            check(player.events.count(8) == 1, 'media reopened before recovery')
            time.sleep(.25)
            player.pump()
            check(abs(float(player.get('time-pos')) - position) < .1, 'position changed while buffering')
            data.release.set()
            player.wait(lambda: float(player.get('time-pos') or 0) > 10, 'did not continue beyond the dropped connection')
            check(all(b >= a - .05 for a, b in zip(player.positions, player.positions[1:])), 'playback jumped backwards')
            check(player.events.count(8) == 1 and 20 not in player.events, 'recovery reloaded or sought the video')
            check(len(data.requests) == 2 and data.requests[1]['offset'] == CUT, 'did not resume at the next missing byte')
            check(data.max_active == 1, 'recovery used concurrent media readers')
            check(all(item['authorization'] == 'LocalResumeTest' for item in data.requests), 'authorization lost on resume')
            check(all(item['query'] == 'test_signature=preserved' for item in data.requests), 'signed query lost on resume')
            network_options = player.get('stream-lavf-o')
            player.command('loadfile', str(LOCAL))
            player.wait(lambda: player.get('path') == str(LOCAL) and player.events.count(8) == 2, 'local file did not open')
            check(player.get('curl-enabled') == 'yes', 'file-local transport policy leaked to local playback')
            check(player.get('stream-lavf-o') != network_options, 'file-local retry options leaked to the next file')
            return {'buffered_position': position, 'resumed_position': max(player.positions),
                    'requests': data.requests, 'max_concurrent': data.max_active}
    finally:
        data.release.set()


def decoder_resume(mode):
    encoded = OUT / 'sample.mkv'
    if not encoded.exists():
        result = subprocess.run([str(BUNDLE / 'mpv.exe'), '--no-config', '--load-scripts=no',
            '--hwdec=no', '--ovc=ffv1', '--o=' + str(encoded), str(LOCAL)], cwd=BUNDLE,
            capture_output=True, timeout=30, creationflags=subprocess.CREATE_NO_WINDOW)
        check(result.returncode == 0, 'could not encode the local Matroska fixture')
    media = encoded.read_bytes()
    data, url = fixture('decoder-' + mode, 'stall-' + mode, media, len(media) * 2 // 3)
    try:
        with NativePlayer() as player:
            player.command('loadfile', url)
            player.wait(lambda: player.get('paused-for-cache') == 'yes' and
                        float(player.get('time-pos') or 0) > 6, 'Matroska did not exhaust its forward cache')
            before = float(player.get('time-pos'))
            data.release.set()
            if mode == 'resume':
                player.wait(lambda: float(player.get('time-pos') or 0) > 10, 'Matroska did not resume at its timestamp')
            else:
                player.wait(lambda: player.get('eof-reached') == 'yes' and player.get('pause') == 'yes',
                            'invalid range did not stop at the interrupted position', seconds=12)
            check(all(b >= a - .05 for a, b in zip(player.positions, player.positions[1:])),
                  'decoder accepted a timestamp from the beginning of the file')
            check(player.events.count(8) == 1 and 20 not in player.events, 'decoder recovery reopened or sought the video')
            check(data.max_active == 1, 'decoder recovery used concurrent media readers')
            return {'position_before': before, 'position_after': float(player.get('time-pos')),
                    'requests': data.requests, 'max_concurrent': data.max_active}
    finally:
        data.release.set()


def case(name, action):
    try:
        RESULTS.append({'case': name, 'passed': True, **action()})
        print('PASS', name, flush=True)
    except Exception as error:
        RESULTS.append({'case': name, 'passed': False, 'error': str(error)})
        print('FAIL', name, str(error), flush=True)


try:
    for mode in ('complete', 'resume', 'ignore-200', 'ignore-206', 'nonseekable', 'denied'):
        case(mode, lambda: dump_case(mode))
    case('cache-stall', cache_stall)
    for mode in ('resume', 'ignore-200', 'ignore-206'):
        case('decoder-' + mode, lambda: decoder_resume(mode))
finally:
    for value in FIXTURES.values():
        value.release.set()
    SERVER.shutdown()
    SERVER.server_close()
    (OUT / 'results.json').write_text(json.dumps(RESULTS, indent=2), encoding='utf-8')
if not all(result['passed'] for result in RESULTS):
    raise SystemExit(1)

"""Exercise mpv's session history with local generated media and null outputs."""
from pathlib import Path
import ctypes as C
import json
import os
import sys
import threading
import time
import wave


class Event(C.Structure):
    _fields_ = [('event_id', C.c_int), ('error', C.c_int),
                ('reply_userdata', C.c_uint64), ('data', C.c_void_p)]


ReadCallback = C.CFUNCTYPE(C.c_int64, C.c_void_p, C.c_void_p, C.c_uint64)
SeekCallback = C.CFUNCTYPE(C.c_int64, C.c_void_p, C.c_int64)
SizeCallback = C.CFUNCTYPE(C.c_int64, C.c_void_p)
CloseCallback = C.CFUNCTYPE(None, C.c_void_p)


class StreamInfo(C.Structure):
    _fields_ = [('cookie', C.c_void_p), ('read_fn', ReadCallback),
                ('seek_fn', SeekCallback), ('size_fn', SizeCallback),
                ('close_fn', CloseCallback), ('cancel_fn', C.c_void_p)]


OpenCallback = C.CFUNCTYPE(C.c_int, C.c_void_p, C.c_char_p, C.POINTER(StreamInfo))


class MemoryStream:
    """Count calls to mpv's original input without serving any network traffic."""
    def __init__(self, player, data):
        self.data, self.position = data, 0
        self.calls = dict(open=0, read=0, seek=0, size=0, close=0)
        self.eof = threading.Event()

        @ReadCallback
        def read(cookie, buffer, count):
            self.calls['read'] += 1
            chunk = self.data[self.position:self.position + count]
            C.memmove(buffer, chunk, len(chunk))
            self.position += len(chunk)
            if not chunk:
                self.eof.set()
            return len(chunk)

        @SeekCallback
        def seek(cookie, offset):
            self.calls['seek'] += 1
            self.position = offset
            return offset

        @SizeCallback
        def size(cookie):
            self.calls['size'] += 1
            return len(self.data)

        @CloseCallback
        def close(cookie):
            self.calls['close'] += 1

        @OpenCallback
        def open_stream(userdata, uri, info):
            self.calls['open'] += 1
            self.position = 0
            info[0] = StreamInfo(None, read, seek, size, close, None)
            return 0

        self.callbacks = (read, seek, size, close, open_stream)
        register = player.lib.mpv_stream_cb_add_ro
        register.restype = C.c_int
        register.argtypes = [C.c_void_p, C.c_char_p, C.c_void_p, OpenCallback]
        assert register(player.handle, b'history-memory', None, open_stream) >= 0


class Player:
    def __init__(self, bundle, history, resume_directory):
        self.directory = os.add_dll_directory(str(bundle)) if hasattr(os, 'add_dll_directory') else None
        self.lib = C.CDLL(str(bundle / 'libmpv-2.dll'))
        specs = {
            'mpv_create': (C.c_void_p, []),
            'mpv_initialize': (C.c_int, [C.c_void_p]),
            'mpv_set_option_string': (C.c_int, [C.c_void_p, C.c_char_p, C.c_char_p]),
            'mpv_set_property_string': (C.c_int, [C.c_void_p, C.c_char_p, C.c_char_p]),
            'mpv_get_property_string': (C.c_void_p, [C.c_void_p, C.c_char_p]),
            'mpv_free': (None, [C.c_void_p]),
            'mpv_command': (C.c_int, [C.c_void_p, C.POINTER(C.c_char_p)]),
            'mpv_wait_event': (C.POINTER(Event), [C.c_void_p, C.c_double]),
            'mpv_terminate_destroy': (None, [C.c_void_p]),
        }
        for name, (result, arguments) in specs.items():
            function = getattr(self.lib, name)
            function.restype, function.argtypes = result, arguments
        self.handle = self.lib.mpv_create()
        assert self.handle
        options = {'config': 'no', 'vo': 'null', 'ao': 'null', 'hwdec': 'no',
                   'idle': 'yes', 'terminal': 'no', 'load-scripts': 'no',
                   'osc': 'no', 'pause': 'yes', 'keep-open': 'no',
                   'save-watch-history': 'yes', 'watch-history-path': str(history),
                   'watch-later-directory': str(resume_directory)}
        for name, value in options.items():
            assert self.lib.mpv_set_option_string(self.handle, name.encode(), value.encode('utf-8')) >= 0, name
        assert self.lib.mpv_initialize(self.handle) >= 0

    def command(self, *args):
        values = (C.c_char_p * (len(args) + 1))(*[str(x).encode('utf-8') for x in args], None)
        assert self.lib.mpv_command(self.handle, values) >= 0, args

    def set(self, name, value):
        assert self.lib.mpv_set_property_string(self.handle, name.encode(), str(value).encode('utf-8')) >= 0, name

    def get(self, name):
        value = self.lib.mpv_get_property_string(self.handle, name.encode())
        if not value:
            return None
        try:
            return C.string_at(value).decode('utf-8')
        finally:
            self.lib.mpv_free(value)

    def drain(self):
        while self.lib.mpv_wait_event(self.handle, 0).contents.event_id:
            pass

    def wait(self, event_id):
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            event = self.lib.mpv_wait_event(self.handle, .1).contents
            if event.event_id == event_id:
                return
        raise AssertionError(f'mpv event {event_id} did not arrive')

    def load(self, path, start):
        self.drain()
        self.set('pause', 'yes')
        self.set('start', start)
        self.command('loadfile', path)
        self.wait(21)  # MPV_EVENT_PLAYBACK_RESTART: initial seek has actually completed.

    def seek(self, position):
        self.drain()
        self.command('seek', position, 'absolute+exact')
        self.wait(21)
        assert abs(float(self.get('time-pos')) - position) < .1

    def stop(self):
        self.drain()
        self.command('stop')
        self.wait(7)  # MPV_EVENT_END_FILE: native history has been flushed.

    def close(self):
        if self.handle:
            self.lib.mpv_terminate_destroy(self.handle)
            self.handle = None


def main():
    bundle, output = [Path(x).resolve() for x in sys.argv[1:3]]
    output.mkdir(parents=True, exist_ok=True)
    history = output / 'watch_history.jsonl'
    assert not history.exists(), 'Use a fresh output directory to preserve prior test evidence'
    media = output / '测试影片.y4m'
    media.write_bytes(b'YUV4MPEG2 W16 H16 F24:1 Ip A1:1 C420jpeg\n' +
                      (b'FRAME\n' + bytes([100]) * 256 + bytes([128]) * 128) * 24 * 30)
    records = lambda: [json.loads(line) for line in history.read_text(encoding='utf-8').splitlines()] if history.exists() else []
    player = Player(bundle, history, output / 'watch_later')
    try:
        title = '测试影片 "第 1 集"'
        player.set('force-media-title', title)
        # Every reopen remains a separate entry, including backward endpoints.
        for start, seeks in [(5, [20]), (20, [5]), (5, [25, 10])]:
            before = len(records())
            player.load(media, start)
            for position in seeks:
                player.seek(position)
            assert len(records()) == before, 'Incomplete session was appended prematurely'
            player.stop()
            entry = records()[-1]
            assert abs(entry['start'] - start) < .1, entry
            assert abs(entry['end'] - seeks[-1]) < .1, entry
            assert abs(entry['duration'] - 30) < .1, entry
            assert entry['title'] == title and entry['path'] == str(media), entry
            assert entry['end_time'] >= entry['time'] > 0, entry
            info = entry['media_info']
            assert info['container'] == 'yuv4mpegpipe', info
            assert len(info['tracks']) == 1, info
            video = info['tracks'][0]
            assert video['type'] == 'video' and video['codec'] == 'rawvideo', video
            assert (video['width'], video['height'], video['fps']) == (16, 16, 24), video
            assert video['selected'] and 'sample_rate' not in video and 'language' not in video, video
        assert len(records()) == 3

        # Restore from mpv's real watch-later file, not a fabricated start value.
        player.load(media, 0)
        player.seek(13)
        player.command('write-watch-later-config')
        player.stop()
        player.load(media, 'none')
        assert abs(float(player.get('time-pos')) - 13) < .1
        player.seek(18)
        player.stop()
        assert abs(records()[-1]['start'] - 13) < .1, records()[-1]

        # Natural EOF, quit while paused, and a failed open each take distinct paths.
        player.load(media, 29)
        player.drain()
        player.set('pause', 'no')
        player.wait(7)
        assert records()[-1]['end'] >= 29.8, records()[-1]
        before = len(records())
        player.drain()
        player.command('loadfile', output / 'missing-media.mkv')
        player.wait(7)
        assert len(records()) == before, 'Failed open created a playback record'
        player.load(media, 17)
        player.close()
        assert len(records()) == before + 1
        assert abs(records()[-1]['start'] - 17) < .1 and abs(records()[-1]['end'] - 17) < .1
    finally:
        player.close()

    before = len(records())
    player = Player(bundle, history, output / 'watch_later')
    try:
        player.set('save-watch-history', 'no')
        player.load(media, 7)
        player.stop()
        assert len(records()) == before, 'Native opt-out was ignored'
    finally:
        player.close()

    audio = output / '测试音轨.wav'
    with wave.open(str(audio), 'wb') as wav:
        wav.setparams((2, 2, 48000, 0, 'NONE', 'not compressed'))
        wav.writeframes(bytes(48000 * 2 * 2 * 2))
    player = Player(bundle, history, output / 'watch_later')
    try:
        player.load(audio, 0)
        player.stop()
        info = records()[-1]['media_info']
        assert info['container'] == 'wav', info
        sound = info['tracks'][0]
        assert sound['type'] == 'audio' and sound['codec'] == 'pcm_s16le', sound
        assert sound['sample_rate'] == 48000 and sound['channels'] == 2, sound
        assert 'width' not in sound and 'fps' not in sound, sound

        # Once the original input is buffered, saving the snapshot must not
        # reopen it, read another byte, seek, or even ask for its file size.
        stream = MemoryStream(player, media.read_bytes())
        player.set('demuxer-readahead-secs', '60')
        player.load('history-memory://fixture.y4m', 0)
        assert stream.eof.wait(5), 'Original input did not reach EOF while buffering'
        before = stream.calls.copy()
        player.stop()
        assert stream.calls['open'] == 1
        assert {k: v for k, v in stream.calls.items() if k != 'close'} == {
            k: v for k, v in before.items() if k != 'close'}, stream.calls
        assert stream.calls['close'] == 1
        assert records()[-1]['media_info']['tracks'][0]['width'] == 16
        stream_access = {'before_history': before, 'after_history': stream.calls.copy()}
    finally:
        player.close()
    report = {'passed': True, 'sessions': len(records()), 'stream_access': stream_access,
              'cases': ['initial-seek', 'forward-range', 'backward-range', 'final-not-furthest',
                        'duplicate-open', 'watch-later-resume', 'eof', 'failed-open',
                        'paused-quit', 'history-opt-out', 'unicode-title',
                        'video-metadata', 'audio-metadata', 'no-extra-stream-access']}
    (output / 'results.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report, ensure_ascii=False))


if __name__ == '__main__':
    main()

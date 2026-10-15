"""Render the real libmpv subtitle composition into memory, without a desktop window."""
from pathlib import Path
import ctypes as C
import json
import os
import shutil
import subprocess
import tempfile
import threading
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]
INSTALL = Path(os.environ.get('ANIMEJANAI_INSTALL', r'D:\Apps\mpv-AnimeJaNai'))
CONFIG = Path(os.environ.get('ANIMEJANAI_PLAYER_CONFIG', ROOT / 'portable_config/mpv.conf'))
SCRIPT = Path(os.environ.get('ANIMEJANAI_DANMAKU_SCRIPT', ROOT / 'portable_config/scripts/player_ui_danmaku.lua'))


class Param(C.Structure):
    _fields_ = [('type', C.c_int), ('data', C.c_void_p)]


class Viewport(unittest.TestCase):
    def test_screen_composition_and_resize(self):
        # Python hosts libmpv here, so match the player's UTF-8 narrow file APIs.
        # Leave LC_NUMERIC in C, as required by the libmpv client API.
        runtime = C.CDLL('ucrtbase')
        runtime.setlocale.argtypes = [C.c_int, C.c_char_p]
        runtime.setlocale.restype = C.c_char_p
        original_locale = runtime.setlocale(2, None)  # LC_CTYPE
        self.assertIsNotNone(runtime.setlocale(2, b'.UTF8'))
        self.addCleanup(runtime.setlocale, 2, original_locale)
        directory = os.add_dll_directory(str(INSTALL))
        dll = C.CDLL(str(INSTALL / 'app/libmpv-2.dll'))
        signatures = {
            'mpv_create': (C.c_void_p, []),
            'mpv_set_option_string': (C.c_int, [C.c_void_p, C.c_char_p, C.c_char_p]),
            'mpv_initialize': (C.c_int, [C.c_void_p]),
            'mpv_command': (C.c_int, [C.c_void_p, C.POINTER(C.c_char_p)]),
            'mpv_get_property_string': (C.c_void_p, [C.c_void_p, C.c_char_p]),
            'mpv_free': (None, [C.c_void_p]),
            'mpv_terminate_destroy': (None, [C.c_void_p]),
            'mpv_render_context_create': (C.c_int, [C.POINTER(C.c_void_p), C.c_void_p, C.POINTER(Param)]),
            'mpv_render_context_render': (C.c_int, [C.c_void_p, C.POINTER(Param)]),
            'mpv_render_context_free': (None, [C.c_void_p]),
        }
        for name, (result, args) in signatures.items():
            fn = getattr(dll, name); fn.restype = result; fn.argtypes = args

        def property(handle, name):
            pointer = dll.mpv_get_property_string(handle, name.encode())
            if not pointer:
                return None
            value = C.string_at(pointer).decode('utf-8'); dll.mpv_free(pointer)
            return value

        def command(handle, *args):
            values = (C.c_char_p * (len(args) + 1))(*[str(a).encode('utf-8') for a in args], None)
            self.assertGreaterEqual(dll.mpv_command(handle, values), 0, args)

        with tempfile.TemporaryDirectory(prefix='mpv-viewport-') as tmp:
            app = Path(tmp); config = app / 'portable_config'; config.mkdir()
            shutil.copy2(CONFIG, config / 'mpv.conf')
            shutil.copy2(INSTALL / 'portable_config/mpv-animejanai.conf', config / 'mpv-animejanai.conf')
            converter = app / 'animejanai/danmaku'; converter.mkdir(parents=True)
            shutil.copy2(INSTALL / 'animejanai/danmaku/DanmakuFactory.exe', converter / 'DanmakuFactory.exe')
            clip = app / 'black.y4m'
            frame = b'FRAME\n' + b'\x10' * (160 * 90) + b'\x80' * (160 * 90 // 2)
            clip.write_bytes(b'YUV4MPEG2 W160 H90 F24:1 Ip A1:1 C420jpeg\n' + frame * (24 * 20))
            xml = app / 'comments.xml'
            xml.write_text('<i><d p="0,1,25,16777215">BLACK BAR 弹幕</d></i>', encoding='utf-8')
            original_env = {name: os.environ.get(name) for name in ('LOCALAPPDATA', 'TEMP')}
            os.environ['LOCALAPPDATA'] = str(app); os.environ['TEMP'] = str(app)
            handle = dll.mpv_create(); self.assertTrue(handle)
            for name, value in {'config': 'yes', 'config-dir': str(config), 'vo': 'libmpv', 'hwdec': 'no', 'vf': '', 'ao': 'null',
                                'load-scripts': 'no', 'scripts': str(SCRIPT), 'idle': 'yes', 'pause': 'yes',
                                'script-opts': 'player_ui_danmaku-autoload_danmaku=no', 'osd-level': '0',
                                'log-file': str(app / 'mpv.log')}.items():
                self.assertGreaterEqual(dll.mpv_set_option_string(handle, name.encode(), value.encode('utf-8')), 0, name)
            self.assertGreaterEqual(dll.mpv_initialize(handle), 0)
            self.assertEqual(property(handle, 'blend-subtitles'), 'no', 'managed video blending must be overridden')
            # Config files are read during initialize; select the memory VO after profiles apply.
            command(handle, 'set', 'vo', 'libmpv')
            command(handle, 'set', 'ao', 'null')
            self.assertEqual(property(handle, 'options/vo'), 'libmpv')
            # Keep normal client commands on this thread and rendering on a separate thread.
            # The software render API writes memory only; it never creates a desktop window.
            context = C.c_void_p(); api = C.create_string_buffer(b'sw')
            params = (Param * 2)(Param(1, C.cast(api, C.c_void_p)), Param(0, None))
            self.assertGreaterEqual(dll.mpv_render_context_create(C.byref(context), handle, params), 0)
            stop = threading.Event(); ready = threading.Event(); state = {'size': (640, 400), 'pixels': b''}
            errors = []

            def render():
                try:
                    while not stop.is_set():
                        w, h = state['size']; size = (C.c_int * 2)(w, h)
                        stride = C.c_size_t(w * 4); format = C.create_string_buffer(b'rgb0')
                        storage = C.create_string_buffer(w * h * 4 + 64)
                        address = (C.addressof(storage) + 63) & ~63
                        params = (Param * 5)(Param(17, C.cast(size, C.c_void_p)), Param(18, C.cast(format, C.c_void_p)),
                                            Param(19, C.cast(C.byref(stride), C.c_void_p)), Param(20, address), Param(0, None))
                        result = dll.mpv_render_context_render(context, params)
                        if result < 0:
                            raise RuntimeError('render failed: ' + str(result))
                        state['pixels'] = C.string_at(address, w * h * 4); ready.set()
                        stop.wait(.025)
                except BaseException as error:
                    errors.append(error); ready.set()

            thread = threading.Thread(target=render); thread.start()
            try:
                command(handle, 'set', 'vf', '')
                command(handle, 'set', 'hwdec', 'no')
                command(handle, 'loadfile', clip)
                deadline = time.monotonic() + 15
                while property(handle, 'time-pos') is None and time.monotonic() < deadline:
                    time.sleep(.02)
                self.assertIsNotNone(property(handle, 'time-pos'), (app / 'mpv.log').read_text(errors='replace'))
                self.assertEqual(property(handle, 'current-vo'), 'libmpv')
                command(handle, 'script-message', 'player_ui-danmaku-load', xml)
                command(handle, 'seek', '6', 'absolute+exact')
                original_track = None
                for w, h in [(640, 400), (2560, 1600), (960, 540), (640, 400)]:
                    state['size'] = (w, h)
                    deadline = time.monotonic() + 12
                    rendered = False
                    while time.monotonic() < deadline:
                        if errors:
                            raise errors[0]
                        pixels = state['pixels']
                        top = round((h - w * 9 / 16) / 2)
                        # At 6 seconds a single scrolling comment is horizontally centered.
                        # A black test video leaves only subtitle RGB coverage in the upper strip.
                        rows = max(1, top) if top else round(h * .05)
                        danmaku = json.loads(property(handle, 'user-data/player_ui/danmaku') or '{}')
                        if original_track is not None:
                            self.assertEqual(danmaku.get('track'), original_track, 'resizing must keep the same subtitle track')
                            self.assertFalse(danmaku.get('render_pending'), 'resizing must not regenerate the ASS')
                        if danmaku.get('track') and not danmaku.get('render_pending') and len(pixels) == w * h * 4 and any(pixels[i] > 40 for i in range(0, rows * w * 4) if i % 4 != 3):
                            rendered = True; break
                        time.sleep(.05)
                    self.assertTrue(rendered, f'danmaku pixels missing at {w}x{h}: ' + (app / 'mpv.log').read_text(errors='replace')[-2500:])
                    dimensions = json.loads(property(handle, 'osd-dimensions'))
                    self.assertEqual((dimensions['w'], dimensions['h']), (w, h))
                    original_track = danmaku['track']
                    time.sleep(.4)
                    stable = json.loads(property(handle, 'user-data/player_ui/danmaku'))
                    self.assertEqual(stable['track'], original_track, 'no delayed track replacement after resizing')
                    self.assertFalse(stable['render_pending'])
                    print(f'Native screen composition: {w}x{h}, top margin {top}, danmaku pixels visible')
            finally:
                stop.set(); thread.join(15)
                dll.mpv_render_context_free(context); dll.mpv_terminate_destroy(handle); directory.close()
                for name, value in original_env.items():
                    if value is None: os.environ.pop(name, None)
                    else: os.environ[name] = value


if __name__ == '__main__':
    unittest.main()

"""Check the packaged Lua search flow with synthetic replies on loopback only."""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import tempfile
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = Path(os.environ.get("ANIMEJANAI_DANMAKU_SCRIPT", ROOT / "portable_config/scripts/player_ui_danmaku.lua"))
MPV = Path(os.environ.get('ANIMEJANAI_MPV_EXE',r"D:\Apps\mpv-AnimeJaNai\mpv.exe"))


def lua_string(value: str) -> str:
    return "[[" + value.replace("\\", "/") + "]]"


class Handler(BaseHTTPRequestHandler):
    seen: list[str] = []

    def log_message(self, *_args):
        pass

    def do_GET(self):
        Handler.seen.append(self.path)
        if "/api/v2/search/anime?" in self.path and self.path.startswith("/unused/"):
            body = {"success": True, "animes": []}
        elif "/api/v2/search/anime?" in self.path:
            body = {"success": True, "animes": [
                {"animeId": 101, "animeTitle": "间谍过家家 第一季(2022)【日番】from qq",
                 "source": "qq", "episodeCount": 25},
                {"animeId": 202, "animeTitle": "间谍过家家 第二季(2023)【日番】from qq&iqiyi",
                 "source": "qq", "episodeCount": 12,
                 "mergedChildren": [{"animeId": 203, "source": "iqiyi", "episodes": 12}]},
            ]}
        elif self.path == "/api/v2/bangumi/203":
            body = {"success": True, "bangumi": {"episodes": [
                {"episodeId": 77, "episodeNumber": 12, "episodeTitle": "第37集_12"},
            ]}}
        elif self.path.startswith("/api/v2/comment/77"):
            body = {"success": True, "comments": [{"p": "1,1,16777215", "m": "测试弹幕"}]}
        else:
            self.send_error(404)
            return
        payload = json.dumps(body, ensure_ascii=False).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(payload)


class AutoloadHandler(BaseHTTPRequestHandler):
    seen: list[str] = []
    match_body: dict[str, object] = {}
    match_count = 0

    def log_message(self, *_args):
        pass

    def reply(self, body):
        payload = json.dumps(body, ensure_ascii=False).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(payload)

    def do_POST(self):
        AutoloadHandler.seen.append(self.path)
        if self.path != "/api/v2/match":
            self.send_error(404)
            return
        size = int(self.headers.get("Content-Length", "0"))
        AutoloadHandler.match_body = json.loads(self.rfile.read(size).decode("utf-8"))
        AutoloadHandler.match_count += 1
        if AutoloadHandler.match_count == 1:
            # Hold the first reply long enough to observe the loading state,
            # then exercise the no-match state before the next file succeeds.
            threading.Event().wait(0.7)
            self.reply({"isMatched": False, "matches": []})
            return
        self.reply({"isMatched": True, "matches": [{
            "animeId": 900, "episodeId": 903,
            "animeTitle": "间谍过家家 第三季(2025)【日番】",
            "episodeTitle": "第3集",
        }]})

    def do_GET(self):
        AutoloadHandler.seen.append(self.path)
        if self.path.startswith("/api/v2/search/anime?"):
            AutoloadHandler.match_count += 1
            time.sleep(.7)
            self.reply({"animes": []} if AutoloadHandler.match_count == 1 else {"animes": [{
                "animeId": 900, "animeTitle": "间谍过家家 第三季(2025)", "source": "qq", "episodeCount": 13}]})
        elif self.path == "/api/v2/bangumi/900":
            self.reply({"success": True, "bangumi": {"episodes": [{
                "episodeId": 903, "episodeNumber": 3, "episodeTitle": "第3集",
            }]}})
        elif self.path.startswith("/api/v2/comment/903"):
            self.reply({"success": True, "comments": [{"p": "1,1,16777215,0", "m": "自动加载弹幕"}]})
        else:
            self.send_error(404)


class TransientAutoloadHandler(BaseHTTPRequestHandler):
    seen: list[str] = []
    match_count = 0

    def log_message(self, *_args):
        pass

    def reply(self, body):
        payload = json.dumps(body, ensure_ascii=False).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(payload)

    def do_POST(self):
        TransientAutoloadHandler.seen.append(self.path)
        size = int(self.headers.get("Content-Length", "0"))
        self.rfile.read(size)
        TransientAutoloadHandler.match_count += 1
        if TransientAutoloadHandler.match_count == 1:
            self.send_error(503)
            return
        self.reply({"isMatched": True, "matches": [{
            "animeId": 990, "episodeId": 995,
            "animeTitle": "尼古喵喵(2026)【日番】",
            "episodeTitle": "第5集",
        }]})

    def do_GET(self):
        TransientAutoloadHandler.seen.append(self.path)
        if self.path == "/api/v2/bangumi/990":
            self.reply({"success": True, "bangumi": {"episodes": [{
                "episodeId": 995, "episodeNumber": 5, "episodeTitle": "第5集",
            }]}})
        elif self.path.startswith("/api/v2/comment/995?"):
            self.reply({"success": True, "comments": [{
                "p": "1,1,16777215,0", "m": "短暂故障后自动载入",
            }]})
        elif self.path.startswith("/api/v2/search/anime?"):
            self.send_error(503)
        else:
            self.send_error(404)


class AutoSearchHandler(BaseHTTPRequestHandler):
    seen: list[str] = []

    def log_message(self, *_args):
        pass

    def do_POST(self):
        AutoSearchHandler.seen.append(self.path)
        size = int(self.headers.get('Content-Length', '0'))
        self.rfile.read(size)
        payload = b'{"isMatched":false,"matches":[]}'
        self.send_response(200)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.send_header('Content-Length', str(len(payload)))
        self.send_header('Connection', 'close')
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self):
        AutoSearchHandler.seen.append(self.path)
        if self.path.startswith('/wrong/api/v2/search/anime?'):
            body = {'animes': [
                {'animeId': 102, 'animeTitle': '尼古喵喵 第二季(2027)from renren',
                 'source': 'renren', 'episodeCount': 12}]}
        elif self.path.startswith('/empty/api/v2/search/anime?'):
            body = {'animes': []}
        elif self.path.startswith('/api/v2/search/anime?'):
            body = {'animes': [
                {'animeId': 101, 'animeTitle': '尼古喵喵(2026)from renren',
                 'source': 'renren', 'episodeCount': 12},
                {'animeId': 102, 'animeTitle': '尼古喵喵 第二季(2027)from renren',
                 'source': 'renren', 'episodeCount': 12},
            ]}
        elif self.path == '/api/v2/bangumi/101':
            body = {'bangumi': {'episodes': [
                {'episodeId': 200 + n, 'episodeNumber': n, 'episodeTitle': f'第{n}集'}
                for n in range(1, 13)]}}
        elif self.path.startswith('/api/v2/comment/205?'):
            body = {'comments': [{'p': '1,1,16777215,0', 'm': '第5集弹幕'}]}
        else:
            self.send_error(404)
            return
        payload = json.dumps(body, ensure_ascii=False).encode('utf-8')
        self.send_response(200)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.send_header('Content-Length', str(len(payload)))
        self.send_header('Connection', 'close')
        self.end_headers()
        self.wfile.write(payload)


class PlatformFallbackHandler(BaseHTTPRequestHandler):
    seen: list[str] = []

    def log_message(self, *_args):
        pass

    def reply(self, body):
        payload = json.dumps(body, ensure_ascii=False).encode('utf-8')
        self.send_response(200)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.send_header('Content-Length', str(len(payload)))
        self.send_header('Connection', 'close')
        self.end_headers()
        self.wfile.write(payload)

    def do_POST(self):
        PlatformFallbackHandler.seen.append(self.path)
        size = int(self.headers.get('Content-Length', '0'))
        self.rfile.read(size)
        self.reply({'isMatched': False, 'matches': []})

    def do_GET(self):
        PlatformFallbackHandler.seen.append(self.path)
        if self.path.startswith('/api/v2/search/anime?'):
            body = {'animes': [{
                'animeId': 101, 'animeTitle': '尼古喵喵(2026)from renren',
                'source': 'renren', 'episodeCount': 12,
                'mergedChildren': [{'animeId': 102, 'source': 'qq', 'episodes': 12}],
            }]}
        elif self.path == '/api/v2/bangumi/101':
            body = {'bangumi': {'episodes': [
                {'episodeId': 205, 'episodeNumber': 5, 'episodeTitle': '第5集'}]}}
        elif self.path == '/api/v2/comment/205?withRelated=true':
            body = {'comments': []}
        elif self.path == '/api/v2/bangumi/102':
            body = {'bangumi': {'episodes': [
                {'episodeId': 305, 'episodeNumber': 5, 'episodeTitle': '第5集'}]}}
        elif self.path == '/api/v2/comment/305?withRelated=true':
            body = {'comments': [{'p': '1,1,16777215,0', 'm': '备用平台弹幕'}]}
        else:
            self.send_error(404)
            return
        self.reply(body)


class RouteFailureHandler(AutoSearchHandler):
    seen: list[str] = []

    def do_POST(self):
        RouteFailureHandler.seen.append(self.path)
        self.send_error(501)

    def do_GET(self):
        RouteFailureHandler.seen.append(self.path)
        if self.path.startswith('/unavailable/api/v2/search/anime?'):
            self.send_error(402)
            return
        if self.path.startswith('/api/v2/search/anime?'):
            body = {'animes': [{'animeId': 101,
                'animeTitle': '尼古喵喵(2026)from renren',
                'source': 'renren', 'episodeCount': 12}]}
        elif self.path == '/api/v2/bangumi/101':
            body = {'bangumi': {'episodes': [
                {'episodeId': 205, 'episodeNumber': 5, 'episodeTitle': '第5集'}]}}
        elif self.path.startswith('/api/v2/comment/205?'):
            body = {'comments': [{'p': '1,1,16777215,0', 'm': '第5集弹幕'}]}
        else:
            self.send_error(404)
            return
        payload = json.dumps(body, ensure_ascii=False).encode('utf-8')
        self.send_response(200)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.send_header('Content-Length', str(len(payload)))
        self.send_header('Connection', 'close')
        self.end_headers()
        self.wfile.write(payload)


class ParallelAutoloadHandler(BaseHTTPRequestHandler):
    seen: list[str] = []
    active = 0
    max_active = 0
    lock = threading.Lock()
    search_delays = (0, 2)
    search_barrier = threading.Barrier(2)

    def log_message(self, *_args):
        pass

    def begin(self):
        with ParallelAutoloadHandler.lock:
            ParallelAutoloadHandler.active += 1
            ParallelAutoloadHandler.max_active = max(
                ParallelAutoloadHandler.max_active, ParallelAutoloadHandler.active)
            ParallelAutoloadHandler.seen.append(self.path)
        time.sleep(.25)

    def reply(self, body):
        payload = json.dumps(body, ensure_ascii=False).encode('utf-8')
        self.send_response(200)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.send_header('Content-Length', str(len(payload)))
        self.send_header('Connection', 'close')
        try:
            self.end_headers()
            self.wfile.write(payload)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass

    def route_path(self):
        return self.path[len('/second'):] if self.path.startswith('/second/') else self.path

    def do_POST(self):
        self.begin()
        self.rfile.read(int(self.headers.get('Content-Length', '0')))
        self.reply({'isMatched': False, 'matches': []})
        with ParallelAutoloadHandler.lock:
            ParallelAutoloadHandler.active -= 1

    def do_GET(self):
        self.begin()
        path = self.route_path()
        second = self.path.startswith('/second/')
        if path.startswith('/api/v2/search/anime?'):
            ParallelAutoloadHandler.search_barrier.wait(timeout=10)
        if path.startswith('/api/v2/search/anime?'): time.sleep(ParallelAutoloadHandler.search_delays[1 if second else 0])
        if path.startswith('/api/v2/search/anime?'):
            body = {'animes': [{'animeId': 202 if second else 101,
                'animeTitle': '尼古喵喵(2026)from qq', 'source': 'qq', 'episodeCount': 12}]}
        elif path == ('/api/v2/bangumi/202' if second else '/api/v2/bangumi/101'):
            body = {'bangumi': {'episodes': [{'episodeId': 305 if second else 205,
                'episodeNumber': 5, 'episodeTitle': '第5集'}]}}
        elif path.startswith('/api/v2/comment/205?') and not second:
            body = {'comments': []}
        elif path.startswith('/api/v2/comment/305?') and second:
            body = {'comments': [{'p': '1,1,16777215,0', 'm': '第二线路弹幕'}]}
        else:
            self.send_error(404)
            with ParallelAutoloadHandler.lock:
                ParallelAutoloadHandler.active -= 1
            return
        self.reply(body)
        with ParallelAutoloadHandler.lock:
            ParallelAutoloadHandler.active -= 1


class DanmakuFlowTest(unittest.TestCase):
    def setUp(self):
        self.runtime=tempfile.TemporaryDirectory(prefix='danmaku-flow-runtime-')
        self.addCleanup(self.runtime.cleanup)
        root=Path(self.runtime.name)
        self.config=root/'portable_config';self.config.mkdir()
        converter=root/'animejanai/danmaku';converter.mkdir(parents=True)
        factory=Path(os.environ.get('DANMAKU_FACTORY',MPV.parent/'animejanai/danmaku/DanmakuFactory.exe'))
        shutil.copy2(factory,converter/'DanmakuFactory.exe')

    def test_selected_route_progress_and_errors_are_scoped(self):
        ScopedSearchHandler.seen = []
        server = ThreadingHTTPServer(('127.0.0.1', 0), ScopedSearchHandler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            with tempfile.TemporaryDirectory(prefix='danmaku-scoped-search-') as directory:
                folder = Path(directory)
                (folder / 'mpv-AnimeFusion-danmaku.conf').write_text('api_servers=' + ','.join(
                    f'http://127.0.0.1:{server.server_port}/{route}|{route}'
                    for route in ('failed', 'empty', 'slow')) + '\n', encoding='utf-8')
                result_path = folder / 'state.json'
                driver = folder / 'driver.lua'
                driver.write_text('''
local utils=require('mp.utils')
local phase=0
local function finish(reason,code)
    local file=assert(io.open(%s,'wb'))
    file:write(utils.format_json({phase=phase,reason=reason}));file:close()
    mp.commandv('quit',code)
end
mp.add_timeout(.5,function()
    mp.commandv('script-message','player_ui-danmaku-search-query','状态测试','0','')
end)
mp.add_periodic_timer(.05,function()
    local data=mp.get_property_native('user-data/player_ui/danmaku') or {}
    if phase==0 and data.search_failed==1 and data.search_responded==1 and data.search_pending==1 then
        phase=1;mp.commandv('script-message','player_ui-danmaku-search-filter','2')
    elseif phase==1 and data.search_source==2 then
        if data.search_pending~=0 or data.search_failed~=0 or data.search_responded~=1
            or data.search_total~=1 or #data.results~=0 then finish('empty route inherited another route state',1);return end
        phase=2;mp.commandv('script-message','player_ui-danmaku-search-filter','1')
    elseif phase==2 and data.search_source==1 then
        if data.search_failed~=1 or data.search_pending~=0 or data.search_responded~=0
            or not data.status:find('服务器维护中',1,true) then finish('failed route lost server error',1);return end
        phase=3;mp.commandv('script-message','player_ui-danmaku-search-filter','3')
    elseif phase==3 and data.search_source==3 then
        if data.search_pending~=1 or data.search_failed~=0 then finish('loading route inherited stale failure',1);return end
        phase=4
    elseif phase==4 and data.search_pending==0 and #data.results==1 then
        if data.search_failed~=0 or data.search_responded~=1 or data.results[1].season~=3 then
            finish('loaded route has inconsistent result state',1);return end
        finish('ok',0)
    end
end)
mp.add_timeout(15,function()finish('timeout',1)end)
''' % lua_string(str(result_path)), encoding='utf-8')
                env = {**os.environ, 'LOCALAPPDATA': directory}
                proc = subprocess.run([str(MPV), f'--config-dir={self.config}', '--load-scripts=no',
                    '--idle=yes', '--vo=null', f'--script={SCRIPT}', f'--script={driver}'],
                    env=env, capture_output=True, timeout=20)
                outcome = json.loads(result_path.read_text(encoding='utf-8')) if result_path.exists() else {}
                self.assertEqual(proc.returncode, 0, (outcome, proc.stderr.decode('utf-8', 'replace')[-1200:]))
                self.assertEqual(outcome, {'phase': 4, 'reason': 'ok'})
                self.assertEqual(len(ScopedSearchHandler.seen), 3, 'Changing filters must not issue more requests')
        finally:
            server.shutdown(); server.server_close(); thread.join(timeout=2)

    def test_cancelled_search_clears_loading_state(self):
        ScopedSearchHandler.seen = []
        server = ThreadingHTTPServer(('127.0.0.1', 0), ScopedSearchHandler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            with tempfile.TemporaryDirectory(prefix='danmaku-cancel-search-') as directory:
                folder = Path(directory)
                (folder / 'mpv-AnimeFusion-danmaku.conf').write_text(
                    f'api_servers=http://127.0.0.1:{server.server_port}/slow|slow\n', encoding='utf-8')
                result_path = folder / 'state.json'
                driver = folder / 'driver.lua'
                driver.write_text('''
local utils=require('mp.utils')
local phase=0
local function finish(reason,code)
    local file=assert(io.open(%s,'wb'))
    file:write(utils.format_json({phase=phase,reason=reason}));file:close()
    mp.commandv('quit',code)
end
mp.add_timeout(.5,function()
    mp.commandv('script-message','player_ui-danmaku-search-query','取消测试','0','')
end)
mp.add_periodic_timer(.05,function()
    local data=mp.get_property_native('user-data/player_ui/danmaku') or {}
    if phase==0 and data.search_pending==1 then
        phase=1;mp.commandv('script-message','player_ui-danmaku-clear')
    elseif phase==1 and data.status=='' then
        finish(data.search_pending==0 and 'ok' or 'cancelled search stayed loading',data.search_pending==0 and 0 or 1)
    end
end)
mp.add_timeout(10,function()finish('timeout',1)end)
''' % lua_string(str(result_path)), encoding='utf-8')
                proc = subprocess.run([str(MPV), f'--config-dir={self.config}', '--load-scripts=no',
                    '--idle=yes', '--vo=null', f'--script={SCRIPT}', f'--script={driver}'],
                    env={**os.environ, 'LOCALAPPDATA': directory}, capture_output=True, timeout=15)
                outcome = json.loads(result_path.read_text(encoding='utf-8')) if result_path.exists() else {}
                self.assertEqual(proc.returncode, 0, (outcome, proc.stderr.decode('utf-8', 'replace')[-1200:]))
                self.assertEqual(outcome, {'phase': 1, 'reason': 'ok'})
        finally:
            server.shutdown(); server.server_close(); thread.join(timeout=2)

    def test_movie_titles_without_episode_markers_search_automatically(self):
        Handler.seen = []
        server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            with tempfile.TemporaryDirectory(prefix='danmaku-movie-title-') as directory:
                folder = Path(directory)
                names = ['间谍过家家 代号：白 (2023).yuv', '动画电影 (2026).yuv']
                samples = []
                for name in names:
                    sample = folder / name
                    sample.write_bytes((bytes([16]) * 256 + bytes([128]) * 128) * 60)
                    samples.append(lua_string(str(sample)))
                (folder / 'mpv-AnimeFusion-danmaku.conf').write_text(
                    f'api_servers=http://127.0.0.1:{server.server_port}|本地回归\n', encoding='utf-8')
                result_path = folder / 'state.json'
                driver = folder / 'driver.lua'
                driver.write_text('''
local utils=require('mp.utils')
local samples={%s}
local records={}
local index=1
local function finish(code)
    local file=assert(io.open(%s,'wb'))
    file:write(utils.format_json(records));file:close();mp.commandv('quit',code)
end
local awaiting=false
mp.register_event('file-loaded',function()awaiting=true end)
mp.add_periodic_timer(.1,function()
    local data=mp.get_property_native('user-data/player_ui/danmaku') or {}
    if awaiting and data.autoload_state=='not-found' then
        awaiting=false
        records[#records+1]={state=data.autoload_state,status=data.status,loaded=data.loaded}
        index=index+1
        if samples[index] then mp.commandv('loadfile',samples[index],'replace') else finish(0) end
    end
end)
mp.add_timeout(.25,function()mp.commandv('loadfile',samples[index],'replace')end)
mp.add_timeout(20,function()finish(1)end)
''' % (','.join(samples), lua_string(str(result_path))), encoding='utf-8')
                executable = Path(os.environ.get('ANIMEJANAI_MPV_EXE', MPV))
                startupinfo = None
                if os.name == 'nt':
                    startupinfo = subprocess.STARTUPINFO()
                    startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
                    startupinfo.wShowWindow = subprocess.SW_HIDE
                env = os.environ.copy()
                env['LOCALAPPDATA'] = directory
                proc = subprocess.run([
                    str(executable), *(['--process-instance=multi'] if executable.name.lower() == 'mpv-AnimeFusion.exe' else []),
                    f'--config-dir={self.config}', '--load-scripts=no', '--idle=yes',
                    '--vo=null', '--ao=null', '--demuxer=rawvideo', '--demuxer-rawvideo-w=16',
                    '--demuxer-rawvideo-h=16', '--demuxer-rawvideo-format=I420',
                    '--demuxer-rawvideo-fps=1', f'--script={SCRIPT}', f'--script={driver}',
                ], env=env, capture_output=True, startupinfo=startupinfo, timeout=25)
                self.assertEqual(proc.returncode, 0, proc.stderr.decode('utf-8', 'replace'))
                records = json.loads(result_path.read_text(encoding='utf-8'))
                self.assertEqual(len(records), len(names))
                for name, record in zip(names, records):
                    self.assertEqual(record['state'], 'not-found', (name, record))
                    self.assertEqual(record['status'], '没有匹配到弹幕', (name, record))
                    self.assertFalse(record['loaded'], (name, record))
                self.assertEqual(len(Handler.seen), len(names), 'each movie starts exactly one route search')
                self.assertTrue(all('/api/v2/search/anime?' in path for path in Handler.seen))
                if os.environ.get('ANIMEJANAI_TEST_EVIDENCE'):
                    evidence = Path(os.environ['ANIMEJANAI_TEST_EVIDENCE'])
                    evidence.mkdir(parents=True, exist_ok=True)
                    (evidence / 'movie-title.json').write_text(json.dumps(
                        {'names': names, 'states': records, 'requests': Handler.seen},
                        ensure_ascii=False, indent=2), encoding='utf-8')
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)

    def test_automatic_match_queries_all_routes_in_parallel_and_skips_empty_route(self):
        self.check_parallel_routes((0, 2), True)

    def test_fast_nonempty_route_loads_before_slow_high_priority_search(self):
        self.check_parallel_routes((15, 0), False)

    def check_parallel_routes(self, delays, empty_route_expected):
        ParallelAutoloadHandler.search_delays = delays
        ParallelAutoloadHandler.seen = []
        ParallelAutoloadHandler.active = 0
        ParallelAutoloadHandler.max_active = 0
        ParallelAutoloadHandler.search_barrier = threading.Barrier(2)
        server = ThreadingHTTPServer(('127.0.0.1', 0), ParallelAutoloadHandler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            with tempfile.TemporaryDirectory(prefix='animejanai-parallel-autoload-') as directory:
                folder = Path(directory)
                sample = folder / '尼古喵喵 (2026) S1E5.yuv'
                sample.write_bytes((bytes([16]) * 256 + bytes([128]) * 128) * 60)
                (folder / 'mpv-AnimeFusion-danmaku.conf').write_text(
                    f'api_servers=http://127.0.0.1:{server.server_port}|第一线路,'
                    f'http://127.0.0.1:{server.server_port}/second|第二线路\n', encoding='utf-8')
                result_path = folder / 'state.json'
                driver = folder / 'driver.lua'
                driver.write_text('''
local utils=require('mp.utils')
local output=%s
local sample=%s
local function finish(value,code)
    local file=assert(io.open(output,'wb'))
    file:write(utils.format_json(value));file:close()
    mp.commandv('quit',code)
end
mp.add_timeout(.25,function()mp.commandv('loadfile',sample,'replace')end)
mp.add_periodic_timer(.05,function()
    local data=mp.get_property_native('user-data/player_ui/danmaku')
    if type(data)=='table' and data.loaded and data.count==1 then
        finish({count=data.count,state=data.autoload_state,source=data.source},0)
    end
end)
mp.add_timeout(30,function()
    local data=mp.get_property_native('user-data/player_ui/danmaku') or {}
    finish({state=data.autoload_state,status=data.status},1)
end)
''' % (lua_string(str(result_path)), lua_string(str(sample))), encoding='utf-8')
                env = os.environ.copy()
                env['LOCALAPPDATA'] = directory
                proc = subprocess.run([
                    str(MPV), f'--config-dir={self.config}', '--load-scripts=no', '--idle=yes',
                    '--vo=null', '--ao=null', '--demuxer=rawvideo',
                    '--demuxer-rawvideo-w=16', '--demuxer-rawvideo-h=16',
                    '--demuxer-rawvideo-format=I420', '--demuxer-rawvideo-fps=1',
                    f'--script={SCRIPT}', f'--script={driver}',
                ], env=env, capture_output=True, timeout=35)
                outcome = json.loads(result_path.read_text(encoding='utf-8')) if result_path.exists() else {}
                if proc.returncode != 0:
                    outcome['requests'] = list(ParallelAutoloadHandler.seen)
                self.assertEqual(proc.returncode, 0,
                    (outcome, proc.stderr.decode('utf-8', 'replace')[-1200:]))
                self.assertEqual(outcome, {'count': 1, 'state': 'loaded', 'source': 2})
                self.assertGreaterEqual(ParallelAutoloadHandler.max_active, 2,
                    'automatic route searches were not in flight concurrently')
                self.assertEqual(sum(path.endswith('/api/v2/match') for path in ParallelAutoloadHandler.seen), 0)
                self.assertEqual(sum('/api/v2/search/anime?' in path for path in ParallelAutoloadHandler.seen), 2)
                if empty_route_expected:
                    self.assertIn('/api/v2/comment/205?withRelated=true', ParallelAutoloadHandler.seen)
                else:
                    self.assertNotIn('/api/v2/bangumi/101', ParallelAutoloadHandler.seen)
                self.assertIn('/second/api/v2/comment/305?withRelated=true', ParallelAutoloadHandler.seen)
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)

    def test_automatic_match_switches_to_platform_with_comments(self):
        PlatformFallbackHandler.seen = []
        server = ThreadingHTTPServer(('127.0.0.1', 0), PlatformFallbackHandler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            with tempfile.TemporaryDirectory(prefix='animejanai-platform-autoload-') as directory:
                folder = Path(directory)
                sample = folder / '尼古喵喵 (2026) S1E5 - 第5集.yuv'
                sample.write_bytes((bytes([16]) * 256 + bytes([128]) * 128) * 60)
                (folder / 'mpv-AnimeFusion-danmaku.conf').write_text(
                    f'api_servers=http://127.0.0.1:{server.server_port}|本地回归\n', encoding='utf-8')
                result_path = folder / 'state.json'
                driver = folder / 'driver.lua'
                driver.write_text('''
local utils=require('mp.utils')
local output=%s
local sample=%s
local function finish(value,code)
    local file=assert(io.open(output,'wb'))
    file:write(utils.format_json(value));file:close()
    mp.commandv('quit',code)
end
mp.add_timeout(.25,function()mp.commandv('loadfile',sample,'replace')end)
mp.add_periodic_timer(.05,function()
    local data=mp.get_property_native('user-data/player_ui/danmaku')
    if type(data)=='table' and data.loaded and data.count==1 then
        finish({loaded=data.loaded,count=data.count,state=data.autoload_state,source=data.source},0)
    end
end)
mp.add_timeout(30,function()
    local data=mp.get_property_native('user-data/player_ui/danmaku') or {}
    finish({error='automatic platform fallback did not load comments',state=data.autoload_state,
        status=data.status},1)
end)
''' % (lua_string(str(result_path)), lua_string(str(sample))), encoding='utf-8')
                env = os.environ.copy()
                env['LOCALAPPDATA'] = directory
                proc = subprocess.run([
                    str(MPV), f'--config-dir={self.config}', '--load-scripts=no', '--idle=yes',
                    '--vo=null', '--ao=null', '--demuxer=rawvideo',
                    '--demuxer-rawvideo-w=16', '--demuxer-rawvideo-h=16',
                    '--demuxer-rawvideo-format=I420', '--demuxer-rawvideo-fps=1',
                    f'--script={SCRIPT}', f'--script={driver}',
                ], env=env, capture_output=True, timeout=40)
                outcome = json.loads(result_path.read_text(encoding='utf-8')) if result_path.exists() else {}
                if proc.returncode != 0:
                    outcome['requests'] = list(PlatformFallbackHandler.seen)
                self.assertEqual(proc.returncode, 0, (outcome, proc.stderr.decode('utf-8', 'replace')[-1200:]))
                self.assertEqual(outcome, {'loaded': True, 'count': 1, 'state': 'loaded', 'source': 1})
                self.assertIn('/api/v2/bangumi/101', PlatformFallbackHandler.seen)
                self.assertIn('/api/v2/comment/205?withRelated=true', PlatformFallbackHandler.seen)
                self.assertIn('/api/v2/bangumi/102', PlatformFallbackHandler.seen)
                self.assertIn('/api/v2/comment/305?withRelated=true', PlatformFallbackHandler.seen)
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)

    def test_manual_episode_pick_switches_to_platform_with_comments(self):
        PlatformFallbackHandler.seen = []
        server = ThreadingHTTPServer(('127.0.0.1', 0), PlatformFallbackHandler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            with tempfile.TemporaryDirectory(prefix='animejanai-platform-manual-') as directory:
                folder = Path(directory)
                (folder / 'mpv-AnimeFusion-danmaku.conf').write_text(
                    f'api_servers=http://127.0.0.1:{server.server_port}|本地回归\n', encoding='utf-8')
                result_path = folder / 'state.json'
                driver = folder / 'driver.lua'
                driver.write_text('''
local utils=require('mp.utils')
local output=%s
local phase=0
local function finish(value,code)
    local file=assert(io.open(output,'wb'))
    file:write(utils.format_json(value));file:close()
    mp.commandv('quit',code)
end
mp.add_timeout(.25,function()
    mp.commandv('script-message','player_ui-danmaku-search-query','尼古喵喵','1','1','true')
end)
mp.add_periodic_timer(.05,function()
    local data=mp.get_property_native('user-data/player_ui/danmaku')
    if type(data)~='table' then return end
    if phase==0 and type(data.results)=='table' and #data.results==1 then
        phase=1
        mp.commandv('script-message','player_ui-danmaku-show','101','1')
    elseif phase==1 and data.search_view=='episodes' and type(data.episodes)=='table'
        and #data.episodes==1 then
        phase=2
        mp.commandv('script-message','player_ui-danmaku-pick','205','第5集','1')
    elseif phase==2 and data.loaded and data.count==1 then
        finish({loaded=data.loaded,count=data.count,state=data.autoload_state,
            source=data.source,label=data.file},0)
    end
end)
mp.add_timeout(30,function()
    local data=mp.get_property_native('user-data/player_ui/danmaku') or {}
    finish({error='manual platform fallback did not load comments',phase=phase,
        state=data.autoload_state,status=data.status,view=data.search_view},1)
end)
''' % lua_string(str(result_path)), encoding='utf-8')
                env = os.environ.copy()
                env['LOCALAPPDATA'] = directory
                proc = subprocess.run([
                    str(MPV), f'--config-dir={self.config}', '--load-scripts=no', '--idle=yes',
                    '--vo=null', '--ao=null', f'--script={SCRIPT}', f'--script={driver}',
                ], env=env, capture_output=True, timeout=40)
                outcome = json.loads(result_path.read_text(encoding='utf-8')) if result_path.exists() else {}
                if proc.returncode != 0:
                    outcome['requests'] = list(PlatformFallbackHandler.seen)
                self.assertEqual(proc.returncode, 0, (outcome, proc.stderr.decode('utf-8', 'replace')[-1200:]))
                self.assertEqual(outcome['count'], 1)
                self.assertTrue(outcome['loaded'])
                self.assertEqual(outcome['source'], 1)
                self.assertIn('腾讯视频', outcome['label'])
                self.assertIn('/api/v2/comment/205?withRelated=true', PlatformFallbackHandler.seen)
                self.assertIn('/api/v2/bangumi/102', PlatformFallbackHandler.seen)
                self.assertIn('/api/v2/comment/305?withRelated=true', PlatformFallbackHandler.seen)
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)

    def test_unavailable_route_does_not_block_healthy_route(self):
        RouteFailureHandler.seen = []
        server = ThreadingHTTPServer(('127.0.0.1', 0), RouteFailureHandler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            with tempfile.TemporaryDirectory(prefix='animejanai-route-failure-') as directory:
                folder = Path(directory)
                sample = folder / '尼古喵喵 (2026) S1E5.yuv'
                sample.write_bytes((bytes([16]) * 256 + bytes([128]) * 128) * 60)
                (folder / 'mpv-AnimeFusion-danmaku.conf').write_text(
                    f'api_servers=http://127.0.0.1:{server.server_port}/unavailable|不可用线路,'
                    f'http://127.0.0.1:{server.server_port}|可用线路\n', encoding='utf-8')
                result_path = folder / 'state.json'
                driver = folder / 'driver.lua'
                driver.write_text('''
local utils=require('mp.utils')
local output=%s
mp.add_periodic_timer(.05,function()
    local data=mp.get_property_native('user-data/player_ui/danmaku')
    if type(data)=='table' and data.loaded and data.count==1 then
        local file=assert(io.open(output,'wb'))
        file:write(utils.format_json({count=data.count,state=data.autoload_state,source=data.source}));file:close()
        mp.commandv('quit',0)
    end
end)
mp.add_timeout(30,function()
    local data=mp.get_property_native('user-data/player_ui/danmaku') or {}
    local file=assert(io.open(output,'wb'))
    file:write(utils.format_json({state=data.autoload_state,status=data.status}));file:close()
    mp.commandv('quit',1)
end)
''' % lua_string(str(result_path)), encoding='utf-8')
                env = os.environ.copy()
                env['LOCALAPPDATA'] = directory
                proc = subprocess.run([str(MPV), f'--config-dir={self.config}', '--load-scripts=no',
                    '--vo=null', '--ao=null', '--demuxer=rawvideo',
                    '--demuxer-rawvideo-w=16', '--demuxer-rawvideo-h=16',
                    '--demuxer-rawvideo-format=I420', '--demuxer-rawvideo-fps=1',
                    f'--script={SCRIPT}', f'--script={driver}', str(sample)],
                    env=env, capture_output=True, timeout=16)
                outcome = json.loads(result_path.read_text(encoding='utf-8')) if result_path.exists() else {}
                self.assertEqual(proc.returncode, 0, (outcome, proc.stderr.decode('utf-8', 'replace')[-1200:]))
                self.assertEqual(outcome, {'count': 1, 'state': 'loaded', 'source': 2})
                self.assertEqual(len([path for path in RouteFailureHandler.seen
                    if path.startswith('/unavailable/api/v2/search/anime?')]), 1)
                self.assertNotIn('/unavailable/api/v2/match', RouteFailureHandler.seen)
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)

    def test_episode_auto_search_loads_same_season_and_episode(self):
        AutoSearchHandler.seen = []
        server = ThreadingHTTPServer(('127.0.0.1', 0), AutoSearchHandler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            with tempfile.TemporaryDirectory(prefix='animejanai-auto-search-') as directory:
                folder = Path(directory)
                sample = folder / '尼古喵喵 (2026) S1E5 - 喵喵们要去秘境啦喵.yuv'
                sample.write_bytes((bytes([16]) * 256 + bytes([128]) * 128) * 60)
                (folder / 'mpv-AnimeFusion-danmaku.conf').write_text(
                    f'api_servers=http://127.0.0.1:{server.server_port}/wrong|季度不符,'
                    f'http://127.0.0.1:{server.server_port}/empty|空结果,'
                    f'http://127.0.0.1:{server.server_port}|可用线路\n', encoding='utf-8')
                result_path = folder / 'state.json'
                driver = folder / 'driver.lua'
                driver.write_text('''
local utils=require('mp.utils')
local output=%s
mp.add_periodic_timer(.05,function()
    local data=mp.get_property_native('user-data/player_ui/danmaku')
    if type(data)=='table' and data.loaded and data.count==1 then
        local file=assert(io.open(output,'wb'))
        file:write(utils.format_json({count=data.count,state=data.autoload_state,source=data.source}));file:close()
        mp.commandv('quit',0)
    end
end)
mp.add_timeout(35,function()
    local data=mp.get_property_native('user-data/player_ui/danmaku') or {}
    local file=assert(io.open(output,'wb'))
    file:write(utils.format_json({state=data.autoload_state,status=data.status,source=data.source}));file:close()
    mp.commandv('quit',1)
end)
''' % lua_string(str(result_path)), encoding='utf-8')
                env = os.environ.copy()
                env['LOCALAPPDATA'] = directory
                proc = subprocess.run([str(MPV), f'--config-dir={self.config}', '--load-scripts=no',
                    '--vo=null', '--ao=null', '--demuxer=rawvideo',
                    '--demuxer-rawvideo-w=16', '--demuxer-rawvideo-h=16',
                    '--demuxer-rawvideo-format=I420', '--demuxer-rawvideo-fps=1',
                    f'--script={SCRIPT}', f'--script={driver}', str(sample)],
                    env=env, capture_output=True, timeout=45)
                outcome = json.loads(result_path.read_text(encoding='utf-8')) if result_path.exists() else {}
                if proc.returncode != 0:
                    outcome['requests'] = list(AutoSearchHandler.seen)
                self.assertEqual(proc.returncode, 0, (outcome, proc.stderr.decode('utf-8', 'replace')[-1200:]))
                self.assertEqual(outcome, {'count': 1, 'state': 'loaded', 'source': 3})
                self.assertEqual(len([path for path in AutoSearchHandler.seen
                    if '/api/v2/search/anime?' in path]), 3, AutoSearchHandler.seen)
                for prefix in ('/wrong/', '/empty/', '/'):
                    self.assertEqual(len([path for path in AutoSearchHandler.seen
                        if path.startswith(prefix + 'api/v2/search/anime?')]), 1,
                        AutoSearchHandler.seen)
                self.assertIn('/api/v2/bangumi/101', AutoSearchHandler.seen)
                self.assertIn('/api/v2/comment/205?withRelated=true', AutoSearchHandler.seen)
                self.assertFalse(any('/api/v2/bangumi/102' in path for path in AutoSearchHandler.seen))
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)

    def test_show_platform_episode_flow(self):
        Handler.seen = []
        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            with tempfile.TemporaryDirectory(prefix="animejanai-danmaku-flow-") as directory:
                folder = Path(directory)
                (folder / "mpv-AnimeFusion-danmaku.conf").write_text(
                    f"api_servers=http://127.0.0.1:{server.server_port}|本地测试,"
                    f"http://127.0.0.1:{server.server_port}/unused|第二线路\n", encoding="utf-8")
                result_path = folder / "state.json"
                driver = folder / "driver.lua"
                driver.write_text("""
local utils=require('mp.utils')
local output=%s
local phase=0
local function fail(reason)
    local file=assert(io.open(output,'wb'))
    file:write(utils.format_json({error=reason,state=mp.get_property_native('user-data/player_ui/danmaku')}));file:close()
    mp.commandv('quit',1)
end
mp.add_timeout(.5,function()
    mp.commandv('script-message','player_ui-danmaku-search-query','间谍过家家','1','2')
end)
mp.add_periodic_timer(.15,function()
    local data=mp.get_property_native('user-data/player_ui/danmaku')
    if type(data)~='table' then return end
    if phase==0 and type(data.results)=='table' and #data.results==2 then
        local second=data.results[2]
        if second.season~=2 or #second.platforms~=2 then fail('wrong season/platform groups');return end
        phase=1
        mp.commandv('script-message','player_ui-danmaku-show','203','1')
    elseif phase==1 and data.search_view=='episodes' and type(data.episodes)=='table' and #data.episodes==1 then
        if data.episodes[1].number_value~=12 then fail('wrong local episode');return end
        phase=2
        mp.commandv('script-message','player_ui-danmaku-pick','77','本地测试','1')
    elseif phase==2 and data.loaded and data.count==1 then
        local file=assert(io.open(output,'wb'))
        file:write(utils.format_json({season=data.search_season,count=data.count,
            source=data.source}));file:close()
        mp.commandv('quit',0)
    end
end)
mp.add_timeout(25,function()fail('timed out in phase '..phase)end)
""" % lua_string(str(result_path)), encoding="utf-8")
                env = os.environ.copy()
                env["LOCALAPPDATA"] = directory
                proc = subprocess.run([str(MPV), f'--config-dir={self.config}', "--load-scripts=no", "--idle=yes",
                    "--vo=null", f"--script={SCRIPT}", f"--script={driver}"],
                    env=env, capture_output=True, timeout=35)
                outcome = json.loads(result_path.read_text(encoding="utf-8")) if result_path.exists() else {}
                self.assertEqual(proc.returncode, 0, (outcome, proc.stderr.decode("utf-8", "replace")[-1200:]))
                self.assertEqual(outcome, {"season": 2, "count": 1, "source": 1})
                self.assertEqual(len([path for path in Handler.seen if "/search/anime" in path]), 2)
                self.assertTrue(any(path.startswith("/unused/api/v2/search/anime") for path in Handler.seen))
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)

    def test_file_loaded_automatically_matches_and_loads_danmaku(self):
        AutoloadHandler.seen = []
        AutoloadHandler.match_body = {}
        AutoloadHandler.match_count = 0
        server = ThreadingHTTPServer(("127.0.0.1", 0), AutoloadHandler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            with tempfile.TemporaryDirectory(prefix="animejanai-danmaku-autoload-") as directory:
                folder = Path(directory)
                sample = folder / "间谍过家家 (2025) S3E3.yuv"
                # A local 16x16 I420 clip exercises a real file-loaded event without
                # opening a media-server stream or putting fixture labels in the UI.
                frame = bytes([16]) * 256 + bytes([128]) * 128
                sample.write_bytes(frame * 60)
                (folder / "mpv-AnimeFusion-danmaku.conf").write_text(
                    f"api_servers=http://127.0.0.1:{server.server_port}|本地回归\n", encoding="utf-8")
                result_path = folder / "state.json"
                driver = folder / "driver.lua"
                driver.write_text("""
local utils=require('mp.utils')
local output=%s
local sample=%s
local phase=0
local saw_loading=false
local saw_not_found=false
local function finish(value,code)
    local file=assert(io.open(output,'wb'))
    file:write(utils.format_json(value));file:close()
    mp.commandv('quit',code)
end
mp.add_timeout(.25,function()mp.commandv('loadfile',sample,'replace')end)
mp.add_periodic_timer(.05,function()
    local data=mp.get_property_native('user-data/player_ui/danmaku')
    if type(data)~='table' then return end
    if data.autoload_state=='loading' then saw_loading=true end
    if phase==0 and data.autoload_state=='not-found' then
        saw_not_found=true
        phase=1
        mp.commandv('loadfile',sample,'replace')
    elseif phase==1 and data.loaded and data.count==1 then
        finish({loaded=data.loaded,count=data.count,autoload_state=data.autoload_state,
            saw_loading=saw_loading,saw_not_found=saw_not_found},0)
    end
end)
mp.add_timeout(30,function()finish({error='automatic matching did not load comments',phase=phase,
    state=mp.get_property_native('user-data/player_ui/danmaku'),
    saw_loading=saw_loading,saw_not_found=saw_not_found},1)end)
""" % (lua_string(str(result_path)), lua_string(str(sample))), encoding="utf-8")
                env = os.environ.copy()
                env["LOCALAPPDATA"] = directory
                proc = subprocess.run([
                    str(MPV), f'--config-dir={self.config}', "--load-scripts=no", "--idle=yes",
                    "--vo=null", "--ao=null", "--demuxer=rawvideo",
                    "--demuxer-rawvideo-w=16", "--demuxer-rawvideo-h=16",
                    "--demuxer-rawvideo-format=I420", "--demuxer-rawvideo-fps=1",
                    f"--script={SCRIPT}", f"--script={driver}",
                ], env=env, capture_output=True, timeout=35)
                outcome = json.loads(result_path.read_text(encoding="utf-8")) if result_path.exists() else {}
                if proc.returncode:
                    outcome['requests'] = list(AutoloadHandler.seen)
                self.assertEqual(proc.returncode, 0, (outcome, proc.stderr.decode("utf-8", "replace")[-1200:]))
                self.assertEqual(outcome, {"loaded": True, "count": 1, "autoload_state": "loaded",
                    "saw_loading": True, "saw_not_found": True})
                self.assertCountEqual(AutoloadHandler.seen, [
                    "/api/v2/search/anime?keyword=%E9%97%B4%E8%B0%8D%E8%BF%87%E5%AE%B6%E5%AE%B6",
                    "/api/v2/search/anime?keyword=%E9%97%B4%E8%B0%8D%E8%BF%87%E5%AE%B6%E5%AE%B6",
                    "/api/v2/bangumi/900", "/api/v2/comment/903?withRelated=true",
                ])
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)

    def test_automatic_search_does_not_retry_failed_routes(self):
        TransientAutoloadHandler.seen = []
        TransientAutoloadHandler.match_count = 0
        server = ThreadingHTTPServer(("127.0.0.1", 0), TransientAutoloadHandler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            with tempfile.TemporaryDirectory(prefix="animejanai-danmaku-autoretry-") as directory:
                folder = Path(directory)
                sample = folder / "尼古喵喵 (2026) S1E5 - 喵喵们要去秘境啦喵.yuv"
                frame = bytes([16]) * 256 + bytes([128]) * 128
                sample.write_bytes(frame * 60)
                (folder / "mpv-AnimeFusion-danmaku.conf").write_text(
                    f"api_servers=http://127.0.0.1:{server.server_port}|本地回归\n", encoding="utf-8")
                result_path = folder / "state.json"
                driver = folder / "driver.lua"
                driver.write_text('''
local utils=require('mp.utils')
local output=%s
local sample=%s
local saw_loading=false
local saw_error=false
local function finish(value,code)
    local file=assert(io.open(output,'wb'))
    file:write(utils.format_json(value));file:close()
    mp.commandv('quit',code)
end
mp.add_timeout(.25,function()mp.commandv('loadfile',sample,'replace')end)
mp.add_periodic_timer(.05,function()
    local data=mp.get_property_native('user-data/player_ui/danmaku')
    if type(data)~='table' then return end
    if data.autoload_state=='loading' then saw_loading=true end
    if data.autoload_state=='error' then saw_error=true end

end)
mp.add_timeout(12,function()
    local data=mp.get_property_native('user-data/player_ui/danmaku') or {}
    finish({state=data.autoload_state,saw_loading=saw_loading,saw_error=saw_error},data.autoload_state=='error' and 0 or 1)
end)
''' % (lua_string(str(result_path)), lua_string(str(sample))), encoding="utf-8")
                env = os.environ.copy()
                env["LOCALAPPDATA"] = directory
                proc = subprocess.run([
                    str(MPV), f'--config-dir={self.config}', "--load-scripts=no", "--idle=yes",
                    "--vo=null", "--ao=null", "--demuxer=rawvideo",
                    "--demuxer-rawvideo-w=16", "--demuxer-rawvideo-h=16",
                    "--demuxer-rawvideo-format=I420", "--demuxer-rawvideo-fps=1",
                    f"--script={SCRIPT}", f"--script={driver}",
                ], env=env, capture_output=True, timeout=45)
                outcome = json.loads(result_path.read_text(encoding="utf-8")) if result_path.exists() else {}
                if proc.returncode != 0:
                    outcome["requests"] = list(TransientAutoloadHandler.seen)
                    outcome["match_count"] = TransientAutoloadHandler.match_count
                self.assertEqual(proc.returncode, 0, (outcome, proc.stderr.decode("utf-8", "replace")[-1200:]))
                self.assertEqual(outcome, {"state": "error", "saw_loading": True, "saw_error": True})
                self.assertEqual(TransientAutoloadHandler.match_count, 0)
                self.assertEqual(len(TransientAutoloadHandler.seen), 1)
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)

    def test_parallel_search_and_route_result_cache(self):
        CacheHandler.seen = []
        CacheHandler.active = 0
        CacheHandler.max_active = 0
        CacheHandler.lock = threading.Lock()
        server = ThreadingHTTPServer(("127.0.0.1", 0), CacheHandler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            with tempfile.TemporaryDirectory(prefix="animejanai-danmaku-cache-") as directory:
                folder = Path(directory)
                (folder / "mpv-AnimeFusion-danmaku.conf").write_text(
                    f"api_servers=http://127.0.0.1:{server.server_port}|主线路,"
                    f"http://127.0.0.1:{server.server_port}/second|第二线路\n", encoding="utf-8")
                result_path = folder / "state.json"
                driver = folder / "driver.lua"
                driver.write_text("""
local utils=require('mp.utils')
local output=%s
local phase=0
local function fail(reason)
    local file=assert(io.open(output,'wb'))
    file:write(utils.format_json({error=reason}));file:close()
    mp.commandv('quit',1)
end
mp.add_timeout(.5,function()
    mp.commandv('script-message','player_ui-danmaku-search-query','缓存测试作品','0','')
end)
mp.add_periodic_timer(.05,function()
    local data=mp.get_property_native('user-data/player_ui/danmaku')
    if type(data)~='table' then return end
    if phase==0 and data.search_pending==0 and type(data.results)=='table' and #data.results==2 then
        if data.results[1].server_index==data.results[2].server_index then fail('all-source results lost route identity');return end
        phase=1
        mp.commandv('script-message','player_ui-danmaku-search-filter','1')
    elseif phase==1 and data.search_source==1 and #data.results==1 then
        if data.results[1].server_index~=1 then fail('first route filter returned wrong source');return end
        phase=2
        mp.commandv('script-message','player_ui-danmaku-search-filter','2')
    elseif phase==2 and data.search_source==2 and #data.results==1 then
        if data.results[1].server_index~=2 then fail('second route filter returned wrong source');return end
        phase=3
        mp.commandv('script-message','player_ui-danmaku-search-query','缓存测试作品','0','')
    elseif phase==3 and data.search_source==0 and data.search_pending==0 and #data.results==2 then
        phase=4
        mp.commandv('script-message','player_ui-danmaku-search-query','缓存测试作品','0','','true')
    elseif phase==4 and data.search_pending==2 then
        phase=5
    elseif phase==5 and data.search_source==0 and data.search_pending==0 and #data.results==2 then
        local file=assert(io.open(output,'wb'))
        file:write(utils.format_json({phase=phase,count=#data.results,source=data.search_source}));file:close()
        mp.commandv('quit',0)
    end
end)
mp.add_timeout(12,function()fail('timed out in cache phase '..phase)end)
""" % lua_string(str(result_path)), encoding="utf-8")
                env = os.environ.copy()
                env["LOCALAPPDATA"] = directory
                proc = subprocess.run([str(MPV), f'--config-dir={self.config}', "--load-scripts=no", "--idle=yes",
                    "--vo=null", f"--script={SCRIPT}", f"--script={driver}"],
                    env=env, capture_output=True, timeout=18)
                outcome = json.loads(result_path.read_text(encoding="utf-8")) if result_path.exists() else {}
                self.assertEqual(proc.returncode, 0, (outcome, proc.stderr.decode("utf-8", "replace")[-1200:]))
                self.assertEqual(outcome, {"phase": 5, "count": 2, "source": 0})
                self.assertCountEqual(CacheHandler.seen, [
                    "/api/v2/search/anime?keyword=%E7%BC%93%E5%AD%98%E6%B5%8B%E8%AF%95%E4%BD%9C%E5%93%81",
                    "/second/api/v2/search/anime?keyword=%E7%BC%93%E5%AD%98%E6%B5%8B%E8%AF%95%E4%BD%9C%E5%93%81",
                    "/api/v2/search/anime?keyword=%E7%BC%93%E5%AD%98%E6%B5%8B%E8%AF%95%E4%BD%9C%E5%93%81",
                    "/second/api/v2/search/anime?keyword=%E7%BC%93%E5%AD%98%E6%B5%8B%E8%AF%95%E4%BD%9C%E5%93%81",
                ])
                self.assertGreaterEqual(CacheHandler.max_active, 2, "routes were not requested in parallel")
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)


class ScopedSearchHandler(BaseHTTPRequestHandler):
    seen: list[str] = []

    def log_message(self, *_args):
        pass

    def do_GET(self):
        ScopedSearchHandler.seen.append(self.path)
        failed = self.path.startswith('/failed/')
        slow = self.path.startswith('/slow/')
        if slow:
            time.sleep(3)
        body = {'errorMessage': '服务器维护中'} if failed else {'animes': [
            {'animeId': 33, 'animeTitle': '无职转生Ⅲ ～到了异世界就拿出真本事～(2026)',
             'source': 'dandan', 'episodeCount': 16}] if slow else []}
        payload = json.dumps(body, ensure_ascii=False).encode('utf-8')
        self.send_response(503 if failed else 200)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.send_header('Content-Length', str(len(payload)))
        self.send_header('Connection', 'close')
        try:
            self.end_headers()
            self.wfile.write(payload)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass


class CacheHandler(BaseHTTPRequestHandler):
    seen: list[str] = []
    active = 0
    max_active = 0
    lock = threading.Lock()
    search_delays = (0, 2)

    def log_message(self, *_args):
        pass

    def do_GET(self):
        if "/api/v2/search/anime?" not in self.path:
            self.send_error(404)
            return
        with CacheHandler.lock:
            CacheHandler.active += 1
            CacheHandler.max_active = max(CacheHandler.max_active, CacheHandler.active)
        CacheHandler.seen.append(self.path)
        try:
            import time
            time.sleep(.35)
            second = self.path.startswith("/second/")
            record = {
                "animeId": 202 if second else 101,
                "animeTitle": "缓存测试作品 第2季(2025)" if second else "缓存测试作品 第1季(2024)",
                "source": "iqiyi" if second else "qq",
                "episodeCount": 12,
            }
            payload = json.dumps({"success": True, "animes": [record]}, ensure_ascii=False).encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(payload)))
            self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(payload)
        finally:
            with CacheHandler.lock:
                CacheHandler.active -= 1


if __name__ == "__main__":
    unittest.main()

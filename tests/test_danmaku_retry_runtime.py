"""Exercise retries and remembered per-series sources through production mpv Lua."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit

ROOT = Path(__file__).resolve().parents[1]


def verify(install, script_root):
    requests = []
    phase = {'episode_limit': 4, 'failures': {}, 'initial_retry': True, 'season': 1}

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_GET(self):
            requests.append(self.path)
            url = urlsplit(self.path)
            backup = url.path.startswith('/backup/')
            route = url.path.removeprefix('/backup') if backup else url.path
            status = 200
            if route.startswith('/api/v2/search/'):
                keyword = parse_qs(url.query)['keyword'][0]
                base_searches = sum(p.startswith('/api/v2/search/') for p in requests)
                if phase['initial_retry'] and not backup and base_searches <= 3:
                    status, data = 503, {'errorMessage': 'temporary failure'}
                else:
                    season = phase['season']
                    first = 401 if backup else 201 if season == 2 else 301 if keyword == 'Other' else 101
                    title = f'{keyword} Season {season}'
                    data = {'animes': [{'animeId': first, 'animeTitle': title,
                        'source': 'youku', 'typeDescription': 'TV动画', 'episodeCount': 7,
                        'mergedChildren': [{'animeId': first + 1, 'source': 'iqiyi', 'episodes': 7}]}]}
            elif route.startswith('/api/v2/bangumi/'):
                show = int(route.rsplit('/', 1)[1])
                # Distinct platform episode IDs, not an arithmetic ID assumption in the app.
                base = {101: 1100, 102: 1200, 201: 2100, 202: 2200,
                        301: 3100, 302: 3200, 401: 4100, 402: 4200}[show]
                data = {'bangumi': {'animeTitle': 'Series Season 1', 'episodes': [
                    {'episodeId': base + n, 'episodeNumber': str(n), 'episodeTitle': f'第{n}集'}
                    for n in range(1, phase['episode_limit'] + 1)]}}
            elif route.startswith('/api/v2/comment/'):
                episode = int(route.rsplit('/', 1)[1])
                failure = phase['failures'].get(episode)
                count = 25 if episode < 1200 else 80 if episode < 2000 else 9
                if failure == 'error':
                    status, data = 503, {'errorMessage': 'source unavailable'}
                else:
                    if failure == 'empty':
                        count = 0
                    data = {'comments': [{'p': f'{i / 10},1,16777215,user', 'm': f'comment {i}'}
                        for i in range(count)]}
            else:
                status, data = 404, {}
            body = json.dumps(data, ensure_ascii=False).encode()
            self.send_response(status)
            self.send_header('Content-Length', str(len(body)))
            self.send_header('Content-Type', 'application/json; charset=utf-8')
            self.end_headers()
            try:
                self.wfile.write(body)
            except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                pass

    server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        with tempfile.TemporaryDirectory(prefix='danmaku-sources-', dir=ROOT / '_probe') as directory:
            path = Path(directory)
            config = path / 'portable_config'
            scripts = config / 'scripts'
            scripts.mkdir(parents=True)
            shutil.copy2(script_root / 'portable_config/scripts/player_ui_danmaku.lua', scripts)
            native = path / 'animejanai/danmaku'
            native.mkdir(parents=True)
            shutil.copy2(install / 'animejanai/danmaku/DanmakuFactory.exe', native / 'DanmakuFactory.exe')
            localdata = path / 'localdata'
            localdata.mkdir()
            base_url = f'http://127.0.0.1:{server.server_port}'
            source_file = localdata / 'AnimeVE-danmaku.conf'
            source_file.write_text(f'api_servers={base_url}|Loopback\n', encoding='utf-8')
            env = dict(os.environ, LOCALAPPDATA=str(localdata))
            saved_path = localdata / 'AnimeVE-danmaku-sources.json'

            def run(name, expected, manual=False):
                start = len(requests)
                media = path / f'{name}.y4m'
                media.write_bytes(b'YUV4MPEG2 W160 H90 F24:1 Ip A1:1 C420jpeg\n'
                    + (b'FRAME\n' + b'\x60' * 14400 + b'\x80' * 7200) * 24)
                acceptance = path / 'acceptance.lua'
                manual_lua = """
    if stage==0 and state.loaded then
        stage=1;mp.commandv('script-message','player_ui-danmaku-search-query','Series','1','1','true');return
    elseif stage==1 and state.search_pending==0 and #(state.results or {})>0 then
        stage=2;mp.commandv('script-message','player_ui-danmaku-show','102','1');return
    elseif stage==2 and #(state.episodes or {})>0 then
        stage=3;mp.commandv('script-message','player_ui-danmaku-pick','1202','Manual Series · 第2集','1');return
    elseif stage<3 then return end
""" if manual else ''
                acceptance.write_text(f"""
local complete,stage=false,0
mp.observe_property('user-data/player_ui/danmaku','native',function(_,state)
    if not state then return end
{manual_lua}
    if state.autoload_state=='empty' and {expected}==0 then
        complete=true;print('PASS runtime count 0');mp.commandv('quit',0);return
    end
    if not state.loaded or state.autoload_state~='loaded' then return end
    complete=true
    if state.count=={expected} then
        print('PASS runtime count '..state.count);mp.commandv('quit',0)
    else print('FAIL runtime '..require('mp.utils').format_json(state));mp.commandv('quit',1) end
end)
mp.add_timeout(45,function()if not complete then print('FAIL loading did not finish');mp.commandv('quit',1)end end)
""", encoding='utf-8')
                result = subprocess.run([str(install / 'app/mpv.exe'), '--config-dir=' + str(config),
                    '--vo=null', '--ao=null', '--hwdec=no', '--pause=yes', '--keep-open=yes',
                    '--script=' + str(acceptance), str(media)], env=env, capture_output=True, timeout=55)
                output = (result.stdout + result.stderr).decode('utf-8', 'replace')
                assert result.returncode == 0 and f'PASS runtime count {expected}' in output, output
                assert 'Lua error' not in output and 'stack traceback' not in output, output
                return requests[start:]

            calls = run('Series S1E1', 25)
            assert sum('/search/' in p for p in calls) == 4, calls
            assert sum('/bangumi/' in p for p in calls) == 1, calls
            assert sum('/comment/' in p for p in calls) == 1 and '/comment/1101?' in calls[-1], calls
            saved = json.loads(saved_path.read_text(encoding='utf-8'))
            assert saved['series/s1/p0']['id'] == '101', saved
            print('PASS: initial search retries three times; 25 comments accepted without switching', flush=True)
            phase['initial_retry'] = False

            calls = run('Series S1E2', 25)
            assert calls == ['/api/v2/comment/1102?withRelated=true'], calls
            print('PASS: next episode after restart uses one comment request, no search or episode-list request', flush=True)

            phase['failures'][1103] = 'error'
            calls = run('Series S1E3', 80)
            assert sum('/comment/1103?' in p for p in calls) == 4, calls
            assert sum('/search/' in p for p in calls) == 1 and '/comment/1203?' in calls[-1], calls
            assert json.loads(saved_path.read_text(encoding='utf-8'))['series/s1/p0']['id'] == '102'
            calls = run('Series S1E4', 80)
            assert calls == ['/api/v2/comment/1204?withRelated=true'], calls
            print('PASS: remembered-source failure retries, searches and replaces platform; next episode uses replacement', flush=True)

            phase['episode_limit'] = 7
            calls = run('Series S1E5', 80)
            assert calls == ['/api/v2/bangumi/102', '/api/v2/comment/1205?withRelated=true'], calls
            print('PASS: newly released episode refreshes original platform directly without searching', flush=True)

            source_file.write_text(f'api_servers={base_url}/backup|Backup,{base_url}|Loopback\n', encoding='utf-8')
            calls = run('Series S1E4', 80)
            assert calls == ['/api/v2/comment/1204?withRelated=true'], calls
            print('PASS: route reordering preserves the exact saved API URL', flush=True)

            phase['failures'][1206] = 'error'
            phase['failures'][1106] = 'empty'
            calls = run('Series S1E6', 9)
            assert sum('/search/' in p for p in calls) == 2, calls
            assert sum('/comment/1206?' in p for p in calls) == 4, calls
            assert json.loads(saved_path.read_text(encoding='utf-8'))['series/s1/p0']['server_url'] == base_url + '/backup'
            calls = run('Series S1E7', 9)
            assert calls == ['/backup/api/v2/comment/4107?withRelated=true'], calls
            print('PASS: failed source searches other API and persists its URL; nine comments are sufficient', flush=True)

            # Separate season and title keys must initiate their own search.
            source_file.write_text(f'api_servers={base_url}|Loopback\n', encoding='utf-8')
            calls = run('Other S1E1', 9)
            assert any('/search/' in p for p in calls) and '/comment/3101?' in calls[-1], calls
            phase['season'] = 2
            calls = run('Series S2E1', 9)
            assert any('/search/' in p for p in calls) and '/comment/2101?' in calls[-1], calls
            saved = json.loads(saved_path.read_text(encoding='utf-8'))
            assert saved['series/s1/p0']['id'] == '401' and saved['series/s2/p0']['id'] == '201'
            assert saved['other/s1/p0']['id'] == '301'
            print('PASS: different works and seasons have independent remembered sources', flush=True)

            phase['season'] = 1
            calls = run('Series S1E2', 80, manual=True)
            assert '/api/v2/comment/1202?withRelated=true' in calls, calls
            assert json.loads(saved_path.read_text(encoding='utf-8'))['series/s1/p0']['id'] == '102'
            calls = run('Series S1E4', 80)
            assert calls == ['/api/v2/comment/1204?withRelated=true'], calls
            print('PASS: manual platform selection becomes the standard for following episodes', flush=True)

            phase['failures'][1204] = 'empty'
            calls = run('Series S1E4', 0)
            assert calls == ['/api/v2/comment/1204?withRelated=true'], calls
            assert json.loads(saved_path.read_text(encoding='utf-8'))['series/s1/p0']['id'] == '102'
            del phase['failures'][1204]
            calls = run('Series S1E4', 80)
            assert calls == ['/api/v2/comment/1204?withRelated=true'], calls
            print('PASS: successful empty response preserves original API/platform and does not search', flush=True)

    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--install', type=Path, default=Path('D:/Apps/mpv-AnimeJaNai'))
    parser.add_argument('--script-root', type=Path, default=ROOT)
    args = parser.parse_args()
    verify(args.install, args.script_root)

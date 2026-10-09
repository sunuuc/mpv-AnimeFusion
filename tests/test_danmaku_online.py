"""Exercise the production danmaku request builder against a loopback HTTP server."""
from __future__ import annotations

import base64
import hashlib
import json
import argparse
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MPV = ROOT / "mpvapp" / "mpv.exe"
ONLINE = ROOT / "portable_config" / "script-modules" / "player_ui_danmaku_online.lua"
TITLE = '间谍「Agent」\\ 特别篇\nS2E6 😀'
CAPTURED: list[tuple[str, dict[str, str], bytes]] = []


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *_args):
        pass

    def _send(self, status: int, body: bytes):
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Connection", "close")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        try:
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass

    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length)
        CAPTURED.append((self.path, dict(self.headers.items()), body))
        self._send(200, b'{"success":true,"isMatched":true,"matches":[{"episodeId":17}]}' )

    def do_GET(self):
        if self.path == "/large":
            self._send(200, b"x" * 65537)
        elif self.path == "/failure":
            self._send(503, json.dumps({"errorMessage":"服务器维护中"},ensure_ascii=False).encode("utf-8"))
        elif self.path == "/slow":
            time.sleep(1.5)
            self._send(200, b'{"ok":true}')
        else:
            CAPTURED.append((self.path, dict(self.headers.items()), b""))
            self._send(200, b'{"ok":true}')


def lua_long(value: str) -> str:
    level = 0
    while "]" + "=" * level + "]" in value:
        level += 1
    fence = "=" * level
    return f"[{fence}[{value}]{fence}]"


def lua_quote(value: str) -> str:
    return json.dumps(value, ensure_ascii=False)


def lua_eval(code: str) -> None:
    """Run assertions in the same mpv Lua runtime used by the packaged player."""
    with tempfile.TemporaryDirectory(prefix="danmaku-online-") as directory:
        script = Path(directory) / "danmaku_online_test.lua"
        wrapper = (
            "local ok,err=xpcall(function()\n" + code + "\nend,debug.traceback)\n"
            "if ok then print('PASS danmaku online Lua contracts') else print(err) end\n"
            "mp.commandv('quit',ok and 0 or 1)\n"
        )
        script.write_text(wrapper, encoding="utf-8")
        result = subprocess.run(
            [str(MPV), "--no-config", "--load-scripts=no", "--idle=yes", "--vo=null", f"--script={script}"],
            cwd=MPV.parent,
            capture_output=True,
            timeout=8,
            check=False,
        )
        output = result.stdout + result.stderr
        if result.returncode != 0 or b"PASS danmaku online Lua contracts" not in output:
            raise AssertionError(output.decode("utf-8", "replace"))


class DanmakuOnlineTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        CAPTURED.clear()
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()
        cls.base = f"http://127.0.0.1:{cls.server.server_port}"

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join(timeout=2)

    def build(self, url: str, body: str | None, *, timeout=5, response_limit=8 * 1024 * 1024,
              app_id="", secret="") -> dict:
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "request.json"
            options = "{timeout=" + str(timeout) + ",response_limit=" + str(response_limit)
            if app_id:
                options += ",app_id=" + lua_long(app_id) + ",app_secret=" + lua_long(secret)
            options += "}"
            body_arg = "nil" if body is None else lua_long(body)
            code = (
                "local module=dofile(" + lua_long(str(ONLINE).replace("\\", "/")) + ");"
                "local file=assert(io.open(" + lua_long(str(output).replace("\\", "/")) + ",'wb'));"
                "file:write(require('mp.utils').format_json(module.request_spec(" + lua_long(url) + "," + body_arg + "," + options + ")));file:close()"
            )
            lua_eval(code)
            return json.loads(output.read_text(encoding="utf-8"))

    def run_request(self, command: dict, *, process_timeout=8) -> subprocess.CompletedProcess[bytes]:
        # Execute through mpv, including its Windows subprocess implementation.
        # Running curl via Python alone would miss dropped stdin_data in mpv.
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "response.json"
            code = (
                "local mp=require 'mp';local utils=require 'mp.utils';"
                "local spec=utils.parse_json(" + lua_long(json.dumps(command)) + ");"
                "spec.name='subprocess';spec.playback_only=false;"
                "spec.capture_stdout=true;spec.capture_stderr=true;"
                "local result=assert(mp.command_native(spec));"
                "local file=assert(io.open(" + lua_long(str(output).replace(chr(92), '/')) + ",'wb'));"
                "file:write(utils.format_json(result));file:close()"
            )
            lua_eval(code)
            response = json.loads(output.read_text(encoding="utf-8"))
            return subprocess.CompletedProcess(command["args"], response["status"],
                response.get("stdout", "").encode("utf-8"), response.get("stderr", "").encode("utf-8"))

    def test_utf8_json_and_official_signature(self):
        body = json.dumps({"fileName": TITLE}, ensure_ascii=False, separators=(",", ":"))
        url = self.base + "/api/v2/match?client=local"
        command = self.build(url, body, app_id="test-app", secret="test-secret")
        result = self.run_request(command)
        self.assertEqual(result.returncode, 0, result.stderr.decode("utf-8", "replace"))
        self.assertEqual(len(CAPTURED), 1)
        path, headers, received = CAPTURED.pop()
        self.assertEqual(path, "/api/v2/match?client=local")
        self.assertEqual(received, body.encode("utf-8"))
        headers = {key.lower(): value for key, value in headers.items()}
        self.assertIn("x-timestamp", headers, repr(headers))
        timestamp = headers["x-timestamp"]
        expected = base64.b64encode(hashlib.sha256(
            f"test-app{timestamp}/api/v2/matchtest-secret".encode("utf-8")
        ).digest()).decode("ascii")
        self.assertEqual(headers["x-appid"], "test-app")
        self.assertEqual(headers["x-signature"], expected)
        self.assertNotIn("x-sign", headers)
        self.assertEqual(json.loads(result.stdout.decode("utf-8"))["matches"][0]["episodeId"], 17)

    def test_server_error_body_is_reported_without_extra_guidance(self):
        result = self.run_request(self.build(self.base + "/failure", None))
        self.assertEqual(result.returncode, 22)
        self.assertEqual(json.loads(result.stdout.decode("utf-8"))["errorMessage"], "服务器维护中")

    def test_response_is_capped_before_capture(self):
        command = self.build(self.base + "/large", None, response_limit=65536)
        result = self.run_request(command)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(b"file", result.stderr.lower())

    def test_http_timeout_is_applied(self):
        command = self.build(self.base + "/slow", None, timeout=1)
        started = time.monotonic()
        result = self.run_request(command, process_timeout=5)
        elapsed = time.monotonic() - started
        self.assertNotEqual(result.returncode, 0)
        self.assertLess(elapsed, 4, f"HTTP timeout did not bound the request ({elapsed:.1f}s)")

    def test_anime_search_keeps_season_and_platform_choices(self):
        # The example service returns shows and merged platform children here,
        # before any episode details are requested.
        response = {
            "success": True,
            "animes": [
                {"animeId": 101, "animeTitle": "间谍过家家 第一季(2022)【日番】from dandan",
                 "source": "dandan", "episodeCount": 25, "mergedChildren": []},
                {"animeId": 202, "animeTitle": "间谍过家家 第二季(2023)【日番】from tencent&iqiyi",
                 "source": "tencent", "episodeCount": 12, "typeDescription": "日番",
                 "mergedChildren": [{"animeId": 203, "source": "iqiyi", "episodes": 12}]},
            ],
        }
        body = lua_long(json.dumps(response, ensure_ascii=False))
        online_path = lua_long(str(ONLINE).replace(chr(92), "/"))
        lua_eval(f"""
local online=dofile({online_path})
local result=online.search_results({body},'间谍过家家',require('mp.utils').parse_json)
assert(#result==2,'show-level search must retain both seasons: '..#result)
assert(result[1].season==1 and result[2].season==2,'season classification is missing')
assert(result[2].kind=='日番' and result[2].year==2023,'API type/year classification is missing')
assert(result[2].id=='202' and #result[2].platforms==2,'platform children were flattened or lost')
assert(result[2].platforms[1].name=='腾讯视频' and result[2].platforms[2].name=='爱奇艺')
""")

    def test_roman_and_explicit_season_markers(self):
        online_path = lua_long(str(ONLINE).replace(chr(92), "/"))
        lua_eval(f"""
local online=dofile({online_path})
local base='无职转生 ~到了异世界就拿出真本事~'
for _,marker in ipairs({{'Ⅲ','III',' 第三季',' Season 3',' S03'}}) do
    local info=online.show_info('无职转生'..marker..' ～到了异世界就拿出真本事～(2026)from dandan')
    assert(info.season==3,marker..' season was not recognized')
    assert(info.series==base,marker..' season was not removed from the series identity: '..info.series)
    local title='无职转生'..marker..' ～到了异世界就拿出真本事～ 第5集'
    local candidates=online.auto_candidates({{{{series=info.series,season=info.season,kind='TV动画',
        platforms={{{{id='33'}}}}}}}},title)
    assert(#candidates==1 and candidates[1].id=='33',marker..' did not match the current season')
end
assert(online.season_number('无职转生Ⅱ')==2)
assert(online.season_number('Overlord IV')==4)
for _,title in ipairs({{'无职转生 OVA(2022)','间谍过家家 代号：白(2023)',
    'Violet Evergarden','SPY x FAMILY','S3Drive','Final Fantasy XIV'}}) do
    assert(online.season_number(title)==nil,'Non-season title was misclassified: '..title)
end
""")

    def test_recent_title_matches_fullwidth_punctuation(self):
        online_path = lua_long(str(ONLINE).replace(chr(92), "/"))
        lua_eval(f"""
local online=dofile({online_path})
local title='无职转生：到了异世界就拿出真本事 (2026) S3E1 - Burn Bright, Mad Dog'
local info=online.show_info('无职转生Ⅲ ～到了异世界就拿出真本事～(2026)')
local shows={{{{series=info.series,season=info.season,year=info.year,kind='TV动画',
    platforms={{{{id='33'}}}}}}}}
assert(#online.auto_candidates(shows,title)==1,'CJK punctuation excluded the same series')
local episodes={{{{id='301',number_value=1}}}}
assert(online.match_verified({{episodeId='301',animeTitle=info.label}},title,episodes))
shows[1].season=2
assert(#online.auto_candidates(shows,title)==0,'Wrong season must remain excluded')
shows[1].season=3;shows[1].year=2024
assert(#online.auto_candidates(shows,title)==1,'A year difference alone must not exclude a title and season match')
shows[1].year=2026;shows[1].series=info.series..' OVA'
assert(#online.auto_candidates(shows,title)==0,'Different works must remain excluded')
""")

    def test_matching_uses_provider_aliases_and_limited_character_differences(self):
        online_path = lua_long(str(ONLINE).replace(chr(92), "/"))
        lua_eval(f"""
local online=dofile({online_path})
local data={{animes={{{{animeId=51,animeTitle='星海中的旅行者 第二季(2024)',source='qq',
    aliases={{'Voyagers of the Stars Season 2','星海旅行记 第二季','星海の旅人Ⅱ'}},episodeCount=12}}}}}}
local shows=online.search_results('x','',function()return data end)
for _,title in ipairs({{'Voyagers of the Stars (2025) S2E5.mkv',
    '星海旅行记 (2024) S2E5','星海の旅人 S2E5',
    '星海里的旅行者 (2024) S2E5','星海中的旅者 (2024) S2E5'}}) do
    local candidates=online.auto_candidates(shows,title)
    assert(#candidates==1 and candidates[1].id=='51','Generic alias/typo matching failed: '..title)
end
assert(#online.auto_candidates(shows,'另一个完全不同的故事 S2E5')==0)
assert(#online.auto_candidates(shows,'星海中的旅行者 S3E5')==0)
assert(#online.auto_candidates(shows,'星海中的旅行者 第5集')==0,'Unknown season must not guess season two')
local episodes={{{{id='5',number_value=5}},{{id='6',number_value=6}},{{id='50',number_value=5,extra=true}}}}
local title='星海里的旅行者 S2E5'
assert(online.auto_episode(episodes,title).id=='5')
assert(online.match_verified({{episodeId='5',animeTitle=data.animes[1].animeTitle}},title,episodes))
assert(not online.match_verified({{episodeId='6',animeTitle=data.animes[1].animeTitle}},title,episodes))
assert(not online.match_verified({{episodeId='50',animeTitle=data.animes[1].animeTitle}},title,episodes))
""")

    def test_close_works_are_ambiguous_and_exact_title_wins(self):
        online_path = lua_long(str(ONLINE).replace(chr(92), "/"))
        lua_eval(f"""
local online=dofile({online_path})
local shows={{
    {{series='星海中的旅行者',season=2,year=2024,platforms={{{{id='51'}}}}}},
    {{series='星海外的旅行者',season=2,year=2025,platforms={{{{id='52'}}}}}},
}}
local candidates,reason=online.auto_candidates(shows,'星海里的旅行者 (2024) S2E5')
assert(#candidates==0 and reason=='ambiguous','Close scores must not choose a work by API order or year')
local exact=online.auto_candidates(shows,'星海中的旅行者 S2E5')
assert(#exact==1 and exact[1].id=='51','Exact title should outrank a similar work')
shows[2].series=shows[1].series;shows[2].year=2024
assert(#online.auto_candidates(shows,'星海中的旅行者 S2E5')==2,'Same work across platforms remains available')
shows[2].part=2
local split,why=online.auto_candidates(shows,'星海中的旅行者 S2E5')
assert(#split==0 and why=='ambiguous','Unspecified split parts must not pick different local episodes')
local shorts={{{{series='星旅',season=1,platforms={{{{id='1'}}}}}}}}
assert(#online.auto_candidates(shorts,'星语 S1E1')==0,'Short unrelated titles must not match')
local numbers={{{{series='星海中的第86位旅行者',season=1,platforms={{{{id='1'}}}}}}}}
assert(#online.auto_candidates(numbers,'星海中的第87位旅行者 S1E1')==0,'Title numbers are not fuzzy')
""")

    def test_alias_seasons_and_filename_metadata_are_parsed_without_guessing(self):
        online_path = lua_long(str(ONLINE).replace(chr(92), "/"))
        lua_eval(f"""
local online=dofile({online_path})
local data={{animes={{{{animeId=51,animeTitle='星海旅行记',source='qq',
    aliases={{'Voyagers of the Stars Season 2','星海旅行记 第二季'}}}}}}}}
local shows=online.search_results('x','',function()return data end)
assert(shows[1].season==2,'Consistent provider aliases should identify a season')
assert(#online.auto_candidates(shows,'星海旅行记 S2E5')==1)
data.animes[1].aliases[2]='星海旅行记 第三季'
shows=online.search_results('x','',function()return data end)
assert(#online.auto_candidates(shows,'星海旅行记 S2E5')==0,'Conflicting seasons must remain unresolved')
for _,title in ipairs({{'[发布组] 星海旅行记 Ｓ０２．Ｅ０５ [1080p][ABC123].mkv',
    '星海旅行记 S02_E05.mkv','星海旅行记 S02 E05.mkv','星海旅行记 第二季 - 05 [HEVC].mkv',
    '星海旅行记 第二季 第05集.mkv'}}) do
    local anime,episode,season=online.episode_query(title)
    assert(anime=='星海旅行记' and episode==5 and season==2,'Filename metadata mismatch: '..title)
end
for _,title in ipairs({{'星海旅行记 S2E5.5.mkv','星海旅行记 S2E5.5','星海旅行记 第二季 EP5.5'}}) do
    local _,episode=online.episode_query(title)
    assert(episode==5.5,'Fractional episode must not truncate to episode five: '..title)
end
assert(not online.auto_episode({{{{id='5',number_value=5}}}},'星海旅行记 S2E5.5.mkv'))
assert(online.search_keyword('[发布组] 星海旅行记【字幕组】 第二季 S2.E05 [HEVC].mkv')=='星海旅行记',
    'The one automatic search must omit release tags and season metadata')
for _,title in ipairs({{'星海旅行记 第二季 S3E5','星海旅行记 S0E5','星海旅行记 S2E0'}}) do
    assert(not online.episode_query(title),'Invalid/conflicting season or episode must not guess: '..title)
end
""")

    def test_match_requires_same_season_and_local_episode_number(self):
        online_path = lua_long(str(ONLINE).replace(chr(92), "/"))
        lua_eval(f"""
local online=dofile({online_path})
local filename='间谍过家家 (2023) S2E12.mkv'
local detail={{bangumi={{episodes={{{{episodeId=91,episodeNumber=12,episodeTitle='第37集_12'}},
    {{episodeId=92,episodeNumber=11,episodeTitle='第36集_11'}}}}}}}}
local episodes=online.bangumi_episodes('x',function()return detail end)
assert(#episodes==2 and episodes[1].number_value==12)
assert(online.match_verified({{episodeId='91',animeTitle='间谍过家家 第二季(2023)【日番】from qq'}},filename,episodes))
assert(not online.match_verified({{episodeId='92',animeTitle='间谍过家家 第二季(2023)【日番】from qq'}},filename,episodes))
assert(not online.match_verified({{episodeId='91',animeTitle='间谍过家家 第一季(2022)【日番】from qq'}},filename,episodes))
""")

    def test_auto_search_selects_first_season_episode_without_season_label(self):
        online_path = lua_long(str(ONLINE).replace(chr(92), "/"))
        lua_eval(f"""
local online=dofile({online_path})
local title='尼古喵喵 (2026) S1E5 - 喵喵们要去秘境啦喵'
local shows={{
    {{series='尼古喵喵',season=nil,year=2026,kind='动画',label='尼古喵喵(2026)from renren',
      platforms={{{{id='101',key='renren'}}}}}},
    {{series='尼古喵喵',season=2,year=2027,kind='动画',label='尼古喵喵 第二季(2027)',
      platforms={{{{id='201',key='renren'}}}}}},
    {{series='别的作品',season=nil,year=2026,kind='动画',label='别的作品',
      platforms={{{{id='301',key='renren'}}}}}},
}}
local candidates=online.auto_candidates(shows,title)
assert(#candidates==1 and candidates[1].id=='101')
local episodes={{{{id='4',number_value=4,extra=false,label='第4集'}},
    {{id='5',number_value=5,extra=false,label='第5集'}},
    {{id='50',number_value=5,extra=true,label='第5集预告'}}}}
assert(online.auto_episode(episodes,title).id=='5')
assert(online.match_verified({{episodeId='5',animeTitle='尼古喵喵(2026)from renren'}},title,episodes))
assert(not online.auto_episode({{{{id='5',number_value=5}},{{id='6',number_value=5}}}},title))
""")

    def test_detail_separates_preview_and_corrects_interleaved_episode_numbers(self):
        online_path = lua_long(str(ONLINE).replace(chr(92), "/"))
        lua_eval(f"""
local online=dofile({online_path})
local detail={{bangumi={{episodes={{
    {{episodeId=1,episodeNumber=1,episodeTitle='【migu】01 跟踪'}},
    {{episodeId=2,episodeNumber=7,episodeTitle='【bilibili1】第26话 跟踪'}},
    {{episodeId=3,episodeNumber=8,episodeTitle='【bilibili1】第27话 邦德'}},
    {{episodeId=4,episodeNumber=13,episodeTitle='【bilibili1】第27话预告'}},
    {{episodeId=5,episodeNumber=17,episodeTitle='【bilibili1】第32话 任务'}},
    {{episodeId=6,episodeNumber=26,episodeTitle='【bilibili1】第37话 家庭'}},
}}}}}}
local episodes=online.bangumi_episodes('x',function()return detail end,nil,'bilibili')
assert(#episodes==5,'other platform was not removed')
assert(episodes[4].number_value==7 and episodes[5].number_value==12,'playlist positions were mistaken for episode numbers')
assert(episodes[3].extra and episodes[3].group=='预告与其他','preview classification is missing')
assert(online.match_verified({{episodeId='6',animeTitle='间谍过家家 第二季(2023)【日番】from bilibili'}},
    '间谍过家家 (2023) S2E12.mkv',episodes))
assert(not online.match_verified({{episodeId='4',animeTitle='间谍过家家 第二季(2023)【日番】from bilibili'}},
    '间谍过家家 (2023) S2E12.mkv',episodes))
""")

    def test_lua_helpers_validate_inputs_and_bound_parsing(self):
        online_path = lua_long(str(ONLINE).replace(chr(92), "/"))
        core_path = lua_long(str(ROOT / "portable_config/script-modules/player_ui_core.lua").replace(chr(92), "/"))
        code = f"""
local online=dofile({online_path})
assert(online.base64('中')=='5Lit')
local entry,why=online.parse_server_entry('https://danmaku.example/api|主线路')
assert(entry and entry.url=='https://danmaku.example/api' and entry.note=='主线路',why)
assert(not online.parse_server_entry('javascript:bad'))
assert(#online.parse_servers('https://a.example,https://a.example,ftp://bad')==1)
assert(online.urlencode('间谍')=='%E9%97%B4%E8%B0%8D')
assert(online.match_body('C:/Anime/Show S01E02.mkv',1048576,120)=='{{"fileName":"Show S01E02","fileSize":1048576,"videoDuration":120}}')
assert(online.match_body('C:/Anime/Show S01E02.mkv',1048576,120,string.rep('A',32))=='{{"fileName":"Show S01E02","fileHash":"'..string.rep('a',32)..'","fileSize":1048576,"videoDuration":120}}')
assert(not online.match_body('Show.mkv',0,0,'not-a-hash'):find('fileHash',1,true))
local name=online.query_name('https://media.example/stream.mkv?token=secret','动画名',function(s)return s end)
assert(name=='动画名')
local anime,episode=online.episode_query('间谍过家家 (2023) S2E6 - 战栗的豪华邮轮')
assert(anime=='间谍过家家' and episode==6)
local anime2,episode2=online.episode_query('Spy x Family - EP 06')
assert(anime2=='Spy x Family' and episode2==6)
assert(online.search_keyword('[Fansub] Spy x Family S02E06 [1080p].mkv')=='Spy x Family')
assert(online.search_keyword('没有集数的番剧标题.mkv')=='没有集数的番剧标题')
assert(online.limit_text(string.rep('间谍',10),3)=='间谍…')
local huge_title=string.rep('番',10000)
local response={{}};response.animes={{}};local anime={{}};anime.animeId=17;anime.animeTitle=huge_title;response.animes[1]=anime
local bounded=online.search_results('x','测试',function()return response end)
assert(#bounded==1,'bounded result count='..#bounded)
assert(#bounded[1].label<=610,'bounded label bytes='..#bounded[1].label)
assert(bounded[1].id=='17','bounded show id')
local expanded={{animes={{}}}}
for i=1,125 do expanded.animes[i]={{animeId=i,animeTitle='结果'..i,source='qq',episodeCount=1}} end
local every_result=online.search_results('x','测试',function()return expanded end)
assert(#every_result==125,'search results were truncated: '..#every_result)
local expanded_children={{animeId=999,animeTitle='多平台',source='qq',episodeCount=1,mergedChildren={{}}}}
for i=1,25 do expanded_children.mergedChildren[i]={{animeId=1000+i,source='provider'..i,episodes=1}} end
local children=online.search_results('x','测试',function()return {{animes={{expanded_children}}}} end)
assert(#children[1].platforms==26,'merged platform results were truncated: '..#children[1].platforms)
assert(online.episode_id(17)=='17' and online.episode_id('0')=='0')
assert(not online.episode_id(string.rep('9',21)) and not online.episode_id('17/extra'))
local match_data={{isMatched=true,matches={{}}}}
match_data.matches[1]={{episodeId=17,animeTitle=huge_title,episodeTitle=huge_title}}
local bounded_match=online.match_result('x',function()return match_data end)
assert(bounded_match and bounded_match.episodeId=='17')
assert(#bounded_match.animeTitle<=361 and #bounded_match.episodeTitle<=241,
  #bounded_match.animeTitle..'/'..#bounded_match.episodeTitle)
local invalid_match_data={{isMatched=true,matches={{}}}}
invalid_match_data.matches[1]={{episodeId=string.rep('9',21)}}
local invalid_match=online.match_result('x',function()return invalid_match_data end)
assert(not invalid_match,'oversized match id must be rejected')
local episode_data={{bangumi={{episodes={{{{episodeId='19',episodeNumber=12,episodeTitle=huge_title}}}}}}}}
local bounded_episode=online.bangumi_episodes('x',function()return episode_data end)
assert(#bounded_episode==1 and bounded_episode[1].id=='19' and #bounded_episode[1].label<=361)
local invalid_episode_data={{bangumi={{episodes={{{{episodeId=string.rep('9',21),episodeNumber=1}}}}}}}}
local invalid_episode=online.bangumi_episodes('x',function()return invalid_episode_data end)
assert(#invalid_episode==0,'oversized detail id must be rejected')
local core=dofile({core_path})
local body='{{"comments":[{{"p":"4,1,16777215","m":"弹幕"}},null,{{"p":"-1,1,1","m":"bad"}}]}}'
local parsed,err=online.parse_comments(body,function()return {{comments={{
  {{p='4,1,16777215',m='弹幕'}},false,{{p='-1,1,1',m='bad'}}}}}} end,core)
assert(parsed and #parsed==1 and parsed[1].text=='弹幕',err)
local empty,empty_error=online.parse_comments('{{"comments":[]}}',function()return {{comments={{}}}} end,core)
assert(type(empty)=='table' and #empty==0 and empty_error==nil,'valid empty episodes must be represented as no comments')
local invalid,invalid_error=online.parse_comments('x',function()return {{comments='bad'}} end,core)
assert(not invalid and invalid_error)
local xml,xml_error=online.parse_comments('<i><d p="1,1,1,16777215">弹幕</d></i>',function()error('invalid JSON')end,core)
assert(not xml and xml_error)
"""
        lua_eval(code)

    def test_renren_members_scroll_normally_and_keep_color(self):
        core_path = lua_long(str(ROOT / 'portable_config/script-modules/player_ui_core.lua').replace(chr(92), '/'))
        online_path = lua_long(str(ONLINE).replace(chr(92), '/'))
        lua_eval(f"""
local online=dofile({online_path})
local core=dofile({core_path})
local data={{comments={{
  {{p='9,2,14463824,[renren]',m='member'}},
  {{p='10,1,14463824,[renren]',m='normal yellow'}},
  {{p='11,6,16777215,[bilibili]user',m='real reverse'}},
  {{p='12,4,16777215,[renren]',m='bottom'}},
  {{p='13,5,16777215,[renren]',m='top'}},
  {{p='14,2,16777215,[other]',m='other source'}}
}}}}
local list,err=online.parse_comments('x',function()return data end,core)
assert(list and #list==6,err)
assert(list[1].mode==1 and list[1].color==14463824,'Renren membership must not change scroll direction or color')
assert(list[2].mode==1 and list[2].color==14463824)
assert(list[3].mode==6,'true reverse comments must remain reverse')
assert(list[4].mode==4 and list[5].mode==5,'fixed comments must remain fixed')
assert(list[6].mode==2,'source-specific correction must not affect other providers')
""")

    def test_file_hash_uses_the_first_sixteen_mib(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "剧集 sample.mkv"
            sample = b"animejanai" * (16 * 1024 * 1024 // 11 + 2)
            path.write_bytes(sample)
            output = Path(directory) / "hash.txt"
            code = (
                "local module=dofile(" + lua_long(str(ONLINE).replace(chr(92), "/")) + ");"
                "local file=assert(io.open(" + lua_long(str(output).replace(chr(92), "/")) + ",'wb'));"
                "file:write(module.file_hash(" + lua_long(str(path)) + "));file:close()"
            )
            lua_eval(code)
            expected = hashlib.md5(sample[:16 * 1024 * 1024]).hexdigest().encode("ascii")
            self.assertEqual(output.read_bytes(), expected)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--mpv", type=Path, default=MPV)
    options, unittest_args = parser.parse_known_args()
    MPV = options.mpv.resolve()
    if not MPV.is_file():
        raise SystemExit(f"mpv Lua runtime not found: {MPV}")
    unittest.main(argv=[sys.argv[0], *unittest_args], verbosity=2)

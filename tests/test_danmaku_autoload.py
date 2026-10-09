"""Run the production autoloader in mpv with isolated source records and real HTTP."""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "portable_config/scripts/player_ui_danmaku.lua"


def quote(value):
    return "[[" + str(value).replace(chr(92), "/") + "]]"


def run(install, video, directory, source_config, records, settings=None):
    directory.mkdir(parents=True, exist_ok=True)
    local = directory / "localdata"
    local.mkdir(exist_ok=True)
    (local / "mpv-AnimeFusion-danmaku.conf").write_bytes(source_config)
    (local / "mpv-AnimeFusion-danmaku-sources.json").write_bytes(records)
    if settings:
        (local / "mpv-AnimeFusion-DanmakuFactory.json").write_bytes(settings)
    lua = DRIVER.replace("@@FACTORY@@", quote(install / "animejanai/danmaku/DanmakuFactory.exe"))
    lua = lua.replace("@@SOURCE@@", quote(SOURCE))
    lua = lua.replace("@@OUTPUT@@", quote(directory / "result.json"))
    (directory / "driver.lua").write_text(lua, encoding="utf-8")
    env = os.environ.copy()
    env["LOCALAPPDATA"] = str(local)
    env["TEMP"] = str(local)
    result = subprocess.run([str(install / "app/mpv.exe"), "--no-config", "--load-scripts=no",
        "--vo=null", "--ao=null", "--hwdec=no", "--script=" + str(directory / "driver.lua"),
        str(video)], env=env, capture_output=True, timeout=52)
    if not (directory / "result.json").exists():
        raise AssertionError(result.stdout.decode("utf-8", "replace") + result.stderr.decode("utf-8", "replace"))
    trace = json.loads((directory / "result.json").read_text(encoding="utf-8"))
    for notice in trace["notices"]:
        motion = re.search(r"\\move\(([-\d.]+),[-\d.]+,([-\d.]+),", notice)
        assert motion and float(motion[1]) > float(motion[2]), notice
    assert trace["center_messages"] == 0, trace
    if trace["state"]["loaded"]:
        assert trace["state"]["track"] == trace["secondary_sid"]
        assert trace["primary_sid"] != trace["secondary_sid"]
        assert any(str(trace["state"]["count"]) + "条弹幕大军正在袭来~~~" in n for n in trace["notices"])
    return trace


DRIVER = r"""local mp=require 'mp'
local utils=require 'mp.utils'
local trace={requests={},notices={},center_messages=0,states={}}
local async=mp.command_native_async
mp.command_native_async=function(spec,callback)
 if spec.args and spec.args[1]:find('curl.exe',1,true) then
  local item={path=spec.args[4]:match('^https?://[^/]+(.*)'),started=mp.get_time()}
  trace.requests[#trace.requests+1]=item
  return async(spec,function(ok,result,err)
   item.ok=ok;item.code=result and result.status;item.bytes=result and #(result.stdout or '')
   callback(ok,result,err)
  end)
 end
 return async(spec,callback)
end
local command=mp.command_native
mp.command_native=function(spec)
 if spec[1]=='expand-path' and spec[2]:find('DanmakuFactory.exe',1,true) then return @@FACTORY@@ end
 return command(spec)
end
local commandv=mp.commandv
mp.commandv=function(...)
 local args={...}
 if args[1]=='show-text' then trace.center_messages=trace.center_messages+1 end
 if args[1]=='sub-add' then
  local file=assert(io.open(args[2],'rb'));local body=file:read('*a');file:close()
  for line in body:gmatch('[^\r\n]+') do
   if line:find('弹幕准备中~~~',1,true) or line:find('条弹幕大军正在袭来~~~',1,true) then
    trace.notices[#trace.notices+1]=line
   end
  end
 end
 return commandv(...)
end
local done=false
local function finish(code)
 if done then return end;done=true
 trace.state=mp.get_property_native('user-data/player_ui/danmaku',{})
 trace.secondary_sid=mp.get_property_native('secondary-sid')
 trace.primary_sid=mp.get_property_native('sid')
 local file=assert(io.open(@@OUTPUT@@,'wb'));file:write(utils.format_json(trace));file:close()
 mp.commandv('quit',code)
end
mp.observe_property('user-data/player_ui/danmaku','native',function(_,s)
 if not s then return end
 trace.states[#trace.states+1]={state=s.autoload_state,status=s.status,count=s.count,time=mp.get_time()}
 if s.autoload_state=='loaded' or s.autoload_state=='error' or s.autoload_state=='not-found' or s.autoload_state=='empty' then
  mp.add_timeout(.5,function()finish(s.loaded and 0 or 1)end)
 end
end)
dofile(@@SOURCE@@)
mp.add_timeout(45,function()finish(2)end)
"""


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_GET(self):
        server = self.server
        server.paths.append(self.path)
        status = 200
        if "/comment/" in self.path:
            server.comment_requests += 1
            if server.comment_requests <= server.failures:
                status = 503
                data = {"errorMessage": "临时失败"}
            else:
                time.sleep(server.delay)
                data = {"comments": [{"p": f"{i * 4},1,16777215,[dandan]", "m": f"弹幕 {i}"}
                    for i in range(server.count)]}
        else:
            data = {"animes": []}
        body = json.dumps(data, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(body)


def loopback(install, video, directory):
    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    url = f"http://127.0.0.1:{server.server_port}"
    config = ("api_servers=" + url + "|测试线路\n").encode("utf-8")
    records = json.dumps({"星海旅行记/s2/p0": {"server_url": url, "id": "1", "key": "dandan",
        "label": "星海旅行记 第二季", "kind": "TV动画",
        "episodes": [{"id": "101", "label": "第1集", "number_value": 1, "extra": False}]}}
        , ensure_ascii=False).encode("utf-8")
    directory.mkdir(parents=True, exist_ok=True)
    sample = directory / "星海旅行记 (2020) S2E1.mp4"
    if not sample.exists():
        shutil.copyfile(video, sample)
    try:
        for name, failures, delay, count, state in [("slow", 0, 2.5, 3, "loaded"),
                ("retry", 3, 0, 3, "loaded"), ("empty", 0, 0, 0, "empty"),
                ("failure", 4, 0, 3, "error")]:
            server.failures, server.delay, server.count = failures, delay, count
            server.comment_requests, server.paths = 0, []
            trace = run(install, sample, directory / name, config, records)
            assert trace["state"]["autoload_state"] == state, trace
            assert server.comment_requests == failures + (0 if name == "failure" else 1), server.paths
            if name == "slow":
                assert any("弹幕准备中~~~" in n for n in trace["notices"]), trace
            if name in ("slow", "retry", "empty"):
                assert all("/comment/101" in path for path in server.paths), server.paths
                assert json.loads((directory / name / "localdata/mpv-AnimeFusion-danmaku-sources.json").read_text(
                    encoding="utf-8")) == json.loads(records)
            print("PASS", name, "state", state, "requests", server.comment_requests,
                "rendered", trace["state"]["count"])
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--install", type=Path, required=True)
    parser.add_argument("--video", type=Path, nargs="+", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--live", action="store_true")
    parser.add_argument("--installed", action="store_true")
    args = parser.parse_args()
    args.install = args.install.resolve()
    args.output = args.output.resolve()
    args.video = [p.resolve() for p in args.video]
    if args.installed:
        SOURCE = args.install / "portable_config/scripts/player_ui_danmaku.lua"
    if args.live:
        root = Path(os.environ["LOCALAPPDATA"])
        paths = [root / "mpv-AnimeFusion-danmaku.conf", root / "mpv-AnimeFusion-danmaku-sources.json"]
        before = [p.read_bytes() for p in paths]
        settings_path = root / "mpv-AnimeFusion-DanmakuFactory.json"
        settings = settings_path.read_bytes() if settings_path.exists() else None
        for i, video in enumerate(args.video):
            trace = run(args.install, video, args.output / str(i), *before, settings)
            assert trace["state"]["autoload_state"] == "loaded", trace
            assert len(trace["requests"]) == 1 and "/comment/" in trace["requests"][0]["path"], trace
            print("PASS live", video.name, "rendered", trace["state"]["count"], "requests", len(trace["requests"]))
        assert before == [p.read_bytes() for p in paths], "formal source records changed"
    else:
        loopback(args.install, args.video[0], args.output)

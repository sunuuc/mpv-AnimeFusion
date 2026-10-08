"""Focused notice lifecycle and native OSD regression checks, without GUI use."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
LUA = ROOT / '_probe/build-tools/msys/ucrt64/bin/luajit.exe'
MPV = Path(r'D:\Apps\mpv-AnimeFusion\app\mpv.exe')


class DanmakuNotices(unittest.TestCase):
    def test_pause_ready_and_once(self):
        source = (ROOT/'portable_config/scripts/player_ui_danmaku.lua').read_text(encoding='utf-8')
        helpers = source[source.index("local loaded='' ".rstrip()):source.index('local picker,generation')]
        begin = source.rindex("mp.register_event('file-loaded',function()")
        finish = source.index("mp.register_event('end-file',function()", begin)
        lifecycle = source[begin:finish]
        begin = source.index('end)()(mp,utils,function()') + len('end)()(mp,utils,function()')
        finish = source.index('\nend)\nlocal function kill', begin)
        ready = source[begin:finish]
        fixture = r'''
local idle=false
local messages,events,observers={},{},{}
local mp={}
function mp.get_property_bool() return idle end
function mp.get_property() return 'https://fixture.invalid/video' end
function mp.get_property_osd() return '' end
function mp.commandv(...) messages[#messages+1]={...} end
function mp.register_event(name,callback) events[name]=callback end
function mp.observe_property(name,kind,callback) observers[name]=callback end
function mp.add_timeout(seconds,callback,disabled)
    assert(seconds==2 and disabled)
    local t={enabled=false,cb=callback}
    function t:resume() self.enabled=true end
    function t:stop() self.enabled=false end
    function t:kill() self.enabled=false;self.killed=true end
    return t
end
local renderer={ready=false,busy=false,count=227}
local generation,autoload_generation=0,0
local o={autoload_danmaku=true}
local autoload_state='loading'
local function publish() end
'''
        fixture += helpers + lifecycle + '\nlocal ready=function()' + ready + '\nend\n'
        fixture += r'''
events['file-loaded']()
assert(preparation_timer.enabled)
idle=true;observers['core-idle']()
assert(not preparation_timer.enabled)
idle=false;observers['core-idle']()
assert(preparation_timer.enabled)
preparation_timer.cb()
assert(#messages==1 and messages[1][2]:find('弹幕准备中~~~',1,true))
assert(messages[1][4]=='0')
load_notice_pending=true;renderer.ready=true
ready();ready()
assert(#messages==2 and messages[2][2]:find('227条弹幕大军正在袭来~~~',1,true))
assert(messages[2][4]=='0')
renderer.ready=false;events['file-loaded']()
local delayed=preparation_timer
load_notice_pending=true;renderer.ready=true
ready()
assert(delayed.killed and preparation_timer==nil)
assert(#messages==3)
print('notice lifecycle passed')
'''
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder)/'lifecycle.lua'
            path.write_text(fixture, encoding='utf-8')
            result = subprocess.run([str(LUA), str(path)], capture_output=True, timeout=20)
        self.assertEqual(result.returncode, 0, result.stderr.decode('utf-8', errors='replace'))

    def test_native_completion_with_stats_and_osd_disabled(self):
        input_xml = os.environ.get('DANMAKU_NOTICE_FIXTURE')
        with tempfile.TemporaryDirectory() as folder:
            folder = Path(folder)
            if not input_xml:
                xml = folder/'comments.xml'
                xml.write_text('<i><d p="9,2,25,14463824,0,0,0,0">黄色反向弹幕</d></i>', encoding='utf-8')
                input_xml = str(xml)
            probe = folder/'probe.lua'
            probe.write_text("""local mp=require 'mp'
mp.add_timeout(.5,function()mp.commandv('script-binding','animejanaistats/show_animejanai_stats')end)
mp.add_timeout(1,function()mp.commandv('script-message','player_ui-danmaku-load',[[%s]])end)
mp.observe_property('user-data/player_ui/danmaku','native',function(_,d)
    if d and d.loaded then mp.add_timeout(.5,function()mp.commandv('quit')end) end
end)
mp.add_timeout(15,function()mp.commandv('quit')end)
""" % input_xml, encoding='utf-8')
            log = folder/'native.log'
            args = [str(MPV), '--config=yes', '--load-scripts=no', '--vo=null', '--ao=null',
                    '--idle=yes', '--terminal=no', '--osd-level=0',
                    '--msg-level=cplayer=trace',
                    r'--config-dir=D:\Apps\mpv-AnimeFusion\portable_config',
                    '--script-opts=player_ui_danmaku-autoload_danmaku=no',
                    '--log-file='+str(log),
                    '--script='+str(ROOT/'portable_config/scripts/player_ui_danmaku.lua'),
                    '--script='+str(ROOT/'portable_config/scripts/animejanaistats.lua'),
                    '--script='+str(probe)]
            result = subprocess.run(args, capture_output=True, timeout=25)
            text = log.read_text(encoding='utf-8', errors='replace')
            if os.environ.get('DANMAKU_NOTICE_LOG'):
                Path(os.environ['DANMAKU_NOTICE_LOG']).write_text(text, encoding='utf-8')
        self.assertEqual(result.returncode, 0, result.stderr.decode('utf-8', errors='replace'))
        self.assertNotIn('Lua error', text)
        self.assertTrue('条弹幕大军正在袭来~~~' in text, 'Completion notice absent; inspect native replay log')
        self.assertIn('level="0"', text)
        self.assertIn('osd-overlay', text)
        self.assertNotIn('Run command: show-text, flags=64, args=[text="AI', text)


if __name__ == '__main__':
    unittest.main()

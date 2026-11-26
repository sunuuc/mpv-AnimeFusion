"""Run the actual Lua pointer guards without creating a desktop window."""
from pathlib import Path
import os
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
MPV = Path(os.environ.get('ANIMEFUSION_TEST_MPV', r'D:\Apps\mpv-AnimeFusion\app\mpv.exe'))


class PointerTransitionTest(unittest.TestCase):
    def test_seek_preview_is_not_loaded_or_called(self):
        source = (ROOT / 'portable_config/scripts/player_ui.lua').read_text(encoding='utf-8-sig')
        self.assertNotIn('thumbfast', source)
        self.assertNotIn('thumb_timer', source)
        self.assertFalse((ROOT / 'portable_config/scripts/thumbfast.lua').exists())
        self.assertIn("if state.drag=='seek' and not state.menu then", source)
        installer = (ROOT / 'src/player/src/Setup/Inno/inno-setup.iss').read_text(encoding='utf-8-sig')
        self.assertIn('Type: files; Name: "{app}\\portable_config\\scripts\\thumbfast.lua"', installer)

    def test_canceled_resize_and_fullscreen_clicks_cannot_seek(self):
        source = (ROOT / 'portable_config/scripts/player_ui.lua').read_text(encoding='utf-8-sig')
        handlers = source[source.index('local mouse_bound=false'):source.index('local function wheel(delta)')]
        binding = source[source.index('local function bind_mouse(enabled)'):source.index('local cover_key')]
        start = source.index("        if p=='fullscreen' or p=='osd-dimensions' or p=='display-hidpi-scale' then")
        transition = source[start:source.index('        -- Property changes', start)]
        script = r'''
local real=require 'mp'
local now,w,h=10,1280,720
local seeks=0
local state={x=640,y=650,visible=true}
local layout={w=w,h=h,scale=1}
local buttons={{id='seek'}}
local client_w,client_h=w,h
package.loaded.ffi={
    cdef=function()end,cast=function(_,window)return window end,new=function()return {[0]={}}end,
    load=function(name)
        assert(name=='user32')
        return {GetClientRect=function(hwnd,rect)
            assert(hwnd==23)
            rect[0].right,rect[0].bottom=client_w,client_h
            return 1
        end}
    end}
local mp={get_time=function()return now end,get_osd_size=function()return w,h end,
    get_property=function(name)assert(name=='platform');return 'windows' end,
    get_property_number=function(name)assert(name=='wid');return 23 end,
    get_mouse_pos=function()return 640,650 end,
    add_forced_key_binding=function()end,remove_key_binding=function()end,
    add_timeout=function(_,callback)return {kill=function()end,callback=callback}end}
local function kill(timer)if timer then timer:kill()end end
local core={finite=function()return true end}
local menus={hover=function()end,drag=function()end}
local function hide_thumb()end
local function request_render()end
local function show()end
local function sync_timers()end
local function num(name,default)return name=='duration' and 1400 or default end
local function bool(name)return name~='window-minimized' end
local function hit()return buttons[1] end
local function seek_value()return 700 end
local function cmd(command)if command=='seek' then seeks=seeks+1 end end
local function activate()error('unexpected control action')end
''' + handlers + binding + r'''
local function check(value,message)assert(value,message)end
mouse_button({event='down'});check(state.drag=='seek','seek did not arm')
mouse_button({event='up',canceled=true});check(seeks==0 and not state.drag,'canceled release sought')
client_w,client_h=2560,1600
mouse_button({event='down'});mouse_button({event='up'})
check(seeks==0 and not state.drag,'new host coordinates sought on the old OSD timeline')
client_w,client_h=w,h
mouse_button({event='down'});check(state.drag=='seek','matching the host did not rearm the timeline')
client_w,client_h=2560,1600
mouse_button({event='up'});check(seeks==0 and not state.drag,'a mid-gesture host resize sought')
client_w,client_h=w,h
mouse_button({event='down'});w=2560;h=1600
mouse_button({event='up'});check(seeks==0 and not state.drag,'stale viewport sought')
client_w,client_h=w,h
layout={w=w,h=h,scale=1}
mouse_button({event='down'})
bind_mouse(true)
for _,p in ipairs({'fullscreen','osd-dimensions','display-hidpi-scale'}) do
''' + transition + r'''
    bind_mouse(false)
    check(mouse_bound and not state.drag and #buttons==0,'transition lost double-click guard or retained old controls')
    mouse_button({event='press'});check(seeks==0,'transition click sought')
end
now=now+.301
mouse_guard_timer.callback()
bind_mouse(false);check(not mouse_bound,'double-click guard did not expire')
buttons={{id='seek'}};bind_mouse(true)
mouse_button({event='press'});check(seeks==1,'seek stayed disabled after transition')
real.msg.info('PASS pointer cancel, viewport change, fullscreen cooldown, normal seek')
real.commandv('quit',0)
'''
        with tempfile.TemporaryDirectory(prefix='pointer-headless-') as path:
            test = Path(path) / 'pointer.lua'
            test.write_text(script, encoding='utf-8')
            result = subprocess.run([str(MPV), '--no-config', '--load-scripts=no', '--idle=yes', '--vo=null', '--ao=null', '--script=' + str(test)], capture_output=True, timeout=15)
            log = result.stdout + result.stderr
            self.assertEqual(result.returncode, 0, log.decode('utf-8', errors='replace'))
            self.assertIn(b'PASS pointer cancel', log)
            print('PASS pointer cancel, viewport change, fullscreen cooldown, normal seek')


if __name__ == '__main__':
    unittest.main()

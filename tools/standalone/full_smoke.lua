local mp=require 'mp'
local function check()
    local ok,why=xpcall(function()
        assert(#(mp.get_property_native('vf',{}) or {})==0,'Fresh install unexpectedly enables AI processing')
        assert(mp.get_property_number('vo-presented-frame-count',0)>0,'No video output')
        local eight,stats=false,false
        for _,b in ipairs(mp.get_property_native('input-bindings',{}) or {}) do
            if (b.key or ''):lower()=='ctrl+8' and (b.cmd or ''):find('aji-slot 8',1,true) then
                assert(not b.cmd:find('apply-profile upscale-on',1,true),'Unsafe legacy shortcut in complete install')
                eight=true
            end
            if (b.cmd or ''):find('show_animejanai_stats',1,true) then stats=true end
        end
        assert(eight,'Preset hotkeys missing')
        assert(stats,'Native status panel shortcut missing')
        mp.commandv('script-binding','animejanaistats/show_animejanai_stats')
        require('mp.msg').info('PASS empty-directory full install: default config/controllers/native counter/shortcuts')
    end,debug.traceback)
    if not ok then require('mp.msg').error(why) end
    mp.commandv('quit',ok and 0 or 1)
end
mp.register_event('file-loaded',function()mp.add_timeout(1.2,check)end)
mp.add_timeout(12,function()mp.commandv('quit',1)end)

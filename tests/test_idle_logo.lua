-- Run the unchanged upstream OSC with simulated native property events and no window.
local real=require 'mp'
local msg=require 'mp.msg'
local source=assert(real.get_property('script-opts'):match('idlelogosource=([^,]+)'))
local overlays,observers,bindings,events,props={},{},{},{},{
    ['osd-dimensions']={w=1280,h=720,aspect=1280/720},['idle-active']=true,
    ['window-minimized']=false,['fullscreen']=false,['track-list']={},['display-fps']=60,
}
local fake=setmetatable({}, {__index=real})
function fake.get_property_native(name,default)
    if props[name]~=nil then return props[name] end
    return real.get_property_native(name,default)
end
function fake.observe_property(name,kind,callback)
    observers[name]=observers[name] or {};table.insert(observers[name],callback)
    local value=props[name]
    if value==nil then value=real.get_property_native(name) end
    callback(name,value)
end
function fake.set_property_native(name,value)props[name]=value;return true end
fake.set_property_number=fake.set_property_native
function fake.create_osd_overlay()
    local overlay={data='',update=function(self)self.visible=true end,remove=function(self)self.visible=false end}
    overlays[#overlays+1]=overlay
    return overlay
end
function fake.enable_key_bindings(name)bindings[name]=true end
function fake.disable_key_bindings(name)bindings[name]=false end
function fake.set_key_bindings()end
function fake.set_mouse_area()end
function fake.add_key_binding()end
function fake.register_event(name,callback)events[name]=callback end
function fake.register_script_message()end
local checks=0
local function check(value,label)checks=checks+1;assert(value,label)end
local function change(name,value)
    props[name]=value
    for _,callback in ipairs(observers[name] or {})do callback(name,value)end
end
local function later(callback)
    real.add_timeout(.1,function()
        local ok,error=xpcall(callback,debug.traceback)
        if not ok then msg.error(error);real.commandv('quit',1)end
    end)
end
package.loaded.mp=fake
local upstream=assert(loadfile(source))
setfenv(upstream,setmetatable({mp=fake},{__index=_G}))
upstream()
later(function()
    local logo
    for _,overlay in ipairs(overlays)do
        if overlay.visible and overlay.data:find('Drop files or URLs to play here.',1,true)then logo=overlay end
    end
    check(logo and logo.data:find('\\p6',1,true),'upstream vector logo appears while idle')
    check(not bindings.input and not bindings.showhide and not bindings.window_controls,'idle logo does not capture playback controls')
    check(props['user-data/osc/visibility']=='auto','original OSC remains enabled for its idle renderer')
    change('idle-active',false)
    later(function()
        for _,overlay in ipairs(overlays)do check(not overlay.visible,'original OSC overlays are removed during windowed playback')end
        change('fullscreen',true)
        later(function()
            for _,overlay in ipairs(overlays)do check(not overlay.visible,'original OSC overlays are removed during fullscreen playback')end
            change('idle-active',true)
            later(function()
                check(logo.visible and logo.data:find('Drop files or URLs to play here.',1,true),'idle logo returns after playback ends')
                msg.info('PASS original idle logo: '..checks..' assertions')
                package.loaded.mp=real
                real.commandv('quit',0)
            end)
        end)
    end)
end)

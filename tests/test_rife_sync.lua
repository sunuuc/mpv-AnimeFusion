-- Exercise native filter notifications and route changes without a physical audio receiver.
local mp = require 'mp'
local get, number, native, set = mp.get_property, mp.get_property_number, mp.get_property_native, mp.set_property
local register, observe = mp.register_event, mp.observe_property
local events, properties, info = {}, {}, {}
local host, mode, format, context, refresh = {}, 'audio', 'float', 'winvk', 60
local audio_id = 1

mp.get_property = function(name, ...)
    if name == 'video-sync' then return mode end
    if name == 'audio-out-params/format' then return format end
    if name == 'current-gpu-context' then return context end
    if name == 'current-vo' then return 'gpu-next' end
    return get(name, ...)
end
mp.get_property_number = function(name, ...)
    if name == 'container-fps' then return 24 end
    if name == 'display-fps' then return refresh end
    if name == 'current-tracks/audio/id' then return audio_id end
    return number(name, ...)
end
mp.get_property_native = function(name, ...)
    if name == 'option-info/video-sync' then return info end
    if name == 'script-opts' then return host end
    return native(name, ...)
end
mp.set_property = function(name, value)
    assert(name ~= 'video-sync', 'must never write the global sync mode')
    if name == 'file-local-options/video-sync' then
        mode, info['set-locally'] = value, true
        return true
    end
    return set(name, value)
end
mp.register_event = function(name, callback)
    events[name] = events[name] or {}
    table.insert(events[name], callback)
    return register(name, callback)
end
mp.observe_property = function(name, kind, callback)
    properties[name] = callback
    return observe(name, kind, callback)
end
dofile(assert(os.getenv('PLAYER_BACKEND_SCRIPT')))

local function event(name, data)
    for _, callback in ipairs(events[name]) do callback(data) end
end
local function configure(factor)
    if factor then event('log-message', {prefix='animejanai', text='RIFE active: 2/1 interpolation'}) end
    event('log-message', {prefix='animejanai', text='Configured slot 1: 1280x720 -> 2560x1440'})
end
configure(false); assert(mode == 'audio')
configure(true); assert(mode == 'display-resample')
format = 'spdif-ac3'; properties['audio-out-params'](); assert(mode == 'audio')
format = 'float'; properties['audio-out-params'](); assert(mode == 'display-resample')
audio_id = nil; properties.aid(); assert(mode == 'audio')
audio_id = 2; properties.aid(); assert(mode == 'display-resample')
context = 'd3d11'; properties['current-gpu-context'](); assert(mode == 'audio')
context = 'winvk'; refresh = 30; properties['display-fps'](); assert(mode == 'audio')
refresh = 60; properties['display-fps'](); assert(mode == 'display-resample')
configure(false); assert(mode == 'audio')
configure(true); assert(mode == 'display-resample')
properties['vf']('vf', {}); assert(mode == 'audio')
configure(true); assert(mode == 'display-resample')
mode = 'audio'; properties['video-sync'](); configure(false); configure(true); assert(mode == 'audio')
for _,key in ipairs({'set-from-config','set-from-commandline','set-locally','host'}) do
    event('start-file'); info, host, mode = {}, {}, 'audio'
    if key == 'host' then host['animejanai_backend-video_sync']='explicit' else info[key]=true end
    configure(true); assert(mode == 'audio', key)
end
print('rate=verified RIFE timing and explicit overrides')
mp.commandv('quit', 0)

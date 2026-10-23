local root=assert(arg[1])
local diagnostic=(os.getenv('TEMP') or os.getenv('TMPDIR') or '/tmp')..
    '/network-playback-'..tostring({}):gsub('[^%w]','')..'.json'
local clock,events,hooks,timers,messages=0,{},{},{},{}
local props={['stream-open-filename']='https://example.invalid/first',path='https://example.invalid/first',
    ['playlist-count']=2,['playlist-pos']=0,['idle-active']=false}
local fake={}
function fake.get_time()return clock end
function fake.command_native()return diagnostic end
function fake.get_property(name,default)return props[name] or default end
function fake.get_property_number(name,default)return props[name] or default end
function fake.get_property_bool(name,default)
    if props[name]==nil then return default end
    return props[name]
end
fake.get_property_native=fake.get_property_bool
function fake.set_property()return true end
function fake.set_property_native(name,value)props[name]=value;return true end
function fake.commandv()return true end
function fake.enable_messages()end
function fake.get_script_name()return 'network_playback' end
function fake.add_hook(name,_,callback)hooks[name]=callback end
function fake.register_event(name,callback)events[name]=callback end
function fake.add_timeout(delay,callback)
    local timer={due=clock+delay,callback=callback,alive=true}
    function timer:kill()self.alive=false end
    timers[#timers+1]=timer
    return timer
end
function fake.osd_message(value)messages[#messages+1]=value end
package.loaded.mp=fake
local serialized={}
package.loaded['mp.utils']={format_json=function(value)serialized[#serialized+1]=value;return '{}' end}
package.loaded['mp.msg']={error=function()end}
dofile(root..'/portable_config/scripts/network_playback.lua')
local original_open=io.open
local diagnostic_writes=0
io.open=function(path,mode)
    if mode=='wb' then diagnostic_writes=diagnostic_writes+1 end
    return original_open(path,mode)
end
local function advance(seconds)
    clock=clock+seconds
    for _,timer in ipairs(timers) do
        if timer.alive and timer.due<=clock then timer.alive=false;timer.callback() end
    end
end
hooks.on_load()
assert(#timers==0,'release must not start diagnostic sampling timers')
events['end-file']({reason='error'})
events['start-file']()
props['stream-open-filename']='https://example.invalid/second'
props.path=props['stream-open-filename']
props['playlist-pos']=1
hooks.on_load()
events['file-loaded']()
events['playback-restart']()
advance(1)
assert(#messages==0,'a failed playlist entry must not show a final playback error')
events['end-file']({reason='error'})
props['idle-active']=true
advance(1)
assert(#messages==1 and messages[1]:find('无法打开视频',1,true),'final failure must remain visible')
assert(diagnostic_writes==0,'release must not write playback diagnostic files')
props['user-data/frontend/diagnostics']=true
hooks.on_load()
assert(diagnostic_writes>0,'test build must retain playback diagnostics')
local sampler=timers[#timers]
assert(sampler.due==clock+.5 and sampler.alive,'test build records opening samples')
props['user-data/frontend/diagnostics']=false
local timer_count=#timers
advance(.5)
assert(#timers==timer_count,'disabled diagnostics must not reschedule a pending sampler')
props['user-data/frontend/diagnostics']=true
hooks.on_load()
events['log-message']({prefix='ffmpeg',level='error',text='HTTP error 502\nCookie: auth=private-cookie\nX-Emby-Token: private-token\nFailed C:\\Users\\private-user\\video.mkv: permission denied\nFailed /home/private-user/video.mkv\nGET https://private.invalid/video?token=private-query'})
events['end-file']({reason='error',error=-13,file_error='network failure'})
advance(1)
local failure=serialized[#serialized-1]
-- The final callback also persists the state, so locate the detailed error entry.
for _,value in ipairs(serialized) do
    if value.certificate_error~=nil then failure=value end
end
assert(failure.http_status==502 and failure.error==-13,'actual failure codes remain available')
local detail=failure.details[1].text
assert(not detail:find('private-',1,true) and not detail:find('private.invalid',1,true),
    'diagnostic errors must hide cookies, custom tokens, media addresses and local paths')
assert(detail:find('permission denied',1,true),'redaction must retain the failure reason')
local visible=messages[#messages]
assert(visible:find('HTTP 502',1,true) and visible:find('network failure',1,true)
    and not visible:find('private-',1,true),'error display keeps the reason without credentials')
for _,text in ipairs({'Peer certificate failed verification','certificate verify failed',
    'TLS certificate verification failed'}) do
    hooks.on_load()
    events['log-message']({prefix='ffmpeg',level='error',text=text})
    events['end-file']({reason='error',error=-13,file_error='network failure'})
    advance(1)
    assert(props['user-data/player_ui/playback'].ssl_error,'native TLS failure must retain the explicit retry choice')
end
io.open=original_open
os.remove(diagnostic)
print('PASS network playback error lifecycle')

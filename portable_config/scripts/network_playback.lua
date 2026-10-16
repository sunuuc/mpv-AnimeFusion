-- Playback policy and local diagnostics. Never starts a worker.
local mp=require 'mp'
local utils=require 'mp.utils'
local message=require 'mp.msg'
local diagnostic=mp.command_native({'expand-path','~~/playback-diagnostic.json'})
local failure_diagnostic=mp.command_native({'expand-path','~~/playback-error.json'})
local state={phase='idle',network=false,has_media=false,http_status=nil}
local history={}
local errors={}
local certificate_failure=false
local failure_generation=0
local opened_at,loaded_at,opening_sampler=nil,nil,nil
local pending_failure
local function kill_sampler()
    if opening_sampler then opening_sampler:kill();opening_sampler=nil end
end
local function cancel_failure()
    if pending_failure then pending_failure:kill();pending_failure=nil end
end
local function remote(path)
    return type(path)=='string' and path:match('^%a[%w+.-]*://') and not path:match('^[Ff][Ii][Ll][Ee]://')
end
local function safe_detail(text)
    text=tostring(text or ''):gsub('%a[%w+.-]*://[^%s"<>]+','<播放地址>')
    text=text:gsub('[Aa]uthorization:[^\r\n]+','Authorization: <已隐藏>')
    text=text:gsub('[Bb]earer%s+[%w._~+/-]+=*','Bearer <已隐藏>')
    return text:gsub('[\r\n]+$','')
end
local function persist()
    mp.set_property_native('user-data/player_ui/playback',state)
    local json=utils.format_json({version=2,events=history})
    local f=io.open(diagnostic,'wb');if f then f:write(json);f:close() end
end
local function sample_opening()
    opening_sampler=nil
    if state.phase~='opening' or not state.network or not opened_at then return end
    local samples=state.opening_samples or {}
    samples[#samples+1]={elapsed_ms=math.floor((mp.get_time()-opened_at)*1000+.5),
        cache_speed_bps=mp.get_property_number('cache-speed',0),
        cache_idle=mp.get_property_bool('demuxer-cache-idle',false)}
    while #samples>80 do table.remove(samples,1) end
    state.opening_samples=samples
    for i=#history,1,-1 do
        if history[i].phase=='opening' then history[i].opening_samples=samples;break end
    end
    persist()
    opening_sampler=mp.add_timeout(.5,sample_opening)
end
local function record(phase)
    local now=mp.get_time()
    if phase=='opening' then
        kill_sampler();opened_at=now;loaded_at=nil;state.opening_samples={}
        state.open_to_loaded_ms=nil;state.loaded_to_playing_ms=nil;state.open_to_playing_ms=nil
    elseif phase=='loaded' and opened_at then
        state.open_to_loaded_ms=math.floor((now-opened_at)*1000+.5);loaded_at=now
        kill_sampler()
    elseif phase=='playing' and opened_at then
        state.open_to_playing_ms=math.floor((now-opened_at)*1000+.5)
        if loaded_at then state.loaded_to_playing_ms=math.floor((now-loaded_at)*1000+.5) end
        kill_sampler()
    end
    state.phase=phase
    state.has_media=mp.get_property('path','')~=''
    state.playlist_count=mp.get_property_number('playlist-count',0)
    state.position=mp.get_property_number('playlist-pos',-1)
    state.network_thumbnails=false;state.playlist_prefetch=false
    history[#history+1]={time=os.date('!%Y-%m-%dT%H:%M:%SZ'),phase=phase,network=state.network,
        has_media=state.has_media,playlist_count=state.playlist_count,position=state.position,http_status=state.http_status,
        file_error=state.file_error,error=state.error,details=phase=='failed' and errors or nil,
        elapsed_ms=opened_at and math.floor((now-opened_at)*1000+.5) or nil,
        open_to_loaded_ms=state.open_to_loaded_ms,loaded_to_playing_ms=state.loaded_to_playing_ms}
    while #history>24 do table.remove(history,1) end
    persist()
    if phase=='opening' and state.network then opening_sampler=mp.add_timeout(.5,sample_opening) end
end
local function set(name,value)
    local ok,err=mp.set_property(name,value)
    if not ok then message.error('Playback policy option '..name..': '..tostring(err)) end
end
set('prefetch-playlist','no')
mp.register_event('start-file',cancel_failure)
mp.add_hook('on_load',-1000,function()
    cancel_failure()
    state.network=not not remote(mp.get_property('stream-open-filename',mp.get_property('path','')))
    state.http_status=nil;state.file_error=nil;state.error=nil;errors={}
    certificate_failure=false;state.ssl_error=false;state.retry_entry_id=nil
    set('prefetch-playlist','no')
    if state.network then
        for name,value in pairs({['sub-auto']='no',['audio-file-auto']='no',['cover-art-auto']='no',
            ['ytdl']='no',['demuxer-cache-wait']='no',['cache-pause-wait']='1',
            -- Do not hold the first frame for the configured startup cache. Keep
            -- cache-pause enabled so playback can still recover from later stalls.
            ['cache-pause-initial']='no',
            -- Read three minutes ahead and keep the cache in memory, but size it
            -- from the bitrate rather than from a round number: three minutes of
            -- 1080p is 110-260 MiB, and even 4K stays under 550 MiB. 768 MiB is
            -- therefore about 1.5x the worst case, while the 2 GiB this used to
            -- ask for pushed committed memory past 3.9 GB because mpv counts the
            -- packet budget against the process commit even when the packets are
            -- never all resident. The backward cache has a separate 384 MiB
            -- budget and may borrow unused forward space. The combined packet
            -- cache budget is therefore 1152 MiB, not 768 MiB.
            ['demuxer-readahead-secs']='180',['cache-secs']='180',
            ['demuxer-cache-unselected-subs']='yes',
            ['demuxer-max-bytes']='768M',['demuxer-max-back-bytes']='384M',
            -- The video/audio decoder queues default to 512M/1M and are counted
            -- in the same committed pool as the packet cache. 256M is still far
            -- more than the decoder needs to stay ahead of playback at 1080p
            -- (the AnimeJaNai filter chain is the real bottleneck, not the
            -- decoders) and removes half a gigabyte of headroom the player was
            -- reserving and never using.
            ['vd-queue-max-bytes']='256M',['ad-queue-max-bytes']='512K',
            ['network-timeout']='20'}) do
            set('file-local-options/'..name,value)
        end
        mp.commandv('change-list','demuxer-lavf-o','append','http_multiple=0')
    end
    record('opening')
end)
mp.enable_messages('warn')
mp.register_event('log-message',function(e)
    if state.phase~='opening' and state.phase~='loaded' and state.phase~='playing' then return end
    if e.prefix==mp.get_script_name() then return end
    local prefix=(e.prefix or ''):match('^(%w+)')
    local detail=(e.text or ''):lower()
    if (prefix=='curl' or prefix=='ffmpeg' or prefix=='stream') and
        (detail:find('certificate verification failed',1,true) or
         detail:find('certificate verify failed',1,true) or
         detail:find('ssl peer certificate',1,true)) then certificate_failure=true end
    local code=e.text and (e.text:match('HTTP error (%d%d%d)') or e.text:match('HTTP/%d[%d.]* (%d%d%d)'))
    if code then state.http_status=tonumber(code) end
    if e.level=='error' or e.level=='fatal' or code or
        (e.level=='warn' and (prefix=='ffmpeg' or prefix=='lavf' or prefix=='stream')) then
        local text=safe_detail(e.text)
        if text~='' then
            errors[#errors+1]={prefix=e.prefix,level=e.level,text=text}
        end
    end
end)
mp.register_event('file-loaded',function()cancel_failure();record('loaded')end)
mp.register_event('playback-restart',function()
    if state.phase~='playing' then record('playing') end
end)
mp.register_event('end-file',function(e)
    if e.reason=='error' then
        failure_generation=failure_generation+1
        state.failure_id=failure_generation;state.retry_entry_id=e.playlist_entry_id
        state.file_error=e.file_error and safe_detail(e.file_error) or nil
        state.error=e.error
        record('failed')
        local failure={time=os.date('!%Y-%m-%dT%H:%M:%SZ'),file_error=state.file_error,
            error=state.error,http_status=state.http_status,certificate_error=certificate_failure,details=errors}
        local f=io.open(failure_diagnostic,'wb')
        if f then f:write(utils.format_json(failure));f:close() end
        local code=state.http_status
        local lines={'无法打开视频'}
        if code then lines[#lines+1]='HTTP '..code end
        if state.error then lines[#lines+1]='错误码：'..tostring(state.error) end
        if state.file_error then lines[#lines+1]='mpv 错误：'..state.file_error end
        for _,detail in ipairs(errors) do
            lines[#lines+1]='['..(detail.prefix or 'mpv')..'] '..detail.text
        end
        local text=table.concat(lines,'\n')
        -- mpv may advance to another playlist entry after this failure. Show an
        -- error only if no next entry starts; a failed attempt is not a failed
        -- playback session.
        cancel_failure()
        pending_failure=mp.add_timeout(.75,function()
            pending_failure=nil
            if state.phase=='failed' and mp.get_property_bool('idle-active',false) then
                mp.osd_message(text,8)
                state.ssl_error=certificate_failure
                persist()
            end
        end)
    elseif e.reason=='eof' then record('ended')
    elseif e.reason=='stop' then record('stopped') end
end)
mp.register_event('shutdown',function()kill_sampler();cancel_failure()end)
record('idle')

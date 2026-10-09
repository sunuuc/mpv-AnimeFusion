-- DanmakuFactory performs layout once. mpv's native secondary ASS decoder owns
-- animation, seeking and presentation; this module has no frame update loop.
return function(mp, utils, on_change)
    local M={ready=false,busy=false,count=0,track=nil,error=nil}
    local defaults={resolution={1920,1080},fps=60,displayArea=1,scrollArea=1,
        scrolltime=12,fixtime=5,density=0,lineSpacing=0,topMargin=0,bottomMargin=0,
        fontsize=38,fontname='Microsoft YaHei',opacity=180,outline=0,shadow=1,bold=false,
        outlineBlur=0,outlineOpacity=255,saveBlocked=true,showUsernames=false,showMsgbox=true,
        msgboxSize={500,1080},msgboxPos={20,0},msgboxFontsize=38,msgboxDuration=0,giftMinPrice=0,
        blockmode=utils.parse_json('[]'),statmode=utils.parse_json('[]'),
        fontSizeStrict=false,fontSizeNorm=false,blacklist='',blacklistRegex=false}
    local root=os.getenv('LOCALAPPDATA') or os.getenv('APPDATA') or os.getenv('TEMP')
    M.config_path=root and (root..'/mpv-AnimeFusion-DanmakuFactory.json') or nil
    local executable=mp.command_native({'expand-path','~~/../animejanai/danmaku/DanmakuFactory.exe'})
    local job,serial,ass_path,input_path,owned_input= nil,0,nil,nil,false
    local previous_secondary,previous_style,previous_visibility,previous_display_sync
    local previous_render_fps
    local playback_speed=mp.get_property_number('speed',1)
    local timeline={{t=0,rate=playback_speed}}
    local regen_timer
    local function playback_context(args)
        local time=mp.get_property_number('time-pos',0)
        for _,item in ipairs({{'playback-speed',playback_speed},{'playback-time',time}}) do
            args[#args+1]='--'..item[1];args[#args+1]=tostring(item[2])
        end
        local rates={}
        for _,item in ipairs(timeline) do rates[#rates+1]=string.format('%.6f:%.6f',item.t,item.rate) end
        args[#args+1]='--playback-timeline';args[#args+1]=table.concat(rates,';')

    end
    local function read(path,limit)
        local f=path and io.open(path,'rb');if not f then return end
        local text=f:read(limit+1);f:close()
        if text and #text<=limit then return text end
    end
    local function write(path,text)
        local f,err=io.open(path,'wb');if not f then return nil,err end
        local ok,why=f:write(text);local closed,close_error=f:close()
        return ok and closed,why or close_error
    end
    local function config()
        local values={}
        for key,value in pairs(defaults) do values[key]=value end
        local text=read(M.config_path,65536)
        local saved=text and utils.parse_json(text)
        if type(saved)=='table' then
            for key,value in pairs(saved) do values[key]=value end
        end
        return values
    end
    M.settings=config()
    local function detach()
        if M.track then
            if tostring(mp.get_property_native('secondary-sid','no'))==tostring(M.track) then
                mp.set_property_native('secondary-sid',previous_secondary or 'no')
                mp.set_property('secondary-sub-ass-override',previous_style or 'strip')
                mp.set_property_bool('secondary-sub-visibility',previous_visibility~=false)
                mp.set_property_bool('secondary-sub-display-sync',previous_display_sync or false)
                mp.set_property_number('secondary-sub-render-fps',previous_render_fps)
            end
            mp.commandv('sub-remove',M.track)
            M.track=nil
        end
        if ass_path then os.remove(ass_path);ass_path=nil end
        M.ready=false
    end
    local function select_track()
        if not ass_path or M.track then return end
        local path=ass_path:gsub('\\','/')
        for _,track in ipairs(mp.get_property_native('track-list',{}) or {}) do
            local external=tostring(track['external-filename'] or ''):gsub('\\','/')
            if track.type=='sub' and external==path then
                M.track=track.id
                previous_secondary=mp.get_property_native('secondary-sid','no')
                previous_style=mp.get_property('secondary-sub-ass-override','strip')
                previous_visibility=mp.get_property_bool('secondary-sub-visibility',true)
                previous_display_sync=mp.get_property_bool('secondary-sub-display-sync',false)
                previous_render_fps=mp.get_property_number('secondary-sub-render-fps',60)
                mp.set_property('secondary-sub-ass-override','no')
                mp.set_property_bool('secondary-sub-visibility',M.enabled~=false)
                mp.set_property_number('secondary-sub-render-fps',M.settings.fps)
                mp.set_property_bool('secondary-sub-display-sync',true)
                mp.set_property_native('secondary-sid',track.id)
                on_change()
                break
            end
        end
    end
    local function attach()
        if not ass_path or M.track or mp.get_property_bool('idle-active',true) then return end
        mp.commandv('sub-add',ass_path,'auto','弹幕 · DanmakuFactory','danmaku')
        select_track()
    end
    function M.cancel()
        serial=serial+1
        if regen_timer then regen_timer:kill();regen_timer=nil end
        if job then mp.abort_async_command(job);job=nil end
        M.busy=false
    end
    function M.clear()
        M.cancel();detach()
        if owned_input and input_path then os.remove(input_path) end
        input_path=nil;owned_input=false;M.count=0;M.error=nil
    end
    function M.convert(path,owned,preserve)
        M.cancel()
        if not preserve then detach() end
        if owned_input and input_path and input_path~=path then os.remove(input_path) end
        input_path,owned_input=path,owned or false
        M.busy=true;M.error=nil;if not preserve then M.count=0 end;M.settings=config();on_change()
        local expected=serial
        local temp=os.getenv('TEMP') or root
        if not temp then M.busy=false;M.error='本机临时目录不可用';on_change();return end
        local output=temp..'/mpv-AnimeFusion-danmaku-'..tostring(utils.getpid())..'-'..serial..'.ass'
        os.remove(output)
        local args={executable,'--ignore-warnings','--force','-o',output,'-i',path}
        -- Pass settings through the upstream CLI, which also supports word blocking.
        -- The saved JSON belongs to this module and is never parsed by the converter.
        for _,option in ipairs({'displayArea','scrollArea','scrolltime','fixtime','fontsize','fontname',
            'opacity','density','outline','shadow','bold'}) do
            args[#args+1]='--'..option:lower();args[#args+1]=tostring(M.settings[option])
        end
        for key,option in pairs({lineSpacing='--line-spacing',outlineBlur='--outline-blur',
            outlineOpacity='--outline-opacity',blacklistRegex='--blacklist-regex'}) do
            args[#args+1]=option;args[#args+1]=tostring(M.settings[key])
        end
        args[#args+1]='--resolution';args[#args+1]=table.concat(M.settings.resolution,'x')
        args[#args+1]='--blockmode';args[#args+1]=#M.settings.blockmode>0 and table.concat(M.settings.blockmode,'-') or 'null'
        if M.settings.fontSizeStrict then args[#args+1]='--font-size-strict' end
        if M.settings.fontSizeNorm then args[#args+1]='--font-size-norm' end
        if M.settings.blacklist~='' then args[#args+1]='--blacklist';args[#args+1]=M.settings.blacklist end
        playback_context(args)
        job=mp.command_native_async({name='subprocess',args=args,playback_only=false,
            capture_stdout=true,capture_stderr=true},function(ok,result)
            if expected~=serial then os.remove(output);return end
            job=nil;M.busy=false
            local text=ok and result and result.status==0 and read(output,32*1024*1024)
            if not text or not text:find('[Script Info]',1,true) then
                os.remove(output);M.error='弹幕转换失败'
                if mp.msg then mp.msg.error('DanmakuFactory: '..tostring(result and (result.stderr~='' and result.stderr or result.stdout) or '无法运行')) end
                on_change();return
            end
            detach();M.count=0
            for _ in text:gmatch('\nDialogue:') do M.count=M.count+1 end
            ass_path=output;M.ready=true;on_change();attach()
        end)
    end
    local function xml_escape(text)
        return tostring(text):gsub('&','&amp;'):gsub('<','&lt;'):gsub('>','&gt;'):gsub('"','&quot;'):gsub("'",'&apos;')
    end
    function M.comments(list)
        local temp=os.getenv('TEMP') or root
        if not temp then M.error='本机临时目录不可用';on_change();return end
        local path=temp..'/mpv-AnimeFusion-comments-'..tostring(utils.getpid())..'-'..(serial+1)..'.xml'
        local lines={'<?xml version="1.0" encoding="UTF-8"?><i>'}
        for _,item in ipairs(list) do
            lines[#lines+1]=string.format('<d p="%.3f,%d,25,%d,0,0,0,0">%s</d>',
                item.t,item.mode,item.color,xml_escape(item.text))
        end
        lines[#lines+1]='</i>'
        local ok,why=write(path,table.concat(lines,'\n'))
        if not ok then M.error='无法保存弹幕数据：'..tostring(why);on_change();return end
        M.convert(path,true)
    end
    local function ass_time(value)
        local cs=math.max(0,math.floor(value*100+.5))
        return string.format('%d:%02d:%02d.%02d',math.floor(cs/360000),
            math.floor(cs/6000)%60,math.floor(cs/100)%60,cs%100)
    end
    function M.notice(text)
        if M.enabled==false or M.track then return end
        local time=mp.get_property_number('time-pos',0)+.1
        local body=ass_path and read(ass_path,32*1024*1024)
        if not body then
            -- A pending HTTP request or conversion has no ASS document yet.
            -- Use the converter's R2L style in the existing secondary decoder;
            -- this one event has no layout pool or Lua animation loop.
            local style=string.format('Style: R2L,%s,%d,&H%02XFFFFFF,&H00FFFFFF,&H00000000,&H00000000,%d,0,0,0,100,100,0,0,1,%g,%g,8,0,0,0,1',
                M.settings.fontname:gsub('[,\r\n]',''),M.settings.fontsize,255-M.settings.opacity,
                M.settings.bold and -1 or 0,M.settings.outline,M.settings.shadow)
            body=table.concat({'[Script Info]','ScriptType: v4.00+',
                'PlayResX: '..M.settings.resolution[1],'PlayResY: '..M.settings.resolution[2],
                'WrapStyle: 2','ScaledBorderAndShadow: yes','[V4+ Styles]',
                'Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding',
                style,'[Events]','Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text'},'\n')
        end
        local width=M.settings.resolution[1]
        local height=M.settings.resolution[2]
        local size=M.settings.fontsize
        local chars=0
        for _ in text:gmatch('[%z\1-\127\194-\244][\128-\191]*') do chars=chars+1 end
        local half=math.ceil(chars*size/2)
        -- Keep the notice just below the configured comment area when possible.
        local y=math.min(height-size,math.floor(height*M.settings.displayArea))
        local escaped=text:gsub('\\','\\\\'):gsub('{','\\{'):gsub('}','\\}')
            :gsub('[\r\n]+',' ')
        local line=string.format('\nDialogue: 0,%s,%s,R2L,,0000,0000,0000,,{\\move(%d,%d,%d,%d)}%s\n',
            ass_time(time),ass_time(time+M.settings.scrolltime*playback_speed),
            width+half,y,-half,y,escaped)
        -- Add the event before attach opens the completed ASS file. mpv owns
        -- motion; the notice never needs an overlay, reload or animation timer.
        local path=ass_path or (os.getenv('TEMP') or root)..'/mpv-AnimeFusion-notice-'
            ..tostring(utils.getpid())..'-'..serial..'.ass'
        if write(path,body..line) then
            ass_path=path
            if not M.ready then attach() end
        end
    end
    function M.set(key,value)
        if not M.config_path then return end
        local values=config()
        if key=='speed' then
            value=tonumber(value)
            if not value or value<.5 or value>3 then return end
            values.scrolltime=12/value;values.fixtime=5/value
        elseif key=='area' then
            value=tonumber(value)
            if not value or value<10 or value>100 then return end
            values.displayArea=value/100;values.scrollArea=1
        elseif key=='opacity-percent' then
            value=tonumber(value)
            if not value or value<1 or value>100 then return end
            values.opacity=math.floor(value*255/100+.5)
        elseif key=='fps' then
            value=tonumber(value)
            if value~=30 and value~=60 and value~=90 then return end
            values.fps=value
        elseif defaults[key]~=nil then
            values[key]=value
            if key=='blacklist' then values.blacklistRegex=false end
        else return end
        local ok,why=write(M.config_path,utils.format_json(values))
        if not ok then M.error='无法保存弹幕设置：'..tostring(why);on_change();return end
        M.settings=values
        if key=='fps' then
            if M.track then mp.set_property_number('secondary-sub-render-fps',value) end
            on_change();return
        end
        if input_path then M.convert(input_path,owned_input) else on_change() end
    end
    function M.words()
        return read(M.settings.blacklist,65536) or ''
    end
    function M.set_words(text)
        if not root or type(text)~='string' or #text>65536 then return end
        local words={}
        for line in text:gmatch('[^\r\n]+') do
            line=line:match('^%s*(.-)%s*$')
            if line~='' then
                if #line>4094 or #words>=4096 then
                    M.error='屏蔽词过长或过多';on_change();return
                end
                words[#words+1]=line
            end
        end
        local path=root..'/mpv-AnimeFusion-danmaku-blocklist.txt'
        local ok,why=write(path,table.concat(words,'\n'))
        if not ok then M.error='无法保存屏蔽词：'..tostring(why);on_change();return end
        M.set('blacklist',#words>0 and path or '')
    end
    function M.reset()
        if M.config_path then os.remove(M.config_path) end
        M.settings=config()
        if input_path then M.convert(input_path,owned_input) else on_change() end
    end
    function M.reload()
        M.settings=config()
        if input_path then M.convert(input_path,owned_input) else on_change() end
    end
    function M.toggle(enabled)
        M.enabled=enabled
        if M.track then mp.set_property_bool('secondary-sub-visibility',enabled) end
        on_change()
    end
    local function regenerate()
        if not input_path then return end
        if regen_timer then regen_timer:kill() end
        regen_timer=mp.add_timeout(.12,function()
            regen_timer=nil
            if input_path then M.convert(input_path,owned_input,true) end
        end)
    end
    mp.observe_property('speed','number',function(_,value)
        value=tonumber(value) or 1
        if value==playback_speed then return end
        playback_speed=value
        local t=mp.get_property_number('time-pos',0)
        while #timeline>1 and timeline[#timeline].t>=t do table.remove(timeline) end
        timeline[#timeline+1]={t=t,rate=value}
        regenerate()
    end)
    mp.register_event('start-file',function()
        playback_speed=mp.get_property_number('speed',1)
        timeline={{t=0,rate=playback_speed}}
    end)
    mp.register_event('seek',function()
        timeline={{t=0,rate=playback_speed}}
        regenerate()
    end)
    mp.observe_property('track-list','native',select_track)
    mp.register_event('file-loaded',attach)
    return M
end

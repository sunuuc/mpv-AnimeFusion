-- Player UI-style controller, rendered with vector icons and system fonts.
local mp=require 'mp'
local utils=require 'mp.utils'
local options=require 'mp.options'
local core=(function()
-- inlined module: player_ui_core.lua
-- Pure functions shared by the controller and its regression tests. No mpv side effects.
local M = {}
function M.clamp(x, a, b) return math.max(a, math.min(b, x)) end
function M.finite(x) return type(x)=='number' and x==x and x~=math.huge and x~=-math.huge end
function M.clean(s)
    return tostring(s or ''):gsub('[%z\1-\8\11\12\14-\31\127]', '')
end
function M.escape(s)
    return M.clean(s):gsub('\\', '\\\239\187\191'):gsub('{','\\{'):gsub('}', '\\}'):gsub('\r?\n','\\N')
end
-- UTF-8 codepoints without depending on Lua 5.3's utf8 module (mpv uses LuaJIT).
local UTF8_CODEPOINT='[%z\1-\127\194-\244][\128-\191]*'
local function each_codepoint(value) return M.clean(value):gmatch(UTF8_CODEPOINT) end
function M.chars(s)
    local t={}; for c in each_codepoint(s) do t[#t+1]=c end; return t
end
function M.char_count(s)
    local count=0
    for _ in each_codepoint(s) do count=count+1 end
    return count
end
function M.limit_chars(s,limit)
    local value=M.clean(s);local count=0
    for _ in each_codepoint(value) do
        count=count+1
        if count>limit then
            local out,n={},0
            for c in each_codepoint(value) do
                n=n+1;if n>limit then break end;out[#out+1]=c
            end
            return table.concat(out)
        end
    end
    return value
end
function M.ellipsize(s, max, size)
    local out,used={},0
    local chars=M.chars(s)
    for i,c in ipairs(chars) do
        local width=(#c>1 and 1 or (c:match('[ilI.,! :;|]') and .3 or .6))*size
        if used+width>max-size then return table.concat(out)..'…' end
        out[#out+1]=c;used=used+width
    end
    return table.concat(out)
end
function M.ass_text_width(value,size)
    local width=0
    for _,c in ipairs(M.chars(value)) do
        if #c>1 then width=width+size
        elseif c:match('%s') then width=width+size*.32
        elseif c=='%' then width=width+size*.8
        elseif c:match('[ilI.,!;:|]') then width=width+size*.3
        else width=width+size*.6 end
    end
    return width
end
function M.volume_osd_layout(pw,ph,scale,label,font_size)
    pw,ph=math.max(1,pw),math.max(1,ph)
    scale=M.clamp(tonumber(scale) or 1,.45,2)
    font_size=math.max(1,tonumber(font_size) or 16)
    local text_width=M.ass_text_width(label,font_size*scale)
    local padding_x=12*scale
    local height=32*scale
    local width=math.min(math.max(1,pw-16*scale),math.max(56*scale,text_width+padding_x*2))
    local x=(pw-width)/2
    local center_y=ph*.70
    local y=center_y-height/2
    return {x=x,y=y,width=width,height=height,center_x=x+width/2,center_y=y+height/2,
        text_width=text_width,scale=scale}
end
function M.time(v)
    if not M.finite(v) then return '--:--' end
    v=math.max(0,math.floor(v));local h=math.floor(v/3600)
    return h>0 and string.format('%d:%02d:%02d',h,math.floor(v/60)%60,v%60)
        or string.format('%02d:%02d',math.floor(v/60),v%60)
end
function M.rate(v)
    if not M.finite(v) or v<0 then return '-- KB/s' end
    if v>=1000000 then return string.format('%.2f MB/s',v/1000000) end
    return string.format('%.1f KB/s',v/1000)
end
function M.theme(accent)
    accent=accent or '9F74D0'
    return {
        accent=accent,text='F7F3F6',secondary='CEC7CF',muted='A49CA5',
        panel='211F23',surface='2B292E',hover='39353D',selected='493743',
        border='141216',divider='4A454C',track='807982',buffer='C8C1C9',
        scrim='000000',current='BBC539',
    }
end
M.metrics={
    menu_margin=16,menu_header=56,menu_padding=10,
    row_min=52,row_text=18,row_line=22,row_detail=14,row_detail_line=19,
    row_padding=16,icon_column=56,separator=16,slider=84,
    control_compact_step=48,control_step=56,control_compact_edge=28,control_edge=42,
    control_y=40,control_width=40,control_hit_height=20,speed_width=58,volume_width=130,volume_offset=8,
    volume_min_width=36,seek_x=26,seek_track_y=88,seek_top=102,seek_bottom=74,
    time_font=14,title_font=26,detail_font=16,
    network_width=104,network_gap=8,
    menu_speed_bottom=94,menu_bottom=106,
    icon_size=68,small_icon_size=40,icon_hover_radius=14,
}
function M.title(title,path)
    title=M.clean(title):gsub('[\r\n]+',' ')
    if title=='' then
        title=M.clean(path)
        if not title:match('^%a[%w+.-]*://') then title=title:gsub('^.*[/\\]','') end
    end
    if title:match('^%a[%w+.-]*://') then
        local tail=title:match('^%a[%w+.-]*://[^/]+/(.*)$') or ''
        title=tail:gsub('[?#].*$', ''):match('([^/]+)/*$') or ''
        title=title:gsub('%%(%x%x)',function(h) return string.char(tonumber(h,16)) end)
    end
    if title=='' then title='视频播放' end
    return title
end
function M.title_lines(title,path)
    title=M.title(title,path)
    local first,last,season,episode=title:find('%f[%a][Ss](%d+)[ ._:%-]*[Ee](%d+%.?%d*)')
    if not first then return title,'' end
    local name=title:sub(1,first-1):gsub('%s*%(%d%d%d%d%)%s*$',''):gsub('%s+$','')
    if name=='' then return title,'' end
    local detail=string.format('S%d:E%s',tonumber(season),tostring(tonumber(episode)))
    local episode_title=title:sub(last+1):gsub('^[%s._%-:]+',''):gsub('%s+$','')
    if episode_title~='' then detail=detail..' - '..episode_title end
    return name,detail
end
-- Player UI layout 1.3.0: the ASS PlayRes IS the window, so glyphs are rasterised at
-- native size and never resampled -- resampling was what made the text soft.
-- ui_scale*dpi scales glyph and icon sizes, and shrinks further when the window
-- is too narrow for the control row.
function M.layout(pw,ph,dpi,ui_scale,time_width)
    pw,ph=math.max(1,pw),math.max(1,ph)
    local want=M.clamp(tonumber(ui_scale) or 1.00,.45,1.5)*M.clamp(tonumber(dpi) or 1,.75,1.5)
    local w,h=pw,ph
    local built
    for _=1,12 do
        local compact=w<1600*want
        local step=(compact and M.metrics.control_compact_step or M.metrics.control_step)*want
        local edge=(compact and M.metrics.control_compact_edge or M.metrics.control_edge)*want
        local y=h-M.metrics.control_y*want
        local controls={}
        local function button(id,x,bw)
            bw=(bw or M.metrics.control_width)*want
            controls[#controls+1]={id=id,x=x,y=y,x0=x-bw/2,x1=x+bw/2,
                y0=y-M.metrics.control_hit_height*want,y1=y+M.metrics.control_hit_height*want}
        end
        button('previous',edge);button('play',edge+step);button('next',edge+2*step);button('volume',edge+3*step)
        local right={'fullscreen','settings','danmaku','sub','audio','speed','bangumi'}
        local x=w-edge
        for _,id in ipairs(right) do button(id,x);x=x-step end
        local volume_button=edge+3*step
        local vx=volume_button+M.metrics.control_width*want/2+M.metrics.volume_offset*want
        local network_width=M.metrics.network_width*want
        local network_gap=M.metrics.network_gap*want
        local volume={x0=vx,x1=vx+M.metrics.volume_width*want,
            y0=y-15*want,y1=y+15*want,y=y}
        local network_x0=volume.x1+network_gap
        local network_x1=network_x0+network_width
        local leftmost_right=w-edge-6*step
        local network={x0=network_x0,x1=network_x1,y0=y-15*want,y1=y+15*want,y=y}
        local ok=network.x0>=0 and network.x1<=leftmost_right-M.metrics.control_width*want/2-12*want
        for i,b in ipairs(controls) do
            if b.x0<0 or b.x1>w or b.y0<0 or b.y1>h then ok=false;break end
            for j=i+1,#controls do
                local c=controls[j]
                if not (b.x1<=c.x0 or c.x1<=b.x0) then ok=false;break end
            end
            if not ok then break end
        end
        if ok then
            built={w=w,h=h,scale=1,ui=want,controls=controls,volume=volume,network_rate=network,
                seek={x0=(M.metrics.seek_x+(time_width or 62))*want,x1=w-(M.metrics.seek_x+(time_width or 62))*want,
                    y0=h-M.metrics.seek_top*want,y1=h-M.metrics.seek_bottom*want,y=h-M.metrics.seek_track_y*want},
                title_y=math.max(18*want,h-176*want),detail_y=math.max(48*want,h-140*want),
                margin=26*want,compact=compact,small=false}
            break
        end
        want=want*0.86
    end
    return built or {w=w,h=h,scale=1,ui=want,controls={},volume=nil,
        seek={x0=0,x1=w,y0=h-90,y1=h-70,y=h-80},title_y=h-140,detail_y=h-104,
        margin=18,compact=true,small=false}
end
function M.wrap(value,width,size,maxlines)
    local chars=M.chars(value);local out,line,used={},'',0
    maxlines=maxlines or 2
    for i,c in ipairs(chars) do
        local cw=(#c>1 and 1 or (c:match('[ilI.,! :;|]') and .3 or .6))*size
        if used+cw>width and line~='' then
            if #out==maxlines-1 then out[#out+1]=M.ellipsize(line..table.concat(chars,'',i),width,size);return out end
            out[#out+1]=line;line='';used=0
        end
        line=line..c;used=used+cw
    end
    if line~='' or #out==0 then out[#out+1]=line end
    return out
end
function M.inside(b,x,y) return b and x>=b.x0 and x<=b.x1 and y>=b.y0 and y<=b.y1 end
function M.parse_presets(text)
    local out,section={},''
    for line in tostring(text):gmatch('[^\r\n]+') do
        local s=line:match('^%s*%[([^%]]+)%]'); if s then section=s end
        local id=section:match('^slot_(%d+)$')
        local name=line:match('^%s*profile_name%s*=%s*(.-)%s*$')
        if id and name and tonumber(id)>=1 and tonumber(id)<=9 then out[tonumber(id)]=name end
    end
    return out
end
function M.fps_sampler()
    local self={samples={}}
    function self:reset() self.samples={} end
    function self:sample(now,n,paused)
        if not M.finite(n) or n<0 then self:reset();return nil end
        if paused then self:reset();return 0 end
        local s=self.samples;local last=s[#s]
        if last and (n<last.n or now<=last.t or now-last.t>4) then self:reset();s=self.samples end
        s[#s+1]={t=now,n=n}
        while #s>2 and s[2].t<=now-2 do table.remove(s,1) end
        if now-s[1].t<.75 then return nil end
        return (n-s[1].n)/(now-s[1].t)
    end
    return self
end
function M.local_media(path,opened,network)
    if network or type(path)~='string' or path=='' then return false end
    local function file_path(s)
        if not s or s=='' then return true end
        if s:match('^%a:[/\\]') then return true end
        if s:match('^%a[%w+.-]*:') or s:sub(1,2)=='//' or s:sub(1,2)=='\\\\' then return false end
        return true
    end
    return file_path(path) and file_path(opened)
end
return M
end)()
local metrics=(function()
-- inlined module: player_ui_metrics.lua
-- Native, in-process Windows measurements. Called only while the performance panel is open.
-- No PowerShell/WMI/nvidia-smi loop and no background monitoring process.
local M={}
local ok,ffi=pcall(require,'ffi')
if not ok or ffi.os~='Windows' then return M end
local declared=pcall(ffi.cdef,[[
typedef struct { unsigned long lo, hi; } PLAYER_UI_FILETIME;
typedef struct { unsigned long cb, PageFaultCount; size_t PeakWorkingSetSize, WorkingSetSize,
 QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage,
 QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage; } PLAYER_UI_PMC;
void* __stdcall GetCurrentProcess(void);
int __stdcall GetProcessTimes(void*, PLAYER_UI_FILETIME*, PLAYER_UI_FILETIME*, PLAYER_UI_FILETIME*, PLAYER_UI_FILETIME*);
unsigned long __stdcall GetActiveProcessorCount(unsigned short);
int __stdcall K32GetProcessMemoryInfo(void*, PLAYER_UI_PMC*, unsigned long);
int __stdcall lstrlenW(const unsigned short*);
typedef struct { unsigned long status; union { long integer; double value; long long large; }; } PLAYER_UI_PDH_VALUE;
typedef struct { unsigned short* name; PLAYER_UI_PDH_VALUE formatted; } PLAYER_UI_PDH_ITEM;
long __stdcall PdhOpenQueryW(const unsigned short*, size_t, void**);
long __stdcall PdhAddEnglishCounterW(void*, const unsigned short*, size_t, void**);
long __stdcall PdhCollectQueryData(void*);
long __stdcall PdhGetFormattedCounterArrayW(void*, unsigned long, unsigned long*, unsigned long*, PLAYER_UI_PDH_ITEM*);
long __stdcall PdhCloseQuery(void*);
]])
if not declared then return M end
local k=ffi.load('kernel32')
local last_time,last_cpu
local pdh_ok,pdh=pcall(ffi.load,'pdh')
local query,counter,gpu_time,gpu_value,gpu_tried
local function gpu_read(now)
    if not pdh_ok then return nil end
    if gpu_time and now-gpu_time<1 then return gpu_value end
    gpu_time=now
    if not gpu_tried then
        gpu_tried=true
        local q,h=ffi.new('void*[1]'),ffi.new('void*[1]')
        local path='\\GPU Engine(*)\\Utilization Percentage'
        local wide=ffi.new('unsigned short[?]',#path+1)
        for i=1,#path do wide[i-1]=path:byte(i) end
        if pdh.PdhOpenQueryW(nil,0,q)~=0 then return nil end
        if pdh.PdhAddEnglishCounterW(q[0],wide,0,h)~=0 then pdh.PdhCloseQuery(q[0]);return nil end
        query,counter=q[0],h[0]
        pdh.PdhCollectQueryData(query)
        return nil -- GPU utilization is a delta and needs two samples.
    end
    if not query or pdh.PdhCollectQueryData(query)~=0 then gpu_value=nil;return nil end
    local size,count=ffi.new('unsigned long[1]'),ffi.new('unsigned long[1]')
    pdh.PdhGetFormattedCounterArrayW(counter,0x200,size,count,nil)
    if size[0]==0 or size[0]>4194304 then gpu_value=nil;return nil end
    local buffer=ffi.new('uint8_t[?]',tonumber(size[0]))
    local rows=ffi.cast('PLAYER_UI_PDH_ITEM*',buffer)
    if pdh.PdhGetFormattedCounterArrayW(counter,0x200,size,count,rows)~=0 then gpu_value=nil;return nil end
    local engines={}
    for i=0,tonumber(count[0])-1 do
        local r=rows[i]
        if (r.formatted.status==0 or r.formatted.status==1) and r.name~=nil then
            local name=ffi.string(ffi.cast('char*',r.name),k.lstrlenW(r.name)*2):gsub('%z','')
            local engine=name:match('_luid_(.-)_engtype_')
            local value=tonumber(r.formatted.value)
            if engine and value==value and value>=0 then engines[engine]=(engines[engine] or 0)+value end
        end
    end
    -- Sum processes sharing an engine; use the busiest engine, as Task Manager does.
    gpu_value=nil
    for _,value in pairs(engines) do gpu_value=math.max(gpu_value or 0,math.min(100,value)) end
    return gpu_value
end
function M.reset()
    last_time,last_cpu=nil,nil
    if query then pdh.PdhCloseQuery(query) end
    query,counter,gpu_time,gpu_value,gpu_tried=nil,nil,nil,nil,nil
end
function M.read(now)
    local result={}
    local success=pcall(function()
        local handle=k.GetCurrentProcess()
        local t=ffi.new('PLAYER_UI_FILETIME[4]')
        if k.GetProcessTimes(handle,t,t+1,t+2,t+3)~=0 then
            local cpu=(tonumber(t[2].hi)*4294967296+tonumber(t[2].lo)+tonumber(t[3].hi)*4294967296+tonumber(t[3].lo))/1e7
            local cores=math.max(1,tonumber(k.GetActiveProcessorCount(65535)))
            if last_time and now>last_time and cpu>=last_cpu then result.cpu=math.min(100,100*(cpu-last_cpu)/((now-last_time)*cores)) end
            last_time,last_cpu=now,cpu
        end
        local m=ffi.new('PLAYER_UI_PMC[1]');m[0].cb=ffi.sizeof(m[0])
        if k.K32GetProcessMemoryInfo(handle,m,ffi.sizeof(m[0]))~=0 then result.memory=tonumber(m[0].WorkingSetSize) end
        result.gpu=gpu_read(now)
    end)
    return success and result or {}
end
return M
end)()
local o={hide_timeout=1.6,network_speed=true,show_clock=true,volume_step=5,ui_scale=1.00,text_outline=0,font='Microsoft YaHei',accent='9F74D0'}
options.read_options(o,'player_ui')
o.ui_scale=core.clamp(tonumber(o.ui_scale) or 1.00,.45,1.5)
o.text_outline=core.clamp(tonumber(o.text_outline) or 0,0,4)
o.hide_timeout=core.clamp(o.hide_timeout,1,20);o.volume_step=core.clamp(o.volume_step,1,20)
if not o.accent:match('^%x%x%x%x%x%x$') then o.accent='9F74D0' end
local THEME,METRICS=core.theme(o.accent),core.metrics
local ui=mp.create_osd_overlay('ass-events');ui.z=20
local volume_osd=mp.create_osd_overlay('ass-events');volume_osd.z=25
local state={visible=false,x=-1,y=-1,hover=nil,menu=nil,scroll=0,drag=nil,pressed=nil,
    thumb=nil,thumb_time=nil,rate=nil,fps=nil,cpu=nil,memory=nil}
local playback_ready=false
local loading=false
local layout,buttons,menu_box,menu_items,menus
local bangumi_account={}
local draw_cover,remove_cover,cover_seen
local render_timer,hide_timer,pulse,network_timer,thumb_timer,clock_timer,loading_timer,volume_osd_timer
local render,request_render,show,sync_timers,open_menu
local show_volume_osd
local samples=core.fps_sampler()
local WHITE,MUTED,PANEL,ACCENT=THEME.text,THEME.muted,THEME.panel,THEME.accent
local presets={}
local function read_presets()
    local p=mp.command_native({'expand-path','~~/../animejanai/animejanai.conf'})
    local f=io.open(p,'rb');if f then presets=core.parse_presets(f:read(1024*1024));f:close() end
end
read_presets()
local function prop(name,default) return mp.get_property_native(name,default) end
local function num(name,default) return mp.get_property_number(name,default) end
local function bool(name,default) return mp.get_property_bool(name,default or false) end
local function cmd(...) return mp.commandv(...) end
playback_ready=not bool('idle-active',true) and not bool('core-idle',false)
state.visible=playback_ready
local function kill(t) if t then t:kill() end end
local function hide_thumb()
    kill(thumb_timer);thumb_timer=nil
    if state.thumb_time then cmd('script-message-to','thumbfast','clear');state.thumb_time=nil end
end
local function format_num(value,unit,decimals)
    return core.finite(value) and string.format('%.'..(decimals or 1)..'f',value)..(unit or '') or '—'
end
local function actual_sample()
    state.fps=samples:sample(mp.get_time(),num('vo-presented-frame-count'),bool('pause') or bool('seeking'))
    if metrics.read then local m=metrics.read(mp.get_time());state.cpu=m.cpu;state.memory=m.memory;state.gpu=m.gpu end
end
local function volume(delta)
    mp.set_property_number('volume',core.clamp(num('volume',100)+delta,0,num('volume-max',100)))
    if request_render then request_render() end
end
local function escape()
    local had_menu=menus and menus.back()
    state.drag=nil;state.pressed=nil;state.menu_drag=nil
    if not had_menu then mp.set_property_bool('fullscreen',false) end
    show()
end
local function open_file(kind)
    state.menu=nil;hide_thumb()
    cmd('script-message-to','mpvnet',kind=='audio' and 'load-audio' or 'load-sub')
end
local function current_slot() return num('user-data/animejanai/requested-slot',-1) end
local function ai_select(id)
    cmd('script-message','aji-slot',tostring(id));state.menu=nil;request_render()
end
local function safe_track(t)
    local title=core.title(t.title or '',t['external-filename'] or '')
    if not t.title or t.title=='' then title=t.type=='audio' and '音轨' or '字幕' end
    local bits={tostring(t.id)..'  '..title}
    if t.lang then bits[#bits+1]=t.lang end
    if t.codec then bits[#bits+1]=t.codec end
    if t['audio-channels'] then bits[#bits+1]=t['audio-channels']..' 声道' end
    return table.concat(bits,' · ')
end
local function info_data(kind)
    local rows={};local title=kind=='stats' and '统计信息' or '设置'
    local function row(text,fn,selected,disabled) rows[#rows+1]={text=text,fn=fn,selected=selected,disabled=disabled or fn==nil} end
    if kind=='stats' then
        local target=num('estimated-vf-fps');if target then target=target*num('speed',1) end
        row('FPS  '..format_num(state.fps,'',2)..'  /  '..format_num(target,'',2)..'    GPU '..format_num(state.gpu,'%'))
        row('播放器 CPU  '..format_num(state.cpu,'%')..'    内存 '..format_num(state.memory and state.memory/1048576,' MiB',0))
        row('源帧率  '..format_num(num('container-fps'),' FPS',3)..'    屏幕 '..format_num(num('display-fps'),' Hz',2))
        row('视频输出丢帧  '..format_num(num('frame-drop-count'),'',0)..'    解码丢帧 '..format_num(num('decoder-frame-drop-count'),'',0))
        row('音画偏差  '..format_num(num('avsync') and num('avsync')*1000,' ms',1))
        row('硬件解码  '..mp.get_property('hwdec-current','—'))
        row('视频输出  '..mp.get_property('current-vo','—'))
        local passes=prop('vo-passes',{}) or {};local total=0;local found=false
        for _,p in ipairs(passes.fresh or {}) do if core.finite(p.avg) then total=total+p.avg;found=true end end
        row('GPU 渲染耗时  '..(found and format_num(total/1e6,' ms',2) or '—'))
        row('缓冲  '..format_num(num('demuxer-cache-duration'),' 秒')..'    读取 '..core.rate(state.rate))
        local v=prop('video-params',{}) or {};local a=prop('audio-params',{}) or {}
        row('视频  '..tostring(v.w or '—')..' × '..tostring(v.h or '—')..' · '..mp.get_property('video-codec','—'))
        row('像素格式  '..tostring(v.pixelformat or '—')..' · '..tostring(v.colormatrix or '—'))
        row('音频  '..mp.get_property('audio-codec-name','—')..' · '..tostring(a.samplerate or '—')..' Hz')
        row('时长  '..core.time(num('duration'))..'    章节 '..tostring(#(prop('chapter-list',{}) or {})))
        row('mpv-AnimeFusion 状态（Ctrl+Tab）',function()state.visible=false;cmd('script-binding','animejanaistats/show_animejanai_stats')end)
        row('mpv 完整统计（Tab）',function()state.visible=false;cmd('script-binding','stats/display-stats-toggle')end)
    elseif kind=='chapters' then
        title='章节'
        for i,c in ipairs(prop('chapter-list',{}) or {}) do local t=c.time
            row(core.time(c.time)..'  '..(c.title or ('章节 '..i)),function()cmd('seek',t,'absolute+exact')end,num('chapter',-1)==i-1)
        end
    end
    return title,rows
end
-- Player UI menus 1.3.0
menus=(function()
-- inlined module: player_ui_menu.lua
-- Anchored player controls and settings popovers.
-- All rows are derived from player state; no network lookups or guessed media metadata.
return function(c)
    local M={boxes={},rows={},hover_timer=nil,hover_target=nil,dismiss_timer=nil}
    local s,core=c.state,c.core
    local theme=c.theme or core.theme(c.accent)
    local metrics=core.metrics
    local data_cache,measure_cache={},{}
    local white,muted,panel,surface,accent=theme.text,theme.muted,theme.panel,theme.surface,theme.accent
    function M.invalidate() data_cache={};measure_cache={} end
    local function close_timer()
        if M.hover_timer then M.hover_timer:kill();M.hover_timer=nil end
        M.hover_target=nil
    end
    local function dismiss_timer()
        if M.dismiss_timer then M.dismiss_timer:kill();M.dismiss_timer=nil end
    end
    function M.close()
        local was_open=s.menu~=nil
        close_timer();dismiss_timer();M.invalidate();M.boxes={};M.rows={};s.menu=nil;s.parent=nil;s.scroll=0;s.parent_scroll=0;s.menu_drag=nil
        if was_open and c.on_close then c.on_close() end
    end
    function M.open(kind,parent)
        if not M.allowed[kind] then return end
        close_timer();dismiss_timer();M.hover_blocked=nil
        if s.menu==kind and not parent then M.close();return end
        M.invalidate()
        if parent then s.parent=parent else s.parent=nil;s.parent_scroll=0 end
        s.menu=kind;s.scroll=0;s.menu_drag=nil
        if kind=='ai' then c.read_presets() end
    end
    function M.back()
        if not s.menu then return false end
        dismiss_timer()
        if s.parent then M.hover_blocked=s.menu;s.menu=s.parent;s.parent=nil;s.scroll=s.parent_scroll or 0;s.parent_scroll=0;close_timer()
        else M.close() end
        return true
    end
    local LANG={ja='日语',jp='日语',jpn='日语',zh='中文',chi='中文',zho='中文',
        en='英语',eng='英语',ko='韩语',kor='韩语',fr='法语',fre='法语',fra='法语',
        de='德语',ger='德语',deu='德语',es='西班牙语',spa='西班牙语',
        pt='葡萄牙语',por='葡萄牙语',ru='俄语',rus='俄语',it='意大利语',ita='意大利语',
        th='泰语',tha='泰语',vi='越南语',vie='越南语',ar='阿拉伯语',ara='阿拉伯语',
        id='印尼语',ind='印尼语',nl='荷兰语',nld='荷兰语',pl='波兰语',pol='波兰语'}
    local CODEC={subrip='SRT',srt='SRT',ass='ASS',ssa='ASS',webvtt='WebVTT',vtt='WebVTT',
        mov_text='MOV 文本',microdvd='MicroDVD',subviewer='SubViewer',text='文本',
        dvd_subtitle='DVD 图形字幕',dvb_subtitle='DVB 图形字幕',hdmv_pgs_subtitle='PGS 图形字幕',
        pgs='PGS 图形字幕',dvb_teletext='图文电视',eia_608='隐藏字幕',
        aac='AAC',ac3='AC3',eac3='E-AC3',dts='DTS',truehd='TrueHD',flac='FLAC',
        opus='Opus',mp3='MP3',vorbis='Vorbis',pcm_s16le='PCM',pcm_s24le='PCM'}
    local function lang_label(value)
        if not value or value=='' then return nil end
        local key=value:lower():gsub('_','-')
        if key:match('^zh') then
            if key:match('hans') or key:match('^zh%-cn') or key:match('^zh%-sg') then return '中文（简体）' end
            if key:match('hant') or key:match('^zh%-tw') or key:match('^zh%-hk') or key:match('^zh%-mo') then return '中文（繁体）' end
            return '中文'
        end
        return LANG[key] or LANG[key:match('^%a+') or ''] or value
    end
    local function codec_label(value)
        if not value or value=='' then return nil end
        local key=value:lower()
        return CODEC[key] or key:upper()
    end
    local function title_label(value)
        if not value or value=='' then return nil end
        local title=value:gsub('^%s+',''):gsub('%s+$','')
        if title:match('%?') or title:match('://') or title:match('/') then
            title=(title:match('^[^?]*') or title)
            title=title:gsub('^%a[%w+.-]*://',''):gsub('^.*[/\\]','')
            title=title:gsub('%%(%x%x)',function(hex)return string.char(tonumber(hex,16))end)
        end
        if title=='' or title:match('^[Ss]tream%.[%w]+$') then return nil end
        return title
    end
    local function track_name(t)
        local parts={}
        local function add(value) if value and value~='' then parts[#parts+1]=value end end
        local lang=lang_label(t.lang)
        local name=title_label(t.title) or (t.external and title_label(t['external-filename'] or '') or nil)
        add(lang);add(name);add(codec_label(t.codec))
        if t.type=='audio' and t['audio-channels'] then add(t['audio-channels']..' 声道') end
        if t.external then add('外挂') end
        if #parts==0 then return (t.type=='sub' and '字幕 ' or '音轨 ')..t.id end
        if not lang and not name then add('#'..t.id) end
        return table.concat(parts,' · ')
    end
    function M.data(kind)
        local cached=data_cache[kind]
        if cached then return cached.title,cached.items end
        local a={};local title=M.allowed[kind]
        if kind=='speed' or kind=='ai' or kind=='scale' then title=nil end
        local function row(text,fn,selected,extra)
            local r=extra or {};r.text=text;r.fn=fn;r.selected=selected;r.disabled=fn==nil and not r.target and not r.slider
            a[#a+1]=r;return r
        end
        local function link(text,target,icon)row(text,nil,false,{target=target,icon=icon})end
        local function separator()a[#a+1]={separator=true,h=12}end
        local account=c.account and c.account() or {}
        local function action(...)c.command('script-message-to','mpvnet','bangumi-action',...)end
        local function enabled(fn)return not account.busy and fn or nil end
        if kind=='settings' then
            link('缩放模式','scale')
            link('超分与补帧','ai')
            link('字幕设置','sub-settings','sub')
            link('弹幕设置','danmaku-settings','danmaku')
            link('同步设置','sync-settings','bangumi')
            link('统计信息','stats','info')
            row('显示时间',c.toggle_clock,c.clock(),{stay=true})
        elseif kind=='sync-settings' then
            local settings=account.settings or {}
            row('自动收藏为在看',function()action('setting','auto-collect',settings.autoCollect and 'no' or 'yes')end,settings.autoCollect,{stay=true})
            row('收藏进度',nil,false,{slider={value=settings.collectPercent or 10,min=1,max=100,step=1,format='%.0f%%',
                set=function(v)action('setting','collect-percent',tostring(v))end}})
            separator()
            row('自动标记剧集看过',function()action('setting','auto-sync',settings.autoSync and 'no' or 'yes')end,settings.autoSync,{stay=true})
            row('看过进度',nil,false,{slider={value=settings.watchedPercent or 90,min=1,max=100,step=1,format='%.0f%%',
                set=function(v)action('setting','watched-percent',tostring(v))end}})
        elseif kind=='bangumi' then
            if not account.connected then
                local retry=account.authorizing or (account.status and account.status~='')
                local login=(not account.busy or account.authorizing) and function()action('login')end or nil
                row(retry and '重新授权' or '登录 Bangumi',login,false,{stay=true})
                if account.authorizing then row('等待浏览器授权…',nil,false,{status=true,spinner=true}) end
            else
                local subject=account.subject
                if subject then
                    row(subject.title,nil,false,{card=subject,h=156,status=true})
                    link('收藏：'..({[0]='未收藏','想看','看过','在看','搁置','抛弃'})[subject.collectionType or 0],'bangumi-collection')
                    for _,episode in ipairs(subject.episodes or {}) do
                        if episode.id==subject.currentEpisode then row('当前：'..episode.title,nil,false,{status=true,wrap=2});break end
                    end
                    row(subject.kind=='电影' and '正片' or '剧集',nil,false,{h=36,status=true})
                    local cells={}
                    for _,episode in ipairs(subject.episodes or {}) do
                        local ep=episode
                        cells[#cells+1]={text=subject.kind=='电影' and ep.kind==0 and '正片' or (ep.kind==0 and '' or 'SP ')..tostring(ep.number),
                            key='bangumi-episode:'..ep.id,selected=ep.id==subject.currentEpisode,state=ep.state,
                            disabled=account.busy,stay=true,fn=function()s.bangumi_episode=ep.id;c.open('bangumi-episode','bangumi')end}
                        if #cells==6 then row('',nil,false,{cells=cells,h=56});cells={} end
                    end
                    if #cells>0 then row('',nil,false,{cells=cells,h=56}) end
                elseif account.resolving then
                    row('正在匹配…',nil,false,{status=true,spinner=true})
                end
                separator()
                if not account.resolving and (not subject or not subject.currentEpisode) then
                    row('重新匹配',function()action('retry-match')end,false,{stay=true})
                end
                row('选择条目…',function()c.command('script-message-to','mpvnet','show-bangumi-match')end)
            end
            if account.status and account.status~='' then row(account.status,nil,false,{status=true,wrap=2}) end
        elseif kind=='bangumi-collection' then
            local subject=account.subject or {}
            for type,label in ipairs({'想看','看过','在看','搁置','抛弃'}) do local value=type
                row(label,enabled(function()action('collection',tostring(value),tostring(account.generation))end),subject.collectionType==value,{stay=true})
            end
        elseif kind=='bangumi-episode' then
            local subject=account.subject or {};local episode
            for _,ep in ipairs(subject.episodes or {}) do if ep.id==s.bangumi_episode then episode=ep;break end end
            if episode then
                row(episode.title,nil,false,{status=true,wrap=3})
                row('设为当前剧集',enabled(function()action('select-episode',tostring(episode.id),tostring(account.generation))end),
                    subject.currentEpisode==episode.id,{stay=true})
                separator()
                local can_edit=(subject.collectionType or 0)>0 and not account.busy
                for _,item in ipairs({{'看过',2},{'看到',2,'through'},{'想看',1},{'抛弃',3},{'未看',0}}) do local label,value,mode=item[1],item[2],item[3]
                    row(label,can_edit and (mode~='through' or episode.kind==0) and function()
                        action('episode',tostring(episode.id),tostring(value),mode or 'single',tostring(account.generation))
                    end or nil,mode~='through' and episode.state==value,{stay=true})
                end
                if not can_edit and not account.busy then row('请先收藏',nil,false,{status=true}) end
            end
        elseif kind=='speed' then
            for _,v in ipairs({8,5,3,2,1.5,1.25,1,.5}) do local n=v
                local label=v%1==0 and string.format('%.1fx',v) or string.format('%gx',v)
                row(label,function()c.set_number('speed',n)end,math.abs(c.num('speed',1)-v)<.001)
            end
        elseif kind=='sub' or kind=='audio' then
            local typ=kind=='sub' and 'sub' or 'audio'
            local p=kind=='audio' and 'aid' or 'sid'
            local selected=tostring(c.prop(p,'no'))
            row('关闭',function()c.set(p,'no')end,selected=='no',{stay=true,key=p..':no'})
            for _,t in ipairs(c.prop('track-list',{}) or {}) do
                local danmaku=c.prop('user-data/player_ui/danmaku',{}) or {}
                if t.type==typ and not (typ=='sub' and t.id==danmaku.track) then local id=t.id
                    row(track_name(t),function()
                        c.set_number(p,id)
                        if p=='sid' then c.set_bool('sub-visibility',true) end
                    end,selected==tostring(id),{stay=true,key=p..':'..id})
                end
            end
            separator()
            row(kind=='sub' and '导入本地字幕' or '添加音轨',function()c.open_file(kind)end,false,{icon='plus'})
            if kind=='audio' then link('音频设置','audio-settings') end
        elseif kind=='danmaku' then
            local d=c.prop('user-data/player_ui/danmaku',{}) or {}
            row('关闭',function()if d.enabled then c.command('script-message','player_ui-danmaku-toggle')end end,not d.loaded or not d.enabled,{stay=true})
            if d.autoload_state=='loading' then
                row('自动加载中…',nil,false,{status=true,spinner=true})
            elseif not d.loaded and d.autoload_state=='error' then
                row('自动加载失败',nil,false,{status=true})
                row('重试匹配',function()c.command('script-message','player_ui-danmaku-retry-match')end,false,{stay=true})
            elseif not d.loaded and d.autoload_state=='not-found' then
                row('没有匹配到弹幕',nil,false,{status=true})
                row('重试匹配',function()c.command('script-message','player_ui-danmaku-retry-match')end,false,{stay=true})
            elseif not d.loaded and d.autoload_state=='empty' then
                row('该集没有弹幕',nil,false,{status=true})
            elseif d.loaded then
                row(core.title('',d.file),function()if not d.enabled then c.command('script-message','player_ui-danmaku-toggle')end end,d.enabled,
                    {stay=true,wrap=5,detail=(d.source_label or '本地弹幕')..'·'..tostring(d.count or 0)..'条弹幕'})
            end
            separator()
            row('搜索弹幕',function()c.command('script-message','player_ui-danmaku-search')end)
            row('导入本地弹幕',function()c.command('script-message','player_ui-danmaku-choose')end)
        elseif kind=='ai' then
            local id=c.current_slot()
            row('关闭',function()c.select_slot(0)end,id==0,{hint='Ctrl+0',key='preset:0'})
            for n=1,9 do local v=n;local name=c.presets()[n]
                if name then row(name,function()c.select_slot(v)end,id==v,{hint='Ctrl+'..v,key='preset:'..v}) end
            end
            separator()
            row('配置管理器',c.manager,false,{icon='settings'})
        elseif kind=='scale' then
            local keep=c.bool('keepaspect',true);local original=c.prop('video-unscaled','no')
            local pan=c.num('panscan',0)
            for _,r in ipairs({{'适应窗口','fit'},{'填充裁剪','fill'},{'拉伸铺满','stretch'},{'原始尺寸','original'}}) do local mode=r[2]
                local selected=(mode=='stretch' and not keep) or (keep and ((mode=='original' and original=='yes') or (original~='yes' and ((mode=='fill' and pan==1) or (mode=='fit' and pan==0)))))
                row(r[1],function()
                    c.set_bool('keepaspect',mode~='stretch');c.set('video-unscaled',mode=='original' and 'yes' or 'no')
                    c.set_number('panscan',mode=='fill' and 1 or 0);c.set_number('video-zoom',0);c.set('video-aspect-override','no')
                end,selected)
            end
        elseif kind=='sub-settings' then
            row('字号缩放',nil,false,{slider={value=c.num('sub-scale',1),min=.5,max=2,step=.05,format='%.2fx',set=function(v)c.set_number('sub-scale',v)end}})
            row('字幕位置',nil,false,{slider={value=c.num('sub-pos',100),min=0,max=100,step=1,format='%.0f%%',set=function(v)c.set_number('sub-pos',v)end}})
            row('延迟  '..string.format('%+.1f 秒',c.num('sub-delay',0)),nil,false,{h=44})
            row('提前 0.1 秒',function()c.command('add','sub-delay','-0.1')end,false,{stay=true})
            row('延后 0.1 秒',function()c.command('add','sub-delay','0.1')end,false,{stay=true})
            row('重置延迟',function()c.set_number('sub-delay',0)end,false,{stay=true})
        elseif kind=='audio-settings' then
            row('音频延迟  '..string.format('%+.1f 秒',c.num('audio-delay',0)),nil,false,{h=44})
            row('提前 0.1 秒',function()c.command('add','audio-delay','-0.1')end,false,{stay=true})
            row('延后 0.1 秒',function()c.command('add','audio-delay','0.1')end,false,{stay=true})
            row('重置延迟',function()c.set_number('audio-delay',0)end,false,{stay=true})
        elseif kind=='danmaku-settings' then
            row('弹幕线路…',function()c.command('script-message','player_ui-danmaku-manage')end,false,{icon='settings'})
            local d=c.prop('user-data/player_ui/danmaku',{}) or {}
            local settings=d.settings or {}
            local function set(key,value) c.command('script-message','player_ui-danmaku-setting',key,tostring(value)) end
            local function slider(label,key,value,min,max,step,format)
                row(label,nil,false,{slider={value=value,min=min,max=max,step=step,
                    format=format,set=function(v)set(key,v)end}})
            end
            slider('显示区域','area',(settings.displayArea or 1)*100,10,100,5,'%.0f%%')
            slider('不透明度','opacity-percent',(settings.opacity or 180)*100/255,1,100,1,'%.0f%%')
            slider('弹幕字号','fontsize',settings.fontsize or 38,12,100,1,'%.0f')
            slider('速度','speed',12/(settings.scrolltime or 12),.5,3,.1,'%.1f×')
            slider('弹幕帧率','fps',settings.fps or 60,30,90,30,'%.0f FPS')
            separator()
            for _,group in ipairs({{label='屏蔽固定弹幕',modes={'TOP','BOTTOM'}},
                {label='屏蔽滚动弹幕',modes={'R2L','L2R'}},{label='屏蔽彩色弹幕',modes={'COLOR'}}}) do
                local selected={}
                for _,mode in ipairs(group.modes) do selected[mode]=true end
                local blocked=false
                for _,value in ipairs(settings.blockmode or {}) do if selected[value] then blocked=true end end
                row(group.label,function()
                    local values={}
                    for _,value in ipairs(settings.blockmode or {}) do if not selected[value] then values[#values+1]='"'..value..'"' end end
                    if not blocked then for _,mode in ipairs(group.modes) do values[#values+1]='"'..mode..'"' end end
                    set('blockmode','['..table.concat(values,',')..']')
                end,blocked,{stay=true})
            end
            row('屏蔽词…',function()c.command('script-message','player_ui-danmaku-words')end)
        else
            title,a=c.info(kind)
            for _,r in ipairs(a) do r.h=52;r.wrap=2;r.key=r.key or r.text end
        end
        data_cache[kind]={title=title,items=a}
        return title,a
    end
    M.allowed={settings='设置',speed='播放速度',sub='字幕',audio='音轨',danmaku='弹幕',ai='超分与补帧',scale='缩放模式',
        ['sub-settings']='字幕设置',['danmaku-settings']='弹幕设置',
        ['audio-settings']='音频设置',stats='统计信息',chapters='章节',
        bangumi='Bangumi',['sync-settings']='同步设置',['bangumi-collection']='收藏',['bangumi-episode']='剧集'}
    local function width(kind,l)
        local widths={speed=216,settings=200,sub=344,audio=344,danmaku=312,ai=256,scale=280,stats=440,chapters=368,bangumi=440}
        local u=l.ui or 1
        return math.min((widths[kind] or 344)*u,math.max(1,l.w-2*metrics.menu_margin*u))
    end
    local function measure(items,w,kind,u)
        local key=string.format('%s:%.2f:%.3f',kind,w,u)
        local cached=measure_cache[key]
        if cached and cached.items==items then return cached.total,cached.rows end
        local y,rows=0,{}
        for _,r in ipairs(items) do
            local indent=r.icon and metrics.icon_column*u or metrics.row_padding*u+8*u
            local available=w-indent-30*u-(r.hint and 52*u or 0)-(r.target and 28*u or 0)
            local lines=core.wrap(r.text or '',available,metrics.row_text*u,r.wrap or 2)
            local height=r.h and r.h*u or r.separator and metrics.separator*u or r.slider and metrics.slider*u
                or math.max(kind=='ai' and 48*u or metrics.row_min*u,
                    #lines*metrics.row_line*u+(r.detail and metrics.row_detail_line*u+5*u or 0)+metrics.row_padding*u)
            rows[#rows+1]={row=r,top=y,height=height,lines=lines}
            y=y+height
        end
        measure_cache[key]={items=items,total=y,rows=rows}
        return y,rows
    end
    local function anchor(l,id)
        for _,b in ipairs(l.controls) do if b.id==id then return b.x end end
        return l.w-232
    end
    local function header_height(kind,title,u) return title and metrics.menu_header*u or 0 end
    local function makebox(l,kind)
        local u=l.ui or 1
        local title,items=M.data(kind);local w=width(kind,l);local hh=header_height(kind,title,u)
        local total,rows=measure(items,w,kind,u);local bottom=l.h-(kind=='speed' and metrics.menu_speed_bottom or metrics.menu_bottom)*u
        local pad=metrics.menu_padding*u
        local margin=math.min(metrics.menu_margin*u,math.max(0,(l.w-w)/2))
        local max_height=math.max(1,l.h-2*margin)
        local h=math.min(hh+total+pad*2,max_height,math.max(1,bottom-margin));local y=math.max(margin,bottom-h)
        local max_x=math.max(margin,l.w-w-margin)
        local x=core.clamp(anchor(l,kind)-w/2,margin,max_x)
        return {kind=kind,title=title,items=items,rows=rows,x0=x,x1=x+w,y0=y,y1=y+h,header=hh,total=total,
            view=math.max(0,h-hh-pad*2),content=y+hh+pad,ui=u}
    end
    function M.draw(l,d,buttons)
        M.boxes={};M.rows={}
        if not s.menu then return M.rows end
        local function add(b) b.menu=true;buttons[#buttons+1]=b end
        local function drawbox(b,scroll_key,is_child)
            local u=b.ui or 1;local w=b.x1-b.x0
            scroll_key=scroll_key or 'scroll'
            s[scroll_key]=core.clamp(s[scroll_key] or 0,0,math.max(0,b.total-b.view));b.offset=s[scroll_key];b.scroll_key=scroll_key
            M.boxes[#M.boxes+1]=b
            d.round(b.x0,b.y0+3*u,b.x1,b.y1+3*u,9*u,theme.border,56)
            d.round(b.x0,b.y0,b.x1,b.y1,9*u,panel,5)
            if b.title then
                d.text(b.x0+metrics.row_padding*u,b.y0+29*u,22*u,b.title,4,white,true,w-80*u,.8)
            end
            if b.kind=='bangumi' and c.account and c.account().connected then
                local account=c.account()
                local button={id='bangumi-logout',key='退出登录',x0=b.x1-52*u,x1=b.x1-12*u,
                    y0=b.y0+9*u,y1=b.y0+49*u,disabled=account.busy}
                local hover=core.inside(button,s.x,s.y)
                if hover then d.round(button.x0,button.y0,button.x1,button.y1,6*u,theme.hover,5) end
                d.icon('logout',b.x1-32*u,b.y0+29*u,hover,account.busy,true)
                add(button)
            end
            local bottom=b.content+b.view
            for _,entry in ipairs(b.rows) do
                local r=entry.row
                if not r.separator then M.rows[#M.rows+1]=r end
                local index=#M.rows
                local yy=b.content+entry.top-b.offset;local y1=yy+entry.height
                if y1>b.content and yy<bottom then
                    -- Keep vertical clipping for scrolled rows, but do not clip text
                    -- to the panel's horizontal edge. libass can crop CJK glyphs at
                    -- that boundary after DPI scaling even when the anchor is inset.
                    d.clip(0,b.content,l.w,bottom,function()
                        if r.separator then d.rect(b.x0+12*u,yy+entry.height/2,b.x1-12*u,yy+entry.height/2+u,theme.divider,18);return end
                        local box={id='row-'..index,index=index,x0=b.x0+6*u,x1=b.x1-6*u,y0=math.max(yy,b.content),y1=math.min(y1,bottom),key=r.key or r.text}
                        if r.card then
                            local left=b.x0+120*u;local available=w-140*u
                            d.round(b.x0+20*u,yy+8*u,b.x0+106*u,yy+137*u,5*u,surface,0)
                            if d.image then d.image(r.card.cover,b.x0+20*u,yy+8*u,86*u,129*u,b.content,bottom) end
                            local cy=yy+12*u
                            for _,line in ipairs(core.wrap(r.text,available,20*u,3)) do d.text(left,cy,20*u,line,7,white,true,nil,.8);cy=cy+24*u end
                            local detail=r.card.kind=='电影' and '电影' or ('第 '..tostring(r.card.season or 1)..' 季')
                            d.text(left,yy+88*u,14*u,(r.card.date or '')..' · '..detail,7,muted,false,available,.8)
                            d.text(left,yy+116*u,22*u,r.card.score and string.format('%.1f',r.card.score) or '暂无评分',7,accent,true,nil,.8)
                            return
                        elseif r.cells then
                            local gap=6*u;local cw=(w-32*u-5*gap)/6
                            for column,cell in ipairs(r.cells) do
                                M.rows[#M.rows+1]=cell
                                local x=b.x0+16*u+(column-1)*(cw+gap)
                                local cellbox={id='row-'..#M.rows,index=#M.rows,key=cell.key,x0=x,x1=x+cw,y0=math.max(yy+3*u,b.content),y1=math.min(y1-3*u,bottom)}
                                local hover=core.inside(cellbox,s.x,s.y)
                                local watched=cell.state==2
                                local marker=cell.selected or not watched
                                d.round(x,yy+3*u,x+cw,y1-(marker and 10 or 3)*u,6*u,watched and accent or (hover and theme.hover or surface),watched and 0 or 5)
                                local colors={[1]=theme.secondary,[2]=accent,[3]=muted}
                                if marker then
                                    d.round(x,yy+entry.height-8*u,x+cw,yy+entry.height-4*u,2*u,cell.selected and theme.current or colors[cell.state] or theme.track,(not cell.selected and cell.state==0) and 120 or 0)
                                end
                                d.text(x+cw/2,yy+entry.height/2,16*u,cell.text,5,cell.disabled and muted or white,cell.selected,nil,.8)
                                if not cell.disabled then add(cellbox) end
                            end
                            return
                        end
                        local hovered=core.inside(box,s.x,s.y)
                        local picked=r.selected or (r.target and r.target==s.menu)
                        if hovered or picked then
                            d.round(box.x0+2*u,yy+3*u,box.x1-2*u,y1-3*u,6*u,theme.hover,5)
                        end
                        if picked then d.round(b.x0+8*u,yy+entry.height/2-12*u,b.x0+11*u,yy+entry.height/2+12*u,1.5*u,accent,0) end
                        if not r.disabled then add(box) end
                        local left=b.x0+metrics.row_padding*u+8*u
                        if r.icon then d.icon(r.icon,b.x0+30*u,yy+entry.height/2,false,false,true);left=b.x0+metrics.icon_column*u end
                        local color=r.disabled and not r.status and muted or white
                        if r.slider then
                            local slider=r.slider;local sx=b.x0+metrics.row_padding*u;local ex=b.x1-metrics.row_padding*u;local sy=yy+62*u
                            d.text(left,yy+22*u,metrics.row_text*u,r.text,4,color,false,nil,.8)
                            d.text(ex,yy+22*u,metrics.row_text*u,string.format(slider.format,slider.value),6,muted,false,nil,.8)
                            d.round(sx,sy-2*u,ex,sy+2*u,2*u,theme.track,0)
                            local xx=sx+(ex-sx)*core.clamp((slider.value-slider.min)/(slider.max-slider.min),0,1)
                            d.round(sx,sy-2*u,xx,sy+2*u,2*u,accent,0);d.circle(xx,sy,6*u,accent,0)
                            add({id='slider-'..index,slider=index,x0=sx-8*u,x1=ex+8*u,y0=sy-18*u,y1=sy+18*u,start=sx,finish=ex})
                        else
                            local detail_height=r.detail and (metrics.row_detail_line+5)*u or 0
                            local text_height=#entry.lines*metrics.row_line*u+detail_height
                            local ty=yy+(entry.height-text_height)/2
                            for _,line in ipairs(entry.lines) do d.text(left,ty,metrics.row_text*u,line,7,color,false,nil,.8);ty=ty+metrics.row_line*u end
                            if r.spinner then
                                local cx,cy=b.x1-30*u,yy+entry.height/2
                                for dot=0,7 do
                                    local angle=dot*math.pi/4
                                    d.circle(cx+math.sin(angle)*8*u,cy-math.cos(angle)*8*u,2*u,accent,
                                        35+math.floor(((dot-(s.tick or 0))%8)*185/7))
                                end
                            end
                            if r.detail then d.text(left,ty+2*u,metrics.row_detail*u,r.detail,7,muted,false,w-(left-b.x0)-24*u,.8) end
                            if r.target then d.text(b.x1-25*u,yy+entry.height/2,27*u,'›',6,theme.secondary,false,nil,.8)
                            elseif r.hint then d.text(b.x1-19*u,yy+entry.height/2,13*u,r.hint,6,muted,false,nil,.8) end
                        end
                    end)
                end
            end
            if b.total>b.view then
                local height=math.max(28*u,b.view*b.view/b.total)
                local sy=b.content+(b.view-height)*b.offset/(b.total-b.view)
                d.round(b.x1-7*u,sy,b.x1-4*u,sy+height,2*u,theme.secondary,45)
                add({id='menu-scroll-'..b.kind,x0=b.x1-13*u,x1=b.x1,y0=b.content,y1=b.content+b.view,scroll_key=scroll_key,
                    start=b.content,range=b.view-height,max=b.total-b.view,height=height,sy=sy})
            end
        end
        local b=makebox(l,s.menu)
        if s.parent then
            local parent=makebox(l,s.parent)
            local u=l.ui or 1;local gap=8*u;local margin=metrics.menu_margin*u
            local child_width=b.x1-b.x0;local left=parent.x0-child_width-gap
            if left<margin then
                local right=parent.x1+gap
                left=right+child_width<=l.w-margin and right or margin
            end
            local dx=left-b.x0;local dy=parent.y1-b.y1
            b.x0=b.x0+dx;b.x1=b.x1+dx;b.y0=b.y0+dy;b.y1=b.y1+dy;b.content=b.content+dy
            drawbox(parent,'parent_scroll',false)
            drawbox(b,'scroll',true)
        else drawbox(b,'scroll',false) end
        return M.rows
    end
    function M.pick(b)
        if b.id=='menu-back' then M.back()
        elseif b.id=='menu-dismiss' then M.close()
        elseif b.id=='bangumi-logout' and not b.disabled then
            c.command('script-message-to','mpvnet','bangumi-action','logout');M.close()
        elseif b.index then
            local r=M.rows[b.index]
            if r and r.target then
                local parent=s.parent or s.menu
                s.parent_scroll=s.parent and s.parent_scroll or s.scroll
                c.open(r.target,parent)
            elseif r and r.fn and not r.disabled then r.fn();if not r.stay then M.close() end end
        end
    end
    function M.press(b,x,y)
        if b.slider then
            local r=M.rows[b.slider]
            if r and r.slider then s.menu_drag={slider=r.slider,box=b};M.drag(x,y);return true end
        elseif b.scroll_key then
            s.menu_drag={box=b,grab=y>=b.sy and y<=b.sy+b.height and y-b.sy or b.height/2}
            M.drag(x,y);return true
        end
        return false
    end
    function M.drag(x,y)
        local p=s.menu_drag;if not p then return end
        if p.slider then
            local r=p.slider;local v=r.min+core.clamp((x-p.box.start)/(p.box.finish-p.box.start),0,1)*(r.max-r.min)
            v=core.clamp(math.floor(v/r.step+.5)*r.step,r.min,r.max)
            if p.last~=v then p.last=v;r.set(v) end
        else local b=p.box;s[b.scroll_key]=core.clamp((y-b.start-p.grab)/math.max(1,b.range),0,1)*b.max end
    end
    function M.wheel(delta,x,y)
        if not s.menu then return false end
        for i=#M.boxes,1,-1 do local b=M.boxes[i]
            if core.inside(b,x,y) then s[b.scroll_key]=core.clamp((s[b.scroll_key] or 0)-delta*(b.drawer and 72 or 60),0,math.max(0,b.total-b.view));return true end
        end
        return s.menu~=nil
    end
    function M.hover(b,suppress_dismiss)
        if not s.menu then close_timer();dismiss_timer();return end
        if suppress_dismiss then close_timer();dismiss_timer();return end
        local id=b and b.id
        local menu_surface=b and b.menu and id~='menu-dismiss'
        local menu_trigger=id=='settings' or id=='speed' or id=='audio' or id=='sub'
            or id=='danmaku' or id=='bangumi'
        if menu_surface or menu_trigger then dismiss_timer()
        elseif not M.dismiss_timer then
            M.dismiss_timer=c.after(.45,function()
                M.dismiss_timer=nil
                if s.menu then M.close() end
            end)
        end
        local r=b and b.index and M.rows[b.index]
        local target=r and r.target
        if target~=M.hover_blocked then M.hover_blocked=nil end
        if target and target==M.hover_blocked then close_timer();return end
        if target==s.menu then close_timer();return end
        if target==M.hover_target then return end
        close_timer()
        if target then
            M.hover_target=target
            M.hover_timer=c.after(.18,function()
                M.hover_timer=nil;M.hover_target=nil
                if s.menu then
                    s.parent_scroll=s.parent and s.parent_scroll or s.scroll
                    c.open(target,s.parent or s.menu)
                end
            end)
        end
    end
    function M.hit_override(b,x,y)
        if not s.menu then return b end
        if b and b.menu then return b end
        for _,box in ipairs(M.boxes) do if core.inside(box,x,y) then return {id='menu-surface',menu=true} end end
        if b and (b.id=='settings' or b.id=='speed' or b.id=='audio'
            or b.id=='sub' or b.id=='danmaku' or b.id=='bangumi') then return b end
        return {id='menu-dismiss',menu=true}
    end
    function M.shutdown()close_timer();dismiss_timer()end
    return M
end
end)()({
    state=state,core=core,theme=THEME,accent=ACCENT,prop=prop,num=num,bool=bool,command=cmd,
    account=function()return bangumi_account end,
    set=mp.set_property,set_number=mp.set_property_number,set_bool=mp.set_property_bool,
    read_presets=read_presets,presets=function()return presets end,current_slot=current_slot,select_slot=ai_select,
    clock=function()return o.show_clock end,
    toggle_clock=function()
        local path=mp.command_native({'expand-path','~~/script-opts/player_ui.conf'})
        local f=io.open(path,'rb');local content=f and f:read('*a') or ''
        if f then f:close() end
        local value=not o.show_clock
        local line='show_clock='..(value and 'yes' or 'no')
        if content:find('show_clock%s*=') then
            content=content:gsub('([\r\n]?)show_clock%s*=[^\r\n]*',function(prefix)return prefix..line end)
        else content=content..'\n'..line..'\n' end
        f=io.open(path,'wb')
        if not f then mp.msg.error('无法保存时间显示设置');return end
        local ok=f:write(content);f:close()
        if not ok then mp.msg.error('无法保存时间显示设置');return end
        o.show_clock=value;menus.invalidate();sync_timers();request_render()
    end,
    open_file=open_file,info=info_data,after=mp.add_timeout,on_close=function()if show then show()end end,
    open=function(kind,parent)open_menu(kind,parent)end,
    manager=function()cmd('run',mp.command_native({'expand-path','~~/../mpv-AnimeFusionManager.exe'}))end
})
open_menu=function(kind,parent)
    if not playback_ready then return end
    if kind=='more' then kind='settings' end
    hide_thumb();samples:reset();state.fps=nil;state.cpu=nil;state.memory=nil;state.gpu=nil
    if metrics.reset then metrics.reset() end
    menus.open(kind,parent)
    if state.menu=='stats' then actual_sample() end
    show();sync_timers()
end
local function activate(id)
    if id=='play' then
        if bool('eof-reached') and bool('seekable') then cmd('seek',0,'absolute+exact') end
        cmd('cycle','pause')
    elseif id=='previous' or id=='next' then
        if num('playlist-count',0)>1 then cmd(id=='previous' and 'playlist-prev' or 'playlist-next','weak')
        elseif bool('seekable') then cmd('seek',id=='previous' and -10 or 10,'relative+exact') end
    elseif id=='volume' then cmd('cycle','mute')
    elseif id=='bangumi' then open_menu('bangumi')
    elseif id=='fullscreen' then cmd('cycle','fullscreen')
    else open_menu(id) end
end
local icons={
 play='m 9 4 l 27 16 9 28',pause='m 7 5 l 13 5 13 27 7 27 m 20 5 l 26 5 26 27 20 27',
 previous='m 5 6 l 8 6 8 26 5 26 m 26 5 l 10 16 26 27',next='m 24 6 l 27 6 27 26 24 26 m 6 5 l 22 16 6 27',
 volume='m 2 12 l 8 12 17 5 17 27 8 20 2 20 m 21 9 b 27 13 27 19 21 23 l 21 20 b 24 17 24 15 21 12 m 23 3 b 36 10 36 22 23 29 l 23 26 b 32 20 32 12 23 6',
 muted='m 2 12 l 8 12 17 5 17 27 8 20 2 20 m 22 11 l 25 14 28 11 30 13 27 16 30 19 28 21 25 18 22 21 20 19 23 16 20 13',
 audio='m 16 3 l 27 3 27 10 19 10 19 24 b 19 32 6 32 6 25 b 6 20 11 18 16 21',
 sub='m 3 4 l 29 4 b 31 4 32 6 32 8 l 32 24 b 32 28 30 28 28 28 l 4 28 b 0 28 0 26 0 24 l 0 8 b 0 4 1 4 3 4',
 danmaku='m 3 3 l 29 3 29 26 10 26 5 30 5 26 3 26 m 6 7 l 6 22 26 22 26 7 6 7 m 9 10 l 23 10 23 13 9 13 m 9 16 l 18 16 18 19 9 19',
 ai='m 16.00 3.50 l 19.12 0.31 22.12 1.22 22.94 5.61 24.84 7.16 29.30 7.11 30.78 9.88 28.26 13.56 28.50 16.00 31.69 19.12 30.78 22.12 26.39 22.94 24.84 24.84 24.89 29.30 22.12 30.78 18.44 28.26 16.00 28.50 12.88 31.69 9.88 30.78 9.06 26.39 7.16 24.84 2.70 24.89 1.22 22.12 3.74 18.44 3.50 16.00 0.31 12.88 1.22 9.88 5.61 9.06 7.16 7.16 7.11 2.70 9.88 1.22 13.56 3.74 m 16 10 b 12.69 10 10 12.69 10 16 b 10 19.31 12.69 22 16 22 b 19.31 22 22 19.31 22 16 b 22 12.69 19.31 10 16 10',
 stats='m 3 3 l 6 3 6 26 30 26 30 29 3 29 m 10 16 l 14 16 14 23 10 23 m 18 11 l 22 11 22 23 18 23 m 26 5 l 30 5 30 23 26 23',
 performance='m 2 15 l 8 15 12 4 19 25 23 15 30 15 30 18 25 18 19 32 12 13 10 18 2 18',
 playlist='m 3 6 l 27 6 27 9 3 9 m 3 13 l 27 13 27 16 3 16 m 3 20 l 18 20 18 23 3 23 m 23 20 l 32 26 23 32',
 fullscreen='m 2 2 l 12 2 12 5 5 5 5 12 2 12 m 20 2 l 30 2 30 12 27 12 27 5 20 5 m 2 20 l 5 20 5 27 12 27 12 30 2 30 m 27 20 l 30 20 30 30 20 30 20 27 27 27',
 restore='m 10 2 l 13 2 13 13 2 13 2 10 10 10 m 19 2 l 22 2 22 10 30 10 30 13 19 13 m 2 19 l 13 19 13 30 10 30 10 22 2 22 m 19 19 l 30 19 30 22 22 22 22 30 19 30',
 more='m 2 14 l 7 14 7 19 2 19 m 14 14 l 19 14 19 19 14 19 m 26 14 l 31 14 31 19 26 19'
}
icons.bangumi='m 16 2 b 8 2 8 16 16 16 b 24 16 24 2 16 2 m 3 30 b 3 13 29 13 29 30 l 25 30 b 25 18 7 18 7 30'
icons.logout='m 3 3 l 16 3 16 6 6 6 6 26 16 26 16 29 3 29 m 13 14 l 25 14 20 9 22 7 31 16 22 25 20 23 25 18 13 18'
icons.settings=icons.ai
icons.plus='m 15 4 l 18 4 18 14 28 14 28 17 18 17 18 28 15 28 15 17 5 17 5 14 15 14'
icons.info='m 16 1 b 7 1 1 7 1 16 b 1 25 7 31 16 31 b 25 31 31 25 31 16 b 31 7 25 1 16 1 m 14 14 l 14 25 18 25 18 14 m 14 7 l 14 11 18 11 18 7'
local output={}
local clip_region
local function line(s)
    if clip_region then
        s=s:gsub('^(%{[^}]*)(%})',function(tags,ending)return tags..clip_region..ending end,1)
    end
    output[#output+1]=s
end
local function rect(x0,y0,x1,y1,color,alpha)
    if x1<=x0 or y1<=y0 then return end
    line(string.format('{\\rDefault\\an7\\pos(0,0)\\bord0\\shad0\\1c&H%s&\\1a&H%02X&\\p1}m %.2f %.2f l %.2f %.2f %.2f %.2f %.2f %.2f{\\p0}',color or WHITE,alpha or 0,x0,y0,x1,y0,x1,y1,x0,y1))
end
local function circle(x,y,r,color,alpha)
    local k=r*.552285
    line(string.format('{\\rDefault\\an7\\pos(0,0)\\bord0\\shad0\\1c&H%s&\\1a&H%02X&\\p1}m %.2f %.2f b %.2f %.2f %.2f %.2f %.2f %.2f b %.2f %.2f %.2f %.2f %.2f %.2f b %.2f %.2f %.2f %.2f %.2f %.2f b %.2f %.2f %.2f %.2f %.2f %.2f{\\p0}',color,alpha or 0,x-r,y,x-r,y-k,x-k,y-r,x,y-r,x+k,y-r,x+r,y-k,x+r,y,x+r,y+k,x+k,y+r,x,y+r,x-k,y+r,x-r,y+k,x-r,y))
end
local function loading_indicator(w,h,u)
    local phase=mp.get_time()*8
    local radius=21*u
    for i=0,11 do
        local angle=(i/12)*math.pi*2
        local alpha=math.floor(25+200*((i-phase)%12)/12)
        circle(w/2+math.sin(angle)*radius,h/2-math.cos(angle)*radius,3.1*u,ACCENT,alpha)
    end
end
local function round(x0,y0,x1,y1,r,color,alpha)
    if x1<=x0 or y1<=y0 then return end
    r=math.min(r,(x1-x0)/2,(y1-y0)/2);local k=r*.552285
    local path=string.format('m %.2f %.2f l %.2f %.2f b %.2f %.2f %.2f %.2f %.2f %.2f l %.2f %.2f b %.2f %.2f %.2f %.2f %.2f %.2f l %.2f %.2f b %.2f %.2f %.2f %.2f %.2f %.2f l %.2f %.2f b %.2f %.2f %.2f %.2f %.2f %.2f',
        x0+r,y0,x1-r,y0,x1-r+k,y0,x1,y0+r-k,x1,y0+r,x1,y1-r,x1,y1-r+k,x1-r+k,y1,x1-r,y1,x0+r,y1,x0+r-k,y1,x0,y1-r+k,x0,y1-r,x0,y0+r,x0,y0+r-k,x0+r-k,y0,x0+r,y0)
    line(string.format('{\\rDefault\\an7\\pos(0,0)\\bord0\\shad0\\1c&H%s&\\1a&H%02X&\\p1}%s{\\p0}',color,alpha or 0,path))
end
local function clip(x0,y0,x1,y1,fn)
    local old=clip_region
    clip_region=string.format('\\clip(%d,%d,%d,%d)',math.floor(x0),math.floor(y0),math.ceil(x1),math.ceil(y1))
    fn();clip_region=old
end
local function text(x,y,size,s,align,color,bold,max,outline,font)
    if max then s=core.ellipsize(s,max,size) end
    line(string.format('{\\rDefault\\an%d\\pos(%.2f,%.2f)\\fn%s\\fs%d\\b%d\\bord%s\\shad0\\1c&H%s&\\1a&H00&}%s',align or 7,x,y,font or o.font,size,bold and 1 or 0,string.format('%.2f',outline or o.text_outline),color or WHITE,core.escape(s)))
end
show_volume_osd=function(value)
    local level=math.floor(core.clamp(tonumber(value) or num('volume',0),0,num('volume-max',100))+.5)
    local pw,ph=mp.get_osd_size()
    if pw<64 or ph<64 or bool('window-minimized') then return end
    local scale=core.clamp(num('display-hidpi-scale',1),.75,1.5)*o.ui_scale
    local label='音量 '..level..'%'
    local box=core.volume_osd_layout(pw,ph,scale,label,16)
    local saved=output;output={}
    round(box.x,box.y,box.x+box.width,box.y+box.height,7*scale,THEME.panel,8)
    text(box.center_x,box.center_y,16*scale,label,5,THEME.text,true)
    volume_osd.res_x=math.floor(pw+.5);volume_osd.res_y=math.floor(ph+.5)
    volume_osd.data=table.concat(output,'\n');volume_osd:update()
    output=saved
    kill(volume_osd_timer)
    volume_osd_timer=mp.add_timeout(1.15,function()
        volume_osd_timer=nil;volume_osd:remove()
    end)
end
local icon_u=1
local function icon(id,x,y,active,disabled,small)
    local sc=icon_u
    local color=disabled and MUTED or (active or state.hover==id) and ACCENT or WHITE
    local size=(small and METRICS.small_icon_size or METRICS.icon_size)*sc
    line(string.format('{\\rDefault\\an7\\pos(%.2f,%.2f)\\bord0\\shad0\\fscx%d\\fscy%d\\1c&H%s&\\1a&H%02X&\\p1}%s{\\p0}',x-16*size/100,y-16*size/100,size,size,color,disabled and 160 or 0,icons[id] or icons.more))
end
local function thumb(t,x)
    if not core.local_media(mp.get_property('path',''),mp.get_property('stream-open-filename',''),bool('demuxer-via-network')) then hide_thumb();return end
    if not state.thumb or state.thumb.disabled or not state.thumb.available then return end
    if state.thumb_time and math.abs(state.thumb_time-t)<.2 then return end
    kill(thumb_timer)
    thumb_timer=mp.add_timeout(.06,function()
        thumb_timer=nil
        if not core.local_media(mp.get_property('path',''),mp.get_property('stream-open-filename',''),bool('demuxer-via-network')) then hide_thumb();return end
        state.thumb_time=t
        local width=state.thumb.width or 160;local height=state.thumb.height or 90
        local px=core.clamp(x*layout.scale-width/2,12,layout.w*layout.scale-width-12)
        local py=(layout.seek.y0-14*(layout.ui or 1))*layout.scale-height
        cmd('script-message-to','thumbfast','thumb',tostring(t),tostring(math.floor(px)),tostring(math.floor(py)))
    end)
end
local function seek_value(x)
    return core.clamp((x-layout.seek.x0)/(layout.seek.x1-layout.seek.x0),0,1)*num('duration',0)
end
local function draw_menu()
    menu_items=menus.draw(layout,{rect=rect,round=round,circle=circle,text=text,icon=icon,clip=clip,image=draw_cover},buttons)
end
local function hit(x,y)
    local found
    for i=#(buttons or {}),1,-1 do local b=buttons[i];if core.inside(b,x,y) then found=b;break end end
    return menus.hit_override(found,x,y)
end
local mouse_bound=false
local function mouse_button(event)
    if event and event.canceled then state.drag=nil;state.menu_drag=nil;state.pressed=nil;hide_thumb();request_render();return end
    local e=event and event.event or 'press'
    local px,py=mp.get_mouse_pos()
    if layout and core.finite(px) and core.finite(py) then
        state.x=px/layout.scale;state.y=py/layout.scale
    end
    if e=='down' or e=='press' then
        show();local b=hit(state.x,state.y);menus.hover(nil,true)
        if b then
            state.pressed=b.id;state.pressed_key=b.key
            if b.menu and menus.press(b,state.x,state.y) then state.drag='menu'
            elseif b.id=='seek' and num('duration',0)>0 and bool('seekable') then state.drag='seek';hide_thumb()
            elseif b.id=='volume-slider' then state.drag='volume-slider' end
        end
    end
    if state.drag=='volume-slider' and (e=='down' or e=='press') and layout.volume then
        mp.set_property_number('volume',core.clamp((state.x-layout.volume.x0)/(layout.volume.x1-layout.volume.x0),0,1)*num('volume-max',100))
    end
    if e=='up' or e=='press' then
        if state.drag=='seek' then cmd('seek',seek_value(state.x),'absolute+exact')
        elseif state.drag=='menu' then menus.drag(state.x,state.y)
        elseif not state.drag then
            local b=hit(state.x,state.y)
            if b and b.id==state.pressed and b.key==state.pressed_key then
                if b.menu then menus.pick(b) else activate(b.id) end
            end
        end
        state.drag=nil;state.menu_drag=nil;state.pressed=nil;sync_timers()
    end
    request_render()
end
local function wheel(delta)
    if menus.wheel(delta,state.x,state.y) then request_render();return end
    if state.hover=='seek' and bool('seekable') then cmd('seek',delta*5,'relative+exact')
    else volume(delta*o.volume_step) end
    request_render()
end
local function bind_mouse(enabled)
    if mouse_bound==enabled then return end
    mouse_bound=enabled
    if enabled then
        mp.add_forced_key_binding('MBTN_LEFT','player_ui-click',mouse_button,{complex=true})
        mp.add_forced_key_binding('MBTN_LEFT_DBL','player_ui-double',function()end)
        mp.add_forced_key_binding('WHEEL_UP','player_ui-wheel-up',function()wheel(1)end,{repeatable=true})
        mp.add_forced_key_binding('WHEEL_DOWN','player_ui-wheel-down',function()wheel(-1)end,{repeatable=true})
    else
        for _,key in ipairs({'player_ui-click','player_ui-double','player_ui-wheel-up','player_ui-wheel-down'}) do mp.remove_key_binding(key) end
    end
end
local cover_key
remove_cover=function()
    if cover_key then cmd('overlay-remove',62);cover_key=nil end
end
draw_cover=function(path,x,y,w,h,top,bottom)
    if not path or path=='' then return end
    local first=math.max(0,math.ceil((top-y)*360/h))
    local last=math.min(360,math.floor((bottom-y)*360/h))
    if first>=last then return end
    local scale=layout.scale
    local px,py=math.floor(x*scale+.5),math.floor((y+first*h/360)*scale+.5)
    local pw,ph=math.max(1,math.floor(w*scale+.5)),math.max(1,math.floor((last-first)*h/360*scale+.5))
    local key=table.concat({path,px,py,pw,ph,first,last},':')
    cover_seen=true
    if key~=cover_key then
        local _,err=mp.command_native({'overlay-add',62,px,py,path,first*960,'bgra',240,last-first,960,pw,ph})
        if err then remove_cover();return end
        cover_key=key
    end
end
local avatar_key
local function remove_avatar()
    if avatar_key then cmd('overlay-remove',61);avatar_key=nil end
    remove_cover()
end
local function draw_avatar(b,u)
    if not bangumi_account.connected or not bangumi_account.avatar or bangumi_account.avatar=='' then return false end
    local size=math.max(1,math.floor(28*u*layout.scale+.5))
    local x,y=math.floor(b.x*layout.scale-size/2+.5),math.floor(b.y*layout.scale-size/2+.5)
    local key=bangumi_account.avatar..':'..x..':'..y..':'..size
    if avatar_key~=key then
        local _,err=mp.command_native({'overlay-add',61,x,y,bangumi_account.avatar,0,'bgra',64,64,256,size,size})
        if err then remove_avatar();return false end
        avatar_key=key
    end
    return true
end
mp.observe_property('user-data/player_ui/bangumi','native',function(_,value)
    bangumi_account=type(value)=='table' and value or type(value)=='string' and utils.parse_json(value) or {}
    if type(bangumi_account)~='table' then bangumi_account={} end
    remove_avatar();menus.invalidate();sync_timers();request_render()
end)
render=function()
    render_timer=nil
    cover_seen=false
    state.tick=(state.tick or 0)+1
    if bool('window-minimized') then remove_avatar();ui:remove();bind_mouse(false);return end
    local pw,ph=mp.get_osd_size();if pw<=0 or ph<=0 then return end
    local dur,pos=num('duration'),num('time-pos',0)
    local time_width=math.max(core.ass_text_width(core.time(pos),core.metrics.time_font),
        core.ass_text_width(core.time(dur),core.metrics.time_font))+16
    layout=core.layout(pw,ph,num('display-hidpi-scale',1),o.ui_scale,time_width);buttons={};output={};menu_box=nil
    local u=layout.ui;icon_u=u
    local playing=playback_ready and not bool('idle-active',true)
    local net=bool('demuxer-via-network') and o.network_speed and playing
    if loading or bool('paused-for-cache') then loading_indicator(layout.w,layout.h,u) end
    if state.visible then
        local band=210*u
        for i=0,47 do local y=layout.h-band+i*band/48
            rect(0,y,layout.w,y+band/48+.2,THEME.scrim,math.floor(255-140*(i/47)^1.4))
        end
        local seek=layout.seek
        text(layout.margin,seek.y,core.metrics.time_font*u,core.time(pos),4,WHITE,false,nil,0,'Segoe UI')
        text(layout.w-layout.margin,seek.y,core.metrics.time_font*u,core.time(dur),6,WHITE,false,nil,0,'Segoe UI')
        -- Track stays dim, the buffered range is clearly brighter, played is accent.
        rect(seek.x0,seek.y-2.5*u,seek.x1,seek.y+2.5*u,THEME.track,35)
        if dur and dur>0 then
            local cache=prop('demuxer-cache-state',{}) or {}
            for _,range in ipairs(cache['seekable-ranges'] or {}) do
                if core.finite(range.start) and core.finite(range['end']) then
                    rect(seek.x0+core.clamp(range.start/dur,0,1)*(seek.x1-seek.x0),seek.y-2.5*u,
                        seek.x0+core.clamp(range['end']/dur,0,1)*(seek.x1-seek.x0),seek.y+2.5*u,THEME.buffer,35)
                end
            end
            local p=state.drag=='seek' and seek_value(state.x) or pos
            local x=seek.x0+core.clamp(p/dur,0,1)*(seek.x1-seek.x0)
            rect(seek.x0,seek.y-2.5*u,x,seek.y+2.5*u,ACCENT);circle(x,seek.y,9*u,THEME.selected,0);circle(x,seek.y,5*u,ACCENT)
            buttons[#buttons+1]={id='seek',x0=seek.x0,x1=seek.x1,y0=seek.y0,y1=seek.y1}
            if core.inside(seek,state.x,state.y) and not state.menu then
                local target=seek_value(state.x)
                round(state.x-40*u,seek.y0-34*u,state.x+40*u,seek.y0-3*u,8*u,PANEL,8)
                text(state.x,seek.y0-19*u,14*u,core.time(target),5)
                if not state.drag then thumb(target,state.x) end
            else hide_thumb() end
        end
        for _,b in ipairs(layout.controls) do
            local id=b.id;local disabled=false
            local drawid=id
            if id=='play' then drawid=(bool('pause') or bool('idle-active',true)) and 'play' or 'pause' end
            if id=='volume' and bool('mute') then drawid='muted' end
            if id=='fullscreen' and bool('fullscreen') then drawid='restore' end
            if id=='bangumi' and draw_avatar(b,u) then
                if state.hover==id then circle(b.x,b.y,16*u,ACCENT,170) end
            elseif id=='bangumi' and bangumi_account.connected then
                circle(b.x,b.y,14*u,THEME.selected,0)
                text(b.x,b.y,16*u,(bangumi_account.username or 'B'):sub(1,1):upper(),5,WHITE,true)
            elseif id=='speed' then
                text(b.x,b.y+1*u,14*u,string.format('%.1fx',num('speed',1)),5,WHITE,true)
            else
                icon(drawid,b.x,b.y,state.menu==id or id=='settings' and state.parent=='settings',disabled)
                if id=='sub' then text(b.x,b.y+1*u,13*u,'CC',5,PANEL,true) end
            end
            if not disabled then buttons[#buttons+1]=b end
        end
        if layout.volume then
            local b=layout.volume
            local max_volume=math.max(1,num('volume-max',100))
            local fraction=core.clamp(num('volume',0)/max_volume,0,1)
            rect(b.x0,b.y-2.5*u,b.x1,b.y+2.5*u,THEME.track,35)
            rect(b.x0,b.y-2.5*u,b.x0+fraction*(b.x1-b.x0),b.y+2.5*u,ACCENT)
            circle(b.x0+fraction*(b.x1-b.x0),b.y,9*u,THEME.selected,0)
            circle(b.x0+fraction*(b.x1-b.x0),b.y,5*u,ACCENT)
            buttons[#buttons+1]={id='volume-slider',x0=b.x0,x1=b.x1,y0=b.y0,y1=b.y1}
        end
        if net and state.rate and layout.network_rate then
            local b=layout.network_rate
            text((b.x0+b.x1)/2,b.y+1*u,14*u,core.rate(state.rate),5,WHITE,true)
        end
    else hide_thumb();remove_avatar() end
    -- The clock remains at the top while playback controls stay in the bottom row.
    if o.show_clock and playing then
        text(0,0,22*u,os.date('%H:%M'),7,THEME.secondary,false,nil,0.6)
    end
    -- Keep the current title visible while either the bottom controls or a
    -- bottom-row menu is open.
    if playback_ready and (state.visible or state.menu~=nil) then
        local title,detail=core.title_lines(prop('media-title',''),prop('path',''))
        local width=math.max(1,layout.w-2*layout.margin)
        text(layout.margin,layout.title_y,core.metrics.title_font*u,title,7,WHITE,true,width,0,'Microsoft YaHei UI')
        if detail~='' then
            text(layout.margin,layout.detail_y,core.metrics.detail_font*u,detail,7,THEME.secondary,false,width,0,'Microsoft YaHei UI')
        end
    end
    if playback_ready and state.menu then draw_menu() end
    if not cover_seen then remove_cover() end
    local b=hit(state.x,state.y);state.hover=b and b.id or nil
    bind_mouse(state.visible and (b~=nil or state.drag~=nil or state.menu~=nil))
    ui.res_x=math.floor(layout.w+.5);ui.res_y=math.floor(layout.h+.5);ui.data=table.concat(output,'\n')
    if ui.data=='' then
        ui:remove();state.overlay_ok=false;state.overlay_error=nil
    else
        local result,err=ui:update()
        state.overlay_ok=err==nil;state.overlay_error=err
        if err and err~=state.last_overlay_error then require('mp.msg').error('Player UI overlay: '..tostring(err)) end
        state.last_overlay_error=err
    end
    local rows
    if state.menu and menu_items then rows={}
        for _,r in ipairs(menu_items) do rows[#rows+1]={text=r.text,selected=r.selected,disabled=r.disabled,key=r.key,target=r.target,hint=r.hint} end
    end
    local boxes={}
    for _,b in ipairs(menus.boxes) do boxes[#boxes+1]={kind=b.kind,x0=b.x0,x1=b.x1,y0=b.y0,y1=b.y1,total=b.total,view=b.view,offset=b.offset} end
    mp.set_property_native('user-data/player_ui/ui',{visible=state.visible,loading=loading or bool('paused-for-cache'),menu=state.menu or '',width=pw,height=ph,
        controls=buttons,scale=layout.scale,hover=state.hover,mouse_x=state.x,mouse_y=state.y,overlay_ok=state.overlay_ok,overlay_error=state.overlay_error,menu_boxes=boxes,version='1.3.0',network_rate=state.rate,rows=rows,
        performance=state.menu=='stats' and {fps=state.fps,cpu=state.cpu,memory=state.memory} or nil})
end
request_render=function()if not render_timer then render_timer=mp.add_timeout(.035,render) end end
local menu_escape_bound=false
local function schedule_clock()
    clock_timer=nil
    if not o.show_clock or not playback_ready or bool('idle-active',true) or bool('window-minimized') then return end
    clock_timer=mp.add_timeout(60-(os.time()%60),function()
        clock_timer=nil
        request_render()
        schedule_clock()
    end)
end
sync_timers=function()
    local needs_loading=not bool('window-minimized') and (loading or bool('paused-for-cache'))
    if needs_loading and not loading_timer then
        loading_timer=mp.add_periodic_timer(1/30,render)
    elseif not needs_loading and loading_timer then
        loading_timer:kill();loading_timer=nil
    end
    local menu_open=state.menu~=nil
    if state.menu~='stats' and metrics.reset then metrics.reset() end
    if menu_open~=menu_escape_bound then
        menu_escape_bound=menu_open
        if menu_open then mp.add_forced_key_binding('ESC','player_ui-menu-escape',escape)
        else mp.remove_key_binding('player_ui-menu-escape') end
    end
    local danmaku=prop('user-data/player_ui/danmaku',{}) or {}
    local bangumi=bangumi_account or {}
    local menu_loading=state.menu=='danmaku' and danmaku.autoload_state=='loading'
        or state.menu=='bangumi' and (bangumi.resolving or bangumi.authorizing)
    local need=not bool('window-minimized') and (state.visible and not bool('pause') and not bool('idle-active',true)
        or state.menu=='stats' or menu_loading)
    if need and not pulse then pulse=mp.add_periodic_timer(.25,function()
        if state.menu=='stats' then actual_sample();menus.invalidate() end
        request_render()
    end)
    elseif not need and pulse then pulse:kill();pulse=nil end
    local net=not bool('window-minimized') and playback_ready and o.network_speed
        and bool('demuxer-via-network') and not bool('idle-active',true)
    if net and not network_timer then
        state.rate=bool('demuxer-cache-idle') and 0 or num('cache-speed',0)
        network_timer=mp.add_periodic_timer(.5,function()
            state.rate=bool('demuxer-cache-idle') and 0 or num('cache-speed',0);request_render()
        end)
    elseif not net and network_timer then network_timer:kill();network_timer=nil;state.rate=nil end
    local want_clock=o.show_clock and playback_ready and not bool('window-minimized') and not bool('idle-active',true)
    if want_clock and not clock_timer then schedule_clock() end
    if not want_clock and clock_timer then clock_timer:kill();clock_timer=nil end
end
local function pointer_over_hud()
    if not layout then return false end
    local x,y=mp.get_mouse_pos()
    if not core.finite(x) or not core.finite(y) then return false end
    x=x/layout.scale;y=y/layout.scale
    return y>=layout.seek.y0 and y<=layout.h or hit(x,y)~=nil
end
local function hide()
    hide_timer=nil
    if state.menu or state.drag or bool('pause') or bool('idle-active',true) then return end
    if pointer_over_hud() then hide_timer=mp.add_timeout(o.hide_timeout,hide);return end
    state.visible=false;state.hover=nil;bind_mouse(false);hide_thumb();sync_timers();request_render()
end
show=function()
    if not playback_ready then
        state.visible=false;state.menu=nil;kill(hide_timer);hide_timer=nil
        sync_timers();request_render();return
    end
    state.visible=true;kill(hide_timer);hide_timer=mp.add_timeout(o.hide_timeout,hide)
    sync_timers();request_render()
end
local function mouse_move()
    if not playback_ready then return end
    local x,y=mp.get_mouse_pos()
    if not layout then local w,h=mp.get_osd_size();layout=core.layout(w,h,num('display-hidpi-scale',1),o.ui_scale) end
    state.x=x/layout.scale;state.y=y/layout.scale
    if state.drag=='volume-slider' and layout.volume then
        mp.set_property_number('volume',core.clamp((state.x-layout.volume.x0)/(layout.volume.x1-layout.volume.x0),0,1)*num('volume-max',100))
    elseif state.drag=='menu' then menus.drag(state.x,state.y) end
    show();local b=hit(state.x,state.y);state.hover=b and b.id or nil;menus.hover(b)
    bind_mouse(b~=nil or state.drag~=nil or state.menu~=nil)
end
mp.add_forced_key_binding('mouse_move','player_ui-move',mouse_move)
mp.add_forced_key_binding('mouse_leave','player_ui-leave',function()
    if not playback_ready then return end
    if pointer_over_hud() then
        local x,y=mp.get_mouse_pos();state.x=x/layout.scale;state.y=y/layout.scale
        local b=hit(state.x,state.y);state.hover=b and b.id or nil;menus.hover(b);show()
        return
    end
    menus.hover(nil);state.x=-1;state.y=-1;state.hover=nil;state.drag=nil;state.pressed=nil;hide_thumb();hide()
end)
mp.add_key_binding('UP','player_ui-volume-up',function()volume(o.volume_step)end,{repeatable=true})
mp.add_key_binding('DOWN','player_ui-volume-down',function()volume(-o.volume_step)end,{repeatable=true})
mp.add_key_binding('ESC','player_ui-escape',escape)
mp.register_script_message('player_ui-menu',function(kind)
    if menus.allowed[kind] then open_menu(kind) end
end)
mp.register_script_message('player_ui-show',show)
mp.register_script_message('player_ui-hide',function()menus.close();state.hover=nil;hide()end)
mp.register_script_message('thumbfast-info',function(json)local info=utils.parse_json(json);if type(info)=='table' then state.thumb=info end end)
mp.add_key_binding(nil,'visibility',function()if state.visible then state.menu=nil;hide() else show() end end)
local last_volume=num('volume',100)
for _,p in ipairs({'pause','idle-active','paused-for-cache','demuxer-via-network','window-minimized','fullscreen',
    'volume','mute','speed','sid','sub-visibility','sub-delay','sub-scale','sub-pos','audio-delay','display-hidpi-scale','keepaspect','panscan','video-unscaled','track-list','playlist','playlist-pos','media-title','duration','video-params','osd-dimensions',
    'user-data/player_ui/danmaku','user-data/animejanai/requested-slot'}) do
    mp.observe_property(p,'native',function(_,value)
        -- Property changes can update the title or the open menu.
        if state.menu then menus.invalidate() end
        if p=='volume' and core.finite(tonumber(value)) then
            local current=tonumber(value)
            if playback_ready and last_volume and math.abs(current-last_volume)>.01 then show_volume_osd(current) end
            last_volume=current
        end
        if p=='pause' then samples:reset();show() else sync_timers();request_render() end
    end)
end
mp.register_event('start-file',function()
    remove_avatar();playback_ready=false;volume_osd:remove();kill(volume_osd_timer);loading=true;state.visible=false;state.rate=nil;state.menu=nil
    menus.close();state.drag=nil;state.pressed=nil;state.fps=nil
    samples:reset();hide_thumb();sync_timers();request_render()
end)
local function paused_video_ready()
    if bool('pause') and bool('vo-configured') and not bool('idle-active',true) then
        playback_ready=true;loading=false;show()
    end
end
mp.observe_property('vo-configured','bool',function(_,configured)
    if configured then paused_video_ready() end
end)
mp.register_event('file-loaded',function()
    samples:reset();paused_video_ready();sync_timers();request_render()
end)
mp.register_event('playback-restart',function()
    playback_ready=true;loading=false;show()
end)
mp.register_event('seek',function()samples:reset()end)
mp.register_event('end-file',function()
    remove_avatar();playback_ready=false;loading=false;state.visible=false;state.menu=nil;state.rate=nil
    hide_thumb();sync_timers();request_render()
end)
mp.register_event('shutdown',function()
    if metrics.reset then metrics.reset() end
    remove_avatar();menus.shutdown();kill(render_timer);kill(hide_timer);kill(pulse);kill(network_timer);kill(thumb_timer);kill(clock_timer);kill(loading_timer);kill(volume_osd_timer);ui:remove();volume_osd:remove();bind_mouse(false)
end)
show()

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

-- Online acquisition with DanmakuFactory and the native secondary ASS track.
local mp=require 'mp'
local utils=require 'mp.utils'
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
local online=(function()
-- inlined module: player_ui_danmaku_online.lua
-- Pure online danmaku helpers. The caller owns mpv state and asynchronous jobs.
local M = {}

local alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/'
local UTF8_CODEPOINT='[%z\1-\127\194-\244][\128-\191]*'

function M.limit_text(value,limit,clean)
    value=tostring(value or '')
    limit=math.max(0,math.floor(tonumber(limit) or 0))
    if limit==0 then return '' end
    local out,count={},0
    for character in value:gmatch(UTF8_CODEPOINT) do
        if count==limit then out[limit]='…';break end
        count=count+1;out[count]=character
    end
    value=table.concat(out)
    return clean and clean(value) or value
end

function M.episode_id(value)
    if type(value) ~= 'string' and type(value) ~= 'number' then return nil end
    local id=tostring(value)
    if #id<1 or #id>20 or not id:match('^%d+$') then return nil end
    return id
end

function M.base64(value)
    local out = {}
    for i = 1, #value, 3 do
        local a, b, c = value:byte(i, i + 2)
        b, c = b or 0, c or 0
        local n = a * 65536 + b * 256 + c
        out[#out + 1] = alphabet:sub(math.floor(n / 262144) % 64 + 1, math.floor(n / 262144) % 64 + 1)
        out[#out + 1] = alphabet:sub(math.floor(n / 4096) % 64 + 1, math.floor(n / 4096) % 64 + 1)
        out[#out + 1] = i + 1 <= #value and alphabet:sub(math.floor(n / 64) % 64 + 1, math.floor(n / 64) % 64 + 1) or '='
        out[#out + 1] = i + 2 <= #value and alphabet:sub(n % 64 + 1, n % 64 + 1) or '='
    end
    return table.concat(out)
end

local function ps_b64(value)
    return "[Convert]::FromBase64String('" .. M.base64(value) .. "')"
end

local function safe_url(url)
    return type(url) == 'string' and not url:find('[%z\r\n]')
        and url:lower():match('^https?://[^/%?#]+') ~= nil
end

-- Build a PowerShell command containing only ASCII and base64 data. This avoids
-- Windows -Command quoting and UTF-8 byte/codepoint confusion for CJK titles.
function M.request_command(url, body, options)
    options = options or {}
    assert(safe_url(url), 'invalid HTTP URL')
    assert(body == nil or type(body) == 'string', 'request body must be a string')

    local timeout = math.floor(math.max(1, math.min(120, tonumber(options.timeout) or 30)))
    local response_limit = math.floor(math.max(65536, math.min(16 * 1024 * 1024,
        tonumber(options.response_limit) or 8 * 1024 * 1024)))
    local parts = {
        "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';try{",
        '[Console]::OutputEncoding=[Text.Encoding]::UTF8;',
        '$utf8=[System.Text.UTF8Encoding]::new($false);',
        '$url=$utf8.GetString(' .. ps_b64(url) .. ');',
        '$request=[System.Net.HttpWebRequest]::Create($url);',
        "$request.UserAgent='mpv-AnimeFusion/1.1.9';",
        "$request.Timeout=" .. tostring(timeout * 1000) .. ';',
        "$request.ReadWriteTimeout=" .. tostring(timeout * 1000) .. ';',
        '[System.Net.ServicePointManager]::SecurityProtocol=[System.Net.SecurityProtocolType]::Tls12;',
    }

    if body ~= nil then
        parts[#parts + 1] = "$request.Method='POST';$request.ContentType='application/json; charset=utf-8';"
        parts[#parts + 1] = '$body=$utf8.GetString(' .. ps_b64(body) .. ');$bytes=$utf8.GetBytes($body);'
        parts[#parts + 1] = '$request.ContentLength=$bytes.Length;'
    else
        parts[#parts + 1] = "$request.Method='GET';"
    end

    local app_id = tostring(options.app_id or '')
    local app_secret = tostring(options.app_secret or '')
    if app_id ~= '' and app_secret ~= '' then
        local path = url:match('^https?://[^/]+(/[^?#]*)') or '/'
        parts[#parts + 1] = '$appId=$utf8.GetString(' .. ps_b64(app_id) .. ');'
        parts[#parts + 1] = '$secret=$utf8.GetString(' .. ps_b64(app_secret) .. ');'
        parts[#parts + 1] = '$path=$utf8.GetString(' .. ps_b64(path) .. ');'
        parts[#parts + 1] = '$timestamp=[string][DateTimeOffset]::UtcNow.ToUnixTimeSeconds();'
        parts[#parts + 1] = '$signing=$appId+$timestamp+$path+$secret;'
        parts[#parts + 1] = '$sha=[Security.Cryptography.SHA256]::Create();'
        parts[#parts + 1] = '$signature=[Convert]::ToBase64String($sha.ComputeHash($utf8.GetBytes($signing)));'
        parts[#parts + 1] = "$request.Headers.Add('X-AppId',$appId);"
        parts[#parts + 1] = "$request.Headers.Add('X-Timestamp',$timestamp);"
        parts[#parts + 1] = "$request.Headers.Add('X-Signature',$signature);"
    end

    if body ~= nil then
        parts[#parts + 1] = '$requestStream=$request.GetRequestStream();'
        parts[#parts + 1] = '$requestStream.Write($bytes,0,$bytes.Length);$requestStream.Dispose();'
    end

    parts[#parts + 1] = '$response=$request.GetResponse();try{$stream=$response.GetResponseStream();'
    parts[#parts + 1] = '$decoder=$utf8.GetDecoder();$buffer=New-Object byte[] 8192;$characters=New-Object char[] 8192;$total=0;'
    parts[#parts + 1] = 'while(($read=$stream.Read($buffer,0,$buffer.Length)) -gt 0){'
    parts[#parts + 1] = '$total+=$read;if($total -gt ' .. tostring(response_limit) .. "){throw 'Response exceeds configured limit'};"
    parts[#parts + 1] = '$count=$decoder.GetChars($buffer,0,$read,$characters,0,$false);[Console]::Out.Write($characters,0,$count)};'
    parts[#parts + 1] = '$count=$decoder.GetChars($buffer,0,0,$characters,0,$true);[Console]::Out.Write($characters,0,$count);'
    parts[#parts + 1] = '[Console]::Out.Flush();$stream.Dispose()}finally{$response.Dispose()}'
    parts[#parts + 1] = "}catch{$exception=$_.Exception;while($exception.InnerException){$exception=$exception.InnerException};$message=$exception.Message;$failed=$exception.Response;"
    parts[#parts + 1] = "if($failed){try{$reader=New-Object IO.StreamReader($failed.GetResponseStream(),$utf8);"
    parts[#parts + 1] = "$buffer=New-Object char[] 4096;$count=$reader.Read($buffer,0,$buffer.Length);"
    parts[#parts + 1] = "$body=New-Object string($buffer,0,$count);$reader.Dispose();if($body.Trim()){"
    parts[#parts + 1] = "try{$errorBody=$body|ConvertFrom-Json;$detail=$errorBody.errorMessage;if(-not $detail){$detail=$errorBody.message};"
    parts[#parts + 1] = "if(-not $detail){$detail=$errorBody.error};if($detail){$message=[string]$detail}else{$message=$body}}catch{$message=$body}"
    parts[#parts + 1] = "}}catch{}finally{$failed.Dispose()}};[Console]::Error.WriteLine($message);exit 1}"

    return table.concat(parts)
end

-- Dandanplay identifies local files by the MD5 of their first 16 MiB. Keep the
-- read bounded and pass the path as base64 so arbitrary filenames cannot alter
-- the PowerShell command line.
function M.hash_command(path)
    assert(type(path) == 'string' and path ~= '', 'file path must not be empty')
    return table.concat({
        "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';try{",
        '$utf8=[System.Text.UTF8Encoding]::new($false);',
        '$path=$utf8.GetString(' .. ps_b64(path) .. ');',
        '$stream=[System.IO.File]::OpenRead($path);try{',
        '$length=[int][Math]::Min($stream.Length,16777216);$buffer=New-Object byte[] $length;$read=0;',
        'while($read -lt $length){$count=$stream.Read($buffer,$read,$length-$read);if($count -le 0){break};$read+=$count};',
        '$md5=[Security.Cryptography.MD5]::Create();try{',
        '$digest=$md5.ComputeHash($buffer,0,$read);',
        "[Console]::Out.Write(([BitConverter]::ToString($digest)).Replace('-','').ToLowerInvariant())",
        '}finally{$md5.Dispose()}}finally{$stream.Dispose()}',
        "}catch{[Console]::Error.WriteLine($_.Exception.Message);exit 1}",
    })
end

local function trim(value)
    return tostring(value or ''):match('^%s*(.-)%s*$')
end

function M.parse_server_entry(value)
    local entry = trim(value)
    if entry == '' then return nil, '线路地址为空' end
    local split = entry:find('[|#]')
    local url, note
    if split then
        url, note = trim(entry:sub(1, split - 1)), trim(entry:sub(split + 1))
        if note == '' then note = nil end
    else
        url = entry
    end
    url = url:gsub('/+$', '')
    local authority = url:match('^https?://([^/%?#]+)')
    if note and (#note>80 or note:find(',',1,true)) then
        return nil, '线路备注不能超过 80 个字符或包含逗号'
    end
    if not authority or authority:find('@', 1, true) or url:find('%s')
        or url:find('[?#]') or url:find(',',1,true) or #url>2048 then
        return nil, '线路必须是无查询参数的 HTTP 或 HTTPS 基础地址'
    end
    return {url = url, note = note}
end

function M.parse_servers(value)
    local out, seen = {}, {}
    for entry in tostring(value or ''):gmatch('[^,]+') do
        local server = M.parse_server_entry(entry)
        if server and not seen[server.url] and #out < 20 then
            seen[server.url] = true
            out[#out + 1] = server
        end
    end
    return out
end

function M.serialize_servers(servers)
    local out = {}
    for _, server in ipairs(servers or {}) do
        out[#out + 1] = server.note and (server.url .. '|' .. server.note) or server.url
    end
    return table.concat(out, ',')
end

function M.urlencode(value)
    local out = {}
    for i = 1, #tostring(value or '') do
        local byte = tostring(value or ''):byte(i)
        local safe = byte >= 48 and byte <= 57 or byte >= 65 and byte <= 90
            or byte >= 97 and byte <= 122 or byte == 45 or byte == 46 or byte == 95 or byte == 126
        out[#out + 1] = safe and string.char(byte) or string.format('%%%02X', byte)
    end
    return table.concat(out)
end

function M.match_body(name,file_size,video_duration,file_hash)
    local value = tostring(name or ''):gsub('^.*[/\\]', ''):gsub('%.[%w%d]+$', '')
    value=M.limit_text(value,256)
    local escaped = value:gsub('[%z\1-\31\\"]', function(char)
        local byte = char:byte()
        if char == '\\' then return '\\\\' end
        if char == '"' then return '\\"' end
        if char == '\b' then return '\\b' end
        if char == '\f' then return '\\f' end
        if char == '\n' then return '\\n' end
        if char == '\r' then return '\\r' end
        if char == '\t' then return '\\t' end
        return string.format('\\u%04x', byte)
    end)
    local fields={'"fileName":"'..escaped..'"'}
    if type(file_hash)=='string' and #file_hash==32 and file_hash:match('^%x+$') then
        fields[#fields+1]='"fileHash":"'..file_hash:lower()..'"'
    end
    local size=tonumber(file_size)
    if size and size==size and size>0 and size<9007199254740992 then
        fields[#fields+1]='"fileSize":'..tostring(math.floor(size))
    end
    local duration=tonumber(video_duration)
    if duration and duration==duration and duration>0 and duration<=2147483647 then
        fields[#fields+1]='"videoDuration":'..tostring(math.floor(duration))
    end
    return '{'..table.concat(fields,',')..'}'
end

function M.query_name(path, media_title, clean)
    path = tostring(path or '')
    if path ~= '' and not path:match('^%a[%w+.-]*://') then
        local name = path:gsub('^.*[/\\]', ''):gsub('%?.*$', '')
        if name ~= '' and not name:match('^[Ss]tream%.[%w]+$') then return name end
    end
    local title = tostring(media_title or ''):gsub('%s+$', '')
    return clean and clean(title) or title
end

local CHINESE_SEASONS={'一','二','三','四','五','六','七','八','九','十','十一','十二'}
local ROMAN_SEASONS={'Ⅰ','Ⅱ','Ⅲ','Ⅳ','Ⅴ','Ⅵ','Ⅶ','Ⅷ','Ⅸ','Ⅹ','Ⅺ','Ⅻ'}
local ASCII_SEASONS={'I','II','III','IV','V','VI','VII','VIII','IX','X','XI','XII'}
local function title_width(value)
    return tostring(value or ''):gsub(UTF8_CODEPOINT,function(character)
        local a,b,c=character:byte(1,3)
        if a==239 and b and c then
            local code=(a-224)*4096+(b-128)*64+c-128
            if code>=65281 and code<=65374 then return string.char(code-65248) end
        end
        return character=='　' and ' ' or character
    end)
end
local SEASON_EPISODE='[Ss](%d+)[ ._%-]*[Ee](%d+%.?%d*)'
local function season_marker(title)
    for _,pattern in ipairs({SEASON_EPISODE,'第%s*(%d+)%s*季',
        '%f[%a][Ss][Ee][Aa][Ss][Oo][Nn]%s+(%d+)%f[%W]','%f[%w][Ss](%d+)%f[%W]'}) do
        local first,last,number=title:find(pattern)
        if number and tonumber(number)>0 then return tonumber(number),first,last end
    end
    for n,word in ipairs(CHINESE_SEASONS) do
        local first,last=title:find('第'..word..'季',1,true)
        if first then return n,first,last end
    end
    for n,word in ipairs(ROMAN_SEASONS) do
        local first,last=title:find(word,1,true)
        if first then return n,first,last end
    end
    for n=2,#ASCII_SEASONS do
        local word=ASCII_SEASONS[n]
        if #word>1 then
            local first,last=title:find('%f[%a]'..word..'%f[%A]')
            if first then return n,first,last end
        end
    end
    return nil
end
function M.season_number(title)
    local number=season_marker(title_width(title))
    return number
end
local function strip_season(title)
    local _,first,last=season_marker(title)
    return first and title:sub(1,first-1):gsub('%s+$','')..title:sub(last+1) or title
end

local function strip_year(title)
    return title:gsub('%s*%(%d%d%d%d%)%s*$',''):gsub('%s*（%d%d%d%d）%s*$','')
        :gsub('[%s%-]+$','')
end
local VIDEO_EXTENSIONS={mkv=true,mp4=true,avi=true,mov=true,wmv=true,flv=true,webm=true,
    m4v=true,mpg=true,mpeg=true,ts=true,m2ts=true,mts=true,vob=true,ogv=true,rm=true,rmvb=true}
local function strip_extension(title)
    local extension=title:match('%.([%a%d]+)$')
    if extension and VIDEO_EXTENSIONS[extension:lower()] then return title:sub(1,-#extension-2) end
    return title
end

function M.episode_query(title)
    title=strip_extension(title_width(title)):gsub('^%s*%[[^%]]+%]%s*','')
    for _=1,8 do
        local stripped=title:gsub('%s*%[[^%]]+%]%s*$','')
        if stripped==title then break end
        title=stripped
    end
    local season=M.season_number(title)
    local start,finish,explicit_season,number=title:find(SEASON_EPISODE)
    if explicit_season and tonumber(explicit_season)==0 then return nil,nil,nil end
    local episode=start and tonumber(number)
    local anime=start and strip_year(title:sub(1,start-1)) or nil
    if not episode then
        local prefix,n
        for _,word in ipairs({'集','话','話'}) do
            prefix,n=title:match('^(.-)%s*第%s*(%d+%.?%d*)%s*'..word)
            if prefix then break end
        end
        if not prefix then prefix,n=title:match('^(.-)%s*%f[%a][Ee][Pp]?%s*(%d+%.?%d*)') end
        if not prefix then prefix,n=title:match('^(.-)%s*#%s*(%d+)') end
        if not prefix then prefix,n=title:match('^(.-)%s*%-%s*(%d+%.?%d*)%s*$') end
        for _,separator in ipairs({'—','–','~'}) do
            if not prefix then prefix,n=title:match('^(.-)%s*'..separator..'%s*(%d+%.?%d*)%s*$') end
        end
        if prefix then anime=strip_year(prefix);episode=tonumber(n) end
    end
    if not anime or anime=='' or not episode or episode<=0 then return nil,nil,season end
    local prefix_season=M.season_number(anime)
    if prefix_season and season and prefix_season~=season then return nil,nil,nil end
    return strip_season(anime):gsub('%s+$',''),episode,season
end

function M.search_keyword(title)
    title = M.limit_text(title,512):gsub('[%z\1-\8\11\12\14-\31\127]', '')
    title = title:gsub('^%s*%[[^%]]+%]%s*', '')
    title = strip_extension(title)
    local anime = M.episode_query(title)
    if anime then return M.show_info(anime).series end
    title = title:gsub('%s*%[[^%]]*[0-9]+[pPkK][^%]]*%]%s*$', '')
    title = title:gsub('%s*%[[^%]]*[Bb][Dd][^%]]*%]%s*$', '')
    title = title:gsub('[%s._%-]+$', ''):gsub('^%s+', ''):gsub('%s+$', '')
    return M.limit_text(M.show_info(title).series,120)
end

local function decode_json(text, parse_json)
    if type(text) ~= 'string' or text == '' then return nil, '服务器响应为空' end
    text = text:gsub('^\239\187\191', '')
    local ok, data = pcall(parse_json, text)
    if not ok or type(data) ~= 'table' then return nil, '服务器返回了无效 JSON' end
    if data.errorCode ~= nil and tonumber(data.errorCode) ~= 0 then
        local message = type(data.errorMessage) == 'string' and data.errorMessage or ''
        return nil, message ~= '' and message or ('接口错误 ' .. tostring(data.errorCode))
    end
    if data.success == false then
        return nil, M.limit_text(data.errorMessage or '接口返回失败',180)
    end
    return data
end

function M.match_result(text, parse_json)
    local data, err = decode_json(text, parse_json)
    if not data then return nil, err end
    if data.isMatched ~= true or type(data.matches) ~= 'table' then return nil, nil end
    local match = data.matches[1]
    if type(match) ~= 'table' or match.episodeId == nil then return nil, nil end
    local id=M.episode_id(match.episodeId)
    if not id then return nil, nil end
    return {episodeId=id,animeId=M.episode_id(match.animeId),
        animeTitle=M.limit_text(match.animeTitle,120),episodeTitle=M.limit_text(match.episodeTitle,80)}
end

local PLATFORM_NAMES={tencent='腾讯视频',qq='腾讯视频',iqiyi='爱奇艺',qiyi='爱奇艺',
    bilibili='哔哩哔哩',youku='优酷',migu='咪咕',dandan='弹弹play',
    animeko='Animeko',leshi='乐视',imgo='芒果TV'}
local function platform_name(value)
    value=M.limit_text(value,32)
    return PLATFORM_NAMES[value:lower()] or value
end
function M.episode_platform(key,label)
    local value=key and key~='' and key or tostring(label or ''):match('【([^】]+)】')
        or tostring(label or ''):match('%[([^%]]+)%]')
    return platform_name(value or '未知平台')
end
local function image_url(value)
    if type(value)~='string' or #value>2048 or value:find('[%z\1-\31]')
        or not value:match('^https?://[^/%?#]+') then return nil end
    return value
end
function M.show_info(value,clean)
    local display=M.limit_text(value,120,clean)
    local title=title_width(display)
    local year=tonumber(title:match('%((%d%d%d%d)%)'))
    local season=M.season_number(title)
    local part=tonumber(title:match('[Pp]art[ ._-]*(%d+)') or title:match('第%s*(%d+)%s*部分'))
    for n,word in ipairs(CHINESE_SEASONS) do
        if title:find('第'..word..'部分',1,true) then part=n;break end
    end
    local kind=(title:find('电影',1,true) or title:find('剧场版',1,true)) and '电影' or '剧集'
    local label=display:gsub('%s*[Ff][Rr][Oo][Mm]%s+.*$',''):gsub('【.-】','')
    label=label:gsub('%s+$','')
    local series=strip_season(title_width(label)):gsub('%s*%(%d%d%d%d%)','')
    series=series:gsub('%s*第%s*%d+%s*季',''):gsub('%s*[Pp]art[ ._-]*%d+','')
    for _,word in ipairs(CHINESE_SEASONS) do
        series=series:gsub('第'..word..'季',''):gsub('第'..word..'部分','')
    end
    series=series:gsub('%s*第%s*%d+%s*部分',''):gsub('%s+$','')
    return {label=label,series=series,season=season,part=part,year=year,kind=kind}
end

local function title_variants(value)
    local out,seen={},{}
    local function add(title)
        title=trim(title)
        if title~='' and not seen[title] then seen[title]=true;out[#out+1]=title end
    end
    value=title_width(value)
    add(value)
    for part in value:gsub('、',';'):gsub('；',';'):gmatch('[^;\r\n]+') do
        add(part)
        local prefix,translated=trim(part):match('^([%a][%w ._\'&:+%-]*%s+)(.+)$')
        if prefix and translated then
            local first=translated:match(UTF8_CODEPOINT)
            local a,b,c
            if first then a,b,c=first:byte(1,3) end
            if a and a>=224 and a<=239 and b and c then
                local code=(a-224)*4096+(b-128)*64+c-128
                if code>=0x3400 and code<=0x9fff or code>=0x3040 and code<=0x30ff then add(translated) end
            end
        end
    end
    return out
end

local TITLE_SEPARATORS={}
for character in ('　：；，。！？、·・～〜—–…“”‘’「」『』（）【】《》〈〉〔〕［］｛｝'):gmatch(UTF8_CODEPOINT) do
    TITLE_SEPARATORS[character]=true
end
local function identity(value)
    local out={}
    for character in title_width(value):lower():gmatch(UTF8_CODEPOINT) do
        if not TITLE_SEPARATORS[character] and not character:match('^[%s%p]$') then
            out[#out+1]=character
        end
    end
    return table.concat(out)
end

function M.series_source_key(media_name)
    local anime,episode,season=M.episode_query(media_name)
    if not anime or not episode then return nil end
    local info=M.show_info(anime)
    local series=identity(info.series)
    if series=='' then return nil end
    -- Unknown seasons remain distinct; a remembered source must never guess one.
    return series..'/s'..tostring(season or 0)..'/p'..tostring(info.part or 0)
end

local function movie_kind(kind)
    kind=tostring(kind or ''):lower()
    return kind:find('电影',1,true) or kind:find('劇場版',1,true)
        or kind:find('剧场版',1,true) or kind:find('movie',1,true)
end

local function season_series_kind(kind)
    local value=tostring(kind or ''):lower()
    return not movie_kind(kind) and not value:find('ova',1,true)
        and not value:find('oad',1,true) and not value:find('special',1,true)
        and not value:find('特别',1,true) and not value:find('特別',1,true)
        and not value:find('特典',1,true)
end
local function numbered_series(value)
    local prefix,number=title_width(value):match('^(.-)%s*(%d+)%s*$')
    number=tonumber(number)
    if not number or number<2 or number>99 or identity(prefix)=='' then return nil end
    return trim(prefix),number
end
local function infer_numbered_seasons(shows)
    local originals={}
    for _,show in ipairs(shows) do
        if (not show.season or show.season==1) and not show.season_ambiguous
            and not show.part_ambiguous and season_series_kind(show.kind) then
            local names={show.series}
            local numbered=numbered_series(show.series)~=nil
            for _,alias in ipairs(show.aliases) do
                names[#names+1]=alias
                numbered=numbered or numbered_series(alias)~=nil
            end
            if not numbered then
                for _,name in ipairs(names) do
                    local key=identity(name)
                    local other=originals[key]
                    if key~='' and other~=false then
                        if other and (identity(other.series)~=identity(show.series)
                            or other.year~=show.year or other.part~=show.part) then originals[key]=false
                        else originals[key]=show end
                    end
                end
            end
        end
    end
    for _,show in ipairs(shows) do
        if not show.season and not show.season_ambiguous and not show.part_ambiguous
            and season_series_kind(show.kind) then
            local names={show.series}
            for _,alias in ipairs(show.aliases) do names[#names+1]=alias end
            local original,season,conflicting=nil,nil,false
            for _,name in ipairs(names) do
                local prefix,number=numbered_series(name)
                local base=prefix and originals[identity(prefix)]
                if base and base~=show and base.part==show.part
                    and (not base.year or not show.year or show.year>base.year) then
                    if original and (original~=base or season~=number) then conflicting=true end
                    original,season=base,number
                end
            end
            if conflicting then show.season_ambiguous=true
            elseif original then
                show.season=season;show.series=original.series
                -- The original's aliases identify the same series in other languages.
                -- Keep the sequel's display title and platform IDs unchanged.
                for _,alias in ipairs(original.aliases) do show.aliases[#show.aliases+1]=alias end
            end
        end
    end
end

function M.search_results(text, keyword, parse_json, clean)
    local data, err = decode_json(text, parse_json)
    if not data then return nil, err end
    if type(data.animes) ~= 'table' then return {}, nil end
    local out={}
    for _, anime in ipairs(data.animes) do
        if type(anime)=='table' then
            local id=M.episode_id(anime.animeId or anime.bangumiId)
            if id then
                local info=M.show_info(anime.animeTitle or '',clean)
                local content_type=M.limit_text(anime.typeDescription or anime.type or '',32,clean)
                if content_type~='' then info.kind=content_type end
                if not info.year then info.year=tonumber(tostring(anime.startDate or ''):match('^(%d%d%d%d)')) end
                local aliases={}
                local alias_season,conflicting_seasons=nil,false
                local alias_part,conflicting_parts=nil,false
                for alias_index,alias in ipairs(type(anime.aliases)=='table' and anime.aliases or {}) do
                    if alias_index>48 then break end
                    if type(alias)=='string' then
                        for _,variant in ipairs(title_variants(alias)) do
                            local alias_info=M.show_info(variant,clean)
                            if alias_info.series~='' and (not info.season or not alias_info.season or info.season==alias_info.season)
                                and (not info.part or not alias_info.part or info.part==alias_info.part) then
                                aliases[#aliases+1]=alias_info.series
                            end
                            if alias_info.season then
                                if alias_season and alias_season~=alias_info.season then conflicting_seasons=true end
                                alias_season=alias_info.season
                            end
                            if alias_info.part then
                                if alias_part and alias_part~=alias_info.part then conflicting_parts=true end
                                alias_part=alias_info.part
                            end
                        end
                    end
                end
                if not info.season and not conflicting_seasons then info.season=alias_season end
                if not info.part and not conflicting_parts then info.part=alias_part end
                if info.series=='' and aliases[1] then info.series=aliases[1];info.label=aliases[1] end
                local platforms,platform_keys={},{}
                local function add_platform(entry,episode_count)
                    local platform_id=M.episode_id(entry.animeId)
                    if not platform_id then return end
                    local source=M.limit_text(entry.source or '',32)
                    local count=tonumber(episode_count) or 0
                    if platform_keys[source] then
                        local current=platforms[platform_keys[source]]
                        if count>current.episode_count then
                            current.id=platform_id;current.episode_count=count
                        end
                        return
                    end
                    platforms[#platforms+1]={id=platform_id,key=source,name=platform_name(source),
                        episode_count=count,
                        image_url=image_url(entry.imageUrl)}
                    platform_keys[source]=#platforms
                end
                add_platform(anime,anime.episodeCount)
                for _,child in ipairs(type(anime.mergedChildren)=='table' and anime.mergedChildren or {}) do
                    if type(child)=='table' then add_platform(child,child.episodes) end
                end
                out[#out+1]={id=id,label=info.label,series=info.series,season=info.season,
                    part=info.part,year=info.year,kind=info.kind,
                    episode_count=tonumber(anime.episodeCount) or 0,
                    image_url=image_url(anime.imageUrl),platforms=platforms,aliases=aliases,
                    season_ambiguous=not info.season and conflicting_seasons,
                    part_ambiguous=not info.part and conflicting_parts}
            end
        end
    end
    infer_numbered_seasons(out)
    -- Search APIs commonly omit the season marker on the original series.
    -- Infer season one only when a later season of the same series is present.
    local sequels={}
    for _,show in ipairs(out) do
        if show.season and show.season>1 and season_series_kind(show.kind) then
            local names={show.series}
            for _,alias in ipairs(show.aliases or {}) do names[#names+1]=alias end
            for _,name in ipairs(names) do
                local key=identity(name)
                if key~='' then
                    local earliest=sequels[key]
                    sequels[key]=math.min(earliest or show.year or 9999,show.year or 9999)
                end
            end
        end
    end
    for _,show in ipairs(out) do
        if not show.season and not show.season_ambiguous and season_series_kind(show.kind) then
            local earliest=sequels[identity(show.series)]
            if earliest and (not show.year or show.year<earliest) then show.season=1 end
        end
    end
    return out, nil
end

local PLATFORM_PREFIXES={tencent='qq',iqiyi='qiyi',bilibili='bilibili',migu='migu'}
local function is_extra_episode(title)
    local lower=title:lower()
    return title:find('预告',1,true) or title:find('花絮',1,true)
        or title:find('特典',1,true) or title:find('插入歌',1,true)
        or lower:find('opening',1,true) or lower:find('ending',1,true)
        or lower:find('ncop',1,true) or lower:find('nced',1,true)
        or lower:match('%f[%a]pv%f[%A]')
end

function M.bangumi_episodes(text, parse_json, clean, platform_key)
    local data, err = decode_json(text, parse_json)
    if not data then return nil, err end
    local bangumi=data.bangumi
    if type(bangumi)~='table' or type(bangumi.episodes)~='table' then return {},nil end
    local out={}
    local selected_prefix=PLATFORM_PREFIXES[platform_key] or tostring(platform_key or ''):lower()
    for _,episode in ipairs(bangumi.episodes) do
        if type(episode)=='table' then
            local id=M.episode_id(episode.episodeId)
            if id then
                local number=M.limit_text(episode.episodeNumber,12)
                local title=M.limit_text(episode.episodeTitle,120,clean)
                local prefix=(title:match('^【(.-)】') or ''):lower()
                local global=tonumber(title:match('第%s*(%d+)%s*[集话話]'))
                out[#out+1]={id=id,number=number,raw_number=tonumber(number),
                    global_number=global,prefix=prefix,extra=not not is_extra_episode(title),
                    label=title~='' and title or ('第'..number..'集')}
            end
        end
        if #out>=500 then break end
    end
    if selected_prefix~='' then
        local matching=0
        for _,item in ipairs(out) do
            if item.prefix:find(selected_prefix,1,true) then matching=matching+1 end
        end
        if matching>0 then
            local selected={}
            for _,item in ipairs(out) do
                if item.prefix=='' or item.prefix:find(selected_prefix,1,true) then
                    selected[#selected+1]=item
                end
            end
            out=selected
        end
    end
    local minimum,offset_counts,total=nil,{},0
    for _,item in ipairs(out) do
        if not item.extra and item.global_number then
            minimum=math.min(minimum or item.global_number,item.global_number)
            if item.raw_number then
                local offset=item.global_number-item.raw_number
                offset_counts[offset]=(offset_counts[offset] or 0)+1
                total=total+1
            end
        end
    end
    local best=0
    for _,count in pairs(offset_counts) do best=math.max(best,count) end
    local raw_numbers_align=total>0 and best/total>=.8
    for _,item in ipairs(out) do
        local number=item.raw_number
        if not item.extra and item.global_number and minimum and not raw_numbers_align then
            number=item.global_number-minimum+1
        end
        item.number_value=number
        item.group=item.extra and '预告与其他' or '正片'
    end
    local info=M.show_info(bangumi.animeTitle or '',clean)
    local content_type=M.limit_text(bangumi.typeDescription or bangumi.type or '',32,clean)
    if content_type~='' then info.kind=content_type end
    return out,nil,info
end

-- Compare codepoints, not UTF-8 bytes: one wrong Chinese character is one edit.
-- Keep short titles exact; longer names may differ by at most 20% of characters.
local function title_score(wanted,actual)
    if wanted==actual then return wanted~='' and 1 or 0 end
    local function numbers(value)
        local out={}
        for number in value:gmatch('%d+') do out[#out+1]=number end
        return table.concat(out,',')
    end
    if numbers(wanted)~=numbers(actual) then return 0 end
    local a,b={},{}
    for c in wanted:gmatch(UTF8_CODEPOINT) do a[#a+1]=c end
    for c in actual:gmatch(UTF8_CODEPOINT) do b[#b+1]=c end
    local length=math.max(#a,#b)
    local limit=math.floor(length*.2)
    if math.min(#a,#b)<5 or length>120 or math.abs(#a-#b)>limit then return 0 end
    local previous={}
    for j=0,#b do previous[j]=j end
    for i=1,#a do
        local current={[0]=i}
        local minimum=i
        for j=1,#b do
            current[j]=math.min(current[j-1]+1,previous[j]+1,
                previous[j-1]+(a[i]==b[j] and 0 or 1))
            minimum=math.min(minimum,current[j])
        end
        if minimum>limit then return 0 end
        previous=current
    end
    return 1-previous[#b]/length
end

local function special_title(value)
    return title_width(value):lower():match('%f[%a]ova%f[%A]')
        or title_width(value):lower():match('%f[%a]oad%f[%A]')
end
local function same_work(a,b)
    return identity(a.series)==identity(b.series) and a.season==b.season
        and a.part==b.part and a.year==b.year
end

local MOVIE_VERSIONS={
    {name='mandarin',labels={'普通话版','普通话','国语版','国语','中文版','中文配音'}},
    {name='cantonese',labels={'粤语版','粤语'}},
    {name='english',labels={'英语版','英语配音'}},
    {name='original',labels={'原声版','原声','原版','日语版','日语'}},
}
local function movie_version(value)
    local title=title_width(value)
    for _,version in ipairs(MOVIE_VERSIONS) do
        for _,label in ipairs(version.labels) do
            if title:find(label,1,true) then return version.name end
        end
    end
end
local function movie_title(value)
    local title=title_width(value)
    for _,version in ipairs(MOVIE_VERSIONS) do
        for _,label in ipairs(version.labels) do title=title:gsub(label,'') end
    end
    return identity(title)
end

function M.auto_candidates(shows,media_name)
    local anime,episode,season=M.episode_query(media_name)
    local movie=not episode
    local info=M.show_info(anime or M.search_keyword(media_name))
    -- Without an episode number only a movie can be selected, never episode 1
    -- of a similarly named series or a season with incomplete metadata.
    if info.series=='' or movie and M.season_number(media_name) then return {} end
    local wanted=movie and movie_title(info.series) or identity(info.series)
    local version=movie_version(media_name) or 'original'
    local year=M.show_info(media_name).year
    local ranked={}
    for order,show in ipairs(shows or {}) do
        local kind=tostring(show.kind or '')
        local season_ok=season and (show.season==season or season==1 and show.season==nil)
            or not season and show.season==nil
        if season_ok and not show.season_ambiguous and not show.part_ambiguous
            and (not info.part or show.part==info.part)
            and (movie and movie_kind(kind) or not movie and not movie_kind(kind)
                and (season_series_kind(kind) or special_title(media_name)))
            and (not movie or not movie_version(show.series) or movie_version(show.series)==version)
            and not not special_title(anime or info.series)==not not special_title(show.series) then
            local score=title_score(wanted,movie and movie_title(show.series) or identity(show.series))
            if score<1 then
                for _,alias in ipairs(show.aliases or {}) do
                    if not movie or not movie_version(alias) or movie_version(alias)==version then
                        score=math.max(score,title_score(wanted,movie and movie_title(alias) or identity(alias)))
                    end
                    if score==1 then break end
                end
            end
            if score>=.8 then
                -- A wrong release year must not veto a title and season match.
                local rank=score+(year and show.year==year and .03 or 0)
                ranked[#ranked+1]={show=show,score=rank,order=order}
            end
        end
    end
    table.sort(ranked,function(a,b)
        if a.score==b.score then return a.order<b.order end
        return a.score>b.score
    end)
    local best=ranked[1]
    if not best then return {} end
    -- Movie platforms can list different release years for the same title.
    -- Prefer the requested year only among identical titles; a matching year
    -- must never promote a weaker title match over a stronger one.
    if movie and year and best.show.year==year then
        for i=#ranked,2,-1 do
            if ranked[i].show.year~=year
                and movie_title(ranked[i].show.series)==movie_title(best.show.series) then
                table.remove(ranked,i)
            end
        end
    end
    -- Different works within eight percentage points need a manual selection.
    for i=2,#ranked do
        if not same_work(best.show,ranked[i].show) and best.score-ranked[i].score<.08 then
            return {},'ambiguous'
        end
    end
    local out,seen={},{}
    for _,entry in ipairs(ranked) do
        if same_work(best.show,entry.show) then
            for _,platform in ipairs(entry.show.platforms or {}) do
                if platform.id and not seen[platform.id] and #out<20 then
                    seen[platform.id]=true
                    out[#out+1]={id=platform.id,key=platform.key,label=entry.show.label,kind=entry.show.kind}
                end
            end
        end
    end
    return out
end

function M.match_verified(match,media_name,episodes,show)
    if type(match)~='table' or type(episodes)~='table' then return false end
    local info=M.show_info(match.animeTitle)
    if show then info.kind=show.kind end
    info.platforms={{id=match.episodeId}}
    if #M.auto_candidates({info},media_name)==0 then return false end
    local episode=M.auto_episode(episodes,media_name,info)
    return episode~=nil and episode.id==match.episodeId
end

function M.auto_episode(episodes,media_name,show)
    local _,wanted=M.episode_query(media_name)
    local version=movie_version(media_name)
    if not wanted and (not show or not movie_kind(show.kind)) then return nil end
    local selected
    for _,item in ipairs(episodes or {}) do
        local matches=item.number_value==wanted
        if not wanted then
            local actual=movie_version(item.label) or movie_version(show.label or show.series)
            matches=(actual or 'original')==(version or 'original')
        end
        if not item.extra and matches then
            if selected then return nil end
            selected=item
        end
    end
    return selected
end

function M.parse_comments(text, parse_json, core)
    if type(text) ~= 'string' or text == '' then return nil, '服务器响应为空' end
    text = text:gsub('^\239\187\191', '')
    local data, err = decode_json(text, parse_json)
    if not data then return nil, err end
    if type(data.comments) ~= 'table' then return nil, '响应中没有弹幕列表' end
    local out, scanned = {}, 0
    for _, comment in ipairs(data.comments) do
        scanned = scanned + 1
        if type(comment) == 'table' then
            local fields = {}
            for field in (tostring(comment.p or '') .. ','):gmatch('(.-),') do fields[#fields + 1] = field end
            local time, mode, color = tonumber(fields[1]), tonumber(fields[2]), tonumber(fields[3])
            -- Renren forwards its member-comment type as 2. It is a normal
            -- scrolling comment, not the converter's internal L2R type 2.
            if mode==2 and tostring(fields[4] or ''):match('^%[renren%]') then mode=1 end
            if core.finite(time) and time >= 0 and time < 604800
                and core.finite(mode) and mode>=1 and mode<=9 and mode==math.floor(mode) then
                local value = core.clean(tostring(comment.m or '')):gsub('[\r\n]+', ' ')
                if value ~= '' then
                    out[#out + 1] = {t = time, mode = mode, text = value,
                        color = core.finite(color) and core.clamp(math.floor(color), 0, 16777215) or 16777215}
                end
            end
        end
        if #out >= 50000 or scanned >= 50000 then break end
    end
    table.sort(out, function(a, b) return a.t < b.t end)
    return out
end

return M
end)()
local options=require 'mp.options'
local o={enabled=true,api_servers='',danmaku_timeout=30,dandanplay_app_id='',dandanplay_app_secret='',
    autoload_danmaku=true}
options.read_options(o,'player_ui_danmaku')
o.danmaku_timeout=core.clamp(tonumber(o.danmaku_timeout) or 30,5,120)
o.dandanplay_app_id=tostring(o.dandanplay_app_id or ''):gsub('^%s+',''):gsub('%s+$','')
o.dandanplay_app_secret=tostring(o.dandanplay_app_secret or ''):gsub('^%s+',''):gsub('%s+$','')
local function private_server_config_path()
    local root=os.getenv('LOCALAPPDATA') or os.getenv('APPDATA')
    if root and root~='' then return root..'/mpv-AnimeFusion-danmaku.conf' end
    root=os.getenv('XDG_CONFIG_HOME')
    if root and root~='' then return root..'/animejanai-danmaku.conf' end
    root=os.getenv('HOME')
    if root and root~='' then return root..'/.animejanai-danmaku.conf' end
    return nil
end
local function read_private_servers()
    local path=private_server_config_path()
    if not path then return {} end
    local file=io.open(path,'rb')
    if not file then file=io.open(path..'.bak','rb') end
    if not file then return {} end
    local body=file:read(65537) or '';file:close()
    if #body>65536 then return {} end
    local value
    for line in (body..'\n'):gmatch('([^\n]*)\n') do
        local key,entry=line:gsub('\r$',''):match('^%s*([%w_%-]+)%s*=(.*)$')
        if key=='api_servers' then value=entry;break end
    end
    return online.parse_servers(value or '')
end
local function write_private_file(path,body)
    if not path then return false end
    local temp=path..'.tmp'
    local file=io.open(temp,'wb')
    if not file then return false end
    local written=file:write(body)
    local closed=file:close()
    if not written or not closed then os.remove(temp);return false end

    local current=io.open(path,'rb')
    if current then
        current:close()
        local backup=path..'.bak'
        os.remove(backup)
        local moved,move_error=os.rename(path,backup)
        if not moved then os.remove(temp);return false end
        local installed,install_error=os.rename(temp,path)
        if not installed then
            local restored=os.rename(backup,path)
            os.remove(temp)
            if not restored then
                -- Keep the backup; read_private_servers can recover it next startup.
                return false
            end
            return false
        end
        os.remove(backup)
    else
        local installed,install_error=os.rename(temp,path)
        if not installed then os.remove(temp);return false end
        os.remove(path..'.bak')
    end
    return true
end
local function write_private_servers(list)
    return write_private_file(private_server_config_path(),
        '# Private local danmaku sources; kept outside release files.\n'
        ..'api_servers='..online.serialize_servers(list)..'\n')
end
local function series_sources_path()
    local path=private_server_config_path()
    return path and path:gsub('%.conf$','-sources.json') or nil
end
local function read_series_sources()
    local path=series_sources_path()
    local file=path and io.open(path,'rb')
    if not file and path then file=io.open(path..'.bak','rb') end
    if not file then return {} end
    local body=file:read(8388609) or '';file:close()
    if #body>8388608 then return {} end
    local ok,data=pcall(utils.parse_json,body)
    return ok and type(data)=='table' and data or {}
end
local servers=read_private_servers()
local series_sources=read_series_sources()
local function remember_source(name,index,show,available)
    local key=online.series_source_key(name)
    local server=servers[index]
    if not key or not server or not show or not online.episode_id(show.id) then return end
    local saved_episodes={}
    for _,episode in ipairs(available or {}) do
        saved_episodes[#saved_episodes+1]={id=episode.id,label=episode.label,
            number_value=episode.number_value,extra=episode.extra}
    end
    series_sources[key]={server_url=server.url,id=show.id,key=show.key,
        label=show.label,kind=show.kind,episodes=saved_episodes}
    if not write_private_file(series_sources_path(),utils.format_json(series_sources)) then
        mp.msg.warn('弹幕来源记录保存失败')
    end
end
-- Only the per-user file supplies endpoints; packaged script options stay blank.
local source,status,results,episodes=1,'',{},{}
local autoload_state='idle'
local search_view,search_keyword,search_season,selected_show='shows','',nil,nil
local search_source,search_cache,search_cache_order,search_batch=0,{}, {},nil
local search_health={pending=0,failed=0,responded=0,total=0}
local episode_load_generation=0
local loaded=''
local loaded_platform=''
local preparation_timer,load_notice_pending=nil,false
local function show_loading_notice(text)
    local message=mp.get_property_osd('osd-ass-cc/0')..'{\\an5}'..text
        ..mp.get_property_osd('osd-ass-cc/1')
    mp.commandv('show-text',message,'3000','0')
end
local function cancel_preparation()
    if preparation_timer then preparation_timer:kill();preparation_timer=nil end
end
local function playback_notice_timer()
    if not preparation_timer then return end
    if mp.get_property_bool('core-idle',true) then preparation_timer:stop()
    else preparation_timer:resume() end
end
local picker,generation,request_jobs,request_serial=nil,0,{},0
local renderer
local publish
local autoload_generation=-1
local autoload_file_active=false
local match_current
local autoload_probe_timer,autoload_metadata_ready,autoload_title_baseline=nil,-1,''
local cancel_online
publish=function()
    local server_view={}
    for i,server in ipairs(servers) do
        server_view[#server_view+1]={index=i,note=server.note or '',selected=i==source}
    end
    local numbers={'一','二','三','四','五','六','七','八','九','十'}
    local number=numbers[source] or (source<20 and '十'..numbers[source-10] or '二十')
    local source_label=loaded_platform~='' and ('线路'..number..'·'..loaded_platform) or '本地弹幕'
    mp.set_property_native('user-data/player_ui/danmaku',{
        loaded=renderer and renderer.ready or false,file=loaded,count=renderer and renderer.count or 0,enabled=o.enabled,
        source_label=source_label,
        settings=renderer and renderer.settings or {},backend='DanmakuFactory',
        render_pending=renderer and renderer.busy or false,track=renderer and renderer.track,
        servers=server_view,source=source,status=status,results=results,episodes=episodes,autoload_state=autoload_state,
        search_view=search_view,search_keyword=search_keyword,search_season=search_season,
        search_source=search_source,search_pending=search_batch and search_health.pending or 0,
        search_failed=search_health.failed,search_responded=search_health.responded,search_total=search_health.total,
        selected_show=selected_show,episode_load_generation=episode_load_generation,
        config_path=renderer and renderer.config_path})
end
renderer=(function()
-- inlined module: player_ui_danmaku_render.lua
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
        if not ass_path or not M.ready or M.track then return end
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
        if not M.ready or M.track or mp.get_property_bool('idle-active',true) then return end
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
            ass_path=output;M.ready=true;attach();on_change()
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
end)()(mp,utils,function()
    if renderer then
        if renderer.error then status=renderer.error;autoload_state='error'
        elseif renderer.busy then status='弹幕转换中…'
        elseif renderer.ready then
            status='';autoload_state='loaded';cancel_preparation()
            if load_notice_pending then
                load_notice_pending=false
                if renderer.count>0 then
                    show_loading_notice(tostring(renderer.count)..'条弹幕大军正在袭来~~~')
                end
            end
        end
    end
    publish()
end)
local function kill() renderer.cancel() end
local function clear() renderer.clear() end
local function safe_path(path)
    return type(path)=='string' and path~='' and not path:find('%z') and not path:match('^%a[%w+.-]*://')
        and (path:lower():match('%.xml$') or path:lower():match('%.json$') or path:lower():match('%.ass$'))
end
local function load(path)
    if not safe_path(path) then mp.osd_message('请选择本地 XML、JSON 或 ASS 弹幕文件',3);return false end
    local file=io.open(path,'rb');if not file then mp.osd_message('无法打开弹幕文件',3);return false end;file:close()
    if cancel_online then cancel_online() end
    results={};autoload_state='loading';
    load_notice_pending=false;clear();loaded=path;loaded_platform='';o.enabled=true;renderer.toggle(true)
    load_notice_pending=true
    renderer.convert(path,false);publish();return true
end
local function cancel_request()
    request_serial=request_serial+1
    if search_batch then
        for index,state in pairs(search_batch.entry.state) do
            if state=='loading' then search_batch.entry.state[index]=nil end
        end
        search_batch=nil
    end
    for handle in pairs(request_jobs) do mp.abort_async_command(handle) end
    request_jobs={}
    return request_serial
end
cancel_online=cancel_request
local function run_powershell(command,callback,shared_serial,error_label)
    local serial=shared_serial or cancel_request()
    local file_generation=generation
    local finished=false
    local watchdog
    local handle
    handle=mp.command_native_async({name='subprocess',
        args={'powershell.exe','-NoProfile','-NonInteractive','-Command',command},
        playback_only=false,capture_stdout=true,capture_stderr=true},function(success,result,err)
        if handle~=nil then request_jobs[handle]=nil end
        if watchdog then watchdog:kill();watchdog=nil end
        if finished then return end
        finished=true
        if serial~=request_serial or file_generation~=generation then return end
        local code=result and result.status or nil
        local output=result and result.stdout or nil
        if success and code==0 and type(output)=='string' and output~='' then
            callback(output,nil)
        else
            local detail=result and (result.stderr or result.error_string) or err
            detail=tostring(detail or '网络请求失败'):gsub('https?://%S+','[线路]')
                :gsub('[\r\n]+',' '):gsub('%s+',' '):sub(1,180)
            callback(nil,detail)
        end
    end)
    if handle~=nil then
        request_jobs[handle]=true
        -- The HTTP timeout limits connection/read stalls. A large comment body can
        -- take longer than that in total while data keeps arriving, so do not kill
        -- the PowerShell reader at the first per-request timeout interval.
        watchdog=mp.add_timeout(math.min(180,o.danmaku_timeout*3+3),function()
            watchdog=nil
            if finished then return end
            finished=true
            request_jobs[handle]=nil
            mp.abort_async_command(handle)
            if serial==request_serial and file_generation==generation then
                callback(nil,(error_label or '请求失败')..'：连接超时')
            end
        end)
    end
end
local unsupported_api_error
local function request(url,body,callback,shared_serial)
    local ok,command=pcall(online.request_command,url,body,{
        timeout=o.danmaku_timeout,app_id=o.dandanplay_app_id,app_secret=o.dandanplay_app_secret})
    if not ok then callback(nil,tostring(command));return end
    local serial=shared_serial or cancel_request()
    local file_generation=generation
    local function attempt(number)
        run_powershell(command,function(response,err)
            if err and number<=3 and not unsupported_api_error(err) then
                mp.add_timeout(.3*2^(number-1),function()
                    if serial==request_serial and file_generation==generation then attempt(number+1) end
                end)
            else callback(response,err) end
        end,serial,'网络请求')
    end
    attempt(1)
end
local cached_hash_path,cached_hash
local function hash_file(path,callback)
    if not core.local_media(path,nil,mp.get_property_bool('demuxer-via-network',false)) then
        callback(nil);return
    end
    if path==cached_hash_path and cached_hash then callback(cached_hash);return end
    local ok,command=pcall(online.hash_command,path)
    if not ok then callback(nil);return end
    run_powershell(command,function(value,err)
        value=tostring(value or ''):lower():gsub('%s+','')
        if not err and #value==32 and value:match('^%x+$') then
            cached_hash_path=path;cached_hash=value;callback(value)
        else
            callback(nil)
        end
    end,nil,'文件识别')
end
local function server_error(action,detail)
    detail=tostring(detail or '未知错误'):gsub('https?://%S+','[线路]')
        :gsub('[\r\n]+',' '):gsub('%s+',' '):sub(1,180)
    return action..'失败：'..detail
end
local function route_name(index)
    local server=servers[index]
    local note=server and core.limit_chars(core.clean(server.note or ''),24) or ''
    return note~='' and note or ('线路 '..tostring(index))
end
local function route_error(index,action,detail)
    detail=tostring(detail or '未知错误'):gsub('https?://%S+','[线路]')
        :gsub('[\r\n]+',' '):gsub('%s+',' '):sub(1,140)
    return route_name(index)..' '..action..'失败：'..detail
end
unsupported_api_error=function(detail)
    detail=tostring(detail or '')
    return detail:find('404',1,true)~=nil or detail:find('405',1,true)~=nil
        or detail:find('501',1,true)~=nil
end
local function route_log(index,action,detail,failed)
    if not mp.msg then return end
    local message='弹幕 '..route_name(index)..' '..action..'：'..tostring(detail or '')
    message=message:gsub('https?://%S+','[线路]'):gsub('[\r\n]+',' '):gsub('%s+',' '):sub(1,220)
    local logger=failed and mp.msg.warn or mp.msg.info
    if logger then logger(message) end
end
local function append_attempt_error(attempt,index,action,detail)
    attempt.errors[index]=attempt.errors[index] or {}
    local values=attempt.errors[index]
    local value=route_error(index,action,detail)
    for _,existing in ipairs(values) do if existing==value then return end end
    values[#values+1]=value

end
local function clear_search_cache()
    search_cache={};search_cache_order={};search_batch=nil
end
local function get_search_cache(keyword)
    local key=tostring(keyword or ''):lower()
    local entry=search_cache[key]
    if entry and entry.completed_at and os.time()-entry.completed_at>600
        and not (search_batch and search_batch.key==key) then
        search_cache[key]=nil
        for i,value in ipairs(search_cache_order) do
            if value==key then table.remove(search_cache_order,i);break end
        end
        entry=nil
    end
    if entry then
        for i,value in ipairs(search_cache_order) do
            if value==key then table.remove(search_cache_order,i);break end
        end
    else
        entry={keyword=keyword,by_server={},state={},errors={},completed_at=nil}
        search_cache[key]=entry
    end
    search_cache_order[#search_cache_order+1]=key
    while #search_cache_order>6 do
        local expired=table.remove(search_cache_order,1)
        if not (search_batch and search_batch.key==expired) then search_cache[expired]=nil end
    end
    return key,entry
end
local function results_for_search(entry,server_index)
    local found={}
    for index=1,#servers do
        if server_index==0 or index==server_index then
            for _,item in ipairs(entry.by_server[index] or {}) do found[#found+1]=item end
        end
    end
    return found
end
local function publish_search(entry)
    results=results_for_search(entry,search_source)
    local pending,failed,responded,total,first_error=0,0,0,0,nil
    for index=1,#servers do
        if search_source==0 or index==search_source then
            total=total+1
            if entry.state[index]=='loading' then pending=pending+1 end
            if entry.state[index]=='error' then
                failed=failed+1
                first_error=first_error or entry.errors[index]
            end
            if entry.state[index]=='loaded' then responded=responded+1 end
        end
    end
    search_health={pending=pending,failed=failed,responded=responded,total=total}
    if pending>0 then
        status=#results>0 and ('找到 '..#results..' 个作品，其他线路搜索中…') or '正在搜索作品…'
    elseif failed>0 and #results>0 then
        status='找到 '..#results..' 个作品，部分线路搜索失败'
    elseif failed>0 and responded==0 then
        status=tostring(first_error or '请求失败')
    elseif #results>0 then
        status='找到 '..#results..' 个作品；请选择季度和平台'
    else
        status='没有搜索结果；可以换关键词再试'
    end
    publish()
end
local function finish_search_batch(batch)
    if search_batch~=batch then return end
    search_batch=nil
    batch.entry.completed_at=os.time()
    publish_search(batch.entry)
    for _,callback in ipairs(batch.callbacks) do
        local visible=results_for_search(batch.entry,search_source)
        callback(#visible>0,status)
    end
end
local function parse_remote(text)
    return online.parse_comments(text,utils.parse_json,core)
end
local function adopt(list,label,platform_key)

    load_notice_pending=false;clear();loaded=label or '';loaded_platform=online.episode_platform(platform_key,label)
    o.enabled=true;renderer.toggle(true);load_notice_pending=true
    autoload_state='loading';renderer.comments(list);publish()
end
local function fetch_episode(episode_id,label,server_index,quiet,on_missing,shared_serial,on_loaded,on_empty,platform_key)
    local id=online.episode_id(episode_id)
    local sv=servers[server_index or source]
    episode_load_generation=episode_load_generation+1
    if not id or not sv then
        status='弹幕条目无效';autoload_state=on_missing and 'error' or 'idle';publish()
        if on_missing then on_missing(status) elseif not quiet then mp.osd_message(status,4) end
        return
    end
    label=online.limit_text(label or ('episode '..id),220,core.clean)
    source=server_index or source
    results={}
    kill();clear();loaded=''
    if not on_missing then autoload_state='idle' end
    status='获取弹幕中…';publish()
    request(sv.url..'/api/v2/comment/'..id..'?withRelated=true',nil,function(body,err)
        local list,why=parse_remote(body or '')
        if not list then
            route_log(server_index or source,'弹幕读取',err or why or '响应无效',true)
            if on_missing then
                if err or why then on_missing(server_error('获取弹幕',err or why)) else on_missing() end
                return
            end
            status=server_error('获取弹幕',err or why);publish()
            return
        end
        source=server_index or source
        if #list==0 then
            kill();clear();loaded=''
            route_log(server_index or source,'弹幕读取','该集返回 0 条弹幕')
            if on_empty then on_empty();return end
            if on_missing then on_missing()
            else status='该集没有弹幕';publish() end
            return
        end
        if on_loaded then on_loaded() end
        adopt(list,label,platform_key)
        route_log(server_index or source,'弹幕读取','载入 '..#list..' 条弹幕')
    end,shared_serial)
end
local function query_name()
    return online.query_name(mp.get_property('path',''),mp.get_property('media-title',''),core.clean)
end
local function server_queue()
    local queue={}
    for index=1,#servers do queue[#queue+1]=index end
    return queue
end
local function search_shows(keyword,server_index,quiet,on_complete,season,force)
    search_source=tonumber(server_index) or 0
    if search_source<0 or search_source>#servers then search_source=0 end
    if type(keyword)~='string' or keyword=='' or #servers==0 then
        status='没有可用的搜索关键词或弹幕线路';publish()
        if on_complete then on_complete(false,status) end
        return
    end
    search_view='shows';search_keyword=keyword;search_season=season
    episodes={};selected_show=nil
    local key,entry=get_search_cache(keyword)
    if force then
        cancel_request()
        entry.state={};entry.errors={};entry.by_server={};entry.completed_at=nil
    end
    if search_batch and search_batch.key==key then
        if on_complete then search_batch.callbacks[#search_batch.callbacks+1]=on_complete end
        publish_search(entry)
        return
    end

    local missing={}
    for index=1,#servers do
        if entry.state[index]~='loaded' then missing[#missing+1]=index end
    end
    if #missing==0 then
        publish_search(entry)
        if on_complete then on_complete(#results>0,status) end
        return
    end

    cancel_request()
    local batch={key=key,entry=entry,pending=#missing,callbacks={},serial=request_serial}
    if on_complete then batch.callbacks[#batch.callbacks+1]=on_complete end
    search_batch=batch
    for _,index in ipairs(missing) do entry.state[index]='loading';entry.errors[index]=nil end
    publish_search(entry)
    for _,index in ipairs(missing) do
        local request_index=index
        local server=servers[request_index]
        local url=server.url..'/api/v2/search/anime?keyword='..online.urlencode(keyword)
        request(url,nil,function(body,err)
            if search_batch~=batch or batch.serial~=request_serial then return end
            local found,why
            if not err then found,why=online.search_results(body or '',keyword,utils.parse_json,core.clean) end
            if found then
                for _,item in ipairs(found) do
                    item.server_index=request_index
                    item.source_note=server.note or ('线路 '..request_index)
                end
                entry.by_server[request_index]=found
                entry.state[request_index]='loaded'
                entry.errors[request_index]=nil
            else
                entry.state[request_index]='error'
                entry.errors[request_index]=tostring(err or why or '请求失败')
                route_log(request_index,'作品搜索',err or why,true)
            end
            batch.pending=batch.pending-1
            if batch.pending<=0 then finish_search_batch(batch) else publish_search(entry) end
        end,batch.serial)
    end
end
local function finish_match(attempt,detail)
    local failed=detail~=nil
    if not detail then
        local failures={}
        for _,index in ipairs(attempt.queue) do
            local route_failures=attempt.errors[index]
            if route_failures and route_failures[1] then failures[#failures+1]=route_failures[1] end
        end
        if attempt.ambiguous then
            detail='有多个相近作品，请手动选择弹幕'
        elseif #failures>0 then
            failed=true
            detail=server_error('弹幕匹配',table.concat(failures,'；'))
        else
            detail='没有匹配到弹幕'
        end
    end
    status=detail
    autoload_state=attempt.automatic and (failed and 'error' or 'not-found') or 'idle'
    publish()
    if attempt.automatic and mp.msg and mp.msg.warn then
        mp.msg.warn('弹幕自动匹配结束：'..tostring(status):gsub('https?://%S+','[线路]'))
    end
    if not attempt.quiet then mp.osd_message(status,5) end
end
local match_at
local match_search_result
local function search_route(attempt,position,index,server)
    if not attempt.keyword then match_at(attempt,position+1);return end
    local url=server.url..'/api/v2/search/anime?keyword='..online.urlencode(attempt.keyword)
    request(url,nil,function(body,err)
        match_search_result(attempt,position,index,server,body,err)
    end,attempt.serial)
end
local function match_by_file(attempt,position,index,server,search_fallback)
    attempt.match_attempted=attempt.match_attempted or {}
    attempt.match_attempted[index]=true
    local function continue()
        if search_fallback and attempt.keyword then search_route(attempt,position,index,server)
        else match_at(attempt,position+1) end
    end
    request(server.url..'/api/v2/match',online.match_body(attempt.name,
        mp.get_property_number('file-size',0),mp.get_property_number('duration',0),attempt.file_hash),function(body,err)
        local matched,why=online.match_result(body or '',utils.parse_json)
        if err or why then
            if unsupported_api_error(err or why) then
                route_log(index,'文件匹配','线路未提供此接口')
            else
                append_attempt_error(attempt,index,'文件匹配',err or why)
                route_log(index,'文件匹配',err or why,true)
            end
            continue()
            return
        elseif not matched then
            route_log(index,'文件匹配','没有匹配项')
            continue()
            return
        end
        if matched and matched.animeId then
            request(server.url..'/api/v2/bangumi/'..matched.animeId,nil,function(detail,detail_error)
                local available,detail_why,show
                if not detail_error then available,detail_why,show=online.bangumi_episodes(detail or '',utils.parse_json,core.clean) end
                if available and online.match_verified(matched,attempt.name,available,show) then
                    route_log(index,'剧集核验','匹配 '..tostring(matched.animeTitle or '')..' · '..tostring(matched.episodeTitle or ''))
                    source=index
                    fetch_episode(matched.episodeId,
                        online.limit_text(tostring(matched.animeTitle or '')..' · '..tostring(matched.episodeTitle or ''),220,core.clean),
                        index,attempt.quiet,function(detail_error)
                            if detail_error then append_attempt_error(attempt,index,'弹幕读取',detail_error) end
                            continue()
                        end,attempt.serial,function()
                            remember_source(attempt.name,index,{id=matched.animeId,label=matched.animeTitle,
                                kind=show and show.kind},available)
                        end)
                else
                    if detail_error or detail_why then
                        append_attempt_error(attempt,index,'剧集核验',detail_error or detail_why)
                        route_log(index,'剧集核验',detail_error or detail_why,true)
                    else
                        route_log(index,'剧集核验','返回结果与文件季集不一致')
                    end
                    continue()
                end
            end,attempt.serial)
            return
        end
        continue()
    end,attempt.serial)
end
match_search_result=function(attempt,position,index,server,body,err)
        local keyword=attempt.keyword
        local shows,why
        if not err then shows,why=online.search_results(body or '',keyword,utils.parse_json,core.clean) end
        if err or why then
            if unsupported_api_error(err or why) then
                route_log(index,'作品搜索','线路未提供此接口')
            else
                append_attempt_error(attempt,index,'作品搜索',err or why)
                route_log(index,'作品搜索',err or why,true)
            end
            -- Search and file matching are separate API capabilities. A failed
            -- search endpoint must not suppress this route's match fallback.
            if attempt.match_attempted and attempt.match_attempted[index] then match_at(attempt,position+1)
            else match_by_file(attempt,position,index,server) end
            return
        end
        local candidates,reason=online.auto_candidates(shows,attempt.name)
        attempt.ambiguous=attempt.ambiguous or reason=='ambiguous'
        route_log(index,'作品搜索','返回 '..#(shows or {})..' 个作品，匹配候选 '..#candidates)
        if #candidates==0 then
            if attempt.match_attempted and attempt.match_attempted[index] then match_at(attempt,position+1)
            else match_by_file(attempt,position,index,server) end
            return
        end
        local function try_candidate(candidate_index)
            local candidate=candidates[candidate_index]
            if not candidate then
                if attempt.match_attempted and attempt.match_attempted[index] then match_at(attempt,position+1)
                else match_by_file(attempt,position,index,server) end
                return
            end
            request(server.url..'/api/v2/bangumi/'..candidate.id,nil,function(detail,detail_error)
                local available,detail_why
                if not detail_error then
                    available,detail_why=online.bangumi_episodes(detail or '',utils.parse_json,core.clean,candidate.key)
                end
                local episode=online.auto_episode(available,attempt.name,candidate)
                if not episode then
                    if detail_error or detail_why then
                        append_attempt_error(attempt,index,'剧集核验',detail_error or detail_why)
                        route_log(index,'剧集核验',detail_error or detail_why,true)
                    end
                    try_candidate(candidate_index+1)
                    return
                end
                source=index
                fetch_episode(episode.id,online.limit_text(candidate.label..' · '..episode.label,220,core.clean),
                    index,attempt.quiet,function(comment_error)
                        if comment_error then append_attempt_error(attempt,index,'弹幕读取',comment_error) end
                        try_candidate(candidate_index+1)
                    end,attempt.serial,function()remember_source(attempt.name,index,candidate,available)end,nil,candidate.key)
            end,attempt.serial)
        end
        try_candidate(1)
end
match_at=function(attempt,position)
    local index=attempt.queue[position]
    if not index then
        local detail
        local failures={}
        for _,route_index in ipairs(attempt.queue) do
            local route_failures=attempt.errors[route_index]
            if route_failures and route_failures[1] then failures[#failures+1]=route_failures[1] end
        end
        if #failures>0 then detail=server_error('弹幕匹配',table.concat(failures,'；')) end
        finish_match(attempt,detail)
        return
    end
    local server=servers[index]
    if not server then match_at(attempt,position+1);return end
    status='匹配弹幕中…（'..attempt.name..'）';publish()
    if attempt.direct_match_first then
        if not (attempt.match_attempted and attempt.match_attempted[index]) then
            match_by_file(attempt,position,index,server,true)
        else
            match_at(attempt,position+1)
        end
        return
    end
    if not attempt.keyword then match_by_file(attempt,position,index,server);return end
    attempt.waiting_position=position
    local reply=attempt.search_replies[position]
    if not reply then return end
    attempt.waiting_position=nil
    attempt.search_replies[position]=nil
    match_search_result(attempt,position,index,server,reply.body,reply.err)
end
local function automatic_match_all_routes(attempt)
    local states={}
    local active,priority_fallback=false,false
    local advance
    local function finish()
        attempt.finished=true
        finish_match(attempt)
    end
    local function try_route(index)
        local state=states[index]
        active=true;state.started=true
        local function next_candidate(position)
            if attempt.finished then return end
            local candidate=state.candidates[position]
            if not candidate then
                state.done=true;active=false;priority_fallback=true;advance();return
            end
            request(servers[index].url..'/api/v2/bangumi/'..candidate.id,nil,function(body,err)
                if attempt.finished then return end
                local available,why
                if not err then available,why=online.bangumi_episodes(body or '',utils.parse_json,core.clean,candidate.key) end
                local episode=available and online.auto_episode(available,attempt.name,candidate) or nil
                if not episode then
                    if err or why then append_attempt_error(attempt,index,'剧集核验',err or why) end
                    next_candidate(position+1);return
                end
                if attempt.failed_episodes and attempt.failed_episodes[index..':'..episode.id] then
                    next_candidate(position+1);return
                end
                request(servers[index].url..'/api/v2/comment/'..episode.id..'?withRelated=true',nil,function(comments,comment_error)
                    if attempt.finished then return end
                    local list,parse_error
                    if not comment_error then list,parse_error=parse_remote(comments or '') end
                    if list and #list>0 then
                        remember_source(attempt.name,index,candidate,available)
                        attempt.finished=true;cancel_request();source=index
                        adopt(list,online.limit_text(candidate.label..' · '..episode.label,220,core.clean),candidate.key)
                        route_log(index,'弹幕读取','载入 '..#list..' 条弹幕');return
                    end
                    if comment_error or parse_error then
                        append_attempt_error(attempt,index,'弹幕读取',comment_error or parse_error)
                    end
                    route_log(index,'弹幕读取',list and ('该集返回 '..#list..' 条弹幕，继续查找') or comment_error or parse_error)
                    -- Exhaust this route's other platforms before moving to the next route.
                    if not priority_fallback and #attempt.queue>1 then
                        state.next_position=position+1
                        if not state.candidates[state.next_position] then state.done=true end
                        active=false;priority_fallback=true;advance()
                    else next_candidate(position+1) end
                end,attempt.serial)
            end,attempt.serial)
        end
        next_candidate(state.next_position or 1)
    end
    advance=function()
        if active or attempt.finished then return end
        for _,index in ipairs(attempt.queue) do
            local state=states[index]
            if not state.done then
                if state.ready then
                    if #state.candidates>0 then try_route(index);return end
                    state.done=true
                elseif priority_fallback then
                    return -- Wait for this higher priority route's single search response.
                end
            end
        end
        for _,state in pairs(states) do if not state.done then return end end
        finish()
    end
    for _,index in ipairs(attempt.queue) do states[index]={ready=false,done=false,candidates={}} end
    if #attempt.queue==0 then finish();return end
    status='正在并行搜索全部弹幕线路…（'..attempt.name..'）';publish()
    for _,index in ipairs(attempt.queue) do
        request(servers[index].url..'/api/v2/search/anime?keyword='..online.urlencode(attempt.keyword or attempt.name),nil,function(body,err)
            if attempt.finished then return end
            local found,why
            if not err then found,why=online.search_results(body or '',attempt.keyword or attempt.name,utils.parse_json,core.clean) end
            if found then
                local candidates,reason=online.auto_candidates(found,attempt.name)
                states[index].candidates=candidates
                attempt.ambiguous=attempt.ambiguous or reason=='ambiguous'
                route_log(index,'作品搜索','返回 '..#found..' 个作品，匹配候选 '..#candidates)
            else
                append_attempt_error(attempt,index,'作品搜索',err or why)
                route_log(index,'作品搜索',err or why,true)
            end
            states[index].ready=true;advance()
        end,attempt.serial)
    end
end
local function load_remembered_source(attempt,on_missing)
    local key=online.series_source_key(attempt.name)
    local saved=key and series_sources[key]
    if type(saved)~='table' or not online.episode_id(saved.id)
        or type(saved.label)~='string' or type(saved.episodes)~='table' then on_missing();return end
    local index
    for i,server in ipairs(servers) do if server.url==saved.server_url then index=i;break end end
    if not index then on_missing();return end
    local function load(available)
        local episode=online.auto_episode(available,attempt.name,saved)
        if not episode or not online.episode_id(episode.id) then on_missing();return end
        route_log(index,'来源复用','直接读取已记住平台的对应剧集')
        fetch_episode(episode.id,online.limit_text(saved.label..' · '..episode.label,220,core.clean),
            index,attempt.quiet,function(err)
                attempt.failed_episodes={[index..':'..episode.id]=true}
                if err then append_attempt_error(attempt,index,'弹幕读取',err) end
                on_missing()
            end,attempt.serial,function()
                if available~=saved.episodes then remember_source(attempt.name,index,saved,available) end
            end,function()
                if available~=saved.episodes then remember_source(attempt.name,index,saved,available) end
                status='该集没有弹幕';autoload_state='empty';publish()
            end,saved.key)
    end
    if online.auto_episode(saved.episodes,attempt.name,saved) then load(saved.episodes);return end
    -- An ongoing series can gain episodes. Refresh this platform's list directly,
    -- without searching the work again, before declaring the source unavailable.
    request(servers[index].url..'/api/v2/bangumi/'..saved.id,nil,function(body,err)
        local available,why
        if not err then available,why=online.bangumi_episodes(body or '',utils.parse_json,core.clean,saved.key) end
        if not available then
            if err or why then append_attempt_error(attempt,index,'剧集核验',err or why) end
            on_missing();return
        end
        load(available)
    end,attempt.serial)
end
match_current=function(quiet,automatic,failed_episodes)
    cancel_request();results={}
    autoload_state=automatic and 'loading' or 'idle'
    publish()
    if #servers==0 then
        status='还没有配置弹幕线路';publish()
        if automatic then autoload_state='error';publish() end
        if not quiet then mp.osd_message('在「设置 → 弹幕设置 → 配置弹幕线路」里添加线路',5) end
        return
    end
    local name=online.limit_text(query_name(),512,core.clean)
    if name=='' then
        status='无法取得片名';autoload_state=automatic and 'error' or 'idle';publish()
        if not quiet then mp.osd_message(status,4) end
        return
    end
    local queue=server_queue()
    local function start(file_hash)
        local attempt={name=name,queue=queue,quiet=quiet,automatic=automatic,errors={},file_hash=file_hash,
            serial=request_serial,generation=generation,direct_match_first=automatic,match_attempted={}}
        local keyword=online.episode_query(name)
        if automatic or keyword then attempt.keyword=online.search_keyword(name) end
        if automatic then
            attempt.failed_episodes=failed_episodes
            if failed_episodes then automatic_match_all_routes(attempt)
            else load_remembered_source(attempt,function()automatic_match_all_routes(attempt)end) end
            return
        end
        if keyword then
            attempt.search_replies={}
            local serial=request_serial
            for position,index in ipairs(queue) do
                local server=servers[index]
                if server then
                    local route_position=position
                    request(server.url..'/api/v2/search/anime?keyword='..online.urlencode(attempt.keyword),nil,
                        function(body,err)
                            attempt.search_replies[route_position]={body=body,err=err}
                            if attempt.waiting_position==route_position then match_at(attempt,route_position) end
                        end,serial)
                end
            end
        end
        match_at(attempt,1)
    end
    if automatic then start(nil) else hash_file(mp.get_property('path',''),start) end
end
mp.register_script_message('player_ui-danmaku-retry-match',function()
    if autoload_state=='loading' then return end
    match_current(true,true)
end)
local try_automatic_match
local function schedule_automatic_match(expected_generation,attempts)
    if autoload_probe_timer or attempts>=120 then return end
    autoload_probe_timer=mp.add_timeout(.25,function()
        autoload_probe_timer=nil
        if try_automatic_match then try_automatic_match(expected_generation,attempts+1) end
    end)
end
try_automatic_match=function(expected_generation,attempts)
    attempts=attempts or 0
    if not autoload_file_active or expected_generation~=generation or not o.autoload_danmaku
        or autoload_generation==expected_generation then return end
    local path=mp.get_property('path','')
    if path=='' then return end
    local remote=path:match('^%a[%w+.-]*://')~=nil
    if not remote then
        local xml=path:gsub('%.[^./\\]+$','')..'.xml'
        local stat=utils.file_info(xml)
        if stat and stat.is_file then
            if autoload_probe_timer then autoload_probe_timer:kill();autoload_probe_timer=nil end
            return
        end
    end
    local current_title=mp.get_property('media-title','')
    local title_ready=not remote or autoload_metadata_ready==expected_generation
        or current_title~='' and current_title~=autoload_title_baseline
    if title_ready and query_name()~='' then
        autoload_generation=expected_generation
        if autoload_probe_timer then autoload_probe_timer:kill();autoload_probe_timer=nil end
        if mp.msg and mp.msg.info then
            local name=tostring(query_name()):gsub('https?://%S+','[线路]'):gsub('[\r\n]+',' '):sub(1,180)
            mp.msg.info('弹幕自动匹配开始：'..name)
        end
        match_current(true,true)
        return
    end
    schedule_automatic_match(expected_generation,attempts)
end
local function do_search(keyword,server_index,season,force)

    autoload_state='idle'
    if #servers==0 then status='还没有配置弹幕线路';publish();mp.osd_message(status,4);return end
    keyword=online.limit_text(keyword,120,core.clean)
    if core.char_count(keyword)<2 then
        status='搜索关键词至少需要 2 个字符';publish();mp.osd_message(status,4);return
    end
    local selected=tonumber(server_index) or source
    search_shows(keyword,selected,false,nil,tonumber(season),force)
end
local function save_servers()
    return write_private_servers(servers)
end
local function refresh_servers(keep_status)
    if #servers==0 then source=1 elseif source>#servers then source=#servers end
    if not keep_status then status='' end
    publish()
end
local function commit_servers(message)
    cancel_request()
    clear_search_cache()
    status='';results={};autoload_state='idle'
    if not save_servers() then message=message..'（配置文件写入失败）' end
    refresh_servers(true);mp.osd_message(message,4)
end
mp.register_script_message('player_ui-danmaku-search',function()
    local title=query_name();local anime,_,season=online.episode_query(title)
    mp.commandv('script-message-to','mpvnet','show-danmaku-search',anime or title,tostring(season or ''))
end)
mp.register_script_message('player_ui-danmaku-search-query',function(value,server_index,season,force)
    value=tostring(value or ''):match('^%s*(.-)%s*$')
    if value~='' then do_search(value,server_index,season,force=='true') end
end)
mp.register_script_message('player_ui-danmaku-search-filter',function(value)
    local index=tonumber(value) or 0
    if index<0 or index>#servers then return end
    search_source=index
    local key=tostring(search_keyword or ''):lower()
    local entry=search_cache[key]
    if entry then
        publish_search(entry)
    else
        results={};status=search_keyword~='' and '请搜索作品后查看线路结果' or ''
        publish()
    end
end)
mp.register_script_message('player_ui-danmaku-show',function(anime_id,server_index)
    local id=online.episode_id(anime_id)
    local index=tonumber(server_index) or source
    local server=servers[index]
    if not id or not server then return end
    local chosen
    for _,show in ipairs(results) do
        if show.server_index==index then
            for _,platform in ipairs(show.platforms or {}) do
                if platform.id==id then
                    chosen={id=id,label=show.label,kind=show.kind,season=show.season,platform=platform.name,key=platform.key,
                        server_index=index,server_url=server.url}
                    break
                end
            end
        end
        if chosen then break end
    end
    if not chosen then return end
    cancel_request();episodes={};selected_show=chosen;search_view='episodes'
    status='获取剧集列表中…';publish()
    request(server.url..'/api/v2/bangumi/'..id,nil,function(body,err)
        local found,why
        if not err then found,why=online.bangumi_episodes(body or '',utils.parse_json,core.clean,chosen.key) end
        if not found then
            status=server_error('获取剧集',err or why);publish();return
        end
        episodes=found
        local main=0
        for _,item in ipairs(found) do if not item.extra then main=main+1 end end
        status=#found>0 and ('正片 '..main..' 条；另有 '..(#found-main)..' 条预告或其他内容') or '该作品没有可用剧集'
        publish()
    end)
end)
mp.register_script_message('player_ui-danmaku-search-back',function()
    cancel_request();search_view='shows';selected_show=nil;episodes={}
    if search_keyword~='' then search_shows(search_keyword,search_source,false,nil,search_season)
    else status='请选择作品、季度和平台';publish() end
end)
mp.register_script_message('player_ui-danmaku-pick',function(id,label,index)
    index=tonumber(index) or source
    local chosen=selected_show
    local available=episodes
    local name=query_name()
    local episode=chosen and online.auto_episode(available,name,chosen)
    local same_episode=episode and episode.id==online.episode_id(id)
    local _,_,season=online.episode_query(name)
    local same_season=chosen and (not chosen.season or chosen.season==season)
    local same_source=chosen and chosen.server_index==index
        and servers[index] and chosen.server_url==servers[index].url
    local serial=cancel_request()
    fetch_episode(id,label,index,false,function(err)
        if err then route_log(index,'弹幕读取',err,true) end
        if same_episode and same_season and same_source then match_current(true,true,{[index..':'..tostring(online.episode_id(id))]=true})
        else status=err or '该集没有弹幕';autoload_state='idle';publish() end
    end,serial,function()
        if same_episode and same_season and same_source then remember_source(name,index,chosen,available) end
    end,function()
        if same_episode and same_season and same_source then remember_source(name,index,chosen,available) end
        status='该集没有弹幕';autoload_state='empty';publish()
    end,chosen and chosen.key)
end)
mp.register_script_message('player_ui-danmaku-episode',function(id,label)
    fetch_episode(id,label,source,false,nil,nil,nil,nil,selected_show and selected_show.key)
end)
local function show_source_manager()
    local args={}
    for i,server in ipairs(servers) do
        args[#args+1]=server.note or ('线路 '..i)
        args[#args+1]=server.url
    end
    mp.commandv('script-message-to','mpvnet','show-danmaku-sources',unpack(args))
end
local function save_source_manager(value)
    local ok,document=pcall(utils.parse_json,tostring(value or ''))
    if not ok or type(document)~='table' or type(document.servers)~='table' then
        mp.osd_message('弹幕线路配置格式无效',4);return
    end
    if #document.servers>20 then mp.osd_message('最多配置 20 条弹幕线路',4);return end
    local updated,seen={},{}
    for _,entry in ipairs(document.servers) do
        if type(entry)~='table' then mp.osd_message('弹幕线路格式无效',4);return end
        local name=core.clean(tostring(entry.name or ''):match('^%s*(.-)%s*$'))
        if name=='' or core.char_count(name)>80 or name:find(',',1,true) then
            mp.osd_message('线路显示名不能为空、不能包含逗号且最多 80 个字符',4);return
        end
        local server,why=online.parse_server_entry(tostring(entry.url or ''))
        if not server or server.note then mp.osd_message(why or '请单独填写有效的线路地址',4);return end
        if seen[server.url] then mp.osd_message('线路地址不能重复',4);return end
        seen[server.url]=true;server.note=name;updated[#updated+1]=server
    end
    servers=updated;source=1
    commit_servers('已保存弹幕线路')
end
mp.register_script_message('player_ui-danmaku-manage',show_source_manager)
mp.register_script_message('player_ui-danmaku-save-servers',save_source_manager)
local function choose()
    if picker then return end
    local g=generation
    local ps=[[Add-Type -AssemblyName System.Windows.Forms; $d=New-Object System.Windows.Forms.OpenFileDialog; $d.Filter='弹幕文件 (*.xml;*.ass;*.json)|*.xml;*.ass;*.json'; $d.Title='选择弹幕文件'; if($d.ShowDialog() -eq 'OK'){[Console]::OutputEncoding=[Text.UTF8Encoding]::new();[Console]::Write($d.FileName)}; $d.Dispose()]]
    picker=mp.command_native_async({name='subprocess',args={'powershell.exe','-NoProfile','-STA','-Command',ps},playback_only=false,capture_stdout=true,capture_stderr=true},function(ok,result)
        picker=nil
        if g~=generation then return end
        if ok and result and result.status==0 and result.stdout~='' then load(result.stdout:gsub('[\r\n]+$',''))
        elseif not ok or not result or result.status~=0 then mp.osd_message('文件选择器不可用；可通过 player_ui-danmaku-load 传入本地 XML 路径',4) end
    end)
end
mp.register_script_message('player_ui-danmaku-load',load)
mp.register_script_message('player_ui-danmaku-choose',choose)
mp.register_script_message('player_ui-danmaku-toggle',function()o.enabled=not o.enabled;renderer.toggle(o.enabled);publish()end)
mp.register_script_message('player_ui-danmaku-clear',function()
    if cancel_online then cancel_online() end

    status='';results={};autoload_state='idle';kill();clear();loaded='';publish()
end)
mp.register_script_message('player_ui-danmaku-setting',function(key,value)
    local current=renderer.settings[key]
    if type(current)=='number' then value=tonumber(value)
    elseif type(current)=='boolean' then value=value=='true'
    elseif type(current)=='table' then value=utils.parse_json(value) end
    if value~=nil then renderer.set(key,value) end
end)
mp.register_script_message('player_ui-danmaku-reset',renderer.reset)
mp.register_script_message('player_ui-danmaku-reload',renderer.reload)
mp.register_script_message('player_ui-danmaku-words',function()
    mp.commandv('script-message-to','mpvnet','show-danmaku-blocklist',renderer.words())
end)
mp.register_script_message('player_ui-danmaku-save-words',renderer.set_words)
mp.register_event('start-file',function()
    cancel_preparation();load_notice_pending=false;loaded_platform=''
    autoload_file_active=true
    generation=generation+1;autoload_generation=-1;autoload_metadata_ready=-1

    autoload_title_baseline=mp.get_property('media-title','')
    if autoload_probe_timer then autoload_probe_timer:kill();autoload_probe_timer=nil end
    cached_hash_path=nil;cached_hash=nil;cancel_request();kill();clear();loaded='';results={}
    autoload_state=o.autoload_danmaku and 'loading' or 'idle'
    status=o.autoload_danmaku and '自动加载中…' or ''
    if picker then mp.abort_async_command(picker);picker=nil end
    publish()
    if o.autoload_danmaku then schedule_automatic_match(generation,0) end
end)
mp.register_event('file-loaded',function()
    local path=mp.get_property('path','')
    local has_local_danmaku=false
    if not path:match('^%a[%w+.-]*://') then
        local xml=path:gsub('%.[^./\\]+$','')..'.xml'
        local stat=utils.file_info(xml)
        has_local_danmaku=stat and stat.is_file or false
        if has_local_danmaku then load(xml) end
    end
    if not has_local_danmaku and o.autoload_danmaku and autoload_generation~=generation then
        autoload_metadata_ready=generation
        try_automatic_match(generation,0)
    end
    if (has_local_danmaku or o.autoload_danmaku) and not renderer.ready then
        local expected_generation=generation
        preparation_timer=mp.add_timeout(2,function()
            preparation_timer=nil
            if generation==expected_generation and autoload_state=='loading' and not renderer.ready then
                show_loading_notice('弹幕准备中~~~')
            end
        end,true)
        playback_notice_timer()
    end
end)
mp.observe_property('core-idle','bool',playback_notice_timer)
mp.register_event('end-file',function()
    cancel_preparation();load_notice_pending=false;loaded_platform=''
    autoload_file_active=false
    generation=generation+1;autoload_metadata_ready=-1

    if autoload_probe_timer then autoload_probe_timer:kill();autoload_probe_timer=nil end
    cached_hash_path=nil;cached_hash=nil;cancel_request();kill();clear();status='';results={};autoload_state='idle'
    if picker then mp.abort_async_command(picker);picker=nil end
    publish()
end)
mp.register_event('shutdown',function()
    cancel_preparation();load_notice_pending=false
    cancel_request();renderer.clear()
    if autoload_probe_timer then autoload_probe_timer:kill();autoload_probe_timer=nil end
    if picker then mp.abort_async_command(picker) end
end)
mp.observe_property('media-title','string',function()
    if o.autoload_danmaku then try_automatic_match(generation,0) end
end)
publish()

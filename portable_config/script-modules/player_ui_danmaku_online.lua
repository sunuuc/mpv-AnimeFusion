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

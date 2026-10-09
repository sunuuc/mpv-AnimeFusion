-- FPS is unique video frames submitted through VO, not monitor scanout.
-- Requires this distribution's vo-presented-frame-count native property.
local mp = require 'mp'
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
local visible = false
local timer
local samples = {}
local status, status_time = nil, -math.huge
local WINDOW, INTERVAL = 2.0, 0.25

local function reset()
    samples = {}
    status, status_time = nil, -math.huge
end

local function actual(now)
    local n = mp.get_property_number('vo-presented-frame-count')
    if not n or n < 0 then samples = {}; return nil end
    if mp.get_property_bool('pause', false) then samples = {}; return 0 end
    local last = samples[#samples]
    if last and (n < last.n or now <= last.t or now - last.t > 4) then samples = {} end
    samples[#samples + 1] = {t=now, n=n}
    -- Retain one sample just before the window boundary; use its real time.
    while #samples > 2 and samples[2].t <= now - WINDOW do table.remove(samples, 1) end
    local first = samples[1]
    if now - first.t < 0.75 then return nil end
    return (n - first.n) / (now - first.t)
end

local function ai_status(now)
    if status and now - status_time < 2 then return status end
    status_time = now
    local active = false
    for _, f in ipairs(mp.get_property_native('vf', {}) or {}) do
        if f.name == 'animejanai' or f.name == 'vapoursynth' then active = true end
    end
    if not active then status = 'AI 已关闭'; return status end
    local path = mp.get_property_native('user-data/animejanai/stats-path')
        or mp.command_native({'expand-path', '~~/../animejanai/currentanimejanai.log'})
    local file = path and io.open(path, 'r')
    status = file and file:read(65536) or nil
    if file then file:close() end
    if not status or status == '' then status = 'mpv-AnimeFusion 状态暂不可用' end
    return status
end

local function render()
    if not visible then return end
    local now = mp.get_time()
    local fps = actual(now)
    local target = mp.get_property_number('estimated-vf-fps')
    local speed = mp.get_property_number('speed', 1) or 1
    local line = 'FPS: ' .. (fps and string.format('%.2f', fps) or '--')
    line = line .. ' / ' .. (target and target > 0 and string.format('%.2f', target * speed) or '--')
    local gpu=metrics.read and metrics.read(now).gpu
    line = line .. '    GPU ' .. (gpu and string.format('%.0f%%',gpu) or '—')
    -- Like mpv's built-in stats persistent_overlay, keep statistics out of
    -- the transient show-text slot used by playback and danmaku notices.
    -- Follow upstream stats.lua's 288-line ASS canvas and 20-point font.
    -- This panel has its own typography instead of inheriting message OSD size.
    local scale=288/720
    if not mp.get_property_bool('osd-scale-by-window',true) then
        scale=288/math.max(1,mp.get_property_number('osd-height',720))
    end
    local style=string.format('{\\r\\an7\\fs%.2f\\bord%.2f\\shad0\\q0}',20*scale,1.65*scale)
    mp.set_osd_ass(0,0,style..mp.command_native({'escape-ass',ai_status(now)..'\n\n'..line}))
end

local function toggle()
    visible = not visible
    reset()
    if visible then
        render()
        timer = mp.add_periodic_timer(INTERVAL, render)
    else
        if timer then timer:kill(); timer = nil end
        mp.set_osd_ass(0,0,'')
    end
end
for _, event in ipairs({'start-file', 'file-loaded', 'seek', 'playback-restart', 'end-file'}) do
    mp.register_event(event, reset)
end
mp.observe_property('pause', 'bool', function() reset(); if visible then render() end end)
mp.observe_property('speed', 'number', reset)
mp.observe_property('vf', 'native', reset)
-- Slot commands reconfigure the existing filter, so the vf property does not
-- change. Do not mix the previous slot's frames with the new target rate.
mp.observe_property('user-data/animejanai/requested-slot', 'number', function()
    reset()
    if metrics.reset then metrics.reset() end
    if visible then render() end
end)
mp.add_key_binding('Ctrl+TAB', 'show_animejanai_stats', toggle)
mp.register_event('shutdown', function() if metrics.reset then metrics.reset() end end)

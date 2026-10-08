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

-- Run with Windows LuaJIT; no window, external monitor, or video is opened.
local ffi=require 'ffi'
if ffi.os~='Windows' then print('SKIP Windows GPU counters');return end
ffi.cdef[[void __stdcall Sleep(unsigned long);]]
local k=ffi.load('kernel32')
local m=dofile(assert(arg[1])..'/portable_config/script-modules/player_ui_metrics.lua')
assert(m.read and m.reset,'Windows metrics API failed to initialize')
assert(m.read(0).gpu==nil,'GPU delta must warm up')
k.Sleep(1100)
local sample=m.read(1.1)
assert(sample.gpu and sample.gpu>=0 and sample.gpu<=100,'GPU utilization unavailable')
for i=1,50 do assert(m.read(1.1+i/100).gpu==sample.gpu,'GPU sampling must be cached for one second') end
m.reset();assert(m.read(2).gpu==nil,'Reopened counter must start a new delta')
m.reset()
print(string.format('PASS Windows GPU metrics: %.2f%%, one-second cache, query close/reset',sample.gpu))

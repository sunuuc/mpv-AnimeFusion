-- Deterministic tests for native slots and the upstream engine monitor. No GPU/network.
local real=require'mp'
local msg=require'mp.msg'
local root=real.get_property('script-opts'):match('r2root=([^,]+)')
local saved={mp=package.loaded.mp,options=package.loaded['mp.options'],open=io.open}
local function test()
  local paused,fail_once=false,false
  local messages,slots,props,timers={}, {}, {}, {}
  local seeks,polls=0,0
  local statustext
  package.loaded.mp={
    get_time=function() return 10 end,
    get_property_native=function(name) if name=='pause' then return paused end end,
    get_property_bool=function(name) if name=='pause' then return paused end end,
    set_property_bool=function(name,value) assert(name=='pause');paused=value end,
    set_property_native=function(name,value) props[name]=value end,
    register_script_message=function(name,fn) messages[name]=fn end,
    commandv=function(command,label,action,slot)
      assert(command=='vf-command' and label=='aji')
      if action=='poll' then polls=polls+1;return true end
      assert(action=='slot')
      if fail_once then fail_once=false;return false end
      slots[#slots+1]=tonumber(slot);return true
    end,
    command=function(command) assert(command=='no-osd seek 0 exact');seeks=seeks+1 end,
    command_native=function(command) assert(command[1]=='expand-path');return 'stats.log' end,
    osd_message=function() end,
    add_periodic_timer=function(_,fn) timers[#timers+1]=fn end,
  }
  package.loaded['mp.options']={read_options=function() end}
  io.open=function(path)
    if path=='stats.log' and statustext then return {read=function() return statustext end,close=function() end} end
  end
  dofile(root..'/portable_config/scripts/animejanai_slot.lua')
  assert(messages['aji-slot'],'native slot message is registered')
  messages['aji-slot']('8')
  assert(slots[#slots]==8 and props['user-data/animejanai/requested-slot']==8)
  assert(seeks==0,'playing slot switch must not seek')
  messages['aji-slot']('9');messages['aji-slot']('6')
  assert(#slots==3 and slots[#slots]==6,'rapid commands follow native ordering')
  fail_once=true;messages['aji-slot']('5')
  assert(#slots==3 and props['user-data/animejanai/requested-slot']==6,'failed command must not publish a new slot')
  paused=true;messages['aji-slot']('4')
  assert(slots[#slots]==4 and seeks==1,'paused switch refreshes once')
  dofile(root..'/portable_config/scripts/animejanai_session.lua')
  assert(#timers==0,'slot and retired session controllers must not start polling')
  dofile(root..'/portable_config/scripts/animejanai_engine_monitor.lua')
  assert(#timers==1,'engine build monitoring stays with its upstream owner')
  paused=false;timers[1]();assert(not paused,'ordinary startup must not wait for engine status')
  statustext='Building TensorRT engine for fixture for 1920x1080'
  timers[1]();assert(paused and polls==1,'an explicit engine build pauses and wakes the filter')
  statustext='Active Upscale Chain: 1'
  timers[1]();assert(not paused,'engine completion releases its own pause')
  msg.info('PASS controls r4: native-slot/single/rapid/failure/pause/engine-build')
end
local ok,err=xpcall(test,debug.traceback)
package.loaded.mp=saved.mp;package.loaded['mp.options']=saved.options;io.open=saved.open
if not ok then msg.error(err) end
real.commandv('quit',ok and 0 or 1)

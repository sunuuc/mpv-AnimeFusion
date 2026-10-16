-- Deterministic tests for native slots and the upstream engine monitor. No GPU/network.
local real=require'mp'
local msg=require'mp.msg'
local root=real.get_property('script-opts'):match('r2root=([^,]+)')
local saved={mp=package.loaded.mp,options=package.loaded['mp.options'],open=io.open}
local function test()
  local paused,fail_once=false,false
  local messages,slots,props,timers={}, {}, {}, {}
  local events,observers,osd={}, {}, {}
  local seeks,polls=0,0
  local reads=0
  local idle,path,video,filters=true,nil,nil,{}
  local statustext
  package.loaded.mp={
    get_time=function() return 10 end,
    get_property=function(name) if name=='path' then return path end end,
    get_property_native=function(name)
      if name=='pause' then return paused elseif name=='vf' then return filters
      elseif name=='video-out-params' then return video end
    end,
    get_property_bool=function(name)
      if name=='pause' then return paused elseif name=='idle-active' then return idle end
    end,
    set_property_bool=function(name,value)
      assert(name=='pause');paused=value
      if observers.pause then observers.pause('pause',value) end
    end,
    set_property_native=function(name,value) props[name]=value end,
    register_script_message=function(name,fn) messages[name]=fn end,
    register_event=function(name,fn) events[name]=fn end,
    observe_property=function(name,_,fn) observers[name]=fn end,
    commandv=function(command,label,action,slot)
      assert(command=='vf-command' and label=='aji')
      if action=='poll' then polls=polls+1;return true end
      assert(action=='slot')
      if fail_once then fail_once=false;return false end
      slots[#slots+1]=tonumber(slot);return true
    end,
    command=function(command) assert(command=='no-osd seek 0 exact');seeks=seeks+1 end,
    command_native=function(command) assert(command[1]=='expand-path');return 'stats.log' end,
    osd_message=function(text) osd[#osd+1]=text end,
    add_periodic_timer=function(_,fn)
      local timer={callback=fn,enabled=true}
      function timer:kill() self.enabled=false end
      function timer:resume() self.enabled=true end
      timers[#timers+1]=timer
      return timer
    end,
  }
  package.loaded['mp.options']={read_options=function() end}
  io.open=function(path)
    if path=='stats.log' then
      reads=reads+1
      if statustext then return {read=function() return statustext end,close=function() end} end
    end
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
  local timer=timers[1]
  local function tick() if timer.enabled then timer.callback() end end
  local function property(name,value)
    if name=='pause' then paused=value elseif name=='idle-active' then idle=value
    elseif name=='path' then path=value elseif name=='vf' then filters=value
    elseif name=='video-out-params' then video=value end
    observers[name](name,value)
  end
  local function start(name)
    events['start-file']()
    property('path',name)
    property('idle-active',false)
    property('video-out-params',nil)
    events['file-loaded']()
  end
  local function ready()
    property('video-out-params',{w=1920,h=1080})
    events['video-reconfig']()
    events['playback-restart']()
  end
  paused=false
  statustext='Building TensorRT engine for fixture for 1920x1080'
  tick();timer.callback()
  assert(not timer.enabled and not paused and reads==0 and polls==0 and #osd==0,
    'idle startup ignores a stale build log, even if a queued timer callback fires')
  property('vf',{{name='animejanai',label='aji',enabled=true}})
  start('first.mkv');tick();events['playback-restart']();tick()
  assert(not timer.enabled and reads==0 and not paused,
    'a loaded file without configured video output cannot consume the previous build log')
  ready();property('vf',filters);tick()
  assert(timer.enabled and paused and polls==1,'an explicit engine build pauses and wakes the active filter')
  statustext='Active Upscale Chain: 1'
  tick();assert(not paused and osd[#osd]:find('completed successfully',1,true),
    'engine completion releases its own pause and reports the live result')

  property('pause',true)
  statustext='Building TensorRT engine for fixture for 1920x1080';tick()
  statustext='Active Upscale Chain: 1';tick()
  assert(paused,'engine completion preserves a pre-existing manual pause')
  property('pause',false)
  statustext='Building TensorRT engine for fixture for 1920x1080';tick()
  assert(paused)
  property('pause',false);property('pause',true)
  statustext='Active Upscale Chain: 1';tick()
  assert(paused,'manual unpause and repause between polls relinquishes monitor ownership')

  property('pause',false)
  statustext='Building TensorRT engine for fixture for 1920x1080';tick()
  local before_reads,before_polls=reads,polls
  events['end-file']();property('idle-active',true);tick();timer.callback()
  assert(not timer.enabled and not paused and reads==before_reads and polls==before_polls and osd[#osd]=='',
    'ending playback cancels the build display and its pause without announcing success')
  start('second.mkv');tick()
  assert(reads==before_reads,'a new file does not inherit the interrupted build status before output is ready')
  statustext='Active Upscale Chain: 1';ready();tick()
  assert(not paused and osd[#osd]=='','a new ready file does not complete the previous file build')

  statustext='Building TensorRT engine for fixture for 1920x1080';tick()
  before_reads,before_polls=reads,polls
  property('vf',{});tick();timer.callback()
  assert(not timer.enabled and not paused and reads==before_reads and polls==before_polls and osd[#osd]=='',
    'removing the native filter stops polling and clears the build state')
  property('vf',{{name='animejanai',label='aji',enabled=false}});ready();tick()
  assert(not timer.enabled and reads==before_reads,'a disabled filter cannot revive stale build status')
  property('vf',{{name='animejanai',label='aji',enabled=true}});tick()
  assert(not timer.enabled,'adding a filter waits for native video reconfiguration')
  ready();tick();assert(paused)
  property('path','third.mkv');tick()
  assert(not timer.enabled and not paused and osd[#osd]=='',
    'a path transition invalidates the old build before its next timer callback')
  property('pause',true);start('third.mkv');ready();tick();events['end-file']()
  assert(paused and not timer.enabled and osd[#osd]=='',
    'ending a build preserves a pre-existing user-owned pause')
  property('pause',false);start('fourth.mkv');ready();tick()
  statustext='Engine build FAILED for fixture (see fixture.build.log); playing without this chain';tick()
  assert(not paused and osd[#osd]:find('failed.',1,true) and not osd[#osd]:find('successfully',1,true),
    'a native build failure resumes playback with its failure message')
  msg.info('PASS controls r5: native slots; engine lifecycle, stale log, pause ownership and filter removal')
end
local ok,err=xpcall(test,debug.traceback)
package.loaded.mp=saved.mp;package.loaded['mp.options']=saved.options;io.open=saved.open
if not ok then msg.error(err) end
real.commandv('quit',ok and 0 or 1)

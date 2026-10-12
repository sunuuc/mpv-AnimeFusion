local mp=require 'mp'
local msg=require 'mp.msg'
local utils=require 'mp.utils'
local out=assert(mp.get_property('script-opts',''):match('playeruiout=([^,]+)'))
local checks,done=0,false
local function ui()return mp.get_property_native('user-data/player_ui/ui',{})end
local function finish(ok,err)
 if done then return end;done=true
 if ok then msg.info('PASS Player UI Windows UI: '..checks..' checks')
 else
  msg.error(tostring(err))
  local x,y=mp.get_mouse_pos()
  local state={ui=ui(),mouse={x=x,y=y},error=tostring(err)}
  local f=io.open(out..'/failure-state.json','wb');if f then f:write(utils.format_json(state));f:close()end
 end
 mp.commandv('quit',ok and 0 or 1)
end
local function guard(fn)if not done then local ok,e=xpcall(fn,debug.traceback);if not ok then finish(false,e)end end end
local function after(delay,fn)mp.add_timeout(delay,function()guard(fn)end)end
local function check(ok,m)checks=checks+1;assert(ok,m);msg.info('PASS '..m)end
local function wait_for(predicate,message,then_)
 local deadline=mp.get_time()+2.5
 local function poll()
  if predicate()then then_()
  elseif mp.get_time()<deadline then after(.025,poll)
  else error(message..': '..utils.format_json(ui()))end
 end
 after(.025,poll)
end
local function button(id)for _,b in ipairs(ui().controls or {})do if b.id==id then return b end end end
local function click(id,then_)
 local u=ui();local b=assert(button(id),'missing UI control: '..id)
 local x=math.floor((b.x0+b.x1)*u.scale/2);local y=math.floor((b.y0+b.y1)*u.scale/2)
 mp.commandv('mouse',x,y)
 local deadline=mp.get_time()+1.5
 local function ready()
  local current=ui()
  assert(current.overlay_ok,'overlay was not accepted: '..tostring(current.overlay_error))
  if current.hover==id then
   mp.commandv('keypress','MBTN_LEFT');after(.15,then_)
  elseif mp.get_time()<deadline then after(.025,ready)
  else error('mouse did not reach '..id..': '..utils.format_json(current))end
 end
 after(.04,ready)
end
local function row(key)
 for i,r in ipairs(ui().rows or {})do if r.key==key or r.target==key or r.text==key then return 'row-'..i end end
 error('missing row '..key)
end
local function has_row(key)
 for _,r in ipairs(ui().rows or {})do if r.key==key or r.target==key or r.text==key then return true end end
 return false
end
local function menu(kind,then_)
 mp.commandv('script-message','player_ui-menu',kind)
 wait_for(function()return ui().menu==kind end,'menu did not open: '..kind,then_)
end
local function close(then_)mp.commandv('script-message','player_ui-hide');after(.1,function()mp.commandv('script-message','player_ui-show');after(.1,then_)end)end
local function set_slider(row_key,fraction,then_)
 local index
 for i,r in ipairs(ui().rows or {})do if r.key==row_key or r.text==row_key then index=i;break end end
 local id='slider-'..tostring(index)
 local slider=assert(index and button(id),'missing slider row '..row_key);local u=ui()
 local x=slider.start+(slider.finish-slider.start)*fraction
 local y=(slider.y0+slider.y1)/2
 mp.commandv('mouse',math.floor(x*u.scale),math.floor(y*u.scale))
 after(.06,function()mp.commandv('keypress','MBTN_LEFT');after(.15,then_)end)
end
local function scroll_menu_down(kind,wheels,then_)
 local bar=button('menu-scroll-'..kind);local u=ui()
 if not bar then
  check(ui().menu==kind and #ui().rows>0,'menu fits without scrolling '..kind)
  then_();return
 end
 mp.commandv('mouse',math.floor((bar.x0+bar.x1)*u.scale/2),math.floor((bar.y0+bar.y1)*u.scale/2))
 local function wheel_left(n)
  if n==0 then after(.1,function()
   local box
   for _,candidate in ipairs(ui().menu_boxes or {})do if candidate.kind==kind then box=candidate end end
   check(box and box.offset>0,'mouse wheel scrolls '..kind)
   then_()
  end)else
   mp.commandv('keypress','WHEEL_DOWN');after(.04,function()wheel_left(n-1)end)
  end
 end
 wheel_left(wheels)
end
local function verify_overlay(name)
 check(ui().overlay_ok,'native overlay accepted at '..name)
end
local steps={}
steps[#steps+1]=function(next_)
 check(ui().version=='1.3.0','Player UI controller version')
 check(mp.get_property_number('vo-presented-frame-count',0)>0,'native video output')
 check(not ui().loading and ui().visible,'paused first frame exits loading and displays controls')
 check(button('previous') and button('play') and button('next') and button('volume') and button('speed'),
  'Player UI playback controls are present')
 check(button('settings') and button('audio') and button('sub') and button('danmaku')
  and not button('playlist') and button('fullscreen'),'Player UI utility controls omit the playlist')
 check(not button('close'),'window close remains a native title-bar control')
 check(not button('ai') and not button('stats') and not button('performance'),'secondary features are inside settings')
 for i,b in ipairs(ui().controls)do
  check(b.x0>=0 and b.y0>=0 and b.x1*ui().scale<=ui().width+1,'bounds '..b.id)
  for j=i+1,#ui().controls do local c=ui().controls[j]
   check(b.x1<=c.x0 or c.x1<=b.x0 or b.y1<=c.y0 or c.y1<=b.y0,'nonoverlap '..b.id..'/'..c.id)
  end
 end
 verify_overlay('player_ui-player');next_()
end
steps[#steps+1]=function(next_)
 local u=ui();local b=assert(button('settings'),'settings control for pointer-retention check')
 mp.commandv('mouse',math.floor((b.x0+b.x1)*u.scale/2),math.floor((b.y0+b.y1)*u.scale/2))
 after(1.9,function()
  check(ui().visible and ui().hover=='settings','HUD remains visible while the pointer rests on a bottom control')
  next_()
 end)
end
steps[#steps+1]=function(next_)
 menu('settings',function()
  check(ui().menu=='settings' and not button('close') and not button('menu-close'),
   'settings popover leaves window chrome to mpv.net')
  check(has_row('弹幕设置'),
   'settings menu owns danmaku settings')
  click(row('ai'),function()
   check(ui().menu=='ai' and #ui().menu_boxes==2,'AI submenu opens beside the settings parent')
   check(not button('close') and not button('menu-close'),'child popover has no drawn close controls')
   local parent,child=ui().menu_boxes[1],ui().menu_boxes[2]
   check(child.x1<parent.x0 or child.x0>parent.x1,'child popover does not overlap its parent')
   mp.commandv('keypress','ESC');after(.12,function()
    check(ui().menu=='settings' and #ui().menu_boxes==1,'Escape returns from child popover to its parent')
    mp.commandv('keypress','ESC');after(.12,function()
     check(ui().menu=='','Escape closes the parent popover')
     next_()
    end)
    end)
   end)
  end)
end
steps[#steps+1]=function(next_)
 local paused=mp.get_property_bool('pause',false)
 click('play',function()
  check(mp.get_property_bool('pause',false)~=paused,'play button toggles playback')
  click('play',function()
   check(mp.get_property_bool('pause',false)==paused,'play button restores pause state')
   local muted=mp.get_property_bool('mute',false)
   click('volume',function()
    check(mp.get_property_bool('mute',false)~=muted,'volume button toggles mute')
    click('volume',function()
     check(mp.get_property_bool('mute',false)==muted,'volume button restores mute state')
     mp.set_property_number('time-pos',6)
     click('previous',function()
      check(mp.get_property_number('time-pos',6)<6,'previous seeks backward for one-item playback')
      mp.set_property_number('time-pos',2)
      click('next',function()
       check(mp.get_property_number('time-pos',2)>2,'next seeks forward for one-item playback')
       click('seek',function()
        check(mp.get_property_number('time-pos',0)>4,'timeline click seeks to its selected position')
        local was_fullscreen=mp.get_property_bool('fullscreen',false)
        click('fullscreen',function()
         after(.1,function()
          check(mp.get_property_bool('fullscreen',false)~=was_fullscreen,'fullscreen button enters fullscreen')
          click('fullscreen',function()
           after(.1,function()
            check(mp.get_property_bool('fullscreen',false)==was_fullscreen,'fullscreen button restores windowed playback')
            mp.set_property_number('volume',30)
            click('volume',function()
             local slider=assert(button('volume-slider'),'the compact volume percentage chip remains adjustable')
             local u=ui();mp.commandv('mouse',math.floor(slider.x1*u.scale),math.floor((slider.y0+slider.y1)*u.scale/2))
             after(.08,function()
              mp.commandv('keypress','MBTN_LEFT');after(.15,function()
               check(mp.get_property_number('volume',0)>=95,'volume slider changes volume')
               mp.set_property_number('volume',50);mp.set_property_number('time-pos',0);next_()
              end)
             end)
            end)
           end)
          end)
         end)
        end)
       end)
     end)
     end)
    end)
   end)
   end)
   end)
 end
steps[#steps+1]=function(next_)
 mp.set_property_bool('pause',true);mp.set_property_number('time-pos',0)
 wait_for(function()return mp.get_property_number('time-pos',0)<.05 end,'volume-key baseline seek settles',function()
  local t=mp.get_property_number('time-pos',0);mp.set_property_number('volume',50);mp.commandv('keypress','UP')
  after(.1,function()
   check(mp.get_property_number('volume')==55,'Up raises volume once');mp.commandv('keypress','DOWN')
   after(.1,function()
    local volume_now,time_now=mp.get_property_number('volume'),mp.get_property_number('time-pos',0)
    check(volume_now==50 and math.abs(time_now-t)<.05,
     'Down changes volume without seeking (volume='..tostring(volume_now)..', time='..tostring(time_now)..', before='..tostring(t)..')')
    next_()
   end)
  end)
 end)
end
steps[#steps+1]=function(next_)
 click('speed',function()
  check(ui().menu=='speed','speed popover opens')
  local speed,audio=button('speed'),button('audio')
  local expected_width=216*math.abs(speed.x-audio.x)/48
  local b=ui().menu_boxes[1]
  check(math.abs((b.x1-b.x0)-expected_width)<.01,'speed popover scales from the Hills base width')
  check(ui().rows[1].text=='8.0x' and ui().rows[8].text=='0.5x','descending speed order');verify_overlay('player_ui-speed')
  local levels={{'8.0x',8},{'5.0x',5},{'3.0x',3},{'2.0x',2},{'1.5x',1.5},{'1.25x',1.25},{'1.0x',1},{'0.5x',.5}}
  local apply
  local function apply_row(i)
   local function click_option()
    click(row(levels[i][1]),function()
     check(math.abs(mp.get_property_number('speed')-levels[i][2])<.001,'speed option '..levels[i][1]..' applies')
     apply(i+1)
    end)
   end
   if i==8 then scroll_menu_down('speed',4,click_option)else click_option()end
  end
  apply=function(i)
   if i>#levels then mp.set_property_number('speed',1);next_();return end
   if ui().menu=='speed' then apply_row(i)else click('speed',function()apply_row(i)end)end
  end
  apply(1)
 end)
end
steps[#steps+1]=function(next_)
 local f=assert(io.open(out..'/second-subtitle.srt','wb'));f:write('1\n00:00:00,000 --> 00:00:12,000\nSecondary subtitles\n');f:close()
 mp.commandv('sub-add',out..'/second-subtitle.srt','auto')
 after(.15,function()
  click('sub',function()
   check(ui().menu=='sub' and not button('subtitle-slot-1') and not button('subtitle-slot-2'),
    'subtitle chooser removes the unused 1/2 selector')
   verify_overlay('player_ui-subtitles')
   click(row('sid:2'),function()
    check(mp.get_property_number('sid')==2,'subtitle chooser selects a primary subtitle track')
    click(row('sid:no'),function()check(mp.get_property('sid')=='no','subtitle off applies to the primary track');close(next_)end)
   end)
  end)
 end)
end
steps[#steps+1]=function(next_)
 click('audio',function()
  check(ui().menu=='audio','audio menu')
  click(row('aid:no'),function()
   check(mp.get_property('aid')=='no','audio disabled')
   click(row('aid:1'),function()check(mp.get_property_number('aid')==1,'audio restored');close(next_)end)
  end)
 end)
end
steps[#steps+1]=function(next_)
 click('settings',function()
  check(ui().menu=='settings','settings root');verify_overlay('player_ui-settings')
  click(row('ai'),function()
   check(ui().menu=='ai' and #ui().menu_boxes==2,'AI submenu beside settings')
   local a,b=ui().menu_boxes[1],ui().menu_boxes[2];check(b.x1<a.x0 and b.y0>=0,'left submenu placement')
   for n=1,9 do check(has_row('preset:'..n),'fixture profile '..n..' is retained')end
   check(not has_row('质量') and not has_row('性能') and not has_row('均衡'),'removed built-in presets are absent')
   verify_overlay('player_ui-ai-presets')
   click(row('preset:0'),function()check(#(mp.get_property_native('vf',{})or{})==0,'AI off does not create a graph');next_()end)
  end)
 end)
end
steps[#steps+1]=function(next_)
 click('settings',function()click(row('scale'),function()
  verify_overlay('player_ui-scale')
  click(row('填充裁剪'),function()
   check(mp.get_property_number('panscan')==1 and mp.get_property_bool('keepaspect'),'real fill/crop mode')
   mp.set_property_number('panscan',0);next_()
  end)
 end)end)
end
steps[#steps+1]=function(next_)
 click('settings',function()click(row('scale'),function()
  click(row('拉伸铺满'),function()
   check(not mp.get_property_bool('keepaspect',true) and mp.get_property('video-unscaled')=='no','stretch mode disables aspect preservation')
   next_()
  end)
 end)end)
end
steps[#steps+1]=function(next_)
 click('settings',function()click(row('scale'),function()
  click(row('原始尺寸'),function()
   check(mp.get_property_bool('keepaspect') and mp.get_property('video-unscaled')=='yes','original-size mode applies')
   next_()
  end)
 end)end)
end
steps[#steps+1]=function(next_)
 click('settings',function()click(row('scale'),function()
  click(row('适应窗口'),function()
   check(mp.get_property_bool('keepaspect') and mp.get_property('video-unscaled')=='no'
    and mp.get_property_number('panscan')==0,'fit mode restores the default presentation')
   next_()
  end)
 end)end)
end
steps[#steps+1]=function(next_)
 click('settings',function()click(row('sub-settings'),function()
  local visible=mp.get_property_bool('sub-visibility',true)
  click(row('显示字幕'),function()
   check(mp.get_property_bool('sub-visibility',true)~=visible,'subtitle visibility toggle applies')
  set_slider('字号缩放',1,function()
    check(mp.get_property_number('sub-scale',0)==2,'subtitle size slider reaches its maximum')
    set_slider('字幕位置',1,function()
     check(mp.get_property_number('sub-pos',0)==100,'subtitle position slider reaches its maximum')
     scroll_menu_down('sub-settings',6,function()
      click(row('提前 0.1 秒'),function()
       check(math.abs(mp.get_property_number('sub-delay',0)+.1)<.001,'subtitle delay advances earlier by 0.1 seconds')
       click(row('延后 0.1 秒'),function()
        check(mp.get_property_number('sub-delay',-1)==0,'subtitle delay returns by 0.1 seconds')
        click(row('延后 0.1 秒'),function()
         check(math.abs(mp.get_property_number('sub-delay',0)-.1)<.001,'subtitle delay advances later by 0.1 seconds')
         click(row('重置延迟'),function()
          check(mp.get_property_number('sub-delay',-1)==0,'subtitle delay resets')
          close(next_)
         end)
        end)
       end)
      end)
     end)
    end)
   end)
  end)
 end)end)
end
steps[#steps+1]=function(next_)
 click('audio',function()click(row('audio-settings'),function()
  click(row('提前 0.1 秒'),function()
   check(math.abs(mp.get_property_number('audio-delay',0)+.1)<.001,'audio delay advances earlier by 0.1 seconds')
   click(row('延后 0.1 秒'),function()
    check(mp.get_property_number('audio-delay',-1)==0,'audio delay returns by 0.1 seconds')
    click(row('延后 0.1 秒'),function()
     check(math.abs(mp.get_property_number('audio-delay',0)-.1)<.001,'audio delay advances later by 0.1 seconds')
     click(row('重置延迟'),function()
      check(mp.get_property_number('audio-delay',-1)==0,'audio delay resets')
      close(next_)
     end)
    end)
   end)
  end)
 end)end)
end
steps[#steps+1]=function(next_)
 click('settings',function()
  mp.commandv('mouse',math.floor(ui().scale),math.floor(ui().scale))
  after(.7,function()
   check(ui().menu=='','popover retracts after the pointer leaves its surface')
   next_()
  end)
 end)
end
steps[#steps+1]=function(next_)
 local sid,aid=mp.get_property('sid'),mp.get_property('aid')
 mp.commandv('script-message','player_ui-danmaku-load',out..'/comments.xml')
 wait_for(function()
  local d=mp.get_property_native('user-data/player_ui/danmaku',{})
  return d.loaded and not d.render_pending and d.count==3
 end,'native XML danmaku did not load',function()
  local d=mp.get_property_native('user-data/player_ui/danmaku',{})
  check(d.loaded and d.count==3,'XML danmaku loaded')
  check(mp.get_property('sid')==sid and mp.get_property('aid')==aid,'independent danmaku layer')
  click('danmaku',function()
   verify_overlay('player_ui-danmaku');check(ui().rows[2].selected,'loaded danmaku entry selected')
   check(not has_row('弹幕设置'),'Danmaku menu does not duplicate settings')
   local loaded_label=ui().rows[2].text
   click(row('关闭'),function()
    check(not mp.get_property_native('user-data/player_ui/danmaku',{}).enabled,'close danmaku disables the loaded overlay')
    click(row(loaded_label),function()
     check(mp.get_property_native('user-data/player_ui/danmaku',{}).enabled,'selecting the loaded item re-enables it')
     check(not has_row('隐藏弹幕') and not has_row('显示弹幕'),'danmaku menu omits duplicate visibility actions')
     check(has_row('导入本地弹幕'),'danmaku import action is named consistently')
       menu('settings',function()
        click(row('弹幕设置'),function()
         verify_overlay('player_ui-danmaku-settings')
         check(ui().menu=='danmaku-settings' and #ui().menu_boxes==2 and has_row('速度'),
          'compact danmaku preferences appear inside Settings')
         set_slider('不透明度',1,function()
          check(mp.get_property_native('user-data/player_ui/danmaku',{}).settings.opacity==255,'opacity reaches 100 percent')
          set_slider('显示区域',0,function()
           local settings=mp.get_property_native('user-data/player_ui/danmaku',{}).settings
           check(settings.displayArea==.1 and settings.scrollArea==1,'one area control reaches ten percent')
           mp.commandv('script-message','player_ui-danmaku-clear')
           close(next_)
          end)
         end)
        end)
       end)
    end)
   end)
  end)
 end)
end
steps[#steps+1]=function(next_)
 click('settings',function()click(row('stats'),function()
  after(.9,function()
   check((ui().performance or {}).fps==0,'paused actual FPS');verify_overlay('player_ui-stats')
   mp.set_property_bool('pause',false)
   after(2,function()
    local fps=(ui().performance or {}).fps;check(type(fps)=='number' and fps>0,'playing actual FPS')
    mp.set_property_bool('pause',true);mp.commandv('keypress','ESC')
    wait_for(function()return ui().menu=='settings'end,'Esc returns to settings',function()
     check(ui().menu=='settings','Esc returns to settings')
     after(.3,function()
      check(ui().menu=='settings','escaped submenu stays closed under stationary pointer');close(next_)
     end)
    end)
   end)
  end)
 end)end)
end
steps[#steps+1]=function(next_)
 -- Queued media does not expose a playlist surface in this player.
 for i=1,8 do mp.commandv('loadfile',out..'/second.y4m','append')end
 after(.3,function()
  check(mp.get_property_number('playlist-count',0)==9,'nine entries are loaded')
  check(button('playlist')==nil and ui().menu~='playlist','queued media does not show a playlist control or drawer')
  mp.set_property_number('playlist-pos',0);close(next_)
 end)
end
steps[#steps+1]=function(next_)
 mp.set_property_number('window-scale',2);mp.commandv('script-message','player_ui-show')
 wait_for(function()
  local u=ui();return u.width>0 and u.height>0 and u.overlay_ok
 end,'compact window did not settle',function()
  check(ui().width>0 and ui().height>0,'compact window resized')
  -- Physical mouse movement during a Win32 resize is nondeterministic on WARP.
  -- All real mouse paths were exercised above; use the public menu messages here
  -- to validate only the compact geometry after the resize has settled.
  menu('settings',function()
   menu('ai',function()
    for _,b in ipairs(ui().menu_boxes)do
     check(b.x0>=0 and b.y0>=0 and b.x1*ui().scale<=ui().width+1 and b.y1*ui().scale<=ui().height+1,'compact menu fits screen')
    end
    verify_overlay('player_ui-compact');next_()
   end)
  end)
 end)
end
local index=0
local function next_step()index=index+1;if index>#steps then finish(true)else guard(function()steps[index](next_step)end)end end
local started=false
mp.register_event('file-loaded',function()
 if started then return end;started=true;local attempts=0
 local function wait()
  attempts=attempts+1
  if ui().overlay_error then error('native overlay rejected: '..ui().overlay_error)end
  if ui().version and ui().overlay_ok and ui().visible and mp.get_property_number('vo-presented-frame-count',0)>0 then next_step()
  elseif attempts<30 then after(.2,wait)else error('UI initialization timeout')end
 end
 after(.5,wait)
end)
mp.add_timeout(80,function()finish(false,'Player UI UI test deadline exceeded')end)

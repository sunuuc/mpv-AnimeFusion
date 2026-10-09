local ok,real=pcall(require,'mp')
local logger=ok and require('mp.msg') or nil
local root=ok and real.get_property('script-opts'):match('playeruiroot=([^,]+)') or arg[1]
local out=ok and real.get_property('script-opts'):match('playeruiout=([^,]+)') or ''
local focus=ok and real.get_property('script-opts'):match('playeruifocus=([^,]+)') or nil
local core=dofile(assert(root)..'/portable_config/script-modules/player_ui_core.lua')
local checks=0
local function check(v,label) checks=checks+1;assert(v,label) end
local function suite()
 check(core.time(3661)=='1:01:01' and core.rate(1250000)=='1.25 MB/s','units')
 check(core.title('', 'https://server.invalid/a/movie.mkv?token=secret')=='movie.mkv','private URL query hidden')
 local show,episode=core.title_lines('作品名字 (2025) S03E07 - 剧集名字','')
 check(show=='作品名字' and episode=='S3:E7 - 剧集名字','season and episode move below the clean series title')
 show,episode=core.title_lines('Series Name S2:E12.5 - Episode Name','')
 check(show=='Series Name' and episode=='S2:E12.5 - Episode Name','title layout preserves explicit season and fractional episode numbers')
 show,episode=core.title_lines('电影名字 (2023)','')
 check(show=='电影名字 (2023)' and episode=='','a movie does not invent a season or episode')
 check(core.escape('{\\pos(1,2)}'):find('\\{',1,true),'escape ASS text')
 check(core.char_count('A间谍')==3 and core.limit_chars('A间谍过家家',3)=='A间谍','danmaku text limits avoid character arrays')
 local volume_box=core.volume_osd_layout(1280,720,1,'音量 95%',16)
 check(math.abs(volume_box.center_x-640)<.001 and math.abs(volume_box.center_y-504)<.001,
  'volume popup is horizontally centered in the lower-middle of the video')
 check(volume_box.width<124 and volume_box.width>volume_box.text_width
  and volume_box.height==32 and math.abs(volume_box.x-(1280-volume_box.width)/2)<.001,
  'volume popup fits its percentage text with even padding and a straight centered box')
 for _,size in ipairs({{1280,720},{960,540},{640,360},{2560,1600},{3840,2160},{300,160}}) do
  for _,dpi in ipairs({1,1.25,1.5,2}) do
   local l=core.layout(size[1],size[2],dpi);local controls={};local ids={}
   for _,b in ipairs(l.controls)do controls[#controls+1]=b;ids[b.id]=b end
   if l.volume then controls[#controls+1]=l.volume end
   check(ids.play and ids.volume and ids.previous and ids.next and ids.speed and ids.audio
    and ids.fullscreen and ids.settings and ids.sub and ids.danmaku and not ids.playlist
    and not ids.ai and not ids.stats and not ids.performance,
    'Hills bottom row keeps the playback, track, danmaku and settings controls')
   check(ids.playlist==nil,'the player has no playlist button')
   check(ids.previous.x<ids.play.x and ids.play.x<ids.next.x and ids.next.x<ids.volume.x,
    'Hills playback controls keep their left-to-right order')
   check(ids.speed.x<ids.audio.x and ids.audio.x<ids.sub.x and ids.sub.x<ids.danmaku.x
    and ids.danmaku.x<ids.settings.x and ids.settings.x<ids.fullscreen.x,
    'Hills utility controls keep their left-to-right order')
   check(l.seek.y==l.h-core.metrics.seek_track_y*l.ui,'timeline sits above the bottom control row')
   check(l.seek.x0>=l.margin+60*l.ui and l.seek.x1<=l.w-l.margin-60*l.ui,
    'timeline leaves clear space for current and total time on both sides')
   check(l.title_y+core.metrics.title_font*l.ui<l.detail_y
    and l.detail_y+core.metrics.detail_font*l.ui<l.seek.y0,
    'bold title, smaller episode detail and timeline have separate vertical space')
   check(l.network_rate and l.network_rate.x0>=0 and l.network_rate.x1<=l.w
   and l.volume and l.network_rate.x0>=l.volume.x1
   and l.network_rate.x0-l.volume.x1<=core.metrics.network_gap*l.ui+.01
   and l.network_rate.x1<ids.speed.x0,
   'network rate sits immediately after the bottom volume slider and before the speed control')
   check(l.close==nil,'window chrome is not part of the in-video HUD layout')
   for i,b in ipairs(controls)do
    check(b.x0>=0 and b.x1<=l.w and b.y0>=0 and b.y1<=l.h,'screen bounds')
    for j=i+1,#controls do local c=controls[j];check(b.x1<=c.x0 or c.x1<=b.x0,
     'non-overlapping hit areas '..size[1]..'x'..size[2]..' dpi='..dpi..' '..tostring(b.id)..'['..b.x0..','..b.x1..'] / '..tostring(c.id)..'['..c.x0..','..c.x1..']')end
   end
   check(l.title_y>=0 and l.seek.x1>l.seek.x0,'title and seek bounds '..size[1]..'x'..size[2]..' dpi='..dpi..' title='..l.title_y..' seek='..l.seek.x0..':'..l.seek.x1)
  end
 end
 local account_layout=core.layout(2560,1600,1.5)
 local account_control
 for _,button in ipairs(account_layout.controls)do if button.id=='bangumi' then account_control=button end end
 check(account_control and account_control.x0>account_layout.network_rate.x1,'account entry remains visible without overlapping network or volume')
 local a,b=core.layout(1280,720,1),core.layout(2560,1440,1)
 check(a.scale==b.scale,'fullscreen does not enlarge controls proportionally')
 local f=core.fps_sampler()
 for i=0,8 do local v=f:sample(i*.25,i*12,false);if i==8 then check(v==48,'native full FPS')end end
 for i=9,16 do local v=f:sample(i*.25,96,false);if i==16 then check(v==0,'stalled FPS')end end
 check(f:sample(5,96,true)==0 and f:sample(5.25,nil,false)==nil,'paused/unavailable counter')
 check(#core.wrap(string.rep('很长的字幕标题',30),260,20,2)==2,'long labels bounded to two lines')
 local now,timers,bindings,messages,observers=0,{},{},{},{}
 local clock_config=os.tmpname();local f=assert(io.open(clock_config,'wb'));f:write('show_clock=yes\nnetwork_speed=yes\n');f:close()
 local pos={x=0,y=0};local commands={};local player_overlay_data,volume_overlay_data;local renderer_arguments
 local props={pause=false,['idle-active']=false,['window-minimized']=false,['playlist-count']=1,['playlist-pos']=0,
   ['time-pos']=10,duration=120,['file-size']=1024,seekable=true,volume=50,['volume-max']=100,speed=1,sid=2,aid=1,['secondary-sid']='no',
  ['track-list']={{id=1,type='audio',title='A',lang='jpn',codec='aac',['audio-channels']='2.0',selected=true},
   {id=2,type='sub',title='https://media.example/Stream.ass?token=private',lang='zh-Hans',codec='hdmv_pgs_subtitle',external=true,selected=true},
   {id=3,type='sub',title='S',codec='ass'}},
   ['playlist']={{filename='movie.mkv'}},['media-title']='Title',['video-params']={w=1920,h=1080},['container-fps']=24,
   ['display-fps']=144}
 local event_handlers,pending_async,json_responses={},{},{}
 local next_async=0
 local function timer(delay,fn,repeated)
  local t={at=now+delay,fn=fn,delay=delay,repeated=repeated,alive=true};function t:kill()self.alive=false end
  timers[#timers+1]=t;return t
 end
 local function advance(dt)
  local limit=now+dt;local n=0
  while true do
   local first
   for _,t in ipairs(timers)do if t.alive and t.at<=limit and(not first or t.at<first.at)then first=t end end
   if not first then break end
   now=first.at;if first.repeated then first.at=now+first.delay else first.alive=false end
   first.fn();n=n+1;assert(n<10000,'timer loop')
  end;now=limit
 end
 local fake={get_time=function()return now end,
  get_property_native=function(n,d)if props[n]~=nil then return props[n]end;return d end,
  get_property_number=function(n,d)if type(props[n])=='number'then return props[n]end;return d end,
  get_property_bool=function(n,d)if type(props[n])=='boolean'then return props[n]end;return d end,
  get_property=function(n,d)if props[n]~=nil then return tostring(props[n])end;return d end,
  set_property_native=function(n,v)props[n]=v;if n=='user-data/player_ui/danmaku' and observers[n] then observers[n](n,'native',v)end;return true end,
  set_property_number=function(n,v)props[n]=v;if observers[n] then observers[n](n,v)end;return true end,
  set_property_bool=function(n,v)props[n]=v;return true end,set_property=function(n,v)props[n]=v;return true end,
   create_osd_overlay=function()
    return {update=function(self)
      if self.z==20 then player_overlay_data=self.data elseif self.z==25 then volume_overlay_data=self.data end
     end,remove=function(self)
      if self.z==20 then player_overlay_data=nil elseif self.z==25 then volume_overlay_data=nil end
     end}
   end,
  get_osd_size=function()return 1280,720 end,get_mouse_pos=function()return pos.x,pos.y end,
  command_native=function(a)if a[1]=='overlay-add' then commands[#commands+1]=a;return true end;if a[1]=='expand-path'then if a[2]=='~~/script-opts/player_ui.conf' then return clock_config end;return a[2]:gsub('^~~/%.%./',root..'/'):gsub('^~~/',root..'/portable_config/')end end,
  command_native_async=function(spec,callback)
   next_async=next_async+1;pending_async[next_async]={spec=spec,callback=callback};return next_async
  end,abort_async_command=function(id)if pending_async[id]then pending_async[id].aborted=true end end,
  commandv=function(...)
   local a={...};commands[#commands+1]=a
   if a[1]=='cycle'then props[a[2]]=not props[a[2]]end
   if a[2]=='danmaku-action' and a[3]=='load' then
    renderer_arguments=a
    local f=assert(io.open(a[4],'rb'));local text=f:read('*a');f:close()
    local count=0;for _ in text:gmatch('<d ')do count=count+1 end
    observers['user-data/player_ui/danmaku-render'](nil,{serial=tonumber(a[6]),ready=true,busy=false,count=count,error=''})
   end
   return true
  end,
  osd_message=function()end,add_timeout=function(d,f)return timer(d,f,false)end,add_periodic_timer=function(d,f)return timer(d,f,true)end,
  add_key_binding=function(_,n,f)bindings[n]=f end,add_forced_key_binding=function(_,n,f)bindings[n]=f end,
  remove_key_binding=function(n)bindings[n]=nil end,register_script_message=function(n,f)messages[n]=f end,
  observe_property=function(n,_,f)observers[n]=f end,
  register_event=function(n,f)event_handlers[n]=event_handlers[n] or {};event_handlers[n][#event_handlers[n]+1]=f end}
 local saved={mp=package.loaded.mp,opts=package.loaded['mp.options'],utils=package.loaded['mp.utils']}
  package.loaded.mp=fake
  package.loaded['mp.options']={read_options=function(opts,name)
    if name=='player_ui_danmaku' then
     opts.api_servers='https://packaged.example|Must be ignored';opts.autoload_danmaku=true
   end
 end}
 package.loaded['mp.utils']={parse_json=function(text)return json_responses[text] or {}end,
  format_json=function(value)local key='json-'..tostring(value);json_responses[key]=value;return key end,
  getpid=function()return 12345 end,file_info=function()return nil end}
 dofile(root..'/portable_config/scripts/player_ui.lua');advance(.1)
 local function ui()return props['user-data/player_ui/ui']end
 local function button(id)for _,b in ipairs(ui().controls)do if b.id==id then return b end end end
 local function click(id)
  local b=assert(button(id),'missing button '..id);pos.x=(b.x0+b.x1)*.5*ui().scale;pos.y=(b.y0+b.y1)*.5*ui().scale
  bindings['player_ui-move']();advance(.04);bindings['player_ui-click']({event='down'});bindings['player_ui-click']({event='up'});advance(.05)
 end
 local function scroll_menu_down(kind,ticks)
  local box
  for _,candidate in ipairs(ui().menu_boxes or {})do if candidate.kind==kind then box=candidate end end
  assert(box,'missing menu box '..kind)
  pos.x=(box.x0+box.x1)*.5*ui().scale;pos.y=(box.y0+box.y1)*.5*ui().scale
  bindings['player_ui-move']();advance(.04)
  for _=1,ticks do bindings['player_ui-wheel-down']();advance(.04)end
  advance(.1)
 end
 local function row(key)
  for i,r in ipairs(ui().rows or {})do if r.key==key or r.target==key or r.text==key then return 'row-'..i end end
  error('missing menu row '..key)
 end
 local function has_row(key)
  for _,r in ipairs(ui().rows or {})do if r.key==key or r.target==key or r.text==key then return true end end
  return false
 end
 check(ui().version=='1.3.0','production layout revision')
 check(button('bangumi')~=nil,'logged-out account entry is in the bottom control bar')
 click('bangumi')
 check(ui().menu=='bangumi' and has_row('登录 Bangumi'),'account entry opens a bottom-bar popover')
 local account_box=ui().menu_boxes[1];local account_button=button('bangumi')
 check(account_box.y1<account_button.y0 and math.abs((account_box.x0+account_box.x1)/2-(account_button.x0+account_button.x1)/2)<1,
  'account popover anchors directly above its bottom-bar button')
 click(row('登录 Bangumi'))
 check(commands[#commands][1]=='script-message-to' and commands[#commands][3]=='bangumi-action' and commands[#commands][4]=='login',
  'login uses native browser authorization')
 observers['user-data/player_ui/bangumi']('user-data/player_ui/bangumi',{connected=true,username='user',avatar='fixture-avatar.bgra'})
 advance(.1)
 local avatar_count=0
 for _,command in ipairs(commands)do if command[1]=='overlay-add' and command[2]==61 then avatar_count=avatar_count+1 end end
 check(avatar_count==1,'connected account draws its cached circular avatar')
 advance(.2)
 local stable_count=0
 for _,command in ipairs(commands)do if command[1]=='overlay-add' and command[2]==61 then stable_count=stable_count+1 end end
 check(stable_count==avatar_count,'unchanged avatar is not resent every frame')
 pos.x=0;pos.y=0;messages['player_ui-hide']();advance(.1)
 check(commands[#commands][1]=='overlay-remove' and commands[#commands][2]==61,'hiding controls removes the avatar')
 observers['user-data/player_ui/bangumi']('user-data/player_ui/bangumi',{connected=true,username='user',generation=7,status='',settings={autoCollect=true,collectPercent=20,autoSync=true,watchedPercent=80},
  subject={id=10,title='间谍过家家 第三季',date='2025-10',score=7.3,season=3,collectionType=3,currentEpisode=102,episodes={
   {id=101,number=38,title='第 38 集',kind=0,state=2},{id=102,number=39,title='第 39 集',kind=0,state=0}}}})
 messages['player_ui-show']();advance(.1);click('bangumi')
 check(has_row('剧集') and has_row('当前：第 39 集') and player_overlay_data:find('7.3',1,true),
  'matched season displays title rating and current episode with a labelled episode grid')
 local watched_cell=button(row('bangumi-episode:101'))
 local current_cell=button(row('bangumi-episode:102'))
 local cell_scale=(watched_cell.y1-watched_cell.y0)/50
 local pink=core.theme().accent
 local cell_drawing=player_overlay_data:gsub('\\clip%([^)]*%)','')
 local function pink_shape(cell,r,y,color)
  return string.format('\\1c&H%s&\\1a&H00&\\p1}m %.2f %.2f',color or pink,cell.x0+r*cell_scale,y)
 end
 check(cell_drawing:find(pink_shape(watched_cell,6,watched_cell.y0),1,true),
  'watched episode fills the entire cell pink')
 check(not cell_drawing:find(pink_shape(current_cell,6,current_cell.y0),1,true),
  'current unwatched episode has no pink cell background')
 check(cell_drawing:find(pink_shape(current_cell,2,current_cell.y1-5*cell_scale,core.theme().current),1,true),
  'current unwatched episode has a #39c5bb bottom strip')
 check(not cell_drawing:find(pink_shape(current_cell,2,current_cell.y0),1,true),
  'current episode has no pink top strip')
 check(core.theme().current=='BBC539','current episode uses the requested teal in ASS BGR order')
 check(not has_row('收藏进度') and not has_row('看过进度'),'account panel has no synchronization settings')
 check(not has_row('退出登录'),'logout is not a text menu row')
 local logout=button('bangumi-logout');local box=ui().menu_boxes[1]
 check(logout and logout.x0>box.x1-60 and logout.y1<box.y0+55,
  'logout icon is within the upper-right header of the account popover')
 click(row('bangumi-episode:102'));check(ui().menu=='bangumi-episode' and #ui().menu_boxes==2,'episode cell opens state actions beside its parent')
 click(row('看到'));check(commands[#commands][4]=='episode' and commands[#commands][5]=='102' and commands[#commands][7]=='through' and commands[#commands][8]=='7',
  'watched-through includes file generation and batch mode')
 bindings['player_ui-menu-escape']();advance(.1);bindings['player_ui-menu-escape']();advance(.1)
 click('settings');click(row('sync-settings'))
 check(has_row('收藏进度') and has_row('看过进度') and has_row('自动收藏为在看') and has_row('自动标记剧集看过'),
  'separate collection and watched thresholds live only under synchronization settings')
 bindings['player_ui-menu-escape']();advance(.1);bindings['player_ui-menu-escape']();advance(.1)
 click('bangumi');click('bangumi-logout')
 check(commands[#commands][4]=='logout' and ui().menu=='','header logout icon dispatches the account action and closes its popover')
 observers['user-data/player_ui/bangumi']('user-data/player_ui/bangumi',{connected=false});messages['player_ui-show']();advance(.1)
 click('bangumi');check(not button('bangumi-logout'),'logged-out panel has no logout icon')
 observers['user-data/player_ui/bangumi']('user-data/player_ui/bangumi',{connected=false,busy=true,authorizing=true})
 advance(.1)
 check(has_row('重新授权') and has_row('等待浏览器授权…'),'pending authorization keeps a retry action visible')
 click(row('重新授权'))
 check(commands[#commands][4]=='login','retry remains actionable while browser authorization is pending')
 bindings['player_ui-menu-escape']();advance(.1)
 local play_button=button('play');pos.x=(play_button.x0+play_button.x1)*ui().scale/2;pos.y=(play_button.y0+play_button.y1)*ui().scale/2;bindings['player_ui-move']();advance(.05)
 event_handlers['start-file'][1]();advance(.1)
 observers['volume']('volume',95)
 check(volume_overlay_data==nil,'startup restoration of volume creates no percentage popup')
 observers['volume']('volume',50)
 check(not ui().visible and ui().loading and player_overlay_data,
  'pre-play state renders an animated loading indicator without playback controls')
 local loading_frame=player_overlay_data;advance(.07)
 check(player_overlay_data~=loading_frame,'loading indicator advances while opening the file')
 event_handlers['file-loaded'][1]();advance(.1)
 check(not ui().visible and ui().loading and player_overlay_data,
  'file-loaded alone keeps the loading indicator until playback starts')
 event_handlers['playback-restart'][1]();advance(.1)
 check(ui().visible and not ui().loading,
  'first playback frame replaces the loading indicator with the bottom HUD')
 check(player_overlay_data:find('00:10',1,true) and player_overlay_data:find('02:00',1,true),
  'bottom HUD displays current playback time and full duration')
 props['media-title']='作品名字 (2025) S1E7 - 剧集名字';observers['media-title']();advance(.1)
 check(player_overlay_data:find('S1:E7 - 剧集名字',1,true) and not player_overlay_data:find('(2025) S1E7',1,true)
  and player_overlay_data:find('\\fnMicrosoft YaHei UI\\fs%d+\\b1')
  and player_overlay_data:find('\\fnSegoe UI',1,true),
  'HUD renders the bold series title and smaller episode row with Windows UI fonts')
 props['media-title']='Title';observers['media-title']();advance(.1)
 if focus=='hud' then
  os.remove(clock_config)
  package.loaded.mp=saved.mp;package.loaded['mp.options']=saved.opts;package.loaded['mp.utils']=saved.utils
  return
 end
 props['paused-for-cache']=true;observers['paused-for-cache']();advance(.1)
 check(ui().loading,'buffering shows the same animated loading indicator')
 props['paused-for-cache']=false;observers['paused-for-cache']();advance(.1)
 check(not ui().loading,'loading indicator ends when buffering ends')
 check(player_overlay_data:find('音量 50%',1,true)==nil and core.metrics.volume_width==130,
  'the bottom volume control is restored as a full-width slider')
 check(player_overlay_data:find('CC',1,true), 'subtitle button renders its CC label')
 bindings['player_ui-volume-up']()
 check(props.volume==55 and volume_overlay_data and volume_overlay_data:find('音量 55%',1,true),
  'volume changes show a compact themed percentage popup in place of the native volume bar')
 bindings['player_ui-volume-down']()
 check(props.volume==50 and volume_overlay_data and volume_overlay_data:find('音量 50%',1,true),
  'the themed popup follows volume changes in either direction')
 advance(1.2)
 check(volume_overlay_data==nil,'the themed volume popup dismisses after its short timeout')
  props.fullscreen=true;bindings['player_ui-move']();advance(.1)
  check(button('close')==nil and button('minimize')==nil and button('maximize')==nil and button('pin')==nil,
   'fullscreen HUD leaves window controls to the native player chrome')
  props.fullscreen=false;bindings['player_ui-move']();advance(.1)
  check(button('close')==nil and button('minimize')==nil and button('maximize')==nil and button('pin')==nil,
   'windowed HUD does not draw duplicate native window controls')
  local hover_button=button('settings');pos.x=(hover_button.x0+hover_button.x1)/2;pos.y=(hover_button.y0+hover_button.y1)/2
 bindings['player_ui-move']();advance(.1)
 check(not player_overlay_data:find('设置',1,true),'bottom controls do not render hover labels')
 advance(3)
 check(ui().visible,'HUD stays visible while the pointer remains on a bottom control')
 bindings['player_ui-leave']();advance(.1)
 check(ui().visible,'mouse-leave cannot hide controls while the pointer is still over them')
 pos.x=-20;pos.y=-20;bindings['player_ui-leave']();advance(.1)
 check(not ui().visible and not button('close'),'HUD hides after the pointer leaves the player')
 bindings['player_ui-move']();advance(.1)
  props['display-hidpi-scale']=1.5;props['demuxer-via-network']=true;props['cache-speed']=1234567
  observers['demuxer-via-network']();advance(1.1)
  check(player_overlay_data:find(os.date('%H:%M'),1,true) and player_overlay_data:find('1.23 MB/s',1,true),
   'the custom HUD keeps the wall clock and bottom network speed beside volume')
 check(player_overlay_data:find('\\an7\\pos(0.00,0.00)',1,true),
   'clock stays flush at the viewport upper left at 150% display scaling')
 check(not player_overlay_data:find('\\an9\\pos(',1,true),
   'network speed is no longer drawn in the upper-right corner')
 click('settings')
 advance(.1)
 check(player_overlay_data:find('1.23 MB/s',1,true),
   'opening a menu keeps the network speed in the bottom control row')
 bindings['player_ui-menu-escape']();advance(.1)
 props['display-hidpi-scale']=1;props['demuxer-via-network']=false;observers['demuxer-via-network']();advance(.1)
 bindings['player_ui-volume-up']();bindings['player_ui-volume-down']();check(props.volume==50 and props['time-pos']==10,'volume keys do not seek')
 click('speed');check(ui().menu_boxes[#ui().menu_boxes].x1-ui().menu_boxes[#ui().menu_boxes].x0==216,'speed menu width')
 local speed_first,speed_last
 for i,r in ipairs(ui().rows) do if r.text=='8.0x' then speed_first=i elseif r.text=='0.5x' then speed_last=i end end
 check(speed_first and speed_last and speed_first<speed_last,'Player UI speed order')
 click(row('1.5x'));check(props.speed==1.5,'speed selection applies')
 click('sub')
 check(ui().menu=='sub' and not button('subtitle-slot-1') and not button('subtitle-slot-2')
  and not player_overlay_data:find('subtitle-slot',1,true),'subtitle chooser has no unused 1/2 selector')
 click(row('sid:3'));check(props.sid==3 and props['secondary-sid']=='no','subtitle chooser selects the primary track')
 click(row('sid:no'));check(props.sid=='no' and props['secondary-sid']=='no','subtitle off does not affect the unused secondary track')
 click('settings');check(ui().menu=='settings' and not button('ai'),'settings owns AI and statistics')
 check(button('close')==nil and button('menu-close')==nil,'settings uses native window chrome and has no drawn panel X')
  check(#ui().menu_boxes==1 and ui().menu_boxes[1].x1-ui().menu_boxes[1].x0==200,
   'Hills settings popover uses a compact width')
   check(has_row('缩放模式') and has_row('超分与补帧') and has_row('字幕设置') and has_row('弹幕设置')
    and not has_row('播放列表')
    and not has_row('音轨') and not has_row('字幕') and not has_row('弹幕') and not has_row('播放速度'),
   'Hills settings menu has no duplicate bottom-row controls')
  click(row('ai'))
  local boxes=ui().menu_boxes
  local settings_box,ai_box=boxes[1],boxes[2]
  check(ui().menu=='ai' and #boxes==2 and settings_box.kind=='settings' and ai_box.kind=='ai',
   'AI submenu keeps the Hills settings parent visible beside it')
  check(ai_box.x1<settings_box.x0 and math.abs(ai_box.y1-settings_box.y1)<1,
   'AI submenu sits left of and bottom-aligns with its Hills parent')
 check(player_overlay_data and player_overlay_data:find('\\1c&H211F23&',1,true)
    and player_overlay_data:find('\\bord0.80',1,true),
   'menu panels have Hills dark surfaces and readable outlined text')
 check(not has_row('preset:1001') and not has_row('preset:1002') and not has_row('preset:1003'),
  'builtin quality balanced and performance presets are removed')
 click(row('preset:0'));check(commands[#commands][2]=='aji-slot' and commands[#commands][3]=='0','reuse AI controller')
 click('settings');click(row('ai'));scroll_menu_down('ai',10);click(row('配置管理器'))
 check(commands[#commands][1]=='run' and commands[#commands][2]:match('AnimeVEManager%.exe$'),
  'AI configuration manager control targets the existing manager executable')
 for _,slot in ipairs({1,2,3,4,5,6,7,8,9}) do
  click('settings');click(row('ai'))
  local target=row('preset:'..slot)
  if not button(target) then scroll_menu_down('ai',10) end
  click(target)
  check(commands[#commands][1]=='script-message' and commands[#commands][2]=='aji-slot'
   and commands[#commands][3]==tostring(slot),'AI slot '..slot..' reaches its controller')
 end
 click('settings');check(has_row('显示时间'),'clock has a settings switch')
 check(player_overlay_data:find('\\pos(0.00,0.00)',1,true),'clock anchors at the upper-left viewport corner')
 click(row('显示时间'));advance(.1)
 local file=assert(io.open(clock_config,'rb'));local config=file:read('*a');file:close()
 check(config:find('show_clock=no',1,true) and config:find('network_speed=yes',1,true),
  'clock switch saves without modifying other preferences')
 check(not player_overlay_data:find('\\pos(0.00,0.00)',1,true),'disabled clock is not drawn')
 click(row('显示时间'));advance(.1);os.remove(clock_config);bindings['player_ui-menu-escape']();advance(.1)
 click('settings');click(row('scale'));click(row('填充裁剪'));check(props.panscan==1 and props.keepaspect,'fill crop mode')
 click('settings');click(row('scale'));click(row('填充裁剪'));check(props.panscan==1 and props.keepaspect,'fill crop mode')
 click('settings');click(row('stats'));check(not has_row('章节…'),'statistics no longer has a chapters button');props.fullscreen=true;bindings['player_ui-menu-escape']();advance(.1)
 check(props.fullscreen and ui().menu=='settings','Esc returns from submenu without leaving fullscreen')
 bindings['player_ui-menu-escape']();advance(.1);check(props.fullscreen and ui().menu=='','Esc closes root menu')
 local n=#commands;local b=button('seek');pos.x=(b.x0+b.x1)*ui().scale/2;pos.y=(b.y0+b.y1)*ui().scale/2
 bindings['player_ui-move']();advance(.1);bindings['player_ui-click']({event='down'});bindings['player_ui-click']({event='up',canceled=true});advance(.1)
 check(#commands==n,'canceled drag does not seek')
 props['playlist-count']=12;props.playlist={}
 for i=1,12 do props.playlist[i]={filename='part'..i..'.mkv'}end
 observers.playlist();advance(.1)
 check(button('playlist')==nil and ui().menu~='playlist',
  'queued media does not add a playlist button or drawer')
 click('settings');bindings['player_ui-menu-escape']();advance(.1);check(ui().menu=='','settings menu closes cleanly')
 click('sub');check(row('中文（简体） · PGS 图形字幕 · 外挂')~=nil,'localized subtitle label hides URL secrets')
 bindings['player_ui-menu-escape']();advance(.1)
 click('audio');click(row('添加音轨'))
 check(commands[#commands][1]=='script-message-to' and commands[#commands][2]=='mpvnet'
  and commands[#commands][3]=='load-audio','add-audio control delegates to the existing mpvnet loader')
 click('sub');click(row('导入本地字幕'))
 check(commands[#commands][1]=='script-message-to' and commands[#commands][2]=='mpvnet'
  and commands[#commands][3]=='load-sub','add-subtitle control delegates to the existing mpvnet loader')
 props['user-data/player_ui/danmaku']={loaded=false,enabled=true,count=0,opacity=85,area=50,
  status='',source_url='https://danmaku.example',servers={{index=1,url='https://danmaku.example',note='线路 A',selected=true}},results={}}
 props['vo-presented-frame-count']=0
 props['track-list'][#props['track-list']+1]={id=4,type='video',selected=true}
 local function danmaku_icon_color()
  local drawing=assert(player_overlay_data:match('([^\n]*m 3 3 l 29 3 29 26 10 26 5 30[^\n]*)'),
   'danmaku icon is rendered')
  return drawing:match('\\1c&H(%x+)&')
 end
 pos.x=ui().width/2;pos.y=ui().height/2;bindings['player_ui-move']();advance(.05)
 check(danmaku_icon_color()==core.theme().text,'danmaku icon uses its normal text color')
 props['user-data/player_ui/danmaku'].loaded=true
 observers['user-data/player_ui/danmaku']();advance(.05)
 check(danmaku_icon_color()==core.theme().text,'loaded and enabled danmaku does not recolor the icon')
 local danmaku_button=assert(button('danmaku'))
 pos.x=danmaku_button.x*ui().scale;pos.y=danmaku_button.y*ui().scale
 bindings['player_ui-move']();advance(.05)
 check(danmaku_icon_color()==core.theme().accent,'hovering over danmaku retains the accent color')
 pos.x=ui().width/2;pos.y=ui().height/2;bindings['player_ui-move']();advance(.05)
 check(danmaku_icon_color()==core.theme().text,'leaving hover restores white while danmaku stays enabled')
 props['user-data/player_ui/danmaku'].loaded=false
  click('danmaku');check(not has_row('danmaku-source') and not has_row('弹幕设置'),
   'danmaku menu leaves source settings under Settings')
 check(danmaku_icon_color()==core.theme().accent,'opening the danmaku menu retains the accent color')
 check(not has_row('隐藏弹幕') and not has_row('显示弹幕') and has_row('导入本地弹幕'),
  'danmaku menu has one off choice and a correctly named local import')
 local menu_title_at=assert(player_overlay_data:find('弹幕',1,true),'danmaku menu title is rendered')
 check(not player_overlay_data:find('正在加载…',1,true) and player_overlay_data:find('Title',1,true),
  'the playback title stays above the timeline while the danmaku menu is open')
 check(button('menu-close')==nil and button('close')==nil,'danmaku popover leaves close controls to the native title bar')
 local text_at=assert(player_overlay_data:find('关闭',1,true),'danmaku row label is rendered')
 local before_text=player_overlay_data:sub(1,text_at)
 local label_line=before_text:match('([^\n]*)$') or before_text
 check(not label_line:find('\\an3\\pos',1,true),'danmaku row label uses its left edge, not ASS bottom-right alignment')
 local text_x=tonumber((assert(label_line:match('\\pos%(([%d%.]+),'),'danmaku row label has a position')))
 local danmaku_box=ui().menu_boxes[1]
 check(text_x==danmaku_box.x0+core.metrics.row_padding+8,'danmaku row label starts at the intended inset')
 local clip_x0,clip_x1=label_line:match('\\clip%((%d+),%d+,(%d+),%d+%)')
 check(clip_x0=='0' and tonumber(clip_x1)==ui().width,
  'danmaku row text is not horizontally cropped at the popover edge')
 check(danmaku_box.x0>=core.metrics.menu_margin and danmaku_box.x1<=ui().width-core.metrics.menu_margin,
  'danmaku popover keeps a clear margin from both screen edges')
  pos.x=2;pos.y=2;bindings['player_ui-move']();advance(.5)
  check(ui().menu=='','popover retracts after the pointer leaves it')
  pos.x=ui().width/2;pos.y=ui().height/2;bindings['player_ui-move']();advance(.1)
  local settings_button=assert(button('settings'),'settings control after popover retracts')
  pos.x=(settings_button.x0+settings_button.x1)*ui().scale/2
  pos.y=(settings_button.y0+settings_button.y1)*ui().scale/2
  bindings['player_ui-move']();advance(.1)
  click('settings');click(row('弹幕设置'))
  check(ui().menu=='danmaku-settings' and row('速度')~=nil,
   'danmaku settings provides a speed multiplier')
  check(row('弹幕字号')~=nil and row('显示区域')~=nil and row('不透明度')~=nil,
   'danmaku settings has size, one display area and percentage opacity')
  check(row('屏蔽固定弹幕')~=nil and row('屏蔽滚动弹幕')~=nil and row('屏蔽彩色弹幕')~=nil and row('屏蔽词…')~=nil,
   'danmaku settings groups blocking into fixed, scrolling, colored and words')
  check(not has_row('滚动时长') and not has_row('固定弹幕时长') and not has_row('不重叠')
   and not has_row('滚动弹幕显示范围') and not has_row('完整 DanmakuFactory 配置…'),
   'removed advanced controls do not appear in the compact settings')
  check(not has_row('danmaku-source') and not has_row('当前线路：线路 A'),
   'danmaku settings avoids the duplicate source-picker page')
  click(row('屏蔽固定弹幕'))
  check(commands[#commands][4]=='["TOP","BOTTOM"]','fixed blocking groups top and bottom comments')
  click(row('屏蔽滚动弹幕'))
  check(commands[#commands][4]=='["R2L","L2R"]','scrolling blocking groups both directions')
  click(row('屏蔽彩色弹幕'))
  check(commands[#commands][4]=='["COLOR"]','color blocking uses the upstream color filter')
  scroll_menu_down('danmaku-settings',2);click(row('屏蔽词…'))
  check(commands[#commands][2]=='player_ui-danmaku-words','word blocking opens from the compact settings')
  local saved_danmaku=props['user-data/player_ui/danmaku']
  props['user-data/player_ui/danmaku']={loaded=false,enabled=false,servers={},results={}}
  click('settings');check(not has_row('弹幕线路…'),'route manager is not a top-level setting');click(row('danmaku-settings'))
  check(row('弹幕线路…')~=nil,'route management is inside Danmaku Settings')
  bindings['player_ui-menu-escape']();advance(.1)
  props['user-data/player_ui/danmaku']=saved_danmaku
 props['track-list'][#props['track-list']]=nil;props['vo-presented-frame-count']=1
 bindings['player_ui-move']();advance(.1)
 props.path='C:/Video/间谍过家家 (2023) S2E6 - 战栗的豪华邮轮.mkv'
  local online_api=dofile(root..'/portable_config/script-modules/player_ui_danmaku_online.lua')
  local function find_async_after(after,url)
   local encoded=url
   for id=after+1,next_async do
    local job=pending_async[id]
    if job and not job.aborted and not job.completed
     and job.spec.args and table.concat(job.spec.args,' '):find(encoded,1,true) then return job,id end
   end
   local seen={}
   for id=after+1,next_async do
    local job=pending_async[id]
    if job and job.spec.args then
     for _,candidate in ipairs({'https://danmaku.example/api/v2/match',
      'https://danmaku.example/api/v2/search/anime?keyword=Anime%20title',
      'https://danmaku2.example/api/v2/match'}) do
      if table.concat(job.spec.args,' '):find(candidate,1,true) then seen[#seen+1]=candidate end
     end
    end
   end
   error('missing asynchronous request '..url..'; observed '..table.concat(seen,', ')
    ..'; async ids '..tostring(after+1)..'..'..tostring(next_async))
  end
  local function complete_async(job,body)
   job.completed=true
   job.callback(true,{status=0,stdout=body,stderr=''},nil)
  end
  local match_json='[=[{"isMatched":true,"matches":[{"animeId":101,"episodeId":17,"animeTitle":"间谍过家家 第二季(2023)","episodeTitle":"第31集_06"}]}]=]'
  local detail_json='[=[{"bangumi":{"episodes":[{"episodeId":17,"episodeNumber":6,"episodeTitle":"第31集_06"}]}}]=]'
  local automatic_match_json='[=[{"isMatched":true,"matches":[{"animeId":102,"episodeId":18,"animeTitle":"间谍过家家 第二季(2023)","episodeTitle":"第37集_12"}]}]=]'
  local automatic_detail_json='[=[{"bangumi":{"episodes":[{"episodeId":18,"episodeNumber":12,"episodeTitle":"第37集_12"}]}}]=]'
  local comments_json='[=[{"comments":[{"p":"2,1,16777215","m":"你好世界"}]}]=]'
  local empty_comments_json='[=[{"comments":[]}]=]'
  local no_match_json='[=[{"isMatched":false,"matches":[]}]=]'
  local empty_search_json='[=[{"animes":[]}]=]'
  local first_search_json='[=[{"animes":[{"animeId":21,"animeTitle":"Anime title 第一季(2023)","source":"qq","episodeCount":12}]}]=]'
  local second_search_json='[=[{"animes":[{"animeId":22,"animeTitle":"Anime title 第一季(2023)","source":"iqiyi","episodeCount":12}]}]=]'
  local search_detail_json='[=[{"bangumi":{"episodes":[{"episodeId":23,"episodeNumber":1,"episodeTitle":"第1集"}]}}]=]'
  json_responses[match_json]={isMatched=true,matches={{animeId=101,episodeId=17,animeTitle='间谍过家家 第二季(2023)',episodeTitle='第31集_06'}}}
  json_responses[detail_json]={bangumi={episodes={{episodeId=17,episodeNumber=6,episodeTitle='第31集_06'}}}}
  json_responses[automatic_match_json]={isMatched=true,matches={{animeId=102,episodeId=18,animeTitle='间谍过家家 第二季(2023)',episodeTitle='第37集_12'}}}
  json_responses[automatic_detail_json]={bangumi={episodes={{episodeId=18,episodeNumber=12,episodeTitle='第37集_12'}}}}
  json_responses[no_match_json]={isMatched=false,matches={}}
  json_responses[empty_search_json]={animes={}}
  json_responses[first_search_json]={animes={{animeId=21,animeTitle='Anime title 第一季(2023)',source='qq',episodeCount=12}}}
  json_responses[second_search_json]={animes={{animeId=22,animeTitle='Anime title 第一季(2023)',source='iqiyi',episodeCount=12}}}
  json_responses[search_detail_json]={bangumi={episodes={{episodeId=23,episodeNumber=1,episodeTitle='第1集'}}}}
  json_responses[comments_json]={comments={{p='2,1,16777215',m='你好世界'}}}
  json_responses[empty_comments_json]={comments={}}
  local old_getenv,old_open,old_remove,old_rename=os.getenv,io.open,os.remove,os.rename
  local private_path='danmaku-private/AnimeVE-danmaku.conf'
  local private_servers='api_servers=https://danmaku.example|ME,https://danmaku2.example|Catcat\n'
  local private_files={[private_path]=private_servers}
  os.getenv=function(name)if name=='TEMP' then return out~='' and out or root..'/_probe' end;
   if name=='LOCALAPPDATA'then return 'danmaku-private'end
   if name=='APPDATA'or name=='XDG_CONFIG_HOME'or name=='HOME'then return nil end
   return old_getenv(name)
  end
  io.open=function(path,mode)
   if path:match('^danmaku%-private/')then
    if mode=='rb'then
     local body=private_files[path];if not body then return nil,'file not found'end
     return {read=function()return body end,close=function()return true end}
    elseif mode=='wb'then
     local body=''
     return {write=function(_,value)body=body..value;return true end,
     close=function()private_files[path]=body;return true end}
    end
   end
   if mode=='rb' and path:match('[\\/]comments%.xml$') then
    local body='<i><d p="2,1,25,16777215">one</d><d p="3,5,25,16777215">two</d><d p="4,4,25,16777215">three</d></i>'
    return {read=function()return body end,close=function()return true end}
   end
   return old_open(path,mode)
  end
  os.remove=function(path)
    if path:match('^danmaku%-private/')then private_files[path]=nil;return true end
   return old_remove(path)
  end
  os.rename=function(source,destination)
   if source:match('^danmaku%-private/')or destination:match('^danmaku%-private/')then
    if not private_files[source]then return nil,'file not found'end
    private_files[destination]=private_files[source];private_files[source]=nil;return true
   end
   return old_rename(source,destination)
  end
  dofile(root..'/portable_config/scripts/player_ui_danmaku.lua')
  local function dispatch_last_message()
   local command=commands[#commands];check(command and command[1]=='script-message','UI row emits a script message')
   local handler=assert(messages[command[2]],'script message handler '..tostring(command[2]))
   return handler(unpack(command,3))
  end
   bindings['player_ui-menu-escape']();advance(.1);click('settings');click(row('弹幕设置'));click(row('弹幕线路…'));dispatch_last_message()
  local manager_command=commands[#commands]
  check(manager_command[1]=='script-message-to' and manager_command[2]=='mpvnet'
   and manager_command[3]=='show-danmaku-sources' and manager_command[4]=='ME' and manager_command[5]=='https://danmaku.example',
   'line management opens the native mpv.net source manager with separate name and URL fields')
  json_responses['sources-add-json']={selected=3,servers={
   {name='Fixture Route 1',url='https://fixture3.example/api'},
   {name='ME',url='https://danmaku.example'},
   {name='Catcat',url='https://danmaku2.example'}}}
  messages['player_ui-danmaku-save-servers']('sources-add-json')
  local danmaku_state=props['user-data/player_ui/danmaku']
  messages['player_ui-danmaku-setting']('fontsize','32')
  messages['player_ui-danmaku-setting']('scrolltime','5.5')
  danmaku_state=props['user-data/player_ui/danmaku']
  check(danmaku_state.settings.fontsize==32 and danmaku_state.settings.scrolltime==5.5,
   'Comment settings are published and saved')
  messages['player_ui-danmaku-setting']('speed','2')
  danmaku_state=props['user-data/player_ui/danmaku']
  check(danmaku_state.settings.scrolltime==6 and danmaku_state.settings.fixtime==2.5,
   'a higher speed shortens both scrolling and fixed durations in one settings change')
  messages['player_ui-danmaku-setting']('area','25')
  messages['player_ui-danmaku-setting']('opacity-percent','80')
  danmaku_state=props['user-data/player_ui/danmaku']
  check(danmaku_state.settings.displayArea==.25 and danmaku_state.settings.scrollArea==1,
   'one display area governs fixed and scrolling comments without a second limit')
  check(danmaku_state.settings.opacity==204,'percentage opacity maps to upstream byte opacity')
  messages['player_ui-danmaku-save-words'](' English \r\n中文\n\n')
  check(private_files['danmaku-private/AnimeVE-danmaku-blocklist.txt']=='English\n中文',
   'word blocking persists trimmed UTF-8 words one per line')
  messages['player_ui-danmaku-words']()
  check(commands[#commands][3]=='show-danmaku-blocklist' and commands[#commands][4]=='English\n中文',
   'the native text editor receives the saved words')
  messages['player_ui-danmaku-save-words']('')
  check(props['user-data/player_ui/danmaku'].settings.blacklist=='','clearing words disables filtering')
  messages['player_ui-danmaku-reset']()
  check(#danmaku_state.servers==3 and danmaku_state.source==1
   and danmaku_state.servers[1].note=='Fixture Route 1','native source manager saves a test endpoint and its display name separately')
  json_responses['sources-edit-json']={selected=1,servers={
   {name='Fixture Route Edited',url='https://fixture3-edited.example/api'},
   {name='ME',url='https://danmaku.example'},
   {name='Catcat',url='https://danmaku2.example'}}}
  messages['player_ui-danmaku-save-servers']('sources-edit-json')
  danmaku_state=props['user-data/player_ui/danmaku']
  messages['player_ui-danmaku-manage']()
  local edited_manager=commands[#commands]
  check(edited_manager[4]=='Fixture Route Edited' and edited_manager[5]=='https://fixture3-edited.example/api'
   and danmaku_state.servers[1].url==nil,'source manager edits both fields without exposing URLs in public player state')
  json_responses['sources-delete-json']={selected=2,servers={
   {name='ME',url='https://danmaku.example'},
   {name='Catcat',url='https://danmaku2.example'}}}
  messages['player_ui-danmaku-save-servers']('sources-delete-json')
  danmaku_state=props['user-data/player_ui/danmaku']
  check(#danmaku_state.servers==2 and danmaku_state.source==1,'source manager keeps saved priority order without a current route')
  check(not private_files[private_path]:find('fixture3',1,true),'only saved test routes are written to the private user config')
  local saved_config=private_files[private_path]
  json_responses['sources-invalid-json']={selected=1,servers={{name='Bad route',url='https://fixture.invalid/api?key=test'}}}
  messages['player_ui-danmaku-save-servers']('sources-invalid-json')
  danmaku_state=props['user-data/player_ui/danmaku']
  check(#danmaku_state.servers==2 and private_files[private_path]==saved_config,
   'invalid route edits leave the current sources and private config unchanged')
  json_responses['sources-single-json']={selected=1,servers={{name='ME',url='https://danmaku.example'}}}
  messages['player_ui-danmaku-save-servers']('sources-single-json')
  local before_initial_title=next_async
  observers['media-title']('media-title','string',props['media-title'])
  check(next_async==before_initial_title,'initial title notification cannot search before start-file')
  event_handlers['start-file'][#event_handlers['start-file']]()
  event_handlers['file-loaded'][#event_handlers['file-loaded']]()
 local search_job=pending_async[next_async]
 local anime_keyword=online_api.urlencode('间谍过家家')
 check(table.concat(search_job.spec.args,' '):find(('https://danmaku.example/api/v2/search/anime?keyword='..anime_keyword),1,true),
  'automatic search starts directly without video hashing or duplicate match requests')
 json_responses['auto-season-two']={animes={{animeId=101,animeTitle='间谍过家家 第二季(2023)',source='qq',episodeCount=12}}}
 complete_async(search_job,'auto-season-two')
 complete_async(pending_async[next_async],detail_json)
 complete_async(pending_async[next_async],comments_json)
 danmaku_state=props['user-data/player_ui/danmaku']
 check(danmaku_state.loaded and danmaku_state.count==1,'search resolves the exact season and episode and loads comments')
 event_handlers['start-file'][#event_handlers['start-file']]()
 props.path='https://media.example/stream.m3u8';props['media-title']='Anime title S1E1'
 local before=next_async
 event_handlers['file-loaded'][#event_handlers['file-loaded']]()
 local hung_search=find_async_after(before,'https://danmaku.example/api/v2/search/anime?keyword=Anime%20title')
 check(props['user-data/player_ui/danmaku'].autoload_state=='loading','loading state appears immediately')
 advance(94)
 check(hung_search.aborted and next_async==before+1 and props['user-data/player_ui/danmaku'].autoload_state=='error',
  'timeout ends the single search without a scheduled automatic retry')
 json_responses['sources-three-json']={servers={
  {name='ME',url='https://danmaku.example'},{name='Catcat',url='https://danmaku2.example'},
  {name='Third',url='https://danmaku3.example'}}}
 messages['player_ui-danmaku-save-servers']('sources-three-json')
 props.path='C:/Video/Anime title S1E1.mkv'
 event_handlers['start-file'][#event_handlers['start-file']]()
 before=next_async
 event_handlers['file-loaded'][#event_handlers['file-loaded']]()
 local searches={}
 for i=1,3 do
  searches[i]=find_async_after(before,'https://danmaku'..(i==1 and '' or i)..'.example/api/v2/search/anime?keyword=Anime%20title')
 end
 check(next_async==before+3,'exactly one automatic search starts on every route in parallel')
 json_responses['auto-episode-one']={bangumi={episodes={{episodeId=17,episodeNumber=1,episodeTitle='第1集'}}}}
 -- Last priority route responds first; do not wait for the earlier routes.
 complete_async(searches[3],first_search_json)
 local fastest_detail=pending_async[next_async]
 check(table.concat(fastest_detail.spec.args,' '):find(('https://danmaku3.example/api/v2/bangumi/21'),1,true),
  'first matching search response is attempted immediately while other searches are pending')
 complete_async(fastest_detail,'auto-episode-one')
 complete_async(pending_async[next_async],empty_comments_json)
 local after_empty=next_async
 complete_async(searches[2],second_search_json)
 check(next_async==after_empty,'empty fastest route waits for the higher priority route, not the next response')
 complete_async(searches[1],first_search_json)
 check(table.concat(pending_async[next_async].spec.args,' '):find(('https://danmaku.example/api/v2/bangumi/21'),1,true),
  'fallback proceeds from the top of the saved route order')
 complete_async(pending_async[next_async],'auto-episode-one')
 complete_async(pending_async[next_async],comments_json)
 danmaku_state=props['user-data/player_ui/danmaku']
 check(danmaku_state.loaded and danmaku_state.source==1,'first non-empty comments finish automatic loading')
 local after_success=next_async
 event_handlers['file-loaded'][#event_handlers['file-loaded']]();advance(100)
 check(next_async==after_success,'metadata events and elapsed time never resubmit automatic searches')
 props.path='C:/Video/星海里的旅行者 (2024) S2E5.mkv'
 props['media-title']='星海里的旅行者 (2024) S2E5'
 event_handlers['start-file'][#event_handlers['start-file']]()
 before=next_async
 event_handlers['file-loaded'][#event_handlers['file-loaded']]()
 json_responses['ambiguous-works']={animes={
  {animeId=51,animeTitle='星海中的旅行者 第二季(2024)',source='qq'},
  {animeId=52,animeTitle='星海外的旅行者 第二季(2025)',source='qq'}}}
 local ambiguous_searches={}
 for i=1,3 do
  ambiguous_searches[i]=find_async_after(before,'https://danmaku'..(i==1 and '' or i)..'.example/api/v2/search/anime?keyword='..
   online_api.urlencode('星海里的旅行者'))
 end
 for i=1,3 do complete_async(ambiguous_searches[i],'ambiguous-works') end
 danmaku_state=props['user-data/player_ui/danmaku']
 check(next_async==before+3 and not danmaku_state.loaded and danmaku_state.autoload_state=='not-found',
  'ambiguous works never request episode details or comments, and each route is searched only once')
 check(danmaku_state.status=='有多个相近作品，请手动选择弹幕',
  'ambiguous automatic results explain the need for a manual selection')
 props.path='C:/Video/Voyagers of the Stars S2E5.mkv'
 props['media-title']='Voyagers of the Stars S2E5'
 event_handlers['start-file'][#event_handlers['start-file']]()
 before=next_async
 event_handlers['file-loaded'][#event_handlers['file-loaded']]()
 json_responses['alias-work']={animes={{animeId=51,animeTitle='星海中的旅行者 第二季(2024)',source='qq',
  aliases={'Voyagers of the Stars Season 2'}}}}
 json_responses['alias-episodes']={bangumi={episodes={{episodeId=55,episodeNumber=5,episodeTitle='第5集'}}}}
 local alias_search=find_async_after(before,'https://danmaku3.example/api/v2/search/anime?keyword='..
  online_api.urlencode('Voyagers of the Stars'))
 complete_async(alias_search,'alias-work')
 complete_async(pending_async[next_async],'alias-episodes')
 complete_async(pending_async[next_async],comments_json)
 check(props['user-data/player_ui/danmaku'].loaded and props['user-data/player_ui/danmaku'].source==3,
  'provider aliases load the exact episode from the first matching route without waiting for other routes')
 props.path='C:/Video/Anime title S1E1.mkv';props['media-title']='Anime title S1E1'
 json_responses['sources-two-json']={servers={{name='ME',url='https://danmaku.example'},{name='Catcat',url='https://danmaku2.example'}}}
 messages['player_ui-danmaku-save-servers']('sources-two-json')
  event_handlers['playback-restart'][1]();bindings['player_ui-move']();advance(.1);click('danmaku');click(row('搜索弹幕'));dispatch_last_message()
  local search_dialog_command=commands[#commands]
  check(search_dialog_command[1]=='script-message-to' and search_dialog_command[2]=='mpvnet'
   and search_dialog_command[3]=='show-danmaku-search' and search_dialog_command[4]~='',
   'choosing Search Danmaku opens the native search window with the detected title')
  messages['player_ui-danmaku-search-query']('Anime title','0','1')
  local search_1=pending_async[next_async-1]
  check(table.concat(search_1.spec.args,' '):find(('https://danmaku.example/api/v2/search/anime?keyword=Anime%20title'),1,true)~=nil,
   'manual search requests every configured route')
  search_1.callback(true,{status=0,stdout=first_search_json,stderr=''},nil)
  danmaku_state=props['user-data/player_ui/danmaku']
  check(#danmaku_state.results==1 and danmaku_state.results[1].season==1
   and danmaku_state.results[1].server_index==1,'manual search preserves season and route classification')
  local search_2=pending_async[next_async]
  check(table.concat(search_2.spec.args,' '):find(('https://danmaku2.example/api/v2/search/anime?keyword=Anime%20title'),1,true)~=nil,
   'manual search also requests the other configured route')
  local next_before_cache=next_async
  messages['player_ui-danmaku-search-query']('Anime title','2','1')
  check(next_async==next_before_cache,'switching routes while the batch is active reuses the search cache')
  search_2.callback(true,{status=0,stdout=second_search_json,stderr=''},nil)
  danmaku_state=props['user-data/player_ui/danmaku']
  check(#danmaku_state.results==1 and danmaku_state.results[1].id=='22'
   and danmaku_state.results[1].platforms[1].name=='爱奇艺','route filter keeps the selected platform result')
  messages['player_ui-danmaku-show']('22','2')
  local selected_detail=pending_async[next_async]
  check(table.concat(selected_detail.spec.args,' '):find(('https://danmaku2.example/api/v2/bangumi/22'),1,true)~=nil,
   'selecting a platform requests only its episode list')
  selected_detail.callback(true,{status=0,stdout=search_detail_json,stderr=''},nil)
  danmaku_state=props['user-data/player_ui/danmaku']
  check(danmaku_state.search_view=='episodes' and #danmaku_state.episodes==1
   and danmaku_state.episodes[1].number_value==1,'episode view uses the selected platform')
  local load_generation=danmaku_state.episode_load_generation or 0
  messages['player_ui-danmaku-pick']('23','Anime title · 第1集','2')
  local selected_search=pending_async[next_async]
  check(table.concat(selected_search.spec.args,' '):find(('https://danmaku2.example/api/v2/comment/23?withRelated=true'),1,true)~=nil,
   'selecting an episode fetches comments from its chosen source')
  danmaku_state=props['user-data/player_ui/danmaku']
  check(not danmaku_state.loaded and danmaku_state.status=='获取弹幕中…'
   and danmaku_state.episode_load_generation==load_generation+1,
   'manual episode selection publishes a new pending-load generation before the request completes')
  selected_search.callback(true,{status=0,stdout=comments_json,stderr=''},nil)
  messages['player_ui-danmaku-pick']('24','Anime title · 第2集','2')
  local empty_comment=pending_async[next_async]
  empty_comment.callback(true,{status=0,stdout=empty_comments_json,stderr=''},nil)
  danmaku_state=props['user-data/player_ui/danmaku']
  check(not danmaku_state.loaded and danmaku_state.count==0 and danmaku_state.status=='该集没有弹幕'
   and danmaku_state.episode_load_generation==load_generation+2,
   'an empty episode clears previous comments and reports that this episode has no danmaku')
  click('danmaku')
  check(has_row('搜索弹幕') and not has_row('Anime title') and not has_row('搜到'),
   'a source with no comments is not shown as a loaded danmaku item')
  bindings['player_ui-menu-escape']();advance(.1)
  click('settings');click(row('弹幕设置'))
  check(has_row('屏蔽词…') and not has_row('在线获取弹幕'),
   'Danmaku Settings lives under Settings and does not duplicate manual search')
  bindings['player_ui-menu-escape']();advance(.1)
  bindings['player_ui-menu-escape']();advance(.1)
  props.path='https://media.example/stream.m3u8'
  local stale_start=next_async
  event_handlers['start-file'][#event_handlers['start-file']]()
  event_handlers['file-loaded'][#event_handlers['file-loaded']]()
  local stale=find_async_after(stale_start,'https://danmaku.example/api/v2/search/anime?keyword='..online_api.urlencode('Anime title'))
  local stale_command=table.concat(stale.spec.args,' ')
  local has_match=true
  check(has_match,'media without a local hash still attempts filename recognition')
  event_handlers['start-file'][#event_handlers['start-file']]()
 complete_async(stale,match_json)
 danmaku_state=props['user-data/player_ui/danmaku']
 check(not danmaku_state.loaded and danmaku_state.autoload_state=='loading'
  and not danmaku_state.status:find('失败',1,true),
  'old file callback cannot replace the new file automatic-loading state')
 click('danmaku');click(row('导入本地弹幕'));dispatch_last_message()
 check(commands[#commands][1]=='script-message-to' and commands[#commands][2]=='mpvnet'
  and commands[#commands][3]=='load-danmaku',
  'local XML picker uses the native player command')
 props['time-pos']=2
 messages['player_ui-danmaku-load'](out..'/comments.xml')
  danmaku_state=props['user-data/player_ui/danmaku']
  check(danmaku_state.loaded and danmaku_state.count==3,'choosing a local XML fixture loads it into the renderer')
  check(danmaku_state.backend=='imtaotao/danmu' and danmaku_state.render_fps==nil,
   'original renderer does not publish a fabricated Lua submission FPS')
  local before_resize=next_async
  local previous_load=renderer_arguments
  for _,size in ipairs({{1920,1200,60},{2560,1600,80},{1280,720,0},{1920,1200,60}}) do
   props['osd-dimensions']={w=size[1],h=size[2],ml=0,mr=0,mt=size[3],mb=size[3]}
   observers['osd-dimensions']('osd-dimensions',props['osd-dimensions']);advance(.2)
   check(next_async==before_resize and renderer_arguments==previous_load,
    'resizing never reloads comments or starts a converter subprocess')
  end
  props['speed']=2;props['time-pos']=5
  observers['speed']('speed',2);advance(.2)
  check(renderer_arguments==previous_load,'playback rate changes never rebuild the comment pool')
  props['speed']=1;observers['speed']('speed',1);advance(.2)
  local ticks=0
  for _,candidate in ipairs(timers) do
   if candidate.alive and candidate.delay<1/30 then ticks=ticks+1 end
  end
  check(ticks==0,'danmaku playback creates no high-frequency Lua animation timer')
  props['time-pos']=20
  bindings['player_ui-leave']();advance(4)
  local active=0;for _,t in ipairs(timers)do if t.alive and t.repeated then active=active+1 end end
  check(active==0,'hidden local video has no repeating UI timer')
  local before_secondary=props['secondary-sid']
  messages['player_ui-danmaku-clear']()
  check(props['secondary-sid']==before_secondary,'independent rendering does not replace secondary subtitles')
 props.path='C:/Video/间谍过家家 代号：白 (2023).mkv';props['media-title']='间谍过家家 代号：白 (2023)'
 local before_movie=next_async
 event_handlers['start-file'][#event_handlers['start-file']]()
 event_handlers['file-loaded'][#event_handlers['file-loaded']]()
 check(next_async==before_movie+2,'movie titles without an episode marker search every route')
 for i=before_movie+1,next_async do
  complete_async(pending_async[i],empty_search_json)
 end
props.path='https://media.example/stream.m3u8'
props['media-title']=''
event_handlers['start-file'][#event_handlers['start-file']]()
check(props['user-data/player_ui/danmaku'].autoload_state=='loading',
 'link opening publishes automatic loading before file-loaded')
advance(.3)
local before_link_title=next_async
props['media-title']='间谍过家家 (2025) S3E3 - MEMORIES II'
observers['media-title']('media-title','string',props['media-title'])
local early_match=find_async_after(before_link_title,'https://danmaku.example/api/v2/search/anime?keyword='..online_api.urlencode('间谍过家家'))
local early_match_other=find_async_after(before_link_title,'https://danmaku2.example/api/v2/search/anime?keyword='..online_api.urlencode('间谍过家家'))
local early_search=find_async_after(before_link_title,'https://danmaku.example/api/v2/search/anime?keyword='..online_api.urlencode('间谍过家家'))
check(early_match and early_match_other and early_search,
 'automatic title matching starts all route matches and show searches as soon as a link title appears')
check(next_async>before_link_title,
 'automatic matching waits visibly for a late link title instead of failing or waiting until playback metadata completes')
 local early_search_count=next_async
 event_handlers['file-loaded'][#event_handlers['file-loaded']]()
 check(next_async==early_search_count and early_match.aborted==nil,
  'file-loaded does not restart an automatic request already started during opening')
 event_handlers['end-file'][#event_handlers['end-file']]()
 local after_end=next_async
 observers['media-title']('media-title','string',props['media-title'])
 advance(.3)
 check(next_async==after_end,'title notifications after end-file cannot start another search')
  os.getenv,io.open,os.remove,os.rename=old_getenv,old_open,old_remove,old_rename
 package.loaded.mp=saved.mp;package.loaded['mp.options']=saved.opts;package.loaded['mp.utils']=saved.utils
end
local success,err=xpcall(suite,debug.traceback)
if out~='' then
 local result=io.open(out..'/player_ui_logic.result','wb')
 if result then result:write(success and ('PASS Player UI logic: '..checks..' assertions') or ('FAIL Player UI logic: '..tostring(err)));result:close()end
end
if success then
 if logger then logger.info('PASS Player UI logic: '..checks..' assertions')else print('PASS Player UI logic: '..checks..' assertions')end
else
 if logger then logger.error(err)else print(err)end
end
if ok then real.commandv('quit',success and 0 or 1)elseif not success then os.exit(1)end

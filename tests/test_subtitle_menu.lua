local root=assert(arg[1])
local core=dofile(root..'/portable_config/script-modules/player_ui_core.lua')
local create_menu=dofile(root..'/portable_config/script-modules/player_ui_menu.lua')
local checks=0
local function check(value,label)checks=checks+1;assert(value,label)end
local function measured_width(value,size)
    local width=0
    for _,char in ipairs(core.chars(value))do width=width+(#char>1 and .8 or .4)*size end
    return width
end
local wrapped=core.wrap('中文😀abcdefghijk',32,10,0,measured_width)
check(table.concat(wrapped)=='中文😀abcdefghijk','measured wrapping preserves complete UTF-8 characters')
for index,line in ipairs(wrapped)do
    check(measured_width(line,10)<=32,'measured lines fit the available width')
    if wrapped[index+1] then
        check(measured_width(line..core.chars(wrapped[index+1])[1],10)>32,'measured wrapping uses the full line before breaking')
    end
end
local limited=core.wrap(string.rep('字幕',12),32,10,2,measured_width)
check(#limited==2 and limited[2]:sub(-3)=='…' and measured_width(limited[2],10)<=32,
    'measured wrapping retains the existing line limit and fits its ellipsis')
check(core.wrap('',32,10,0,measured_width)[1]=='','measured wrapping keeps empty labels valid')
check(table.concat(core.wrap('字幕',1,10,0,measured_width))=='字幕','a narrow viewport still advances by complete characters')
local state={x=-1,y=-1,scroll=0}
local commands,changes={},{}
local measure_calls,measure_dimensions=0,true
local filename='[字幕组][作品名][第01集][BDRIP][1920x1080][x264_FLAC][简体中文].sc.ass'
local props={sid=1,['track-list']={
    {id=1,type='sub',title='内封标题',lang='jpn'},
    {id=2,type='sub',external=true,['external-filename']='C:\\字幕\\'..filename},
    {id=3,type='sub',external=true,['external-filename']='C:\\字幕\\另一个版本.tc.ass'},
    {id=4,type='sub',external=true,['external-filename']='https://media.example/Stream.ass'},
    {id=5,type='audio',title='音频'},
    {id=6,type='sub',title='弹幕'},
},['user-data/player_ui/danmaku']={track=6}}
local metadata={['2']={managed=true,pinned=false},['3']={managed=true,pinned=true},
    ['4']={title='在线字幕',lang='zh'},['6']={managed=true,pinned=true}}
local menu=create_menu({state=state,core=core,
    measure_text=function(value,size,w,h)
        measure_calls=measure_calls+1
        measure_dimensions=measure_dimensions and type(w)=='number' and w>0 and type(h)=='number' and h>0
        return measured_width(value,size)
    end,
    prop=function(name,default)local value=props[name];if value==nil then return default end;return value end,
    num=function(name,default)return tonumber(props[name]) or default end,
    subtitle_tracks=function()return metadata end,
    command=function(...)commands[#commands+1]={...}end,
    set=function(name,value)props[name]=value;changes[#changes+1]={name,value}end,
    set_number=function(name,value)props[name]=value;changes[#changes+1]={name,value}end,
    set_bool=function(name,value)props[name]=value;changes[#changes+1]={name,value}end,
    open_file=function()end,
})
local buttons,drawn={},{text={},icons={},rounds={}}
local function draw(width,height,dpi)
    buttons={};drawn={text={},icons={},rounds={}}
    menu.draw(core.layout(width or 1280,height or 720,dpi or 1),{
        round=function(x0,y0,x1,y1,r,color,alpha)drawn.rounds[#drawn.rounds+1]={x0=x0,y0=y0,x1=x1,y1=y1,color=color,alpha=alpha}end,
        rect=function()end,circle=function()end,
        icon=function(id,x,y,active,disabled,small)drawn.icons[#drawn.icons+1]={id=id,x=x,y=y,active=active,disabled=disabled,small=small}end,
        clip=function(_,_,_,_,fn)fn()end,
        text=function(x,y,size,text,align,color)drawn.text[#drawn.text+1]={x=x,y=y,size=size,text=text,align=align,color=color}end,
    },buttons)
end
local function button(key)
    for _,b in ipairs(buttons)do if b.key==key or b.id==key then return b end end
end
local function row_highlight(key)
    local box=menu.boxes[1];local row=assert(button(key));local cy=(row.y0+row.y1)/2
    for _,rect in ipairs(drawn.rounds)do
        if rect.color==core.theme().hover and rect.y0<=cy and rect.y1>=cy
            and math.abs(rect.x0-(box.x0+8*box.ui))<.001 and math.abs(rect.x1-(box.x1-8*box.ui))<.001 then
            return rect
        end
    end
end
local function command_is(action,id)
    local c=commands[#commands]
    return c and #c==4 and c[1]=='script-message-to' and c[2]=='mpvnet' and c[3]==action and c[4]==tostring(id)
end
menu.open('sub');draw()
check(measure_calls>0 and measure_dimensions,'subtitle layout measures actual text with the current viewport dimensions')
local measured=measure_calls;draw()
check(measure_calls==measured,'unchanged menu redraws reuse the measured layout')
check(menu.rows[1].key=='sid:no' and menu.rows[2].key=='sid:3' and menu.rows[3].key=='sid:1'
    and menu.rows[4].key=='sid:2' and menu.rows[5].key=='sid:4','pinned subtitles lead tracks while other ordering stays stable')
check(not button('sid:6') and not button('sid:6:pin'),'danmaku stays outside the primary subtitle menu')
check(button('sid:3:pin') and button('sid:3:delete') and button('sid:2:pin') and button('sid:2:delete'),
    'managed local subtitle rows expose separate pin and delete controls')
check(not button('sid:1:pin') and not button('sid:4:delete'),'embedded and unmanaged remote subtitles have no management controls')
check(menu.rows[2].actions[1].text=='F' and menu.rows[4].actions[1].text=='F','force controls always display a capital F')
check(menu.rows[2].actions[2].icon=='delete' and menu.rows[2].actions[2].text==nil,'delete uses the native vector icon without a text label')
local theme=core.theme();local forced,ordinary,deletes=0,0,0
for _,entry in ipairs(drawn.text)do
    if entry.text=='F' then
        if entry.color==theme.accent then forced=forced+1 elseif entry.color==theme.text then ordinary=ordinary+1 end
    end
    check(entry.text~='置顶' and entry.text~='取消置顶' and entry.text~='删除','subtitle actions render symbols without their old text labels')
end
for _,entry in ipairs(drawn.icons)do if entry.id=='delete' then deletes=deletes+1 end end
check(forced==1 and ordinary==1 and deletes==2,'forced state is highlighted while every managed row renders a delete icon')
local force_button=assert(button('sid:2:pin'));state.x=(force_button.x0+force_button.x1)/2;state.y=(force_button.y0+force_button.y1)/2
draw()
local hover_highlight=false
for _,entry in ipairs(drawn.text)do
    if entry.text=='F' and entry.x==state.x and entry.y==state.y then hover_highlight=entry.color==theme.accent end
end
check(hover_highlight,'hovering the inactive force control uses the theme accent')
check(row_highlight('sid:2'),'hovering force highlights the entire subtitle row')
local delete_button=assert(button('sid:2:delete'));state.x=(delete_button.x0+delete_button.x1)/2;state.y=(delete_button.y0+delete_button.y1)/2
draw()
local delete_highlight=false
for _,entry in ipairs(drawn.icons)do
    if entry.id=='delete' and entry.x==state.x and entry.y==state.y then delete_highlight=entry.active==true end
end
check(delete_highlight,'hovering delete uses the native icon highlight')
check(row_highlight('sid:2'),'hovering delete highlights the entire subtitle row')
state.x=-1;state.y=-1;draw()
local before=props.sid
menu.pick(assert(button('sid:2:pin')))
check(command_is('pin-subtitle',2) and props.sid==before and #changes==0 and state.menu=='sub',
    'pin control only dispatches its track identity and keeps the menu open')
metadata['2'].pinned=true;metadata['3'].pinned=false;menu.invalidate();draw()
check(menu.rows[2].key=='sid:2' and menu.rows[2].actions[1].text=='F' and menu.rows[2].actions[1].selected,
    'metadata refresh moves the forced version first and highlights its capital F')
menu.pick(assert(button('sid:2:pin')))
check(command_is('pin-subtitle',2) and state.menu=='sub','unpin uses the same native toggle action')
menu.pick(assert(button('sid:2')))
check(props.sid==2 and props['sub-visibility']==true,'clicking the name still selects the subtitle through native properties')
menu.pick(assert(button('sid:2:delete')))
check(command_is('delete-subtitle',2) and state.menu==nil and props.sid==2,
    'delete delegates to the native confirmation without unloading or deleting a file itself')
menu.open('sub');draw()
local selected_background=assert(row_highlight('sid:2'),'selected subtitle background covers its entire row')
check(selected_background.x0<=button('sid:2').x0+2*menu.boxes[1].ui
    and selected_background.x1>=button('sid:2:delete').x1,
    'selected subtitle background includes the name, force control and delete control')
for _,rect in ipairs(drawn.rounds)do
    local action=button('sid:2:pin')
    check(not (rect.color==theme.surface and math.abs(rect.x0-action.x0)<.001 and math.abs(rect.x1-action.x1)<.001),
        'inactive force background does not split the selected row into separate surfaces')
end
for _,entry in ipairs(menu.boxes[1].rows)do
    if entry.row.key=='sid:2' then
        check(table.concat(entry.lines)==filename and #entry.lines>1,'long local names retain every character across measured lines')
    end
end
local screenshot_name='[VCB-S][Toaru Majutsu no Index][08][BDRIP][FLAC+AC3][1920X1080].HKG.ass'
props['track-list'][2]['external-filename']='C:\\字幕\\'..screenshot_name
menu.invalidate();draw(1639,922,1.5)
for _,entry in ipairs(menu.boxes[1].rows)do
    if entry.row.key=='sid:2' then
        check(table.concat(entry.lines)==screenshot_name and #entry.lines==2,
            'the reported filename uses two measured lines instead of three estimated lines')
        local box=menu.boxes[1];local first_action=button('sid:2:pin');local left=box.x0+24*box.ui
        for index,line in ipairs(entry.lines)do
            check(left+measured_width(line,18*box.ui)<=first_action.x0-8*box.ui+.001,
                'measured subtitle text leaves the action gap and does not enter a button')
            if entry.lines[index+1] then
                check(left+measured_width(line..core.chars(entry.lines[index+1])[1],18*box.ui)>first_action.x0-8*box.ui,
                    'the filename fills the available space before wrapping')
            end
        end
    end
end
for _,size in ipairs({{1280,720,1},{2560,1600,1.5},{640,360,1.5}})do
    state.scroll=0;draw(size[1],size[2],size[3])
    local box=menu.boxes[1]
    for _,b in ipairs(buttons)do
        if b.action then
            local r=menu.rows[b.index];local row=assert(button(r.key))
            check(b.x0>=row.x1 and b.x1<=box.x1-13*box.ui,'action hit areas do not overlap the name or scrollbar')
            check(b.y0>=box.content and b.y1<=box.content+box.view and b.y1>b.y0,'visible action hit areas stay within the scroll viewport')
            for _,other in ipairs(buttons)do
                if other.action and other~=b then
                    check(b.x1<=other.x0 or other.x1<=b.x0 or b.y1<=other.y0 or other.y1<=b.y0,
                        'subtitle action hit areas do not overlap each other')
                end
            end
        end
    end
    if box.total>box.view then
        state.scroll=box.total-box.view;draw(size[1],size[2],size[3])
        for _,b in ipairs(buttons)do
            if b.action then check(b.y0>=menu.boxes[1].content and b.y1<=menu.boxes[1].content+menu.boxes[1].view,
                'scrolling clips action hit areas with their subtitle rows')end
        end
    end
end
menu.open('audio');draw()
check(menu.rows[2].key=='aid:5' and not menu.rows[2].actions,'audio selection retains its existing menu behavior')
menu.open('sub-settings');draw()
local position=menu.rows[2].slider
check(position.value==0 and string.format(position.format,position.value)=='0',
    'subtitle position displays its native default as zero without a percentage')
check(position.min==-100 and position.max==50,'subtitle position covers the complete native range including downward offsets')
props['secondary-sub-pos']=25
for _,offset in ipairs({-5,5,-100,50,0})do
    local slider_button=assert(button('slider-2'))
    state.menu_drag={slider=position,box=slider_button}
    menu.drag(slider_button.start+(slider_button.finish-slider_button.start)*(offset-position.min)/(position.max-position.min),0)
    state.menu_drag=nil
    check(props['sub-pos']==100+offset,'dragging a signed subtitle offset updates the native position')
    menu.invalidate();draw();position=menu.rows[2].slider
    local label=offset==0 and '0' or string.format('%+d',offset)
    check(position.value==offset and string.format(position.format,position.value)==label,
        'subtitle position redraw preserves the signed offset and neutral zero')
    check(props['secondary-sub-pos']==25,'primary subtitle position does not move secondary subtitles')
end
props['sub-pos']=108;menu.invalidate();draw()
check(menu.rows[2].slider.value==8,'native subtitle position changes remain reflected by the offset control')
print('PASS subtitle menu: '..checks..' assertions')

-- Run by SubtitleMenuLayoutChecks with a real, windowless libmpv instance.
local mp=require('mp')
local utils=require('mp.utils')
local options=mp.get_property_native('options/script-opts')
local root=assert(options['subtitle-layout-root']):gsub('\\','/')
local report_path=assert(options['subtitle-layout-report'])
local checks=0
local function check(value,message) checks=checks+1;assert(value,message) end
local function run()
    local core=dofile(root..'/portable_config/script-modules/player_ui_core.lua')
    local create_menu=dofile(root..'/portable_config/script-modules/player_ui_menu.lua')
    local file=assert(io.open(root..'/portable_config/scripts/player_ui.lua','rb'))
    local source=file:read('*a'):gsub('\r\n','\n');file:close()
    -- Execute the production measurer itself, keeping font and ASS tags identical.
    local measurement=assert(source:match('(local width_overlay=.-)\nlocal state='),'Production text measurement source is present')
    local factory=assert(loadstring(measurement..'\nreturn measure_text,width_overlay'))
    setfenv(factory,setmetatable({mp=mp,core=core,o={font='Microsoft YaHei'}},{__index=_G}))
    local native_measure,overlay=factory()
    check(overlay.hidden and overlay.compute_bounds,'Measurement uses hidden native bounds and never displays a test overlay')
    local calls=0
    local function measure(value,size,w,h) calls=calls+1;return native_measure(value,size,w,h) end
    local name='[VCB-S][Toaru Majutsu no Index][08][BDRIP][FLAC+AC3][1920X1080].HKG.ass'
    local state={x=-1,y=-1,scroll=0}
    local properties={sid=1,['track-list']={
        {id=1,type='sub',external=true,['external-filename']='C:/fixture/'..name},
        {id=2,type='sub',lang='zh',title='chs[KTXP&SumiSora]'},
        {id=3,type='sub',lang='zh',title='cht[KTXP&SumiSora]'},
    }}
    local metadata={['1']={managed=true,pinned=true}}
    local menu=create_menu({state=state,core=core,measure_text=measure,
        prop=function(key,default) local value=properties[key];if value==nil then return default end;return value end,
        subtitle_tracks=function()return metadata end,
        command=function()error('Layout checks must not issue player commands')end,
        set=function()error('Layout checks must not change playback properties')end,
        open_file=function()error('Layout checks must not open files')end,
    })
    local layout=core.layout(1640,922,1.5)
    local buttons,rounds={},{ }
    local function draw()
        buttons={};rounds={}
        menu.draw(layout,{
            round=function(x0,y0,x1,y1,r,color,alpha)rounds[#rounds+1]={x0=x0,y0=y0,x1=x1,y1=y1,color=color}end,
            rect=function()end,circle=function()end,icon=function()end,text=function()end,
            clip=function(_,_,_,_,fn)fn()end,
        },buttons)
    end
    local function button(key) for _,b in ipairs(buttons)do if b.key==key then return b end end;error('Missing button '..key) end
    menu.open('sub')
    local started=mp.get_time();draw();local first_elapsed=(mp.get_time()-started)*1000
    local first_calls=calls
    local box=menu.boxes[1]
    local entry
    for _,candidate in ipairs(box.rows)do if candidate.row.key=='sid:1' then entry=candidate end end
    check(entry~=nil,'Selected subtitle row exists')
    check(table.concat(entry.lines)==name,'Actual measured wrapping preserves every filename character')
    check(#entry.lines==2,'The reported filename occupies two native measured lines instead of three')
    local force=button('sid:1:pin');local delete=button('sid:1:delete');local row=button('sid:1')
    local left=box.x0+24*box.ui;local right=force.x0-8*box.ui
    local lines={}
    for index,line in ipairs(entry.lines)do
        local width=native_measure(line,18*box.ui,layout.w,layout.h)
        check(width>0,'Native text measurement returns a nonzero bound')
        check(left+width<=right+.001,'Native rendered filename stays clear of F and deletion controls')
        if entry.lines[index+1] then
            local next_char=core.chars(entry.lines[index+1])[1]
            check(left+native_measure(line..next_char,18*box.ui,layout.w,layout.h)>right,
                'The next glyph would exceed the available width, so the wrap is not premature')
        end
        lines[#lines+1]={text=line,width=width,right=left+width}
    end
    local function full_highlight()
        local cy=(row.y0+row.y1)/2
        for _,r in ipairs(rounds)do
            if r.color==core.theme().hover and r.y0<=cy and r.y1>=cy
                and math.abs(r.x0-(box.x0+8*box.ui))<.001 and math.abs(r.x1-(box.x1-8*box.ui))<.001 then return r end
        end
    end
    local selected=assert(full_highlight(),'Selected native-layout row has one full-width highlight')
    check(selected.x1>=delete.x1,'Selected highlight extends behind both operation controls')
    check(row.x1<=force.x0 and force.x1<=delete.x0,'Name, F and delete hit targets remain separate')
    for _,action in ipairs({force,delete})do
        state.x=(action.x0+action.x1)/2;state.y=(action.y0+action.y1)/2;draw()
        check(calls==first_calls,'Hover redraw reuses cached layout without additional native text measurements')
        check(full_highlight()~=nil,'Hovering an operation keeps the full-row highlight')
    end
    local old_lines=core.wrap(name,right-left,18*box.ui,0)
    check(#old_lines==3,'The previous estimate reproduces the reported three-line filename')
    local hover_extra_measurements=calls-first_calls
    properties.sid=9
    properties['track-list']={
        {id=8,type='sub',external=true,['external-filename']='C:/fixture/不正经的魔术讲师与禁忌教典 第08集.ass'},
        {id=9,type='sub',external=true,['external-filename']='C:/fixture/不正经的魔术讲师与禁忌教典 第09集.ass'},
    }
    metadata={['8']={managed=true,pinned=false},['9']={managed=true,pinned=false}}
    menu.invalidate();draw()
    for _,id in ipairs({8,9})do
        local name_button=button('sid:'..id)
        local force_button=button('sid:'..id..':pin')
        local delete_button=button('sid:'..id..':delete')
        check(name_button.x1<=force_button.x0 and force_button.x1<=delete_button.x0,
            'Automatic and manually selected subtitle rows both reuse the existing separate F and delete controls')
    end
    overlay:remove()
    return {ok=true,checks=checks,viewport={width=layout.w,height=layout.h,dpi=1.5},ui=box.ui,
        font='Microsoft YaHei',font_size=18*box.ui,line_count=#entry.lines,old_line_count=#old_lines,
        available_width=right-left,text_left=left,text_right_limit=right,lines=lines,
        force_x0=force.x0,delete_x1=delete.x1,highlight_x1=selected.x1,
        first_layout_ms=first_elapsed,first_layout_measurements=first_calls,hover_extra_measurements=hover_extra_measurements}
end
local ok,result=xpcall(run,debug.traceback)
if not ok then result={ok=false,checks=checks,error=result}end
local file=assert(io.open(report_path..'.pending','wb'));file:write(utils.format_json(result));file:close()
assert(os.rename(report_path..'.pending',report_path))

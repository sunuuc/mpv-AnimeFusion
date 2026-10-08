-- Anchored player controls and settings popovers.
-- All rows are derived from player state; no network lookups or guessed media metadata.
return function(c)
    local M={boxes={},rows={},hover_timer=nil,hover_target=nil,dismiss_timer=nil}
    local s,core=c.state,c.core
    local theme=c.theme or core.theme(c.accent)
    local metrics=core.metrics
    local data_cache,measure_cache={},{}
    local white,muted,panel,surface,accent=theme.text,theme.muted,theme.panel,theme.surface,theme.accent
    function M.invalidate() data_cache={};measure_cache={} end
    local function close_timer()
        if M.hover_timer then M.hover_timer:kill();M.hover_timer=nil end
        M.hover_target=nil
    end
    local function dismiss_timer()
        if M.dismiss_timer then M.dismiss_timer:kill();M.dismiss_timer=nil end
    end
    function M.close()
        local was_open=s.menu~=nil
        close_timer();dismiss_timer();M.invalidate();M.boxes={};M.rows={};s.menu=nil;s.parent=nil;s.scroll=0;s.parent_scroll=0;s.menu_drag=nil
        if was_open and c.on_close then c.on_close() end
    end
    function M.open(kind,parent)
        if not M.allowed[kind] then return end
        close_timer();dismiss_timer();M.hover_blocked=nil
        if s.menu==kind and not parent then M.close();return end
        M.invalidate()
        if parent then s.parent=parent else s.parent=nil;s.parent_scroll=0 end
        s.menu=kind;s.scroll=0;s.menu_drag=nil
        if kind=='ai' then c.read_presets() end
    end
    function M.back()
        if not s.menu then return false end
        dismiss_timer()
        if s.parent then M.hover_blocked=s.menu;s.menu=s.parent;s.parent=nil;s.scroll=s.parent_scroll or 0;s.parent_scroll=0;close_timer()
        else M.close() end
        return true
    end
    local LANG={ja='日语',jp='日语',jpn='日语',zh='中文',chi='中文',zho='中文',
        en='英语',eng='英语',ko='韩语',kor='韩语',fr='法语',fre='法语',fra='法语',
        de='德语',ger='德语',deu='德语',es='西班牙语',spa='西班牙语',
        pt='葡萄牙语',por='葡萄牙语',ru='俄语',rus='俄语',it='意大利语',ita='意大利语',
        th='泰语',tha='泰语',vi='越南语',vie='越南语',ar='阿拉伯语',ara='阿拉伯语',
        id='印尼语',ind='印尼语',nl='荷兰语',nld='荷兰语',pl='波兰语',pol='波兰语'}
    local CODEC={subrip='SRT',srt='SRT',ass='ASS',ssa='ASS',webvtt='WebVTT',vtt='WebVTT',
        mov_text='MOV 文本',microdvd='MicroDVD',subviewer='SubViewer',text='文本',
        dvd_subtitle='DVD 图形字幕',dvb_subtitle='DVB 图形字幕',hdmv_pgs_subtitle='PGS 图形字幕',
        pgs='PGS 图形字幕',dvb_teletext='图文电视',eia_608='隐藏字幕',
        aac='AAC',ac3='AC3',eac3='E-AC3',dts='DTS',truehd='TrueHD',flac='FLAC',
        opus='Opus',mp3='MP3',vorbis='Vorbis',pcm_s16le='PCM',pcm_s24le='PCM'}
    local function lang_label(value)
        if not value or value=='' then return nil end
        local key=value:lower():gsub('_','-')
        if key:match('^zh') then
            if key:match('hans') or key:match('^zh%-cn') or key:match('^zh%-sg') then return '中文（简体）' end
            if key:match('hant') or key:match('^zh%-tw') or key:match('^zh%-hk') or key:match('^zh%-mo') then return '中文（繁体）' end
            return '中文'
        end
        return LANG[key] or LANG[key:match('^%a+') or ''] or value
    end
    local function codec_label(value)
        if not value or value=='' then return nil end
        local key=value:lower()
        return CODEC[key] or key:upper()
    end
    local function title_label(value)
        if not value or value=='' then return nil end
        local title=value:gsub('^%s+',''):gsub('%s+$','')
        if title:match('%?') or title:match('://') or title:match('/') then
            title=(title:match('^[^?]*') or title)
            title=title:gsub('^%a[%w+.-]*://',''):gsub('^.*[/\\]','')
            title=title:gsub('%%(%x%x)',function(hex)return string.char(tonumber(hex,16))end)
        end
        if title=='' or title:match('^[Ss]tream%.[%w]+$') then return nil end
        return title
    end
    local function track_name(t)
        local parts={}
        local function add(value) if value and value~='' then parts[#parts+1]=value end end
        local lang=lang_label(t.lang)
        local name=title_label(t.title) or (t.external and title_label(t['external-filename'] or '') or nil)
        add(lang);add(name);add(codec_label(t.codec))
        if t.type=='audio' and t['audio-channels'] then add(t['audio-channels']..' 声道') end
        if t.external then add('外挂') end
        if #parts==0 then return (t.type=='sub' and '字幕 ' or '音轨 ')..t.id end
        if not lang and not name then add('#'..t.id) end
        return table.concat(parts,' · ')
    end
    function M.data(kind)
        local cached=data_cache[kind]
        if cached then return cached.title,cached.items end
        local a={};local title=M.allowed[kind]
        if kind=='speed' or kind=='ai' or kind=='scale' then title=nil end
        local function row(text,fn,selected,extra)
            local r=extra or {};r.text=text;r.fn=fn;r.selected=selected;r.disabled=fn==nil and not r.target and not r.slider
            a[#a+1]=r;return r
        end
        local function link(text,target,icon)row(text,nil,false,{target=target,icon=icon})end
        local function separator()a[#a+1]={separator=true,h=12}end
        local account=c.account and c.account() or {}
        local function action(...)c.command('script-message-to','mpvnet','bangumi-action',...)end
        local function enabled(fn)return not account.busy and fn or nil end
        if kind=='settings' then
            link('缩放模式','scale')
            link('超分与补帧','ai')
            link('字幕设置','sub-settings','sub')
            link('弹幕设置','danmaku-settings','danmaku')
            link('同步设置','sync-settings','bangumi')
            link('统计信息','stats','info')
            row('显示时间',c.toggle_clock,c.clock(),{stay=true})
        elseif kind=='sync-settings' then
            local settings=account.settings or {}
            row('自动收藏为在看',function()action('setting','auto-collect',settings.autoCollect and 'no' or 'yes')end,settings.autoCollect,{stay=true})
            row('收藏进度',nil,false,{slider={value=settings.collectPercent or 10,min=1,max=100,step=1,format='%.0f%%',
                set=function(v)action('setting','collect-percent',tostring(v))end}})
            separator()
            row('自动标记剧集看过',function()action('setting','auto-sync',settings.autoSync and 'no' or 'yes')end,settings.autoSync,{stay=true})
            row('看过进度',nil,false,{slider={value=settings.watchedPercent or 90,min=1,max=100,step=1,format='%.0f%%',
                set=function(v)action('setting','watched-percent',tostring(v))end}})
        elseif kind=='bangumi' then
            if not account.connected then
                local retry=account.authorizing or (account.status and account.status~='')
                local login=(not account.busy or account.authorizing) and function()action('login')end or nil
                row(retry and '重新授权' or '登录 Bangumi',login,false,{stay=true})
                if account.authorizing then row('等待浏览器授权…',nil,false,{status=true,spinner=true}) end
            else
                local subject=account.subject
                if subject then
                    row(subject.title,nil,false,{card=subject,h=156,status=true})
                    link('收藏：'..({[0]='未收藏','想看','看过','在看','搁置','抛弃'})[subject.collectionType or 0],'bangumi-collection')
                    for _,episode in ipairs(subject.episodes or {}) do
                        if episode.id==subject.currentEpisode then row('当前：'..episode.title,nil,false,{status=true,wrap=2});break end
                    end
                    row(subject.kind=='电影' and '正片' or '剧集',nil,false,{h=36,status=true})
                    local cells={}
                    for _,episode in ipairs(subject.episodes or {}) do
                        local ep=episode
                        cells[#cells+1]={text=subject.kind=='电影' and ep.kind==0 and '正片' or (ep.kind==0 and '' or 'SP ')..tostring(ep.number),
                            key='bangumi-episode:'..ep.id,selected=ep.id==subject.currentEpisode,state=ep.state,
                            disabled=account.busy,stay=true,fn=function()s.bangumi_episode=ep.id;c.open('bangumi-episode','bangumi')end}
                        if #cells==6 then row('',nil,false,{cells=cells,h=56});cells={} end
                    end
                    if #cells>0 then row('',nil,false,{cells=cells,h=56}) end
                elseif account.resolving then
                    row('正在匹配…',nil,false,{status=true,spinner=true})
                end
                separator()
                if not account.resolving and (not subject or not subject.currentEpisode) then
                    row('重新匹配',function()action('retry-match')end,false,{stay=true})
                end
                row('选择条目…',function()c.command('script-message-to','mpvnet','show-bangumi-match')end)
            end
            if account.status and account.status~='' then row(account.status,nil,false,{status=true,wrap=2}) end
        elseif kind=='bangumi-collection' then
            local subject=account.subject or {}
            for type,label in ipairs({'想看','看过','在看','搁置','抛弃'}) do local value=type
                row(label,enabled(function()action('collection',tostring(value),tostring(account.generation))end),subject.collectionType==value,{stay=true})
            end
        elseif kind=='bangumi-episode' then
            local subject=account.subject or {};local episode
            for _,ep in ipairs(subject.episodes or {}) do if ep.id==s.bangumi_episode then episode=ep;break end end
            if episode then
                row(episode.title,nil,false,{status=true,wrap=3})
                row('设为当前剧集',enabled(function()action('select-episode',tostring(episode.id),tostring(account.generation))end),
                    subject.currentEpisode==episode.id,{stay=true})
                separator()
                local can_edit=(subject.collectionType or 0)>0 and not account.busy
                for _,item in ipairs({{'看过',2},{'看到',2,'through'},{'想看',1},{'抛弃',3},{'未看',0}}) do local label,value,mode=item[1],item[2],item[3]
                    row(label,can_edit and (mode~='through' or episode.kind==0) and function()
                        action('episode',tostring(episode.id),tostring(value),mode or 'single',tostring(account.generation))
                    end or nil,mode~='through' and episode.state==value,{stay=true})
                end
                if not can_edit and not account.busy then row('请先收藏',nil,false,{status=true}) end
            end
        elseif kind=='speed' then
            for _,v in ipairs({8,5,3,2,1.5,1.25,1,.5}) do local n=v
                local label=v%1==0 and string.format('%.1fx',v) or string.format('%gx',v)
                row(label,function()c.set_number('speed',n)end,math.abs(c.num('speed',1)-v)<.001)
            end
        elseif kind=='sub' or kind=='audio' then
            local typ=kind=='sub' and 'sub' or 'audio'
            local p=kind=='audio' and 'aid' or 'sid'
            local selected=tostring(c.prop(p,'no'))
            row('关闭',function()c.set(p,'no')end,selected=='no',{stay=true,key=p..':no'})
            for _,t in ipairs(c.prop('track-list',{}) or {}) do
                local danmaku=c.prop('user-data/player_ui/danmaku',{}) or {}
                if t.type==typ and not (typ=='sub' and t.id==danmaku.track) then local id=t.id
                    row(track_name(t),function()
                        c.set_number(p,id)
                        if p=='sid' then c.set_bool('sub-visibility',true) end
                    end,selected==tostring(id),{stay=true,key=p..':'..id})
                end
            end
            separator()
            row(kind=='sub' and '导入本地字幕' or '添加音轨',function()c.open_file(kind)end,false,{icon='plus'})
            if kind=='audio' then link('音频设置','audio-settings') end
        elseif kind=='danmaku' then
            local d=c.prop('user-data/player_ui/danmaku',{}) or {}
            row('关闭',function()if d.enabled then c.command('script-message','player_ui-danmaku-toggle')end end,not d.loaded or not d.enabled,{stay=true})
            if d.autoload_state=='loading' then
                row('自动加载中…',nil,false,{status=true,spinner=true})
            elseif not d.loaded and d.autoload_state=='error' then
                row('自动加载失败',nil,false,{status=true})
                row('重试匹配',function()c.command('script-message','player_ui-danmaku-retry-match')end,false,{stay=true})
            elseif not d.loaded and d.autoload_state=='not-found' then
                row('没有匹配到弹幕',nil,false,{status=true})
                row('重试匹配',function()c.command('script-message','player_ui-danmaku-retry-match')end,false,{stay=true})
            elseif not d.loaded and d.autoload_state=='empty' then
                row('该集没有弹幕',nil,false,{status=true})
            elseif d.loaded then
                row(core.title('',d.file),function()if not d.enabled then c.command('script-message','player_ui-danmaku-toggle')end end,d.enabled,
                    {stay=true,wrap=5,detail=(d.source_label or '本地弹幕')..'·'..tostring(d.count or 0)..'条弹幕'})
            end
            separator()
            row('搜索弹幕',function()c.command('script-message','player_ui-danmaku-search')end)
            row('导入本地弹幕',function()c.command('script-message','player_ui-danmaku-choose')end)
        elseif kind=='ai' then
            local id=c.current_slot()
            row('关闭',function()c.select_slot(0)end,id==0,{hint='Ctrl+0',key='preset:0'})
            for n=1,9 do local v=n;local name=c.presets()[n]
                if name then row(name,function()c.select_slot(v)end,id==v,{hint='Ctrl+'..v,key='preset:'..v}) end
            end
            separator()
            row('配置管理器',c.manager,false,{icon='settings'})
        elseif kind=='scale' then
            local keep=c.bool('keepaspect',true);local original=c.prop('video-unscaled','no')
            local pan=c.num('panscan',0)
            for _,r in ipairs({{'适应窗口','fit'},{'填充裁剪','fill'},{'拉伸铺满','stretch'},{'原始尺寸','original'}}) do local mode=r[2]
                local selected=(mode=='stretch' and not keep) or (keep and ((mode=='original' and original=='yes') or (original~='yes' and ((mode=='fill' and pan==1) or (mode=='fit' and pan==0)))))
                row(r[1],function()
                    c.set_bool('keepaspect',mode~='stretch');c.set('video-unscaled',mode=='original' and 'yes' or 'no')
                    c.set_number('panscan',mode=='fill' and 1 or 0);c.set_number('video-zoom',0);c.set('video-aspect-override','no')
                end,selected)
            end
        elseif kind=='sub-settings' then
            row('字号缩放',nil,false,{slider={value=c.num('sub-scale',1),min=.5,max=2,step=.05,format='%.2fx',set=function(v)c.set_number('sub-scale',v)end}})
            row('字幕位置',nil,false,{slider={value=c.num('sub-pos',100),min=0,max=100,step=1,format='%.0f%%',set=function(v)c.set_number('sub-pos',v)end}})
            row('延迟  '..string.format('%+.1f 秒',c.num('sub-delay',0)),nil,false,{h=44})
            row('提前 0.1 秒',function()c.command('add','sub-delay','-0.1')end,false,{stay=true})
            row('延后 0.1 秒',function()c.command('add','sub-delay','0.1')end,false,{stay=true})
            row('重置延迟',function()c.set_number('sub-delay',0)end,false,{stay=true})
        elseif kind=='audio-settings' then
            row('音频延迟  '..string.format('%+.1f 秒',c.num('audio-delay',0)),nil,false,{h=44})
            row('提前 0.1 秒',function()c.command('add','audio-delay','-0.1')end,false,{stay=true})
            row('延后 0.1 秒',function()c.command('add','audio-delay','0.1')end,false,{stay=true})
            row('重置延迟',function()c.set_number('audio-delay',0)end,false,{stay=true})
        elseif kind=='danmaku-settings' then
            row('弹幕线路…',function()c.command('script-message','player_ui-danmaku-manage')end,false,{icon='settings'})
            local d=c.prop('user-data/player_ui/danmaku',{}) or {}
            local settings=d.settings or {}
            local function set(key,value) c.command('script-message','player_ui-danmaku-setting',key,tostring(value)) end
            local function slider(label,key,value,min,max,step,format)
                row(label,nil,false,{slider={value=value,min=min,max=max,step=step,
                    format=format,set=function(v)set(key,v)end}})
            end
            slider('显示区域','area',(settings.displayArea or 1)*100,10,100,5,'%.0f%%')
            slider('不透明度','opacity-percent',(settings.opacity or 180)*100/255,1,100,1,'%.0f%%')
            slider('弹幕字号','fontsize',settings.fontsize or 38,12,100,1,'%.0f')
            slider('速度','speed',12/(settings.scrolltime or 12),.5,3,.1,'%.1f×')
            slider('弹幕帧率','fps',settings.fps or 60,30,90,30,'%.0f FPS')
            separator()
            for _,group in ipairs({{label='屏蔽固定弹幕',modes={'TOP','BOTTOM'}},
                {label='屏蔽滚动弹幕',modes={'R2L','L2R'}},{label='屏蔽彩色弹幕',modes={'COLOR'}}}) do
                local selected={}
                for _,mode in ipairs(group.modes) do selected[mode]=true end
                local blocked=false
                for _,value in ipairs(settings.blockmode or {}) do if selected[value] then blocked=true end end
                row(group.label,function()
                    local values={}
                    for _,value in ipairs(settings.blockmode or {}) do if not selected[value] then values[#values+1]='"'..value..'"' end end
                    if not blocked then for _,mode in ipairs(group.modes) do values[#values+1]='"'..mode..'"' end end
                    set('blockmode','['..table.concat(values,',')..']')
                end,blocked,{stay=true})
            end
            row('屏蔽词…',function()c.command('script-message','player_ui-danmaku-words')end)
        else
            title,a=c.info(kind)
            for _,r in ipairs(a) do r.h=52;r.wrap=2;r.key=r.key or r.text end
        end
        data_cache[kind]={title=title,items=a}
        return title,a
    end
    M.allowed={settings='设置',speed='播放速度',sub='字幕',audio='音轨',danmaku='弹幕',ai='超分与补帧',scale='缩放模式',
        ['sub-settings']='字幕设置',['danmaku-settings']='弹幕设置',
        ['audio-settings']='音频设置',stats='统计信息',chapters='章节',
        bangumi='Bangumi',['sync-settings']='同步设置',['bangumi-collection']='收藏',['bangumi-episode']='剧集'}
    local function width(kind,l)
        local widths={speed=216,settings=200,sub=344,audio=344,danmaku=312,ai=256,scale=280,stats=440,chapters=368,bangumi=440}
        local u=l.ui or 1
        return math.min((widths[kind] or 344)*u,math.max(1,l.w-2*metrics.menu_margin*u))
    end
    local function measure(items,w,kind,u)
        local key=string.format('%s:%.2f:%.3f',kind,w,u)
        local cached=measure_cache[key]
        if cached and cached.items==items then return cached.total,cached.rows end
        local y,rows=0,{}
        for _,r in ipairs(items) do
            local indent=r.icon and metrics.icon_column*u or metrics.row_padding*u+8*u
            local available=w-indent-30*u-(r.hint and 52*u or 0)-(r.target and 28*u or 0)
            local lines=core.wrap(r.text or '',available,metrics.row_text*u,r.wrap or 2)
            local height=r.h and r.h*u or r.separator and metrics.separator*u or r.slider and metrics.slider*u
                or math.max(kind=='ai' and 48*u or metrics.row_min*u,
                    #lines*metrics.row_line*u+(r.detail and metrics.row_detail_line*u+5*u or 0)+metrics.row_padding*u)
            rows[#rows+1]={row=r,top=y,height=height,lines=lines}
            y=y+height
        end
        measure_cache[key]={items=items,total=y,rows=rows}
        return y,rows
    end
    local function anchor(l,id)
        for _,b in ipairs(l.controls) do if b.id==id then return b.x end end
        return l.w-232
    end
    local function header_height(kind,title,u) return title and metrics.menu_header*u or 0 end
    local function makebox(l,kind)
        local u=l.ui or 1
        local title,items=M.data(kind);local w=width(kind,l);local hh=header_height(kind,title,u)
        local total,rows=measure(items,w,kind,u);local bottom=l.h-(kind=='speed' and metrics.menu_speed_bottom or metrics.menu_bottom)*u
        local pad=metrics.menu_padding*u
        local margin=math.min(metrics.menu_margin*u,math.max(0,(l.w-w)/2))
        local max_height=math.max(1,l.h-2*margin)
        local h=math.min(hh+total+pad*2,max_height,math.max(1,bottom-margin));local y=math.max(margin,bottom-h)
        local max_x=math.max(margin,l.w-w-margin)
        local x=core.clamp(anchor(l,kind)-w/2,margin,max_x)
        return {kind=kind,title=title,items=items,rows=rows,x0=x,x1=x+w,y0=y,y1=y+h,header=hh,total=total,
            view=math.max(0,h-hh-pad*2),content=y+hh+pad,ui=u}
    end
    function M.draw(l,d,buttons)
        M.boxes={};M.rows={}
        if not s.menu then return M.rows end
        local function add(b) b.menu=true;buttons[#buttons+1]=b end
        local function drawbox(b,scroll_key,is_child)
            local u=b.ui or 1;local w=b.x1-b.x0
            scroll_key=scroll_key or 'scroll'
            s[scroll_key]=core.clamp(s[scroll_key] or 0,0,math.max(0,b.total-b.view));b.offset=s[scroll_key];b.scroll_key=scroll_key
            M.boxes[#M.boxes+1]=b
            d.round(b.x0,b.y0+3*u,b.x1,b.y1+3*u,9*u,theme.border,56)
            d.round(b.x0,b.y0,b.x1,b.y1,9*u,panel,5)
            if b.title then
                d.text(b.x0+metrics.row_padding*u,b.y0+29*u,22*u,b.title,4,white,true,w-80*u,.8)
            end
            if b.kind=='bangumi' and c.account and c.account().connected then
                local account=c.account()
                local button={id='bangumi-logout',key='退出登录',x0=b.x1-52*u,x1=b.x1-12*u,
                    y0=b.y0+9*u,y1=b.y0+49*u,disabled=account.busy}
                local hover=core.inside(button,s.x,s.y)
                if hover then d.round(button.x0,button.y0,button.x1,button.y1,6*u,theme.hover,5) end
                d.icon('logout',b.x1-32*u,b.y0+29*u,hover,account.busy,true)
                add(button)
            end
            local bottom=b.content+b.view
            for _,entry in ipairs(b.rows) do
                local r=entry.row
                if not r.separator then M.rows[#M.rows+1]=r end
                local index=#M.rows
                local yy=b.content+entry.top-b.offset;local y1=yy+entry.height
                if y1>b.content and yy<bottom then
                    -- Keep vertical clipping for scrolled rows, but do not clip text
                    -- to the panel's horizontal edge. libass can crop CJK glyphs at
                    -- that boundary after DPI scaling even when the anchor is inset.
                    d.clip(0,b.content,l.w,bottom,function()
                        if r.separator then d.rect(b.x0+12*u,yy+entry.height/2,b.x1-12*u,yy+entry.height/2+u,theme.divider,18);return end
                        local box={id='row-'..index,index=index,x0=b.x0+6*u,x1=b.x1-6*u,y0=math.max(yy,b.content),y1=math.min(y1,bottom),key=r.key or r.text}
                        if r.card then
                            local left=b.x0+120*u;local available=w-140*u
                            d.round(b.x0+20*u,yy+8*u,b.x0+106*u,yy+137*u,5*u,surface,0)
                            if d.image then d.image(r.card.cover,b.x0+20*u,yy+8*u,86*u,129*u,b.content,bottom) end
                            local cy=yy+12*u
                            for _,line in ipairs(core.wrap(r.text,available,20*u,3)) do d.text(left,cy,20*u,line,7,white,true,nil,.8);cy=cy+24*u end
                            local detail=r.card.kind=='电影' and '电影' or ('第 '..tostring(r.card.season or 1)..' 季')
                            d.text(left,yy+88*u,14*u,(r.card.date or '')..' · '..detail,7,muted,false,available,.8)
                            d.text(left,yy+116*u,22*u,r.card.score and string.format('%.1f',r.card.score) or '暂无评分',7,accent,true,nil,.8)
                            return
                        elseif r.cells then
                            local gap=6*u;local cw=(w-32*u-5*gap)/6
                            for column,cell in ipairs(r.cells) do
                                M.rows[#M.rows+1]=cell
                                local x=b.x0+16*u+(column-1)*(cw+gap)
                                local cellbox={id='row-'..#M.rows,index=#M.rows,key=cell.key,x0=x,x1=x+cw,y0=math.max(yy+3*u,b.content),y1=math.min(y1-3*u,bottom)}
                                local hover=core.inside(cellbox,s.x,s.y)
                                local watched=cell.state==2
                                local marker=cell.selected or not watched
                                d.round(x,yy+3*u,x+cw,y1-(marker and 10 or 3)*u,6*u,watched and accent or (hover and theme.hover or surface),watched and 0 or 5)
                                local colors={[1]=theme.secondary,[2]=accent,[3]=muted}
                                if marker then
                                    d.round(x,yy+entry.height-8*u,x+cw,yy+entry.height-4*u,2*u,cell.selected and theme.current or colors[cell.state] or theme.track,(not cell.selected and cell.state==0) and 120 or 0)
                                end
                                d.text(x+cw/2,yy+entry.height/2,16*u,cell.text,5,cell.disabled and muted or white,cell.selected,nil,.8)
                                if not cell.disabled then add(cellbox) end
                            end
                            return
                        end
                        local hovered=core.inside(box,s.x,s.y)
                        local picked=r.selected or (r.target and r.target==s.menu)
                        if hovered or picked then
                            d.round(box.x0+2*u,yy+3*u,box.x1-2*u,y1-3*u,6*u,theme.hover,5)
                        end
                        if picked then d.round(b.x0+8*u,yy+entry.height/2-12*u,b.x0+11*u,yy+entry.height/2+12*u,1.5*u,accent,0) end
                        if not r.disabled then add(box) end
                        local left=b.x0+metrics.row_padding*u+8*u
                        if r.icon then d.icon(r.icon,b.x0+30*u,yy+entry.height/2,false,false,true);left=b.x0+metrics.icon_column*u end
                        local color=r.disabled and not r.status and muted or white
                        if r.slider then
                            local slider=r.slider;local sx=b.x0+metrics.row_padding*u;local ex=b.x1-metrics.row_padding*u;local sy=yy+62*u
                            d.text(left,yy+22*u,metrics.row_text*u,r.text,4,color,false,nil,.8)
                            d.text(ex,yy+22*u,metrics.row_text*u,string.format(slider.format,slider.value),6,muted,false,nil,.8)
                            d.round(sx,sy-2*u,ex,sy+2*u,2*u,theme.track,0)
                            local xx=sx+(ex-sx)*core.clamp((slider.value-slider.min)/(slider.max-slider.min),0,1)
                            d.round(sx,sy-2*u,xx,sy+2*u,2*u,accent,0);d.circle(xx,sy,6*u,accent,0)
                            add({id='slider-'..index,slider=index,x0=sx-8*u,x1=ex+8*u,y0=sy-18*u,y1=sy+18*u,start=sx,finish=ex})
                        else
                            local detail_height=r.detail and (metrics.row_detail_line+5)*u or 0
                            local text_height=#entry.lines*metrics.row_line*u+detail_height
                            local ty=yy+(entry.height-text_height)/2
                            for _,line in ipairs(entry.lines) do d.text(left,ty,metrics.row_text*u,line,7,color,false,nil,.8);ty=ty+metrics.row_line*u end
                            if r.spinner then
                                local cx,cy=b.x1-30*u,yy+entry.height/2
                                for dot=0,7 do
                                    local angle=dot*math.pi/4
                                    d.circle(cx+math.sin(angle)*8*u,cy-math.cos(angle)*8*u,2*u,accent,
                                        35+math.floor(((dot-(s.tick or 0))%8)*185/7))
                                end
                            end
                            if r.detail then d.text(left,ty+2*u,metrics.row_detail*u,r.detail,7,muted,false,w-(left-b.x0)-24*u,.8) end
                            if r.target then d.text(b.x1-25*u,yy+entry.height/2,27*u,'›',6,theme.secondary,false,nil,.8)
                            elseif r.hint then d.text(b.x1-19*u,yy+entry.height/2,13*u,r.hint,6,muted,false,nil,.8) end
                        end
                    end)
                end
            end
            if b.total>b.view then
                local height=math.max(28*u,b.view*b.view/b.total)
                local sy=b.content+(b.view-height)*b.offset/(b.total-b.view)
                d.round(b.x1-7*u,sy,b.x1-4*u,sy+height,2*u,theme.secondary,45)
                add({id='menu-scroll-'..b.kind,x0=b.x1-13*u,x1=b.x1,y0=b.content,y1=b.content+b.view,scroll_key=scroll_key,
                    start=b.content,range=b.view-height,max=b.total-b.view,height=height,sy=sy})
            end
        end
        local b=makebox(l,s.menu)
        if s.parent then
            local parent=makebox(l,s.parent)
            local u=l.ui or 1;local gap=8*u;local margin=metrics.menu_margin*u
            local child_width=b.x1-b.x0;local left=parent.x0-child_width-gap
            if left<margin then
                local right=parent.x1+gap
                left=right+child_width<=l.w-margin and right or margin
            end
            local dx=left-b.x0;local dy=parent.y1-b.y1
            b.x0=b.x0+dx;b.x1=b.x1+dx;b.y0=b.y0+dy;b.y1=b.y1+dy;b.content=b.content+dy
            drawbox(parent,'parent_scroll',false)
            drawbox(b,'scroll',true)
        else drawbox(b,'scroll',false) end
        return M.rows
    end
    function M.pick(b)
        if b.id=='menu-back' then M.back()
        elseif b.id=='menu-dismiss' then M.close()
        elseif b.id=='bangumi-logout' and not b.disabled then
            c.command('script-message-to','mpvnet','bangumi-action','logout');M.close()
        elseif b.index then
            local r=M.rows[b.index]
            if r and r.target then
                local parent=s.parent or s.menu
                s.parent_scroll=s.parent and s.parent_scroll or s.scroll
                c.open(r.target,parent)
            elseif r and r.fn and not r.disabled then r.fn();if not r.stay then M.close() end end
        end
    end
    function M.press(b,x,y)
        if b.slider then
            local r=M.rows[b.slider]
            if r and r.slider then s.menu_drag={slider=r.slider,box=b};M.drag(x,y);return true end
        elseif b.scroll_key then
            s.menu_drag={box=b,grab=y>=b.sy and y<=b.sy+b.height and y-b.sy or b.height/2}
            M.drag(x,y);return true
        end
        return false
    end
    function M.drag(x,y)
        local p=s.menu_drag;if not p then return end
        if p.slider then
            local r=p.slider;local v=r.min+core.clamp((x-p.box.start)/(p.box.finish-p.box.start),0,1)*(r.max-r.min)
            v=core.clamp(math.floor(v/r.step+.5)*r.step,r.min,r.max)
            if p.last~=v then p.last=v;r.set(v) end
        else local b=p.box;s[b.scroll_key]=core.clamp((y-b.start-p.grab)/math.max(1,b.range),0,1)*b.max end
    end
    function M.wheel(delta,x,y)
        if not s.menu then return false end
        for i=#M.boxes,1,-1 do local b=M.boxes[i]
            if core.inside(b,x,y) then s[b.scroll_key]=core.clamp((s[b.scroll_key] or 0)-delta*(b.drawer and 72 or 60),0,math.max(0,b.total-b.view));return true end
        end
        return s.menu~=nil
    end
    function M.hover(b,suppress_dismiss)
        if not s.menu then close_timer();dismiss_timer();return end
        if suppress_dismiss then close_timer();dismiss_timer();return end
        local id=b and b.id
        local menu_surface=b and b.menu and id~='menu-dismiss'
        local menu_trigger=id=='settings' or id=='speed' or id=='audio' or id=='sub'
            or id=='danmaku' or id=='bangumi'
        if menu_surface or menu_trigger then dismiss_timer()
        elseif not M.dismiss_timer then
            M.dismiss_timer=c.after(.45,function()
                M.dismiss_timer=nil
                if s.menu then M.close() end
            end)
        end
        local r=b and b.index and M.rows[b.index]
        local target=r and r.target
        if target~=M.hover_blocked then M.hover_blocked=nil end
        if target and target==M.hover_blocked then close_timer();return end
        if target==s.menu then close_timer();return end
        if target==M.hover_target then return end
        close_timer()
        if target then
            M.hover_target=target
            M.hover_timer=c.after(.18,function()
                M.hover_timer=nil;M.hover_target=nil
                if s.menu then
                    s.parent_scroll=s.parent and s.parent_scroll or s.scroll
                    c.open(target,s.parent or s.menu)
                end
            end)
        end
    end
    function M.hit_override(b,x,y)
        if not s.menu then return b end
        if b and b.menu then return b end
        for _,box in ipairs(M.boxes) do if core.inside(box,x,y) then return {id='menu-surface',menu=true} end end
        if b and (b.id=='settings' or b.id=='speed' or b.id=='audio'
            or b.id=='sub' or b.id=='danmaku' or b.id=='bangumi') then return b end
        return {id='menu-dismiss',menu=true}
    end
    function M.shutdown()close_timer();dismiss_timer()end
    return M
end

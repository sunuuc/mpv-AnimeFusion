local root=assert(arg[1])
local online=dofile(root..'/portable_config/script-modules/player_ui_danmaku_online.lua')
assert(online.series_source_key('星海旅行记 (2020) S1E1.mkv')==online.series_source_key('星海旅行记 S1E2.mp4'))
assert(online.series_source_key('星海旅行记 S1E1')~=online.series_source_key('星海旅行记 S2E1'))
assert(online.series_source_key('星海旅行记 E1')~=online.series_source_key('星海旅行记 S1E1'))
assert(online.series_source_key('星海旅行记 Part 1 S1E1')~=online.series_source_key('星海旅行记 Part 2 S1E1'))
assert(online.series_source_key('其他作品 S1E1')~=online.series_source_key('星海旅行记 S1E1'))
assert(online.series_source_key('星海旅行记 剧场版')==nil)
local data={animes={
    {animeId=1,animeTitle='星海旅行记(2020)',typeDescription='TV动画',source='dandan'},
    {animeId=2,animeTitle='星海旅行记 第二季(2021)',typeDescription='TV动画',source='dandan'},
    {animeId=3,animeTitle='别的作品(2020)',typeDescription='TV动画',source='dandan'},
    {animeId=4,animeTitle='星海旅行记 电影(2020)',typeDescription='电影',source='dandan'},
}}
local result=assert(online.search_results('data','星海旅行记',function()return data end))
assert(result[1].season==1 and result[2].season==2,'original series must be classified as season one')
assert(result[3].season==nil and result[4].season==nil,'unrelated series and movies must not gain a season')
local candidates=online.auto_candidates(result,'星海旅行记 S1E1')
assert(#candidates==1 and candidates[1].id=='1','season one must still auto-match')
candidates=online.auto_candidates(result,'星海旅行记 S2E1')
assert(#candidates==1 and candidates[1].id=='2','season two must remain distinct')
data.animes[2].animeTitle='星海旅行记♪♪(2021)'
data.animes[2].aliases={'星海旅行记 第二季'}
result=assert(online.search_results('data','星海旅行记',function()return data end))
assert(result[1].season==1 and result[2].season==2,'sequel aliases must classify the original series')
data.animes[#data.animes+1]={animeId=5,animeTitle='星海旅行记2(2021)',source='dandan'}
data.animes[2].aliases[#data.animes[2].aliases+1]='星海旅行记2'
result=assert(online.search_results('data','星海旅行记',function()return data end))
assert(result[5].season==2,'numbered sequel backed by an earlier original must not become season one')
local numbered={animes={
    {animeId=11,animeTitle='遥远星海3(2024)【TV动画】from dandan',typeDescription='TV动画',aliases={'Faraway Stars 3'}},
    {animeId=12,animeTitle='遥远星海2(2023)【TV动画】from dandan',typeDescription='TV动画',aliases={'Faraway Stars 2'}},
    {animeId=13,animeTitle='遥远星海(2021)【TV动画】from dandan',typeDescription='TV动画',aliases={'Faraway Stars'}},
    {animeId=14,animeTitle='遥远星海、(2020)',typeDescription='OVA'},
    {animeId=15,animeTitle='遥远星海2(2025)',typeDescription='TV特别放送'},
    {animeId=16,animeTitle='独立作品2(2025)',typeDescription='TV动画'},
    {animeId=17,animeTitle='遥远星海4(2021)',typeDescription='TV动画'},
}}
result=assert(online.search_results('data','遥远星海',function()return numbered end))
assert(result[1].season==3 and result[2].season==2 and result[3].season==1)
assert(result[4].season==nil and result[5].season==nil,'OVA and specials must not gain a season')
assert(result[6].season==nil and result[7].season==nil,'numbers need a distinct earlier original, never a guess')
assert(result[1].label=='遥远星海3(2024)' and result[1].id=='11','keep display title and platform ID')
candidates=online.auto_candidates(result,'遥远星海 S1E2')
assert(#candidates==1 and candidates[1].id=='13','an OVA with the same title must not compete with season one')
candidates=online.auto_candidates(result,'遥远星海 S2E2')
assert(#candidates==1 and candidates[1].id=='12','numeric sequel must match the exact season')
candidates=online.auto_candidates(result,'Faraway Stars S3E1')
assert(#candidates==1 and candidates[1].id=='11','numeric sequels must retain multilingual series aliases')
numbered.animes[2].aliases[#numbered.animes[2].aliases+1]='Faraway Stars 3'
result=assert(online.search_results('data','遥远星海',function()return numbered end))
assert(result[2].season==nil and result[2].season_ambiguous,'conflicting numbered aliases must not choose a season')
local err
result,err=online.search_results('data','星海旅行记',function()return {success=false,errorMessage='暂时不可用'} end)
assert(result==nil and err=='暂时不可用','provider errors must return an error instead of throwing')

local account={connected=true,resolving=false,subject={title='星海旅行记',currentEpisode=10,episodes={}}}
local danmaku={autoload_state='not-found',loaded=false}
local commands={}
local core=dofile(root..'/portable_config/script-modules/player_ui_core.lua')
local menu=dofile(root..'/portable_config/script-modules/player_ui_menu.lua')({
    state={},core=core,account=function()return account end,
    prop=function()return danmaku end,
    command=function(...)commands[#commands+1]={...}end,
})
local function find(rows,label)
    for _,row in ipairs(rows) do if row.text==label then return row end end
end
local _,rows=menu.data('bangumi')
assert(not find(rows,'重新匹配'),'successfully matched episode must not show retry')
assert(find(rows,'选择条目…'),'manual subject selection must remain available')
account.subject=nil
menu.invalidate();_,rows=menu.data('bangumi')
local retry=assert(find(rows,'重新匹配'))
retry.fn()
assert(commands[#commands][4]=='retry-match','failed match retry must invoke the existing action')
account.resolving=true
menu.invalidate();_,rows=menu.data('bangumi')
assert(find(rows,'正在匹配…').spinner and not find(rows,'重新匹配'),'loading must spin without a redundant retry')
for _,state in ipairs({'not-found','error'}) do
    danmaku.autoload_state=state
    menu.invalidate();_,rows=menu.data('danmaku')
    assert(find(rows,'重试匹配'),'failed and empty danmaku results must allow retry')
    assert(find(rows,'搜索弹幕').fn,'manual search must remain available')
end
danmaku.autoload_state='loading'
menu.invalidate();_,rows=menu.data('danmaku')
assert(find(rows,'自动加载中…').spinner and find(rows,'搜索弹幕').fn,'loading must not disable manual search')
print('PASS search parsing, strict season matching, success/failure/loading menu actions')

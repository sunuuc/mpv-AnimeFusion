"""Check the maintained player sources before building the portable package."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def require(path: str, *markers: str) -> None:
    source = (ROOT / path).read_text(encoding="utf-8-sig")
    missing = [marker for marker in markers if marker not in source]
    if missing:
        raise RuntimeError(f"{path} is missing required source contracts: {missing!r}")


def require_setting(path: str, key: str, value: str) -> None:
    source = (ROOT / path).read_text(encoding="utf-8-sig")
    actual = [
        line.partition("=")[2].strip()
        for line in source.splitlines()
        if not line.lstrip().startswith("#") and line.partition("=")[0].strip() == key
    ]
    if actual != [value]:
        raise RuntimeError(f"{path} must define {key}={value!s} exactly once")


def forbid(path: str, *markers: str) -> None:
    source = (ROOT / path).read_text(encoding="utf-8-sig")
    found = [marker for marker in markers if marker in source]
    if found:
        raise RuntimeError(f"{path} contains forbidden source contracts: {found!r}")


require(
    "portable_config/script-modules/player_ui_core.lua",
    "-- Player UI layout 1.3.0:",
    "function M.layout(pw,ph,dpi,ui_scale,time_width)",
    "function M.theme(accent)",
    "function M.local_media(path,opened,network)",
)
require(
    "portable_config/scripts/player_ui.lua",
    "if playback_ready and (state.visible or state.menu~=nil) then",
    "layout.network_rate",
    "if layout.volume then",
)
require(
    "portable_config/scripts/player_ui_danmaku.lua",
    "automatic_match_all_routes(attempt)",
    "正在并行搜索全部弹幕线路…",
)
require(
    "portable_config/mpv.conf",
    "osc=yes",
    "blend-subtitles=no",
    "title-bar=yes",
    "window-maximized=no",
    "autofit-larger=1280x720",
)
require("portable_config/script-opts/osc.conf", "idlescreen=yes", "showwindowed=no", "showfullscreen=no", "windowcontrols=no")
require(
    "portable_config/script-modules/player_ui_menu.lua",
    "settings=200",
    "local data_cache,measure_cache={},{}",
    "link('弹幕设置','danmaku-settings','danmaku')",
)
menu_source = (ROOT / "portable_config/script-modules/player_ui_menu.lua").read_text(encoding="utf-8-sig")
danmaku_menu = menu_source.split("elseif kind=='danmaku' then", 1)[1].split("elseif kind=='ai' then", 1)[0]
danmaku_settings = menu_source.split("elseif kind=='danmaku-settings' then", 1)[1].split("else\n            title,a=M.info(kind)", 1)[0]
if "弹幕设置" in danmaku_menu:
    raise RuntimeError("Danmaku Settings must be reachable from the Settings menu only")
if "d.results" in danmaku_settings or "search_pending" in danmaku_settings:
    raise RuntimeError("Danmaku Settings must not show search results")
for marker in ("自动加载中…", "搜索弹幕"):
    if marker not in danmaku_menu:
        raise RuntimeError(f"Danmaku menu is missing its Hills-style state/action row: {marker!r}")
if "未找到弹幕" in danmaku_menu:
    raise RuntimeError("Danmaku menu must keep only Close in the source area after no match")
if "搜到" in danmaku_menu or "d.results" in danmaku_menu:
    raise RuntimeError("Danmaku menu must not present manual search results as auto-loaded comments")
forbid(
    "portable_config/script-modules/player_ui_menu.lua",
    "kind=='playlist'",
    "playlist='播放列表'",
    "row('弹幕设置'",
)
require(
    "portable_config/script-modules/player_ui_danmaku_online.lua",
    "function M.request_spec(url,body,options)",
    "function M.file_hash(path)",
    "function M.limit_text(value,limit,clean)",
    'fields[#fields+1]=\'"fileHash":"\'..file_hash:lower()..\'"\'',
)
require(
    "portable_config/scripts/player_ui.lua",
    "version='1.3.0'",
    "core.local_media(",
    "local right={'fullscreen','settings','danmaku','sub','audio','speed','bangumi'}",
    "local net=bool('demuxer-via-network') and o.network_speed and playing",
    "text((b.x0+b.x1)/2,b.y+1*u,14*u,core.rate(state.rate)",
    "text(0,0,22*u,os.date('%H:%M')",
    "local title,detail=core.title_lines(prop('media-title',''),prop('path',''))",
    "text(layout.margin,layout.title_y,core.metrics.title_font*u,title",
    "if layout.volume then",
    "local volume_osd=mp.create_osd_overlay('ass-events');volume_osd.z=25",
    "local fraction=core.clamp(num('volume',0)/max_volume,0,1)",
    "volume_osd.data=table.concat(output,'\\n')",
    "local last_volume=num('volume',100)",
    "if playback_ready and state.menu then draw_menu() end",
    "local function loading_indicator(w,h,u)",
    "mp.register_event('playback-restart',function()",
)
forbid(
    "portable_config/scripts/player_ui.lua",
    "local status_y=62*u",
    "text(layout.w-16*u,26*u,22*u,core.rate(state.rate)",
    "媒体标题和集数",
)
require_setting("portable_config/mpv.conf", "osc", "yes")
require("src/player/src/MpvNet/Player.cs", 'SetPropertyString("osc", "no")')
forbid("portable_config/scripts/player_ui.lua", "osc-visibility")
require("src/player/src/MpvNet.Windows/GuiCommand.cs", "ElementHost.EnableModelessKeyboardInterop(window)")
require("src/player/src/MpvNet.Windows/WPF/DanmakuSourcesWindow.xaml.cs", "ElementHost.EnableModelessKeyboardInterop(window)")
require_setting("portable_config/mpv.conf", "border", "yes")
require_setting("portable_config/mpv.conf", "title-bar", "yes")
require_setting("portable_config/mpv.conf", "fullscreen", "no")
require_setting("portable_config/mpv.conf", "window-maximized", "no")
require_setting("portable_config/mpv.conf", "osd-on-seek", "no")
for input_path in ("portable_config/input.conf", "portable_config/input-animejanai.conf"):
    input_source = (ROOT / input_path).read_text(encoding="utf-8-sig")
    for key in ("Right", "Left", "Ctrl+Right", "Ctrl+Left"):
        binding = next(
            (line for line in input_source.splitlines() if line.split()[:1] == [key]),
            None,
        )
        if binding is None or "no-osd seek" not in binding:
            raise RuntimeError(f"{input_path} must suppress mpv's duplicate seek OSD for {key}")
    # The original AnimeJaNai bindings are retained as the base config;
    # the product's active input.conf owns its themed volume popup.
    for key in (("Up", "Down") if input_path == "portable_config/input.conf" else ()):
        binding = next(
            (line for line in input_source.splitlines() if line.split()[:1] == [key]),
            None,
        )
        if binding is None or "no-osd add volume" not in binding:
            raise RuntimeError(f"{input_path} must replace mpv's native volume OSD with the themed volume popup")
for key in ("Wheel_Up", "Wheel_Down", "Wheel_Left", "Wheel_Right"):
    binding = next(
        (line for line in (ROOT / "portable_config/input.conf").read_text(encoding="utf-8-sig").splitlines()
         if line.split()[:1] == [key]),
        None,
    )
    if binding is None or "no-osd add volume" not in binding:
        raise RuntimeError(f"input.conf must suppress native volume OSD for {key}")
require(
    "src/player/src/MpvNet.Windows/WinForms/MainForm.cs",
    "bool IsFullscreen => WindowState == FormWindowState.Maximized && FormBorderStyle == FormBorderStyle.None;",
    "FormBorderStyle = Player.Border ? FormBorderStyle.Sizable : FormBorderStyle.None;",
    "bool keyboardMessage = m.Msg is 0x0100 or 0x0101 or 0x0104 or 0x0105;",
    "GetForegroundWindow() == Handle",
)
require(
    "src/player/src/MpvNet.Windows/WPF/Controls/SearchControl.xaml.cs",
    "FrameworkPropertyMetadataOptions.BindsTwoWayByDefault",
    "public void FocusInput(bool selectAll = false)",
)
require(
    "src/player/src/MpvNet.Windows/WPF/Controls/TextInputFocus.cs",
    "DispatcherPriority.Input",
    "Keyboard.Focus(input)",
)
forbid(
    "src/player/src/MpvNet.Windows/WinForms/MainForm.cs",
    "EnsureImmersiveStartupFrame",
    "_immersiveStartupFrameReady",
    "UpdateCaptionButtonRegion",
    "SetWindowRgn(MpvWindowHandle, videoRegion, true)",
)
require(
    "portable_config/scripts/player_ui_danmaku.lua",
    "private_server_config_path()",
    "if not file then file=io.open(path..'.bak','rb') end",
    "local servers=read_private_servers()",
    "match_current=function(quiet,automatic,failed_episodes)",
    "priority_fallback=true",
    "autoload_state=automatic and 'loading' or 'idle'",
    "autoload_state=attempt.automatic and (failed and 'error' or 'not-found') or 'idle'",
    "match_by_file(attempt,position,index,server)",
    "mp.observe_property('media-title','string'",
    "entry.errors[request_index]=tostring(err or why",
    "autoload_state=autoload_state",
    "for _,item in ipairs(entry.by_server[index] or {}) do found[#found+1]=item end",
    "if on_missing then on_missing()",
    "else status='该集没有弹幕';publish() end",
    "episode_load_generation=episode_load_generation+1",
)
require("portable_config/script-modules/player_ui_danmaku_render.lua",
    "DanmakuFactory.exe", "'sub-add'", "'secondary-sid'",
    "'secondary-sub-display-sync'", "'secondary-sub-render-fps'")
forbid("portable_config/script-modules/player_ui_danmaku_render.lua",
    "create_osd_overlay", "add_periodic_timer")
forbid("portable_config/scripts/player_ui_danmaku.lua",
    "create_osd_overlay", "playback_clock", "danmaku_fps", "add_periodic_timer")
require(
    "portable_config/scripts/network_playback.lua",
    "version=2,events=history",
    "cache_speed_bps=mp.get_property_number('cache-speed',0)",
    "['cache-pause-initial']='no'",
    "history[i].opening_samples=samples",
    "open_to_loaded_ms=state.open_to_loaded_ms",
    "loaded_to_playing_ms=state.loaded_to_playing_ms",
)
forbid(
    "portable_config/scripts/player_ui_danmaku.lua",
    "online.parse_servers(o.api_servers)",
    "clear_packaged_server_config",
    "local function search_episode(",
)
require("portable_config/script-opts/player_ui.conf", "ui_scale=1.00")
require(
    "portable_config/script-opts/player_ui_danmaku.conf",
    "autoload_danmaku=yes",
    "没有线路时不会联网",
)
require_setting("portable_config/script-opts/player_ui_danmaku.conf", "api_servers", "")
require_setting("portable_config/script-opts/player_ui_danmaku.conf", "dandanplay_app_id", "")
require_setting("portable_config/script-opts/player_ui_danmaku.conf", "dandanplay_app_secret", "")
require(
    "tools/standalone/build.py",
    "def source_release_files(folders):",
    "git','ls-files','-z'",
    "SOURCE_ADDITIONS={",
)
private_config_names = {"animejanai-danmaku.conf", "animejanai-danmaku.conf.tmp", "animejanai-danmaku.conf.bak"}
bundled_private_configs = [
    path.relative_to(ROOT).as_posix()
    for path in (ROOT / "portable_config").rglob("*")
    if path.is_file() and path.name.lower() in private_config_names
]
if bundled_private_configs:
    raise RuntimeError(f"Private danmaku config must stay outside release inputs: {bundled_private_configs!r}")
require(
    "portable_config/scripts/thumbfast.lua",
    "local function local_file_only()",
    "if disabled or not local_file_only() then return end",
)
require("portable_config/script-opts/thumbfast.conf", "network=no")
require(
    "portable_config/scripts/animejanai_slot.lua",
    "mp.commandv('vf-command', 'aji', 'slot', slot)",
    "if mp.get_property_native('pause') then",
    "mp.command('no-osd seek 0 exact')",
)
forbid("portable_config/scripts/animejanai_slot.lua", "if startup and desired>0 then hold_pause() end")
require(
    "src/player/src/MpvNet/Player.cs",
    'if (!file.Contains("://") && file.Contains(\'|\'))',
    'string ext = file.Contains("://") ? "" : file.Ext();',
    '"append-play" : "append"',
    "loadfile replace starts playback itself",
)
require(
    "tests/test_player_ui_runtime.lua",
    "ui().version=='1.3.0'",
)
require(
    "tests/test_network_playback.py",
    "get('version')=='1.3.0'",
)
require(
    "src/player/src/MpvNet.Windows/WPF/DanmakuSearchWindow.xaml",
    'x:Name="Status"',
    'Visibility="Collapsed"',
)
forbid(
    "src/player/src/MpvNet.Windows/WPF/DanmakuSearchWindow.xaml",
    '<ControlTemplate TargetType="TextBox">',
)
require(
    "src/player/src/MpvNet.Windows/GuiCommand.cs",
    "RestorePlayerOwner();",
    "owner.BeginInvoke(new Action(() =>",
)
require(
    "tests/test_danmaku_flow.py",
    "test_automatic_match_queries_all_routes_in_parallel_and_skips_empty_route",
    "automatic route searches were not in flight concurrently",
)
require(
    "src/player/src/MpvNet.Windows/WPF/DanmakuSearchWindow.xaml.cs",
    "Status.Visibility = Visibility.Collapsed;",
    'EmptyHeading.Text = "正在搜索作品";',
    '_failedSearches > 0',
    'EmptyHeading.Text = "请求失败";',
    'EmptyDescription.Text = Status.Text;',
)
require(
    "src/player/src/MpvNet.Windows/WPF/DanmakuSearchWindow.xaml",
    'x:Key="ResultGroupItem"',
    'ContainerStyle="{StaticResource ResultGroupItem}"',
    '<ContentPresenter />',
)
forbid(
    "src/player/src/MpvNet.Windows/WPF/DanmakuSearchWindow.xaml",
    'ContentSource="Header"',
)
forbid("portable_config/mpv.conf", "video-sync=display-resample")
forbid(
    "src/player/src/MpvNet.Windows/WPF/DanmakuSearchWindow.xaml.cs",
    "EpisodeList.IsEnabled = false;",
)

print("Player source contracts verified")

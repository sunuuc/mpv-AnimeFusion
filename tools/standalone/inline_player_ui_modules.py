"""Inline Player UI Lua modules into production scripts before packaging.

LuaJIT's loadfile/dofile path handling on Windows can fail when the portable
folder contains non-ASCII characters. mpv itself can load the top-level script,
so embedding the small local modules removes that second filesystem open while
keeping the standalone module files for source-level tests.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MODULES = ROOT / "portable_config" / "script-modules"
SCRIPTS = ROOT / "portable_config" / "scripts"


def wrapped(module_name: str) -> str:
    body = (MODULES / module_name).read_text(encoding="utf-8-sig").rstrip()
    return "(function()\n-- inlined module: " + module_name + "\n" + body + "\nend)()"


def inline_module(path: Path, module_name: str, loader: str) -> None:
    text = path.read_text(encoding="utf-8-sig")
    marker = "-- inlined module: " + module_name
    replacement = wrapped(module_name)
    if marker in text:
        if text.count(marker) != 1:
            raise RuntimeError(f"Ambiguous Player UI module inclusion: {path.name}: {module_name}")
        marker_at = text.index(marker)
        start = text.rfind("(function()", 0, marker_at)
        end = text.find("\nend)()", marker_at)
        if start < 0 or end < 0:
            raise RuntimeError(f"Malformed inlined module block: {path.name}: {module_name}")
        text = text[:start] + replacement + text[end + len("\nend)()"):]
    else:
        if loader not in text:
            raise RuntimeError(f"Player UI module inclusion context changed: {path.name}: {loader}")
        if text.count(loader) != 1:
            raise RuntimeError(f"Ambiguous Player UI module inclusion: {path.name}: {module_name}")
        text = text.replace(loader, replacement)
    path.write_text(text, encoding="utf-8")


player_ui = SCRIPTS / "player_ui.lua"
inline_module(
    SCRIPTS / "animejanaistats.lua",
    "player_ui_metrics.lua",
    "dofile(mp.command_native({'expand-path','~~/script-modules/player_ui_metrics.lua'}))",
)
inline_module(
    player_ui,
    "player_ui_core.lua",
    "dofile(mp.command_native({'expand-path','~~/script-modules/player_ui_core.lua'}))",
)
inline_module(
    player_ui,
    "player_ui_metrics.lua",
    "dofile(mp.command_native({'expand-path','~~/script-modules/player_ui_metrics.lua'}))",
)
inline_module(
    player_ui,
    "player_ui_menu.lua",
    "dofile(mp.command_native({'expand-path','~~/script-modules/player_ui_menu.lua'}))",
)

danmaku = SCRIPTS / "player_ui_danmaku.lua"
inline_module(
    danmaku,
    "player_ui_core.lua",
    "dofile(mp.command_native({'expand-path','~~/script-modules/player_ui_core.lua'}))",
)
inline_module(
    danmaku,
    "player_ui_danmaku_online.lua",
    "dofile(mp.command_native({'expand-path','~~/script-modules/player_ui_danmaku_online.lua'}))",
)

inline_module(
    danmaku,
    "player_ui_danmaku_render.lua",
    "dofile(mp.command_native({'expand-path','~~/script-modules/player_ui_danmaku_render.lua'}))",
)

for path in (player_ui, danmaku):
    text = path.read_text(encoding="utf-8")
    if "dofile(mp.command_native({'expand-path','~~/script-modules/" in text:
        raise RuntimeError(f"Runtime module loader remains in {path.name}")

print("Player UI runtime modules inlined for Unicode-safe portable paths")

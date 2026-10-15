"""Remove the playback filename row from the original mpv statistics page."""
from pathlib import Path
import sys


def apply(root):
    path = root / 'player/lua/stats.lua'
    text = path.read_text(encoding='utf-8')
    old = '''local function add_file(s, print_cache, print_tags)
    append(s, "", {prefix="File:", nl="", indent=""})
    append_property(s, "filename", {prefix_sep="", nl="", indent=""})
    if mp.get_property_osd("filename") ~= mp.get_property_osd("media-title") then
        append_property(s, "media-title", {prefix="Title:"})
    end
'''
    new = '''local function add_file(s, print_cache, print_tags)
    if mp.get_property_osd("filename") ~= mp.get_property_osd("media-title") then
        append_property(s, "media-title", {prefix="Title:", nl="", indent=""})
    end
'''
    if text.count(old) != 1:
        raise RuntimeError('Native source context mismatch: player/lua/stats.lua')
    path.write_text(text.replace(old, new), encoding='utf-8', newline='\n')


if __name__ == '__main__':
    apply(Path(sys.argv[1]).resolve())
    print('Applied statistics header change')

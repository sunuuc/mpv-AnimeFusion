"""Contracts for native secondary ASS cadence, without a Lua render loop."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]


class NativeDanmaku(unittest.TestCase):
    def test_native_second_track(self):
        render = (ROOT/'portable_config/script-modules/player_ui_danmaku_render.lua').read_text(encoding='utf-8')
        for required in ('DanmakuFactory.exe', 'secondary-sid', 'secondary-sub-render-fps'):
            self.assertIn(required, render)
        for forbidden in ('add_periodic_timer', 'create_osd_overlay'):
            self.assertNotIn(forbidden, render)

    def test_secondary_clock_does_not_modify_video_frames(self):
        patch = (ROOT/'tools/apply_secondary_sub_sync.py').read_text(encoding='utf-8')
        self.assertNotRegex(patch, r'frame->(?:current->)?pts\s*(?:=(?!=)|\+=|-=)')
        for forbidden in ('flip_page(', 'add_periodic_timer'):
            self.assertNotIn(forbidden, patch)
        for required in ('secondary_clock_pts', 'secondary_anchor_ns',
                         'elapsed * in->secondary_clock_speed', 'in->paused ? 0',
                         'in->secondary_clock_valid = false', 'secondary_next_deadline'):
            self.assertIn(required, patch)

    def test_primary_subtitles_keep_ahead_cache(self):
        patch = (ROOT/'tools/apply_secondary_sub_sync.py').read_text(encoding='utf-8')
        self.assertIn('sub->order == 1 && render_opts->secondary_sub_display_sync', patch)
        self.assertIn('in->paused', patch)

    def test_original_gpu_configuration(self):
        config = (ROOT/'portable_config/mpv.conf').read_text(encoding='utf-8')
        lines = [line.strip() for line in config.splitlines() if not line.startswith('#')]
        self.assertIn('include="~~/mpv-animejanai.conf"', lines)
        self.assertIn('profile=animejanai', lines)
        self.assertFalse(any(line.startswith(('vulkan-queue-count=', 'vulkan-async-compute=')) for line in lines))


if __name__ == '__main__':
    unittest.main()

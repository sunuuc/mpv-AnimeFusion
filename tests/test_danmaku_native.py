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

    def test_original_video_present_loop_is_preserved(self):
        patch = (ROOT/'tools/apply_secondary_sub_sync.py').read_text(encoding='utf-8')
        for forbidden in ('secondary_next_deadline', 'secondary_redraw_due',
                          'frame_queued->pts', 'wait_vo(', 'flip_page(',
                          "edit('video/out/vo_gpu_next.c'", "edit('video/out/gpu/video.c'"):
            self.assertNotIn(forbidden, patch)
        self.assertIn('frame->ideal_frame_vsync', patch)
        self.assertIn('video-sync=display-resample',
                      (ROOT/'portable_config/mpv.conf').read_text(encoding='utf-8'))

    def test_primary_subtitles_keep_ahead_cache(self):
        patch = (ROOT/'tools/apply_secondary_sub_sync.py').read_text(encoding='utf-8')
        self.assertIn('sub->order == 1 && render_opts->secondary_sub_display_sync', patch)
        self.assertIn('in->paused', patch)

    def test_vulkan_present_configuration(self):
        config = (ROOT/'portable_config/mpv.conf').read_text(encoding='utf-8')
        lines = [line.strip() for line in config.splitlines() if not line.startswith('#')]
        self.assertGreater(lines.index('vulkan-queue-count=1'), lines.index('profile=animejanai'))
        self.assertIn('vulkan-async-compute=no', lines)


if __name__ == '__main__':
    unittest.main()

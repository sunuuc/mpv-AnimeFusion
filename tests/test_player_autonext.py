"""Check playlist EOF behavior in real mpv with null video/audio outputs."""
from pathlib import Path
import os
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
MPV = Path(os.environ.get('ANIMEFUSION_TEST_MPV', r'D:\Apps\mpv-AnimeFusion\app\mpv.exe'))


class PlayerAutoNextTest(unittest.TestCase):
    def test_playlist_stops_by_default_and_advances_when_enabled(self):
        frame = b'FRAME\n' + bytes([100]) * 256 + bytes([128]) * 128
        with tempfile.TemporaryDirectory(prefix='autonext-headless-', dir=ROOT / '_probe') as path:
            config = Path(path)
            (config / 'script-opts').mkdir()
            media = []
            for i in range(2):
                clip = config / f'episode-{i + 1}.y4m'
                clip.write_bytes(b'YUV4MPEG2 W16 H16 F24:1 Ip A1:1 C420jpeg\n' + frame * 6)
                media.append(str(clip))
            check = config / 'check.lua'
            for enabled in (False, True):
                with self.subTest(auto_next=enabled):
                    # Missing option must behave as OFF, as on an existing install.
                    (config / 'script-opts/player_ui.conf').write_text('auto_next=yes\n' if enabled else '', encoding='utf-8')
                    check.write_text('''local mp=require 'mp'
mp.add_timeout(1.5,function()
    local good=mp.get_property_number('playlist-pos',-1)==EXPECTED
        and mp.get_property_bool('eof-reached',false)
        and mp.get_property_bool('pause',false)
        and mp.get_property('keep-open')=='KEEP'
    mp.msg.info(good and 'PASS automatic next EOF' or 'FAIL automatic next EOF')
    mp.commandv('quit',good and 0 or 1)
end)
'''.replace('EXPECTED', '1' if enabled else '0').replace('KEEP', 'yes' if enabled else 'always'), encoding='utf-8')
                    result = subprocess.run([
                        str(MPV), '--config-dir=' + str(config), '--load-scripts=no',
                        '--osc=no', '--vo=null', '--ao=null', '--terminal=yes',
                        '--script=' + str(ROOT / 'portable_config/scripts/player_ui.lua'),
                        '--script=' + str(check), *media,
                    ], capture_output=True, timeout=15)
                    log = result.stdout + result.stderr
                    self.assertEqual(result.returncode, 0, log.decode('utf-8', errors='replace'))
                    self.assertIn(b'PASS automatic next EOF', log)
                    self.assertNotIn(b'stack traceback', log.lower())
                    print(f'PASS headless playlist EOF: auto_next={enabled}')


if __name__ == '__main__':
    unittest.main()

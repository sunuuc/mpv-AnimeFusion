"""Keep fullscreen transitions identical to the original mpv.net handler."""
from pathlib import Path
import hashlib
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]


class PlayerFullscreenTest(unittest.TestCase):
    def test_original_window_state_transitions(self):
        # stax76/mpvnet: original CycleFullscreen, excluding whitespace
        # and the existing shutdown guard. The mouse cooldown timestamp is
        # unrelated to the original window geometry/state transitions.
        source = (ROOT / 'src/player/src/MpvNet.Windows/WinForms/MainForm.cs').read_text(encoding='utf-8-sig')
        start = source.index('    public void CycleFullscreen(')
        end = source.index('    public int GetHorizontalLocation(', start)
        handler = re.sub(r'\s+', '', source[start:end])
        handler = handler.replace('if(_shutdownStarted)return;', '')
        handler = handler.replace('_lastCycleFullscreen=Environment.TickCount;', '')
        self.assertEqual(hashlib.sha256(handler.encode()).hexdigest(),
                         '5e3c6f35d5bbd40b9e9f7878c2fd6270282926b94673456a21c0998bd1a34a42')
        self.assertIn('bool IsFullscreen => WindowState == FormWindowState.Maximized && FormBorderStyle == FormBorderStyle.None;', source)
        self.assertIn('RECT rect = Marshal.PtrToStructure<RECT>(m.LParam);', source)
        self.assertNotIn('_changingFullscreen', source)
        self.assertNotIn('_isFullscreen', source)
        self.assertNotIn('_windowedBounds', source)


if __name__ == '__main__':
    unittest.main()

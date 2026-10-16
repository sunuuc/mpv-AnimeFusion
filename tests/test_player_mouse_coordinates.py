"""Compile the real WM_MOUSEMOVE branch with a fake clock and cursor.

No window is created. This verifies coordinate forwarding, not native hit
testing or the visible fullscreen transition.
"""
from pathlib import Path
import os
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
MAIN_FORM = ROOT / 'src/player/src/MpvNet.Windows/WinForms/MainForm.cs'


def build_program(source):
    start = source.index('            case 0x200: // WM_MOUSEMOVE')
    end = source.index('            case 0x203: // WM_LBUTTONDBLCLK', start)
    mouse_move = source[start:end]
    return r'''
using System.Drawing;
using System.Globalization;
using System.Text.Json;

var results = new List<object>();
int failures = 0;
foreach (int elapsed in new[] { 0, 100, 400, 600 }) {
    var form = new TestForm();
    Environment.TickCount = 10000 + elapsed;
    Cursor.Position = new Point(850 + elapsed, 690 - elapsed);
    form.ClientOrigin = new Point(120, 80);
    Point expected = Cursor.Position - (Size)form.ClientOrigin;
    form.MouseMove();
    var command = form.Player.Commands.SingleOrDefault();
    bool passed = command != null && command.SequenceEqual(new[] {
        "mouse", expected.X.ToString(CultureInfo.InvariantCulture),
        expected.Y.ToString(CultureInfo.InvariantCulture)
    });
    passed &= form.CursorShowCount == 1;
    if (!passed) failures++;
    results.Add(new { elapsed_ms = elapsed, passed,
        expected_x = expected.X, expected_y = expected.Y, command,
        cursor_show_count = form.CursorShowCount });
}
Console.WriteLine(JsonSerializer.Serialize(new {
    scope = "headless-mouse-coordinate-forwarding", tests = results, failures
}, new JsonSerializerOptions { WriteIndented = true }));
return failures == 0 ? 0 : 1;

static class Environment { public static int TickCount; }
static class Cursor { public static Point Position; }
class FakePlayer {
    public readonly List<string[]> Commands = new();
    public void CommandVAsync(params string[] args) => Commands.Add(args);
}
class TestForm {
    public readonly FakePlayer Player = new();
    public Point ClientOrigin;
    public int CursorShowCount;
    int _lastCycleFullscreen = 10000;
    Point _lastCursorPosition = new(1, 1);
    Point PointToClient(Point point) => point - (Size)ClientOrigin;
    bool IsCursorPosDifferent(Point previous) => previous != Cursor.Position;
    void ShowCursor() => CursorShowCount++;
    public void MouseMove() {
        switch (0x200) {
''' + mouse_move + r'''
        }
    }
}
'''


def run_harness(source):
    sdk = Path(os.environ['USERPROFILE']) / '.dotnet/dotnet.exe'
    with tempfile.TemporaryDirectory(prefix='player-mouse-coordinates-') as path:
        fixture = Path(path)
        project = fixture / 'MouseCoordinates.csproj'
        project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>')
        (fixture / 'Program.cs').write_text(build_program(source), encoding='utf-8')
        return subprocess.run([str(sdk), 'run', '--project', str(project), '-c', 'Release'],
                              capture_output=True, text=True, encoding='utf-8', timeout=60)


class PlayerMouseCoordinatesTest(unittest.TestCase):
    def test_new_coordinates_are_forwarded_immediately_after_fullscreen(self):
        result = run_harness(MAIN_FORM.read_text(encoding='utf-8-sig'))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        print(result.stdout.strip())


if __name__ == '__main__':
    unittest.main()

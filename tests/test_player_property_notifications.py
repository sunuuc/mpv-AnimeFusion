"""Run the real progress callback without waiting for the native player."""
from pathlib import Path
import os
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class PlayerPropertyNotificationsTest(unittest.TestCase):
    def test_progress_never_waits_for_native_properties(self):
        source = (ROOT / 'src/player/src/MpvNet.Windows/WinForms/MainForm.cs').read_text(encoding='utf-8-sig')
        self.assertIn('ObservePropertyDouble("time-pos", value => Volatile.Write(ref _playbackPosition, value))', source)
        progress = source[source.index('    void UpdateProgressBar()'):source.index('    void PropChangeOnTop(')]
        program = r'''
using System.Diagnostics;
using System.Threading;
var form = new TestForm();
form.Position(42.5); form.Progress();
if (form.PositionShown != 42.5) throw new Exception("notification not displayed");
form.Position(3); form.Progress();
if (form.PositionShown != 3) throw new Exception("backward seek not displayed");
var clock = Stopwatch.StartNew();
for (int i = 0; i < 20000; i++) form.Progress();
if (clock.ElapsedMilliseconds > 1000) throw new Exception("progress callback blocked");
form.Stop(); form.Position(8); form.Progress();
if (form.PositionShown != 3)
    throw new Exception("callback used a closing window");
Console.WriteLine($"PASS progress, seek, shutdown; 20000 updates {clock.ElapsedMilliseconds} ms; zero native queries");
class FakePlayer {
    public bool TaskbarProgress = true;
    public TimeSpan Duration = TimeSpan.FromMinutes(24);
    public double GetPropertyDouble(string name, bool errors) => throw new Exception("native core is blocked");
    public bool GetPropertyBool(string name) => throw new Exception("native core is blocked");
    public void SetPropertyBool(string name, bool value) => throw new Exception("native core is blocked");
}
class Taskbar { public double Position; public void SetValue(double position, double duration) => Position = position; }
class TestForm {
    public static readonly FakePlayer Player = new();
    Taskbar? _taskbar = new();
    double _playbackPosition;
    bool _shutdownStarted;
    public void Position(double value) => Volatile.Write(ref _playbackPosition, value);
    public void Progress() => UpdateProgressBar();
    public double PositionShown => _taskbar!.Position;
    public void Stop() => _shutdownStarted = true;
''' + progress + '\n}\n'
        sdk = Path(os.environ['USERPROFILE']) / '.dotnet/dotnet.exe'
        with tempfile.TemporaryDirectory(prefix='player-notifications-') as path:
            fixture = Path(path)
            project = fixture / 'Notifications.csproj'
            project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>')
            (fixture / 'Program.cs').write_text(program, encoding='utf-8')
            result = subprocess.run([str(sdk), 'run', '--project', str(project), '-c', 'Release'], capture_output=True, text=True, encoding='utf-8', timeout=60)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            print(result.stdout.strip())


if __name__ == '__main__':
    unittest.main()

"""Run the real window callbacks without opening a desktop window."""
from pathlib import Path
import os
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class PlayerPropertyNotificationsTest(unittest.TestCase):
    def test_progress_and_window_state_never_wait_for_native_properties(self):
        source = (ROOT / 'src/player/src/MpvNet.Windows/WinForms/MainForm.cs').read_text(encoding='utf-8-sig')
        self.assertIn('ObservePropertyDouble("time-pos", PropChangeTimePosition)', source)
        self.assertIn('ObservePropertyBool("window-maximized", PropChangeWindowMaximized)', source)
        self.assertIn('ObservePropertyBool("window-minimized", PropChangeWindowMinimized)', source)
        progress = source[source.index('    void UpdateProgressBar()'):source.index('    void PropChangeOnTop(')]
        windows = source[source.index('    void PropChangeWindowMaximized('):source.index('    void PropChangeCursorAutohide(')]
        resize = source[source.index('    protected override void OnResize('):source.index('    protected override async void OnFormClosing(')]
        program = r'''
using System.Collections.Concurrent;
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
form.Maximized(true); form.Minimized(false); form.Pump();
if (form.WindowState != FormWindowState.Maximized) throw new Exception("maximized flag lost");
form.Maximized(false); form.Minimized(true); form.Pump();
if (form.WindowState != FormWindowState.Minimized) throw new Exception("minimized flag lost");
form.Minimized(false); form.Pump();
if (form.WindowState != FormWindowState.Normal) throw new Exception("restore flag lost");
form.Resize();
if (TestForm.Player.Commands.Count != 2) throw new Exception("restore not sent asynchronously");
form.WindowState = FormWindowState.Maximized; form.Resize();
form.WindowState = FormWindowState.Minimized; form.Resize();
form.WindowState = FormWindowState.Normal;
if (TestForm.Player.Commands.Count != 4) throw new Exception("resize command missing");
form.Maximized(true); form.Stop(); form.Pump(); form.Position(8); form.Progress();
if (form.WindowState != FormWindowState.Normal || form.PositionShown != 3)
    throw new Exception("callback used a closing window");
Console.WriteLine($"PASS progress, seek, window state, shutdown; 20000 updates {clock.ElapsedMilliseconds} ms; zero native queries");
enum FormWindowState { Normal, Maximized, Minimized }
enum FormBorderStyle { None, Sizable }
class Form { protected virtual void OnResize(EventArgs e) { } }
class FakePlayer {
    public bool TaskbarProgress = true, WindowMaximized, WindowMinimized;
    public TimeSpan Duration = TimeSpan.FromMinutes(24);
    public double GetPropertyDouble(string name, bool errors) => throw new Exception("native core is blocked");
    public bool GetPropertyBool(string name) => throw new Exception("native core is blocked");
    public void SetPropertyBool(string name, bool value) => throw new Exception("native core is blocked");
    public readonly List<string> Commands = new();
    public void CommandVAsync(params string[] args) => Commands.Add(string.Join(" ", args));
}
class Taskbar { public double Position; public void SetValue(double position, double duration) => Position = position; }
class TestForm : Form {
    public static readonly FakePlayer Player = new();
    Taskbar? _taskbar = new();
    double _timePosition;
    bool _shutdownStarted;
    bool WasShown = true;
    bool _wasMaximized;
    FormBorderStyle FormBorderStyle = FormBorderStyle.Sizable;
    void SaveWindowProperties() { }
    public void Resize() => OnResize(EventArgs.Empty);
    readonly ConcurrentQueue<Action> pending = new();
    public FormWindowState WindowState;
    void BeginInvoke(Action callback) => pending.Enqueue(callback);
    public void Pump() { while (pending.TryDequeue(out var callback)) callback(); }
    public void Position(double value) => PropChangeTimePosition(value);
    public void Progress() => UpdateProgressBar();
    public double PositionShown => _taskbar!.Position;
    public void Maximized(bool value) => PropChangeWindowMaximized(value);
    public void Minimized(bool value) => PropChangeWindowMinimized(value);
    public void Stop() => _shutdownStarted = true;
''' + progress + windows + resize + '\n}\n'
        sdk = Path(os.environ['USERPROFILE']) / '.dotnet/dotnet.exe'
        with tempfile.TemporaryDirectory(prefix='player-notifications-', dir=ROOT / '_probe') as path:
            fixture = Path(path)
            project = fixture / 'Notifications.csproj'
            project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>')
            (fixture / 'Program.cs').write_text(program, encoding='utf-8')
            result = subprocess.run([str(sdk), 'run', '--project', str(project), '-c', 'Release'], capture_output=True, text=True, encoding='utf-8', timeout=60)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            print(result.stdout.strip())


if __name__ == '__main__':
    unittest.main()

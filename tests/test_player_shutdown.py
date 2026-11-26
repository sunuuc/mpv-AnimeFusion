"""Exercise the actual close handler without opening a desktop window."""
from pathlib import Path
import os
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class PlayerShutdownTest(unittest.TestCase):
    def test_close_keeps_message_pump_alive_and_destroys_once(self):
        source = (ROOT / 'src/player/src/MpvNet.Windows/WinForms/MainForm.cs').read_text(encoding='utf-8-sig')
        handler = source[source.index('    protected override async void OnFormClosing('):source.index('    internal bool TryCloseAfterUnhandledException(')]
        program = r'''
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
var ui = new Pump();
SynchronizationContext.SetSynchronizationContext(ui);
var form = new TestForm();
_ = TestForm.Player;
form.RequestClose();
if (!form.Cancelled || TestForm.Player.DestroyCalls != 0)
    throw new Exception("first close must return before teardown completes");
form.RequestClose();
if (TaskDialog.Prompts != 0) throw new Exception("ordinary close prompted before timeout");
System.Windows.Forms.Timer.Last!.Fire();
if (System.Windows.Forms.Timer.Last.Interval != 10000 || TaskDialog.Prompts != 1 || Process.Kills != 0)
    throw new Exception("timeout must offer to continue waiting without forcing exit");
TaskDialog.ChooseForce = true;
form.RequestClose();
if (TaskDialog.Prompts != 2 || Process.Kills != 1) throw new Exception("only an explicitly chosen force exit may terminate the process");
var until = DateTime.UtcNow.AddSeconds(5);
while (!form.Closed && DateTime.UtcNow < until) { ui.Step(); Thread.Sleep(1); }
if (!form.Closed || TestForm.Player.DestroyCalls != 1 || TestForm.Player.QuitCalls != 1)
    throw new Exception("close did not complete exactly once through the UI pump");
Console.WriteLine("PASS nonblocking close, reentrant close, shutdown delivery, single destruction");
class Pump : SynchronizationContext {
    readonly ConcurrentQueue<Action> queue = new();
    public override void Post(SendOrPostCallback cb, object? state) => queue.Enqueue(() => cb(state));
    public void Step() { if (queue.TryDequeue(out var cb)) cb(); }
}
class FormClosingEventArgs { public bool Cancel; }
class Form {
    protected virtual void OnFormClosing(FormClosingEventArgs e) { }
}
class FakePlayer {
    public bool IsQuitNeeded = true;
    public AutoResetEvent ShutdownAutoResetEvent = new(false);
    public int QuitCalls, DestroyCalls;
    readonly int uiThread = Environment.CurrentManagedThreadId;
    readonly SynchronizationContext ui = SynchronizationContext.Current!;
    public void CommandVAsync(string command) {
        QuitCalls++;
        ui.Post(_ => { IsQuitNeeded = false; ShutdownAutoResetEvent.Set(); }, null);
    }
    public void Destroy() {
        if (Environment.CurrentManagedThreadId == uiThread)
            throw new Exception("native destruction must not block the window message pump");
        DestroyCalls++;
    }
}
class FakeTimer { public void Stop() { } }
class Application { public static string ProductName => "player"; }
class Process {
    public static int Kills;
    public static Process GetCurrentProcess() => new();
    public void Kill() => Kills++;
}
class TaskDialogButton(string title) { public string Title => title; }
class TaskDialogPage {
    public string Caption = "", Heading = "", Text = "";
    public List<TaskDialogButton> Buttons { get; } = new();
    public TaskDialogButton? DefaultButton;
    public bool AllowCancel;
    public TaskDialog? BoundDialog;
}
class TaskDialog {
    public static bool ChooseForce; public static int Prompts;
    public static TaskDialogButton ShowDialog(Form owner, TaskDialogPage page) {
        Prompts++;
        if (page.DefaultButton?.Title != "继续等待" || !page.AllowCancel) throw new Exception("dialog default must be safe");
        return ChooseForce ? page.Buttons[1] : page.Buttons[0];
    }
    public void Close() { }
}
class Msg { public static void ShowException(Exception ex) => throw ex; }
class TestForm : Form {
    public static readonly FakePlayer Player = new();
    bool _shutdownStarted, _shutdownCompleted, _shutdownDelayed;
    TaskDialogPage? _shutdownPrompt;
    FakeTimer CursorTimer = new(), ProgressTimer = new();
    public bool Closed, Cancelled;
    public void RequestClose() {
        var e = new FormClosingEventArgs(); OnFormClosing(e);
        Cancelled = e.Cancel; if (!e.Cancel) Closed = true;
    }
    void Close() => RequestClose();
''' + handler + r'''
}
namespace System.Windows.Forms {
    class Timer : IDisposable {
        public static Timer? Last;
        public int Interval;
        public event EventHandler? Tick;
        public void Start() => Last = this;
        public void Fire() => Tick?.Invoke(this, EventArgs.Empty);
        public void Stop() { }
        public void Dispose() { }
    }
}
'''
        sdk = Path(os.environ.get('USERPROFILE', '')) / '.dotnet/dotnet.exe'
        with tempfile.TemporaryDirectory(prefix='animefusion-shutdown-') as path:
            fixture = Path(path)
            (fixture / 'Shutdown.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>')
            (fixture / 'Program.cs').write_text(program, encoding='utf-8')
            result = subprocess.run([str(sdk), 'run', '--project', str(fixture / 'Shutdown.csproj'), '-c', 'Release'], capture_output=True, text=True, timeout=60)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            print(result.stdout.strip())


if __name__ == '__main__':
    unittest.main()

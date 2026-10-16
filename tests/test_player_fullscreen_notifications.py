"""Exercise actual fullscreen callbacks with controlled queues, without WinForms.

This checks notification ordering only. The fake form does not validate Win32
geometry, rendering, mouse input, or native thread liveness.
"""
from pathlib import Path
import os
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
MAIN_FORM = ROOT / 'src/player/src/MpvNet.Windows/WinForms/MainForm.cs'


def build_program(source):
    def methods(start, end):
        first = source.index(start)
        return source[first:source.index(end, first)]

    fullscreen = methods('    public void CycleFullscreen(',
                         '    public int GetHorizontalLocation(')
    notifications = methods('    void PropChangeWindowMaximized(',
                            '    void PropChangeCursorAutohide(')
    resize = methods('    protected override void OnResize(',
                     '    protected override async void OnFormClosing(')
    # The baseline can use the identical harness without rewriting its handlers.
    max_argument = 'value' if 'PropChangeWindowMaximized(bool value)' in notifications else ''
    min_argument = 'value' if 'PropChangeWindowMinimized(bool value)' in notifications else ''
    return r'''
using System.Drawing;
using System.Text.Json;

var results = new List<object>();
int failures = 0;
void Check(string name, Action test) {
    try { test(); results.Add(new { name, passed = true }); }
    catch (Exception e) {
        failures++;
        results.Add(new { name, passed = false, error = e.Message });
    }
}
void Expect(bool condition, string message, TestForm form) {
    if (!condition) throw new Exception(message + "; " + form.Describe());
}
void Full(TestForm form, bool enabled) {
    form.CycleFullscreen(enabled);
    // Finish native writes before delivering notifications. Delay only the UI
    // callbacks: this reproduces the race even without delaying native writes.
    form.Player.DrainWrites();
}
void PumpExpected(TestForm form, bool fullscreen) {
    while (form.PumpOne())
        Expect(form.Player.Fullscreen == fullscreen && form.IsFull == fullscreen,
               "queued notification changed the requested fullscreen geometry", form);
}

Check("stale-maximized-false-before-current-true", () => {
    var form = new TestForm();
    Full(form, true);
    form.NotifyMax(false);
    form.NotifyMax(true);
    Expect(form.Player.NativeMaximized, "fixture must already have latest native value", form);
    PumpExpected(form, true);
});
Check("stale-maximized-true-after-fullscreen-exit", () => {
    var form = new TestForm();
    Full(form, true);
    form.NotifyMax(true);
    Full(form, false);
    Expect(!form.Player.NativeMaximized, "native restore must precede delayed callback", form);
    form.Pump();
    Expect(form.WindowState == FormWindowState.Normal && !form.IsFull,
           "old maximized notification enlarged the restored window", form);
});
Check("stale-minimized-true-before-current-false", () => {
    var form = new TestForm();
    Full(form, true);
    form.NotifyMin(true);
    form.NotifyMin(false);
    PumpExpected(form, true);
});
Check("rapid-fullscreen-and-windowed-notification-bursts", () => {
    var form = new TestForm();
    for (int i = 0; i < 100; i++) {
        Full(form, true);
        form.NotifyMax(false); form.NotifyMin(true);
        form.NotifyMax(true); form.NotifyMin(false);
        PumpExpected(form, true);
        Full(form, false);
        form.NotifyMax(true); form.NotifyMax(false);
        form.NotifyMin(true); form.NotifyMin(false);
        PumpExpected(form, false);
        Expect(form.WindowState == FormWindowState.Normal,
               "rapid transition did not return to normal window", form);
    }
});
Check("current-maximize-minimize-restore", () => {
    var form = new TestForm();
    form.NotifyMax(true); form.Pump();
    Expect(form.WindowState == FormWindowState.Maximized, "maximize ignored", form);
    form.Player.DrainWrites();
    form.NotifyMax(false); form.Pump();
    Expect(form.WindowState == FormWindowState.Normal, "restore ignored", form);
    form.Player.DrainWrites();
    form.NotifyMin(true); form.Pump();
    Expect(form.WindowState == FormWindowState.Minimized, "minimize ignored", form);
    form.Player.DrainWrites();
    form.NotifyMin(false); form.Pump();
    Expect(form.WindowState == FormWindowState.Normal, "unminimize ignored", form);
});
Check("resize-publishes-current-native-state-before-return", () => {
    var form = new TestForm();
    form.WindowState = FormWindowState.Maximized;
    Expect(form.Player.NativeMaximized, "maximize write is still pending", form);
    form.WindowState = FormWindowState.Normal;
    Expect(!form.Player.NativeMaximized && !form.Player.NativeMinimized,
           "normal write is still pending", form);
    form.WindowState = FormWindowState.Minimized;
    Expect(form.Player.NativeMinimized, "minimize write is still pending", form);
    Expect(form.Player.PendingWrites == 0, "resize accumulated native write backlog", form);
});
Check("fullscreen-restores-previous-maximized-window", () => {
    var form = new TestForm();
    form.WindowState = FormWindowState.Maximized;
    Full(form, true);
    Expect(form.IsFull, "fullscreen entry failed", form);
    Full(form, false);
    Expect(form.WindowState == FormWindowState.Maximized && !form.IsFull,
           "previous maximized state was not restored", form);
});
Check("repeated-fullscreen-is-idempotent", () => {
    var form = new TestForm();
    Full(form, true);
    int resizeCount = form.ResizeCount;
    for (int i = 0; i < 100; i++) form.CycleFullscreen(true);
    Expect(form.IsFull && form.ResizeCount == resizeCount,
           "repeated fullscreen changed geometry", form);
});
Check("pending-callbacks-do-not-access-closing-window", () => {
    var form = new TestForm();
    form.NotifyMax(true); form.NotifyMin(true);
    int reads = form.Player.Reads;
    form.Stop(); form.Pump(); form.CycleFullscreen(true);
    Expect(form.WindowState == FormWindowState.Normal && !form.Player.Fullscreen,
           "callback changed closing window", form);
    Expect(form.Player.Reads == reads, "closing callback queried native player", form);
});
Check("notifications-before-show-are-ignored", () => {
    var form = new TestForm { WasShown = false };
    form.NotifyMax(true); form.NotifyMin(true);
    Expect(!form.PumpOne() && form.WindowState == FormWindowState.Normal,
           "pre-show notification queued a window change", form);
});
Console.WriteLine(JsonSerializer.Serialize(new {
    scope = "headless-notification-ordering", tests = results, failures
}, new JsonSerializerOptions { WriteIndented = true }));
return failures == 0 ? 0 : 1;

enum FormWindowState { Normal, Maximized, Minimized }
enum FormBorderStyle { None, Sizable }
class Form {
    FormWindowState state;
    public int ResizeCount;
    public FormWindowState WindowState {
        get => state;
        set {
            if (state == value) return;
            state = value; ResizeCount++; OnResize(EventArgs.Empty);
        }
    }
    public FormBorderStyle FormBorderStyle = FormBorderStyle.Sizable;
    protected virtual void OnResize(EventArgs e) { }
}
class Screen {
    public Rectangle Bounds = new(0, 0, 2560, 1600);
    public static Screen FromControl(Form form) => new();
}
class FakePlayer {
    public bool Fullscreen, WindowMaximized, WindowMinimized;
    public bool WasInitialSizeSet = true, Border = true;
    public bool NativeMaximized, NativeMinimized;
    public int Reads;
    readonly Queue<Action> writes = new();
    public int PendingWrites => writes.Count;
    public bool GetPropertyBool(string name) {
        Reads++;
        return name switch {
            "window-maximized" => NativeMaximized,
            "window-minimized" => NativeMinimized,
            _ => throw new Exception("unexpected native query: " + name)
        };
    }
    public void SetPropertyBool(string name, bool value) {
        if (name == "window-maximized") NativeMaximized = value;
        else if (name == "window-minimized") NativeMinimized = value;
        else throw new Exception("unexpected native write: " + name);
    }
    public void CommandVAsync(params string[] args) {
        if (args.Length != 3 || args[0] != "set") throw new Exception("unexpected command");
        writes.Enqueue(() => SetPropertyBool(args[1], args[2] == "yes"));
    }
    public void DrainWrites() { while (writes.TryDequeue(out var write)) write(); }
}
class TestForm : Form {
    public readonly FakePlayer Player = new();
    public bool WasShown = true;
    bool _shutdownStarted, _wasMaximized;
    int _lastCycleFullscreen;
    IntPtr Handle = IntPtr.Zero;
    readonly Queue<Action> pending = new();
    public bool IsFull => WindowState == FormWindowState.Maximized && FormBorderStyle == FormBorderStyle.None;
    void BeginInvoke(Action callback) => pending.Enqueue(callback);
    public bool PumpOne() {
        if (!pending.TryDequeue(out var callback)) return false;
        callback(); return true;
    }
    public void Pump() { while (PumpOne()) { } }
    public void Stop() => _shutdownStarted = true;
    public string Describe() => $"state={WindowState}, border={FormBorderStyle}, fullscreen={Player.Fullscreen}, native-max={Player.NativeMaximized}, native-min={Player.NativeMinimized}";
    void SaveWindowProperties() { }
    bool KeepSize() => true;
    void SetFormPosAndSize() { }
    static void SetWindowPos(IntPtr handle, IntPtr top, int x, int y, int width, int height, uint flags) { }
''' + f'''
    public void NotifyMax(bool value) {{
        Player.NativeMaximized = value;
        PropChangeWindowMaximized({max_argument});
    }}
    public void NotifyMin(bool value) {{
        Player.NativeMinimized = value;
        PropChangeWindowMinimized({min_argument});
    }}
''' + fullscreen + notifications + resize + '\n}\n'


def run_harness(source):
    sdk = Path(os.environ['USERPROFILE']) / '.dotnet/dotnet.exe'
    with tempfile.TemporaryDirectory(prefix='player-fullscreen-') as path:
        fixture = Path(path)
        project = fixture / 'Fullscreen.csproj'
        project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>')
        (fixture / 'Program.cs').write_text(build_program(source), encoding='utf-8')
        return subprocess.run([str(sdk), 'run', '--project', str(project), '-c', 'Release'],
                              capture_output=True, text=True, encoding='utf-8', timeout=60)


class PlayerFullscreenNotificationsTest(unittest.TestCase):
    def test_delayed_notifications_and_rapid_fullscreen_transitions(self):
        result = run_harness(MAIN_FORM.read_text(encoding='utf-8-sig'))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        print(result.stdout.strip())


if __name__ == '__main__':
    unittest.main()

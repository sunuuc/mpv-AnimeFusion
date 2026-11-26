"""Exercise the input handlers with a simulated cursor and Win32 message-only windows."""
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
    start = source.index('            case 0x0007: // WM_SETFOCUS')
    end = source.index('            case 0x001A: // WM_SETTINGCHANGE', start)
    forward_input = source[start:end]
    return r'''
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using static Native;

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
// HWND_MESSAGE is outside the desktop window hierarchy and never takes focus.
using var windows = new VideoWindows();
var host = new TestForm { Handle = windows.Host, Foreground = windows.Host };
host.Input(0x201);
nint first = windows.AddVideo();
host.Input(0x201);
if (!DestroyWindow(first) || IsWindow(first)) throw new InvalidOperationException("old window");
host.Input(0x202);
nint next = windows.AddVideo();
if (first == next || IsWindowVisible(next)) throw new InvalidOperationException("new window");
host.Input(0x100);
host.Input(0x101);
host.Input(0x201);
host.Input(0x202);
host.Foreground = 0;                    // The player is no longer foreground.
host.Input(0x100);
host.Input(0x101);
host.Input(0x8);                        // mpv must still see the loss of focus.
host.Foreground = host.Handle;
host.ContextMenu.IsOpen = true;
host.Input(0x100, (int)Keys.Escape);
bool delivered = !host.ContextMenu.IsOpen && windows.Received.SequenceEqual(new (nint, int)[] {
    (first, 0x201), (next, 0x100), (next, 0x101), (next, 0x201), (next, 0x202), (next, 0x8)
});
if (!delivered) failures++;
results.Add(new { case_name = "input-after-video-window-recreation", passed = delivered });
Console.WriteLine(JsonSerializer.Serialize(new {
    scope = "headless-player-input-forwarding", tests = results, failures
}, new JsonSerializerOptions { WriteIndented = true }));
return failures == 0 ? 0 : 1;

static class Environment { public static int TickCount; }
static class Cursor { public static Point Position; }
enum Keys { Escape = 27, KeyCode = 65535 }
class Menu { public bool IsOpen; }
class Message { public int Msg; public nint WParam, LParam, Result; }
static class Native {
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate nint WindowProc(nint window, int msg, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WindowClass {
        public uint style;
        public WindowProc proc;
        public int classExtra, windowExtra;
        public nint instance, icon, cursor, background, menuName;
        public string className;
    }
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandle(string? name);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern ushort RegisterClass(ref WindowClass wc);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint CreateWindowEx(
        uint exStyle, string cls, string name, uint style, int x, int y, int w, int h,
        nint parent, nint menu, nint instance, nint param);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint DefWindowProc(nint window, int msg, nint wParam, nint lParam);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint FindWindowEx(nint parent, nint after, string cls, string? name);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern nint SendMessage(nint window, int msg, nint wParam, nint lParam);
    [DllImport("user32")] public static extern bool DestroyWindow(nint window);
    [DllImport("user32")] public static extern bool IsWindow(nint window);
    [DllImport("user32")] public static extern bool IsWindowVisible(nint window);
}
sealed class VideoWindows : IDisposable {
    public readonly nint Host;
    public readonly List<(nint, int)> Received = new();
    readonly nint instance = GetModuleHandle(null);
    readonly WindowProc proc;
    public VideoWindows() {
        proc = Receive;
        var wc = new WindowClass { proc = proc, className = "mpv", instance = instance };
        if (RegisterClass(ref wc) == 0) throw new InvalidOperationException("mpv test class");
        Host = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, -3, 0, instance, 0);
        if (Host == 0 || IsWindowVisible(Host)) throw new InvalidOperationException("message-only host");
    }
    public nint AddVideo() {
        const uint wsChild = 0x40000000, wsDisabled = 0x08000000;
        nint child = CreateWindowEx(0, "mpv", "", wsChild | wsDisabled, 0, 0, 0, 0, Host, 0, instance, 0);
        if (child == 0) throw new InvalidOperationException("mpv child");
        return child;
    }
    nint Receive(nint window, int msg, nint wParam, nint lParam) {
        if (msg is 0x100 or 0x101 or 0x201 or 0x202 or 0x8) {
            Received.Add((window, msg));
            return 0;
        }
        return DefWindowProc(window, msg, wParam, lParam);
    }
    public void Dispose() {
        if (!DestroyWindow(Host) || !UnregisterClass("mpv", instance))
            throw new InvalidOperationException("window cleanup");
        GC.KeepAlive(proc);
    }
}
class FakePlayer {
    public readonly List<string[]> Commands = new();
    public void CommandVAsync(params string[] args) => Commands.Add(args);
}
class TestForm {
    public nint Handle, Foreground;
    public readonly Menu ContextMenu = new();
    bool _contextMenuIsReady = true;
    nint GetForegroundWindow() => Foreground;
    public void Input(int msg, int key = 32) {
        var m = new Message { Msg = msg, WParam = key, LParam = 0 };
        switch (msg) {
''' + forward_input + r'''
        }
    }
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
        return subprocess.run([str(sdk), 'run', '--disable-build-servers', '--project', str(project), '-c', 'Release'],
                              capture_output=True, text=True, encoding='utf-8', timeout=60)


class PlayerMouseCoordinatesTest(unittest.TestCase):
    def test_coordinates_and_input_follow_current_video_window(self):
        result = run_harness(MAIN_FORM.read_text(encoding='utf-8-sig'))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        print(result.stdout.strip())


if __name__ == '__main__':
    unittest.main()

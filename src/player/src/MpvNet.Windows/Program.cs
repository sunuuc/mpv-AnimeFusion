using System.Windows.Forms;
using System.Threading;

using MpvNet.Windows.Native;
using MpvNet.Help;
using MpvNet.Windows.UI;
using MpvNet.Windows.Help;
using MpvNet.Windows.WPF;

namespace MpvNet.Windows;

static class Program
{
    internal static string? InstanceWindowProperty { get; private set; }

    [STAThread]
    static void Main()
    {
        using var installMutex = new Mutex(false, "mpv-AnimeFusion_Player");
        try
        {
            RegistryHelp.ProductName = AppInfo.Product;
            Translator.Current = new WpfTranslator();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            AppDomain.CurrentDomain.UnhandledException += (sender, e) => Terminal.WriteError(e.ExceptionObject);
            Application.ThreadException += (sender, e) => Terminal.WriteError(e.Exception);

            if (App.IsTerminalAttached)
                WinApi.AttachConsole(-1 /*ATTACH_PARENT_PROCESS*/);

            string[] args = Environment.GetCommandLineArgs().Skip(1).ToArray();

            if (args.Length > 0 && args[0] == "--register-file-associations")
            {
                if (args.Length < 2) throw new ArgumentException("缺少文件关联参数。");
                FileAssociation.Register(args[1], args.Skip(1).ToArray());
                return;
            }

            StartupDiagnostics.Begin();
            App.Init();
            Theme.Init();

            if (Control.ModifierKeys == Keys.Shift ||
                App.CommandLine.Contains("--process-instance=multi") ||
                App.CommandLine.Contains("--o=") ||
                CommandLine.Parsed.NeedsDedicatedProcess)
            {
                App.ProcessInstance = "multi";
                if (CommandLine.Parsed.HasGroups)
                    App.AutoLoadFolder = false;
            }

            bool first = true;
            if (App.ProcessInstance is "single" or "queue")
                InstanceWindowProperty = "mpv-AnimeFusion-" + StringHelp.GetMD5Hash(App.ConfPath);
            using var mutex = InstanceWindowProperty == null ? null : new Mutex(false, InstanceWindowProperty, out first);
            bool commandOnly = CommandLine.Parsed.GlobalOptions.Any(option => option.Name == "command");
            if (!first && (ForwardToExistingInstance(InstanceWindowProperty!) || commandOnly))
            {
                if (App.IsTerminalAttached)
                    WinApi.FreeConsole();
                return;
            }

            if (ProcessCommandLineArguments())
                Environment.GetCommandLineArgs();
            else if (App.CommandLine.Contains("--o="))
            {
                App.AutoLoadFolder = false;
                Player.Init(IntPtr.Zero, true);
                CommandLine.ProcessCommandLineArgsPostInit();
                CommandLine.ProcessCommandLineFiles();
                Player.SetPropertyString("idle", "no");
                Player.EventLoop();
                Player.Destroy();
            }
            else
                Application.Run(new WinForms.MainForm());

            if (App.IsTerminalAttached)
                WinApi.FreeConsole();

        }
        catch (Exception ex)
        {
            StartupDiagnostics.Failed(ex);
            Terminal.WriteError(ex);
        }
    }

    static bool ForwardToExistingInstance(string windowProperty)
    {
        var parsed = CommandLine.Parsed;
        var command = parsed.GlobalOptions.LastOrDefault(option => option.Name == "command");
        var queue = parsed.GlobalOptions.LastOrDefault(option => option.Name == "queue");
        var mode = queue == null ? App.ProcessInstance : queue.Value == "yes" ? "queue" : "single";
        var payload = command == null
            ? new[] { mode }.Concat(parsed.Entries.Select(entry => entry.Path.Contains("://")
                ? entry.Path : Path.GetFullPath(entry.Path)))
            : new[] { "command", command.Value };
        var data = new WinApi.CopyDataStruct { lpData = string.Join("\n", payload) };
        data.cbData = (data.lpData.Length + 1) * sizeof(char);

        // The configured instance may still be creating its window.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            IntPtr window = IntPtr.Zero;
            bool found = false;
            while ((window = WinApi.FindWindowEx(IntPtr.Zero, window, null, null)) != IntPtr.Zero)
            {
                if (WinApi.GetProp(window, windowProperty) == IntPtr.Zero)
                    continue;

                found = true;
                WinApi.GetWindowThreadProcessId(window, out int processId);
                WinApi.AllowSetForegroundWindow(processId);
                const uint abortIfHung = 0x0002, errorOnExit = 0x0020;
                if (WinApi.SendMessageTimeout(window, 0x004A, IntPtr.Zero, ref data,
                    abortIfHung | errorOnExit, 2000, out var accepted) != IntPtr.Zero && accepted != IntPtr.Zero)
                    return true;
            }
            if (found)
                break;
            Thread.Sleep(50);
        }
        if (command != null)
        {
            Terminal.WriteError("播放器没有接收命令。");
            Environment.ExitCode = 1;
        }
        return false;
    }

    static bool ProcessCommandLineArguments()
    {
        foreach (string arg in Environment.GetCommandLineArgs().Skip(1))
        {
            if (arg == "--profile=help")
            {
                Player.Init(IntPtr.Zero, false);
                Console.WriteLine(Player.GetProfiles());
                Player.Destroy();
                return true;
            }
            else if (arg == "--vd=help" || arg == "--ad=help")
            {
                Player.Init(IntPtr.Zero, false);
                Console.WriteLine(Player.GetDecoders());
                Player.Destroy();
                return true;
            }
            else if (arg == "--audio-device=help")
            {
                Player.Init(IntPtr.Zero, false);
                Console.WriteLine(Player.GetPropertyOsdString("audio-device-list"));
                Player.Destroy();
                return true;
            }
            else if (arg == "--input-keylist")
            {
                Player.Init(IntPtr.Zero, false);
                Console.WriteLine(Player.GetPropertyString("input-key-list").Replace(",", BR));
                Player.Destroy();
                return true;
            }
            else if (arg == "--version")
            {
                Player.Init(IntPtr.Zero, false);
                Console.WriteLine(AppClass.About);
                Player.Destroy();
                return true;
            }
        }

        return false;
    }
}

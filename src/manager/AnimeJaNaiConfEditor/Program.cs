using Avalonia;
using Avalonia.Threading;
using ReactiveUI.Avalonia;
using System;
using System.IO.Pipes;
using System.Threading;

namespace AnimeJaNaiConfEditor
{
    internal class Program
    {
        // Shared id for both the single-instance mutex and the activation pipe. Mutex and named
        // pipes are cross-platform in .NET (pipes are Unix domain sockets on Linux/macOS), so this
        // works without any OS-specific code.
        private const string SingleInstanceId = "mpv-AnimeFusionManager_SingleInstance";

        // Held in a static field for the process lifetime so the mutex stays owned until exit.
        private static Mutex? _singleInstanceMutex;

        // Raised on the UI thread when another launch asks this instance to come to the front.
        public static event Action? ActivationRequested;

        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            // Single-instance guard. mpv's Ctrl+E binding can fire this launch many times from one
            // held keypress; without this, each would open another editor window. Only the first
            // instance wins the mutex - any extra launch signals the running one to surface its
            // window, then exits. The OS releases the mutex on process exit, so no cleanup is needed.
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceId, out bool createdNew);
            if (!createdNew)
            {
                SignalExistingInstance();
                return;
            }

            StartActivationListener();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .UseReactiveUI(_ => { });

        // Duplicate launch: connect to the running instance's pipe to ask it to come forward.
        // Best effort - if the first instance isn't listening yet, we just exit quietly.
        private static void SignalExistingInstance()
        {
            try
            {
                using var client = new NamedPipeClientStream(".", SingleInstanceId, PipeDirection.Out);
                client.Connect(1000);
                client.WriteByte(1);
                client.Flush();
            }
            catch
            {
                // No reachable instance to signal - nothing more to do.
            }
        }

        // First instance: listen for activation requests from later launches and forward them to
        // the UI thread, where App raises the main window.
        private static void StartActivationListener()
        {
            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(SingleInstanceId, PipeDirection.In, 1);
                        server.WaitForConnection();
                        _ = server.ReadByte();
                        Dispatcher.UIThread.Post(() => ActivationRequested?.Invoke());
                    }
                    catch
                    {
                        // Keep listening despite transient pipe errors.
                    }
                }
            })
            {
                IsBackground = true,
                Name = "SingleInstanceActivationListener",
            };
            thread.Start();
        }
    }
}

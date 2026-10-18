
using System.Windows;
using System.Windows.Threading;
using MpvNet.Windows.WinForms;

namespace MpvNet.Windows.WPF;

public class WpfApplication
{
    public static void Init()
    {
        new Application();

        Application.Current!.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Application.Current!.DispatcherUnhandledException += Dispatcher_UnhandledException;

        Application.Current?.Resources.MergedDictionaries.Add(
            Application.LoadComponent(new Uri("mpv-AnimeFusion;component/WPF/Resources.xaml",
                UriKind.Relative)) as ResourceDictionary);
    }

    static void Dispatcher_UnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        StartupDiagnostics.Failed(e.Exception);
        Terminal.WriteError(e.Exception);
        e.Handled = MainForm.Instance?.TryCloseAfterUnhandledException() == true;
    }
}

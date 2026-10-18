using System.IO;
using System.Reflection;
using System.Windows.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class Program
{
    static int _checks;

    [STAThread]
    static int Main()
    {
        try
        {
            var root = FindRoot();
            var formSource = Path.Combine(root, "src/player/src/MpvNet.Windows/WinForms/MainForm.cs");
            var applicationSource = Path.Combine(root, "src/player/src/MpvNet.Windows/WPF/WpfApplication.cs");
            var source = $$"""
                using System;
                using System.Collections.Generic;
                using System.Windows.Threading;
                namespace ShutdownHarness;
                public class MainForm
                {
                    public static MainForm Instance;
                    public bool IsDisposed, Disposing, InvokeRequired, _shutdownStarted, _shutdownCompleted;
                    public bool IsHandleCreated = true, AcceptClose = true;
                    public int CloseCount;
                    public void Close()
                    {
                        CloseCount++;
                        if (AcceptClose) _shutdownStarted = true;
                    }
                    {{Method(formSource, "TryCloseAfterUnhandledException")}}
                }
                public static class StartupDiagnostics
                {
                    public static readonly List<Exception> Errors = new();
                    public static void Failed(Exception error) => Errors.Add(error);
                }
                public static class Terminal
                {
                    public static readonly List<Exception> Errors = new();
                    public static void WriteError(Exception error) => Errors.Add(error);
                }
                public class WpfApplication
                {
                    {{Method(applicationSource, "Dispatcher_UnhandledException")}}
                    public static DispatcherUnhandledExceptionEventHandler Handler() => Dispatcher_UnhandledException;
                }
                """;

            // Compile the actual production methods, not a copied implementation.
            // Only the window and logging boundaries are replaced; no form is created.
            var assembly = Compile(source);
            var formType = assembly.GetType("ShutdownHarness.MainForm")!;
            var handler = (DispatcherUnhandledExceptionEventHandler)assembly.GetType("ShutdownHarness.WpfApplication")!
                .GetMethod("Handler")!.Invoke(null, null)!;
            var diagnostics = (IReadOnlyList<Exception>)assembly.GetType("ShutdownHarness.StartupDiagnostics")!
                .GetField("Errors")!.GetValue(null)!;
            var terminal = (IReadOnlyList<Exception>)assembly.GetType("ShutdownHarness.Terminal")!
                .GetField("Errors")!.GetValue(null)!;

            object NewForm()
            {
                var form = Activator.CreateInstance(formType)!;
                formType.GetField("Instance")!.SetValue(null, form);
                return form;
            }
            void Set(object form, string name, bool value) => formType.GetField(name)!.SetValue(form, value);
            int CloseCount(object form) => (int)formType.GetField("CloseCount")!.GetValue(form)!;
            bool TryClose(object form) => (bool)formType.GetMethod("TryCloseAfterUnhandledException", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(form, null)!;

            var usable = NewForm();
            Check(TryClose(usable), "usable form accepts orderly shutdown");
            Check(CloseCount(usable) == 1, "first error requests Close once");
            Check(TryClose(usable) && CloseCount(usable) == 1, "in-flight shutdown accepts subsequent error without another Close");

            foreach (var state in new[] { "IsDisposed", "Disposing", "InvokeRequired", "_shutdownCompleted" })
            {
                var form = NewForm();
                Set(form, state, true);
                Check(!TryClose(form) && CloseCount(form) == 0, $"{state} cannot own orderly shutdown");
            }
            var uncreated = NewForm();
            Set(uncreated, "IsHandleCreated", false);
            Check(!TryClose(uncreated) && CloseCount(uncreated) == 0, "window without a handle is never closed or invoked");
            var canceled = NewForm();
            Set(canceled, "AcceptClose", false);
            Check(!TryClose(canceled) && CloseCount(canceled) == 1, "cancelled Close cannot consume dispatcher exception");

            // HwndSource.OnPreprocessMessageThunk uses this same legacy Send
            // Invoke overload within ElementHost's WinForms message filter.
            // Invoke on this STA runs synchronously, without showing any window.
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.UnhandledException += handler;
            try
            {
                var first = new InvalidOperationException("first UI failure");
                var active = NewForm();
                Exception? escaped = null;
                try { ThrowThroughDispatcher(dispatcher, first); }
                catch (Exception error) { escaped = error; }
                Check(escaped == null, "accepted fatal error does not escape Dispatcher.Invoke into WinForms");
                Check(CloseCount(active) == 1, "dispatcher error requests normal Close");
                Check(ReferenceEquals(diagnostics[^1], first) && ReferenceEquals(terminal[^1], first), "original exception instance reaches diagnostics and terminal");

                var repeated = new InvalidOperationException("another UI failure while closing");
                ThrowThroughDispatcher(dispatcher, repeated);
                Check(CloseCount(active) == 1, "repeated dispatcher error shares pending shutdown");
                Check(ReferenceEquals(diagnostics[^1], repeated) && ReferenceEquals(terminal[^1], repeated), "subsequent error is still logged");

                foreach (var unavailable in new[] { "no form", "IsDisposed", "IsHandleCreated", "AcceptClose" })
                {
                    var form = NewForm();
                    if (unavailable == "no form") formType.GetField("Instance")!.SetValue(null, null);
                    else Set(form, unavailable, unavailable == "IsDisposed");
                    var expected = new InvalidOperationException(unavailable);
                    escaped = null;
                    try { ThrowThroughDispatcher(dispatcher, expected); }
                    catch (Exception error) { escaped = error; }
                    Check(ReferenceEquals(escaped, expected), $"{unavailable}: unowned exception continues to outer handler");
                    Check(ReferenceEquals(diagnostics[^1], expected) && ReferenceEquals(terminal[^1], expected), $"{unavailable}: error retained before propagation");
                }
            }
            finally
            {
                dispatcher.UnhandledException -= handler;
                dispatcher.InvokeShutdown();
            }
            Console.WriteLine($"PASS: {_checks} window shutdown checks; no application window, GUI interaction, mpv, media, or network.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    static void ThrowThroughDispatcher(Dispatcher dispatcher, Exception error) => dispatcher.Invoke(
        DispatcherPriority.Send, new DispatcherOperationCallback(_ => throw error), new object());

    static string Method(string path, string name) => CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot()
        .DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == name).ToFullString();

    static Assembly Compile(string source)
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(Dispatcher).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase);
        var references = paths.Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("ShutdownHarness", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success) throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
        return Assembly.Load(stream.ToArray());
    }

    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src/player/src/MpvNet.Windows/WPF/WpfApplication.cs"))) return dir.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }
}

using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MpvNet;
using MpvNet.Native;
using MpvNet.Windows.Bangumi;
using MpvNet.Windows.MyAnimeList;
using MpvNet.Windows.Subtitles;
using MpvNet.Windows.Trakt;
using MpvNet.Windows.UI;
using MpvNet.Windows.WinForms;
using MpvNet.Windows.WPF;
using Forms = System.Windows.Forms;

internal static class NativeChecks
{
    const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    static int _checks;
    static readonly ConcurrentQueue<string> Events = new();
    static readonly List<Exception> DispatcherErrors = [];
    static readonly List<Exception> FormsErrors = [];
    static readonly List<Exception> LateInvokeErrors = [];

    [STAThread]
    static int Main()
    {
        try { Run(); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static void Run()
    {
        string native = Required("ANIMEFUSION_NATIVE_DIRECTORY");
        string output = Path.GetFullPath(Required("ANIMEFUSION_SHUTDOWN_AUDIT"));
        string expectedHash = Required("ANIMEFUSION_TEST_DLL_SHA256");
        string assemblyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainForm).Assembly.Location)));
        Check(assemblyHash == expectedHash, "The host uses the final candidate DLL");
        string config = Path.Combine(output, "portable_config");
        Directory.CreateDirectory(config);
        Check(!Directory.Exists(Path.Combine(Folder.Startup, "portable_config")), "Host has no competing portable configuration");
        Environment.SetEnvironmentVariable("ANIMEVE_HOME", config);
        File.WriteAllText(Path.Combine(config, "sync.json"), "{\"bangumi\":false,\"trakt\":false,\"mal\":false}");
        File.WriteAllText(Path.Combine(config, "mpv.conf"), "load-scripts=no\nvo=gpu-next\ngpu-api=d3d11\nao=null\nsave-watch-history=no\nresume-playback=no\nsave-position-on-quit=no\n");
        File.WriteAllText(Path.Combine(config, "mpv-AnimeFusion.conf"), "process-instance=multi\nremember-window-position=no\n");
        string personal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mpv-AnimeFusion");
        CheckCachedAccounts(config, personal);
        var before = PersonalFiles(personal);
        Console.WriteLine("Preflight passed: existing account avatars are cached; settings are isolated; no media or online provider will be opened.");
        if (Environment.GetEnvironmentVariable("ANIMEFUSION_PREFLIGHT_ONLY") == "1") return;

        NativeLibrary.SetDllImportResolver(typeof(LibMpv).Assembly, (name, _, _) =>
            File.Exists(Path.Combine(native, name)) ? NativeLibrary.Load(Path.Combine(native, name)) : IntPtr.Zero);
        Translator.Current = new WpfTranslator();
        Forms.Application.EnableVisualStyles();
        Forms.Application.SetCompatibleTextRenderingDefault(false);
        Forms.Application.ThreadException += (_, args) => FormsErrors.Add(args.Exception);
        AppDomain.CurrentDomain.FirstChanceException += (_, args) =>
        {
            if (args.Exception is InvalidOperationException && args.Exception.StackTrace?.Contains("MarshaledInvoke", StringComparison.Ordinal) == true)
                lock (LateInvokeErrors) LateInvokeErrors.Add(args.Exception);
        };
        StartupDiagnostics.Begin();
        Global.App.Init();
        Theme.Init();
        using var main = new MainForm();
        Check(Global.Player.MainHandle != IntPtr.Zero && Global.Player.Handle != IntPtr.Zero, "Real MainForm initialized the native mpv core and client");
        Check(Global.Player.ConfigFolder.TrimEnd('\\') == config, "Native and managed configuration is isolated");
        Check(Global.Player.GetPropertyString("path") == "", "Native core has no media");
        var expected = new ControlledDispatcherFailure("controlled lifecycle check");
        Exception? workflowFailure = null;
        bool shutdownWithWindow = false, destroyedAfterNative = false;
        main.FormClosing += (_, _) => Events.Enqueue("form-closing:" + State(main, "_shutdownCompleted"));
        Global.Player.Shutdown += () =>
        {
            shutdownWithWindow = main.IsHandleCreated && !main.IsDisposed;
            Events.Enqueue("native-shutdown:window=" + shutdownWithWindow);
        };
        main.HandleDestroyed += (_, _) =>
        {
            destroyedAfterNative = State(main, "_shutdownCompleted") && shutdownWithWindow;
            Events.Enqueue("handle-destroyed:destroy-completed=" + destroyedAfterNative);
        };
        main.Shown += (_, _) => main.BeginInvoke(new Action(() =>
        {
            try
            {
                System.Windows.Application.Current.DispatcherUnhandledException += (_, args) => DispatcherErrors.Add(args.Exception);
                Check(main.WasShown && main.IsHandleCreated, "Real MainForm completed its normal OnShown initialization");
                ExerciseSubtitlePopup(main, config);
                Events.Enqueue("controlled-exception");
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Send, new DispatcherOperationCallback(_ => throw expected), new object());
                Check(State(main, "_shutdownStarted") && main.IsHandleCreated, "Dispatcher error starts normal shutdown while retaining the HWND");
                Check(DispatcherErrors.Count == 1 && ReferenceEquals(DispatcherErrors[0], expected), "Original exception reaches the production dispatcher handler");
                Events.Enqueue("dispatcher-returned:window=" + main.IsHandleCreated);
            }
            catch (Exception error)
            {
                workflowFailure = error;
                main.Close();
            }
        }));
        using var timeout = new System.Threading.Timer(_ =>
        {
            Console.Error.WriteLine("The isolated MainForm did not finish shutdown within 30 seconds.");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
        Forms.Application.Run(main);
        timeout.Change(Timeout.Infinite, Timeout.Infinite);
        if (workflowFailure != null) throw new InvalidOperationException("Visible workflow failed", workflowFailure);
        Check(destroyedAfterNative && State(main, "_shutdownCompleted"), "Native quit and Destroy complete before the main HWND is destroyed");
        Check(main.IsDisposed && !main.IsHandleCreated, "Normal MainForm disposal completes after native shutdown");
        Check(FormsErrors.Count == 0 && LateInvokeErrors.Count == 0, "No WinForms exception or late BeginInvoke failure follows the controlled error");
        string errorPath = Path.Combine(config, "startup-error.json");
        using var diagnostic = JsonDocument.Parse(File.ReadAllText(errorPath));
        Check(diagnostic.RootElement.GetProperty("error").GetProperty("type").GetString() == typeof(ControlledDispatcherFailure).FullName,
            "First controlled exception is retained in the isolated persistent diagnostic");
        Check(diagnostic.RootElement.GetProperty("error").GetProperty("frames").GetArrayLength() > 0,
            "Original diagnostic retains its throwing stack");
        var after = PersonalFiles(personal);
        Check(before.Count == after.Count && before.All(pair => after.GetValueOrDefault(pair.Key) == pair.Value), "Personal sources, bindings, accounts, subtitle files and history remain unchanged");
        File.WriteAllLines(Path.Combine(output, "lifecycle-events.log"), Events);
        foreach (string item in Events) Console.WriteLine(item);
        Console.WriteLine($"PASS: {_checks} real MainForm/native shutdown checks; DLL SHA256={assemblyHash}; no media, network requests, formal data writes, or screenshots.");
    }

    static void ExerciseSubtitlePopup(MainForm main, string config)
    {
        var window = new SubtitleSearchWindow();
        var type = window.GetType();
        var media = TraktMedia.Parse("生命周期测试 S2E2", "", new Dictionary<string, string>());
        var titles = new SubtitleTitles(media, default);
        string[] names = [media.Title, "Lifecycle Check", "ライフサイクル検証"];
        ((Dictionary<string, Task<SubtitleQuery>>)typeof(SubtitleTitles).GetField("_queries", Fields)!.GetValue(titles)!)[media.Title]
            = Task.FromResult(new SubtitleQuery(media.Title, 2, 2, "") { Names = names });
        type.GetField("_media", Fields)!.SetValue(window, media);
        type.GetField("_titles", Fields)!.SetValue(window, titles);
        type.GetField("_store", Fields)!.SetValue(window, new SubtitleStore(Path.Combine(config, "subtitle-account")));
        type.GetField("_library", Fields)!.SetValue(window, new SubtitleLibrary(Path.Combine(config, "Subtitles")));
        ((SubtitleSearch)type.GetField("_search", Fields)!.GetValue(window)!).Dispose();
        var client = new MemoryClient();
        type.GetField("_search", Fields)!.SetValue(window, new SubtitleSearch(client));
        var input = (System.Windows.Controls.TextBox)window.FindName("SearchBox");
        input.Text = media.Title;
        new WindowInteropHelper(window).Owner = main.Handle;
        Forms.Integration.ElementHost.EnableModelessKeyboardInterop(window);
        window.Show();
        Wait(() => window.IsLoaded && window.IsVisible);
        Click((System.Windows.Controls.Button)window.FindName("SearchButton"));
        Wait(() => type.GetField("_searchResult", Fields)!.GetValue(window) is SubtitleSearchResult { Complete: true });
        Check(client.Requests == 1, "Real owned subtitle window searches the in-memory provider once");
        input.Text = "另一个手工关键词";
        Click((System.Windows.Controls.Button)window.FindName("AliasesButton"));
        var popup = (Popup)window.FindName("AliasesPopup");
        var aliases = (System.Windows.Controls.ListBox)window.FindName("AliasesList");
        Wait(() => popup.IsOpen && aliases.Items.Count >= 2 && PresentationSource.FromVisual(popup.Child) != null);
        aliases.SelectedIndex = aliases.Items.IndexOf(names[1]);
        var source = PresentationSource.FromVisual(aliases)!;
        aliases.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = aliases });
        Wait(() => !popup.IsOpen);
        Check(input.Text == names[1] && client.Requests == 1, "Real alias popup selects cached current-title alias without searching again");
        window.Close();
        Check(!window.IsVisible && window.IsCurrent, "Owned subtitle window hides normally and remains subscribed for shutdown cleanup");
        Events.Enqueue("subtitle-popup-complete");
    }

    static void CheckCachedAccounts(string config, string personal)
    {
        var bangumi = new BangumiStore(config, Path.Combine(personal, "Bangumi"));
        string bangumiUrl = bangumi.ReadAccount()?.AvatarUrl ?? "";
        if (Uri.TryCreate(bangumiUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https") Cached(bangumi.AvatarPath(bangumiUrl));
        var trakt = new TraktStore(config, Path.Combine(personal, "Trakt"));
        string traktUrl = trakt.ReadAccount()?.AvatarUrl ?? "";
        if (ValidAvatar(ref traktUrl)) Cached(trakt.ImagePath(traktUrl, true));
        var mal = new MalStore(config, Path.Combine(personal, "MyAnimeList"));
        string malUrl = mal.ReadAccount()?.AvatarUrl ?? "";
        if (ValidAvatar(ref malUrl)) Cached(mal.ImagePath(malUrl, true));
    }
    static bool ValidAvatar(ref string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!url.Contains("://")) url = "https://" + url;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https";
    }
    static void Cached(string path) => Check(File.Exists(path) && new FileInfo(path).Length == 64 * 64 * 4,
        "A current account avatar lacks a cache; stop before initializing any account or network client");
    static Dictionary<string, string> PersonalFiles(string root) => Directory.Exists(root)
        ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => Path.GetRelativePath(root, path),
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.OrdinalIgnoreCase)
        : new(StringComparer.OrdinalIgnoreCase);
    static bool State(MainForm form, string field) => (bool)typeof(MainForm).GetField(field, Fields)!.GetValue(form)!;
    static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name + " required");
    static void Click(System.Windows.Controls.Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));
    static void Wait(Func<bool> ready)
    {
        for (int n = 0; n < 5000; n++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            if (ready()) return;
            Thread.Sleep(1);
        }
        throw new TimeoutException("Isolated subtitle interaction did not complete");
    }
    static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); _checks++; }
    sealed class ControlledDispatcherFailure(string message) : Exception(message);
    sealed class MemoryClient : ISubtitleClient
    {
        public SubtitleSource Source => SubtitleSource.Assrt;
        public int Requests;
        public Task<SubtitlePage> SearchAsync(SubtitleQuery query, int page, CancellationToken token, IProgress<IReadOnlyList<SubtitleResult>>? progress = null)
        {
            token.ThrowIfCancellationRequested(); Requests++;
            return Task.FromResult(new SubtitlePage([new(Source, "fixture", query.Title, "", "", []) { Season = 2, Episode = 2, LanguageCode = "ja" }], false));
        }
        public Task<SubtitleResult> DetailAsync(SubtitleResult result, CancellationToken token) => throw new InvalidOperationException("No downloads allowed");
    }
}

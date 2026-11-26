using System.IO;
using System.Text;
using System.Windows.Controls;
using MpvNet;
using MpvNet.Windows.WPF;
using static MpvNet.Native.LibMpv;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Translator.Current = new English();
        var registered = Command.Current.Commands.Keys.Concat(GuiCommand.Current.Commands.Keys).ToHashSet();
        var configs = args.Where(arg => arg != "--native").ToArray();
        foreach (string file in configs)
        {
            var bindings = InputHelp.Parse(File.ReadAllText(file));
            var sections = new HashSet<string>();
            var menu = new ContextMenu();
            foreach (var binding in bindings)
            {
                const string prefix = "script-message-to mpvnet ";
                if (binding.Command.StartsWith(prefix))
                    Check(registered.Contains(binding.Command[prefix.Length..].Split(' ')[0]), binding.Command);

                if (!binding.IsMenu) continue;
                var item = MenuHelp.Add(menu.Items, binding.Comment);
                if (item != null && binding.Command == "" &&
                    MenuHelp.GetPlaceholderName((string)item.Header) is string name)
                    sections.Add(name);
            }
            Check(sections.IsSupersetOf(["Recent Files", "Profile", "Title", "Chapter", "Track"]), file);
            Console.WriteLine($"{Path.GetFileName(file)}: {bindings.Count} parsed rows, {sections.Count} dynamic menus");
        }
        Check(configs.Length == 2, "expected both shipped input files");
        Check(MenuHelp.GetPlaceholderName("Recent") == "Recent Files" &&
              MenuHelp.GetPlaceholderName("近期文件") == "Recent Files", "existing menu labels");
        var children = new MenuItem().Items;
        var literal = MenuHelp.AddLabel(children, "A_B | C > D");
        Check(children.Count == 1 && !literal.HasItems && (string)literal.Header == "A__B | C > D",
              "literal media titles and device names");

        var keys = InputHelp.GetActiveBindings(InputHelp.Parse("x cycle pause\nx ignore\ny cycle mute\nbroken"));
        Check(!keys.ContainsKey("x") && keys.ContainsKey("y"), "explicit unbinding and incomplete last line");
        var fileEntry = AppClass.GetTitleAndPath("https://example.invalid/video|A | B");
        Check(fileEntry.Path == "https://example.invalid/video" && fileEntry.Title == "A | B", "recent URL and title");
        const string signed = "ytdl://https://user:pass@example.invalid/video?key=test#part";
        var signedEntry = AppClass.GetTitleAndPath(signed);
        Check(signedEntry.Path == signed && signedEntry.Title == "https://example.invalid/video", "private URL display");
        var recent = new AppSettings();
        foreach (string entry in new[] { "one", "two", "three", "two" }) recent.AddRecentFile(entry, 2);
        Check(recent.GetRecentFiles().SequenceEqual(["two", "three"]), "recent order, deduplication and limit");
        recent.AddRecentFile("off", -1);
        Check(recent.GetRecentFiles().Length == 0, "invalid recent-count");
        if (args.Contains("--native")) TestBookmark();
        Console.WriteLine("Menu binding regression passed");
    }

    static void TestBookmark()
    {
        string wave = Path.Combine(Path.GetTempPath(), $"menu-test-{Guid.NewGuid():N}.wav");
        string image = Path.ChangeExtension(wave, ".png");
        const int bytes = 8000 * 12;
        using (var file = new BinaryWriter(File.Create(wave)))
        {
            file.Write("RIFF"u8); file.Write(36 + bytes); file.Write("WAVEfmt "u8);
            file.Write(16); file.Write((short)1); file.Write((short)1); file.Write(8000);
            file.Write(8000); file.Write((short)1); file.Write((short)8);
            file.Write("data"u8); file.Write(bytes); file.Write(new byte[bytes]);
        }

        var player = Global.Player;
        var bookmark = Command.Current.Commands["quick-bookmark"];
        Global.App.MediaInfo = false;
        player.MainHandle = mpv_create();
        Task? loop = null;
        try
        {
            foreach (var (key, value) in new[] { ("config", "no"), ("load-scripts", "no"),
                ("vo", "null"), ("ao", "null"), ("audio-display", "no"), ("idle", "yes"), ("pause", "yes") })
                Check(mpv_set_option_string(player.MainHandle, Encoding.UTF8.GetBytes(key + "\0"),
                    Encoding.UTF8.GetBytes(value + "\0")) == 0, key);
            Check((int)mpv_initialize(player.MainHandle) >= 0, "libmpv initialization");
            player.Handle = mpv_create_client(player.MainHandle, "menu-test");
            Check(player.Handle != 0, "libmpv event client");
            using var loaded = new AutoResetEvent(false);
            player.FileLoaded += () => loaded.Set();
            loop = Task.Run(player.EventLoop);
            void Load()
            {
                player.CommandV("loadfile", wave);
                Check(loaded.WaitOne(5000), "local WAV opened");
            }
            void WaitAt(double expected) => Check(SpinWait.SpinUntil(() =>
                Math.Abs(player.GetPropertyDouble("time-pos", false) - expected) < 0.1, 4000), $"time-pos {expected}");
            void Seek(int seconds)
            {
                player.CommandV("seek", seconds.ToString(), "absolute+exact");
                WaitAt(seconds);
            }
            Load();
            Seek(2); bookmark([]); Seek(7); bookmark([]); WaitAt(2);
            Seek(5); bookmark([]); Load(); Seek(1); bookmark([]); Seek(8); bookmark([]); WaitAt(1);
            Console.WriteLine("libmpv: quick bookmark restored, reset on reopen and restored in new file");
            File.WriteAllBytes(image, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGNgAAAAAgABSK+kcQAAAABJRU5ErkJggg=="));
            using var imageInfo = new AutoResetEvent(false);
            player.LogMessage += (_, text) =>
            {
                if (text.Contains("Size: 67 B")) imageInfo.Set();
            };
            Check((int)mpv_request_log_messages(player.Handle, "trace") >= 0, "libmpv image OSD messages");
            player.CommandV("loadfile", image);
            Check(loaded.WaitOne(5000), "local single-pixel PNG opened");
            player.PlaylistPos = player.GetPropertyInt("playlist-pos");
            GuiCommand.Current.Commands["show-media-info"](["osd"]);
            Check(imageInfo.WaitOne(5000), "image information OSD shows the 67-byte PNG size");
            Console.WriteLine("libmpv: the real image information OSD reports 67 B");
        }
        finally
        {
            if (loop != null)
            {
                player.CommandV("quit");
                Check(loop.Wait(5000), "libmpv event loop exit");
            }
            player.Destroy();
            File.Delete(wave);
            File.Delete(image);
        }
    }

    static void Check(bool ok, string name)
    {
        if (!ok) throw new InvalidOperationException(name);
    }

    sealed class English : ITranslator
    {
        public string Gettext(string msgId) => msgId;
        public string GetParticularString(string context, string text) => text;
    }
}

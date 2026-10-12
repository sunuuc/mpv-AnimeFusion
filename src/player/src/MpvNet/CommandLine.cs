namespace MpvNet;

public class CommandLine
{
    static List<StringPair>? _arguments;
    static ScopedCommandLine? _parsed;
    public static ScopedCommandLine Parsed => _parsed ??=
        ScopedCommandLine.Parse(Environment.GetCommandLineArgs().Skip(1));

    static string[] _preInitProperties { get; } = {
        "input-terminal", "terminal", "input-file", "config", "o", "config-dir", "input-conf",
        "load-scripts", "scripts", "script-opts", "player-operation-mode", "idle", "log-file",
        "msg-color", "dump-stats", "msg-level", "really-quiet",
        "playlist", "playlist-start", "shuffle" };

    public static List<StringPair> Arguments => _arguments ??= BuildArguments();

    // Options that describe one playback item rather than the player session.
    static readonly HashSet<string> PerEntryOptions = new(StringComparer.Ordinal) {
        "sub-file", "sub-files", "audio-file", "audio-files", "external-file", "external-files",
        "force-media-title", "title", "start", "end", "length",
        "sid", "aid", "vid", "slang", "alang", "sub-delay", "audio-delay",
        "http-header-fields", "referrer", "user-agent" };

    // Empty --{ ... --} groups that carry their own playlist. Each one is a
    // separate playback item: its own playlist, subtitle, title, resume position
    // and track selection. The caller sends these as independent groups so the
    // options stay attached to the item they describe.
    public static List<(string Playlist, List<ScopedCommandLine.Option> Options)> ScopedPlaylistGroups()
    {
        var groups = new List<(string Playlist, List<ScopedCommandLine.Option> Options)>();
        foreach (var group in Parsed.EmptyGroupOptions)
        {
            string playlist = "";
            foreach (var option in group)
                if (option.Name == "playlist") playlist = option.Value;
            if (playlist.Length == 0) continue;
            groups.Add((playlist, group.Where(o => o.Name != "playlist").ToList()));
        }
        // A plain global --playlist still has to be honoured, and mpv keeps only
        // the last value of this String option, so every one of them is loaded.
        if (groups.Count == 0)
            foreach (var option in Parsed.GlobalOptions)
                if (option.Name == "playlist" && option.Value.Length > 0)
                    groups.Add((option.Value, new List<ScopedCommandLine.Option>()));
        return groups;
    }

    static List<StringPair> BuildArguments()
    {
        var result = new List<StringPair>();
        bool scriptsAssigned = false;
        bool scriptOptionsAssigned = false;

        // With several playback items, their per-item options must not become
        // globals: the last item's title would then label the first one, which is
        // what made the played episode look wrong. They are applied per item by
        // ProcessCommandLineFiles instead.
        HashSet<ScopedCommandLine.Option>? perEntry = null;
        if (ScopedPlaylistGroups().Count > 1)
            perEntry = ScopedPlaylistGroups().SelectMany(g => g.Options)
                .Where(o => PerEntryOptions.Contains(ScopedCommandLine.BaseName(o.Name)))
                .ToHashSet();

        foreach (var raw in Parsed.GlobalOptions)
        {
            if (perEntry != null && perEntry.Contains(raw)) continue;
            string name;
            if (Parsed.LegacyScriptHandoff && raw.Name == "script")
                name = scriptsAssigned ? "scripts-append" : "scripts";
            else if (Parsed.LegacyScriptHandoff && raw.Name == "script-opt")
                name = scriptOptionsAssigned ? "script-opts-append" : "script-opts";
            else
                name = ScopedCommandLine.CanonicalName(raw.Name);

            result.Add(new StringPair(name, raw.Value));

            string baseName = ScopedCommandLine.BaseName(name);
            bool destructiveOnly = name.EndsWith("-remove", StringComparison.Ordinal) ||
                                   name.EndsWith("-toggle", StringComparison.Ordinal);
            if (!destructiveOnly && baseName == "scripts") scriptsAssigned = true;
            if (!destructiveOnly && baseName == "script-opts") scriptOptionsAssigned = true;
        }

        return result;
    }

    static void SetStartupOption(string name, string value)
    {
        int error = MpvNet.Native.LibMpv.mpv_set_option_string(Player.Handle,
            MpvNet.Native.LibMpv.GetUtf8Bytes(name), MpvNet.Native.LibMpv.GetUtf8Bytes(value));
        if (error < 0)
        {
            StartupDiagnostics.OptionError(error);
            // Values may contain media-server credentials; never include them.
            throw new ArgumentException($"播放器不接受启动选项 --{name}（错误 {error}）。");
        }
    }

    public static void ProcessCommandLineArgsPreInit()
    {
        // Player UI transports its playlist and selected index in an empty
        // --{ ... --} scope. --playlist is NOT a libmpv-settable startup
        // option: handing it to mpv_set_option_string before mpv_initialize
        // deadlocks the player (no request is ever issued, init never returns).
        // It is therefore skipped here and loaded explicitly after
        // initialization in ProcessCommandLineFiles().
        foreach (var pair in Arguments)
        {
            if (pair.Name is "playlist" or "input-ipc-server" || IsStartupList(pair.Name) || IsListOperation(pair.Name))
                continue;

            Player.ProcessProperty(pair.Name, pair.Value);
            if (!App.ProcessProperty(pair.Name, pair.Value))
                SetStartupOption(pair.Name, pair.Value);
        }

        if (TryBuildStartupList("script-opts", ',', out string scriptOptions))
            SetStartupOption("script-opts", scriptOptions);

        // Like startup scripts, external tracks must be available before a
        // script loads media. libmpv accepts the assembled list, not CLI suffixes.
        foreach (string name in new[] { "sub-files", "audio-files", "external-files" })
            if (TryBuildStartupList(name, Path.PathSeparator, out string files))
                SetStartupOption(name, files);
    }

    public static void ProcessCommandLineArgsPostInit()
    {
        foreach (var pair in Arguments)
        {
            if (pair.Name == "input-ipc-server" || IsStartupList(pair.Name) || IsNativePlaylistStartupOption(pair.Name))
                continue;

            if (pair.Name.EndsWith("-add", StringComparison.Ordinal))
                Player.CommandV("change-list", pair.Name[..^4], "add", pair.Value);
            else if (pair.Name.EndsWith("-set", StringComparison.Ordinal))
                Player.CommandV("change-list", pair.Name[..^4], "set", pair.Value);
            else if (pair.Name.EndsWith("-append", StringComparison.Ordinal))
                Player.CommandV("change-list", pair.Name[..^7], "append", pair.Value);
            else if (pair.Name.EndsWith("-pre", StringComparison.Ordinal))
                Player.CommandV("change-list", pair.Name[..^4], "pre", pair.Value);
            else if (pair.Name.EndsWith("-clr", StringComparison.Ordinal))
                Player.CommandV("change-list", pair.Name[..^4], "clr", "");
            else if (pair.Name.EndsWith("-remove", StringComparison.Ordinal))
                Player.CommandV("change-list", pair.Name[..^7], "remove", pair.Value);
            else if (pair.Name.EndsWith("-toggle", StringComparison.Ordinal))
                Player.CommandV("change-list", pair.Name[..^7], "toggle", pair.Value);
            else if (!_preInitProperties.Contains(pair.Name))
            {
                // Reapply mutable properties after initialization so saved
                // frontend state cannot override explicit caller values.
                Player.ProcessProperty(pair.Name, pair.Value);
                if (!App.ProcessProperty(pair.Name, pair.Value))
                    Player.SetPropertyString(pair.Name, pair.Value);
            }
        }

        // External scripts can start media immediately. Load them through mpv's
        // native command after configuration and caller options are finalized.
        if (TryBuildStartupList("scripts", Path.PathSeparator, out string scripts))
        {
            Player.SetPropertyString("scripts", scripts);
            foreach (string path in Player.GetPropertyStringArray("scripts"))
                if (path.Length > 0)
                    Player.CommandV("load-script", path);
        }

        StartupDiagnostics.Ready();

        // A caller may send loadfile as soon as the pipe opens. Publish it only
        // after the frontend has applied all caller options and loaded scripts.
        if (Contains("input-ipc-server"))
            Player.SetPropertyString("input-ipc-server", GetValue("input-ipc-server"));
    }

    static bool IsNativePlaylistStartupOption(string name) =>
        name is "playlist" or "playlist-start" or "shuffle";

    static bool IsListOperation(string name) =>
        name.EndsWith("-add", StringComparison.Ordinal) ||
        name.EndsWith("-set", StringComparison.Ordinal) ||
        name.EndsWith("-pre", StringComparison.Ordinal) ||
        name.EndsWith("-clr", StringComparison.Ordinal) ||
        name.EndsWith("-append", StringComparison.Ordinal) ||
        name.EndsWith("-remove", StringComparison.Ordinal) ||
        name.EndsWith("-toggle", StringComparison.Ordinal);

    static bool IsStartupList(string name) =>
        ScopedCommandLine.BaseName(name) is "scripts" or "script-opts" or
            "sub-files" or "audio-files" or "external-files";

    static string EscapeListItem(string value, char separator) =>
        value.Replace(separator.ToString(), "\\" + separator, StringComparison.Ordinal);

    static bool TryBuildStartupList(string baseName, char separator, out string value)
    {
        bool seen = false;
        value = "";

        foreach (var pair in Arguments)
        {
            if (ScopedCommandLine.BaseName(pair.Name) != baseName)
                continue;

            seen = true;
            if (pair.Name == baseName || pair.Name.EndsWith("-set", StringComparison.Ordinal))
            {
                value = pair.Value;
            }
            else if (pair.Name.EndsWith("-clr", StringComparison.Ordinal))
            {
                value = "";
            }
            else if (pair.Name.EndsWith("-append", StringComparison.Ordinal) ||
                     pair.Name.EndsWith("-add", StringComparison.Ordinal))
            {
                string item = EscapeListItem(pair.Value, separator);
                value = value.Length == 0 ? item : value + separator + item;
            }
            else if (pair.Name.EndsWith("-pre", StringComparison.Ordinal))
            {
                string item = EscapeListItem(pair.Value, separator);
                value = value.Length == 0 ? item : item + separator + value;
            }
            else
            {
                throw new ArgumentException($"启动阶段不支持列表操作 --{pair.Name}。");
            }
        }

        return seen;
    }

    public static void ProcessCommandLineFiles()
    {
        var groups = ScopedPlaylistGroups();
        bool shuffle = GetValue("shuffle") == "yes";

        if (groups.Count == 0 && !Parsed.HasGroups && !Contains("playlist-start") && !shuffle)
        {
            Player.LoadFiles(Parsed.Entries.Select(e => e.Path).ToArray(), !App.Queue, App.Queue);
            return;
        }
        if (groups.Count == 0 && Parsed.Entries.Count == 0) return;

        bool keepPlaying = App.Queue && Player.GetPropertyInt("playlist-count") > 0
            && Player.GetPropertyInt("playlist-pos") >= 0;
        if (!App.Queue)
        {
            Player.CommandV("stop");
            Player.CommandV("playlist-clear");
        }
        int offset = Player.GetPropertyInt("playlist-count");

        // loadlist is synchronous and, on an idle player, does not start playback,
        // so no entry is opened before the requested index is known. The index is
        // then selected with the playlist-play-index command instead of the
        // playlist-pos property, which is what used to open the first entry.
        if (groups.Count > 1)
        {
            // Several playback items. A playlist URL cannot carry file-local
            // options, so each item is loaded, detached and re-added as a plain
            // loadfile with its own group's options. Without this the items share
            // one global title and subtitle, and the last item wins.
            foreach (var group in groups)
            {
                int before = Player.GetPropertyInt("playlist-count");
                Player.CommandV("loadlist", MainPlayer.ConvertFilePath(group.Playlist), "append");
                if (Player.GetPropertyInt("playlist-count") - before != 1) continue;
                string item = Player.GetPropertyString($"playlist/{before}/filename");
                if (item.Length == 0) continue;
                Player.CommandV("playlist-remove", before.ToString());
                Player.CommandV("loadfile", item, "append", "-1",
                    ScopedCommandLine.FileOptions(group.Options));
            }
        }
        else
        {
            foreach (var group in groups)
                Player.CommandV("loadlist", MainPlayer.ConvertFilePath(group.Playlist), "append");
        }

        foreach (var entry in Parsed.Entries)
            Player.CommandV("loadfile", MainPlayer.ConvertFilePath(entry.Path), "append",
                "-1", ScopedCommandLine.FileOptions(entry.Options));

        if (shuffle) Player.CommandV("playlist-shuffle");
        int count = Player.GetPropertyInt("playlist-count");
        if (!keepPlaying && count > offset)
        {
            string requested = GetValue("playlist-start");
            int start = int.TryParse(requested, out int selected) ? selected : 0;
            start = Math.Clamp(start, 0, count - offset - 1);
            Player.CommandV("playlist-play-index", (shuffle ? 0 : offset + start).ToString());
        }
    }

    public static bool Contains(string name) => Arguments.Any(p => p.Name == name);
    public static string GetValue(string name)
    {
        for (int i=Arguments.Count-1; i>=0; i--)
            if (Arguments[i].Name == name) return Arguments[i].Value;
        return "";
    }
}

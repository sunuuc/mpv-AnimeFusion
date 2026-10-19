using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MpvNet;

// Event-driven local diagnostics. Never records argument values, URLs, titles,
// authentication headers or machine paths, and never contacts a media server.
public static class StartupDiagnostics
{
#if ANIMEFUSION_DIAGNOSTICS
    static readonly object Sync = new();
    static readonly List<string> Phases = [];
    static bool nativeReady;
    static bool sawFile;
    static bool failureRecorded;
    static int? optionError;
#endif

    public static void Begin()
    {
#if ANIMEFUSION_DIAGNOSTICS
        Player.Initialized += () => { nativeReady = true; Record("native-initialized"); };
        Player.StartFile += () => Record("media-opening");
        Player.FileLoaded += () => { sawFile = true; Record("media-loaded"); };
        Record("process-started");
        Task.Run(RecordProxy);
#endif
    }

    public static void Ready()
    {
        Record("external-handoff-ready");
        if (MissingScripts() > 0)
            Player.CommandV("show-text", "外部启动脚本不存在，请检查调用方保存的播放器路径。", "8000");
    }

    public static void OptionError(int error)
    {
#if ANIMEFUSION_DIAGNOSTICS
        optionError = error;
        Record("option-rejected");
#endif
    }
    public static void Failed(Exception error)
    {
#if ANIMEFUSION_DIAGNOSTICS
        Diagnostics.Write("startup", "failed", error: error);
        lock (Sync)
        {
            // Keep the first failure; shutdown callbacks must not replace its stack.
            if (failureRecorded) return;
            failureRecorded = true;
            Record("startup-failed");
            try
            {
                Write("startup-error.json", new
                {
                    time = DateTimeOffset.Now,
                    error = ErrorDetails(error)
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }
#endif
    }

#if ANIMEFUSION_DIAGNOSTICS
    static void RecordProxy()
    {
        try
        {
            var proxy = HttpClient.DefaultProxy;
            var targets = new[] { "api.assrt.net", "amenzb.moe", "api.tsukihime.org" };
            Diagnostics.Write("startup", "network-proxy", targets.Select(host =>
            {
                var target = new Uri("https://" + host + "/");
                bool bypassed = proxy.IsBypassed(target);
                Uri? endpoint = bypassed ? null : proxy.GetProxy(target);
                bool enabled = endpoint != null && endpoint != target;
                return new { service = host, enabled, host = enabled ? endpoint!.Host : null,
                    port = enabled ? (int?)endpoint!.Port : null };
            }).ToArray());
        }
        catch (Exception error) { Diagnostics.Write("startup", "network-proxy-failed", error: error); }
    }

    static object ErrorDetails(Exception error, int depth = 0) => new
    {
        type = error.GetType().FullName,
        code = error.HResult,
        // Exception messages may contain credentials or media URLs. Record method
        // names and source locations, without messages, arguments or machine paths.
        frames = new StackTrace(error, true).GetFrames().Select(frame => new
        {
            method = frame.GetMethod()?.DeclaringType?.FullName + "." + frame.GetMethod()?.Name,
            file = Path.GetFileName(frame.GetFileName()),
            line = frame.GetFileLineNumber()
        }).ToArray(),
        inner = error.InnerException == null || depth >= 7 ? null : ErrorDetails(error.InnerException, depth + 1)
    };

    static void Write(string name, object data)
    {
        string folder = Path.Combine(Folder.Startup, "portable_config");
        if (!Directory.Exists(folder)) folder = Player.ConfigFolder;
        string path = Path.Combine(folder, name);
        string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path + ".tmp", json);
        File.Move(path + ".tmp", path, true);
    }

    // Shape of the caller's argument list: option names, value kinds and short
    // hashes only. Never the values themselves, so URLs, titles and tokens stay
    // out of the diagnostic.
    static string[] ArgvShape()
    {
        string[] argv = Environment.GetCommandLineArgs();
        var shape = new List<string>();
        for (int i = 1; i < argv.Length; i++)
        {
            string raw = argv[i];
            if (raw is "--{" or "--}") { shape.Add(raw); continue; }
            int eq = raw.StartsWith("--") ? raw.IndexOf('=') : -1;
            shape.Add(eq < 0 ? (raw.StartsWith("--") ? raw : ValueKind(raw))
                             : raw[..eq] + "=" + ValueKind(raw[(eq + 1)..]));
        }
        return shape.ToArray();
    }

    static string ValueKind(string value)
    {
        if (value.Contains("://")) return "<url:" + Hash8(value) + ">";
        if (value.StartsWith("\\\\") || (value.Length > 1 && value[1] == ':')) return "<path>";
        if (double.TryParse(value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double number)) return "<num>";
        return "<text:" + value.Length + ">";
    }

    static string Hash8(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();

    static string[] PlaylistHashes()
    {
        var hashes = new List<string>();
        foreach (var option in CommandLine.Parsed.GlobalOptions)
            if (option.Name == "playlist") hashes.Add(Hash8(option.Value));
        return hashes.ToArray();
    }

    // One short hash per playlist entry, taken from the URL path with the query
    // string removed. Enough to tell entries apart and to see which one plays,
    // without writing any address, title or token to disk.
    static string[] PlaylistEntryHashes()
    {
        var hashes = new List<string>();
        try
        {
            int count = Player.GetPropertyInt("playlist-count");
            for (int i = 0; i < count && i < 64; i++)
            {
                string file = Player.GetPropertyString($"playlist/{i}/filename");
                if (file.Length == 0)
                    file = Player.GetPropertyString($"playlist/{i}/playlist-path");
                int query = file.IndexOf('?');
                if (query >= 0) file = file[..query];
                hashes.Add(Hash8(file));
            }
        }
        catch { }
        return hashes.ToArray();
    }
#endif

    static int MissingScripts()
    {
        int missing = 0;
        foreach (var option in CommandLine.Parsed.GlobalOptions)
        {
            // A singular --script is one filename, not a semicolon-delimited list.
            if (option.Name != "script") continue;
            string path = option.Value;
            if (path.Contains("://") || path.StartsWith("\\\\") || path.StartsWith("//")) continue;
            if (path.StartsWith("~~/")) path = Path.Combine(Player.ConfigFolder, path[3..]);
            if (path.StartsWith("~")) continue;
            if (!File.Exists(path) && !Directory.Exists(path)) missing++;
        }
        return missing;
    }

    static void Record(string phase)
    {
#if ANIMEFUSION_DIAGNOSTICS
        Diagnostics.Write("startup", phase);
        lock (Sync)
        {
            try
            {
                Phases.Add(phase);
                if (Phases.Count > 24) Phases.RemoveAt(0);
                ScopedCommandLine? parsed;
                try { parsed = CommandLine.Parsed; } catch { parsed = null; }
                var options = parsed?.GlobalOptions ?? [];
                var playlistStarts = options.Where(o => o.Name == "playlist-start").ToList();
                string? rawStart = playlistStarts.LastOrDefault()?.Value;
                int? startIndex = int.TryParse(rawStart, out int selected) ? selected : null;
                string startKind = rawStart == null ? "none"
                    : startIndex.HasValue ? "index"
                    : rawStart is "auto" or "no" ? "auto" : "other";
                string configuration = options.Any(o => o.Name == "config-dir") ? "explicit"
                    : Directory.Exists(Environment.GetEnvironmentVariable("ANIMEVE_HOME")) ? "environment"
                    : Directory.Exists(Path.Combine(Folder.Startup, "portable_config")) ? "portable" : "appdata";
                var data = new
                {
                    version = 3,
                    phases = Phases.ToArray(),
                    argument_count = Environment.GetCommandLineArgs().Length - 1,
                    argv_shape = ArgvShape(),
                    media_arguments = parsed?.Entries.Count,
                    scoped_playlist = parsed?.HasGroups,
                    empty_scope_count = parsed?.EmptyGroupCount,
                    empty_scope_options_promoted = parsed?.EmptyGroupOptionsPromoted,
                    promoted_option_names = parsed?.PromotedOptions.Select(o => o.Name)
                        .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                    legacy_script_handoff = parsed?.LegacyScriptHandoff,
                    global_option_count = options.Count,
                    playlist_option = options.Any(o => o.Name == "playlist"),
                    playlist_option_count = options.Count(o => o.Name == "playlist"),
                    playlist_hashes = parsed == null ? [] : PlaylistHashes(),
                    playlist_start_option_count = playlistStarts.Count,
                    playlist_start_kind = startKind,
                    playlist_start_index = startIndex,
                    native_playlist_handoff = options.Any(o => o.Name == "playlist"),
                    script_options = options.Count(o => ScopedCommandLine.BaseName(ScopedCommandLine.CanonicalName(o.Name)) == "scripts"),
                    script_payload_options = options.Count(o => ScopedCommandLine.BaseName(ScopedCommandLine.CanonicalName(o.Name)) == "script-opts"),
                    missing_scripts = parsed == null ? (int?)null : MissingScripts(),
                    ipc_requested = options.Any(o => o.Name is "input-ipc-server" or "input-ipc-client"),
                    configuration,
                    native_initialized = nativeReady,
                    has_media = nativeReady && Player.GetPropertyString("path").Length > 0,
                    playlist_count = nativeReady ? Player.GetPropertyInt("playlist-count") : 0,
                    playlist_current_pos = nativeReady ? Player.GetPropertyInt("playlist-pos") : -1,
                    playlist_entry_hashes = nativeReady ? PlaylistEntryHashes() : [],
                    file_loaded = sawFile,
                    option_error = optionError
                };
                Write("startup-diagnostic.json", data);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }
#endif
    }
}

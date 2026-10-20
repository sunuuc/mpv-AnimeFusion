using MpvNet.Windows.Trakt;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace MpvNet.Windows.Subtitles;

public sealed class SubtitlePlayback(SubtitleLibrary library)
{
    public static SubtitlePlayback Current { get; private set; } = null!;
    public SubtitleLibrary Library { get; } = library;
    readonly object _lock = new();
    readonly HashSet<string> _knownTracks = [];
    bool _loaded, _applying;
    string _title = "";
    string _path = "";
    TraktMedia _media = TraktMedia.Parse("", "", new Dictionary<string, string>());
    CancellationTokenSource? _download;
    TaskCompletionSource? _downloadFinished;
    int _generation;
    CancellationTokenSource _metadataLifetime = new();
    readonly HashSet<string> _metadataRequested = [];
    readonly Dictionary<string, Dictionary<string, Dictionary<int, EmbySubtitleMetadata.Track>>> _metadata = [];
    public string DownloadStatus { get; private set; } = "";
    sealed class DownloadProgress(SubtitlePlayback owner, int generation) : IProgress<string>
    { public void Report(string text) => owner.Notice(generation, text); }

    public static void Initialize()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mpv-AnimeFusion", "Subtitles");
        Initialize(new(root));
    }
    public static void Initialize(SubtitleLibrary library)
    {
        Current = new(library);
        Player.StartFile += () => { lock (Current._lock) { Current.CancelDownload(); Current.ResetMetadata(); Current._loaded = false; Current._title = ""; Current._knownTracks.Clear(); } };
        Player.FileLoaded += () =>
        {
            lock (Current._lock)
            {
                Current.KnowTracks();
                Current._loaded = true;
                Current.Load(Player.GetPropertyString("media-title"));
                Current.UpdateMetadata();
            }
        };
        Player.ObservePropertyString("media-title", Current.Load);
        Player.ObservePropertyString("sid", _ => Current.Remember());
        Player.ObserveProperty("track-list", Current.TracksChanged);
        Player.Shutdown += () => { lock (Current._lock) { Current.CancelDownload(); Current._metadataLifetime.Cancel(); Current._loaded = false; } };
    }
    sealed record PlaybackTrack(string Id, string Path, string Language);
    IReadOnlyList<PlaybackTrack> LocalTracks()
    {
        using var document = JsonDocument.Parse(Player.GetPropertyString("track-list"));
        return document.RootElement.EnumerateArray()
            .Where(track => track.GetProperty("type").GetString() == "sub" && track.GetProperty("external").GetBoolean())
            .Select(track => new PlaybackTrack(track.GetProperty("id").ToString(), track.GetProperty("external-filename").GetString() ?? "",
                track.TryGetProperty("lang", out var language) ? language.GetString() ?? "" : "")).ToArray();
    }
    static string TrackKey(PlaybackTrack track) => track.Id + "\n" + track.Path;
    void KnowTracks()
    {
        _knownTracks.Clear();
        foreach (var track in LocalTracks()) _knownTracks.Add(TrackKey(track));
    }
    void TracksChanged()
    {
        lock (_lock)
        {
            if (!_loaded || _applying || Player.GetPropertyString("path") != _path) return;
            var tracks = LocalTracks();
            var added = tracks.Where(track => !_knownTracks.Contains(TrackKey(track))).ToArray();
            _knownTracks.Clear();
            foreach (var track in tracks) _knownTracks.Add(TrackKey(track));
            _applying = true;
            try
            {
                var managed = Library.Tracks(_media).Select(track => track.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                string secondary = Player.GetPropertyString("secondary-sid");
                foreach (var track in added)
                {
                    try
                    {
                        if (Player.GetPropertyString("path") != _path) return;
                        if (track.Id == secondary || track.Language == "danmaku" || managed.Contains(track.Path)
                            || !FileTypes.Subtitle.Contains(Path.GetExtension(track.Path).TrimStart('.').ToLowerInvariant()) || !File.Exists(track.Path)) continue;
                        string path = Library.Import(_media, track.Path);
                        if (Player.GetPropertyString("path") != _path) return;
                        if (track.Id == Player.GetPropertyString("secondary-sid")) continue;
                        if (!LocalTracks().Any(item => item.Id == track.Id && item.Path == track.Path)) continue;
                        bool wasSelected = Player.GetPropertyString("sid") == track.Id;
                        if (!path.Equals(track.Path, StringComparison.OrdinalIgnoreCase))
                            Player.CommandV("sub-add", path, wasSelected ? "cached" : "auto", Path.GetFileName(path));
                        bool available = LocalTracks().Any(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                        bool selected = Player.GetPropertyString("current-tracks/sub/external-filename", handleError: false).Equals(path, StringComparison.OrdinalIgnoreCase);
                        foreach (var imported in LocalTracks().Where(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                            _knownTracks.Add(TrackKey(imported));
                        if (available && (!wasSelected || selected))
                        {
                            if (selected)
                            {
                                CancelDownload();
                                Library.Remember(_media.Title, _media.Kind == "show" ? _media.Season : null, path);
                            }
                            if (!path.Equals(track.Path, StringComparison.OrdinalIgnoreCase)) Player.CommandV("sub-remove", track.Id);
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or SubtitleServiceException)
                    {
                        Terminal.WriteError(error);
                        Notice(_generation, error is SubtitleServiceException ? error.Message : "本地字幕保存失败", true);
                    }
                }
            }
            finally { _applying = false; }
            UpdateMetadata();
        }
    }
    void ResetMetadata()
    {
        _metadataLifetime.Cancel(); _metadataLifetime.Dispose(); _metadataLifetime = new();
        _metadataRequested.Clear(); _metadata.Clear();
        Player.SetPropertyString("user-data/player_ui/subtitles", "{}");
    }
    void UpdateMetadata()
    {
        lock (_lock)
        {
            if (!_loaded) return;
            string video = Player.GetPropertyString("path"), agent = Player.GetPropertyString("user-agent");
            var labels = new Dictionary<string, object>();
            var local = Library.Tracks(_media).DistinctBy(track => track.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(track => track.Path, StringComparer.OrdinalIgnoreCase);
            var requests = new List<EmbySubtitleMetadata.Source>();
            foreach (var playbackTrack in LocalTracks())
            {
                string path = playbackTrack.Path, id = playbackTrack.Id;
                if (id != Player.GetPropertyString("secondary-sid") && local.TryGetValue(path, out var saved))
                    labels[id] = new { managed = true, pinned = saved.Pinned };
                var source = EmbySubtitleMetadata.Identify(path, video, agent);
                if (source == null) continue;
                string key = source.Request.AbsoluteUri;
                if (_metadata.TryGetValue(key, out var versions) && versions.TryGetValue(source.MediaSource, out var tracks)
                    && tracks.TryGetValue(source.Index, out var track)) labels[id] = new { title = track.Title, lang = track.Language };
                if (_metadataRequested.Add(key)) requests.Add(source);
            }
            Player.SetPropertyString("user-data/player_ui/subtitles", JsonSerializer.Serialize(new
            {
                tracks = labels
            }));
            if (requests.Count > 0)
            {
                Task pending = ReadMetadataAsync(requests, _metadataLifetime.Token,
                    Player.GetPropertyString("http-proxy"), Player.GetPropertyBool("tls-verify"));
            }
        }
    }
    async Task ReadMetadataAsync(IReadOnlyList<EmbySubtitleMetadata.Source> requests, CancellationToken token, string proxy, bool verifyTls)
    {
        using var http = EmbySubtitleMetadata.CreateClient(proxy, verifyTls);
        foreach (var request in requests)
        {
            try
            {
                var metadata = await EmbySubtitleMetadata.ReadAsync(http, request, token).ConfigureAwait(false);
                lock (_lock)
                {
                    if (token.IsCancellationRequested || !_loaded) return;
                    _metadata[request.Request.AbsoluteUri] = metadata;
                    UpdateMetadata();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error) when (error is HttpRequestException or JsonException or IOException or OperationCanceledException or SubtitleServiceException)
            {
                if (token.IsCancellationRequested) return;
                string status = error is HttpRequestException { StatusCode: { } code } ? "：HTTP " + (int)code : "";
                Terminal.WriteError("在线字幕资料读取失败" + status);
            }
        }
    }
    void Load(string title)
    {
        lock (_lock)
        {
            if (!_loaded || title.Length == 0 || title == _title) return;
            CancelDownload();
            _title = title;
            _path = Player.GetPropertyString("path");
            _media = TraktMedia.Parse(title, _path, new Dictionary<string, string>());
            Apply(null);
        }
    }
    void CancelDownload()
    {
        if (_download != null) MpvNet.Diagnostics.Write("subtitle.playback", "cancel", new { generation = _generation });
        _generation++;
        _download?.Cancel(); _download = null;
        if (DownloadStatus.Length > 0) Player.CommandV("show-text", "", "0");
        DownloadStatus = "";
    }
    void Notice(int generation, string text, bool finished = false)
    {
        lock (_lock)
        {
            if (!_loaded || generation != _generation || DownloadStatus == text) return;
            DownloadStatus = text;
            if (text.Length == 0) { Player.CommandV("show-text", "", "0"); return; }
            string message = Player.GetPropertyString("osd-ass-cc/0") + @"{\an2\fs8\bord0.6\shad0}"
                + Player.GetPropertyString("osd-ass-cc/1") + text;
            Player.CommandV("show-text", message, finished ? "5000" : "600000");
        }
    }
    public async Task DownloadAsync(SubtitleQuery query, SubtitleResult result, SubtitleStore store, SubtitleTitles titles,
        SubtitleFile? selectedFile = null)
    {
        CancellationTokenSource request;
        TaskCompletionSource finished;
        int generation;
        lock (_lock)
        {
            CancelDownload();
            request = _download = new(); generation = _generation;
            finished = _downloadFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        using var api = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
        using var filesClient = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(60) };
        try
        {
            MpvNet.Diagnostics.Write("subtitle.playback", "start", new { generation, source = result.Source.ToString(),
                result.Id, result.Local, result.Individual, single = selectedFile != null,
                query.RequestSeason, query.RequestEpisode, query.Language });
            Notice(generation, result.Local ? "正在加载字幕…" : selectedFile != null || result.Individual ? "正在获取字幕…" : "正在获取字幕包…");
            if (!result.Local && result.Files.Count == 0 && result.DownloadUrl.Length == 0)
            {
                ISubtitleClient source = result.Source switch
                {
                    SubtitleSource.Amenzb => new AmenzbClient(api),
                    SubtitleSource.Tsukihime => new TsukihimeClient(api),
                    _ => new AssrtClient(api, store)
                };
                result = await source.DetailAsync(result, request.Token).ConfigureAwait(false);
                MpvNet.Diagnostics.Write("subtitle.playback", "detail", new { generation, files = result.Files.Count });
            }
            request.Token.ThrowIfCancellationRequested();
            bool current;
            (query, current) = titles.CurrentWork().ForDownload(query, result);
            MpvNet.Diagnostics.Write("subtitle.playback", "match", new { generation, current, query.Season, query.Episode,
                query.ContentType, query.ContentEpisode, sourceSeason = result.Season, sourceEpisode = result.Episode,
                result.Collection, result.Individual });
            if (selectedFile != null && (selectedFile.Source != result.Source || selectedFile.ResultId != result.Id))
                throw new SubtitleServiceException("字幕文件无效。");
            var selected = selectedFile ?? (result.Local ? Library.CurrentFile(query, result, result.Files) : null);
            if (selected == null && result.Local)
                throw new SubtitleServiceException("没有当前集的字幕。");
            var progress = new DownloadProgress(this, generation);
            string? path = await new SubtitleDownload(filesClient, Library)
                { ChooseArchiveNamesAsync = WPF.SubtitleArchiveNamesWindow.ChooseAsync }
                .DownloadAsync(query, result, selected, result.Files, progress, request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            lock (_lock)
            {
                MpvNet.Diagnostics.Write("subtitle.playback", "saved", new { generation, current, matched = path != null });
                if (!_loaded || generation != _generation || Player.GetPropertyString("path") != _path
                    || Player.GetPropertyString("media-title") != _title)
                { MpvNet.Diagnostics.Write("subtitle.playback", "stale", new { generation }); return; }
                if (path == null || !current) { Notice(generation, result.Individual ? "字幕已保存" : "字幕包已保存", true); return; }
                Select(path);
                MpvNet.Diagnostics.Write("subtitle.playback", "selected", new { generation,
                    sid = Player.GetPropertyString("sid"), visible = Player.GetPropertyBool("sub-visibility") });
                Notice(generation, "");
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        { MpvNet.Diagnostics.Write("subtitle.playback", "cancelled", new { generation }); }
        catch (Exception error)
        {
            MpvNet.Diagnostics.Write("subtitle.playback", "failed", new { generation, source = result.Source.ToString() }, error);
            Notice(generation, result.Local ? "字幕加载失败" : "字幕下载失败", true);
        }
        finally
        {
            lock (_lock)
            {
                if (_download == request) _download = null;
                if (_downloadFinished == finished) _downloadFinished = null;
            }
            request.Dispose();
            finished.TrySetResult();
        }
    }
    public void Select(string path)
    {
        lock (_lock)
        {
            Library.Remember(_media.Title, _media.Kind == "show" ? _media.Season : null, path);
            Apply(path);
        }
    }
    public sealed record Removal(string Title, string Path, string Name, int Generation);
    public Removal? PrepareRemoval(string id)
    {
        lock (_lock)
        {
            if (!_loaded || Player.GetPropertyString("path") != _path || id == Player.GetPropertyString("secondary-sid")) return null;
            var track = LocalTracks().FirstOrDefault(track => track.Id == id);
            var package = track == null ? null : Library.Results(_media.Title)
                .FirstOrDefault(package => package.Files.Any(file => file.LocalPath.Equals(track.Path, StringComparison.OrdinalIgnoreCase)));
            return track != null && package != null
                && Library.Tracks(_media).Any(saved => saved.Path.Equals(track.Path, StringComparison.OrdinalIgnoreCase))
                ? new(_media.Title, track.Path, package.Title, _generation) : null;
        }
    }
    public async Task<bool> RemoveAsync(Removal removal, bool deleteLocal)
    {
        Task pending;
        int generation;
        lock (_lock)
        {
            if (!_loaded || Player.GetPropertyString("path") != _path || removal.Generation != _generation || removal.Title != _media.Title) return false;
            pending = _downloadFinished?.Task ?? Task.CompletedTask;
            CancelDownload();
            generation = _generation;
        }
        // Finish cancellation before removing the set, so an in-flight archive
        // cannot restore files or records after the deletion has completed.
        await pending.ConfigureAwait(false);
        lock (_lock)
        {
            bool Current() => _loaded && generation == _generation && removal.Title == _media.Title
                && Player.GetPropertyString("path") == _path && Player.GetPropertyString("media-title") == _title;
            if (!Current()) return false;
            var paths = Library.Remove(removal.Title, removal.Path, deleteLocal).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!Current()) return true;
            _applying = true;
            try
            {
                foreach (var track in LocalTracks())
                {
                    if (!Current()) return true;
                    if (paths.Contains(track.Path) && track.Id != Player.GetPropertyString("secondary-sid")) Player.CommandV("sub-remove", track.Id);
                }
            }
            finally { if (Current()) KnowTracks(); _applying = false; }
            if (Current()) UpdateMetadata();
            return true;
        }
    }
    public void TogglePinned(string id)
    {
        lock (_lock)
        {
            var removal = PrepareRemoval(id);
            if (removal == null) return;
            var track = Library.Tracks(_media).First(item => item.Path.Equals(removal.Path, StringComparison.OrdinalIgnoreCase));
            Library.SetPinned(_media.Title, track.Path, !track.Pinned);
            if (!track.Pinned) { CancelDownload(); Apply(track.Path); }
            UpdateMetadata();
        }
    }
    void Apply(string? selected)
    {
        _applying = true;
        try
        {
            var tracks = Library.Tracks(_media);
            string selection = Player.GetPropertyString("sid");
            string external = Player.GetPropertyString("current-tracks/sub/external-filename", handleError: false);
            // Preserve explicit playback options. A same-name subtitle discovered
            // by mpv's sub-auto must not override the remembered library choice.
            bool supplied = Player.GetPropertyString("options/sub-files").Length > 0
                || external.Length > 0 && Player.GetPropertyStringArray("options/external-files").Contains(external, StringComparer.OrdinalIgnoreCase)
                || (CommandLine.Contains("sid") || Player.GetPropertyBool("option-info/sid/set-locally") || Player.GetPropertyBool("option-info/sid/set-from-commandline"))
                    && !external.StartsWith(Library.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            var existing = LocalTracks().Select(track => track.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var track in tracks)
                if (!existing.Contains(track.Path)) Player.CommandV("sub-add", track.Path, "auto", track.Title, track.Language);
            string? preferred = selected ?? tracks.FirstOrDefault(track => track.Pinned)?.Path
                ?? (supplied ? null : tracks.FirstOrDefault(track => track.Preferred)?.Path);
            if (preferred != null)
            {
                Player.CommandV("sub-add", preferred, "cached");
                Player.SetPropertyBool("sub-visibility", true);
            }
            else if (supplied && selection.Length > 0) Player.SetPropertyString("sid", selection);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { Terminal.WriteError(error); }
        finally { KnowTracks(); _applying = false; UpdateMetadata(); }
    }
    void Remember()
    {
        lock (_lock)
        {
            if (!_loaded || _applying) return;
            string path = Player.GetPropertyString("current-tracks/sub/external-filename", handleError: false);
            if (!path.StartsWith(Library.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            try { Library.Remember(_media.Title, _media.Kind == "show" ? _media.Season : null, path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Terminal.WriteError(error); }
        }
    }
}

using MpvNet.Windows.Trakt;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MpvNet.Windows.Subtitles;

public sealed class SubtitlePlayback(SubtitleLibrary library)
{
    public static SubtitlePlayback Current { get; private set; } = null!;
    public SubtitleLibrary Library { get; } = library;
    readonly object _lock = new();
    bool _loaded, _applying;
    string _title = "";
    TraktMedia _media = TraktMedia.Parse("", "", new Dictionary<string, string>());
    CancellationTokenSource? _download;
    int _generation;
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
        Player.StartFile += () => { lock (Current._lock) { Current.CancelDownload(); Current._loaded = false; Current._title = ""; } };
        Player.FileLoaded += () => { lock (Current._lock) Current._loaded = true; Current.Load(Player.GetPropertyString("media-title")); };
        Player.ObservePropertyString("media-title", Current.Load);
        Player.ObservePropertyString("sid", _ => Current.Remember());
        Player.Shutdown += () => { lock (Current._lock) { Current.CancelDownload(); Current._loaded = false; } };
    }
    void Load(string title)
    {
        lock (_lock)
        {
            if (!_loaded || title.Length == 0 || title == _title) return;
            CancelDownload();
            _title = title;
            _media = TraktMedia.Parse(title, Player.GetPropertyString("path"), new Dictionary<string, string>());
            Apply(null);
        }
    }
    void CancelDownload()
    {
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
    public async Task DownloadAsync(SubtitleQuery query, SubtitleResult result, SubtitleStore store, bool current)
    {
        CancellationTokenSource request;
        int generation;
        lock (_lock)
        {
            CancelDownload();
            request = _download = new(); generation = _generation;
        }
        using var api = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
        using var filesClient = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(60) };
        try
        {
            Notice(generation, result.Local ? "正在加载字幕…" : "正在获取字幕包…");
            if (!result.Local)
                result = await new AssrtClient(api, store).DetailAsync(result, request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            var selected = Library.CurrentFile(query, result, result.Files);
            if (selected == null && result.Local)
                throw new SubtitleServiceException("没有当前集的字幕。");
            var progress = new DownloadProgress(this, generation);
            string? path = await new SubtitleDownload(filesClient, Library)
                .DownloadAsync(query, result, selected, result.Files, progress, request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (!_loaded || generation != _generation) return;
                if (path == null || !current) { Notice(generation, "字幕包已保存", true); return; }
                Select(path);
                Notice(generation, "");
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error)
        {
            string text = error is SubtitleServiceException ? error.Message : error is OperationCanceledException ? "下载超时，请重试。"
                : error is IOException or UnauthorizedAccessException ? "字幕保存失败。" : "字幕下载失败，请重试。";
            Notice(generation, text, true);
        }
        finally
        {
            lock (_lock) if (_download == request) _download = null;
            request.Dispose();
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
    void Apply(string? selected)
    {
        _applying = true;
        try
        {
            var tracks = Library.Tracks(_media);
            int sid = Player.GetPropertyInt("sid");
            string selection = Player.GetPropertyString("sid");
            string external = Player.GetPropertyString("current-tracks/sub/external-filename");
            // mpv already processes per-file sub-files and external subtitle streams.
            // Preserve that selection while registering our downloaded alternatives.
            bool supplied = Player.GetPropertyString("options/sub-files").Length > 0
                || (Player.GetPropertyBool("option-info/sid/set-locally") || Player.GetPropertyBool("option-info/sid/set-from-commandline"))
                    && !external.StartsWith(Library.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || external.Length > 0 && !external.StartsWith(Library.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && sid != Player.GetPropertyInt("secondary-sid");
            var existing = Enumerable.Range(0, Player.GetPropertyInt("track-list/count"))
                .Select(index => Player.GetPropertyString($"track-list/{index}/external-filename")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var track in tracks)
                if (!existing.Contains(track.Path)) Player.CommandV("sub-add", track.Path, "auto", track.Title, track.Language);
            string? preferred = selected ?? (supplied ? null : tracks.FirstOrDefault(track => track.Preferred)?.Path);
            if (preferred != null)
            {
                Player.CommandV("sub-add", preferred, "cached");
                Player.SetPropertyBool("sub-visibility", true);
            }
            else if (supplied && selection.Length > 0) Player.SetPropertyString("sid", selection);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { Terminal.WriteError(error); }
        finally { _applying = false; }
    }
    void Remember()
    {
        lock (_lock)
        {
            if (!_loaded || _applying) return;
            string path = Player.GetPropertyString("current-tracks/sub/external-filename");
            if (!path.StartsWith(Library.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            try { Library.Remember(_media.Title, _media.Kind == "show" ? _media.Season : null, path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Terminal.WriteError(error); }
        }
    }
}

using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MpvNet.Windows.Sync;
using MpvNet.Windows.Bangumi;

namespace MpvNet.Windows.MyAnimeList;

public sealed class MalPlayback
{
    public static MalPlayback Current { get; private set; } = null!;
    public MalStore Store { get; }
    public MalClient Client { get; }
    public MalMedia Media { get; private set; } = MalMedia.Parse("", "", new Dictionary<string, string>());
    public MalSubject? Subject { get; private set; }
    public IReadOnlyList<MalEpisode> Episodes { get; private set; } = [];
    public MalEpisode? Selection { get; private set; }
    public int Season { get; private set; }
    public string Status { get; private set; } = "";
    public event Action? Changed;
    readonly object _lock = new();
    CancellationTokenSource _file = new();
    readonly CancellationTokenSource _lifetime = new();
    CancellationTokenSource? _authorization;
    readonly SemaphoreSlim _authorizationGate = new(1, 1);
    int _generation;
    bool _loaded, _resolving, _authorizing, _syncing, _attempted, _enabled;
    double _percent;
    string _mediaTitle = "", _avatar = "", _cover = "";
    int _watched;
    public int Generation { get { lock (_lock) return _generation; } }
    bool Enabled => SyncPreferences.Current.Enabled("mal");

    public MalPlayback(MalStore store, MalClient client)
    { Store = store; Client = client; Status = store.LoadError.Length > 0 ? store.LoadError : client.AccountError; }

    public static void Initialize()
    {
        var store = new MalStore(Player.ConfigFolder,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mpv-AnimeFusion", "MyAnimeList"));
        Current = new(store, new(store, new HttpClient { Timeout = TimeSpan.FromSeconds(20) }));
        Current._enabled = Current.Enabled;
        Current.Changed += () => Player.SetPropertyString("user-data/player_ui/mal", Current.SnapshotJson());
        Current.Client.AccountChanged += () => { Task pending = Current.PublishAccountAsync(); };
        Task account = Current.PublishAccountAsync();
        Player.StartFile += () => { lock (Current._lock) Current._loaded = false; Current.SetMedia(""); };
        Player.FileLoaded += () =>
        {
            lock (Current._lock) Current._loaded = true;
            Current.SetMedia(Player.GetPropertyString("media-title"));
        };
        Player.ObservePropertyString("media-title", title =>
        {
            lock (Current._lock) { if (!Current._loaded || title.Length == 0 || title == Current._mediaTitle) return; }
            Current.SetMedia(title);
        });
        Player.ObservePropertyDouble("percent-pos", Current.UpdateProgress);
        SyncPreferences.Current.Changed += () =>
        {
            bool enabled = Current.Enabled;
            if (enabled == Current._enabled) return;
            Current._enabled = enabled;
            Current.SetMedia(Current._loaded ? Player.GetPropertyString("media-title") : "");
            Current.UpdateProgress(Player.GetPropertyDouble("percent-pos"));
        };
        Player.Shutdown += () => { Current.Changed = null; Current._lifetime.Cancel(); Current._authorization?.Cancel(); Current.Cancel(); };
    }

    async Task PublishAccountAsync()
    {
        var account = Client.Account;
        lock (_lock) _avatar = "";
        Changed?.Invoke();
        if (account == null) return;
        try
        {
            string avatar = await Client.AvatarFileAsync(_lifetime.Token).ConfigureAwait(false);
            lock (_lock) { if (Client.Account != account) return; _avatar = avatar; }
            Changed?.Invoke();
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or NotSupportedException or ArgumentException) { }
    }

    public string SnapshotJson()
    {
        lock (_lock) return JsonSerializer.Serialize(new
        {
            connected = Client.Account != null, username = Client.Account?.Username ?? "", avatar = _avatar,
            generation = _generation, busy = _syncing || _authorizing, authorizing = _authorizing, resolving = _resolving, status = Status,
            settings = new { autoSync = Store.Settings.AutoSync, watchedPercent = Store.Settings.WatchedPercent },
            subject = Subject == null ? null : new
            {
                id = Subject.Id, title = Subject.Title, date = Subject.StartDate ?? "", score = Subject.Mean,
                season = Subject.MediaType == "movie" ? (int?)null : Season, kind = Subject.MediaType == "movie" ? "电影" : "剧集", cover = _cover,
                watchedCount = _watched, currentEpisode = Selection?.Number,
                episodes = Episodes.OrderBy(e => e.Number).Select(e => new
                { id = e.Number, number = e.Number, title = Subject.MediaType == "movie" ? "正片" : e.Display,
                    kind = 0, state = e.Number <= _watched ? 2 : 0 })
            }
        });
    }

    void Cancel()
    {
        lock (_lock)
        {
            _file.Cancel(); _file.Dispose(); _file = new(); _generation++;
            _resolving = _syncing = _attempted = false;
            Subject = null; Selection = null; Episodes = []; _watched = 0; _cover = ""; Status = "";
        }
        Changed?.Invoke();
    }
    public void SetMedia(string title)
    {
        Cancel();
        var metadata = new Dictionary<string, string>();
        foreach (string provider in new[] { "mal", "bangumi" })
        {
            string value = Player.GetPropertyString("metadata/by-key/" + provider + "_id", handleError: false);
            if (value.Length > 0) metadata[provider] = value;
        }
        var media = MalMedia.Parse(title, title.Length > 0 ? Player.GetPropertyString("path") : "", metadata);
        if (media.BangumiId == null && BangumiPlayback.Current is { } bangumi
            && bangumi.Store.FindBinding(media.Key) is { } binding)
            media = media with { BangumiId = binding.SubjectId };
        lock (_lock) { Media = media; _mediaTitle = title; _percent = 0; }
        Changed?.Invoke();
        if (Enabled && Client.Account != null && Media.Title.Length > 0) { Task pending = ResolveAsync(); }
    }
    public void RetryResolve()
    {
        lock (_lock) { if (_resolving || Client.Account == null || !Enabled) return; Status = ""; }
        Task pending = ResolveAsync();
    }

    public async Task ResolveAsync()
    {
        int generation; MalMedia media; CancellationToken token; MalEpisode? chosen;
        lock (_lock)
        {
            if (_resolving || !Enabled || Client.Account == null || Media.Title.Length == 0) return;
            generation = _generation; media = Media; token = _file.Token; chosen = Selection; _resolving = true; Status = "";
        }
        Changed?.Invoke();
        try
        {
            var binding = Store.FindBinding(media.Key);
            var subject = binding == null ? await MalMatching.MatchAsync(Client, media, token).ConfigureAwait(false)
                : await Client.SubjectAsync(binding.Id, false, token).ConfigureAwait(false);
            if (subject == null) { SetStatus(generation, "请选择对应作品和当前剧集。"); return; }
            int season = binding?.Season ?? media.Season;
            long? number = media.Episode is int local ? (long)local + (binding?.EpisodeOffset ?? 0) : subject.MediaType == "movie" ? 1 : null;
            if (number is <= 0 || subject.NumEpisodes > 0 && number > subject.NumEpisodes) number = null;
            // Unknown episode totals remain unknown; expose only the identified current episode.
            var episodes = subject.NumEpisodes > 0 ? Enumerable.Range(1, subject.NumEpisodes).Select(n => new MalEpisode(n)).ToArray()
                : number is > 0 and <= int.MaxValue ? new[] { new MalEpisode((int)number.Value) } : [];
            var selected = episodes.FirstOrDefault(e => e.Number == chosen?.Number)
                ?? (media.Parsed.Ambiguous ? null : episodes.SingleOrDefault(e => e.Number == number));
            var watched = (await Client.SubjectAsync(subject.Id, true, token).ConfigureAwait(false)).MyListStatus?.NumEpisodesWatched ?? 0;
            lock (_lock)
            {
                if (generation != _generation || token.IsCancellationRequested || !Enabled) return;
                Subject = subject; Episodes = episodes; Selection = selected; Season = season; _watched = watched;
                if (selected != null) Store.Bind(media.Key, new(subject.Id, season, subject.Title, binding?.EpisodeOffset ?? 0));
                Status = selected == null ? "请选择当前剧集。" : ""; _resolving = false;
            }
            Changed?.Invoke(); UpdateProgress(_percent);
            try
            {
                string cover = await Client.CoverFileAsync(subject.Poster, token).ConfigureAwait(false);
                lock (_lock) { if (generation != _generation || token.IsCancellationRequested) return; _cover = cover; }
                Changed?.Invoke();
            }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or ArgumentException or NotSupportedException) { }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { SetStatus(generation, ErrorText(error)); }
        finally
        {
            bool changed;
            lock (_lock) { changed = generation == _generation; if (changed) _resolving = false; }
            if (changed) Changed?.Invoke();
        }
    }

    public void Bind(MalMedia media, int generation, MalSubject subject, int number)
    {
        if (number < 1 || subject.NumEpisodes > 0 && number > subject.NumEpisodes) throw new InvalidOperationException("观看集数超出该作品范围。");
        lock (_lock)
        {
            if (generation != _generation || media != Media) throw new InvalidOperationException("视频已切换，请重新选择当前剧集。");
            _file.Cancel(); _file.Dispose(); _file = new(); _generation++;
            Store.Bind(media.Key, new(subject.Id, media.Season, subject.Title, media.Episode is int local ? number - local : 0));
            Subject = subject; Episodes = subject.NumEpisodes > 0 ? Enumerable.Range(1, subject.NumEpisodes).Select(n => new MalEpisode(n)).ToArray() : [new(number)];
            Selection = Episodes.Single(e => e.Number == number); Season = media.Season;
            _resolving = _syncing = _attempted = false; Status = ""; _cover = ""; _watched = 0;
        }
        Changed?.Invoke(); Task pending = ResolveAsync();
    }
    public void SetSetting(string key, string value) { Store.SetSetting(key, value); Changed?.Invoke(); UpdateProgress(_percent); }
    public void UpdateProgress(double percent)
    {
        int generation; int id; CancellationToken token;
        lock (_lock)
        {
            _percent = percent;
            if (!Enabled || !double.IsFinite(percent) || Subject == null || Selection == null || Client.Account == null || _resolving || _syncing || _attempted
                || !Store.Settings.AutoSync || percent < Store.Settings.WatchedPercent) return;
            _attempted = true;
            generation = _generation; id = Selection.Number; token = _file.Token; _syncing = true;
        }
        Task pending = AutoSyncAsync(generation, id, token);
    }
    async Task AutoSyncAsync(int generation, int number, CancellationToken token)
    {
        await WriteProgressAsync(generation, number, true, token).ConfigureAwait(false);
    }
    async Task WriteProgressAsync(int generation, int number, bool automatic, CancellationToken token)
    {
        Changed?.Invoke();
        try
        {
            MalSubject subject;
            lock (_lock) { if (generation != _generation || !Enabled || Subject == null) return; subject = Subject; }
            var state = await Client.SetProgressAsync(subject, number, automatic, token).ConfigureAwait(false);
            lock (_lock) { if (generation != _generation) return; _watched = state.NumEpisodesWatched; Status = ""; }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { SetStatus(generation, ErrorText(error)); }
        finally { lock (_lock) { if (generation == _generation) _syncing = false; } Changed?.Invoke(); }
    }
    public async Task ChangeEpisodeAsync(int number, int state, bool through, int generation)
    {
        if (!through || state != 2) throw new ArgumentException("MyAnimeList 使用累计观看集数。");
        CancellationToken token;
        lock (_lock)
        {
            if (generation != _generation || !Enabled || Subject == null || _syncing || _resolving) return;
            if (!Episodes.Any(e => e.Number == number)) throw new InvalidOperationException("剧集不存在。");
            token = _file.Token; _syncing = true; _attempted = true;
        }
        await WriteProgressAsync(generation, number, false, token).ConfigureAwait(false);
    }
    public void SelectEpisode(int number, int generation)
    {
        lock (_lock)
        {
            if (generation != _generation || !Enabled || Subject == null || _syncing || _resolving) return;
            Selection = Episodes.FirstOrDefault(e => e.Number == number) ?? throw new InvalidOperationException("剧集不存在。"); _attempted = false;
            Store.Bind(Media.Key, new(Subject.Id, Season, Subject.Title, Media.Episode is int local ? number - local : 0));
        }
        Changed?.Invoke(); UpdateProgress(_percent);
    }
    public async Task AuthorizeAsync(Action<string> openBrowser)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        lock (_lock) { _authorization?.Cancel(); _authorization = attempt; _authorizing = true; Status = ""; }
        Changed?.Invoke(); bool entered = false;
        try
        {
            await _authorizationGate.WaitAsync(attempt.Token).ConfigureAwait(false); entered = true;
            var account = await Client.OAuth.AuthorizeAsync(openBrowser, attempt.Token).ConfigureAwait(false);
            await Client.ConnectAsync(account, attempt.Token).ConfigureAwait(false);
            await ResolveAsync().ConfigureAwait(false);
        }
        catch (Exception error) { lock (_lock) { if (_authorization == attempt) Status = ErrorText(error); } }
        finally
        {
            if (entered) _authorizationGate.Release();
            lock (_lock) { if (_authorization == attempt) { _authorization = null; _authorizing = false; } }
            Changed?.Invoke();
        }
    }
    public async Task DisconnectAsync()
    {
        lock (_lock) _authorization?.Cancel();
        Cancel(); await Client.DisconnectAsync(_lifetime.Token).ConfigureAwait(false);
    }
    public void ReportError(Exception error) => SetStatus(Generation, ErrorText(error));
    void SetStatus(int generation, string status) { lock (_lock) { if (generation != _generation) return; Status = status; } Changed?.Invoke(); }
    public static string ErrorText(Exception error) => error switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => "MyAnimeList 登录已失效，请重新授权。",
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => "MyAnimeList 请求过于频繁，请稍后重试。",
        HttpRequestException => "MyAnimeList 请求失败，请检查网络。",
        OperationCanceledException => "授权或请求已取消、超时，请重试。",
        InvalidOperationException => error.Message,
        _ => "MyAnimeList 操作失败，请重试。"
    };
}

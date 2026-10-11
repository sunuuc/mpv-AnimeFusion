using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MpvNet.Windows.Sync;

namespace MpvNet.Windows.Trakt;

public sealed class TraktPlayback
{
    public static TraktPlayback Current { get; private set; } = null!;
    public TraktStore Store { get; }
    public TraktClient Client { get; }
    public TraktMedia Media { get; private set; } = TraktMedia.Parse("", "", new Dictionary<string, string>());
    public TraktSubject? Subject { get; private set; }
    public IReadOnlyList<TraktEpisode> Episodes { get; private set; } = [];
    public TraktEpisode? Selection { get; private set; }
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
    HashSet<int> _watched = [];
    public int Generation { get { lock (_lock) return _generation; } }
    bool Enabled => SyncPreferences.Current.Enabled("trakt");

    public TraktPlayback(TraktStore store, TraktClient client)
    { Store = store; Client = client; Status = store.LoadError.Length > 0 ? store.LoadError : client.AccountError; }

    public static void Initialize()
    {
        var store = new TraktStore(Player.ConfigFolder,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mpv-AnimeFusion", "Trakt"));
        Current = new(store, new(store, new HttpClient { Timeout = TimeSpan.FromSeconds(20) }));
        Current._enabled = Current.Enabled;
        Current.Changed += () => Player.SetPropertyString("user-data/player_ui/trakt", Current.SnapshotJson());
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
                id = Subject.Ids.Trakt, title = Subject.Title, date = Subject.Year?.ToString() ?? "", score = Subject.Rating,
                season = Subject.Kind == "movie" ? (int?)null : Season, kind = Subject.Kind == "movie" ? "电影" : "剧集", cover = _cover,
                currentEpisode = Selection?.Ids.Trakt,
                episodes = Episodes.OrderBy(e => e.Number).Select(e => new
                { id = e.Ids.Trakt, number = e.Number, title = Subject.Kind == "movie" ? "正片" : e.Display,
                    kind = Subject.Kind == "movie" || e.Season > 0 ? 0 : 1, state = _watched.Contains(e.Ids.Trakt) ? 2 : 0 })
            }
        });
    }

    void Cancel()
    {
        lock (_lock)
        {
            _file.Cancel(); _file.Dispose(); _file = new(); _generation++;
            _resolving = _syncing = _attempted = false;
            Subject = null; Selection = null; Episodes = []; _watched = []; _cover = ""; Status = "";
        }
        Changed?.Invoke();
    }
    public void SetMedia(string title)
    {
        Cancel();
        var metadata = new Dictionary<string, string>();
        foreach (string provider in new[] { "imdb", "tmdb", "tvdb", "trakt" })
        {
            string value = Player.GetPropertyString("metadata/by-key/" + provider + "_id");
            if (value.Length > 0) metadata[provider] = value;
        }
        lock (_lock) { Media = TraktMedia.Parse(title, title.Length > 0 ? Player.GetPropertyString("path") : "", metadata); _mediaTitle = title; _percent = 0; }
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
        int generation; TraktMedia media; CancellationToken token; TraktEpisode? chosen;
        lock (_lock)
        {
            if (_resolving || !Enabled || Client.Account == null || Media.Title.Length == 0) return;
            generation = _generation; media = Media; token = _file.Token; chosen = Selection; _resolving = true; Status = "";
        }
        Changed?.Invoke();
        try
        {
            var binding = Store.FindBinding(media.Key);
            TraktSubject? subject = binding == null ? await Client.MatchAsync(media, token).ConfigureAwait(false)
                : (await Client.SubjectAsync(binding.Id, binding.Kind, token).ConfigureAwait(false)) with { Kind = binding.Kind };
            if (subject == null) { SetStatus(generation, "请选择对应作品和当前剧集。"); return; }
            int season = binding?.Season ?? media.Season;
            TraktEpisode[] episodes;
            if (subject.Kind == "movie") episodes = [new(0, 1, "正片", subject.Ids)];
            else
            {
                var seasons = await Client.SeasonsAsync(subject.Ids.Trakt, token).ConfigureAwait(false);
                // Unspecified seasons are safe only if the catalogue contains one regular season.
                if (binding == null && !media.Parsed.HasSeason && seasons.Count(s => s.Number > 0) != 1)
                { SetStatus(generation, "请选择当前季度和剧集。"); return; }
                if (binding == null && !media.Parsed.HasSeason) season = seasons.Single(s => s.Number > 0).Number;
                if (!seasons.Any(s => s.Number == season)) { SetStatus(generation, "未找到对应季度，请选择条目。"); return; }
                episodes = await Client.EpisodesAsync(subject.Ids.Trakt, season, token).ConfigureAwait(false);
            }
            long? number = media.Episode is int local ? (long)local + (binding?.EpisodeOffset ?? 0) : null;
            var selected = episodes.FirstOrDefault(e => e.Ids.Trakt == chosen?.Ids.Trakt)
                ?? (media.Parsed.Ambiguous ? null : subject.Kind == "movie" ? episodes[0] : episodes.SingleOrDefault(e => e.Season == season && e.Number == number));
            var watched = await Client.WatchedAsync(subject, episodes, season, token).ConfigureAwait(false);
            lock (_lock)
            {
                if (generation != _generation || token.IsCancellationRequested || !Enabled) return;
                Subject = subject; Episodes = episodes; Selection = selected; Season = season; _watched = watched;
                if (selected != null) Store.Bind(media.Key, new(subject.Ids.Trakt, subject.Kind, season, subject.Title, binding?.EpisodeOffset ?? 0));
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

    public void Bind(TraktMedia media, int generation, TraktSubject subject, TraktEpisode episode, IReadOnlyList<TraktEpisode> episodes)
    {
        lock (_lock)
        {
            if (generation != _generation || media != Media) throw new InvalidOperationException("视频已切换，请重新选择当前剧集。");
            if (!episodes.Any(e => e.Ids.Trakt == episode.Ids.Trakt && e.Season == episode.Season)) throw new InvalidOperationException("剧集不属于所选季度。");
            _file.Cancel(); _file.Dispose(); _file = new(); _generation++;
            Store.Bind(media.Key, new(subject.Ids.Trakt, subject.Kind, episode.Season, subject.Title,
                media.Episode is int number && subject.Kind == "show" ? episode.Number - number : 0));
            Subject = subject; Episodes = episodes; Selection = episode; Season = episode.Season;
            _resolving = _syncing = _attempted = false; Status = ""; _cover = ""; _watched.Clear();
        }
        Changed?.Invoke(); Task pending = ResolveAsync();
    }
    public void SetSetting(string key, string value) { Store.SetSetting(key, value); Changed?.Invoke(); UpdateProgress(_percent); }
    public void UpdateProgress(double percent)
    {
        int generation; int id; string kind; CancellationToken token;
        lock (_lock)
        {
            _percent = percent;
            if (!Enabled || !double.IsFinite(percent) || Subject == null || Selection == null || Client.Account == null || _resolving || _syncing || _attempted
                || !Store.Settings.AutoSync || percent < Store.Settings.WatchedPercent) return;
            _attempted = true;
            generation = _generation; id = Selection.Ids.Trakt; kind = Subject.Kind; token = _file.Token; _syncing = true;
        }
        Task pending = AutoSyncAsync(generation, id, kind, token);
    }
    async Task AutoSyncAsync(int generation, int id, string kind, CancellationToken token)
    {
        Changed?.Invoke();
        try
        {
            // Re-read the current account state at the threshold, including edits made on Trakt during playback.
            TraktSubject subject; IReadOnlyList<TraktEpisode> episodes; int season;
            lock (_lock) { if (generation != _generation || Subject == null) return; subject = Subject; episodes = Episodes; season = Season; }
            var watched = await Client.WatchedAsync(subject, episodes, season, token).ConfigureAwait(false);
            if (!watched.Contains(id)) await Client.SetWatchedAsync(kind, [id], true, token).ConfigureAwait(false);
            lock (_lock) { if (generation != _generation) return; _watched = watched; _watched.Add(id); Status = ""; }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { SetStatus(generation, ErrorText(error)); }
        finally { lock (_lock) { if (generation == _generation) _syncing = false; } Changed?.Invoke(); }
    }
    public async Task ChangeEpisodeAsync(int id, int state, bool through, int generation)
    {
        if (state is not (0 or 2)) throw new ArgumentException("无效的观看状态。");
        int[] ids; string kind; CancellationToken token; TraktSubject subject; IReadOnlyList<TraktEpisode> episodes; int season;
        lock (_lock)
        {
            if (generation != _generation || Subject == null || _syncing || _resolving) return;
            var episode = Episodes.FirstOrDefault(e => e.Ids.Trakt == id) ?? throw new InvalidOperationException("剧集不存在。");
            ids = through ? Episodes.Where(e => e.Season == episode.Season && e.Number <= episode.Number).Select(e => e.Ids.Trakt).ToArray() : [id];
            kind = Subject.Kind; token = _file.Token; _syncing = true;
            subject = Subject; episodes = Episodes; season = Season;
            if (Selection != null && ids.Contains(Selection.Ids.Trakt)) _attempted = true;
        }
        Changed?.Invoke();
        try
        {
            var watched = await Client.WatchedAsync(subject, episodes, season, token).ConfigureAwait(false);
            // Adding history isn't idempotent; don't repeat already watched episodes.
            if (state == 2) ids = ids.Where(i => !watched.Contains(i)).ToArray();
            if (ids.Length > 0) await Client.SetWatchedAsync(kind, ids, state == 2, token).ConfigureAwait(false);
            lock (_lock)
            {
                if (generation != _generation) return;
                _watched = watched;
                foreach (int ep in ids) { if (state == 2) _watched.Add(ep); else _watched.Remove(ep); }
                Status = "";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { SetStatus(generation, ErrorText(error)); }
        finally { lock (_lock) { if (generation == _generation) _syncing = false; } Changed?.Invoke(); }
    }
    public void SelectEpisode(int id, int generation)
    {
        lock (_lock)
        {
            if (generation != _generation || Subject == null || _syncing || _resolving) return;
            Selection = Episodes.FirstOrDefault(e => e.Ids.Trakt == id) ?? throw new InvalidOperationException("剧集不存在。"); _attempted = false;
            Store.Bind(Media.Key, new(Subject.Ids.Trakt, Subject.Kind, Season, Subject.Title,
                Media.Episode is int number && Subject.Kind == "show" ? Selection.Number - number : 0));
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
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => "Trakt 登录已失效，请重新授权。",
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => "Trakt 请求过于频繁，请稍后重试。",
        HttpRequestException => "Trakt 请求失败，请检查网络。",
        OperationCanceledException => "授权或请求已取消、超时，请重试。",
        InvalidOperationException => error.Message,
        _ => "Trakt 操作失败，请重试。"
    };
}

using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BangumiNet.Api.V0.Models;
using Microsoft.Kiota.Abstractions;

namespace MpvNet.Windows.Bangumi;

public sealed record BangumiSelection(int SubjectId, int EpisodeId, string SubjectTitle, string EpisodeTitle);

public sealed class BangumiPlayback
{
    public static BangumiPlayback Current { get; private set; } = null!;
    public BangumiStore Store { get; }
    public BangumiClient Client { get; }
    public BangumiMedia Media { get; private set; } = BangumiMedia.Parse("");
    public BangumiSelection? Selection { get; private set; }
    public Subject? Subject { get; private set; }
    public IReadOnlyList<Episode> Episodes { get; private set; } = [];
    public int CollectionType { get; private set; }
    public string Status { get; private set; } = "";
    public event Action? Changed;
    readonly object _lock = new();
    CancellationTokenSource _file = new();
    readonly CancellationTokenSource _accountLifetime = new();
    int _generation;
    int _resolvingGeneration = -1;
    bool _attempted;
    bool _collectAttempted;
    bool _authorizing;
    bool _resolving;
    bool _fileLoaded;
    string _mediaTitle = "";
    CancellationTokenSource? _authorization;
    readonly SemaphoreSlim _authorizationGate = new(1, 1);
    string _avatar = "", _cover = "";
    bool _syncing;
    double _percent;
    public int Generation { get { lock (_lock) return _generation; } }
    readonly Dictionary<int, IReadOnlyList<Episode>> _episodes = new();
    readonly Dictionary<int, Subject> _subjects = new();
    readonly Dictionary<int, int> _episodeStates = new();

    public BangumiPlayback(BangumiStore store, BangumiClient client)
    {
        Store = store;
        Client = client;
        Status = store.LoadError.Length > 0 ? store.LoadError : client.AccountError;
    }

    public static void Initialize()
    {
        var store = new BangumiStore(Player.ConfigFolder,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mpv-AnimeFusion", "Bangumi"));
        Current = new(store, new(store, new HttpClient { Timeout = TimeSpan.FromSeconds(20) }));
        Current.Changed += () => Player.SetPropertyString("user-data/player_ui/bangumi", Current.SnapshotJson());
        Current.Client.AccountChanged += () => { Task pending = Current.PublishAccountAsync(); };
        Task pendingAccount = Current.PublishAccountAsync();
        Player.StartFile += () =>
        {
            lock (Current._lock) Current._fileLoaded = false;
            Current.SetMedia("");
        };
        Player.FileLoaded += () =>
        {
            Current.SetMedia(Player.GetPropertyString("media-title"));
            lock (Current._lock) Current._fileLoaded = true;
        };
        // Native property events: no render timer, polling loop or network request per frame.
        Player.ObservePropertyString("media-title", Current.UpdateMediaTitle);
        Player.ObservePropertyDouble("percent-pos", Current.UpdateProgress);
        Player.Shutdown += () => { Current.Changed = null; Current._accountLifetime.Cancel(); Current.Cancel(); };
    }

    async Task PublishAccountAsync()
    {
        var account = Client.Account;
        lock (_lock) _avatar = "";
        void Publish() => Changed?.Invoke();
        Publish();
        if (account == null) return;
        try
        {
            string avatar = await Client.AvatarFileAsync(_accountLifetime.Token).ConfigureAwait(false);
            lock (_lock) { if (Client.Account != account) return; _avatar = avatar; }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or ArgumentException or OperationCanceledException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException or NotSupportedException) { }
        if (!_accountLifetime.IsCancellationRequested && Client.Account == account) Publish();
    }

    public void SetWatchedPercent(int percent)
    {
        Store.SetWatchedPercent(percent);
        Changed?.Invoke();
        UpdateProgress(_percent);
    }

    public string SnapshotJson()
    {
        lock (_lock) return System.Text.Json.JsonSerializer.Serialize(new
        {
            connected = Client.Account != null, username = Client.Account?.Username ?? "", avatar = _avatar,
            generation = _generation, busy = _syncing || _authorizing, authorizing = _authorizing, status = Status,
            resolving = _resolving,
            settings = new { autoCollect = Store.Settings.AutoCollect, collectPercent = Store.Settings.CollectPercent,
                autoSync = Store.Settings.AutoSync, watchedPercent = Store.Settings.WatchedPercent },
            subject = Subject == null ? null : new
            {
                id = Subject.Id, title = DisplayTitle(Subject), name = Subject.Name ?? "", date = Subject.Date ?? "",
                score = Subject.Rating?.Score, season = BangumiMedia.IsMovie(Subject) ? (int?)null : SubjectSeason(Subject),
                kind = BangumiMedia.IsMovie(Subject) ? "电影" : "剧集", cover = _cover, collectionType = CollectionType,
                currentEpisode = Selection?.EpisodeId,
                episodes = Episodes.OrderBy(e => e.Type).ThenBy(e => e.Sort).Select(e => new
                { id = e.Id, number = e.Sort, title = DisplayEpisode(e, BangumiMedia.IsMovie(Subject)), kind = e.Type, state = _episodeStates.GetValueOrDefault(e.Id ?? 0) })
            }
        });
    }

    public void SetSetting(string key, string value)
    {
        switch (key)
        {
            case "auto-collect": Store.SetAutoCollect(value == "yes"); break;
            case "auto-sync": Store.SetAutoSync(value == "yes"); break;
            case "collect-percent": Store.SetCollectPercent(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)); break;
            case "watched-percent": Store.SetWatchedPercent(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)); break;
            default: throw new ArgumentException("未知同步设置。", nameof(key));
        }
        Changed?.Invoke(); UpdateProgress(_percent);
    }

    public void Cancel()
    {
        lock (_lock)
        {
            _file.Cancel();
            _file.Dispose();
            _file = new();
            _generation++;
            Selection = null;
            Subject = null; Episodes = []; CollectionType = 0; _episodeStates.Clear(); _cover = "";
            _collectAttempted = false;
            _attempted = false;
            _syncing = false;
            _resolving = false;
            Status = "";
        }
        Changed?.Invoke();
    }

    public void SetMedia(string title)
    {
        Cancel();
        lock (_lock)
        {
            _attempted = false;
            _syncing = false;
            _percent = 0;
            Selection = null;
            Media = BangumiMedia.Parse(title);
            _mediaTitle = title;
            Status = "";
        }
        Changed?.Invoke();
        if (Client.Account != null && Media.Title.Length > 0)
        {
            Task pending = ResolveAsync();
        }
    }

    void UpdateMediaTitle(string title)
    {
        lock (_lock)
        {
            if (!_fileLoaded || title.Length == 0 || title == _mediaTitle) return;
        }
        SetMedia(title);
    }

    public void RetryResolve()
    {
        lock (_lock)
        {
            if (_resolving || Client.Account == null) return;
            _resolvingGeneration = -1;
            Status = "";
        }
        Changed?.Invoke();
        Task pending = ResolveAsync();
    }

    public static async Task<T> RetryReadAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken token)
    {
        for (int retry = 0; ; retry++)
        {
            token.ThrowIfCancellationRequested();
            try { return await read(token).ConfigureAwait(false); }
            catch (Exception error) when (retry < 3 && !token.IsCancellationRequested &&
                (error is HttpRequestException or TaskCanceledException ||
                 error is ApiException api && (api.ResponseStatusCode is 0 or 408 or 429 || api.ResponseStatusCode >= 500)))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300 * (1 << retry)), token).ConfigureAwait(false);
            }
        }
    }

    public async Task ResolveAsync()
    {
        int generation;
        BangumiMedia media;
        CancellationToken cancellationToken;
        lock (_lock) { generation = _generation; media = Media; cancellationToken = _file.Token; }
        if (media.Title.Length == 0 || Client.Account == null) return;
        lock (_lock)
        {
            if (generation != _generation || _resolvingGeneration == generation) return;
            _resolvingGeneration = generation;
            _resolving = true;
        }
        try
        {
            Changed?.Invoke();
            var binding = Store.FindBinding(media.Key);
            Subject? subject = null;
            if (binding == null)
            {
                var subjects = await RetryReadAsync(token => Client.SearchAsync(media.Title, token), cancellationToken).ConfigureAwait(false);
                subject = media.MatchSubject(subjects);
                if (subject == null)
                {
                    SetStatus(generation, media.EpisodeNumber == null && !media.HasSeason
                        ? "请选择电影条目与正片。" : "请选择本季条目与当前剧集。");
                    return;
                }
                binding = new(subject.Id!.Value, DisplayTitle(subject));
            }
            if (subject == null)
            {
                lock (_lock) _subjects.TryGetValue(binding.SubjectId, out subject);
                subject ??= await RetryReadAsync(token => Client.SubjectAsync(binding.SubjectId, token), cancellationToken).ConfigureAwait(false);
            }
            IReadOnlyList<Episode>? episodes;
            lock (_lock) _episodes.TryGetValue(binding.SubjectId, out episodes);
            episodes ??= await RetryReadAsync(token => Client.EpisodesAsync(binding.SubjectId, token), cancellationToken).ConfigureAwait(false);
            int? selectedId;
            lock (_lock) selectedId = generation == _generation ? Selection?.EpisodeId : null;
            var episode = episodes.FirstOrDefault(e => e.Id == selectedId)
                ?? BangumiMedia.MatchEpisode(episodes, media.EpisodeNumber, SubjectSeason(subject), BangumiMedia.IsMovie(subject));
            int collectionType = await RetryReadAsync(token => Client.CollectionTypeAsync(binding.SubjectId, token), cancellationToken).ConfigureAwait(false);
            var states = collectionType == 0 ? [] : await RetryReadAsync(token => Client.EpisodeStatesAsync(binding.SubjectId, token), cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                if (generation != _generation || cancellationToken.IsCancellationRequested) return;
                _episodes[binding.SubjectId] = episodes;
                _subjects[binding.SubjectId] = subject;
                Subject = subject; Episodes = episodes; CollectionType = collectionType;
                _episodeStates.Clear();
                foreach (var item in states) if (item.Episode?.Id is int id) _episodeStates[id] = item.Type ?? 0;
                Selection = episode == null ? null : new(binding.SubjectId, episode.Id!.Value, DisplayTitle(subject), DisplayEpisode(episode, BangumiMedia.IsMovie(subject)));
                Status = episode == null ? (BangumiMedia.IsMovie(subject) ? "请选择正片。" : "请选择当前剧集。") : "";
                _resolving = false;
            }
            Changed?.Invoke();
            UpdateProgress(_percent);
            try
            {
                string cover = await Client.CoverFileAsync(subject.Images?.Large ?? subject.Images?.Common ?? "", cancellationToken).ConfigureAwait(false);
                lock (_lock) { if (generation != _generation || cancellationToken.IsCancellationRequested) return; _cover = cover; }
                Changed?.Invoke();
            }
            catch (Exception e) when (e is HttpRequestException or IOException or ArgumentException or OperationCanceledException
                or System.Runtime.InteropServices.ExternalException or NotSupportedException) { }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { SetStatus(generation, ErrorText(e)); }
        finally
        {
            bool changed;
            lock (_lock)
            {
                changed = generation == _generation && _resolving;
                if (changed) _resolving = false;
            }
            if (changed) Changed?.Invoke();
        }
    }

    public void Bind(BangumiMedia media, int generation, Subject subject, Episode episode, IReadOnlyList<Episode> episodes)
    {
        lock (_lock)
        {
            if (generation != _generation || Media != media) throw new InvalidOperationException("视频已切换，请重新选择当前剧集。");
            _file.Cancel();
            _file.Dispose();
            _file = new();
            _generation++;
            _syncing = false;
            _resolving = false;
            Store.Bind(media.Key, new(subject.Id!.Value, DisplayTitle(subject)));
            _subjects[subject.Id!.Value] = subject;
            _episodes[subject.Id!.Value] = episodes;
            Selection = new(subject.Id.Value, episode.Id!.Value, DisplayTitle(subject), DisplayEpisode(episode, BangumiMedia.IsMovie(subject)));
            _attempted = false;
            Subject = subject; Episodes = episodes; CollectionType = 0; _episodeStates.Clear(); _cover = "";
            Status = ""; _collectAttempted = false;
        }
        Changed?.Invoke();
        Task pending = ResolveAsync();
    }

    // Native progress events trigger one write per episode at the selected threshold.
    public void UpdateProgress(double percent)
    {
        BangumiSelection selection;
        bool collect, watch;
        CancellationToken cancellationToken;
        int generation;
        lock (_lock)
        {
            _percent = percent;
            if (!double.IsFinite(percent) || Selection == null || Client.Account == null || _syncing) return;
            collect = Store.Settings.AutoCollect && percent >= Store.Settings.CollectPercent && !_collectAttempted;
            watch = Store.Settings.AutoSync && percent >= Store.Settings.WatchedPercent && !_attempted && CollectionType != 0;
            if (!collect && !watch) return;
            if (collect) _collectAttempted = true;
            if (watch) _attempted = true;
            _syncing = true;
            selection = Selection;
            generation = _generation;
            cancellationToken = _file.Token;
        }
        Task pending = AutoSyncAsync(selection, collect, watch, generation, cancellationToken);
    }

    async Task AutoSyncAsync(BangumiSelection selection, bool collect, bool watch, int generation, CancellationToken token)
    {
        try
        {
            if (collect)
            {
                int type = await Client.CollectAsync(selection.SubjectId, 3, true, token).ConfigureAwait(false);
                lock (_lock) { if (generation != _generation) return; CollectionType = type; }
                SetStatus(generation, "");
            }
            if (watch)
            {
                await Client.MarkWatchedAsync(selection.SubjectId, selection.EpisodeId, token).ConfigureAwait(false);
                lock (_lock) { if (generation != _generation) return; _episodeStates[selection.EpisodeId] = 2; }
                SetStatus(generation, "");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { SetStatus(generation, ErrorText(e)); }
        finally { lock (_lock) { if (generation == _generation) _syncing = false; } Changed?.Invoke(); }
        if (generation == Generation) UpdateProgress(_percent);
    }

    public async Task AuthorizeAsync(Action<string> openBrowser)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_accountLifetime.Token);
        lock (_lock)
        {
            _authorization?.Cancel();
            _authorization = attempt;
            _authorizing = true;
            Status = "";
        }
        Changed?.Invoke();
        bool entered = false;
        try
        {
            await _authorizationGate.WaitAsync(attempt.Token).ConfigureAwait(false);
            entered = true;
            var account = await Client.OAuth.AuthorizeAsync(openBrowser, attempt.Token).ConfigureAwait(false);
            await Client.ConnectAsync(account, attempt.Token).ConfigureAwait(false);
            lock (_lock) _resolvingGeneration = -1;
            await ResolveAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            lock (_lock) { if (_authorization == attempt) Status = ErrorText(e); }
        }
        finally
        {
            if (entered) _authorizationGate.Release();
            lock (_lock)
            {
                if (_authorization == attempt) { _authorization = null; _authorizing = false; }
            }
            Changed?.Invoke();
        }
    }

    public async Task DisconnectAsync()
    {
        Cancel(); await Client.DisconnectAsync(_accountLifetime.Token).ConfigureAwait(false);
    }

    public async Task ChangeCollectionAsync(int type, int generation)
    {
        Subject subject; CancellationToken token;
        lock (_lock)
        {
            if (generation != _generation || Subject == null || _syncing) return;
            subject = Subject; token = _file.Token; _syncing = true;
        }
        Changed?.Invoke();
        try
        {
            int updated = await Client.CollectAsync(subject.Id!.Value, type, false, token).ConfigureAwait(false);
            lock (_lock) { if (generation != _generation) return; CollectionType = updated; _collectAttempted = true; }
            SetStatus(generation, "");
        }
        catch (Exception e) { SetStatus(generation, ErrorText(e)); }
        finally { lock (_lock) { if (generation == _generation) _syncing = false; } Changed?.Invoke(); }
    }

    public async Task ChangeEpisodeAsync(int episodeId, int type, bool through, int generation)
    {
        int subjectId; int[] ids; CancellationToken token;
        lock (_lock)
        {
            if (generation != _generation || Subject == null || _syncing) return;
            var episode = Episodes.FirstOrDefault(e => e.Id == episodeId) ?? throw new InvalidOperationException("剧集不存在。");
            if (CollectionType == 0) throw new InvalidOperationException("请先收藏，再修改剧集进度。");
            if (through && episode.Type != 0) throw new InvalidOperationException("“看到”仅用于正篇剧集。");
            var main = Episodes.Where(e => e.Type == 0).OrderBy(e => e.Sort).ToList();
            ids = through ? main.Take(main.FindIndex(e => e.Id == episodeId) + 1).Select(e => e.Id!.Value).ToArray() : [episodeId];
            subjectId = Subject.Id!.Value; token = _file.Token; _syncing = true;
        }
        Changed?.Invoke();
        try
        {
            await Client.SetEpisodeStateAsync(subjectId, ids, type, token).ConfigureAwait(false);
            lock (_lock)
            {
                if (generation != _generation) return;
                foreach (int id in ids) _episodeStates[id] = type;
                if (Selection != null && ids.Contains(Selection.EpisodeId)) _attempted = true;
            }
            SetStatus(generation, "");
        }
        catch (Exception e) { SetStatus(generation, ErrorText(e)); }
        finally { lock (_lock) { if (generation == _generation) _syncing = false; } Changed?.Invoke(); }
    }

    public void SelectEpisode(int episodeId, int generation)
    {
        lock (_lock)
        {
            if (generation != _generation || Subject == null || _syncing) return;
            var episode = Episodes.FirstOrDefault(e => e.Id == episodeId) ?? throw new InvalidOperationException("剧集不存在。");
            Selection = new(Subject.Id!.Value, episodeId, DisplayTitle(Subject), DisplayEpisode(episode, BangumiMedia.IsMovie(Subject)));
            _attempted = false;
        }
        Changed?.Invoke(); UpdateProgress(_percent);
    }

    public void ReportError(Exception error) => SetStatus(Generation, ErrorText(error));

    void SetStatus(int generation, string status)
    {
        lock (_lock) { if (generation != _generation) return; Status = status; }
        Changed?.Invoke();
    }

    public static string DisplayTitle(Subject subject) => !string.IsNullOrWhiteSpace(subject.NameCn) ? subject.NameCn : subject.Name ?? "";
    int SubjectSeason(Subject subject)
    {
        int season = Math.Max(BangumiMedia.Parse(subject.NameCn ?? "").Season, BangumiMedia.Parse(subject.Name ?? "").Season);
        return season > 1 ? season : Media.Season;
    }
    public static string DisplayEpisode(Episode episode, bool movie = false) => movie && episode.Type == 0 ? "正片" : (episode.Type == 0 ? $"第 {episode.Sort} 集" : $"SP {episode.Sort}") + " · "
        + (!string.IsNullOrWhiteSpace(episode.NameCn) ? episode.NameCn : episode.Name ?? "");
    public static string ErrorText(Exception error) => error switch
    {
        ApiException api when api.ResponseStatusCode == 401 => "Bangumi 登录已失效，请重新登录。",
        ApiException api => $"Bangumi 请求失败（HTTP {api.ResponseStatusCode}）。",
        HttpRequestException => "Bangumi 请求失败，请检查网络。",
        OperationCanceledException => "请求已取消或超时。",
        InvalidOperationException => error.Message,
        _ => "Bangumi 操作失败。"
    };
}

using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Globalization;

namespace MpvNet.Windows.Subtitles;

public sealed class SubtitleSearch(params ISubtitleClient[] clients) : IDisposable
{
    sealed class Request(CancellationTokenSource cancellation, IProgress<SubtitleSearchResult> progress)
    {
        readonly object _lock = new();
        readonly HashSet<CancellationTokenSource> _pages = [];
        TaskCompletionSource? _resume;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public CancellationToken Token { get; } = cancellation.Token;
        public Task<SubtitleSearchResult> Result { get; set; } = null!;
        public SubtitleSearchResult? State { get; private set; }

        public void Initialize(SubtitleSearchResult state)
        {
            lock (_lock)
            {
                State = state;
                State.Paused = _resume != null;
            }
            progress.Report(state);
        }

        public bool SetPaused(bool paused)
        {
            lock (_lock)
            {
                if (Token.IsCancellationRequested || State?.Complete == true || Result?.IsCompleted == true) return false;
                if (paused == (_resume != null)) return true;
                if (paused)
                {
                    _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    // The page token also cancels a request still waiting for the provider's rate limit.
                    foreach (var page in _pages.ToArray()) page.Cancel();
                }
                else
                {
                    var resume = _resume;
                    _resume = null;
                    resume?.TrySetResult();
                }
                if (State != null) State.Paused = paused;
            }
            if (State != null) progress.Report(State);
            return true;
        }

        public async Task<CancellationTokenSource> BeginPageAsync()
        {
            while (true)
            {
                Task wait;
                lock (_lock)
                {
                    Token.ThrowIfCancellationRequested();
                    if (_resume == null)
                    {
                        var page = CancellationTokenSource.CreateLinkedTokenSource(Token);
                        _pages.Add(page);
                        return page;
                    }
                    wait = _resume.Task;
                }
                await wait.WaitAsync(Token);
            }
        }

        public void AcceptResults(CancellationTokenSource page, IReadOnlyList<SubtitleResult> results)
        {
            SubtitleSearchResult state;
            lock (_lock)
            {
                if (Token.IsCancellationRequested || State?.Complete != false || !_pages.Contains(page)
                    || page.IsCancellationRequested || State.Add(results) == 0) return;
                state = State;
            }
            progress.Report(state);
        }

        public bool AcceptPage(CancellationTokenSource page, SubtitlePage response, HashSet<string> pages, out bool more)
        {
            lock (_lock)
            {
                Token.ThrowIfCancellationRequested();
                more = true;
                if (page.IsCancellationRequested || !_pages.Remove(page)) return false;
                State!.Add(response.Results);
                // Partial results may already be present, including those retained across a pause.
                // Detect repeated pages from their own contents rather than the shared result count.
                string key = response.PageKey.Length > 0 ? "page:" + response.PageKey : "results:" + string.Join('\0',
                    response.Results.Select(item => ((int)item.Source) + ":" + item.Id).Distinct().Order());
                more = response.HasMore && (response.PageKey.Length > 0 || response.Results.Count > 0) && pages.Add(key);
                return true;
            }
        }

        public void EndPage(CancellationTokenSource page)
        {
            lock (_lock) { _pages.Remove(page); page.Dispose(); }
        }

        public void Complete()
        {
            lock (_lock)
            {
                Token.ThrowIfCancellationRequested();
                State!.Complete = true;
                State.Paused = false;
                _resume?.TrySetResult();
                _resume = null;
            }
            progress.Report(State);
        }
    }
    sealed class PageProgress(Request request, CancellationTokenSource page) : IProgress<IReadOnlyList<SubtitleResult>>
    {
        public void Report(IReadOnlyList<SubtitleResult> value) => request.AcceptResults(page, value);
    }
    readonly object _lock = new();
    readonly Dictionary<string, Request> _searches = new(StringComparer.OrdinalIgnoreCase);

    public Task<SubtitleSearchResult> SearchAsync(string keyword, Func<CancellationToken, Task<SubtitleQuery>> query,
        IProgress<SubtitleSearchResult> progress, CancellationToken token, bool refresh = false,
        IReadOnlyCollection<SubtitleSource>? sources = null, string language = "", int? season = null, int? episode = null)
    {
        language = Language(language);
        string key = Key(keyword, language, season, episode);
        lock (_lock)
        {
            _searches.TryGetValue(key, out var cached);
            if (cached != null)
            {
                if (!refresh && !cached.Cancellation.IsCancellationRequested && !cached.Result.IsCanceled) return cached.Result;
                cached.Cancellation.Cancel();
                cached.Cancellation.Dispose();
            }
            var request = new Request(CancellationTokenSource.CreateLinkedTokenSource(token), progress);
            _searches[key] = request;
            var selected = clients.Where(client => sources == null || sources.Contains(client.Source)).ToArray();
            request.Result = LoadAsync(query, progress, selected, cached?.State, request, language, season, episode);
            return request.Result;
        }
    }

    public bool SetPaused(string keyword, bool paused, string language = "", int? season = null, int? episode = null)
    {
        string key = Key(keyword, Language(language), season, episode);
        lock (_lock) return _searches.TryGetValue(key, out var request) && request.SetPaused(paused);
    }

    static string Language(string value) => string.IsNullOrWhiteSpace(value) ? "" : SubtitleLanguage.Find(value)?.Code ?? "other";
    static string Key(string keyword, string language, int? season, int? episode) => keyword.Normalize(NormalizationForm.FormKC).Trim()
        + "\0" + language + "\0" + season?.ToString(CultureInfo.InvariantCulture) + "\0" + episode?.ToString(CultureInfo.InvariantCulture);

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var request in _searches.Values)
            { request.Cancellation.Cancel(); request.Cancellation.Dispose(); }
            _searches.Clear();
        }
    }

    async Task<SubtitleSearchResult> LoadAsync(Func<CancellationToken, Task<SubtitleQuery>> createQuery,
        IProgress<SubtitleSearchResult> progress, IReadOnlyList<ISubtitleClient> selected, SubtitleSearchResult? previous,
        Request request, string language, int? season, int? episode)
    {
        var token = request.Token;
        var result = new SubtitleSearchResult((await createQuery(token)) with
            { Language = language, RequestSeason = season, RequestEpisode = episode });
        token.ThrowIfCancellationRequested();
        if (previous != null)
        {
            var replaced = selected.Select(client => client.Source).ToHashSet();
            result.Add(previous.Results.Where(item => !replaced.Contains(item.Source)).ToArray());
            foreach (var error in previous.Errors.Where(error => !replaced.Contains(error.Key)))
                result.Fail(error.Key, error.Value);
        }
        request.Initialize(result);
        async Task LoadSourceAsync(ISubtitleClient client)
        {
            if (client is AssrtClient { Configured: false }) return;
            try
            {
                client = client.BeginSearch();
                var pages = new HashSet<string>();
                // Commit completed pages once; pausing only retries the unfinished page.
                for (int page = 1; ;)
                {
                    var attempt = await request.BeginPageAsync();
                    try
                    {
                        var response = await client.SearchAsync(result.Query, page, attempt.Token, new PageProgress(request, attempt));
                        if (!request.AcceptPage(attempt, response, pages, out bool more)) continue;
                        page++;
                        progress.Report(result);
                        if (!more) break;
                    }
                    catch (Exception) when (attempt.IsCancellationRequested && !token.IsCancellationRequested) { }
                    finally { request.EndPage(attempt); }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { token.ThrowIfCancellationRequested(); result.Fail(client.Source, error); }
        }
        await Task.WhenAll(selected.Select(LoadSourceAsync));
        request.Complete();
        return result;
    }
}

public sealed class SubtitleSearchResult(SubtitleQuery query)
{
    readonly object _lock = new();
    readonly Dictionary<(SubtitleSource, string), SubtitleResult> _results = new();
    readonly Dictionary<SubtitleSource, Exception> _errors = new();
    public SubtitleQuery Query { get; } = query;
    public bool Complete { get; internal set; }
    public bool Paused { get; internal set; }
    public IReadOnlyList<SubtitleResult> Results { get { lock (_lock) return _results.Values.ToArray(); } }
    public IReadOnlyDictionary<SubtitleSource, Exception> Errors { get { lock (_lock) return new Dictionary<SubtitleSource, Exception>(_errors); } }
    internal int Add(IReadOnlyList<SubtitleResult> items)
    {
        lock (_lock)
        {
            int count = _results.Count;
            foreach (var item in items) _results.TryAdd((item.Source, item.Id), item);
            return _results.Count - count;
        }
    }
    internal void Fail(SubtitleSource source, Exception error) { lock (_lock) _errors[source] = error; }
}

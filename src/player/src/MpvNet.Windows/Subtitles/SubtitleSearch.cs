using System.Threading;
using System.Threading.Tasks;
using System.Text;

namespace MpvNet.Windows.Subtitles;

public sealed class SubtitleSearch(AssrtClient assrt) : IDisposable
{
    sealed record Request(CancellationTokenSource Cancellation, Task<SubtitleSearchResult> Result);
    readonly Dictionary<string, Request> _searches = new(StringComparer.OrdinalIgnoreCase);

    public Task<SubtitleSearchResult> SearchAsync(string keyword, Func<CancellationToken, Task<SubtitleQuery>> query,
        IProgress<SubtitleSearchResult> progress, CancellationToken token, bool refresh = false)
    {
        string key = keyword.Normalize(NormalizationForm.FormKC).Trim();
        if (_searches.TryGetValue(key, out var cached))
        {
            if (!refresh) return cached.Result;
            cached.Cancellation.Cancel();
            cached.Cancellation.Dispose();
        }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var result = LoadAsync(query, progress, cancellation.Token);
        _searches[key] = new(cancellation, result);
        return result;
    }

    public void Dispose()
    {
        foreach (var request in _searches.Values)
        { request.Cancellation.Cancel(); request.Cancellation.Dispose(); }
        _searches.Clear();
    }

    async Task<SubtitleSearchResult> LoadAsync(Func<CancellationToken, Task<SubtitleQuery>> createQuery,
        IProgress<SubtitleSearchResult> progress, CancellationToken token)
    {
        var result = new SubtitleSearchResult(await createQuery(token));
        progress.Report(result);
        try
        {
            // Fetch each page once. Filters operate on this stored result, never on API parameters.
            for (int page = 1; ; page++)
            {
                token.ThrowIfCancellationRequested();
                var response = await assrt.SearchAsync(result.Query, page, token);
                int added = result.Add(response.Results);
                progress.Report(result);
                if (!response.HasMore || added == 0) break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { result.Fail(SubtitleSource.Assrt, error); }
        result.Complete = true;
        progress.Report(result);
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

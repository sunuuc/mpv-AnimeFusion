using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BangumiNet.Api.V0.Models;
using MpvNet.Windows.Bangumi;
using MpvNet.Windows.MyAnimeList;
using MpvNet.Windows.Subtitles;
using MpvNet.Windows.Trakt;

static class Program
{
    static readonly string Root = Path.Combine(Path.GetTempPath(), "AnimeFusion-AliasFailures-" + Guid.NewGuid().ToString("N"));
    static readonly TraktMedia Media = TraktMedia.Parse("摇曳百合 S2E3", "fixture.mkv", new Dictionary<string, string>());
    const string English = "YuruYuri: Happy Go Lily ♪♪";
    const string SubjectJson = """
        {"id":28900,"type":2,"name":"ゆるゆり♪♪","name_cn":"摇曳百合 第二季","platform":"TV","date":"2012-07-02"}
        """;
    const string Translation = """
        {"id":12403,"title":"Yuru Yuri♪♪","alternative_titles":{"synonyms":["Yuru Yuri 2nd Season"],"en":"YuruYuri: Happy Go Lily ♪♪","ja":"ゆるゆり♪♪"},"start_date":"2012-07-03","media_type":"tv","num_episodes":12}
        """;
    static int _checks, _failures;

    static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) _failures++;
        Console.WriteLine((condition ? "PASS " : "FAIL ") + message);
    }
    static void Set(object? value, Type type, string name, object? target = null)
        => type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)!.SetValue(target, value);
    static Task Pending(SubtitleTitles titles, string field)
    {
        var type = typeof(SubtitleTitles);
        object sync = type.GetField("_lock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(titles)!;
        lock (sync)
        {
            var tasks = (System.Collections.IDictionary)type.GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(titles)!;
            return tasks.Values.Cast<Task>().Single(task => !task.IsCompleted);
        }
    }
    static async Task<bool> Canceled(Task task)
    {
        try { await task; return false; }
        catch (OperationCanceledException) { return true; }
    }
    static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    static MalClient Client(string label, HttpClient http)
        => new(new(Path.Combine(Root, label, "config"), Path.Combine(Root, label, "account")), http);
    static void ClearPlayback()
    {
        Set(null, typeof(BangumiPlayback), nameof(BangumiPlayback.Current));
        Set(null, typeof(MalPlayback), nameof(MalPlayback.Current));
        Set(null, typeof(TraktPlayback), nameof(TraktPlayback.Current));
    }
    static void KnownSubject(HttpClient http, string label)
    {
        var store = new BangumiStore(Path.Combine(Root, label, "bgm-config"), Path.Combine(Root, label, "bgm-account"));
        var playback = new BangumiPlayback(store, new(store, http));
        Set(Media.Parsed, typeof(BangumiPlayback), nameof(BangumiPlayback.Media), playback);
        Set(new Subject { Id = 28900, Name = "ゆるゆり♪♪", NameCn = "摇曳百合 第二季", Platform = "TV", Date = "2012-07-02" },
            typeof(BangumiPlayback), nameof(BangumiPlayback.Subject), playback);
        Set(playback, typeof(BangumiPlayback), nameof(BangumiPlayback.Current));
    }

    static async Task KnownNamesSurviveCatalogueFailure()
    {
        ClearPlayback();
        var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.Host != "api.bgm.tv") throw new Exception("Unexpected non-catalogue request.");
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("Simulated catalogue disconnect."));
        });
        using var http = new HttpClient(handler);
        string config = Path.Combine(Root, "known", "config");
        Directory.CreateDirectory(config);
        var store = new MalStore(config, Path.Combine(Root, "known", "account"));
        store.Bind(Media.Parsed.Key, new(12403, 2, English, 0));
        var client = new MalClient(store, http);
        Set(new MalPlayback(store, client), typeof(MalPlayback), nameof(MalPlayback.Current));
        var titles = new SubtitleTitles(Media, default, client);
        Check(titles.CurrentWork().Names.Contains(English), "Fixture has a verified local English binding before the failed read.");
        Check(titles.CachedNames.Contains(Media.Title) && titles.CachedNames.Contains(English) && handler.Count == 0,
            "The initial popup snapshot exposes verified local aliases without making a request.");
        IReadOnlyList<string> names = [];
        Exception? failure = null;
        try { names = await titles.NamesAsync(default); }
        catch (Exception error) { failure = error; }
        Console.WriteLine($"OBSERVE catalogue: requests={handler.Count}, names={names.Count}, error={failure?.GetType().Name ?? "none"}");
        Check(failure is HttpRequestException,
            "A catalogue network failure remains visible to the picker instead of pretending the lookup succeeded.");
        var known = titles.CurrentWork().Names;
        Check(known.Contains(Media.Title) && known.Contains(English),
            "The picker can retain the current title and verified local aliases after the failed lookup.");
        Check(titles.CachedNames.Contains(Media.Title) && titles.CachedNames.Contains(English),
            "The failure popup snapshot retains the original Chinese title and bound English name.");
    }

    static async Task FailedTranslationCanRetry()
    {
        ClearPlayback();
        bool disconnected = true;
        int catalogueCount = 0, translationCount = 0;
        var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.Host == "api.bgm.tv")
            {
                Interlocked.Increment(ref catalogueCount);
                return request.RequestUri.AbsolutePath switch
                {
                    "/v0/search/subjects" => Task.FromResult(Json("{\"data\":[" + SubjectJson + "]}")),
                    "/v0/subjects/28900" => Task.FromResult(Json(SubjectJson)),
                    _ => throw new Exception("Unexpected catalogue endpoint.")
                };
            }
            if (request.RequestUri!.Host != "api.myanimelist.net" || request.RequestUri.AbsolutePath != "/v2/anime/12403")
                throw new Exception("Unexpected translation endpoint.");
            Interlocked.Increment(ref translationCount);
            return disconnected
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("Simulated translation disconnect."))
                : Task.FromResult(Json(Translation));
        });
        using var http = new HttpClient(handler);
        var titles = new SubtitleTitles(Media, default, Client("retry", http));
        Exception? failure = null;
        try { await titles.NamesAsync(default); }
        catch (Exception error) { failure = error; }
        int firstCount = translationCount;
        int firstCatalogueCount = catalogueCount;
        var known = titles.CurrentWork().Names;
        Check(failure is HttpRequestException,
            "A translation network failure is reported instead of being cached as a successful empty translation.");
        Check(known.Contains(Media.Title) && known.Contains("ゆるゆり♪♪") && !known.Contains(English),
            "A failed translation leaves the successful catalogue's Chinese/Japanese aliases available from CurrentWork.");
        var failedSnapshot = titles.CachedNames;
        Check(failedSnapshot.Contains(Media.Title) && failedSnapshot.Contains("ゆるゆり♪♪") && !failedSnapshot.Contains(English),
            "The failure popup snapshot retains Chinese/Japanese catalogue names before the translation recovers.");
        Check(firstCatalogueCount == 2 && firstCount > 0,
            "The initial attempt queried the catalogue and its full subject before requesting the translation.");
        disconnected = false;
        var second = await titles.NamesAsync(default);
        Console.WriteLine($"OBSERVE translation retry: catalogue={firstCatalogueCount}->{catalogueCount}, translation={firstCount}->{translationCount}, englishReturned={second.Contains(English)}, firstError={failure?.GetType().Name ?? "none"}");
        Check(translationCount > firstCount && second.Contains(English),
            "The next explicit alias lookup retries a failed translation and returns the recovered English name.");
        Check(catalogueCount == firstCatalogueCount,
            "Retrying a failed translation never repeats the already successful catalogue query.");
        Check(titles.CachedNames.Contains(English) && !failedSnapshot.Contains(English),
            "Each popup read gets an independent snapshot while recovered names appear in the current cache.");
        int recoveredCount = handler.Count;
        var third = await titles.NamesAsync(default);
        Check(third.Contains(English) && handler.Count == recoveredCount,
            "A successful translation is reused without another request.");
    }

    static async Task CatalogueConcurrency()
    {
        ClearPlayback();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host != "api.bgm.tv" || request.RequestUri.AbsolutePath != "/v0/search/subjects")
                throw new Exception("Unexpected catalogue endpoint.");
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return Json("{\"data\":[]}");
        });
        using var http = new HttpClient(handler);
        var titles = new SubtitleTitles(Media, default, Client("concurrent-catalogue", http));
        using var canceledWait = new CancellationTokenSource();
        var first = titles.NamesAsync(canceledWait.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = titles.NamesAsync(default);
        Check(handler.Count == 1 && !first.IsCompleted && !second.IsCompleted, "Concurrent alias clicks share one in-flight catalogue request.");
        canceledWait.Cancel();
        bool canceled = false;
        try { await first; } catch (OperationCanceledException) { canceled = true; }
        Check(canceled && !second.IsCompleted, "Canceling one popup wait leaves the shared catalogue request alive.");
        release.TrySetResult();
        var names = await second;
        Check(names.Contains(Media.Title) && handler.Count == 1, "The remaining popup receives the shared result without a second request.");
    }

    static async Task TranslationConcurrency()
    {
        ClearPlayback();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host != "api.myanimelist.net") throw new Exception("Unexpected catalogue read for a confirmed subject.");
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return Json(Translation);
        });
        using var http = new HttpClient(handler);
        KnownSubject(http, "concurrent-translation");
        var titles = new SubtitleTitles(Media, default, Client("concurrent-translation", http));
        var first = titles.NamesAsync(default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = titles.NamesAsync(default);
        Check(handler.Count == 1 && !first.IsCompleted && !second.IsCompleted, "Concurrent alias clicks share one in-flight translation request.");
        release.TrySetResult();
        var names = await Task.WhenAll(first, second);
        Check(names.All(value => value.Contains(English)) && handler.Count == 1, "Both callers receive the same confirmed translation from one request.");
    }

    static async Task AbandonedRequestCanRetry(bool translation)
    {
        ClearPlayback();
        string stage = translation ? "translation" : "catalogue";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool recovered = false;
        var handler = new Handler(async (request, token) =>
        {
            string host = translation ? "api.myanimelist.net" : "api.bgm.tv";
            string path = translation ? "/v2/anime/12403" : "/v0/search/subjects";
            if (request.RequestUri!.Host != host || request.RequestUri.AbsolutePath != path)
                throw new Exception("Unexpected abandoned-request endpoint.");
            if (!recovered)
            {
                started.TrySetResult();
                await fail.Task.WaitAsync(token);
                throw new HttpRequestException("Simulated failure after every popup waiter was canceled.");
            }
            return Json(translation ? Translation : "{\"data\":[]}");
        });
        using var http = new HttpClient(handler);
        if (translation) KnownSubject(http, "abandoned-translation");
        var titles = new SubtitleTitles(Media, default, Client("abandoned-" + stage, http));
        using var firstWait = new CancellationTokenSource();
        using var secondWait = new CancellationTokenSource();
        var first = titles.NamesAsync(firstWait.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = titles.NamesAsync(secondWait.Token);
        // Capture only for deterministic completion of the abandoned HTTP task; do not poll it.
        Task shared = Pending(titles, translation ? "_translations" : "_queries");
        firstWait.Cancel();
        secondWait.Cancel();
        Check(await Canceled(first) && await Canceled(second) && !shared.IsCompleted && handler.Count == 1,
            $"All {stage} popup waiters can leave while the one shared HTTP request remains in flight.");
        fail.TrySetResult();
        Exception? abandonedFailure = null;
        try { await shared.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (HttpRequestException error) { abandonedFailure = error; }
        int failedRequests = handler.Count;
        Check(abandonedFailure != null && shared.IsFaulted,
            $"The shared {stage} request fails only after all popup waiters have left.");
        Check(titles.CachedNames.Contains(Media.Title) && (!translation || titles.CachedNames.Contains("ゆるゆり♪♪")),
            $"An abandoned {stage} failure still leaves the known popup names usable.");
        recovered = true;
        IReadOnlyList<string> names = [];
        Exception? retryFailure = null;
        try { names = await titles.NamesAsync(default); }
        catch (Exception error) { retryFailure = error; }
        Console.WriteLine($"OBSERVE abandoned {stage}: requests={failedRequests}->{handler.Count}, retryError={retryFailure?.GetType().Name ?? "none"}");
        Check(retryFailure == null && handler.Count == failedRequests + 1
            && names.Contains(translation ? English : Media.Title),
            $"The very next {stage} click replaces the abandoned failure and succeeds without needing an extra click.");
    }

    static async Task<int> Main()
    {
        var bgm = BangumiPlayback.Current;
        var mal = MalPlayback.Current;
        var trakt = TraktPlayback.Current;
        try
        {
            Console.WriteLine("Product SHA256: " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(SubtitleTitles).Assembly.Location))));
            await KnownNamesSurviveCatalogueFailure();
            await FailedTranslationCanRetry();
            await CatalogueConcurrency();
            await TranslationConcurrency();
            await AbandonedRequestCanRetry(false);
            await AbandonedRequestCanRetry(true);
            Console.WriteLine($"Alias failure checks: {_checks - _failures}/{_checks} passed, {_failures} failures; no network, accounts, windows or video.");
            return _failures == 0 ? 0 : 1;
        }
        finally
        {
            Set(bgm, typeof(BangumiPlayback), nameof(BangumiPlayback.Current));
            Set(mal, typeof(MalPlayback), nameof(MalPlayback.Current));
            Set(trakt, typeof(TraktPlayback), nameof(TraktPlayback.Current));
            string full = Path.GetFullPath(Root);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("AnimeFusion-AliasFailures-", StringComparison.Ordinal)
                && Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        int _count;
        public int Count => Volatile.Read(ref _count);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref _count);
            if (request.Headers.Authorization != null) throw new Exception("Isolated fixture unexpectedly added authentication.");
            return respond(request, token);
        }
    }
}

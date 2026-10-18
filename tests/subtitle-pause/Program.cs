using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using MpvNet.Windows.Subtitles;

static class Program
{
    static int _checks;
    static readonly SubtitleQuery Query = new("Fixture", 1, 1, "");
    static Task<SubtitleQuery> MakeQuery(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Query); }
    static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); _checks++; }
    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static Task Ready(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));
    static SubtitlePage Page(SubtitleSource source, string id, bool more) => new([new(source, id, "Fixture", "", "", [])], more) { PageKey = id };
    static async Task Cancelled(Task task)
    {
        try { await Ready(task); }
        catch (OperationCanceledException) { _checks++; return; }
        throw new InvalidOperationException("Expected the abandoned search to be cancelled.");
    }

    static async Task MultipleSources()
    {
        var firstStarted = Signal(); var secondStarted = Signal();
        var firstCancelled = Signal(); var secondCancelled = Signal();
        int firstPageTwo = 0, secondPageTwo = 0;
        var first = new Client(SubtitleSource.Assrt, async (page, token) =>
        {
            if (page == 2 && Interlocked.Increment(ref firstPageTwo) == 1)
            {
                using var registration = token.Register(() => firstCancelled.TrySetResult());
                firstStarted.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return Page(SubtitleSource.Assrt, "a" + page, page < 2);
        });
        var second = new Client(SubtitleSource.Amenzb, async (page, token) =>
        {
            if (page == 2 && Interlocked.Increment(ref secondPageTwo) == 1)
            {
                using var registration = token.Register(() => secondCancelled.TrySetResult());
                secondStarted.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return Page(SubtitleSource.Amenzb, "b" + page, page < 3);
        });
        var complete = new Client(SubtitleSource.Tsukihime, (page, _) => Task.FromResult(Page(SubtitleSource.Tsukihime, "c" + page, false)));
        using var search = new SubtitleSearch(first, second, complete);
        var progress = new Progress();
        var running = search.SearchAsync("Fixture", MakeQuery, progress, default);
        await Ready(Task.WhenAll(firstStarted.Task, secondStarted.Task));
        Check(search.SetPaused(" fixture ", true), "An active query can pause using its normalized cache key.");
        await Ready(Task.WhenAll(firstCancelled.Task, secondCancelled.Task));
        Check(progress.Last is { Paused: true, Complete: false } && progress.Last.Results.Count == 3,
            "Pausing preserves each provider's completed rows and marks the same result as paused.");
        Check(ReferenceEquals(running, search.SearchAsync("Fixture", MakeQuery, progress, default)) && progress.Last?.Paused == true,
            "Returning to the cached search neither restarts it nor clears its paused state.");
        Check(first.Calls.SequenceEqual([1, 2]) && second.Calls.SequenceEqual([1, 2]) && complete.Calls.SequenceEqual([1]),
            "Pausing cancels unfinished pages without launching another page.");
        Check(search.SetPaused("Fixture", false), "A paused query accepts an explicit resume.");
        await Ready(running);
        var result = await running;
        Check(result.Complete && !result.Paused && result.Results.Count == 6 && result.Errors.Count == 0,
            "Resume finishes all pending providers without presenting cancellation as a service error.");
        Check(first.Calls.SequenceEqual([1, 2, 2]) && second.Calls.SequenceEqual([1, 2, 2, 3]) && complete.Calls.SequenceEqual([1]),
            "Each provider resumes its unfinished page; completed pages and completed providers are not fetched again.");
        Check(!search.SetPaused("Fixture", true) && !search.SetPaused("missing", false), "Completed or unknown searches cannot pause or resume.");
    }

    static async Task BetweenPages()
    {
        var client = new Client(SubtitleSource.Assrt, (page, _) => Task.FromResult(Page(SubtitleSource.Assrt, page.ToString(), page < 2)));
        using var search = new SubtitleSearch(client);
        bool paused = false;
        var progress = new Progress(state =>
        {
            if (!paused && state.Results.Count == 1)
            {
                paused = true;
                Check(search.SetPaused("Fixture", true), "Pause is accepted immediately after a completed page is reported.");
            }
        });
        var running = search.SearchAsync("Fixture", MakeQuery, progress, default);
        Check(client.Calls.SequenceEqual([1]) && progress.Last is { Paused: true }, "A pause between pages prevents the next request from starting.");
        search.SetPaused("Fixture", false);
        await Ready(running);
        Check(client.Calls.SequenceEqual([1, 2]), "The completed page is not retried when pausing at the page boundary.");
    }

    static async Task RateWait()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var provider = new TsukihimeClient(http);
        var entered = Signal();
        var wrapper = new Client(SubtitleSource.Tsukihime, (page, token) =>
        {
            if (page == 2) entered.TrySetResult();
            return provider.SearchAsync(Query, page, token);
        });
        using var search = new SubtitleSearch(wrapper);
        var progress = new Progress();
        var running = search.SearchAsync("Fixture", MakeQuery, progress, default);
        await Ready(entered.Task);
        search.SetPaused("Fixture", true);
        // Outlast the real provider throttle once to verify the queued request was cancelled.
        await Task.Delay(1150);
        Check(handler.Count == 1 && progress.Last is { Paused: true } && !running.IsCompleted,
            "Pausing a page in the actual rate-limit wait prevents its HTTP request from being sent.");
        search.SetPaused("Fixture", false);
        await Ready(running);
        Check(handler.Count == 2 && wrapper.Calls.SequenceEqual([1, 2, 2]) && (await running).Results.Count == 2,
            "Resume sends only the previously unsent page through the existing rate limiter.");
    }

    static async Task LateReply()
    {
        var started = Signal(); var release = Signal();
        int attempts = 0;
        var client = new Client(SubtitleSource.Tsukihime, async (page, _) =>
        {
            if (page == 1) return Page(SubtitleSource.Tsukihime, "saved", true);
            if (Interlocked.Increment(ref attempts) == 1)
            {
                started.SetResult(); await release.Task;
                return Page(SubtitleSource.Tsukihime, "obsolete", false);
            }
            return Page(SubtitleSource.Tsukihime, "current", false);
        });
        using var search = new SubtitleSearch(client);
        var running = search.SearchAsync("Fixture", MakeQuery, new Progress(), default);
        await Ready(started.Task);
        search.SetPaused("Fixture", true);
        search.SetPaused("Fixture", false);
        release.SetResult();
        await Ready(running);
        Check((await running).Results.Select(item => item.Id).Order().SequenceEqual(["current", "saved"])
            && client.Calls.SequenceEqual([1, 2, 2]), "A response ignoring pause cancellation cannot overwrite the resumed page.");
    }

    static async Task AbandonPaused(bool dispose)
    {
        var started = Signal();
        var client = new Client(SubtitleSource.Tsukihime, async (_, token) =>
        {
            started.TrySetResult(); await Task.Delay(Timeout.Infinite, token);
            return new([], false);
        });
        using var cancellation = new CancellationTokenSource();
        using var search = new SubtitleSearch(client);
        var running = search.SearchAsync("Fixture", MakeQuery, new Progress(), cancellation.Token);
        await Ready(started.Task);
        search.SetPaused("Fixture", true);
        if (dispose) search.Dispose(); else cancellation.Cancel();
        await Cancelled(running);
        Check(!search.SetPaused("Fixture", false) && client.Calls.Count() == 1,
            "Disposal or changing the media cancels the paused wait without issuing another request.");
    }

    static async Task RefreshPaused()
    {
        var started = Signal(); int calls = 0;
        var first = new Client(SubtitleSource.Assrt, async (page, token) =>
        {
            int call = Interlocked.Increment(ref calls);
            if (call == 2) { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return Page(SubtitleSource.Assrt, "a" + call, call == 1);
        });
        var second = new Client(SubtitleSource.Tsukihime, (_, _) => Task.FromResult(Page(SubtitleSource.Tsukihime, "saved-other-source", false)));
        using var search = new SubtitleSearch(first, second);
        var old = search.SearchAsync("Fixture", MakeQuery, new Progress(), default);
        await Ready(started.Task);
        search.SetPaused("Fixture", true);
        var replacement = await search.SearchAsync("Fixture", MakeQuery, new Progress(), default, refresh: true, sources: [SubtitleSource.Assrt]);
        await Cancelled(old);
        Check(replacement.Complete && !replacement.Paused && replacement.Results.Select(item => item.Id).Order().SequenceEqual(["a3", "saved-other-source"])
            && second.Calls.SequenceEqual([1]), "An explicit source refresh replaces the paused operation and preserves other cached providers.");
    }

    static async Task BeforeQuery()
    {
        var release = Signal(); var initialized = Signal();
        var client = new Client(SubtitleSource.Assrt, (page, _) => Task.FromResult(Page(SubtitleSource.Assrt, page.ToString(), false)));
        using var search = new SubtitleSearch(client);
        var progress = new Progress(_ => initialized.TrySetResult());
        var running = search.SearchAsync("Fixture", async token => { await release.Task.WaitAsync(token); return Query; }, progress, default);
        Check(search.SetPaused("Fixture", true), "A query still preparing its initial state can be paused.");
        release.SetResult();
        await Ready(initialized.Task);
        Check(progress.Last is { Paused: true } && !client.Calls.Any(), "A pause during query preparation blocks all initial provider requests.");
        search.SetPaused("Fixture", false);
        await Ready(running);
        Check(client.Calls.SequenceEqual([1]), "Resuming initial preparation starts the first page exactly once.");
    }

    static async Task LanguageCaches()
    {
        var languages = new ConcurrentQueue<string>();
        int queries = 0;
        var client = new Client(SubtitleSource.Amenzb, (query, _, _) =>
        {
            languages.Enqueue(query.Language);
            return Task.FromResult(Page(SubtitleSource.Amenzb, query.Language.Length > 0 ? query.Language : "all", false));
        });
        using var search = new SubtitleSearch(client);
        Task<SubtitleQuery> Prepare(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref queries);
            return Task.FromResult(Query with { Language = "ja" });
        }
        var chineseTask = search.SearchAsync("Ｆｉｘｔｕｒｅ", Prepare, new Progress(), default, language: "zh-CN");
        var chinese = await chineseTask;
        Check(chinese.Query.Language == "zh-Hans" && languages.SequenceEqual(["zh-Hans"]),
            "The captured canonical request language overrides stale language from query preparation.");
        Check(ReferenceEquals(chineseTask, search.SearchAsync(" fixture ", Prepare, new Progress(), default, language: "chs"))
            && queries == 1 && client.Calls.Count() == 1,
            "Equivalent keyword and language spellings reuse the same completed request without preparing or fetching again.");
        var english = await search.SearchAsync("Fixture", Prepare, new Progress(), default, language: "eng");
        var all = await search.SearchAsync("Fixture", Prepare, new Progress(), default);
        Check(english.Query.Language == "en" && all.Query.Language == ""
            && languages.SequenceEqual(["zh-Hans", "en", ""]) && queries == 3,
            "English and all-language searches for the same keyword each make their own request.");
        Check(chinese.Results.Select(item => item.Id).SequenceEqual(["zh-Hans"])
            && english.Results.Select(item => item.Id).SequenceEqual(["en"])
            && all.Results.Select(item => item.Id).SequenceEqual(["all"]),
            "Cached results never carry rows from another language identity.");
    }

    static async Task LanguagePause()
    {
        var chineseStarted = Signal(); var englishStarted = Signal(); var chineseCancelled = Signal();
        var finishEnglish = Signal();
        var attempts = new ConcurrentDictionary<string, int>();
        var calls = new ConcurrentQueue<string>();
        int englishCancelled = 0;
        var client = new Client(SubtitleSource.Amenzb, async (query, page, token) =>
        {
            string language = query.Language;
            calls.Enqueue(language + ":" + page);
            if (page == 1) return Page(SubtitleSource.Amenzb, language + "-saved", true);
            int attempt = attempts.AddOrUpdate(language, 1, (_, value) => value + 1);
            if (language == "zh-Hans" && attempt == 1)
            {
                using var registration = token.Register(() => chineseCancelled.TrySetResult());
                chineseStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            if (language == "en")
            {
                using var registration = token.Register(() => Interlocked.Increment(ref englishCancelled));
                englishStarted.TrySetResult();
                await finishEnglish.Task.WaitAsync(token);
            }
            return Page(SubtitleSource.Amenzb, language + "-finished", false);
        });
        using var search = new SubtitleSearch(client);
        var chineseProgress = new Progress(); var englishProgress = new Progress();
        var chinese = search.SearchAsync("Fixture", MakeQuery, chineseProgress, default, language: "zh-Hans");
        var english = search.SearchAsync("Fixture", MakeQuery, englishProgress, default, language: "en");
        await Ready(Task.WhenAll(chineseStarted.Task, englishStarted.Task));
        Check(search.SetPaused(" fixture ", true, "zh-CN"), "Pausing accepts the canonical identity of a specific language request.");
        await Ready(chineseCancelled.Task);
        Check(chineseProgress.Last is { Paused: true } && englishProgress.Last is { Paused: false } && englishCancelled == 0,
            "Pausing Chinese does not pause or cancel the concurrent English page for the same keyword.");
        Check(!search.SetPaused("Fixture", true, "ja") && !search.SetPaused("Fixture", true),
            "Missing Japanese and all-language identities cannot control a different active language request.");
        finishEnglish.SetResult();
        await Ready(english);
        Check(!chinese.IsCompleted && (await english).Complete && englishProgress.Last is { Paused: false },
            "Another language can finish while the selected language remains paused.");
        Check(ReferenceEquals(chinese, search.SearchAsync("Fixture", MakeQuery, new Progress(), default, language: "chs"))
            && chineseProgress.Last is { Paused: true }, "Returning to the same language preserves its existing paused request.");
        Check(search.SetPaused("Fixture", false, "zh-Hans"), "Resuming targets only the selected language identity.");
        await Ready(chinese);
        Check(calls.Where(call => call.StartsWith("zh-Hans:", StringComparison.Ordinal)).SequenceEqual(["zh-Hans:1", "zh-Hans:2", "zh-Hans:2"])
            && calls.Where(call => call.StartsWith("en:", StringComparison.Ordinal)).SequenceEqual(["en:1", "en:2"]),
            "Language-scoped resume retries only that language's unfinished page.");
        Check((await chinese).Results.All(item => item.Id.StartsWith("zh-Hans-", StringComparison.Ordinal))
            && (await english).Results.All(item => item.Id.StartsWith("en-", StringComparison.Ordinal)),
            "Concurrent language pagination remains isolated after pause and resume.");
    }

    static async Task LanguageRefresh()
    {
        var calls = new ConcurrentDictionary<string, int>();
        Client Provider(SubtitleSource source) => new(source, (query, _, _) =>
        {
            string identity = source + ":" + query.Language;
            int count = calls.AddOrUpdate(identity, 1, (_, value) => value + 1);
            return Task.FromResult(Page(source, identity + ":" + count, false));
        });
        using var search = new SubtitleSearch(Provider(SubtitleSource.Assrt), Provider(SubtitleSource.Tsukihime));
        var chinese = await search.SearchAsync("Fixture", MakeQuery, new Progress(), default, language: "zh-Hans");
        var englishTask = search.SearchAsync("Fixture", MakeQuery, new Progress(), default, language: "en");
        var english = await englishTask;
        var refreshed = await search.SearchAsync("Fixture", MakeQuery, new Progress(), default,
            refresh: true, sources: [SubtitleSource.Assrt], language: "zh-CN");
        Check(refreshed.Query.Language == "zh-Hans" && refreshed.Results.Select(item => item.Id).Order()
            .SequenceEqual(["Assrt:zh-Hans:2", "Tsukihime:zh-Hans:1"]),
            "Refreshing one source replaces only that language's rows and retains its other source.");
        Check(calls["Assrt:zh-Hans"] == 2 && calls["Tsukihime:zh-Hans"] == 1
            && calls["Assrt:en"] == 1 && calls["Tsukihime:en"] == 1,
            "A language-scoped source refresh sends no request to other sources or another language.");
        Check(ReferenceEquals(englishTask, search.SearchAsync("Fixture", MakeQuery, new Progress(), default, language: "en"))
            && english.Results.Select(item => item.Id).Order().SequenceEqual(["Assrt:en:1", "Tsukihime:en:1"])
            && chinese.Results.Select(item => item.Id).Order().SequenceEqual(["Assrt:zh-Hans:1", "Tsukihime:zh-Hans:1"]),
            "Refreshing Chinese does not replace the English cache or mutate a previously delivered result.");
    }

    static string Position(SubtitleQuery query) => (query.RequestSeason?.ToString() ?? "all") + ":" + (query.RequestEpisode?.ToString() ?? "all");

    static async Task EpisodeCaches()
    {
        var queries = new ConcurrentQueue<SubtitleQuery>();
        int prepared = 0;
        var client = new Client(SubtitleSource.Amenzb, (query, _, _) =>
        {
            queries.Enqueue(query);
            return Task.FromResult(Page(SubtitleSource.Amenzb, Position(query), false));
        });
        using var search = new SubtitleSearch(client);
        Task<SubtitleQuery> Prepare(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref prepared);
            return Task.FromResult(Query with { Season = 9, Episode = 12, SearchSeason = 9, RequestSeason = 99, RequestEpisode = 99 });
        }
        var firstTask = search.SearchAsync("Ｆｉｘｔｕｒｅ", Prepare, new Progress(), default, language: "zh-CN", season: 4, episode: 2);
        var first = await firstTask;
        Check(first.Query is { Season: 9, Episode: 12, SearchSeason: 9, RequestSeason: 4, RequestEpisode: 2, Language: "zh-Hans" },
            "The requested season and episode override stale preparation fields without rewriting playback identity.");
        Check(ReferenceEquals(firstTask, search.SearchAsync(" fixture ", Prepare, new Progress(), default, language: "chs", season: 4, episode: 2))
            && prepared == 1 && queries.Count == 1,
            "Equivalent keyword and language spellings reuse the cache only for the same requested season and episode.");
        (int? Season, int? Episode)[] positions = [(4, 3), (5, 2), (4, null), (null, 2), (null, null), (0, 0), (null, 0), (0, null)];
        foreach (var position in positions)
        {
            var result = await search.SearchAsync("Fixture", Prepare, new Progress(), default, language: "zh-Hans", season: position.Season, episode: position.Episode);
            Check(result.Query.RequestSeason == position.Season && result.Query.RequestEpisode == position.Episode
                && result.Results.Single().Id == Position(result.Query),
                "Each requested position retains only its own result, including all-season, all-episode and zero positions.");
        }
        Check(prepared == positions.Length + 1 && queries.Count == prepared && queries.Select(Position).Distinct().Count() == prepared,
            "Different seasons, episodes and null/zero positions cannot reuse another request's cache.");
        Check(first.Results.Single().Id == "4:2", "Later episode searches do not mutate the earlier cached snapshot.");
    }

    static async Task EpisodePause()
    {
        var positions = new[] { "4:2", "4:3", "5:2" };
        var started = positions.ToDictionary(position => position, _ => Signal());
        var finishOthers = Signal(); var targetCancelled = Signal();
        var attempts = new ConcurrentDictionary<string, int>();
        var calls = new ConcurrentQueue<string>();
        int othersCancelled = 0;
        var client = new Client(SubtitleSource.Amenzb, async (query, page, token) =>
        {
            string position = Position(query);
            calls.Enqueue(position + ":" + page);
            if (page == 1) return Page(SubtitleSource.Amenzb, position + "-saved", true);
            int attempt = attempts.AddOrUpdate(position, 1, (_, count) => count + 1);
            if (position == "4:2" && attempt == 1)
            {
                using var registration = token.Register(() => targetCancelled.TrySetResult());
                started[position].TrySetResult(); await Task.Delay(Timeout.Infinite, token);
            }
            if (position != "4:2")
            {
                using var registration = token.Register(() => Interlocked.Increment(ref othersCancelled));
                started[position].TrySetResult(); await finishOthers.Task.WaitAsync(token);
            }
            return Page(SubtitleSource.Amenzb, position + "-finished", false);
        });
        using var search = new SubtitleSearch(client);
        var targetProgress = new Progress(); var nextProgress = new Progress(); var seasonProgress = new Progress();
        var target = search.SearchAsync("Fixture", MakeQuery, targetProgress, default, language: "zh-Hans", season: 4, episode: 2);
        var nextEpisode = search.SearchAsync("Fixture", MakeQuery, nextProgress, default, language: "zh-Hans", season: 4, episode: 3);
        var nextSeason = search.SearchAsync("Fixture", MakeQuery, seasonProgress, default, language: "zh-Hans", season: 5, episode: 2);
        await Ready(Task.WhenAll(started.Values.Select(signal => signal.Task)));
        Check(!search.SetPaused("Fixture", true, "zh-Hans", 4) && !search.SetPaused("Fixture", true, "zh-Hans", episode: 2)
            && !search.SetPaused("Fixture", true, "zh-Hans"), "Missing broader request scopes cannot pause an episode-specific search.");
        Check(search.SetPaused(" fixture ", true, "chs", 4, 2), "Pause uses the complete keyword, language, season and episode identity.");
        await Ready(targetCancelled.Task);
        Check(targetProgress.Last is { Paused: true } && nextProgress.Last is { Paused: false } && seasonProgress.Last is { Paused: false }
            && othersCancelled == 0, "Pausing an episode does not cancel the next episode or the same episode in another season.");
        finishOthers.SetResult(); await Ready(Task.WhenAll(nextEpisode, nextSeason));
        Check(!target.IsCompleted && (await nextEpisode).Complete && (await nextSeason).Complete,
            "Other season and episode scopes can finish while the target scope remains paused.");
        Check(ReferenceEquals(target, search.SearchAsync("Fixture", MakeQuery, new Progress(), default, language: "zh-CN", season: 4, episode: 2))
            && targetProgress.Last is { Paused: true }, "Reopening the exact requested episode preserves its paused pagination.");
        Check(search.SetPaused("Fixture", false, "zh-Hans", 4, 2), "Resume targets the same season and episode.");
        await Ready(target);
        Check(calls.Where(call => call.StartsWith("4:2:", StringComparison.Ordinal)).SequenceEqual(["4:2:1", "4:2:2", "4:2:2"])
            && calls.Where(call => call.StartsWith("4:3:", StringComparison.Ordinal)).SequenceEqual(["4:3:1", "4:3:2"])
            && calls.Where(call => call.StartsWith("5:2:", StringComparison.Ordinal)).SequenceEqual(["5:2:1", "5:2:2"]),
            "Episode-scoped resume retries only its own unfinished page without restarting another scope.");
        Check((await target).Results.All(item => item.Id.StartsWith("4:2-", StringComparison.Ordinal))
            && (await nextEpisode).Results.All(item => item.Id.StartsWith("4:3-", StringComparison.Ordinal))
            && (await nextSeason).Results.All(item => item.Id.StartsWith("5:2-", StringComparison.Ordinal)),
            "Concurrent episode searches never mix their results after pause and resume.");
    }

    static async Task EpisodeRefresh()
    {
        var calls = new ConcurrentDictionary<string, int>();
        Client Provider(SubtitleSource source) => new(source, (query, _, _) =>
        {
            string identity = source + ":" + Position(query);
            int count = calls.AddOrUpdate(identity, 1, (_, value) => value + 1);
            return Task.FromResult(Page(source, identity + ":" + count, false));
        });
        using var search = new SubtitleSearch(Provider(SubtitleSource.Assrt), Provider(SubtitleSource.Tsukihime));
        var first = await search.SearchAsync("Fixture", MakeQuery, new Progress(), default, season: 4, episode: 2);
        var nextTask = search.SearchAsync("Fixture", MakeQuery, new Progress(), default, season: 4, episode: 3);
        var next = await nextTask;
        var otherSeasonTask = search.SearchAsync("Fixture", MakeQuery, new Progress(), default, season: 5, episode: 2);
        var otherSeason = await otherSeasonTask;
        var refreshed = await search.SearchAsync("Fixture", MakeQuery, new Progress(), default,
            refresh: true, sources: [SubtitleSource.Assrt], season: 4, episode: 2);
        Check(refreshed.Results.Select(item => item.Id).Order().SequenceEqual(["Assrt:4:2:2", "Tsukihime:4:2:1"])
            && calls["Assrt:4:2"] == 2 && calls["Tsukihime:4:2"] == 1,
            "Refreshing one source retains other providers only from the same requested season and episode.");
        Check(ReferenceEquals(nextTask, search.SearchAsync("Fixture", MakeQuery, new Progress(), default, season: 4, episode: 3))
            && ReferenceEquals(otherSeasonTask, search.SearchAsync("Fixture", MakeQuery, new Progress(), default, season: 5, episode: 2))
            && calls["Assrt:4:3"] == 1 && calls["Tsukihime:4:3"] == 1 && calls["Assrt:5:2"] == 1 && calls["Tsukihime:5:2"] == 1,
            "A source refresh for one episode neither cancels nor replaces another cached season or episode scope.");
        var newPosition = await search.SearchAsync("Fixture", MakeQuery, new Progress(), default,
            refresh: true, sources: [SubtitleSource.Assrt], season: 6, episode: 2);
        Check(newPosition.Results.Select(item => item.Id).SequenceEqual(["Assrt:6:2:1"]) && !calls.ContainsKey("Tsukihime:6:2"),
            "A single-source search for a new episode cannot inherit another source's rows from a previous scope.");
        Check(first.Results.Select(item => item.Id).Order().SequenceEqual(["Assrt:4:2:1", "Tsukihime:4:2:1"])
            && next.Results.All(item => item.Id.Contains(":4:3:", StringComparison.Ordinal))
            && otherSeason.Results.All(item => item.Id.Contains(":5:2:", StringComparison.Ordinal)),
            "Refreshing a requested position leaves previously delivered snapshots unchanged.");
    }

    static async Task ProgressivePages()
    {
        var firstReady = Signal(); var finishFirst = Signal(); var secondReady = Signal(); var finishSecond = Signal();
        IProgress<IReadOnlyList<SubtitleResult>>? firstProgress = null;
        var first = Page(SubtitleSource.Amenzb, "first", true).Results;
        var second = Page(SubtitleSource.Amenzb, "second", true).Results;
        var client = new Client(SubtitleSource.Amenzb, async (_, page, token, progress) =>
        {
            if (page == 1)
            {
                firstProgress = progress;
                progress!.Report(first); progress.Report(first);
                firstReady.SetResult(); await finishFirst.Task.WaitAsync(token);
                return new(first, true);
            }
            if (page == 2)
            {
                secondReady.SetResult(); await finishSecond.Task.WaitAsync(token);
                progress!.Report(second); progress.Report(second);
            }
            return new(second, true);
        });
        using var search = new SubtitleSearch(client);
        int reports = 0;
        var updates = new Progress(_ => Interlocked.Increment(ref reports));
        var running = search.SearchAsync("Fixture", MakeQuery, updates, default);
        await Ready(firstReady.Task);
        Check(!running.IsCompleted && updates.Last is { Complete: false } && updates.Last.Results.Select(item => item.Id).SequenceEqual(["first"]),
            "Verified subtitles are reported before the provider finishes its page.");
        Check(reports == 2, "Repeated partial rows do not send duplicate search updates.");
        finishFirst.SetResult(); await Ready(secondReady.Task);
        int beforeLate = reports;
        firstProgress!.Report(Page(SubtitleSource.Amenzb, "late-finished-page", false).Results);
        Check(reports == beforeLate && updates.Last!.Results.Count == 1,
            "A late progress callback from an already completed page is ignored while later pages are running.");
        finishSecond.SetResult(); await Ready(running);
        Check(client.Calls.SequenceEqual([1, 2, 3]) && (await running).Results.Select(item => item.Id).SequenceEqual(["first", "second"]),
            "Partial rows do not stop pagination when the final page adds zero new rows, and a repeated page still stops it.");
        beforeLate = reports;
        firstProgress.Report(Page(SubtitleSource.Amenzb, "late-complete-search", false).Results);
        Check(reports == beforeLate && (await running).Results.Count == 2, "Completed searches reject all late partial callbacks.");
    }

    static async Task ProgressivePause()
    {
        var started = Signal(); var resumed = Signal(); var finish = Signal();
        IProgress<IReadOnlyList<SubtitleResult>>? abandonedProgress = null;
        int attempts = 0;
        var first = Page(SubtitleSource.Amenzb, "first", true).Results;
        var second = Page(SubtitleSource.Amenzb, "second", true).Results;
        var client = new Client(SubtitleSource.Amenzb, async (_, page, token, progress) =>
        {
            if (page == 2) return Page(SubtitleSource.Amenzb, "next-page", false);
            progress!.Report(first);
            if (Interlocked.Increment(ref attempts) == 1)
            {
                abandonedProgress = progress;
                started.SetResult(); await Task.Delay(Timeout.Infinite, token);
            }
            resumed.SetResult(); await finish.Task.WaitAsync(token);
            progress.Report(second);
            return new(first.Concat(second).ToArray(), true);
        });
        using var search = new SubtitleSearch(client);
        int reports = 0;
        var updates = new Progress(_ => Interlocked.Increment(ref reports));
        var running = search.SearchAsync("Fixture", MakeQuery, updates, default);
        await Ready(started.Task);
        Check(search.SetPaused("Fixture", true) && updates.Last is { Paused: true } && updates.Last.Results.Count == 1,
            "Pausing inside a page retains its already verified rows.");
        int beforeLate = reports;
        abandonedProgress!.Report(Page(SubtitleSource.Amenzb, "late-paused", false).Results);
        Check(reports == beforeLate && updates.Last!.Results.Count == 1, "A canceled attempt cannot report more rows while paused.");
        search.SetPaused("Fixture", false); await Ready(resumed.Task);
        beforeLate = reports;
        abandonedProgress.Report(Page(SubtitleSource.Amenzb, "late-resumed", false).Results);
        Check(reports == beforeLate && updates.Last!.Results.Count == 1,
            "The old attempt remains invalid after resume and its replayed rows are not duplicated.");
        finish.SetResult(); await Ready(running);
        Check(client.Calls.SequenceEqual([1, 1, 2]) && (await running).Results.Select(item => item.Id).SequenceEqual(["first", "second", "next-page"]),
            "Resuming retries only the unfinished page, deduplicates partial rows and continues to the next page.");
    }

    static async Task ProgressiveRefresh()
    {
        var started = Signal(); var release = Signal(); var otherReady = Signal();
        IProgress<IReadOnlyList<SubtitleResult>>? abandonedProgress = null;
        int calls = 0;
        var first = new Client(SubtitleSource.Amenzb, async (_, _, _, progress) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                abandonedProgress = progress;
                progress!.Report(Page(SubtitleSource.Amenzb, "old-partial", true).Results);
                started.SetResult(); await release.Task;
                return Page(SubtitleSource.Amenzb, "old-final", false);
            }
            progress!.Report(Page(SubtitleSource.Amenzb, "new-partial", false).Results);
            return Page(SubtitleSource.Amenzb, "new-partial", false);
        });
        var other = new Client(SubtitleSource.Tsukihime, (_, _) =>
        {
            otherReady.TrySetResult(); return Task.FromResult(Page(SubtitleSource.Tsukihime, "retained-source", false));
        });
        using var search = new SubtitleSearch(first, other);
        int oldReports = 0;
        var updates = new Progress(_ => Interlocked.Increment(ref oldReports));
        var abandoned = search.SearchAsync("Fixture", MakeQuery, updates, default);
        await Ready(Task.WhenAll(started.Task, otherReady.Task));
        var replacement = await search.SearchAsync("Fixture", MakeQuery, new Progress(), default, refresh: true, sources: [SubtitleSource.Amenzb]);
        int beforeLate = oldReports;
        abandonedProgress!.Report(Page(SubtitleSource.Amenzb, "late-refreshed", false).Results);
        release.SetResult(); await Cancelled(abandoned);
        Check(oldReports == beforeLate && updates.Last!.Results.Select(item => item.Id).Order().SequenceEqual(["old-partial", "retained-source"]),
            "Refreshing a source rejects both partial and final late responses from the abandoned request.");
        Check(replacement.Complete && replacement.Results.Select(item => item.Id).Order().SequenceEqual(["new-partial", "retained-source"])
            && other.Calls.SequenceEqual([1]), "A source refresh replaces its partial rows while preserving another source's cached rows.");
    }

    static async Task ProgressiveEmptyPage()
    {
        var client = new Client(SubtitleSource.Amenzb, (_, page, _, progress) =>
        {
            if (page == 1) return Task.FromResult(new SubtitlePage([], true) { PageKey = "raw-filtered-release" });
            var result = Page(SubtitleSource.Amenzb, "verified", false);
            progress!.Report(result.Results);
            return Task.FromResult(result);
        });
        using var search = new SubtitleSearch(client);
        var result = await search.SearchAsync("Fixture", MakeQuery, new Progress(), default);
        Check(client.Calls.SequenceEqual([1, 2]) && result.Results.Select(item => item.Id).SequenceEqual(["verified"]),
            "An empty filtered page with a raw source page key still reaches the next page's streamed results.");
    }

    static async Task Main()
    {
        await MultipleSources(); await BetweenPages(); await RateWait(); await LateReply();
        await AbandonPaused(false); await AbandonPaused(true); await RefreshPaused(); await BeforeQuery();
        await LanguageCaches(); await LanguagePause(); await LanguageRefresh();
        await EpisodeCaches(); await EpisodePause(); await EpisodeRefresh();
        await ProgressivePages(); await ProgressivePause(); await ProgressiveRefresh(); await ProgressiveEmptyPage();
        Console.WriteLine($"Subtitle pause checks passed: {_checks}; no real service, GUI or video access.");
    }

    sealed class Progress(Action<SubtitleSearchResult>? report = null) : IProgress<SubtitleSearchResult>
    {
        public SubtitleSearchResult? Last { get; private set; }
        public void Report(SubtitleSearchResult value) { Last = value; report?.Invoke(value); }
    }
    sealed class Client : ISubtitleClient
    {
        readonly ConcurrentQueue<int> _calls = new();
        readonly Func<SubtitleQuery, int, CancellationToken, IProgress<IReadOnlyList<SubtitleResult>>?, Task<SubtitlePage>> _load;
        public Client(SubtitleSource source, Func<int, CancellationToken, Task<SubtitlePage>> load)
            : this(source, (_, page, token) => load(page, token)) { }
        public Client(SubtitleSource source, Func<SubtitleQuery, int, CancellationToken, Task<SubtitlePage>> load)
            : this(source, (query, page, token, _) => load(query, page, token)) { }
        public Client(SubtitleSource source, Func<SubtitleQuery, int, CancellationToken, IProgress<IReadOnlyList<SubtitleResult>>?, Task<SubtitlePage>> load)
        { Source = source; _load = load; }
        public SubtitleSource Source { get; }
        public IEnumerable<int> Calls => _calls.ToArray();
        public Task<SubtitlePage> SearchAsync(SubtitleQuery query, int page, CancellationToken token,
            IProgress<IReadOnlyList<SubtitleResult>>? progress = null) { _calls.Enqueue(page); return _load(query, page, token, progress); }
        public Task<SubtitleResult> DetailAsync(SubtitleResult result, CancellationToken token) => throw new NotSupportedException();
    }
    sealed class Handler : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(request.RequestUri!.Host == "api.tsukihime.org" && request.RequestUri.AbsolutePath == "/v1/search/torrents",
                "The throttle fixture only exercises the provider's subtitle search route.");
            int page = ++Count;
            string json = JsonSerializer.Serialize(new
            {
                total = 51, start = page == 1 ? 0 : 50,
                results = new[] { new { id = page, name = "Fixture S01E01", filecount = 1, state = "completed", episode_no = 1, sublangs = new[] { "zh-Hans" } } }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}

using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using MpvNet.Windows.Subtitles;

static class SubtitleSearchCrashChecks
{
    public static Task<int> RunAsync(string repository, string root)
    {
        string fixture = File.ReadAllText(Path.Combine(repository, "tests/subtitles/Fixtures/assrt-macross-delta-page1.json"));
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, error) =>
            {
                completion.TrySetException(error.Exception);
                error.Handled = true;
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            };
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { completion.TrySetResult(await RunSearchesAsync(fixture, root, dispatcher)); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Subtitle search dispatcher regression" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    static async Task<int> RunSearchesAsync(string fixture, string root, Dispatcher dispatcher)
    {
        int checks = 0, updates = 0, populatedUpdates = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new Exception(message);
            checks++;
        }
        var store = new SubtitleStore(Path.Combine(root, "search-crash"));
        store.Save(new(new string('a', 32)));
        using var handler = new PageHandler(fixture);
        using var http = new HttpClient(handler);
        using var search = new SubtitleSearch(new AssrtClient(http, store));
        const string keyword = "超时空要塞Δ";
        var query = new SubtitleQuery(keyword, null, null, "");
        var progress = new Progress<SubtitleSearchResult>(state =>
        {
            Check(dispatcher.CheckAccess() && SynchronizationContext.Current is DispatcherSynchronizationContext,
                "Search progress refreshes on the WPF dispatcher synchronization context");
            updates++;
            var results = state.Results;
            var clock = Stopwatch.StartNew();
            Console.WriteLine($"Search crash regression: progress {updates}, results {results.Count}, refresh start");
            // Run the same episode-chip calculation once per progress notification.
            var episodes = results.Where(result => result.MatchesFilter(null, "", null))
                .SelectMany(result => result.EpisodesFor(null, "")).ToHashSet();
            Console.WriteLine($"Search crash regression: progress {updates}, episodes {episodes.Count}, {clock.ElapsedMilliseconds} ms");
            const int selectedEpisode = 1;
            int visible = results.Count(result => result.MatchesFilter(null, "", null, selectedEpisode));
            Check(visible <= results.Count && episodes.All(episode => episode >= 0),
                "Public ASSRT filenames survive the episode-chip calculation and current-episode filter");
            Console.WriteLine($"Search crash regression: progress {updates}, refresh complete, {clock.ElapsedMilliseconds} ms");
            if (results.Count > 0) populatedUpdates++;
        });
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Console.WriteLine($"Search crash regression: explicit search {attempt + 1} start");
            var result = await search.SearchAsync(keyword, _ => Task.FromResult(query), progress,
                default, refresh: true, sources: [SubtitleSource.Assrt]);
            Console.WriteLine($"Search crash regression: explicit search {attempt + 1} response complete");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Console.WriteLine($"Search crash regression: explicit search {attempt + 1} progress drained");
            Check(result.Complete && result.Errors.Count == 0 && result.Results.Count == 15,
                "Consecutive explicit searches complete with the real first-page result fixture");
        }
        Check(handler.Count == 4 && handler.FirstPages == 2,
            "Each explicit search requests the first page again and terminates at the empty fixture page");
        Check(updates >= 4 && populatedUpdates >= 2,
            "Both explicit searches deliver populated progress callbacks without a dispatcher exception");
        return checks;
    }

    sealed class PageHandler(string firstPage) : HttpMessageHandler
    {
        public int Count { get; private set; }
        public int FirstPages { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Post || request.RequestUri?.Host != "api.assrt.net"
                || request.RequestUri.AbsolutePath != "/v1/sub/search")
                throw new Exception("The regression permits only the production ASSRT search endpoint");
            string form = await request.Content!.ReadAsStringAsync(token);
            if (!form.Contains("cnt=15", StringComparison.Ordinal)) throw new Exception("Unexpected search page size");
            string response;
            if (form.Contains("pos=0", StringComparison.Ordinal)) { FirstPages++; response = firstPage; }
            else if (form.Contains("pos=15", StringComparison.Ordinal)) response = "{\"status\":0,\"sub\":{\"subs\":[]}}";
            else throw new Exception("Unexpected search page position");
            Count++;
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}

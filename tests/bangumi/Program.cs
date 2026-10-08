using System.Net;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BangumiNet.Api.V0.Models;
using MpvNet.Windows.Bangumi;
using MpvNet.Windows.WPF;
using MpvNet.Windows.UI;
using System.Windows.Media;

static class Program
{
    static int _checks;
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
    }
    static readonly string Root = Path.Combine(Path.GetTempPath(), "AnimeVE-Bangumi-" + Guid.NewGuid().ToString("N"));
    static bool Watched(BangumiPlayback playback, int id)
    {
        using var snapshot = JsonDocument.Parse(playback.SnapshotJson());
        return snapshot.RootElement.GetProperty("subject").GetProperty("episodes").EnumerateArray()
            .Any(e => e.GetProperty("id").GetInt32() == id && e.GetProperty("state").GetInt32() == 2);
    }
    static BangumiStore Store(string name) => new(Path.Combine(Root, name, "config"), Path.Combine(Root, name, "account"));
    static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    [STAThread]
    static void Main(string[] args)
    {
        var store = Store("ui");
        var client = new BangumiClient(store, new HttpClient(new Handler()));
        Theme.Current = new() { Foreground = Brushes.White, Foreground2 = Brushes.Gray, Background = Brushes.Black, MenuBackground = Brushes.DarkSlateGray, MenuHighlight = Brushes.DimGray, Heading = Brushes.Purple };
        var window = new BangumiMatchWindow(new(store, client));
        Check(window.Title == "匹配 Bangumi", "Manual matching window constructs without desktop interaction");
        Check(window.FindName("AutoSyncBox") == null && window.FindName("ThresholdSlider") == null,
            "Matching window has no account or synchronization settings");
        store.SetCollectPercent(15); store.SetWatchedPercent(75);
        var saved = new BangumiStore(Path.Combine(Root, "ui", "config"), Path.Combine(Root, "ui", "account"));
        Check(saved.Settings.CollectPercent == 15 && saved.Settings.WatchedPercent == 75, "Both independent thresholds persist");
        using (var image = new System.Drawing.Bitmap(8, 8))
        {
            using (var graphics = System.Drawing.Graphics.FromImage(image)) graphics.Clear(System.Drawing.Color.Red);
            byte[] pixels = BangumiClient.CircularAvatar(image);
            Check(pixels.Length == 16384 && pixels[3] == 0 && pixels[(32 * 64 + 32) * 4 + 3] == 255,
                "Avatar has transparent corners and opaque circular center");
        }
        window.Measure(new System.Windows.Size(540, 760));
        window.Arrange(new System.Windows.Rect(0, 0, 540, 760));
        var combo = (System.Windows.Controls.ComboBox)window.FindName("EpisodesBox");
        Check(combo.ApplyTemplate() && combo.Template.FindName("PART_Popup", combo) != null,
            "Episode dropdown template constructs without showing a popup");
        window.Close();
        RunAsync().GetAwaiter().GetResult();
        string expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        Check(Path.GetFullPath(Root).StartsWith(expected, StringComparison.OrdinalIgnoreCase), "Cleanup target stays in temporary directory");
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
        Console.WriteLine($"PASS Bangumi: {_checks} checks (mock HTTP and loopback only; no browser or account writes)");
    }

    static async Task RunAsync()
    {
        var media = BangumiMedia.Parse("间谍过家家 (2025) S3E10 - 标题");
        Check(media.Title == "间谍过家家" && media.Season == 3 && media.EpisodeNumber == 10, "Parse formatted series title");
        var season = BangumiMedia.Parse("间谍过家家 第三季 (2025)");
        Check(season.Key == media.Key && season.EpisodeNumber == null, "Season is not misparsed as an episode");
        Check(BangumiMedia.Parse("无职转生Ⅲ ～到了异世界就拿出真本事～ (2026)").Key == BangumiMedia.Parse("无职转生：到了异世界就拿出真本事 (2026) S3E1").Key,
            "Attached Roman numeral resolves the correct season");
        Check(BangumiMedia.Parse("Example Season 3").Key == BangumiMedia.Parse("Example S3E1").Key && BangumiMedia.Parse("Example 3rd Season").Season == 3,
            "English season formats resolve the correct season");
        Check(BangumiMedia.Parse("我的摄影 2026").EpisodeNumber == null, "Unnumbered files do not trigger automatic search");
        var episodes = new List<Episode>
        {
            new() { Id = 201, Type = 1, Ep = 1, Sort = 1 },
            new() { Id = 101, Type = 0, Ep = 1, Sort = 13 },
            new() { Id = 102, Type = 0, Ep = 2, Sort = 14 }
        };
        Check(BangumiMedia.MatchEpisode(episodes, 1, 1)?.Id == 101, "Main episodes take precedence over specials");
        Check(BangumiMedia.MatchEpisode(episodes, 14, 1)?.Id == 102, "Absolute sort fallback follows upstream matcher");

        var absolute = new List<Episode> {
            new() { Id=201, Type=1, Ep=1, Sort=1 }, new() { Id=101, Type=0, Ep=38, Sort=38 },
            new() { Id=102, Type=0, Ep=39, Sort=39 }
        };
        Check(BangumiMedia.MatchEpisode(absolute, 1, 3)?.Id == 101 && BangumiMedia.MatchEpisode(absolute, 2, 3)?.Id == 102,
            "Season-local numbers map to absolute Bangumi numbering before specials");
        Check(BangumiMedia.MatchEpisode(absolute, 39, 3)?.Id == 102,
            "Absolute episode numbers remain exact matches");

        var handler = new Handler();
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        var store = Store("account");
        var client = new BangumiClient(store, http);
        await client.ConnectAsync(new("unit-test-token", "refresh-token", DateTimeOffset.UtcNow.AddDays(7), ""), CancellationToken.None);
        Check(client.Account?.Username == "test-user", "Browser authorization account uses /v0/me");
        Check(store.ReadAccount()?.AccessToken == "unit-test-token", "Encrypted account round trip");
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Root, "account", "account", "account.bin"))).Contains("unit-test-token"), "Token is not stored in plaintext");
        handler.InvalidToken = true;
        try { await client.ConnectAsync(new("invalid", "refresh-token", DateTimeOffset.UtcNow.AddDays(7), ""), CancellationToken.None); throw new Exception("Invalid token accepted"); }
        catch (Microsoft.Kiota.Abstractions.ApiException) { }
        Check(client.Account?.AccessToken == "unit-test-token", "Failed replacement token preserves connected account");
        handler.InvalidToken = false;

        bool updated = await client.MarkWatchedAsync(10, 101, CancellationToken.None);
        Check(updated && handler.Writes.Count == 1 && handler.Writes[0].Path.EndsWith("/101"), "Mark one episode watched");
        Check(handler.Writes[0].Body == "{\"type\":2}", "Episode write uses generated client payload");
        Check(handler.Seen.All(r => (r.Header == "Bearer unit-test-token" || r.Header == "Bearer invalid") && !r.Url.Contains("unit-test-token")), "Bearer token stays in headers");
        int count = handler.Writes.Count;
        handler.Watched = true;
        Check(!await client.MarkWatchedAsync(10, 101, CancellationToken.None) && handler.Writes.Count == count, "Already-watched episode does not write again");
        handler.Watched = false;
        handler.CollectionType = 1;
        count = handler.Writes.Count;
        await client.MarkWatchedAsync(10, 101, CancellationToken.None);
        Check(handler.Writes.Count == count + 1 && handler.Writes[^1].Method == "PUT",
            "Marking an episode preserves the subject collection status");
        await client.CollectAsync(10, 3, false, CancellationToken.None);
        Check(handler.Writes[^1].Method == "PATCH" && handler.Writes[^1].Body == "{\"type\":3}",
            "Explicit collection update only writes type and preserves ratings and privacy");
        handler.CollectionType = 2;
        count = handler.Writes.Count;
        await client.MarkWatchedAsync(10, 101, CancellationToken.None);
        Check(handler.Writes.Count == count + 1, "Completed collection still allows individual episode correction");
        handler.NotCollected = true;
        count = handler.Writes.Count;
        try { await client.MarkWatchedAsync(10, 101, CancellationToken.None); throw new Exception("Uncollected episode accepted"); }
        catch (InvalidOperationException) { }
        Check(handler.Writes.Count == count, "Episode operation cannot implicitly collect a subject");
        await client.CollectAsync(10, 3, true, CancellationToken.None);
        Check(handler.Writes[^1].Method == "POST" && handler.Writes[^1].Body == "{\"type\":3}", "New collection is a separate operation");
        handler.NotCollected = false;
        handler.CollectionType = 3;
        count = handler.Writes.Count;
        Check(await client.CollectAsync(10, 3, true, CancellationToken.None) == 3 && handler.Writes.Count == count,
            "Auto collection never resubmits an existing collection");

        string avatar = await client.AvatarFileAsync(CancellationToken.None);
        Check(File.Exists(avatar) && new FileInfo(avatar).Length == 16384, "Real profile avatar is cached as circular BGRA");
        await client.AvatarFileAsync(CancellationToken.None);
        Check(handler.AvatarRequests == 1, "Cached avatar is not downloaded repeatedly");
        string cover = await client.CoverFileAsync("https://images.example.invalid/avatar.png", CancellationToken.None);
        Check(File.Exists(cover) && new FileInfo(cover).Length == 240*360*4, "Poster cache matches the popover BGRA dimensions");
        await client.CoverFileAsync("https://images.example.invalid/avatar.png", CancellationToken.None);
        Check(handler.AvatarRequests == 2, "Poster is downloaded once and reused without network calls per redraw");
        handler.Writes.Clear();
        handler.CollectionType = 3;
        var playback = new BangumiPlayback(store, client);
        playback.SetMedia("Example S1E1");
        await Until(() => playback.Selection != null);
        Check(handler.Searches == 1 && playback.Selection?.EpisodeId == 101, "Unique exact subject resolves once");
        await playback.ResolveAsync();
        Check(handler.Searches == 1, "Resolve calls for same file do not repeat search");
        int collectionReads = handler.Seen.Count(r => r.Url.EndsWith("/collections/10"));
        playback.Store.SetCollectPercent(20);
        playback.UpdateProgress(20); await Task.Delay(30);
        Check(handler.Seen.Count(r => r.Url.EndsWith("/collections/10")) == collectionReads + 1,
            "Automatic collection checks the server even when the local subject is collected");
        playback.UpdateProgress(89);
        await Task.Delay(30);
        Check(handler.Writes.Count == 0, "Below progress threshold does not sync");
        playback.UpdateProgress(90);
        for (int i = 0; i < 200; i++) playback.UpdateProgress(95);
        await Until(() => Watched(playback, 101));
        Check(playback.Status == "", "Watched episodes are shown by the episode tile without redundant status text");
        Check(handler.Writes.Count == 1, "Progress events trigger a single sync, not network polling");

        playback.SetMedia("Unknown S2E1");
        await Until(() => playback.Status == "请选择本季条目与当前剧集。");
        Check(playback.Selection == null, "Nonmatching season is not silently bound");
        var oldMedia = playback.Media;
        int generation = playback.Generation;
        playback.SetMedia("Example S1E2");
        try { playback.Bind(oldMedia, generation, new() { Id = 10, Name = "Unknown" }, episodes[1], episodes); throw new Exception("Stale UI binding accepted"); }
        catch (InvalidOperationException) { }
        await Until(() => playback.Selection?.EpisodeId == 102);
        Check(playback.Media.EpisodeNumber == 2 && playback.Selection?.EpisodeId == 102, "Next episode resolves independently");
        playback.Cancel();
        await playback.ResolveAsync();
        Check(playback.Selection?.EpisodeId == 102, "Reconnection can resolve after canceling old account requests");
        playback.SetWatchedPercent(60);
        handler.Writes.Clear();
        playback.UpdateProgress(59);
        await Task.Delay(30);
        Check(handler.Writes.Count == 0, "Configured threshold blocks early sync");
        playback.UpdateProgress(60);
        await Until(() => handler.Writes.Count == 1);
        Check(handler.Writes.Count == 1, "Configured threshold triggers a single sync");

        using (var snapshot = JsonDocument.Parse(playback.SnapshotJson()))
        {
            var subject = snapshot.RootElement.GetProperty("subject");
            Check(subject.GetProperty("title").GetString() == "Example" && subject.GetProperty("score").GetDouble() == 7.3,
                "Avatar state contains matched subject metadata and rating");
            Check(subject.GetProperty("currentEpisode").GetInt32() == 102 && subject.GetProperty("episodes").GetArrayLength() == 2,
                "Avatar state contains current episode and the episode grid");
        }
        playback.Cancel();
        var manual = new BangumiPlayback(Store("manual"), client);
        manual.Store.SetAutoCollect(false); manual.Store.SetAutoSync(false);
        manual.SetMedia("Example S1E1"); await Until(() => manual.Selection != null);
        var ordered = new List<Episode> {
            new() { Id=201, Type=1, Ep=1, Sort=1 }, new() { Id=101, Type=0, Ep=1, Sort=38 },
            new() { Id=102, Type=0, Ep=2, Sort=39 }, new() { Id=103, Type=0, Ep=3, Sort=40 }
        };
        manual.Bind(manual.Media, manual.Generation, new() { Id=10, Name="Example" }, ordered[2], ordered);
        await Until(() => manual.CollectionType == 3);
        Check(manual.Selection?.EpisodeId == 102, "Manual episode correction is not overwritten by title detection");
        handler.Writes.Clear();
        await manual.ChangeEpisodeAsync(102, 2, false, manual.Generation);
        Check(handler.Writes.Count == 1 && handler.Writes[0].Path.EndsWith("/102") && handler.Writes[0].Method == "PUT",
            "Watched changes exactly one episode");
        await manual.ChangeEpisodeAsync(102, 2, true, manual.Generation);
        using (var batch = JsonDocument.Parse(handler.Writes[^1].Body))
            Check(handler.Writes[^1].Method == "PATCH" && batch.RootElement.GetProperty("episode_id").EnumerateArray().Select(v => v.GetInt32()).SequenceEqual(new[] {101,102}),
                "Watched-through uses season ordinal, excludes specials and leaves later episodes untouched");
        count = handler.Writes.Count;
        await manual.ChangeEpisodeAsync(103, 2, false, manual.Generation-1);
        Check(handler.Writes.Count == count, "Stale popover action does not write to another video");
        manual.Cancel();
        var separateHandler = new Handler { NotCollected=true };
        var separateStore = Store("separate"); separateStore.SetCollectPercent(20); separateStore.SetWatchedPercent(80);
        var separateClient = new BangumiClient(separateStore,new HttpClient(separateHandler));
        await separateClient.ConnectAsync(new("unit-test-token","refresh-token",DateTimeOffset.UtcNow.AddDays(7),""),CancellationToken.None);
        var separate = new BangumiPlayback(separateStore,separateClient);
        separate.SetMedia("Example S1E1"); await Until(() => separate.Selection != null);
        separate.UpdateProgress(19); await Task.Delay(30);
        Check(separateHandler.Writes.Count == 0, "Collection waits for its own threshold");
        separate.UpdateProgress(20); await Until(() => separate.CollectionType == 3);
        Check(separateHandler.Writes.Count == 1 && separateHandler.Writes[0].Method == "POST", "Collection threshold only creates watching collection");
        separate.UpdateProgress(79); await Task.Delay(30);
        Check(separateHandler.Writes.Count == 1, "Watched threshold does not follow collection threshold");
        separate.UpdateProgress(80); await Until(() => Watched(separate, 101));
        Check(separate.Status == "", "Automatic watched sync has no redundant success line");
        Check(separateHandler.Writes.Count == 2 && separateHandler.Writes[1].Method == "PUT", "Watched threshold only updates current episode");
        separate.Cancel();

        var canceledHandler = new Handler();
        var canceledStore = Store("canceled-collection");
        canceledStore.SetCollectPercent(20); canceledStore.SetWatchedPercent(80);
        var canceledClient = new BangumiClient(canceledStore,new HttpClient(canceledHandler));
        await canceledClient.ConnectAsync(new("unit-test-token","refresh-token",DateTimeOffset.UtcNow.AddDays(7),""),CancellationToken.None);
        var canceled = new BangumiPlayback(canceledStore,canceledClient);
        canceled.SetMedia("Example S1E1"); await Until(() => canceled.Selection != null);
        Check(canceled.CollectionType == 3,"Collection initially resolved from the server");
        canceledHandler.NotCollected = true;
        canceled.UpdateProgress(20); await Until(() => canceledHandler.Writes.Count == 1);
        Check(canceledHandler.Writes[0].Method == "POST","Collection removed during playback is detected at the collection threshold");
        await Task.Delay(30);
        canceledHandler.NotCollected = true;
        canceled.UpdateProgress(80); await Until(() => canceled.Status.Length > 0);
        Check(canceledHandler.Writes.Count == 1 && !Watched(canceled,101),
            "Collection removed before watched threshold blocks episode write instead of trusting cached state");
        canceled.Cancel();

        string corruptFolder = Path.Combine(Root, "corrupt", "config");
        Directory.CreateDirectory(corruptFolder);
        File.WriteAllText(Path.Combine(corruptFolder, "bangumi.json"), "broken-json");
        var corrupt = Store("corrupt");
        Check(corrupt.LoadError.Length > 0 && corrupt.Settings.Bindings.Count == 0,
            "Damaged feature settings do not prevent player startup");

        // Cancel a slow episode lookup when changing files; no delayed response may bind the new file.
        var slowHandler = new Handler { DelayEpisodes = true };
        var slowStore = Store("slow");
        var slowClient = new BangumiClient(slowStore, new HttpClient(slowHandler));
        await slowClient.ConnectAsync(new("unit-test-token", "refresh-token", DateTimeOffset.UtcNow.AddDays(7), ""), CancellationToken.None);
        var slow = new BangumiPlayback(slowStore, slowClient);
        slow.SetMedia("Example S1E1");
        await slowHandler.EpisodesStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        slow.SetMedia("我的摄影");
        await Task.Delay(100);
        Check(slow.Selection == null && slowHandler.CanceledEpisodes && slowHandler.Writes.Count == 0, "File switch cancels slow lookup without stale sync");
        slow.Cancel();

        await OAuthAsync();
        await client.DisconnectAsync(CancellationToken.None);
        Check(client.Account == null && store.ReadAccount() == null, "Disconnect removes saved account");
    }

    static async Task OAuthAsync()
    {
        var handler = new Handler();
        var oauth = new BangumiOAuth(new HttpClient(handler));
        var url = BangumiOAuth.AuthorizationUri("a&b", "challenge");
        Check(url.Host == "bangumi.sunuuc.de5.net" && url.Query.Contains("state=a%26b") && !url.Query.Contains("secret"), "Authorization opens project service and escapes state");
        Task? callback = null;
        var account = await oauth.AuthorizeAsync(authorization => callback = SendCallbacksAsync(authorization), CancellationToken.None);
        await callback!;
        Check(account.AccessToken == "oauth-token" && handler.TokenRequests == 1, "Valid local callback exchanges handoff exactly once");
        Task? failureCallback = null;
        try
        {
            await oauth.AuthorizeAsync(authorization => failureCallback = SendFailureAsync(authorization), CancellationToken.None);
            throw new Exception("Failed authorization accepted");
        }
        catch (InvalidOperationException error) { Check(error.Message.Contains("重新授权"), "OAuth error immediately ends waiting with a retry message"); }
        await failureCallback!;
        Check(handler.TokenRequests == 1, "OAuth error does not exchange a token");
        var retryStore = Store("oauth-retry");
        var retryPlayback = new BangumiPlayback(retryStore, new BangumiClient(retryStore, new HttpClient(handler)));
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = retryPlayback.AuthorizeAsync(_ => opened.SetResult());
        await opened.Task;
        Check(JsonDocument.Parse(retryPlayback.SnapshotJson()).RootElement.GetProperty("authorizing").GetBoolean(), "Pending login publishes authorizing state");
        Task? retryCallback = null;
        await retryPlayback.AuthorizeAsync(authorization => retryCallback = SendCallbacksAsync(authorization));
        await first;
        await retryCallback!;
        Check(retryPlayback.Client.Account != null && !JsonDocument.Parse(retryPlayback.SnapshotJson()).RootElement.GetProperty("busy").GetBoolean(),
            "Retry cancels the old listener, connects the account, and clears busy state");
        using (var proof = JsonDocument.Parse(handler.TokenBody))
        {
            string verifier = proof.RootElement.GetProperty("verifier").GetString()!;
            string challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Check(challenge == handler.Challenge && !handler.TokenBody.Contains("client_secret"), "Handoff uses SHA-256 proof and never sends App Secret");
        }
        var store = Store("refresh");
        store.SaveAccount(account with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1), Username = "test-user" });
        var client = new BangumiClient(store, new HttpClient(handler));
        await client.MarkWatchedAsync(10, 101, CancellationToken.None);
        Check(handler.TokenRequests == 3 && handler.TokenBody.Contains("refreshToken") && !handler.TokenBody.Contains("client_secret"), "Refresh uses project service without client secret");
        using var cancellation = new CancellationTokenSource();
        var pending = oauth.AuthorizeAsync(_ => cancellation.Cancel(), cancellation.Token);
        try { await pending; throw new Exception("Authorization cancellation ignored"); }
        catch (OperationCanceledException) { }
        using var listener = new HttpListener();
        listener.Prefixes.Add(BangumiOAuth.RedirectUri);
        listener.Start();
        listener.Stop();
        Check(true, "Canceled authorization releases loopback listener");

        async Task SendFailureAsync(string authorization)
        {
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(authorization).Query);
            using var browser = new HttpClient();
            using var failure = await browser.GetAsync(BangumiOAuth.RedirectUri + "?state=" + query["state"] + "&error=authorization_failed");
            Check(failure.StatusCode == HttpStatusCode.BadRequest, "OAuth failure callback acknowledged without waiting for timeout");
        }

        async Task SendCallbacksAsync(string authorization)
        {
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(authorization).Query);
            string state = query["state"]!;
            handler.Challenge = query["challenge"]!;
            using var browser = new HttpClient();
            using var bad = await browser.GetAsync(BangumiOAuth.RedirectUri + "?state=wrong&code=bad");
            Check(bad.StatusCode == HttpStatusCode.BadRequest, "Forged callback state rejected");
            using var good = await browser.GetAsync(BangumiOAuth.RedirectUri + "?state=" + state + "&code=" + new string('A', 43));
            Check(good.IsSuccessStatusCode, "Expected callback accepted");
        }
    }
    static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    sealed class Handler : HttpMessageHandler
    {
        public bool InvalidToken, Watched, DelayEpisodes, CanceledEpisodes, NotCollected;
        public int CollectionType = 3, Searches, TokenRequests, AvatarRequests;
        public string TokenBody = "", Challenge = "";
        public readonly List<(string Method, string Path, string Body)> Writes = [];
        public readonly List<(string Url, string Header)> Seen = [];
        public readonly TaskCompletionSource EpisodesStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (path == "/avatar.png")
            {
                AvatarRequests++;
                using var bitmap = new System.Drawing.Bitmap(16, 16);
                using var stream = new MemoryStream();
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(stream.ToArray()) };
            }
            if (path is "/token" or "/refresh")
            {
                TokenRequests++;
                TokenBody = body;
                return Json(JsonSerializer.Serialize(new { accessToken = "oauth-token", refreshToken = "refresh-token", expiresAt = DateTimeOffset.UtcNow.AddDays(7), username = "" }));
            }
            Seen.Add((request.RequestUri.AbsoluteUri, request.Headers.Authorization?.ToString() ?? ""));
            if (path == "/v0/me") return InvalidToken ? Json("{}", HttpStatusCode.Unauthorized) : Json("{\"username\":\"test-user\",\"id\":1,\"avatar\":{\"large\":\"https://images.example.invalid/avatar.png\"}}");
            if (path == "/v0/search/subjects")
            {
                Searches++;
                return Json("{\"total\":1,\"data\":[{\"id\":10,\"type\":2,\"name\":\"Example\",\"rating\":{\"score\":7.3}}]}");
            }
            if (path == "/v0/episodes")
            {
                EpisodesStarted.TrySetResult();
                if (DelayEpisodes)
                {
                    try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                    catch (OperationCanceledException) { CanceledEpisodes = true; throw; }
                }
                return Json("{\"total\":2,\"data\":[{\"id\":101,\"type\":0,\"ep\":1,\"sort\":1},{\"id\":102,\"type\":0,\"ep\":2,\"sort\":2}]}");
            }
            if (path == "/v0/subjects/10") return Json("{\"id\":10,\"type\":2,\"name\":\"Example\",\"rating\":{\"score\":7.3}}");
            if (path == "/v0/users/-/collections/10/episodes" && request.Method == HttpMethod.Get)
                return Json("{\"total\":2,\"data\":[{\"episode\":{\"id\":101},\"type\":0},{\"episode\":{\"id\":102},\"type\":0}]}");
            if (request.Method == HttpMethod.Get)
            {
                if (path.Contains("/episodes/")) return Json("{\"type\":" + (Watched ? 2 : 0) + "}");
                if (NotCollected) return Json("{}", HttpStatusCode.NotFound);
                return Json("{\"type\":" + CollectionType + "}");
            }
            Writes.Add((request.Method.Method, path, body));
            if (!path.Contains("episodes")) { NotCollected=false; CollectionType=JsonDocument.Parse(body).RootElement.GetProperty("type").GetInt32(); }
            return new(HttpStatusCode.NoContent);
        }
    }
}

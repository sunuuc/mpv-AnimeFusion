using System.Net;
using System.IO;
using System.Net.Http;
using System.Text;
using MpvNet.Windows.Subtitles;
using MpvNet.Windows.Trakt;
using MpvNet.Windows.MyAnimeList;

static class Program
{
    static int _checks;
    static readonly string Root = Path.Combine(Path.GetTempPath(), "AnimeFusion-SubtitleProviders-" + Guid.NewGuid().ToString("N"));
    static readonly SubtitleQuery Query = new("Fixture", 4, 8, "zh");
    const string Ass = "[Script Info]\nScriptType: v4.00+\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,字幕测试\n";
    // The ASS fixture above, compressed using the standard XZ format.
    const string AssXz = "/Td6WFoAAATm1rRGAgAhARYAAAB0L+Wj4AC4AJNdAC2UyMtHifLr5yTJxebCHTfb/GkkOgEs1COSDHvfUSrshW6QnNfR6ZWxm8Q+CW/q7qsh638zJ7Yu5LkcyowAXGERIoRjG1+NV1UFfBEQXPEo3kVfrZ/873YWf5/f9pDXRC+fluA5kNMgn/5hGxyoKb7jskCmK43XrzkqP4OXo55utka3NnFOfX4nefbuuNqCK99UAAAACl4SWxIvwe0AAa8BuQEAAHE1x3exxGf7AgAAAAAEWVo=";

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
    }

    static SubtitleResult Result(SubtitleSource source, string id = "8") => new(source, id, "Fixture S04E08", "简体中文", "简体中文", [])
    {
        Individual = true, Season = 4, Episode = 8, LanguageCode = "zh", Format = "ASS",
        DownloadName = "Fixture.S04E08.zh-Hans.ass", DownloadUrl = "https://subtitles.example/fixture.ass"
    };

    static async Task Cancelled(Task task, string message)
    {
        try { await task; }
        catch (OperationCanceledException) { Check(true, message); return; }
        throw new Exception(message);
    }

    static HttpResponseMessage Content(string content, string type = "application/json") =>
        new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, type) };

    static async Task Rejected(Func<Task> action, string message)
    {
        try { await action(); }
        catch (SubtitleServiceException) { Check(true, message); return; }
        throw new Exception(message);
    }

    static async Task TitleChecks()
    {
        const string subject = """
            {"id":1,"type":2,"name":"ゆるゆり♪♪","name_cn":"摇曳百合 第二季","platform":"TV","date":"2012-07-02","infobox":[{"key":"别名","value":[{"v":"YuruYuri: Happy Go Lily"},{"v":"Yuru Yuri Season 2"},{"v":"Yuru Yuri Season 2"},{"v":"楽園"},{"v":"Yuru Yuri ゆるゆり"}]},{"key":"中文别名","value":"摇曳动画"}]}
            """;
        const string unrelated = """
            {"id":2,"type":2,"name":"Other Anime","name_cn":"另一部动画","platform":"TV","date":"2012-07-02"}
            """;
        var catalogueKeywords = new List<string>();
        var handler = new Handler(request =>
        {
            Check(request.Headers.Authorization == null && request.RequestUri!.Host == "api.bgm.tv",
                "Alias lookup only reads the existing public catalogue without subtitle credentials.");
            if (request.RequestUri!.AbsolutePath.EndsWith("/search/subjects", StringComparison.Ordinal))
            {
                using var body = System.Text.Json.JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                catalogueKeywords.Add(body.RootElement.GetProperty("keyword").GetString()!);
            }
            return Content(request.RequestUri!.AbsolutePath.EndsWith("/search/subjects", StringComparison.Ordinal)
                ? "{\"data\":[" + subject + "," + unrelated + "]}" : request.RequestUri.AbsolutePath.EndsWith("/1", StringComparison.Ordinal) ? subject : unrelated);
        });
        using var http = new HttpClient(handler);
        var client = new MalClient(new(Path.Combine(Root, "titles-config"), Path.Combine(Root, "titles-account")), http);
        var media = TraktMedia.Parse("摇曳百合 S2E3", "episode.mkv", new Dictionary<string, string>());
        var titles = new SubtitleTitles(media, default, client);
        var localQuery = await titles.QueryAsync(media.Title, default);
        Check(handler.Count == 0 && localQuery.Title == media.Title && localQuery.Episode == 3 && localQuery.SearchSeason == 2,
            "Ordinary subtitle queries keep playback context without requesting any alias catalogue.");
        await titles.QueryAsync("另一部动画", default);
        await titles.NamesAsync(default);
        Check(catalogueKeywords.Count > 0 && catalogueKeywords.All(keyword => keyword == media.Title),
            "The alias picker requests the current playback title even after a different subtitle keyword was entered.");
        var original = await titles.QueryAsync(media.Title, default);
        Check(original.Names.Contains("摇曳百合 第二季") && original.Names.Contains("搖曳百合 第二季")
            && original.Names.Contains("ゆるゆり♪♪") && original.Names.Contains("YuruYuri: Happy Go Lily"),
            "Matched names contain simplified/traditional Chinese, original Japanese and English aliases.");
        Check(!original.Names.Contains("另一部动画") && original.Names.Count(name => name == "Yuru Yuri Season 2") == 1,
            "Aliases are deduplicated and never mixed with a different catalogue result.");
        Check(original.Names.Contains("摇曳动画") && original.Names.Contains("搖曳動畫")
            && original.Names.Contains("楽園") && !original.Names.Contains("樂園"),
            "Explicit Chinese aliases gain both scripts without transforming unlabelled Japanese kanji.");
        Check(original.Names.Contains("Yuru Yuri ゆるゆり") && original.Names.Contains("ゆるゆり"),
            "Existing bilingual title variants remain available after extracting alias lookup from the window.");
        int loaded = handler.Count;
        var english = await titles.QueryAsync("Yuru Yuri Season 2", default);
        Check(english.Title == "Yuru Yuri Season 2" && english.Episode == 3 && english.SearchSeason == 2
            && titles.IsCurrent("  Yuru Yuri Season 2  "), "An explicitly chosen alias retains the current episode and season.");
        foreach (string suffix in new[] { " S02", " E01", " S02E01" })
        {
            string keyword = "Yuru Yuri Season 2" + suffix;
            var selectedQuery = await titles.QueryAsync(keyword, default);
            Check(titles.IsCurrent(keyword) && selectedQuery.Title == keyword && selectedQuery.Episode == 3
                && selectedQuery.SearchSeason == 2 && handler.Count == loaded,
                "Visible season/episode conditions retain the playback identity and full search text without extra requests.");
        }
        Check(!titles.IsCurrent("Unrelated movie S02E01"), "A season/episode suffix never makes an unrelated title the current work.");
        Check((await titles.NamesAsync(default)).SequenceEqual(original.Names) && handler.Count == loaded,
            "Reopening the alias list or selecting another matched name reuses the catalogue request.");
        foreach (string keyword in new[] { "另一部动画", "Unrelated movie", "" })
        {
            var edited = await titles.QueryAsync(keyword, default);
            Check(edited.Title == keyword && edited.Episode == null && edited.SearchSeason == null
                && (await titles.NamesAsync(default)).SequenceEqual(original.Names) && handler.Count == loaded,
                "Editing or clearing the search text leaves the playback alias list and its cached catalogue request unchanged.");
        }
        using (var scriptHttp = new HttpClient(new Handler(request => Content(request.RequestUri!.AbsolutePath.EndsWith("/search/subjects", StringComparison.Ordinal)
            ? "{\"data\":[" + subject + "]}" : subject))))
        {
            var scriptClient = new MalClient(new(Path.Combine(Root, "script-config"), Path.Combine(Root, "script-account")), scriptHttp);
            bool terminated = true;
            for (int repeat = 0; repeat < 32; repeat++)
            {
                var scriptNames = await new SubtitleTitles(media, default, scriptClient).NamesAsync(default);
                terminated &= scriptNames.Where(name => name.StartsWith("搖曳百合", StringComparison.Ordinal))
                    .SequenceEqual(["搖曳百合 第二季"]);
            }
            Check(terminated, "Repeated native script conversion returns exact names without characters beyond the destination terminator.");
        }

        const string untranslated = """
            {"id":28900,"type":2,"name":"ゆるゆり♪♪","name_cn":"摇曳百合 第二季","platform":"TV","date":"2012-07-02","infobox":[{"key":"别名","value":[{"v":"摇曳百合 第二季\u001f"}]}]}
            """;
        const string translated = """
            {"id":12403,"title":"Yuru Yuri♪♪","alternative_titles":{"synonyms":["Yuru Yuri 2nd Season"],"en":"YuruYuri: Happy Go Lily ♪♪","ja":"ゆるゆり♪♪"},"start_date":"2012-07-03","media_type":"tv","num_episodes":12}
            """;
        int translationReads = 0;
        using (var translatedHttp = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.Host == "api.myanimelist.net")
            {
                Check(request.Headers.Authorization == null && request.RequestUri.AbsolutePath == "/v2/anime/12403",
                    "Missing English aliases use the exact existing cross-site ID without account authorization.");
                translationReads++;
                return Content(translated);
            }
            return Content(request.RequestUri.AbsolutePath.EndsWith("/search/subjects", StringComparison.Ordinal)
                ? "{\"data\":[" + untranslated + "]}" : untranslated);
        })))
        {
            var translatedClient = new MalClient(new(Path.Combine(Root, "translated-config"), Path.Combine(Root, "translated-account")), translatedHttp);
            var translatedTitles = new SubtitleTitles(media, default, translatedClient);
            var matchedNames = await translatedTitles.NamesAsync(default);
            Check(matchedNames.Contains("YuruYuri: Happy Go Lily ♪♪") && matchedNames.Contains("ゆるゆり♪♪")
                && matchedNames.Contains("搖曳百合 第二季"), "A confirmed Chinese/Japanese-only catalogue entry gains its exact MAL English title.");
            Check(matchedNames.All(name => !name.Any(char.IsControl)) && matchedNames.Count(name => name == "摇曳百合 第二季") == 1,
                "Control characters are removed before alias deduplication and display.");
            Check((await translatedTitles.QueryAsync("YuruYuri: Happy Go Lily ♪♪", default)).Episode == 3
                && translationReads == 1, "The English-title lookup is cached and selecting it preserves the current episode.");
            var unrelatedMedia = TraktMedia.Parse("其他影片 (2020)", "other.mkv", new Dictionary<string, string>());
            var typedTitles = new SubtitleTitles(unrelatedMedia, default, translatedClient);
            var otherSearch = await typedTitles.QueryAsync("摇曳百合 第二季", default);
            Check((await typedTitles.NamesAsync(default)).SequenceEqual([unrelatedMedia.Title])
                && otherSearch.Title == "摇曳百合 第二季" && otherSearch.Episode == null
                && !typedTitles.IsCurrent("YuruYuri: Happy Go Lily ♪♪") && translationReads == 1,
                "Searching for a different TV show cannot replace the current movie's alias identity or request its translated names.");
        }

        var confirmed = new BangumiNet.Api.V0.Models.Subject { Id = 28900, Name = "ゆるゆり♪♪", NameCn = "摇曳百合 第二季", Platform = "TV", Date = "2012-07-02" };
        foreach (string wrong in new[] { translated.Replace("2012-07-03", "2011-07-03", StringComparison.Ordinal),
            translated.Replace("\"media_type\":\"tv\"", "\"media_type\":\"movie\"", StringComparison.Ordinal),
            translated.Replace("ゆるゆり♪♪", "別の作品", StringComparison.Ordinal) })
        {
            using var wrongHttp = new HttpClient(new Handler(_ => Content(wrong)));
            var wrongClient = new MalClient(new(Path.Combine(Root, "wrong-config"), Path.Combine(Root, "wrong-account")), wrongHttp);
            Check(await MalMatching.MatchCatalogueAsync(wrongClient, confirmed, default) == null,
                "A cross-site ID does not admit a different premiere, medium or original title.");
        }
        int failedCatalogueReads = 0, failedTranslationReads = 0;
        bool translationRecovered = false;
        using (var failedTranslationHttp = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.Host == "api.myanimelist.net")
            {
                Check(request.RequestUri.AbsolutePath == "/v2/anime/12403",
                    "The supplemental failure fixture targets the exact mapped MAL subject endpoint.");
                failedTranslationReads++;
                return Content(translationRecovered ? translated : "[]");
            }
            Check(request.RequestUri.Host == "api.bgm.tv", "The successful base catalogue fixture remains on Bangumi.");
            failedCatalogueReads++;
            return Content(request.RequestUri.AbsolutePath.EndsWith("/search/subjects", StringComparison.Ordinal)
                ? "{\"data\":[" + untranslated + "]}" : untranslated);
        })))
        {
            var failedClient = new MalClient(new(Path.Combine(Root, "failed-translation-config"), Path.Combine(Root, "failed-translation-account")), failedTranslationHttp);
            var partialTitles = new SubtitleTitles(media, default, failedClient);
            bool rejected = false;
            try { await partialTitles.NamesAsync(default); }
            catch (System.Text.Json.JsonException) { rejected = true; }
            Check(rejected, "An invalid supplemental response is reported as an alias lookup failure rather than a successful partial result.");
            var partialNames = partialTitles.CachedNames;
            Check(partialNames.Contains("摇曳百合 第二季") && partialNames.Contains("ゆるゆり♪♪"),
                "A supplemental failure preserves already matched Chinese and Japanese names for the error view.");
            Check(failedCatalogueReads == 2 && failedTranslationReads == 1,
                "The initial failure occurs after one catalogue search/detail pair and one supplemental read.");
            translationRecovered = true;
            var recoveredNames = await partialTitles.NamesAsync(default);
            Check(recoveredNames.Contains("YuruYuri: Happy Go Lily ♪♪") && failedCatalogueReads == 2 && failedTranslationReads == 2,
                "Retrying the alias action reuses the successful Bangumi catalogue and retries only the failed supplemental lookup.");
            Check((await partialTitles.NamesAsync(default)).SequenceEqual(recoveredNames) && failedCatalogueReads == 2 && failedTranslationReads == 2,
                "After recovery, reopening the alias list reuses both successful cache layers.");
        }
        using (var ambiguousTranslationHttp = new HttpClient(new Handler(_ => Content("{\"data\":[{\"node\":" + translated + "},{\"node\":"
            + translated.Replace("12403", "12404", StringComparison.Ordinal) + "}]}"))))
        {
            var ambiguousClient = new MalClient(new(Path.Combine(Root, "ambiguous-translation-config"), Path.Combine(Root, "ambiguous-translation-account")), ambiguousTranslationHttp);
            var unmapped = new BangumiNet.Api.V0.Models.Subject { Id = int.MaxValue, Name = confirmed.Name, NameCn = confirmed.NameCn, Date = confirmed.Date, Platform = confirmed.Platform };
            Check(await MalMatching.MatchCatalogueAsync(ambiguousClient, unmapped, default) == null,
                "Multiple equally matching translated catalogue entries are not combined or silently selected.");
        }

        const string movie = """
            {"id":3,"type":2,"name":"言の葉の庭","name_cn":"言叶之庭","platform":"剧场版","date":"2013-05-31","infobox":[{"key":"别名","value":[{"v":"The Garden of Words"}]}]}
            """;
        using var movieHttp = new HttpClient(new Handler(request => Content(request.RequestUri!.AbsolutePath.EndsWith("/search/subjects", StringComparison.Ordinal)
            ? "{\"data\":[" + movie + "]}" : movie)));
        var movieTitles = new SubtitleTitles(TraktMedia.Parse("言叶之庭 (2013)", "movie.mkv", new Dictionary<string, string>()), default,
            new MalClient(new(Path.Combine(Root, "movie-config"), Path.Combine(Root, "movie-account")), movieHttp));
        await movieTitles.NamesAsync(default);
        var movieQuery = await movieTitles.QueryAsync("The Garden of Words", default);
        Check(movieQuery.Year == 2013 && movieQuery.Episode == null && movieQuery.SearchSeason == null,
            "Changing a movie search to its alias preserves the release year without inventing an episode.");

        using var ambiguousHttp = new HttpClient(new Handler(request => Content(request.RequestUri!.AbsolutePath.EndsWith("/search/subjects", StringComparison.Ordinal)
            ? "{\"data\":[" + subject + "," + subject.Replace("\"id\":1", "\"id\":4", StringComparison.Ordinal) + "]}" : subject.Replace("\"id\":1", "\"id\":" + request.RequestUri.AbsolutePath.Split('/').Last(), StringComparison.Ordinal))));
        var ambiguousTitles = new SubtitleTitles(media, default,
            new MalClient(new(Path.Combine(Root, "ambiguous-config"), Path.Combine(Root, "ambiguous-account")), ambiguousHttp));
        Check((await ambiguousTitles.NamesAsync(default)).SequenceEqual([media.Title]),
            "Equally likely catalogue matches do not merge or silently select an alias set.");

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int concurrentReads = 0;
        using var delayedHttp = new HttpClient(new AsyncHandler(async (request, token) =>
        {
            Interlocked.Increment(ref concurrentReads);
            await ready.Task.WaitAsync(token);
            return Content(request.RequestUri!.AbsolutePath.EndsWith("/search/subjects", StringComparison.Ordinal)
                ? "{\"data\":[" + subject + "]}" : subject);
        }));
        using var lifetime = new CancellationTokenSource();
        var sharedTitles = new SubtitleTitles(media, lifetime.Token,
            new MalClient(new(Path.Combine(Root, "shared-config"), Path.Combine(Root, "shared-account")), delayedHttp));
        using var waiting = new CancellationTokenSource();
        var cancelled = sharedTitles.NamesAsync(waiting.Token);
        var localDuringLookup = await sharedTitles.QueryAsync(media.Title, default);
        Check(localDuringLookup.Title == media.Title && concurrentReads == 1,
            "A subtitle search does not wait for or start supplemental metadata while the alias picker is loading.");
        await sharedTitles.QueryAsync("Another title typed while loading", default);
        var shared = sharedTitles.NamesAsync(default);
        waiting.Cancel();
        await Cancelled(cancelled, "Cancelling one alias wait does not cancel the shared metadata request.");
        ready.SetResult();
        Check((await shared).Contains("Yuru Yuri Season 2") && concurrentReads == 2
            && !sharedTitles.IsCurrent("Another title typed while loading"),
            "Concurrent alias pickers share the playback catalogue read without adopting text edited while the reply is pending.");

        using var emptyHttp = new HttpClient(new Handler(_ => Content("{\"data\":[]}")));
        var lateStore = new MalStore(Path.Combine(Root, "late-config"), Path.Combine(Root, "late-account"));
        var lateClient = new MalClient(lateStore, emptyHttp);
        var lateTitles = new SubtitleTitles(media, default, lateClient);
        Check((await lateTitles.NamesAsync(default)).SequenceEqual([media.Title]),
            "Unmatched catalogue data initially leaves only the current playback title.");
        var previousPlayback = MalPlayback.Current;
        try
        {
            var playback = new MalPlayback(lateStore, lateClient);
            typeof(MalPlayback).GetProperty(nameof(MalPlayback.Media))!.SetValue(playback, new MalMedia(media.Parsed, null, null));
            typeof(MalPlayback).GetProperty(nameof(MalPlayback.Subject))!.SetValue(playback,
                new MalSubject(1, "Yuru Yuri Season 2", null, new([], "Happy Go Lily", "ゆるゆり♪♪"), "2012-07-02", "tv", 12, null, null, null));
            typeof(MalPlayback).GetProperty(nameof(MalPlayback.Current))!.SetValue(null, playback);
            Check((await lateTitles.NamesAsync(default)).Contains("Happy Go Lily")
                && lateTitles.IsCurrent("ゆるゆり♪♪") && (await lateTitles.QueryAsync("Happy Go Lily", default)).Episode == 3,
                "An existing alias cache incorporates a later confirmed playback match and preserves its episode.");
        }
        finally { typeof(MalPlayback).GetProperty(nameof(MalPlayback.Current))!.SetValue(null, previousPlayback); }
    }

    static async Task ProviderParsingChecks()
    {
        const string title = "That Time I Got Reincarnated as a Slime S04E08";
        const string torrent = """
            {"id":4856,"name":"That Time I Got Reincarnated as a Slime S04E08","filecount":1,"state":"completed","source_date":1781883540,"episode_no":8,"sublangs":["en","zh-Hant","zh-Hans"],"anime":{"title":"Tensei Shitara Slime Datta Ken 4th Season Part 1 & 2","english_title":"That Time I Got Reincarnated as a Slime Season 4"}}
            """;
        const string detailJson = """
            {"id":4856,"name":"That Time I Got Reincarnated as a Slime S04E08","files":[{"filename":"That.Time.I.Got.Reincarnated.as.a.Slime.S04E08.mkv","attachments":[
              {"id":31639,"type":1,"info":{"codec":"ASS","lang":"zh-Hans","name":"Simplified","default":0,"cached":1,"forced":0}},
              {"id":31636,"type":1,"info":{"codec":"ASS","lang":"zh-Hant","name":"Traditional","default":0,"cached":1,"forced":0}},
              {"id":777,"type":1,"info":{"codec":"ASS","lang":"zh-Hans","name":"Signs","default":1,"cached":1,"forced":0}},
              {"id":778,"type":1,"info":{"codec":"ASS","lang":"zh-Hans","name":"Forced","default":1,"cached":1,"forced":1}},
              {"id":779,"type":1,"info":{"codec":"ASS","lang":"zh-Hans","name":"Uncached","default":1,"cached":0,"forced":0}},
              {"id":780,"type":1,"info":{"codec":"PGS","lang":"zh-Hans","name":"Bitmap","default":1,"cached":1,"forced":0}}
            ]}]}
            """;
        int details = 0;
        var handler = new Handler(request =>
        {
            var uri = request.RequestUri!;
            Check(request.Headers.Authorization == null, "Public provider metadata does not carry ASSRT credentials.");
            if (uri.AbsolutePath.EndsWith("/torrents/4856", StringComparison.Ordinal)) { details++; return Content(detailJson); }
            string query = Uri.UnescapeDataString(uri.Query);
            Check(query.Contains("q=Slime&", StringComparison.Ordinal), "TsukiHime receives the entered keyword without an implicit episode or alternate title.");
            Check(query.Contains("offset=", StringComparison.Ordinal) && !query.Contains("start=", StringComparison.Ordinal),
                "TsukiHime pagination uses its supported offset parameter.");
            return Content(query.Contains("offset=50", StringComparison.Ordinal)
                ? "{\"total\":51,\"start\":50,\"results\":[]}" : "{\"total\":51,\"start\":0,\"results\":[" + torrent + "]}");
        });
        using (var http = new HttpClient(handler))
        {
            var client = new TsukihimeClient(http);
            var query = new SubtitleQuery("Slime", null, 8, "") { SearchSeason = 4, Names = ["That Time I Got Reincarnated as a Slime Season 4"] };
            var page = await client.SearchAsync(query, 1, default);
            Check(page.Results.Count == 3 && page.Results.Select(result => result.Id).SequenceEqual(["4856:zh-Hans", "4856:zh-Hant", "4856:en"])
                && page.Results.All(result => result.Individual && result.Season == 4 && result.Episode == 8),
                "TsukiHime produces separate language rows with actual season and episode metadata.");
            Check(page.HasMore && page.PageKey == "4856", "Pagination keeps the raw torrent page identity.");
            Check(page.Results.All(result => result.TitleNames.SequenceEqual([
                    "Tensei Shitara Slime Datta Ken 4th Season Part 1 & 2", "That Time I Got Reincarnated as a Slime Season 4"])
                && result.MalId == null && result.Year == null && result.IsMovie == null),
                "Search results preserve the actual catalogue titles without inventing identifiers, a premiere year or medium.");
            var simplified = await client.DetailAsync(page.Results[0], default);
            var traditional = await client.DetailAsync(page.Results[1], default);
            Check(simplified.DownloadUrl == "https://storage.tsukihime.org/attach/00007b97/31639.xz"
                && simplified.DownloadName.EndsWith(".zh-Hans.ass.xz", StringComparison.Ordinal)
                && traditional.DownloadUrl.EndsWith("/31636.xz", StringComparison.Ordinal) && details == 1,
                "Only full cached text tracks are selected; language choices reuse one metadata request.");
            Check(simplified.TitleNames.SequenceEqual(page.Results[0].TitleNames)
                && simplified.MalId == null && simplified.Year == null && simplified.IsMovie == null,
                "A detail response without catalogue data retains known search titles and leaves absent identity fields unknown.");
            Check(!(await client.SearchAsync(query, 2, default)).HasMore, "The final TsukiHime page ends pagination.");
        }

        var explicitHandler = new Handler(request =>
        {
            Check(Uri.UnescapeDataString(request.RequestUri!.Query).Contains("q=Slime S04E08&", StringComparison.Ordinal),
                "Explicit episode text is preserved in the provider query.");
            return Content("{\"total\":0,\"start\":0,\"results\":[]}");
        });
        using (var http = new HttpClient(explicitHandler))
        {
            await new TsukihimeClient(http).SearchAsync(new("Slime S04E08", null, null, ""), 1, default);
            Check(explicitHandler.Count == 1, "An explicit episode with no result does not broaden to another episode.");
        }
        var requestedNames = new List<string>();
        using (var http = new HttpClient(new Handler(request =>
        {
            requestedNames.Add(Uri.UnescapeDataString(request.RequestUri!.Query));
            return Content("{\"total\":0,\"start\":0,\"results\":[]}");
        })))
        {
            await new TsukihimeClient(http).SearchAsync(new("ゆるゆり", null, 3, "")
                { SearchSeason = 2, Names = ["Yuru Yuri"] }, 1, default);
            Check(requestedNames.Count == 1 && requestedNames[0].Contains("q=ゆるゆり&", StringComparison.Ordinal),
                "An empty TsukiHime response does not trigger an English alias or append the playback episode.");
        }
        using (var http = new HttpClient(new Handler(_ => Content(detailJson.Replace("S04E08.mkv", "S04E09.mkv", StringComparison.Ordinal)))))
            await Rejected(() => new TsukihimeClient(http).DetailAsync(Result(SubtitleSource.Tsukihime, "4856:zh-Hans") with { Title = title }, default),
                "A subtitle file that disagrees with the selected episode is rejected.");
        using (var http = new HttpClient(new Handler(_ => Content("{\"total\":51,\"start\":0,\"results\":[" + torrent.Replace("\"filecount\":1", "\"filecount\":2", StringComparison.Ordinal) + "]}"))))
        {
            var page = await new TsukihimeClient(http).SearchAsync(new("Slime", null, null, "zh"), 1, default);
            Check(page.Results.Count == 0 && page.HasMore && page.PageKey == "4856", "Unsupported batches still retain raw pagination metadata.");
        }

        // Live API fields from mirrored release 1000760412: AnimeTosho tracks omit cached.
        const string mirroredDetail = """
            {"id":1000760412,"name":"That Time I Got Reincarnated as a Slime S04E01 New Days","animetosho":true,
             "anime":{"id":24,"title":"Tensei Shitara Slime Datta Ken 4th Season Part 1 & 2","english_title":"That Time I Got Reincarnated as a Slime Season 4","mal":59970,"anilist":182205,"anidb":18884,"release_year":2026,"is_movie":0},
             "files":[{"filename":"That.Time.I.Got.Reincarnated.as.a.Slime.S04E01.New.Days.mkv","attachments":[
              {"id":2841638,"type":1,"info":{"codec":"ASS","lang":"zh-Hans","name":"Simplified","default":0,"enabled":1,"forced":0,"trackid":36,"tracknum":37}},
              {"id":2841640,"type":1,"info":{"codec":"ASS","lang":"zh-Hant","name":"Traditional","default":0,"enabled":1,"forced":0,"trackid":37,"tracknum":38}},
              {"id":2868474,"type":1,"info":{"codec":"ASS","lang":"en","name":"Forced","default":1,"enabled":1,"forced":1,"trackid":9,"tracknum":10}},
              {"id":2841622,"type":1,"info":{"codec":"ASS","lang":"en","default":1,"enabled":1,"forced":0,"trackid":10,"tracknum":11}},
              {"id":2868476,"type":1,"info":{"codec":"SRT","lang":"en","name":"CC","default":0,"enabled":1,"forced":0,"trackid":11,"tracknum":12}}
            ]}]}
            """;
        var mirroredHandler = new Handler(_ => Content(mirroredDetail));
        SubtitleResult Mirrored(string language) => Result(SubtitleSource.Tsukihime, "1000760412:" + language)
            with { Title = "That Time I Got Reincarnated as a Slime S04E01 New Days", Episode = 1 };
        using (var http = new HttpClient(mirroredHandler))
        {
            var client = new TsukihimeClient(http);
            var simplified = await client.DetailAsync(Mirrored("zh-Hans"), default);
            var traditional = await client.DetailAsync(Mirrored("zh-Hant"), default);
            var english = await client.DetailAsync(Mirrored("en"), default);
            Check(simplified.DownloadUrl == "https://storage.tsukihime.org/tosho/attach/002b5c26/2841638.xz"
                && simplified.DownloadName.EndsWith(".zh-Hans.ass.xz", StringComparison.Ordinal)
                && simplified.Season == 4 && simplified.Episode == 1,
                "Available AnimeTosho text subtitles do not require TsukiHime's absent local cached flag.");
            Check(traditional.DownloadUrl.EndsWith("/2841640.xz", StringComparison.Ordinal)
                && english.DownloadUrl.EndsWith("/2841622.xz", StringComparison.Ordinal) && mirroredHandler.Count == 1,
                "Mirrored languages reuse one detail read and still prefer full dialogue over forced or CC tracks.");
            Check(simplified.MalId == 59970 && simplified.Year == 2026 && simplified.IsMovie == false
                && simplified.TitleNames.SequenceEqual([
                    "Tensei Shitara Slime Datta Ken 4th Season Part 1 & 2", "That Time I Got Reincarnated as a Slime Season 4"]),
                "Existing detail reads carry the real MAL identity, catalogue titles, premiere year and medium for work verification.");
        }
        using (var http = new HttpClient(new Handler(_ => Content(mirroredDetail
            .Replace("\"mal\":59970", "\"mal\":0", StringComparison.Ordinal)
            .Replace("\"release_year\":2026", "\"release_year\":0", StringComparison.Ordinal)
            .Replace("\"is_movie\":0", "\"is_movie\":null", StringComparison.Ordinal)))))
        {
            var result = await new TsukihimeClient(http).DetailAsync(Mirrored("zh-Hans"), default);
            Check(result.MalId == null && result.Year == null && result.IsMovie == null,
                "Missing or invalid catalogue identity fields are not inferred from the release's season and episode.");
        }
        using (var http = new HttpClient(new Handler(_ => Content(mirroredDetail.Replace("\"animetosho\":true", "\"animetosho\":false", StringComparison.Ordinal)))))
            await Rejected(() => new TsukihimeClient(http).DetailAsync(Mirrored("zh-Hans"), default),
                "TsukiHime's own attachments still require a confirmed local cache entry.");
        foreach (string replacement in new[] { "Signs", "Songs", "Forced" })
        {
            using var http = new HttpClient(new Handler(_ => Content(mirroredDetail.Replace("Simplified", replacement, StringComparison.Ordinal))));
            await Rejected(() => new TsukihimeClient(http).DetailAsync(Mirrored("zh-Hans"), default),
                "The AnimeTosho source does not bypass incomplete-track exclusion: " + replacement);
        }
        using (var http = new HttpClient(new Handler(_ => Content(mirroredDetail.Replace("\"codec\":\"ASS\"", "\"codec\":\"PGS\"", StringComparison.Ordinal)))))
            await Rejected(() => new TsukihimeClient(http).DetailAsync(Mirrored("zh-Hans"), default),
                "The AnimeTosho source does not bypass text-format validation.");

        await AmenzbSearchChecks();
        await AmenzbLanguageChecks();
        await AmenzbAggregateChecks();
        await AmenzbCoordinatesChecks();
        await AmenzbEpisodeRequestChecks();
        await AssrtLanguagePagingChecks();
    }

    static string BrowseRow(string id, string title, string languages, string seasonEpisode)
    {
        string data = WebUtility.HtmlEncode(System.Text.Json.JsonSerializer.Serialize(new
            { title, subs = languages, se = seasonEpisode, date = "Oct 15, 2026" }));
        return $"<tr><td><a class=\"nzb-title\" href=\"/release/{id}\" data-release=\"{data}\">Release</a></td></tr>";
    }
    static string SubtitleTrack(string owner, string id, string language, string name, string format = "ass") =>
        $"<tr data-lang=\"{language}\"><td>{language}</td><td>{name}</td><td>{format}</td><td>36 KB</td><td><a href=\"/release/{owner}/subtitles/{id}\">Download</a></td></tr>";

    static async Task AmenzbSearchChecks()
    {
        const string keyword = "Tensei shitara Slime";
        const string title = "That Time I Got Reincarnated as a Slime S04E08";
        const string owner = "172986325";
        const string withoutSubtitles = "177814480";
        const string withoutSubtitlesTitle = "[Doomdos] - Yuru Yuri Season 2 - 01 [ OV ]";
        const string yameii = "172886996";
        const string yameiiTitle = "[Yameii] Slime S04E08 [DB2A090C]";
        const string laterOwner = "175000002";
        const string laterTitle = "A third series S01E03";
        string Next(int page, bool disabled = false) => $"<a aria-label=\"Next page\" href=\"/browse?q=Tensei+shitara+Slime&amp;page={page}\""
            + (disabled ? " tabindex=\"-1\" aria-disabled=\"true\"" : "") + ">Next</a>";
        string Release(string heading, string tracks, string series = "") => $"<h1>{heading}</h1>"
            + (series.Length > 0 ? $"<a class=\"series-link\" href=\"/anime/20052\">{series}<span class=\"series-arrow\">&#8599;</span></a>" : "")
            + "<table id=\"subs-table\">" + tracks + "</table>";
        string first = BrowseRow(owner, title, "chi,eng", "S4 E8")
            + BrowseRow("173000893", "Another source-matched title S03E02", "jpn,chi", "S3 E2")
            + BrowseRow("174000001", "Movie Result (2022)", "und", "")
            + BrowseRow(withoutSubtitles, withoutSubtitlesTitle, "", "S2 E01")
            + BrowseRow(yameii, yameiiTitle, "eng", "S4 E8")
            + BrowseRow("172000005", "Missing subtitle table S04E08", "chi", "S4 E8")
            + BrowseRow("172000006", "Foreign owner only S04E08", "chi", "S4 E8")
            + BrowseRow("172000007", "Signs only S04E08", "chi", "S4 E8")
            + BrowseRow("172000008", "Bitmap subtitles only S04E08", "chi", "S4 E8")
            + BrowseRow(owner, title, "chi,eng", "S4 E8") + Next(2);
        string second = BrowseRow(laterOwner, laterTitle, "eng", "S1 E3") + Next(3, true);
        string release = Release(title,
            SubtitleTrack("173000893", "101", "chi", "Simplified")
            + SubtitleTrack(owner, "102", "eng", "English")
            + SubtitleTrack(owner, "103", "chi", "Simplified Forced")
            + SubtitleTrack(owner, "104", "chi", "Simplified Signs")
            + SubtitleTrack(owner, "105", "chi", "Simplified")
            + SubtitleTrack(owner, "106", "chi", "Traditional")
            + SubtitleTrack(owner, "107", "chi", "Simplified", "pgs")
            + SubtitleTrack(owner, "108", "eng", "English SDH"), "Tensei Shitara Slime Datta Ken");
        var releases = new Dictionary<string, string>
        {
            [owner] = release,
            ["173000893"] = Release("Another source-matched title S03E02",
                SubtitleTrack("173000893", "201", "jpn", "Dialogue") + SubtitleTrack("173000893", "202", "chi", "Chinese")),
            ["174000001"] = Release("Movie Result (2022)", SubtitleTrack("174000001", "301", "und", "Dialogue")),
            [yameii] = Release(yameiiTitle, ""),
            ["172000005"] = "<h1>Missing subtitle table S04E08</h1>",
            ["172000006"] = Release("Foreign owner only S04E08", SubtitleTrack(owner, "105", "chi", "Simplified")),
            ["172000007"] = Release("Signs only S04E08", SubtitleTrack("172000007", "701", "chi", "Simplified Forced")
                + SubtitleTrack("172000007", "702", "chi", "Simplified Signs") + SubtitleTrack("172000007", "703", "chi", "Simplified Songs")),
            ["172000008"] = Release("Bitmap subtitles only S04E08", SubtitleTrack("172000008", "801", "chi", "Simplified", "pgs")),
            [laterOwner] = Release(laterTitle, SubtitleTrack(laterOwner, "901", "eng", "English"))
        };
        var paths = new List<string>();
        var handler = new Handler(request =>
        {
            var uri = request.RequestUri!;
            paths.Add(uri.AbsolutePath);
            Check(request.Method == HttpMethod.Get && uri.Host == "amenzb.moe", "Search uses only direct public ameNZB metadata requests.");
            if (uri.AbsolutePath == "/browse")
            {
                var parameters = uri.Query.TrimStart('?').Split('&').ToDictionary(pair => pair.Split('=')[0],
                    pair => Uri.UnescapeDataString(pair[(pair.IndexOf('=') + 1)..].Replace('+', ' ')));
                Check(parameters["q"] == keyword && parameters.Keys.Order().SequenceEqual(parameters.ContainsKey("sub_lang")
                    ? ["page", "q", "sub_lang"] : ["page", "q"])
                    && (!parameters.TryGetValue("sub_lang", out string? selectedLanguage) || selectedLanguage == "ko"),
                    "ameNZB sends the exact keyword and page with only the explicitly selected supported language.");
                Check(parameters["page"] is "1" or "2", "Browse requests use the requested original page number.");
                return Content(parameters["page"] == "1" ? first : second, "text/html");
            }
            Check(uri.AbsolutePath.StartsWith("/release/", StringComparison.Ordinal)
                && releases.ContainsKey(uri.AbsolutePath[9..]) && uri.Query.Length == 0,
                "Only releases advertising subtitles are resolved, never suggestions, anime pages or subtitle downloads.");
            return Content(releases[uri.AbsolutePath[9..]], "text/html");
        });
        SubtitleResult extracted, unknown;
        using (var http = new HttpClient(handler))
        {
            var client = new AmenzbClient(http);
            var query = new SubtitleQuery(keyword, null, null, "");
            var page = await client.SearchAsync(query, 1, default);
            Check(page.Results.Count == 6 && page.HasMore && handler.Count == 9 && paths[0] == "/browse",
                "Search first browses once, then resolves each of eight nonempty-subs releases once before offering six actual subtitle languages.");
            Check(paths.Skip(1).Distinct().Count() == 8 && paths.Count(path => path == "/release/" + owner) == 1,
                "Duplicate browse rows and multiple languages share one release-detail request.");
            Check(!paths.Contains("/release/" + withoutSubtitles), "A release with empty subs never triggers a detail request.");
            Check(page.PageKey.Split(',').ToHashSet().SetEquals(new[] { owner, "173000893", "174000001", withoutSubtitles,
                yameii, "172000005", "172000006", "172000007", "172000008" }) && page.PageKey.Split(',').Length == 9,
                "The pagination cursor retains every distinct browse release even when its attachments are unavailable.");
            extracted = page.Results.Single(result => result.Id == owner + ":zh-Hans");
            unknown = page.Results.Single(result => result.Id == "174000001:other");
            Check(extracted.Title == title && extracted.Individual && extracted.Season == 4 && extracted.Episode == 8
                && extracted.LanguageCode == "zh-Hans", "Original server titles and coordinates survive attachment resolution without adding track names.");
            Check(page.Results.Where(result => result.Id.StartsWith(owner + ":", StringComparison.Ordinal)).Select(result => result.LanguageCode)
                .ToHashSet().SetEquals(["zh-Hans", "zh-Hant", "en"]),
                "Actual simplified, traditional and English attachments become separate canonical language IDs.");
            Check(page.Results.All(result => result.Id.EndsWith(":" + result.LanguageCode, StringComparison.Ordinal)),
                "All result IDs use normalized attachment language codes instead of browse-language abbreviations.");
            Check(page.Results.Any(result => result.Id == "173000893:ja" && result.Season == 3 && result.Episode == 2)
                && page.Results.Any(result => result.Id == "173000893:zh")
                && unknown.Season == null && unknown.Episode == null && unknown.LanguageCode == "other",
                "Other seasons and movies stay searchable, unknown Chinese remains Chinese, and an actual und track becomes other.");
            Check(page.Results.All(result => result.DownloadUrl.Length > 0 && result.DownloadName.Length > 0
                && result.PackageNames.Contains(result.DownloadName) && result.TitleNames.Contains(result.Title)),
                "Every offered result already contains its actual downloadable attachment, filename and source title.");
            Check(extracted.DownloadUrl == "https://amenzb.moe/release/172986325/subtitles/105" && extracted.Format == "ASS"
                && extracted.DownloadName.EndsWith(".Simplified.ass", StringComparison.Ordinal)
                && page.Results.Single(result => result.Id == owner + ":zh-Hant").DownloadUrl.EndsWith("/106", StringComparison.Ordinal)
                && page.Results.Single(result => result.Id == owner + ":en").DownloadUrl.EndsWith("/102", StringComparison.Ordinal),
                "Only the selected owner's complete text tracks are offered, preferring ordinary dialogue over SDH.");
            Check(extracted.TitleNames.Contains("Tensei Shitara Slime Datta Ken") && !extracted.TitleNames.Any(name => name.Contains('↗')),
                "Search retains the actual source series alias without its navigation icon.");
            Check(!page.Results.Any(result => new[] { withoutSubtitles, yameii, "172000005", "172000006", "172000007", "172000008" }
                .Any(id => result.Id.StartsWith(id + ":", StringComparison.Ordinal))),
                "Advertised video languages cannot fabricate results for missing attachments, foreign owners, signs, forced tracks or PGS.");
            int resolvedRequests = handler.Count;
            foreach (var result in page.Results)
                Check(await client.DetailAsync(result, default) == result, "Selecting a fully resolved result reuses exactly its existing attachment.");
            Check(handler.Count == resolvedRequests, "Selecting resolved languages makes no second detail request.");
            var later = await client.SearchAsync(query, 2, default);
            Check(later.Results.Count == 1 && later.Results[0].Id == laterOwner + ":en" && later.Results[0].Season == 1
                && later.Results[0].Episode == 3 && !later.HasMore && later.PageKey != page.PageKey && handler.Count == 11,
                "The second original browse page resolves its attachment and a disabled next link ends pagination.");
            var filteredContext = await client.SearchAsync(query with { Season = 9, Episode = 19, SearchSeason = 9, Year = 1999, Names = ["Unrelated alias"] }, 1, default);
            Check(filteredContext.Results.Select(result => result.Id).SequenceEqual(page.Results.Select(result => result.Id)) && handler.Count == 11,
                "Playback context cannot delete results or repeat successful browse and detail requests within the same session.");
            var filteredLanguage = await client.SearchAsync(query with { Language = "ko" }, 1, default);
            Check(filteredLanguage.Results.Count == 0 && filteredLanguage.HasMore && filteredLanguage.PageKey == page.PageKey && handler.Count == 12,
                "Selecting a new language requests its browse page while reusing resolved details and preserving pagination with no matching attachments.");
            var current = TraktMedia.Parse("关于我转生变成史莱姆这档事 S04E08", "", new Dictionary<string, string>());
            var work = new SubtitleWork(current, ["Tensei Shitara Slime Datta Ken 4th Season"], null);
            var match = work.ForDownload(new(keyword, null, null, ""), extracted);
            Check(match.Current && match.Query.Title == current.Title && match.Query.Episode == 8,
                "The resolved source series name associates a short search keyword with the current canonical library.");
        }
        var reuseHandler = new Handler(_ => throw new Exception("A resolved result must not issue another detail request."));
        using (var http = new HttpClient(reuseHandler))
        {
            var client = new AmenzbClient(http);
            Check(await client.DetailAsync(extracted, default) == extracted && await client.DetailAsync(unknown, default) == unknown
                && reuseHandler.Count == 0, "Resolved text and und attachments remain reusable across client instances without requests.");
        }
        foreach (var invalid in new[]
        {
            release.Replace("S04E08", "S04E09", StringComparison.Ordinal),
            Release(title, SubtitleTrack(owner, "102", "eng", "English")),
            Release(title, SubtitleTrack("173000893", "101", "chi", "Simplified")),
            Release(title, SubtitleTrack(owner, "103", "chi", "Simplified Forced")
                + SubtitleTrack(owner, "104", "chi", "Simplified Signs") + SubtitleTrack(owner, "106", "chi", "Simplified Songs")),
            Release(title, SubtitleTrack(owner, "107", "chi", "Simplified", "pgs"))
        })
        {
            using var http = new HttpClient(new Handler(_ => Content(invalid, "text/html")));
            await Rejected(() => new AmenzbClient(http).DetailAsync(extracted with { DownloadUrl = "", DownloadName = "" }, default),
                "An unresolved selection cannot switch language, episode or owner, or choose an incomplete or bitmap track.");
        }
        using (var http = new HttpClient(new Handler(_ => Content(Release("Movie Result (2022)",
            SubtitleTrack("174000001", "302", "eng", "English")), "text/html"))))
            await Rejected(() => new AmenzbClient(http).DetailAsync(unknown with { DownloadUrl = "", DownloadName = "" }, default),
                "An unavailable other-language track must not silently fall back to an English attachment.");
        var ownerHandler = new Handler(request =>
        {
            Check(request.RequestUri!.AbsolutePath == "/release/" + owner, "An untrusted attachment owner is resolved using the selected release only.");
            return Content(release, "text/html");
        });
        using (var http = new HttpClient(ownerHandler))
        {
            var resolved = await new AmenzbClient(http).DetailAsync(extracted with
                { DownloadUrl = "https://amenzb.moe/release/173000893/subtitles/101" }, default);
            Check(ownerHandler.Count == 1 && resolved.DownloadUrl == extracted.DownloadUrl,
                "A prefilled attachment from another owner cannot bypass detail validation.");
        }
        foreach (string mismatched in new[] { release.Replace("S04E08", "S04E09", StringComparison.Ordinal),
            release.Replace("S04E08", "S05E08", StringComparison.Ordinal), release.Replace(title, "A completely different series S04E08", StringComparison.Ordinal) })
        {
            var mismatchHandler = new Handler(request => Content(request.RequestUri!.AbsolutePath == "/browse"
                ? BrowseRow(owner, title, "chi,eng", "S4 E8") + Next(2) : mismatched, "text/html"));
            using var http = new HttpClient(mismatchHandler);
            var page = await new AmenzbClient(http).SearchAsync(new(keyword, null, null, ""), 1, default);
            Check(page.Results.Count == 0 && page.HasMore && page.PageKey == owner && mismatchHandler.Count == 2,
                "A release detail with a conflicting title, season or episode is skipped without losing browse pagination.");
        }
        foreach (string empty in new[] { "", BrowseRow(withoutSubtitles, withoutSubtitlesTitle, "", "S2 E01"), BrowseRow(yameii, yameiiTitle, "eng", "S4 E8") })
        {
            int browsed = 0, detailed = 0;
            using var http = new HttpClient(new Handler(request =>
            {
                string path = request.RequestUri!.AbsolutePath;
                if (path == "/browse") return Content(++browsed == 1 ? empty + Next(2) : second, "text/html");
                detailed++;
                Check(path == "/release/" + yameii || path == "/release/" + laterOwner,
                    "Pagination resolves only advertised nonempty-subs releases.");
                return Content(releases[path[9..]], "text/html");
            }));
            using var search = new SubtitleSearch(new AmenzbClient(http));
            var found = await search.SearchAsync(keyword, _ => Task.FromResult(new SubtitleQuery(keyword, null, null, "")), new SearchProgress(), default);
            Check(found.Complete && found.Errors.Count == 0 && found.Results.Count == 1 && found.Results[0].Id == laterOwner + ":en"
                && browsed == 2 && detailed == (empty.Contains(yameii, StringComparison.Ordinal) ? 2 : 1),
                "An empty browse page, empty subs, or advertised language with no attachment still advances to the next source page.");
        }
        int pages = 0, details = 0;
        using (var http = new HttpClient(new Handler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/browse") return Content(++pages == 1 ? BrowseRow(laterOwner, laterTitle, "eng", "S1 E3") + Next(2)
                : BrowseRow("175000003", "A third series S01E04", "chi", "S1 E4"), "text/html");
            details++;
            Check(path is "/release/175000002" or "/release/175000003", "Language-filtered pagination resolves each advertised release once.");
            return Content(path.EndsWith(laterOwner, StringComparison.Ordinal) ? releases[laterOwner]
                : Release("A third series S01E04", SubtitleTrack("175000003", "902", "chi", "Simplified")), "text/html");
        })))
        using (var search = new SubtitleSearch(new AmenzbClient(http)))
        {
            var found = await search.SearchAsync(keyword, _ => Task.FromResult(new SubtitleQuery(keyword, null, null, "zh")), new SearchProgress(), default, language: "zh");
            Check(found.Complete && found.Errors.Count == 0 && found.Results.Count == 1
                && found.Results[0].Id == "175000003:zh-Hans" && pages == 2 && details == 2,
                "A page emptied by actual attachment language filtering still reaches later pages with the requested language.");
        }
        int browseRequests = 0, firstDetailRequests = 0, secondDetailRequests = 0;
        bool refreshed = false;
        var secondDetailStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDetailCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var http = new HttpClient(new AsyncHandler(async (request, token) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/browse")
            {
                Interlocked.Increment(ref browseRequests);
                return Content(BrowseRow(owner, title, "chi,eng", "S4 E8")
                    + BrowseRow(laterOwner, laterTitle, "eng", "S1 E3"), "text/html");
            }
            if (path == "/release/" + owner)
            {
                Interlocked.Increment(ref firstDetailRequests);
                return Content(refreshed ? release.Replace("/subtitles/105", "/subtitles/205", StringComparison.Ordinal) : release, "text/html");
            }
            Check(path == "/release/" + laterOwner, "Paused search requests only its unresolved second release.");
            if (Interlocked.Increment(ref secondDetailRequests) == 1)
            {
                secondDetailStarted.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    secondDetailCancelled.TrySetResult();
                    throw;
                }
            }
            return Content(releases[laterOwner], "text/html");
        })))
        using (var search = new SubtitleSearch(new AmenzbClient(http)))
        {
            var progress = new SearchProgress();
            Task<SubtitleQuery> Query(CancellationToken _) => Task.FromResult(new SubtitleQuery(keyword, null, null, ""));
            var pending = search.SearchAsync(keyword, Query, progress, default);
            await secondDetailStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(browseRequests == 1 && firstDetailRequests == 1 && secondDetailRequests == 1,
                "The second detail is pending after exactly one browse and one completed detail.");
            Check(progress.Last is { Complete: false } && progress.Last.Results.Count == 3
                && progress.Last.Results.All(result => result.Id.StartsWith(owner + ":", StringComparison.Ordinal)),
                "Verified languages appear through search progress before the next detail or the whole page finishes.");
            Check(search.SetPaused(keyword, true), "An in-flight ameNZB detail request can be paused.");
            await secondDetailCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!pending.IsCompleted && browseRequests == 1 && firstDetailRequests == 1 && secondDetailRequests == 1,
                "Pausing cancels the unfinished detail while preserving the active search and completed metadata.");
            Check(progress.Last is { Paused: true } && progress.Last.Results.Count == 3,
                "Pausing preserves the verified partial-page results already displayed to the user.");
            Check(search.SetPaused(keyword, false), "The paused search accepts a resume request.");
            var found = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Check(found.Complete && !found.Paused && found.Errors.Count == 0 && found.Results.Count == 4
                && browseRequests == 1 && firstDetailRequests == 1 && secondDetailRequests == 2,
                "Resuming retries only the cancelled second detail, without repeating browse or the first detail.");
            Check(found.Results.Select(result => result.Id).Distinct().Count() == found.Results.Count,
                "Replayed cached progress after resume does not duplicate previously displayed subtitle rows.");
            Check(ReferenceEquals(search.SearchAsync(keyword, Query, progress, default), pending)
                && browseRequests == 1 && firstDetailRequests == 1 && secondDetailRequests == 2,
                "Reopening a completed search reuses its session without additional requests.");
            refreshed = true;
            var refresh = await search.SearchAsync(keyword, Query, progress, default, refresh: true).WaitAsync(TimeSpan.FromSeconds(10));
            Check(refresh.Complete && refresh.Errors.Count == 0 && refresh.Results.Count == 4
                && browseRequests == 2 && firstDetailRequests == 2 && secondDetailRequests == 3,
                "Explicit refresh creates a new session and requests browse and each detail once again.");
            Check(refresh.Results.Single(result => result.Id == owner + ":zh-Hans").DownloadUrl.EndsWith("/205", StringComparison.Ordinal)
                && found.Results.Single(result => result.Id == owner + ":zh-Hans").DownloadUrl.EndsWith("/105", StringComparison.Ordinal),
                "An explicit refresh uses newly resolved attachments instead of stale session metadata.");
        }
    }

    static async Task AmenzbLanguageChecks()
    {
        const string keyword = "字幕原词 & Slime";
        const string title = "Fixture S01E01";
        string chinese = BrowseRow("900001", title, "chi,eng", "S1 E1");
        string english = BrowseRow("900002", title, "eng", "S1 E1");
        string japanese = BrowseRow("900003", title, "jpn", "S1 E1");
        string unknown = BrowseRow("900004", title, "und", "S1 E1");
        var browseRequests = new List<(string Language, int Page)>();
        var detailRequests = new List<string>();
        var details = new Dictionary<string, string>
        {
            ["900001"] = SubtitleTrack("900001", "1", "chi", "Simplified")
                + SubtitleTrack("900001", "2", "chi", "Traditional")
                + SubtitleTrack("900001", "3", "chi", "Chinese")
                + SubtitleTrack("900001", "4", "eng", "English"),
            ["900002"] = SubtitleTrack("900002", "5", "eng", "English"),
            ["900003"] = SubtitleTrack("900003", "6", "jpn", "Japanese"),
            ["900004"] = SubtitleTrack("900004", "7", "und", "Dialogue"),
            ["900005"] = SubtitleTrack("900005", "8", "chi", "Traditional")
        };
        var handler = new Handler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/browse")
            {
                var parameters = uri.Query.TrimStart('?').Split('&').ToDictionary(pair => pair.Split('=')[0],
                    pair => Uri.UnescapeDataString(pair[(pair.IndexOf('=') + 1)..].Replace('+', ' ')));
                Check(parameters["q"] == keyword && parameters.Keys.All(key => key is "q" or "page" or "sub_lang"),
                    "Language-scoped browsing preserves the exact Unicode keyword and sends no other search restrictions.");
                string language = parameters.GetValueOrDefault("sub_lang", "");
                Check(language is "" or "zh" or "en" or "ja" or "fil",
                    "Only supported public browse language values are sent; script tags, all, other and unknown codes are never sent.");
                int page = int.Parse(parameters["page"], System.Globalization.CultureInfo.InvariantCulture);
                browseRequests.Add((language, page));
                if (page == 2)
                {
                    Check(language == "zh", "Later Chinese search pages retain the same server language filter.");
                    return Content(BrowseRow("900005", title, "chi", "S1 E1")
                        + "<a aria-label=\"Next page\" href=\"/browse?page=3&amp;sub_lang=zh\">Next</a>", "text/html");
                }
                return Content(language switch
                {
                    "zh" => chinese,
                    "en" => english,
                    "ja" => japanese,
                    "fil" => "",
                    _ => chinese + english + japanese + unknown
                }, "text/html");
            }
            Check(uri.AbsolutePath.StartsWith("/release/", StringComparison.Ordinal) && details.ContainsKey(uri.AbsolutePath[9..]),
                "Language searches resolve only returned release details.");
            string id = uri.AbsolutePath[9..];
            detailRequests.Add(id);
            return Content($"<h1>{title}</h1><table id=\"subs-table\">{details[id]}</table>", "text/html");
        });
        using var http = new HttpClient(handler);
        var client = new AmenzbClient(http);
        var query = new SubtitleQuery(keyword, null, null, "zh-Hans");
        var simplified = await client.SearchAsync(query, 1, default);
        Check(simplified.Results.Select(result => result.Id).SequenceEqual(["900001:zh-Hans"])
            && browseRequests.SequenceEqual(new[] { ("zh", 1) }) && detailRequests.SequenceEqual(["900001"]),
            "Simplified Chinese searches the server's zh category and keeps only confirmed simplified attachments.");
        var en = await client.SearchAsync(query with { Language = "en" }, 1, default);
        var ja = await client.SearchAsync(query with { Language = "ja" }, 1, default);
        Check(en.Results.Select(result => result.Id).SequenceEqual(["900002:en"])
            && ja.Results.Select(result => result.Id).SequenceEqual(["900003:ja"])
            && browseRequests.SequenceEqual(new[] { ("zh", 1), ("en", 1), ("ja", 1) }),
            "Different selected languages request separate source pages instead of reusing another language's browse cache.");
        var traditional = await client.SearchAsync(query with { Language = "zh-Hant" }, 1, default);
        Check(traditional.Results.Select(result => result.Id).SequenceEqual(["900001:zh-Hant"])
            && browseRequests.Last() == ("zh", 1) && detailRequests.Count(id => id == "900001") == 1,
            "Traditional Chinese also sends zh but remains distinct locally and reuses complete release metadata.");
        var generic = await client.SearchAsync(query with { Language = "zh" }, 1, default);
        Check(generic.Results.Select(result => result.LanguageCode).ToHashSet().SetEquals(["zh-Hans", "zh-Hant", "zh"]),
            "A generic Chinese selection includes known scripts and genuinely unclassified Chinese without including English.");
        int beforeRepeat = handler.Count;
        var again = await client.SearchAsync(query, 1, default);
        Check(again.Results.Select(result => result.Id).SequenceEqual(simplified.Results.Select(result => result.Id)) && handler.Count == beforeRepeat,
            "Revisiting the same language within a session reuses only its own completed page.");
        var all = await client.SearchAsync(query with { Language = "" }, 1, default);
        Check(all.Results.Count == 7 && browseRequests.Last() == ("", 1), "All languages omit sub_lang and preserve every available attachment language.");
        int beforeAll = handler.Count;
        var namedAll = await client.SearchAsync(query with { Language = "all" }, 1, default);
        Check(namedAll.Results.Count == all.Results.Count && handler.Count == beforeAll, "The all selection uses the unfiltered page without an invalid all parameter.");
        var other = await client.SearchAsync(query with { Language = "other" }, 1, default);
        Check(other.Results.Select(result => result.Id).SequenceEqual(["900004:other"]) && browseRequests.Last() == ("", 1),
            "Other languages never send an unsupported parameter and still require an actual unknown-language attachment.");
        var unsupported = await client.SearchAsync(query with { Language = "not-a-language" }, 1, default);
        Check(unsupported.Results.Count == 0 && browseRequests.Last() == ("", 1), "Unknown language values are not forwarded to ameNZB or used to invent matches.");
        var filipino = await client.SearchAsync(query with { Language = "fil" }, 1, default);
        Check(filipino.Results.Count == 0 && browseRequests.Last() == ("fil", 1), "The site's explicitly listed fil value is supported even though it has three letters.");
        var emptyScript = await client.SearchAsync(query, 2, default);
        Check(emptyScript.Results.Count == 0 && emptyScript.HasMore && emptyScript.PageKey == "900005",
            "A Chinese source page with only traditional attachments remains empty for simplified selection without stopping pagination.");
    }

    static async Task AmenzbAggregateChecks()
    {
        const string firstOwner = "910001", secondOwner = "910002", laterOwner = "910003", hiddenOwner = "919999";
        const string firstTitle = "Fixture S04E08", secondTitle = "Different Fixture S02E03", laterTitle = "Fixture S04E09";
        string Detail(string title, string tracks) => $"<h1>{title}</h1>"
            + "<a class=\"series-link\" href=\"/anime/42\">First Owner Alias</a>"
            + $"<table id=\"subs-table\">{tracks}</table>";
        string firstPage = BrowseRow(firstOwner, firstTitle, "chi", "S4 E8")
            + BrowseRow(secondOwner, secondTitle, "chi,eng", "S2 E3")
            + "<a aria-label=\"Next page\" href=\"/browse?page=2\">Next</a>";
        string secondPage = BrowseRow(laterOwner, laterTitle, "chi", "S4 E9");
        string aggregate = Detail(firstTitle, SubtitleTrack(firstOwner, "1001", "chi", "Simplified")
            + SubtitleTrack(secondOwner, "1002", "chi", "Simplified")
            + SubtitleTrack(laterOwner, "1003", "chi", "Simplified")
            + SubtitleTrack(hiddenOwner, "1999", "chi", "Simplified"));
        var paths = new List<string>();
        var handler = new Handler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            if (path == "/browse")
                return Content(request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal) ? secondPage : firstPage, "text/html");
            if (path == "/release/" + firstOwner) return Content(aggregate, "text/html");
            if (path == "/release/" + secondOwner) return Content(Detail(secondTitle,
                SubtitleTrack(secondOwner, "1002", "chi", "Simplified")
                + SubtitleTrack(secondOwner, "1004", "chi", "Traditional")
                + SubtitleTrack(secondOwner, "1005", "eng", "English")), "text/html");
            if (path == "/release/" + secondOwner + "/subtitles/1002") return Content(Ass, "text/plain");
            throw new Exception("Unexpected aggregate fixture request: " + path);
        });
        using (var http = new HttpClient(handler))
        {
            var client = new AmenzbClient(http);
            var query = new SubtitleQuery("Fixture", null, null, "zh-Hans");
            var batches = new List<IReadOnlyList<SubtitleResult>>();
            var progress = new BatchProgress(items =>
            {
                Check(paths.SequenceEqual(["/browse", "/release/" + firstOwner]),
                    "Each verified owner is reported immediately without waiting for another detail request.");
                batches.Add(items.ToArray());
            });
            var first = await client.SearchAsync(query, 1, default, progress);
            Check(first.Results.Select(result => result.Id).SequenceEqual([firstOwner + ":zh-Hans", secondOwner + ":zh-Hans"])
                && first.HasMore && paths.SequenceEqual(["/browse", "/release/" + firstOwner]),
                "One aggregate detail verifies two actual browse releases and skips the second redundant detail request.");
            Check(batches.Count == 2 && batches.All(batch => batch.Count == 1)
                && batches.SelectMany(batch => batch).Select(result => result.Id).SequenceEqual(first.Results.Select(result => result.Id)),
                "Provider progress publishes verified rows in browse order rather than waiting for the full page.");
            var second = first.Results.Single(result => result.Id == secondOwner + ":zh-Hans");
            Check(second.Title == secondTitle && second.Season == 2 && second.Episode == 3
                && second.DownloadUrl == "https://amenzb.moe/release/910002/subtitles/1002"
                && second.DownloadName == secondTitle + ".Simplified.ass"
                && second.TitleNames.SequenceEqual([secondTitle]),
                "A reused owner retains its own browse title, season, episode, filename and attachment URL without inheriting the first owner's alias.");
            Check(first.Results.All(result => !result.Id.StartsWith(hiddenOwner + ":", StringComparison.Ordinal)
                && !result.Id.StartsWith(laterOwner + ":", StringComparison.Ordinal)),
                "Aggregated attachment owners absent from the current browse page are not inserted as search results.");
            var laterBatches = new List<IReadOnlyList<SubtitleResult>>();
            var later = await client.SearchAsync(query, 2, default, new BatchProgress(items => laterBatches.Add(items.ToArray())));
            Check(later.Results.Single().Id == laterOwner + ":zh-Hans" && later.Results[0].Title == laterTitle
                && later.Results[0].Season == 4 && later.Results[0].Episode == 9 && !later.HasMore
                && later.PageKey == laterOwner && paths.SequenceEqual(["/browse", "/release/" + firstOwner, "/browse"])
                && laterBatches.Single().Single() == later.Results[0],
                "A later browse page reuses the exact owner's positive attachment while retaining its own pagination and episode metadata.");
            int beforeSelection = handler.Count;
            Check(await client.DetailAsync(second, default) == second && handler.Count == beforeSelection,
                "Selecting an aggregate-resolved subtitle reuses its exact attachment without a second detail lookup.");
            var library = new SubtitleLibrary(Path.Combine(Root, "aggregate-owner-download"));
            var download = new SubtitleDownload(http, library);
            var current = new SubtitleQuery("Different Fixture", 2, 3, "zh-Hans");
            string? saved = await download.DownloadAsync(current, second, null, [], null, default);
            Check(saved != null && File.ReadAllText(saved) == Ass && paths.Last() == "/release/910002/subtitles/1002"
                && handler.Count == beforeSelection + 1,
                "A reused attachment downloads and saves the selected owner's real subtitle instead of borrowing the first release's file.");
            Check(await download.DownloadAsync(current, second, null, [], null, default) == saved && handler.Count == beforeSelection + 1
                && library.CurrentFile(current with { Episode = 4 }, library.Results(current.Title).Single(), library.Results(current.Title).Single().Files) == null,
                "The downloaded aggregate result is reused locally and cannot become another episode's subtitle.");
            var all = await client.SearchAsync(query with { Language = "" }, 1, default);
            Check(paths.Count(path => path == "/release/" + secondOwner) == 1
                && all.Results.Where(result => result.Id.StartsWith(secondOwner + ":", StringComparison.Ordinal))
                    .Select(result => result.LanguageCode).ToHashSet().SetEquals(["zh-Hans", "zh-Hant", "en"]),
                "An all-language request still reads the owner's own detail rather than treating a partial positive cache as complete metadata.");
        }

        var invalidOwners = new[]
        {
            (Id: "920002", Name: "English", Language: "eng", Format: "ass"),
            (Id: "920003", Name: "Simplified Forced", Language: "chi", Format: "ass"),
            (Id: "920004", Name: "Simplified Signs", Language: "chi", Format: "ass"),
            (Id: "920005", Name: "Simplified Songs", Language: "chi", Format: "ass"),
            (Id: "920006", Name: "Simplified", Language: "chi", Format: "pgs")
        };
        string rootRow = BrowseRow("920001", firstTitle, "chi", "S4 E8");
        string invalidPage = rootRow + string.Concat(invalidOwners.Select(item => BrowseRow(item.Id, firstTitle, "chi,eng", "S4 E8")))
            + BrowseRow("920007", firstTitle, "chi", "S4 E8");
        string invalidAggregate = Detail(firstTitle, SubtitleTrack("920001", "2001", "chi", "Simplified")
            + string.Concat(invalidOwners.Select(item => SubtitleTrack(item.Id, item.Id, item.Language, item.Name, item.Format))));
        var detailOwners = new List<string>();
        using (var http = new HttpClient(new Handler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/browse") return Content(invalidPage, "text/html");
            string owner = path["/release/".Length..];
            detailOwners.Add(owner);
            return Content(owner == "920001" ? invalidAggregate : Detail(firstTitle, SubtitleTrack(owner, "3" + owner, "chi", "Simplified")), "text/html");
        })))
        {
            var results = await new AmenzbClient(http).SearchAsync(new("Fixture", null, null, "zh-Hans"), 1, default);
            Check(detailOwners.SequenceEqual(new[] { "920001" }.Concat(invalidOwners.Select(item => item.Id)).Append("920007"))
                && results.Results.Count == 7,
                "A missing owner, wrong language, forced, signs, songs or bitmap track never supplies a false positive or negative cache hit.");
            Check(results.Results.Where(result => result.Id != "920001:zh-Hans")
                .All(result => result.DownloadUrl.EndsWith("/subtitles/3" + result.Id.Split(':')[0], StringComparison.Ordinal)),
                "Rejected aggregate candidates are replaced only by each actual owner's complete simplified dialogue track.");
        }
    }

    static async Task AmenzbCoordinatesChecks()
    {
        // Exact source titles from the reported search; attachment bytes remain isolated fixtures.
        var cases = new[]
        {
            (Id: "177803833", Position: "Movie", Season: (int?)null, Episode: (int?)null,
                Browse: "[ToonsHub] That Time I Got Reincarnated a Slime the Movie Tears of the Azure Sea (2026) DDP5.1 H.264 (Tensei Shitara Slime Datta Ken Movie: Sokai no Namida-hen, Multi-Subs)",
                Detail: "[ToonsHub] That Time I Got Reincarnated a Slime the Movie Tears of the Azure Sea (2026) DDP5.1 H.264"),
            (Id: "173026624", Position: "Movie", Season: (int?)null, Episode: (int?)null,
                Browse: "[ToonsHub] That Time I Got Reincarnated a Slime the Movie Tears of the Azure Sea (2026) AAC2.0 H.264 (Tensei Shitara Slime Datta Ken: Soukai no Namida-hen, Multi-Subs)",
                Detail: "[ToonsHub] That Time I Got Reincarnated a Slime the Movie Tears of the Azure Sea (2026) AAC2.0 H.264"),
            (Id: "176517364", Position: "S3 E20", Season: (int?)4, Episode: (int?)20,
                Browse: "[AnoZu] That Time I Got Reincarnated a Slime S04E20 AAC 2.0 H.264 | Tensei shitara Slime Datta Ken 4th Season",
                Detail: "[AnoZu] That Time I Got Reincarnated a Slime S04E20 AAC 2.0 H.264 | Tensei shitara Slime Datta Ken 4th Season"),
            (Id: "176087063", Position: "S2 E90", Season: (int?)-1, Episode: (int?)null,
                Browse: "[Doomdos] - That Time I Got Reincarnated a Slime Season 4",
                Detail: "[Doomdos] - That Time I Got Reincarnated a Slime Season 4"),
            (Id: "173000965", Position: "Specials E10", Season: (int?)-1, Episode: (int?)null,
                Browse: "[Mehul] That Time I Got Reincarnated a Slime S00E10-E12 (Visions of Coleus OVA 2023) ( HEVC Opus) [Dual-Audio] [Multi-Sub] | Tensei shitara Slime Datta Ken: Coleus no Yume",
                Detail: "[Mehul] That Time I Got Reincarnated a Slime S00E10-E12 (Visions of Coleus OVA 2023) ( HEVC Opus) [Dual-Audio] [Multi-Sub] | Tensei shitara Slime Datta Ken: Coleus no Yume"),
            (Id: "178177171", Position: "S41 E86", Season: (int?)41, Episode: (int?)86,
                Browse: "[Kosaka] That Time I Got Reincarnated a Slime - 86 ( AV1 OPUS) [MultiSubs] {Tags:L0;V7;C3;A=ja;S=en,ar,de,es419,frfr,,,ms,pl,ru,th,vi;}",
                Detail: "[Kosaka] That Time I Got Reincarnated a Slime - 86 ( AV1 OPUS) [MultiSubs] {Tags:L0;V7;C3;A=ja;S=en,ar,de,es419,frfr,,,ms,pl,ru,th,vi;}")
        };
        var paths = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            if (path == "/browse") return Content(string.Concat(cases.Select(item => BrowseRow(item.Id, item.Browse, "chi", item.Position))), "text/html");
            if (path.Contains("/subtitles/", StringComparison.Ordinal)) return Content(Ass, "text/plain");
            var item = cases.Single(item => path == "/release/" + item.Id);
            return Content($"<h1>{item.Detail}</h1><table id=\"subs-table\">{SubtitleTrack(item.Id, "1" + item.Id, "chi", "Simplified")}</table>", "text/html");
        }));
        var client = new AmenzbClient(http);
        var page = await client.SearchAsync(new("Tensei shitara Slime", null, null, "zh-Hans"), 1, default);
        Check(page.Results.Count == cases.Length && paths.Count == cases.Length + 1,
            "Same-title films, explicit episode names and unresolved ranges all remain visible when their own text attachments exist.");
        foreach (var item in cases)
        {
            var result = page.Results.Single(result => result.Id == item.Id + ":zh-Hans");
            Check(result.Title == item.Browse && result.Season == item.Season && result.Episode == item.Episode
                && result.DownloadUrl == "https://amenzb.moe/release/" + item.Id + "/subtitles/1" + item.Id,
                item.Id + ": explicit title coordinates supersede conflicting catalogue numbers; ambiguous coordinates stay unclassified, and unnamed seasons stay as reported.");
            if (item.Season == null)
                Check(result.TitleNames.Contains(item.Browse) && !result.DownloadName.Contains("Tensei Shitara", StringComparison.Ordinal),
                    "Removing a film's long alias from its detail heading does not reject the same film or remove its original search title.");
            if (item.Season != -1) continue;
            var library = new SubtitleLibrary(Path.Combine(Root, "unclassified-" + item.Id));
            var download = new SubtitleDownload(http, library);
            var current = new SubtitleQuery("That Time I Got Reincarnated a Slime", 4, 20, "zh-Hans");
            Check(await download.DownloadAsync(current, result, null, [], null, default) == null,
                "A multi-episode range or conflicting unspecified episode is saved without automatically selecting it for the current episode.");
            var local = library.Results(current.Title).Single();
            Check(local.AvailableSeasons.SequenceEqual(new int?[] { -1 }) && local.Episode == null && local.Files.Count == 1
                && local.Files[0].LocalPath.Contains("未分类", StringComparison.Ordinal) && File.ReadAllText(local.Files[0].LocalPath) == Ass,
                "Unresolved source coordinates remain downloadable in the unclassified shelf rather than adopting the current playback episode.");
        }
    }

    static async Task AmenzbEpisodeRequestChecks()
    {
        var releases = new[]
        {
            (Id: "930001", Title: "Fixture S04E08", Position: "S4 E8"),
            (Id: "930002", Title: "Fixture S04E02", Position: "S4 E2"),
            (Id: "930003", Title: "Fixture S02E08", Position: "S2 E8"),
            (Id: "930004", Title: "Fixture S00E08", Position: "Specials E08"),
            (Id: "930005", Title: "Fixture (2026)", Position: "Movie"),
            (Id: "930006", Title: "Fixture S04E08", Position: "S3 E8"),
            (Id: "930007", Title: "Fixture S04E08-E09", Position: "S4 E8"),
            (Id: "930008", Title: "Fixture S04E08", Position: "S4 E8")
        };
        string firstPage = string.Concat(releases.Take(7).Select(item => BrowseRow(item.Id, item.Title, "chi", item.Position)))
            + "<a aria-label=\"Next page\" href=\"/browse?page=2\">Next</a>";
        string pageKey = string.Join(',', releases.Take(7).Select(item => item.Id));
        var requests = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            if (path == "/browse")
            {
                var parameters = request.RequestUri.Query.TrimStart('?').Split('&').ToDictionary(part => part.Split('=')[0],
                    part => Uri.UnescapeDataString(part[(part.IndexOf('=') + 1)..].Replace('+', ' ')));
                Check(parameters["q"] == "Fixture" && parameters["sub_lang"] == "zh"
                    && parameters.Keys.ToHashSet().SetEquals(["q", "page", "sub_lang"]),
                    "Episode requests keep the exact user keyword and do not send unsupported browse season/episode parameters.");
                return Content(parameters["page"] == "1" ? firstPage
                    : BrowseRow("930008", "Fixture S04E08", "chi", "S4 E8"), "text/html");
            }
            var item = releases.Single(item => path == "/release/" + item.Id);
            string tracks = SubtitleTrack(item.Id, "1" + item.Id, "chi", "Simplified");
            if (item.Id == "930001")
                tracks += SubtitleTrack("930003", "1930003", "chi", "Simplified")
                    + SubtitleTrack("939999", "1939999", "chi", "Simplified");
            return Content($"<h1>{item.Title}</h1><table id=\"subs-table\">{tracks}</table>", "text/html");
        }));
        var client = new AmenzbClient(http);
        var query = new SubtitleQuery("Fixture", 1, 1, "zh-Hans") { RequestSeason = 4, RequestEpisode = 8 };
        var batches = new List<IReadOnlyList<SubtitleResult>>();
        var selected = await client.SearchAsync(query, 1, default, new BatchProgress(items => batches.Add(items.ToArray())));
        Check(selected.Results.Select(item => item.Id).SequenceEqual(["930001:zh-Hans", "930006:zh-Hans"])
            && selected.Results.All(item => item.Season == 4 && item.Episode == 8)
            && requests.SequenceEqual(["/browse", "/release/930001", "/release/930006"]),
            "Only requested episode owners need details, including explicit title coordinates corrected from a wrong catalogue season.");
        Check(selected.HasMore && selected.PageKey == pageKey
            && batches.SelectMany(items => items).SequenceEqual(selected.Results),
            "Episode filtering preserves the complete raw page identity and publishes only matching verified results.");

        var otherEpisode = await client.SearchAsync(query with { RequestEpisode = 2 }, 1, default);
        Check(otherEpisode.Results.Single().Id == "930002:zh-Hans" && otherEpisode.PageKey == pageKey
            && requests.Count(path => path == "/browse") == 1 && requests.Last() == "/release/930002",
            "Changing the requested episode reuses raw browse metadata but resolves only that episode's actual owner.");
        int beforeOtherSeason = requests.Count;
        var otherSeason = await client.SearchAsync(query with { RequestSeason = 2 }, 1, default);
        Check(otherSeason.Results.Single().Id == "930003:zh-Hans" && otherSeason.Results[0].Season == 2
            && otherSeason.Results[0].Episode == 8 && requests.Count == beforeOtherSeason,
            "A previously cached foreign owner appears only after its actual browse season and episode are requested.");
        var special = await client.SearchAsync(query with { RequestSeason = 0 }, 1, default);
        Check(special.Results.Single().Id == "930004:zh-Hans" && special.Results[0].Season == 0
            && requests.Last() == "/release/930004",
            "Season zero selects only specials and is distinct from the unrestricted season value.");

        int beforeEmpty = requests.Count;
        var empty = await client.SearchAsync(query with { RequestEpisode = 5 }, 1, default);
        var emptyLast = await client.SearchAsync(query with { RequestEpisode = 5 }, 2, default);
        Check(empty.Results.Count == 0 && empty.HasMore && empty.PageKey == pageKey
            && emptyLast.Results.Count == 0 && !emptyLast.HasMore && emptyLast.PageKey == "930008"
            && requests.Skip(beforeEmpty).SequenceEqual(["/browse"]),
            "Pages without the selected episode remain pageable and never fetch unrelated details.");
        var later = await client.SearchAsync(query, 2, default);
        Check(later.Results.Single().Id == "930008:zh-Hans" && !later.HasMore
            && requests.Last() == "/release/930008" && requests.Count(path => path == "/browse") == 2,
            "A raw page cached by a nonmatching request can later resolve its matching episode without another browse request.");

        var seasonOnly = await client.SearchAsync(query with { RequestEpisode = null }, 1, default);
        Check(seasonOnly.Results.Select(item => item.Id).SequenceEqual(["930001:zh-Hans", "930002:zh-Hans", "930006:zh-Hans"]),
            "Selecting all episodes in one season includes every normalized single episode of that season.");
        var episodeOnly = await client.SearchAsync(query with { RequestSeason = null }, 1, default);
        Check(episodeOnly.Results.Select(item => item.Id).SequenceEqual(["930001:zh-Hans", "930003:zh-Hans", "930004:zh-Hans", "930006:zh-Hans"]),
            "An unrestricted season with one episode includes that episode across actual seasons, including specials.");
        var all = await client.SearchAsync(query with { RequestSeason = null, RequestEpisode = null }, 1, default);
        Check(all.Results.Select(item => item.Id).SequenceEqual(releases.Take(7).Select(item => item.Id + ":zh-Hans"))
            && all.Results.Single(item => item.Id == "930005:zh-Hans").Season == null
            && all.Results.Single(item => item.Id == "930007:zh-Hans").Season == -1
            && requests.TakeLast(2).SequenceEqual(["/release/930005", "/release/930007"]),
            "Clearing both request filters restores films and unclassified packs while retaining the per-owner detail cache.");
        Check(all.Results.All(item => !item.Id.StartsWith("939999:", StringComparison.Ordinal))
            && requests.Count(path => path == "/browse") == 2,
            "An aggregate attachment owner absent from browse never appears, even when episode filters are cleared.");
        int beforeSelection = requests.Count;
        Check(await client.DetailAsync(selected.Results[0], default) == selected.Results[0] && requests.Count == beforeSelection,
            "An episode-scoped result retains its resolved attachment and needs no duplicate detail request when selected.");
    }

    static async Task AssrtLanguagePagingChecks()
    {
        const string keyword = "Fixture subtitles";
        var store = new SubtitleStore(Path.Combine(Root, "assrt-language-pages"));
        store.Save(new(new string('a', 32)));
        var positions = new List<string>();
        var handler = new Handler(request =>
        {
            Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/sub/search",
                "ASSRT filtered pagination only searches subtitle metadata.");
            string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var parameters = body.Split('&').ToDictionary(pair => pair.Split('=')[0],
                pair => Uri.UnescapeDataString(pair[(pair.IndexOf('=') + 1)..].Replace('+', ' ')));
            Check(parameters["q"] == keyword && parameters["cnt"] == "15" && parameters["filelist"] == "1",
                "ASSRT pagination preserves the entered keyword and original source page size.");
            positions.Add(parameters["pos"]);
            Check(positions.Count <= 2 && parameters["pos"] is "0" or "15", "The filtered search advances exactly one raw source page.");
            var subs = parameters["pos"] == "0"
                ? Enumerable.Range(1, 15).Select(id => new
                    { id, videoname = "Fixture S01E01", subtype = "ASS", lang = new { desc = "英语", langlist = new Dictionary<string, bool> { ["langeng"] = true } } }).ToArray()
                : [new { id = 16, videoname = "Fixture S01E01", subtype = "ASS", lang = new { desc = "简体中文", langlist = new Dictionary<string, bool> { ["langchs"] = true } } }];
            return Content(System.Text.Json.JsonSerializer.Serialize(new { status = 0, sub = new { subs } }));
        });
        using var http = new HttpClient(handler);
        using var search = new SubtitleSearch(new AssrtClient(http, store));
        var result = await search.SearchAsync(keyword, _ => Task.FromResult(new SubtitleQuery(keyword, null, null, "zh-Hans")),
            new SearchProgress(), default, language: "zh-Hans");
        Check(result.Complete && result.Errors.Count == 0 && positions.SequenceEqual(["0", "15"])
            && result.Results.Count == 1 && result.Results[0].Id == "16" && result.Results[0].LanguageCode == "zh-Hans",
            "A full ASSRT page with no selected language still reaches a later page containing simplified Chinese.");
    }

    static async Task ExactSearchChecks()
    {
        const string title = "摇曳百合: 第二季!";
        var query = new SubtitleQuery(title, null, 3, "") { SearchSeason = 2, Names = ["Yuru Yuri", "ゆるゆり"] };
        var store = new SubtitleStore(Path.Combine(Root, "exact-search"));
        store.Save(new(new string('a', 32)));
        foreach (var source in Enum.GetValues<SubtitleSource>())
        {
            var handler = new Handler(request =>
            {
                string parameters = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? request.RequestUri!.Query.TrimStart('?');
                string sent = Uri.UnescapeDataString(parameters.Split('&').Single(value => value.StartsWith("q=", StringComparison.Ordinal))[2..].Replace('+', ' '));
                Check(sent == title, source + " sends exactly the selected input without translating, broadening or appending episode text.");
                return Content(source switch
                {
                    SubtitleSource.Assrt => "{\"status\":0,\"sub\":{\"subs\":[]}}",
                    SubtitleSource.Amenzb => "<html><body>No releases</body></html>",
                    _ => "{\"total\":0,\"start\":0,\"results\":[]}"
                }, source == SubtitleSource.Amenzb ? "text/html" : "application/json");
            });
            using var http = new HttpClient(handler);
            ISubtitleClient client = source switch
            {
                SubtitleSource.Assrt => new AssrtClient(http, store),
                SubtitleSource.Amenzb => new AmenzbClient(http),
                _ => new TsukihimeClient(http)
            };
            var result = await client.SearchAsync(query, 1, default);
            Check(handler.Count == 1 && result.Results.Count == 0 && !result.HasMore,
                source + " stops after the entered keyword has no results instead of requesting cached aliases.");
        }
    }

    static async Task SearchChecks()
    {
        var pages = new List<int>();
        var assrt = new Client(SubtitleSource.Assrt, (_, page, _) =>
        {
            pages.Add(page);
            return Task.FromResult(new SubtitlePage([Result(SubtitleSource.Assrt, page.ToString())], page < 2));
        });
        var amenzb = new Client(SubtitleSource.Amenzb, (_, _, _) =>
            Task.FromException<SubtitlePage>(new SubtitleServiceException("ameNZB：搜索失败。")));
        string tsukihimeId = "1";
        var tsukihime = new Client(SubtitleSource.Tsukihime, (_, _, _) =>
            Task.FromResult(new SubtitlePage([Result(SubtitleSource.Tsukihime, tsukihimeId)], false)));
        var progress = new SearchProgress();
        int queryCalls = 0;
        Task<SubtitleQuery> MakeQuery(CancellationToken token) { token.ThrowIfCancellationRequested(); queryCalls++; return Task.FromResult(Query); }
        using var search = new SubtitleSearch(assrt, amenzb, tsukihime);
        var first = await search.SearchAsync(Query.Title, MakeQuery, progress, default, language: Query.Language);
        Check(first.Complete && first.Results.Count == 3, "Successful sources remain available when another source fails.");
        Check(first.Errors.Keys.SequenceEqual([SubtitleSource.Amenzb]), "Only the failed source reports an error.");
        Check(pages.SequenceEqual([1, 2]), "A source is paged once until its final page.");
        Check(first.Results.Count(result => result.Id == "1") == 2, "Identical IDs from different sources remain distinct.");
        Check(progress.Count > 1, "Results are reported as providers complete, without polling.");
        var cached = await search.SearchAsync("  fixture  ", MakeQuery, progress, default, language: Query.Language);
        Check(ReferenceEquals(first, cached) && queryCalls == 1 && tsukihime.Calls == 1 && amenzb.Calls == 1,
            "Reopening or filtering a cached query does not request services again.");
        var refreshed = await search.SearchAsync(Query.Title, MakeQuery, progress, default, refresh: true, language: Query.Language);
        Check(!ReferenceEquals(first, refreshed) && queryCalls == 2 && tsukihime.Calls == 2 && amenzb.Calls == 2,
            "An explicit refresh replaces the cached query and retries every source.");
        tsukihimeId = "updated";
        var single = await search.SearchAsync(Query.Title, MakeQuery, progress, default, refresh: true, sources: [SubtitleSource.Tsukihime], language: Query.Language);
        Check(single.Complete && single.Results.Count == 3 && assrt.Calls == 4 && amenzb.Calls == 2 && tsukihime.Calls == 3,
            "Refreshing one source keeps other cached rows and contacts only the selected provider.");
        Check(single.Errors.ContainsKey(SubtitleSource.Amenzb), "A single-source refresh retains another source's earlier failure state.");
        Check(single.Results.Single(item => item.Source == SubtitleSource.Tsukihime).Id == "updated"
            && single.Results.Any(item => item.Source == SubtitleSource.Assrt && item.Id == "1"),
            "Refreshed rows replace that provider's previous rows without deleting another provider's identical IDs.");
        var local = await search.SearchAsync(Query.Title, MakeQuery, progress, default, refresh: true, sources: [], language: Query.Language);
        Check(local.Complete && local.Results.Count == 3 && assrt.Calls == 4 && amenzb.Calls == 2 && tsukihime.Calls == 3,
            "Refreshing local-only results performs no provider requests and preserves cached online rows.");
        var another = await search.SearchAsync("Another keyword", MakeQuery, progress, default, refresh: true, sources: [SubtitleSource.Tsukihime], language: Query.Language);
        Check(another.Results.Count == 1 && assrt.Calls == 4 && amenzb.Calls == 2 && tsukihime.Calls == 4,
            "A new keyword searches only the selected provider and never inherits another keyword's cache.");

        var denied = new Handler(_ => throw new Exception("An absent ASSRT key must never reach the network."));
        using var http = new HttpClient(denied);
        var open = new Client(SubtitleSource.Tsukihime, (_, _, _) => Task.FromResult(new SubtitlePage([Result(SubtitleSource.Tsukihime)], false)));
        using var withoutKey = new SubtitleSearch(new AssrtClient(http, new SubtitleStore(Path.Combine(Root, "no-key"))), open);
        var openResults = await withoutKey.SearchAsync("Fixture", _ => Task.FromResult(Query), new SearchProgress(), default, language: Query.Language);
        Check(openResults.Results.Count == 1 && open.Calls == 1 && denied.Count == 0 && openResults.Errors.Count == 0,
            "Unconfigured ASSRT is skipped without blocking sources that do not require a key.");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new Client(SubtitleSource.Tsukihime, async (_, _, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new([], false);
        });
        using var cancellation = new CancellationTokenSource();
        using var pendingSearch = new SubtitleSearch(blocked);
        Task<SubtitleSearchResult> pending = pendingSearch.SearchAsync("cancelled", _ => Task.FromResult(Query), new SearchProgress(), cancellation.Token, language: Query.Language);
        await started.Task;
        cancellation.Cancel();
        await Cancelled(pending, "Changing media cancels the active provider request.");

        int refreshCalls = 0;
        var staleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshing = new Client(SubtitleSource.Amenzb, async (_, _, token) =>
        {
            if (Interlocked.Increment(ref refreshCalls) == 1)
            {
                staleStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return new([Result(SubtitleSource.Amenzb, "new")], false);
        });
        using var refreshSearch = new SubtitleSearch(refreshing);
        var stale = refreshSearch.SearchAsync("refresh", _ => Task.FromResult(Query), new SearchProgress(), default, language: Query.Language);
        await staleStarted.Task;
        var fresh = await refreshSearch.SearchAsync("refresh", _ => Task.FromResult(Query), new SearchProgress(), default, refresh: true, language: Query.Language);
        await Cancelled(stale, "Explicit refresh cancels the older request before replacing its results.");
        Check(fresh.Results.Single().Id == "new" && refreshCalls == 2, "Only the current refresh result is retained.");

        var lateReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int lateCalls = 0;
        var late = new Client(SubtitleSource.Tsukihime, async (_, _, _) =>
        {
            if (Interlocked.Increment(ref lateCalls) == 1)
            {
                lateStarted.SetResult();
                await lateReady.Task;
                return new([Result(SubtitleSource.Tsukihime, "obsolete")], false);
            }
            return new([Result(SubtitleSource.Tsukihime, "current")], false);
        });
        using var lateSearch = new SubtitleSearch(late);
        var obsoleteProgress = new SearchProgress();
        var obsolete = lateSearch.SearchAsync("late", _ => Task.FromResult(Query), obsoleteProgress, default, language: Query.Language);
        await lateStarted.Task;
        var current = await lateSearch.SearchAsync("late", _ => Task.FromResult(Query), new SearchProgress(), default,
            refresh: true, sources: [SubtitleSource.Tsukihime], language: Query.Language);
        lateReady.SetResult();
        await Cancelled(obsolete, "A provider response that ignores cancellation is rejected when it eventually arrives.");
        Check(obsoleteProgress.Count == 1 && current.Results.Single().Id == "current",
            "A delayed obsolete response cannot publish progress or contaminate the replacement cache.");

        var filteredPages = new List<int>();
        var filtered = new Client(SubtitleSource.Tsukihime, (_, page, _) =>
        {
            filteredPages.Add(page);
            if (page > 3) throw new Exception("A repeating provider page must stop pagination.");
            return Task.FromResult(page == 1 ? new SubtitlePage([], true) { PageKey = "non-subtitle-releases" }
                : new SubtitlePage([Result(SubtitleSource.Tsukihime, "retained")], true) { PageKey = "subtitle-release" });
        });
        using var filteredSearch = new SubtitleSearch(filtered);
        var filteredResult = await filteredSearch.SearchAsync("filtered", _ => Task.FromResult(Query), new SearchProgress(), default, language: Query.Language);
        Check(filteredPages.SequenceEqual([1, 2, 3]) && filteredResult.Results.Single().Id == "retained" && filteredResult.Errors.Count == 0,
            "A page without extracted subtitles does not hide later pages, while a repeated page stops pagination.");
    }

    static TraktMedia Media(int season, int episode) => TraktMedia.Parse($"Fixture S{season:00}E{episode:00}", "", new Dictionary<string, string>());

    static async Task DownloadChecks()
    {
        foreach (bool compressed in new[] { false, true })
        {
            var library = new SubtitleLibrary(Path.Combine(Root, compressed ? "xz" : "text"));
            var handler = new Handler(request =>
            {
                Check(request.Headers.Authorization == null, "Subtitle downloads do not carry service credentials.");
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(compressed ? Convert.FromBase64String(AssXz) : Encoding.UTF8.GetBytes(Ass)) };
            });
            using var http = new HttpClient(handler);
            var download = new SubtitleDownload(http, library);
            var result = Result(compressed ? SubtitleSource.Tsukihime : SubtitleSource.Amenzb) with
            {
                DownloadName = compressed ? "Fixture.S04E08.zh-Hans.ass.xz" : "Fixture.S04E08.chi.txt"
            };
            var wrongEpisode = await download.DownloadAsync(Query with { Episode = 7 }, result, null, [], null, default);
            Check(wrongEpisode == null && handler.Count == 1, "A different episode is saved but is not returned for playback.");
            Check(library.Tracks(Media(4, 7)).Count == 0 && library.Tracks(Media(4, 8)).Count == 1,
                "Individual subtitles are indexed under their actual episode.");
            var path = await download.DownloadAsync(Query, result, null, [], null, default);
            Check(path != null && path.EndsWith(".ass", StringComparison.OrdinalIgnoreCase), "XZ and ASS content served as TXT are saved as loadable ASS files.");
            Check(File.ReadAllText(path!) == Ass, "Decompression and extension detection preserve subtitle text.");
            Check(handler.Count == 1, "A previously saved individual subtitle is reused without a second download.");
            Check(await download.DownloadAsync(Query with { Season = 3 }, result, null, [], null, default) == null && handler.Count == 1,
                "A different current season cannot load the cached subtitle.");
            Check(library.Tracks(Media(3, 8)).Count == 0 && path!.Contains("第 4 季"), "A mismatched current season does not change the storage season.");
            var local = library.Results(Query.Title).Single();
            Check(local.Local && local.Files.Count == 1, "Individual downloads appear in the existing local subtitle library.");
            Check(!File.ReadAllText(Path.Combine(library.SeasonDirectory(Query.Title, 4), "subtitles.json")).Contains("https://", StringComparison.Ordinal),
                "The local index does not store temporary download URLs.");
        }

        var namedLibrary = new SubtitleLibrary(Path.Combine(Root, "metadata-episode"));
        var namedHandler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Ass)) });
        using var namedHttp = new HttpClient(namedHandler);
        var namedDownload = new SubtitleDownload(namedHttp, namedLibrary);
        var namedResult = Result(SubtitleSource.Amenzb, "generic") with { DownloadName = "subtitle.chi.txt" };
        string? namedPath = await namedDownload.DownloadAsync(Query, namedResult, null, [], null, default);
        var namedLocal = namedLibrary.Results(Query.Title).Single();
        Check(namedPath != null && namedLibrary.CurrentFile(Query, namedLocal, namedLocal.Files)?.LocalPath == namedPath,
            "A generic subtitle filename can be selected using the recorded episode.");
        Check(namedLibrary.CurrentFile(Query with { Episode = 7 }, namedLocal, namedLocal.Files) == null,
            "A generic local subtitle filename does not permit loading it for another episode.");
        Check(namedLibrary.CurrentFile(Query with { Episode = null }, namedLocal, namedLocal.Files) == null,
            "An unknown current episode does not select a cached episode subtitle.");
    }

    static async Task LiveChecks(string source)
    {
        using var handler = new LiveHandler();
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        ISubtitleClient client = source == "amenzb" ? new AmenzbClient(http) : new TsukihimeClient(http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        if (source == "amenzb")
        {
            var broad = new SubtitleQuery("Tensei shitara Slime", null, null, "");
            for (int number = 1; number <= 2; number++)
            {
                var browsing = await client.SearchAsync(broad, number, timeout.Token);
                Check(browsing.Results.Count > 0, "ameNZB broad search without a current episode returns source releases on both initial pages.");
                if (!browsing.HasMore) break;
            }
        }
        var query = new SubtitleQuery("That Time I Got Reincarnated as a Slime S04E08", null, 8, "") { SearchSeason = 4 };
        using var search = new SubtitleSearch(client);
        var page = await search.SearchAsync(query.Title, _ => Task.FromResult(query), new SearchProgress(), timeout.Token, language: query.Language);
        if (page.Errors.Count > 0) throw page.Errors.Values.First();
        Check(page.Results.Any(result => result.LanguageCode.StartsWith("zh", StringComparison.Ordinal)), source + " search returns Chinese subtitles.");
        var chosen = page.Results.First(result => result.LanguageCode.StartsWith("zh", StringComparison.Ordinal) && result.Season == 4 && result.Episode == 8);
        var detail = await client.DetailAsync(chosen, timeout.Token);
        Check(detail.Individual && detail.DownloadUrl.Length > 0, source + " resolves an independently downloadable subtitle.");
        var library = new SubtitleLibrary(Path.Combine(Root, "live-" + source));
        var download = new SubtitleDownload(http, library);
        var path = await download.DownloadAsync(query with { Season = detail.Season, Episode = detail.Episode, Language = "zh" }, detail, null, [], null, timeout.Token);
        Check(path != null && File.ReadAllText(path).Contains("[Script Info]", StringComparison.Ordinal), source + " downloads a valid ASS subtitle.");
        Check(File.ReadAllText(path!).Any(character => character is >= '\u4e00' and <= '\u9fff'), source + " subtitle contains Chinese text.");
        int requests = handler.Count;
        Check(await download.DownloadAsync(query with { Season = detail.Season, Episode = detail.Episode, Language = "zh" }, detail, null, [], null, timeout.Token) == path
            && handler.Count == requests, source + " live subtitle reuses the local download.");
        Console.WriteLine($"{source}: search rows={page.Results.Count}, selected={detail.Id}, S{detail.Season}E{detail.Episode}, subtitle bytes={new FileInfo(path!).Length}, HTTP requests={requests}; no video requests.");
    }

    static async Task EpisodeIdentityChecks()
    {
        var rangeLibrary = new SubtitleLibrary(Path.Combine(Root, "range-only-package"));
        var rangeFile = new SubtitleFile(SubtitleSource.Assrt, "range", 0, "Fixture S04E08–E10.ass", "");
        var rangePackage = new SubtitleResult(SubtitleSource.Assrt, "range", "Fixture", "中文", "", [rangeFile]);
        Check(rangeLibrary.CurrentFile(Query, rangePackage, rangePackage.Files) == null,
            "A package with one range-named file cannot fall back to the current episode simply because no single episode was parsed.");
        const string title = "[LbE3L] Hanaori-san Still Wants to Fight in the Next S01E01–E04 [ AV1 Opus 2.0 Multi-Audio MSubs]";
        const string owner = "176456154";
        string detail = "<h1>" + title + "</h1><table id=\"subs-table\">"
            + SubtitleTrack(owner, "181748", "chi", "FANSUBS ASS) Chinese – 简体中文 [Nekomoe kissaten]") + "</table>";
        using (var http = new HttpClient(new Handler(request => Content(request.RequestUri!.AbsolutePath == "/browse"
            ? BrowseRow(owner, title.Replace("–E04", "", StringComparison.Ordinal), "chi", "S1 E01") : detail, "text/html"))))
        {
            var page = await new AmenzbClient(http).SearchAsync(new("Hanaori-san", 1, 1, "zh-Hans")
                { RequestSeason = 1, RequestEpisode = 1 }, 1, default);
            Check(page.Results.Count == 0, "A detail page revealing a collection overrides a single-episode browse row before results are offered.");
        }
        var handler = new Handler(request => Content(request.RequestUri!.AbsolutePath == "/browse"
            ? BrowseRow(owner, title, "chi", "S1 E01") : detail, "text/html"));
        using (var http = new HttpClient(handler))
        {
            var client = new AmenzbClient(http);
            var query = new SubtitleQuery("Hanaori-san", 1, 1, "zh-Hans");
            var page = await client.SearchAsync(query, 1, default);
            var result = page.Results.Single();
            Check(result.Season == -1 && result.Episode == null && result.EpisodesFor(1).Count == 0,
                "A real Unicode episode-range release cannot label its extracted attachment as the catalogue's first episode.");
            Check(!result.MatchesFilter(null, "zh-Hans", 1, 1) && !result.MatchesFilter(null, "zh-Hans", 1, 4),
                "An attachment with no per-file coordinates is not offered as either endpoint of its collection.");
            int before = handler.Count;
            foreach (int episode in new[] { 1, 4 })
                Check((await client.SearchAsync(query with { RequestSeason = 1, RequestEpisode = episode }, 1, default)).Results.Count == 0,
                    "An episode-scoped search cannot substitute an unidentified collection attachment.");
            Check(handler.Count == before, "Excluding unidentified collection attachments does not add detail or MediaInfo requests.");
            string payload = Ass.Replace("ScriptType:", "Title: [Nekomoe kissaten] Hanaori-san wa Tensei shitemo Kenka ga Shitai [04][Web].SC\nScriptType:", StringComparison.Ordinal);
            using var files = new HttpClient(new Handler(_ => Content(payload, "text/plain")));
            var library = new SubtitleLibrary(Path.Combine(Root, "range-attachment"));
            Check(await new SubtitleDownload(files, library).DownloadAsync(query, result, null, [], null, default) == null,
                "Downloading the collection's fourth-episode track does not return a first-episode playback path.");
            var local = library.Results(query.Title).Single();
            Check(local.AvailableSeasons.SequenceEqual(new int?[] { -1 }) && local.Files[0].Episode == null
                && File.ReadAllText(local.Files[0].LocalPath) == payload,
                "Unidentified attachments retain their original bytes in the unclassified shelf.");
            await Rejected(() => Task.Run(() => library.Import(TraktMedia.Parse("Hanaori-san S01E01", "", new Dictionary<string, string>()),
                local.Files[0].LocalPath)), "Manually importing a collection-named track cannot silently assign the first episode.");
        }
        foreach (SubtitleSource source in new[] { SubtitleSource.Amenzb, SubtitleSource.Tsukihime })
        foreach (string contentTitle in new[] { "[Group] Fixture [09][Web].SC", "Fixture S05E08", "Fixture S04E08–E10" })
        {
            string payload = Ass.Replace("ScriptType:", "Title: " + contentTitle + "\nScriptType:", StringComparison.Ordinal);
            using var http = new HttpClient(new Handler(_ => Content(payload, "text/plain")));
            var library = new SubtitleLibrary(Path.Combine(Root, "conflicting-content-" + Guid.NewGuid().ToString("N")));
            await Rejected(() => new SubtitleDownload(http, library).DownloadAsync(Query, Result(source), null, [], null, default),
                source + ": an explicit conflicting episode or season inside the actual subtitle is rejected.");
            Check(library.Results(Query.Title).Count == 0 && !Directory.EnumerateFiles(library.Root, "*", SearchOption.AllDirectories).Any(),
                "A conflicting subtitle is not written or indexed before reporting download failure.");
        }
        using (var http = new HttpClient(new Handler(_ => Content(Ass.Replace("ScriptType:", "Title: Fixture S04E08\nScriptType:", StringComparison.Ordinal), "text/plain"))))
        {
            var library = new SubtitleLibrary(Path.Combine(Root, "matching-content"));
            Check(await new SubtitleDownload(http, library).DownloadAsync(Query, Result(SubtitleSource.Amenzb), null, [], null, default) != null,
                "A subtitle whose embedded title agrees with the selected episode remains loadable.");
        }
    }

    static async Task IndividualWorkChecks()
    {
        var movieQuery = new SubtitleQuery("Spirited Away", null, null, "zh") { Year = 2001 };
        var movieResult = Result(SubtitleSource.Tsukihime, "movie") with
        {
            Title = "Spirited Away (2001)", Season = null, Episode = null,
            DownloadName = "Spirited.Away.2001.zh-Hans.ass"
        };
        var cases = new[]
        {
            (Name: "correct-movie", Query: movieQuery, Result: movieResult, Match: true),
            (Name: "different-movie", Query: movieQuery, Result: movieResult with
                { Title = "Your Name (2016)", DownloadName = "Your.Name.2016.zh-Hans.ass" }, Match: false),
            (Name: "different-year", Query: movieQuery, Result: movieResult with
                { Title = "Spirited Away (2020)", DownloadName = "Spirited.Away.2020.zh-Hans.ass" }, Match: false),
            (Name: "different-year-generic-file", Query: movieQuery, Result: movieResult with
                { Title = "Spirited Away (2020)", DownloadName = "Spirited.Away.zh-Hans.ass" }, Match: false),
            (Name: "different-series", Query, Result: Result(SubtitleSource.Amenzb, "series") with
                { Title = "Unrelated Series S04E08", DownloadName = "Unrelated.Series.S04E08.zh-Hans.ass" }, Match: false)
        };
        foreach (var item in cases)
        {
            var library = new SubtitleLibrary(Path.Combine(Root, item.Name));
            var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Ass)) });
            using var http = new HttpClient(handler);
            var download = new SubtitleDownload(http, library);
            string? path = await download.DownloadAsync(item.Query, item.Result, null, [], null, default);
            Check((path != null) == item.Match, item.Name + ": only subtitles for the current work and year are returned for playback.");
            var local = library.Results(item.Query.Title).Single();
            Check(local.Files.Count == 1 && File.ReadAllText(local.Files[0].LocalPath) == Ass,
                item.Name + ": user-requested subtitles are preserved even when not selected for playback.");
            if (!item.Match)
            {
                Check(local.AvailableSeasons.SequenceEqual(new int?[] { -1 }) && local.Files[0].LocalPath.Contains("未分类", StringComparison.Ordinal),
                    item.Name + ": another work or year is not indexed as the current movie or season.");
                var media = item.Query.Season == null
                    ? TraktMedia.Parse(item.Query.Title + " (" + item.Query.Year + ")", "", new Dictionary<string, string>())
                    : Media(item.Query.Season.Value, item.Query.Episode!.Value);
                Check(library.Tracks(media).Count == 0, item.Name + ": an unmatched subtitle cannot be automatically restored for the current work.");
            }
            Check((library.CurrentFile(item.Query, local, local.Files) != null) == item.Match,
                item.Name + ": local search applies the same work and year checks.");
            Check(await download.DownloadAsync(item.Query, item.Result, null, [], null, default) == path && handler.Count == 1,
                item.Name + ": cached reuse preserves the match decision without another network request.");
        }
    }

    static async Task Main(string[] args)
    {
        Directory.CreateDirectory(Root);
        try
        {
            await SearchChecks();
            await TitleChecks();
            await ExactSearchChecks();
            await ProviderParsingChecks();
            await DownloadChecks();
            await IndividualWorkChecks();
            await EpisodeIdentityChecks();
            if (args.Length > 0 && args[0] == "--live")
            {
                var sources = args.Skip(1).ToArray();
                if (sources.Length == 0) sources = ["tsukihime", "amenzb"];
                foreach (string source in sources)
                {
                    if (source is not ("tsukihime" or "amenzb")) throw new ArgumentException("Unknown subtitle provider.");
                    await LiveChecks(source);
                }
            }
            Console.WriteLine($"Subtitle provider checks passed: {_checks}; no desktop interaction or user credentials used.");
        }
        finally
        {
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(Root).StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    sealed class SearchProgress : IProgress<SubtitleSearchResult>
    {
        int _count;
        public int Count => _count;
        public SubtitleSearchResult? Last { get; private set; }
        public void Report(SubtitleSearchResult value) { Last = value; Interlocked.Increment(ref _count); }
    }

    sealed class BatchProgress(Action<IReadOnlyList<SubtitleResult>> report) : IProgress<IReadOnlyList<SubtitleResult>>
    {
        public void Report(IReadOnlyList<SubtitleResult> value) => report(value);
    }

    sealed class Client(SubtitleSource source, Func<SubtitleQuery, int, CancellationToken, Task<SubtitlePage>> search) : ISubtitleClient
    {
        int _calls;
        public SubtitleSource Source => source;
        public int Calls => _calls;
        public Task<SubtitlePage> SearchAsync(SubtitleQuery query, int page, CancellationToken token,
            IProgress<IReadOnlyList<SubtitleResult>>? progress = null)
        { Interlocked.Increment(ref _calls); return search(query, page, token); }
        public Task<SubtitleResult> DetailAsync(SubtitleResult result, CancellationToken token) => Task.FromResult(result);
    }

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Count++; return Task.FromResult(respond(request)); }
    }

    sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    }

    sealed class LiveHandler() : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All })
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing request URI.");
            bool subtitleRoute = uri.Host switch
            {
                "api.tsukihime.org" => uri.AbsolutePath.StartsWith("/v1/search/torrents", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/v1/torrents/", StringComparison.Ordinal),
                "storage.tsukihime.org" => (uri.AbsolutePath.StartsWith("/attach/", StringComparison.Ordinal)
                    || uri.AbsolutePath.StartsWith("/tosho/attach/", StringComparison.Ordinal)) && uri.AbsolutePath.EndsWith(".xz", StringComparison.Ordinal),
                "amenzb.moe" => System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath,
                    @"^/(?:browse|release/[1-9]\d*(?:/subtitles/[1-9]\d*)?)$"),
                _ => false
            };
            Check(subtitleRoute && request.Method == HttpMethod.Get && request.Headers.Authorization == null,
                "Live verification only requests public subtitle metadata or subtitle files.");
            Count++;
            return base.SendAsync(request, token);
        }
    }
}

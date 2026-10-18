using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using MpvNet.Windows.Subtitles;
using MpvNet.Windows.Trakt;
using MpvNet.Windows.MyAnimeList;
using MpvNet.Windows.Bangumi;

static class Program
{
    static int _checks;
    static readonly string Root = Path.Combine(Path.GetTempPath(), "AnimeFusion-SubtitleIdentity-" + Guid.NewGuid().ToString("N"));
    static readonly TraktMedia Media = TraktMedia.Parse("关于我转生变成史莱姆这档事 (2026) S4E2 - 不断进化的迷宫", "episode.mkv", new Dictionary<string, string>());
    const string English = "That Time I Got Reincarnated as a Slime";
    const string Romanized = "Tensei shitara Slime Datta Ken 4th Season";
    const string Short = "Tensei shitara Slime";
    const string Ass = "[Script Info]\nScriptType: v4.00+\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,字幕测试\n";
    static SubtitleResult Result(int season = 4, int episode = 2) => new(SubtitleSource.Tsukihime, $"release-{season}-{episode}",
        $"{English} S{season:00}E{episode:00} The Dungeon Evolves 1080p CR WEB-DL MULTi AAC2.0 H 264-VARYG (Tensei Shitara Slime Datta Ken, Multi-Audio, Multi-Subs)", "简体中文", "", [])
    {
        Individual = true, Season = season, Episode = episode, LanguageCode = "zh-Hans",
        DownloadName = $"That.Time.I.Got.Reincarnated.as.a.Slime.S{season:00}E{episode:00}.The.Dungeon.Evolves.1080p.CR.WEB-DL.MULTi.AAC2.0.H.264-VARYG.zh-Hans.ass",
        DownloadUrl = "https://subtitles.example/track.ass"
    };
    static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); _checks++; }
    static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    static void IdentityChecks()
    {
        var search = new SubtitleQuery(Short, 4, null, "zh-Hans") { Names = [Short] };
        var work = new SubtitleWork(Media, [Media.Title, English, Romanized], 59970);
        var selected = work.ForDownload(search, Result());
        Check(selected.Current && selected.Query.Title == Media.Title && selected.Query.Season == 4 && selected.Query.Episode == 2,
            "A release with a verified English title belongs to the current Chinese work despite a short search term.");
        Check(search.Title == Short && search.Episode == null && search.Names.SequenceEqual([Short]),
            "Download association never rewrites the literal search query or its matching context.");
        Check(!new SubtitleWork(Media, [Media.Title], null).ForDownload(search, Result()).Current,
            "The search term alone cannot confirm a cross-language work association.");
        Check(work.ForDownload(search, Result() with { MalId = 59970, Title = "Another script", TitleNames = [] }).Current,
            "A shared confirmed MAL identity can associate a release without a matching written title.");
        Check(!work.ForDownload(search, Result() with { MalId = 99999 }).Current,
            "An explicit MAL identity conflict wins over matching titles.");
        Check(!work.ForDownload(search, Result() with { Title = "Unrelated Work S04E02" }).Current,
            "Matching season and episode do not identify a work.");
        Check(!work.ForDownload(search, Result() with { Title = English + " Adventures S04E02" }).Current,
            "A shared prefix does not identify a different series.");
        Check(!work.ForDownload(search, Result() with { IsMovie = true, MalId = 59970 }).Current,
            "A movie/series conflict rejects association even with a shared ID.");
        Check(!(work with { Media = Media with { Parsed = Media.Parsed with { Ambiguous = true } } }).ForDownload(search, Result()).Current,
            "An ambiguous current video cannot acquire a confirmed download association.");
        Check(work.ForDownload(search, Result() with { Title = "Unfamiliar release title", TitleNames = [English] }).Current,
            "Canonical source metadata identifies a release independently of its release label.");
        Check(work.ForDownload(search, Result(3, 1)).Query.Season == 4 && work.ForDownload(search, Result(3, 1)).Query.Episode == 2,
            "Current playback coordinates remain distinct from the downloaded release coordinates.");

        var movie = TraktMedia.Parse("言叶之庭 (2013)", "movie.mkv", new Dictionary<string, string>());
        var movieWork = new SubtitleWork(movie, [movie.Title, "The Garden of Words"], 16782);
        var film = new SubtitleResult(SubtitleSource.Tsukihime, "film", "The Garden of Words (2013)", "简体中文", "", [])
            { Individual = true, IsMovie = true, Year = 2013, TitleNames = ["The Garden of Words"] };
        var filmQuery = movieWork.ForDownload(new("Garden", null, null, "zh-Hans"), film);
        Check(filmQuery.Current && filmQuery.Query.Title == movie.Title && filmQuery.Query.Year == 2013
            && filmQuery.Query.Season == null && filmQuery.Query.Episode == null, "Movies retain their actual year without invented episodes.");
        Check(!movieWork.ForDownload(search, film with { Year = 2025 }).Current, "A same-named remake with another year is rejected.");
        Check(!movieWork.ForDownload(search, film with { Title = "The Garden of Words (2025)", Year = null }).Current,
            "A conflicting year in the release title is rejected when the separate field is missing.");
        Check(!movieWork.ForDownload(search, film with { Title = "The Garden of Words 2 (2013)", TitleNames = [] }).Current,
            "A numbered movie sequel is not the current film.");
        Check(!movieWork.ForDownload(search, film with { IsMovie = false, MalId = 16782 }).Current,
            "Series metadata cannot be attached to the current movie.");
        Check(work.ForDownload(new(Media.Title, 4, 2, "zh-Hans"), new(SubtitleSource.Assrt, "local", "Imported", "", "", [])
            { Local = true, TitleNames = [Media.Title] }).Current,
            "Existing local imports keep the current-work selection behavior.");
        Check(!work.ForDownload(new(Media.Title, 4, 2, "zh-Hans"), Result() with { Local = true, MalId = 99999 }).Current,
            "A locally cached result cannot bypass an explicit work identity conflict.");
        var pack = new SubtitleResult(SubtitleSource.Assrt, "pack", "Subtitle Collection", "", "", [])
            { Collection = true, PackageNames = [$"{English} S04E02.ass"] };
        Check(work.ForDownload(search, pack).Current, "Package filenames can confirm the work when the archive label is generic.");
        var yuruMedia = TraktMedia.Parse("摇曳百合 S2E1", "episode.mkv", new Dictionary<string, string>());
        var yuruWork = new SubtitleWork(yuruMedia, [yuruMedia.Title], null);
        var aliasedPack = new SubtitleResult(SubtitleSource.Assrt, "yuru-pack",
            "ゆるゆり/摇曳百合/輕鬆百合/YuruYuri/[字幕组][合集(1+2+3期+OVA)]", "", "", [])
            { Collection = true, PackageNames = ["[01].tc.ass", "[02].tc.ass"] };
        Check(yuruWork.ForDownload(new("Yuru", 2, null, ""), aliasedPack).Current,
            "ASSRT slash-separated aliases are display names, not a path whose final component replaces the title.");
        var remakePack = new SubtitleResult(SubtitleSource.Assrt, "remake", "The Garden of Words (2025)/电影合集", "", "", [])
            { Collection = true, PackageNames = ["The Garden of Words.ass"] };
        Check(!movieWork.ForDownload(search, remakePack).Current,
            "A conflicting year on the matching movie title cannot be hidden by a slash or an undated file.");
        Check(movieWork.ForDownload(search, remakePack with { Title = "The Garden of Words (2013)/Other Movie (2025)/电影合集" }).Current,
            "Another movie's year in a collection does not reject the current film's correctly dated title.");
    }

    static async Task DownloadChecks()
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Ass, Encoding.UTF8) });
        using var http = new HttpClient(handler);
        var library = new SubtitleLibrary(Path.Combine(Root, "subtitles"));
        var download = new SubtitleDownload(http, library);
        var work = new SubtitleWork(Media, [Media.Title, English], 59970);
        var search = new SubtitleQuery(Short, 4, null, "zh-Hans");
        foreach (var release in new[] { Result(), Result(4, 1), Result(3, 2) })
        {
            var selection = work.ForDownload(search, release);
            string? path = await download.DownloadAsync(selection.Query, release, null, [], null, default);
            Check((path != null) == (release.Season == 4 && release.Episode == 2),
                "Only a verified current episode returns a path for automatic loading.");
            var storedQuery = selection.Query with { Season = release.Season, Episode = release.Episode };
            var saved = library.Package(storedQuery, release);
            Check(saved?.Files.Count == 1 && File.Exists(saved.Files[0].Path)
                && saved.Files[0].Path.StartsWith(library.SeasonDirectory(Media.Title, release.Season), StringComparison.OrdinalIgnoreCase),
                "A release is saved below the canonical Chinese work and its actual season.");
        }
        Check(handler.Count == 3 && !Directory.Exists(library.SeasonDirectory(Short, 4)),
            "Identity resolution adds no HTTP requests or short-search-name work directory.");
        var cachedSelection = work.ForDownload(search, Result());
        Check(await download.DownloadAsync(cachedSelection.Query, Result(), null, [], null, default) != null && handler.Count == 3,
            "The already downloaded current episode is reused without another download.");
        var reloaded = library.Results(Media.Title).Single(result => result.Id == Result().Id);
        Check(reloaded.TitleNames.Contains(Media.Title)
            && new SubtitleWork(Media, [Media.Title], null).ForDownload(search, reloaded).Current,
            "A saved shelf supplies its canonical work title when a local result is reopened with a short search.");
    }

    static async Task BindingChecks()
    {
        var handler = new Handler(_ => throw new Exception("Local identity resolution must not request a catalogue."));
        using var http = new HttpClient(handler);
        Directory.CreateDirectory(Path.Combine(Root, "binding-config"));
        var store = new MalStore(Path.Combine(Root, "binding-config"), Path.Combine(Root, "binding-account"));
        var client = new MalClient(store, http);
        var titles = new SubtitleTitles(Media, default, client);
        var original = await titles.QueryAsync(Short, default);
        var previous = MalPlayback.Current;
        var previousTrakt = TraktPlayback.Current;
        var previousBangumi = BangumiPlayback.Current;
        try
        {
            var playback = new MalPlayback(store, client);
            typeof(MalPlayback).GetProperty(nameof(MalPlayback.Current))!.SetValue(null, playback);
            store.Bind(Media.Parsed.Key, new(59970, 4, Romanized, 0));
            var current = titles.CurrentWork();
            Check(current.MalId == 59970 && current.Names.Contains(Romanized),
                "A later confirmed local binding supplies identity and names before a live subject is available.");
            Check(current.ForDownload(original, Result() with { MalId = 59970 }).Current && handler.Count == 0,
                "The same-window download refreshes local identity after the original search without networking.");
            Check((await titles.QueryAsync(Short, default)).Title == Short && !titles.IsCurrent(Short) && handler.Count == 0,
                "A short search stays literal and is not falsely promoted to an exact alias.");
            Check(new SubtitleQuery(Short, null, null, "").MatchesTitle(current.Names)
                && !titles.IsCurrent(Short), "Existing verified aliases can match local search display without turning a short search into a confirmed identity.");
            store.Bind(Media.Parsed.Key, new(59971, 3, "Wrong season name", 0));
            var isolated = new SubtitleTitles(Media, default, client).CurrentWork();
            Check(isolated.MalId == null && !isolated.Names.Contains("Wrong season name"), "A season-conflicting binding is not used for identity.");
            store.Settings.Bindings.Clear();
            store.Bind(Media.Parsed.Key + "-other", new(59970, 4, Romanized, 0));
            Check(new SubtitleTitles(Media, default, client).CurrentWork().MalId == null, "Bindings require the exact current media key.");
            var traktStore = new TraktStore(Path.Combine(Root, "binding-config"), Path.Combine(Root, "trakt-account"));
            traktStore.Bind(Media.Key, new(135985, "show", 4, English, 0));
            typeof(TraktPlayback).GetProperty(nameof(TraktPlayback.Current))!.SetValue(null, new TraktPlayback(traktStore, new(traktStore, http)));
            var bangumiStore = new BangumiStore(Path.Combine(Root, "binding-config"), Path.Combine(Root, "bgm-account"));
            bangumiStore.Bind(Media.Parsed.Key, new(515594, Media.Title + " 第四季"));
            typeof(BangumiPlayback).GetProperty(nameof(BangumiPlayback.Current))!.SetValue(null, new BangumiPlayback(bangumiStore, new(bangumiStore, http)));
            var otherBindings = new SubtitleTitles(Media, default, client).CurrentWork();
            Check(otherBindings.Names.Contains(English) && otherBindings.Names.Contains(Media.Title + " 第四季")
                && otherBindings.ForDownload(original, Result()).Current && handler.Count == 0,
                "Confirmed Trakt and Bangumi binding names identify the result even with no live subjects or MAL ID.");
            traktStore.Bind(Media.Key, new(135985, "movie", 4, "Wrong kind", 0));
            Check(!new SubtitleTitles(Media, default, client).CurrentWork().Names.Contains("Wrong kind"),
                "A Trakt binding must agree with the current media kind.");
        }
        finally
        {
            typeof(MalPlayback).GetProperty(nameof(MalPlayback.Current))!.SetValue(null, previous);
            typeof(TraktPlayback).GetProperty(nameof(TraktPlayback.Current))!.SetValue(null, previousTrakt);
            typeof(BangumiPlayback).GetProperty(nameof(BangumiPlayback.Current))!.SetValue(null, previousBangumi);
        }
    }

    static async Task UnclassifiedDownloadChecks()
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Ass, Encoding.UTF8) });
        using var http = new HttpClient(handler);
        var library = new SubtitleLibrary(Path.Combine(Root, "unclassified"));
        var download = new SubtitleDownload(http, library);
        var query = new SubtitleQuery(English, 4, 2, "zh-Hans");
        var unknown = Result() with { Id = "unknown", Season = -1, Episode = null, DownloadName = "unknown.zh-Hans.ass" };
        var cases = new[]
        {
            (Result: unknown, Query: query),
            (Result: unknown with { Id = "unknown-equal" }, Query: query with { Season = -1, Episode = null }),
            (Result: unknown with { Id = "range", Title = English + " S00E10-E12", DownloadName = "range.zh-Hans.ass" }, Query: query with { Season = 0, Episode = 10 }),
            (Result: unknown with { Id = "missing-episode", Season = 4 }, Query: query with { Episode = null }),
            (Result: unknown with { Id = "missing-season", Season = null, Episode = 2 }, Query: query with { Season = null }),
            (Result: unknown with { Id = "not-a-movie" }, Query: query with { Season = null, Episode = null })
        };
        foreach (var item in cases)
        {
            int requests = handler.Count;
            Check(await download.DownloadAsync(item.Query, item.Result, null, [], null, default) == null,
                "An unresolved extracted subtitle is saved without returning an automatic-load path.");
            var storedQuery = item.Query with { Season = -1, Episode = null };
            var package = library.Package(storedQuery, item.Result);
            Check(package?.Complete == true && package.Files.Count == 1 && package.Files[0].Episode == null
                && File.Exists(package.Files[0].Path)
                && package.Files[0].Path.StartsWith(library.SeasonDirectory(English, -1) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && File.ReadAllText(package.Files[0].Path) == Ass,
                "Unresolved coordinates preserve the original subtitle content below the unclassified shelf.");
            var cached = library.Results(English).Single(result => result.Id == item.Result.Id);
            Check(cached.Season == -1 && cached.Episode == null && cached.Individual
                && library.CurrentFile(storedQuery, cached, cached.Files) == null,
                "An unclassified local result cannot automatically match even identical unknown query coordinates.");
            Check(await download.DownloadAsync(item.Query, item.Result, null, [], null, default) == null
                && handler.Count == requests + 1,
                "Repeating an unresolved download reuses the saved file without loading it or requesting it again.");
        }
        Check(library.Tracks(Media).Count == 0, "Unclassified subtitles never appear as confirmed current-episode tracks.");

        var special = Result(0, 10) with { Id = "confirmed-special", DownloadName = "special.S00E10.zh-Hans.ass" };
        var specialQuery = query with { Season = 0, Episode = 10 };
        string? specialPath = await download.DownloadAsync(specialQuery, special, null, [], null, default);
        Check(specialPath != null && File.Exists(specialPath)
            && specialPath.StartsWith(library.SeasonDirectory(English, 0) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "A confirmed special with season zero still saves in the specials shelf and automatically loads.");

        var movie = new SubtitleResult(SubtitleSource.Amenzb, "confirmed-movie", "The Garden of Words (2013)", "简体中文", "", [])
        {
            Individual = true, IsMovie = true, Year = 2013, LanguageCode = "zh-Hans",
            DownloadName = "The Garden of Words (2013).zh-Hans.ass", DownloadUrl = "https://subtitles.example/movie.ass"
        };
        var movieQuery = new SubtitleQuery("The Garden of Words", null, null, "zh-Hans") { Year = 2013 };
        string? moviePath = await download.DownloadAsync(movieQuery, movie, null, [], null, default);
        Check(moviePath != null && File.Exists(moviePath)
            && moviePath.StartsWith(library.SeasonDirectory(movieQuery.Title, null) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "A confirmed movie with null season and episode still saves in the movie shelf and automatically loads.");
        var movieCached = library.Results(movieQuery.Title).Single();
        Check(library.CurrentFile(movieQuery, movieCached, movieCached.Files)?.LocalPath == moviePath,
            "A confirmed local movie remains eligible for automatic selection.");
        int movieRequests = handler.Count;
        Check(await download.DownloadAsync(movieQuery, movie, null, [], null, default) == moviePath && handler.Count == movieRequests,
            "A confirmed movie still reuses the existing subtitle without another request.");
    }

    static async Task AliasIdentityChecks()
    {
        const string subject = """
            {"id":28900,"type":2,"name":"ゆるゆり♪♪","name_cn":"摇曳百合 第二季","platform":"TV","date":"2012-07-02"}
            """;
        const string translated = """
            {"id":12403,"title":"Yuru Yuri♪♪","alternative_titles":{"synonyms":["Yuru Yuri 2nd Season"],"en":"YuruYuri: Happy Go Lily ♪♪","ja":"ゆるゆり♪♪"},"start_date":"2012-07-03","media_type":"tv","num_episodes":12}
            """;
        var media = TraktMedia.Parse("摇曳百合 S2E3", "episode.mkv", new Dictionary<string, string>());
        string[] drafts = ["另一部作品", media.Title + " S04E02", ""];
        for (int index = 0; index < drafts.Length; index++)
        {
            var keywords = new List<string>();
            var handler = new Handler(request =>
            {
                Check(request.Headers.Authorization == null, "Alias catalogue reads are public and do not carry account credentials.");
                if (request.RequestUri!.Host == "api.myanimelist.net" && request.RequestUri.AbsolutePath == "/v2/anime/12403")
                    return Json(translated);
                if (request.RequestUri.Host == "api.bgm.tv" && request.RequestUri.AbsolutePath == "/v0/subjects/28900")
                    return Json(subject);
                if (request.RequestUri.Host == "api.bgm.tv" && request.RequestUri.AbsolutePath == "/v0/search/subjects")
                {
                    using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    keywords.Add(body.RootElement.GetProperty("keyword").GetString()!);
                    return Json("{\"data\":[" + subject + "]}");
                }
                throw new Exception("Unexpected alias catalogue request: " + request.RequestUri);
            });
            using var http = new HttpClient(handler);
            var titles = new SubtitleTitles(media, default,
                new(new(Path.Combine(Root, "aliases-config-" + index), Path.Combine(Root, "aliases-account-" + index)), http));
            var search = await titles.QueryAsync(drafts[index], default);
            Check(search.Title == drafts[index] && search.Episode == null && search.SearchSeason == null && handler.Count == 0,
                "A different, episode-suffixed or empty search stays literal without acquiring current playback identity or making catalogue requests.");
            var names = await titles.NamesAsync(default);
            int reads = handler.Count;
            var work = titles.CurrentWork();
            Check(keywords.SequenceEqual([media.Title]) && reads == 3,
                "The alias picker requests only the current playback title, even after the search input was changed or cleared.");
            Check(names.Contains(media.Title) && names.Contains("摇曳百合 第二季") && names.Contains("搖曳百合 第二季")
                && names.Contains("ゆるゆり♪♪") && names.Contains("YuruYuri: Happy Go Lily ♪♪") && !names.Contains(drafts[index]),
                "Current-playback aliases include confirmed Chinese, Japanese and English names without the unrelated search text.");
            Check(work.MalId == 12403 && work.Media == media && work.Names.Contains("YuruYuri: Happy Go Lily ♪♪"),
                "An explicitly resolved current alias retains its confirmed MAL identity and original playback coordinates.");
            Check(work.ForDownload(await titles.QueryAsync("Yuru", default), new(SubtitleSource.Tsukihime, "yuru", "Unfamiliar title", "", "", [])
                { MalId = 12403, Season = 2, Episode = 3, Individual = true }).Current && handler.Count == reads,
                "Downloads reuse the current alias identity without an extra catalogue request.");
            foreach (string draft in drafts)
            {
                var edited = await titles.QueryAsync(draft, default);
                var repeated = await titles.NamesAsync(default);
                Check(new HashSet<string>(names).SetEquals(repeated) && handler.Count == reads,
                    "Changing or clearing the search input reuses the same current-work alias cache without new requests.");
                Check(edited.Title == draft && edited.Episode == null && edited.SearchSeason == null && !titles.IsCurrent(draft)
                    && titles.CurrentWork().MalId == 12403 && titles.CurrentWork().Media == media,
                    "Opening the current alias picker never promotes different search input into the current video's identity.");
            }
            var current = await titles.QueryAsync(media.Title, default);
            Check(current.Title == media.Title && current.Episode == 3 && current.SearchSeason == 2 && handler.Count == reads,
                "The actual current title retains its playback season and episode after unrelated searches.");
        }
        var otherMedia = TraktMedia.Parse("另一部电影 (2020)", "other.mkv", new Dictionary<string, string>());
        var otherHandler = new Handler(request =>
        {
            Check(request.RequestUri!.Host == "api.bgm.tv" && request.RequestUri.AbsolutePath == "/v0/search/subjects",
                "An unmatched current film only searches the public catalogue.");
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Check(body.RootElement.GetProperty("keyword").GetString() == otherMedia.Title,
                "Manually typing another work never changes the alias catalogue's current-film keyword.");
            return Json("{\"data\":[]}");
        });
        using var otherHttp = new HttpClient(otherHandler);
        var unrelatedTitles = new SubtitleTitles(otherMedia, default,
            new(new(Path.Combine(Root, "unrelated-config"), Path.Combine(Root, "unrelated-account")), otherHttp));
        await unrelatedTitles.QueryAsync("摇曳百合 第二季", default);
        var otherNames = await unrelatedTitles.NamesAsync(default);
        Check(otherNames.SequenceEqual([otherMedia.Title]) && unrelatedTitles.CurrentWork().MalId == null
            && unrelatedTitles.CurrentWork().Media == otherMedia && otherHandler.Count == 1,
            "An unmatched current film keeps only its own title instead of resolving the manually searched other work.");
    }

    static async Task Main()
    {
        try
        {
            IdentityChecks(); await DownloadChecks(); await UnclassifiedDownloadChecks(); await BindingChecks(); await AliasIdentityChecks();
            Console.WriteLine($"PASS: {_checks} subtitle identity checks.");
        }
        finally { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Count++; return Task.FromResult(respond(request)); }
    }
}

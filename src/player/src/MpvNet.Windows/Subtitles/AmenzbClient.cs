using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MpvNet.Windows.Bangumi;
using static MpvNet.Windows.Subtitles.SubtitleHttp;

namespace MpvNet.Windows.Subtitles;

public sealed class AmenzbClient(HttpClient http) : ISubtitleClient
{
    const string Origin = "https://amenzb.moe";
    static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);
    public SubtitleSource Source => SubtitleSource.Amenzb;
    public ISubtitleClient BeginSearch() => new AmenzbClient(http);

    sealed record Release(string Id, string Title, int? Season, int? Episode, string Languages, string Date);
    sealed record Track(string ReleaseId, string Id, string Language, string Name, string Format, string Size, bool Forced);
    readonly Dictionary<(string, string, int), (Release[] Releases, bool More)> _pages = new();
    readonly Dictionary<string, IReadOnlyList<SubtitleResult>> _details = new();
    readonly Dictionary<string, Track[]> _tracks = new();

    public async Task<SubtitlePage> SearchAsync(SubtitleQuery query, int page, CancellationToken token,
        IProgress<IReadOnlyList<SubtitleResult>>? progress = null)
    {
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        string name = query.Title.Normalize(NormalizationForm.FormKC).Trim();
        if (name.Length < 2) throw new SubtitleServiceException("ameNZB 搜索词至少需要两个字符。");
        string language = query.Language == "all" ? "" : query.Language;
        string searchLanguage = SearchLanguage(language);
        token.ThrowIfCancellationRequested();
        if (!_pages.TryGetValue((name, language, page), out var browse))
        {
            string html = await HtmlAsync("/browse?q=" + Uri.EscapeDataString(name) + "&page=" + page
                + (searchLanguage.Length > 0 ? "&sub_lang=" + searchLanguage : ""), token).ConfigureAwait(false);
            browse = (Releases(html).DistinctBy(release => release.Id).ToArray(),
                Matches(html, @"<a\b(?<attrs>[^>]*)>").Any(link =>
                    Attribute(link.Groups["attrs"].Value, "aria-label").Equals("Next page", StringComparison.OrdinalIgnoreCase)
                    && !Attribute(link.Groups["attrs"].Value, "aria-disabled").Equals("true", StringComparison.OrdinalIgnoreCase)));
            token.ThrowIfCancellationRequested();
            _pages.Add((name, language, page), browse);
        }
        var releases = browse.Releases;
        var results = new List<SubtitleResult>();
        foreach (var release in releases)
        {
            token.ThrowIfCancellationRequested();
            // The public browse endpoint has no season/episode filter. Its list
            // already includes coordinates, so unrelated releases need no detail request.
            if (query.RequestSeason is { } season && release.Season != season
                || query.RequestEpisode is { } episode && release.Episode != episode) continue;
            // Browse languages describe the video, not downloadable attachments.
            if (string.IsNullOrWhiteSpace(release.Languages)) continue;
            var candidate = new SubtitleResult(Source, release.Id, release.Title, "", release.Date, [])
                { Individual = true, Season = release.Season, Episode = release.Episode };
            if (!_details.TryGetValue(release.Id, out var details))
            {
                // Episode pages include attachments for several versions. Reuse positive
                // matches by their actual owner, without treating missing rows as absence.
                if (language.Length > 0 && _tracks.TryGetValue(release.Id, out var tracks)
                    && tracks.Any(track => MatchesLanguage(LanguageCode(track), language))
                    && !BangumiMedia.FromPlayback(release.Title, release.Title).Ambiguous)
                    details = Results(candidate, release.Id, release.Title, tracks, []).ToArray();
                else
                {
                    string detail = await HtmlAsync("/release/" + release.Id, token).ConfigureAwait(false);
                    details = Details(candidate, release.Id, detail).ToArray();
                    token.ThrowIfCancellationRequested();
                    _details.Add(release.Id, details);
                }
            }
            var matches = details.Where(result => MatchesLanguage(result.LanguageCode, language)
                && (query.RequestSeason == null || result.Season == query.RequestSeason)
                && (query.RequestEpisode == null || result.Episode == query.RequestEpisode)).ToArray();
            token.ThrowIfCancellationRequested();
            results.AddRange(matches);
            if (matches.Length > 0) progress?.Report(matches);
        }
        token.ThrowIfCancellationRequested();
        return new(results, browse.More) { PageKey = releases.Length > 0 ? string.Join(',', releases.Select(release => release.Id)) : "page:" + page };
    }

    static bool MatchesLanguage(string code, string language) => language.Length == 0
        || code == language || SubtitleLanguage.Find(code)?.Matches(language) == true;

    static string SearchLanguage(string language) => language switch
    {
        "zh-Hans" or "zh-Hant" => "zh",
        "ja" or "en" or "es" or "fr" or "de" or "it" or "pt" or "ru" or "ko" or "zh" or "ar" or "tr"
            or "pl" or "nl" or "sv" or "hi" or "ta" or "te" or "th" or "id" or "ms" or "vi" or "fil"
            or "he" or "hu" or "cs" or "el" or "da" or "fi" or "no" or "ro" or "uk" => language,
        _ => ""
    };

    public async Task<SubtitleResult> DetailAsync(SubtitleResult result, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var id = Match(result.Id, @"^(?<release>[1-9][0-9]*):(?<language>[a-z]{2,3}(?:-[a-z0-9]{2,8})*|other)$");
        if (result.Source != Source || !id.Success) throw new SubtitleServiceException("ameNZB 字幕编号无效。");
        string releaseId = id.Groups["release"].Value, language = LanguageCode(id.Groups["language"].Value);
        if (result.DownloadName.Length > 0 && Match(result.DownloadUrl,
            "^" + Regex.Escape(Origin + "/release/" + releaseId + "/subtitles/") + @"[1-9][0-9]*$").Success)
            return result;
        string html = await HtmlAsync("/release/" + releaseId, token).ConfigureAwait(false);
        return Details(result, releaseId, html).FirstOrDefault(detail => detail.LanguageCode == language
            || SubtitleLanguage.Find(detail.LanguageCode)?.Matches(language) == true)
            ?? throw new SubtitleServiceException("ameNZB：没有可用字幕。");
    }

    IEnumerable<SubtitleResult> Details(SubtitleResult result, string releaseId, string html)
    {
        string title = ReleaseTitle(html);
        var original = BangumiMedia.FromPlayback(result.Title, result.Title);
        var query = new SubtitleQuery(original.Title, null, null, result.LanguageCode);
        var parsed = BangumiMedia.FromPlayback(title, title);
        if (title.Length == 0 || !query.MatchesTitle([title])
            || original.Year != null && parsed.Year != null && original.Year != parsed.Year
            || original.HasSeason && parsed.HasSeason && parsed.Season != original.Season
            || original.EpisodeNumber != null && parsed.EpisodeNumber != null && parsed.EpisodeNumber != original.EpisodeNumber)
            yield break;
        var tracks = Tracks(html).Where(track => !track.Forced
            && !Match(track.Name, @"\b(?:signs?|songs?|forced)\b").Success
            && track.Format is "" or "ASS" or "SSA" or "SRT" or "VTT").ToArray();
        foreach (var owner in tracks.GroupBy(track => track.ReleaseId))
            _tracks[owner.Key] = (_tracks.TryGetValue(owner.Key, out var known) ? known.Concat(owner) : owner)
                .DistinctBy(track => track.Id).ToArray();
        foreach (var detail in Results(result, releaseId, title, tracks, SeriesTitles(html)))
            yield return detail;
    }

    static IEnumerable<SubtitleResult> Results(SubtitleResult result, string releaseId, string title,
        IEnumerable<Track> available, IEnumerable<string> titles)
    {
        var parsed = BangumiMedia.FromPlayback(title, title);
        var tracks = available.Where(track => track.ReleaseId == releaseId)
            .OrderBy(track => SubtitleLanguage.Find(LanguageCode(track))?.SortOrder ?? 5)
            .ThenBy(track => Match(track.Name, @"\b(?:CC|SDH)\b").Success).DistinctBy(LanguageCode);
        foreach (var track in tracks)
        {
            string filename = Regex.Replace(title, @"\s*\{Tags:.*\}\s*$", "", RegexOptions.CultureInvariant, MatchTimeout).Trim();
            if (Path.GetExtension(filename).ToLowerInvariant() is ".mkv" or ".mp4" or ".webm" or ".avi")
                filename = Path.GetFileNameWithoutExtension(filename);
            string extension = track.Format.ToLowerInvariant() is "ass" or "ssa" or "srt" or "vtt" ? track.Format.ToLowerInvariant() : "txt";
            filename += "." + TrackName(track) + "." + extension;
            yield return result with
            {
                Id = releaseId + ":" + LanguageCode(track),
                Language = LanguageName(track),
                LanguageCode = LanguageCode(track),
                Details = string.Join(" · ", new[] { LanguageName(track), result.Details, track.Size }.Where(value => value.Length > 0)),
                Format = track.Format,
                DownloadName = filename,
                DownloadUrl = Origin + "/release/" + releaseId + "/subtitles/" + track.Id,
                PackageNames = [filename],
                TitleNames = result.TitleNames.Append(result.Title).Concat(titles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Individual = true,
                Season = parsed.Ambiguous || result.Season == -1 ? -1 : SubtitleQuery.IsSpecial(title) ? 0
                    : parsed.HasSeason ? parsed.Season : result.Season ?? (parsed.EpisodeNumber != null ? parsed.Season : null),
                Episode = parsed.Ambiguous || result.Season == -1 ? null : parsed.EpisodeNumber is >= 0 and <= int.MaxValue and var episode
                    && episode == Math.Truncate(episode) ? (int)episode : result.Episode
            };
        }
    }

    async Task<string> HtmlAsync(string path, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Origin + path);
        request.Headers.Accept.ParseAdd("text/html");
        return await ReadAsync(http, request, "ameNZB", async (response, readToken) =>
        {
            if (!response.IsSuccessStatusCode)
                throw new SubtitleServiceException(response.StatusCode == HttpStatusCode.TooManyRequests
                    ? "ameNZB：请求太频繁，请稍后重试。" : "ameNZB：服务暂时不可用，请稍后重试。");
            using var stream = await response.Content.ReadAsStreamAsync(readToken).ConfigureAwait(false);
            return Encoding.UTF8.GetString(await ReadBytesAsync(stream, 4 * 1024 * 1024, readToken).ConfigureAwait(false));
        }, token).ConfigureAwait(false);
    }

    static IEnumerable<Release> Releases(string html)
    {
        foreach (Match link in Matches(html, @"<a\b(?<attrs>[^>]*)>"))
        {
            string attrs = link.Groups["attrs"].Value;
            if (!Attribute(attrs, "class").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("nzb-title")) continue;
            var id = Match(Attribute(attrs, "href"), @"^/release/(?<id>[1-9][0-9]*)$");
            if (!id.Success) continue;
            using var document = JsonDocument.Parse(Attribute(attrs, "data-release"));
            var item = document.RootElement;
            string title = Text(item, "title");
            if (title.Length == 0) continue;
            var parsed = BangumiMedia.FromPlayback(title, title);
            string position = Text(item, "se");
            int? season = int.TryParse(Match(position, @"\bS(?<value>\d+)\b").Groups["value"].Value, out int s) ? s
                : parsed.HasSeason || parsed.EpisodeNumber != null ? parsed.Season : null;
            int? episode = int.TryParse(Match(position, @"\bE(?<value>\d+)\b").Groups["value"].Value, out int e) ? e
                : parsed.EpisodeNumber is >= 0 and <= int.MaxValue and var number && number == Math.Truncate(number) ? (int)number : null;
            // Explicit season/episode names take precedence over catalogue coordinates.
            // A range or a conflicting season without a named episode is not a single episode.
            if (parsed.Ambiguous)
            { season = -1; episode = null; }
            else if (SubtitleQuery.IsSpecial(title))
            {
                season = 0;
                if (parsed.EpisodeNumber is >= 0 and <= int.MaxValue and var specialEpisode && specialEpisode == Math.Truncate(specialEpisode))
                    episode = (int)specialEpisode;
            }
            else if (parsed.HasSeason && season != parsed.Season && parsed.EpisodeNumber == null)
            { season = -1; episode = null; }
            else if (parsed.HasSeason && parsed.EpisodeNumber is >= 0 and <= int.MaxValue and var namedEpisode
                && namedEpisode == Math.Truncate(namedEpisode))
            { season = parsed.Season; episode = (int)namedEpisode; }
            string date = DateTime.TryParse(Text(item, "date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
            yield return new(id.Groups["id"].Value, title, season, episode, Text(item, "subs"), date);
        }
    }

    static IEnumerable<Track> Tracks(string html)
    {
        foreach (Match table in Matches(html, @"<table\b(?<attrs>[^>]*)>(?<body>.*?)</table>"))
        {
            if (Attribute(table.Groups["attrs"].Value, "id") != "subs-table") continue;
            foreach (Match row in Matches(table.Groups["body"].Value, @"<tr\b(?<attrs>[^>]*)>(?<body>.*?)</tr>"))
            {
                string body = row.Groups["body"].Value;
                var cells = Matches(body, @"<td\b[^>]*>(?<body>.*?)</td>").Select(cell => Plain(cell.Groups["body"].Value)).ToArray();
                if (cells.Length < 5) continue;
                var link = Matches(body, @"<a\b(?<attrs>[^>]*)>").Select(link => Match(Attribute(link.Groups["attrs"].Value, "href"),
                    @"^/release/(?<release>[1-9][0-9]*)/subtitles/(?<id>[1-9][0-9]*)$")).FirstOrDefault(link => link.Success);
                if (link == null) continue;
                string name = Regex.Replace(cells[1], @"\s+Default\b", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout).Trim();
                string format = cells[2].Equals("other", StringComparison.OrdinalIgnoreCase) ? "" : cells[2].ToUpperInvariant();
                yield return new(link.Groups["release"].Value, link.Groups["id"].Value,
                    Attribute(row.Groups["attrs"].Value, "data-lang"), name, format, cells[3], Match(name, @"\bforced\b").Success);
            }
        }
    }

    static string ReleaseTitle(string html) => Matches(html, @"<h1\b[^>]*>(?<body>.*?)</h1>")
        .Select(match => Plain(match.Groups["body"].Value)).FirstOrDefault() ?? "";
    static IEnumerable<string> SeriesTitles(string html)
    {
        foreach (Match link in Matches(html, @"<a\b(?<attrs>[^>]*)>(?<body>.*?)</a>"))
        {
            string attrs = link.Groups["attrs"].Value;
            if (!Attribute(attrs, "class").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("series-link")
                || !Match(Attribute(attrs, "href"), @"^/anime/[1-9][0-9]*$").Success) continue;
            string name = Plain(Regex.Replace(link.Groups["body"].Value, @"<span\b[^>]*>.*?</span>", "",
                RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout));
            if (name.Length > 0) yield return name;
        }
    }
    static string TrackName(Track track) => track.Name.Length > 0 ? track.Name : LanguageName(track);
    static string LanguageName(Track track)
    {
        string language = SubtitleLanguage.Find(LanguageCode(track))?.Name ?? "其他";
        return language + (track.Forced ? "（强制片段）" : "");
    }
    static string LanguageCode(Track track)
    {
        string code = LanguageCode(track.Language);
        return code == "zh" ? Match(track.Name, "simplified|简体|簡體").Success ? "zh-Hans"
            : Match(track.Name, "traditional|繁体|繁體").Success ? "zh-Hant" : "zh" : code;
    }
    static string LanguageCode(string value) => value.ToLowerInvariant() switch
    {
        "chi" or "zho" => "zh", "fre" => "fr", "ger" => "de", "dut" => "nl", "cze" => "cs", "slo" => "sk",
        "rum" => "ro", "gre" => "el", "per" => "fa", "may" => "ms", "bur" => "my", "tib" => "bo", "wel" => "cy",
        "ice" => "is", "alb" => "sq", "arm" => "hy", "baq" => "eu", "mac" => "mk", "geo" => "ka",
        _ => SubtitleLanguage.Find(value)?.Code ?? "other"
    };
    static string Attribute(string html, string name)
    {
        var match = Match(html, @"(?:^|\s)" + Regex.Escape(name) + "\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)')");
        return WebUtility.HtmlDecode(match.Groups["value"].Value);
    }
    static string Plain(string html) => WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, "<[^>]*>", " ",
        RegexOptions.CultureInvariant, MatchTimeout), @"\s+", " ", RegexOptions.CultureInvariant, MatchTimeout)).Trim();
    static Match Match(string value, string pattern) => Regex.Match(value, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, MatchTimeout);
    static IEnumerable<Match> Matches(string value, string pattern) => Regex.Matches(value, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, MatchTimeout);
}

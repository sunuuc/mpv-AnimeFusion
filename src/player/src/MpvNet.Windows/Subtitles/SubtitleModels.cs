using System.Text.RegularExpressions;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MpvNet.Windows.Bangumi;
using MpvNet.Windows.Trakt;

namespace MpvNet.Windows.Subtitles;

public enum SubtitleSource { Assrt, Amenzb, Tsukihime }

public interface ISubtitleClient
{
    SubtitleSource Source { get; }
    // A fresh request owns its page/detail cache; pause and resume keep that session.
    ISubtitleClient BeginSearch() => this;
    // Progress contains verified rows; the returned page still includes all of its results.
    Task<SubtitlePage> SearchAsync(SubtitleQuery query, int page, CancellationToken token,
        IProgress<IReadOnlyList<SubtitleResult>>? progress = null);
    Task<SubtitleResult> DetailAsync(SubtitleResult result, CancellationToken token);
}

public sealed record SubtitleLanguage(string Code, string Name)
{
    static readonly CultureInfo[] Cultures = CultureInfo.GetCultures(CultureTypes.NeutralCultures | CultureTypes.SpecificCultures)
        .Where(culture => culture.Name.Length > 0).ToArray();
    public int SortOrder => Code switch { "zh-Hans" => 0, "zh-Hant" => 1, "en" => 2, "ja" => 3, "zh" => 4, "other" => 6, _ => 5 };
    public static SubtitleLanguage? Find(string code)
    {
        code = code.Trim().Replace('_', '-').ToLowerInvariant();
        if (code is "chs" or "zh-cn" or "zh-sg" or "zh-hans" || code.StartsWith("zh-hans-", StringComparison.Ordinal))
            return new("zh-Hans", "简体中文");
        if (code is "cht" or "tc" or "zh-tw" or "zh-hk" or "zh-mo" or "zh-hant" || code.StartsWith("zh-hant-", StringComparison.Ordinal))
            return new("zh-Hant", "繁体中文");
        if (code is "zh" or "zho" or "chi") return new("zh", "中文");
        if (code is "ja" or "jpn") return new("ja", "日语");
        if (code is "en" or "eng") return new("en", "英语");
        var culture = Cultures.FirstOrDefault(culture => culture.Name.Equals(code, StringComparison.OrdinalIgnoreCase))
            ?? Cultures.FirstOrDefault(culture => culture.TwoLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase)
                || culture.ThreeLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (culture == null) return null;
        string language = culture.TwoLetterISOLanguageName;
        return language != code ? Find(language) : new(language, culture.DisplayName);
    }
    public bool Matches(string language) => Code == language || language == "zh" && Code is "zh-Hans" or "zh-Hant";
}

public sealed record SubtitleContent(string Type, double? Episode, int? Season, bool Ambiguous)
{
    public bool HasSeason => Season != null;
    public bool Extra => IsExtra(Type);
    public static bool IsExtra(string type) => type is "OVA" or "OAD" or "SP" or "PV" or "PREVIEW" or "NCED" or "NCOP" or "ED" or "OP";
    public string Title { get; init; } = "";
    public int? Year { get; init; }
    public bool PlaybackNumbered { get; init; }
}

public sealed record SubtitleQuery(string Title, int? Season, int? Episode, string Language)
{
    public IReadOnlyList<string> Names { get; init; } = [];
    public int? Year { get; init; }
    public int? SearchSeason { get; init; }
    public int? RequestSeason { get; init; }
    public int? RequestEpisode { get; init; }
    public string ContentType { get; init; } = "";
    public double? ContentEpisode { get; init; }

    static string NormalizeType(string value) => value.Trim().ToUpperInvariant() switch
    {
        "OAV" or "OVA" => "OVA",
        "SP" or "SPS" or "SPECIAL" or "SPECIALS" => "SP",
        "ENDING" or "ED" => "ED",
        "OPENING" or "OP" => "OP",
        "GEKIJOUBAN" or "MOVIE" => "MOVIE",
        "OAD" => "OAD",
        "PV" => "PV",
        "PREVIEW" => "PREVIEW",
        "NCED" => "NCED",
        "NCOP" => "NCOP",
        "TV" => "TV",
        "ONA" => "ONA",
        _ => ""
    };

    const string SpecialPattern = @"(?:^|[^a-z0-9])(?:OVA|OAD)(?=\d|[^a-z0-9]|$)"
        + @"|(?:^|[^a-z0-9])SP(?=\s*(?:\d|Ep?(?:isode)?\s*\d|[._\[\]()/\\-]|$))"
        + @"|(?:^|[^a-z0-9])Specials?(?=\s*(?:\d|Ep?(?:isode)?\s*\d|[._\[\]()/\\-]|$))|特典|特别篇|特別篇|特别版|特別版";

    static SubtitleContent ReadContent(string name)
    {
        var elements = AnitomySharp.AnitomySharp.Parse(name, new(delimiters: " _.&+,|/\\")).ToArray();
        var parsed = BangumiMedia.FromPlayback(name, name);
        var numbered = BangumiMedia.Parse(name);
        var types = elements.Where(element => element.Category == AnitomySharp.Element.ElementCategory.ElementAnimeType)
            .Where(element => element.Value.ToUpperInvariant() is not ("SPECIAL" or "SPECIALS")
                || Regex.IsMatch(name, SpecialPattern, RegexOptions.IgnoreCase))
            .Select(element => NormalizeType(element.Value)).Where(type => type.Length > 0).Distinct().ToArray();
        if (types.Length == 0 && NormalizeType(name.Trim(' ', '[', ']', '(', ')')) is { Length: > 0 } exact)
            types = [exact];
        var numbers = elements.Where(element => element.Category == AnitomySharp.Element.ElementCategory.ElementEpisodeNumber)
            .Select(element => double.TryParse(element.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double number)
                && double.IsFinite(number) && number >= 0 ? (double?)number : null)
            .OfType<double>().Distinct().ToArray();
        if (numbers.Length > 0)
        {
            // Anitomy intentionally leaves a separated SP token unsearchable so
            // titles such as SP Professional are not labelled as extras. Retain
            // its known type only immediately before an already parsed number.
            var prefixes = Regex.Matches(name, @"(?:^|[^a-z0-9])(?<type>[a-z]+)(?=[\s._-]*(?:[\[(]\s*)?\d)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Select(match => AnitomySharp.KeywordManager.Normalize(match.Groups["type"].Value))
                .Where(type => AnitomySharp.KeywordManager.Contains(AnitomySharp.Element.ElementCategory.ElementAnimeType, type))
                .Select(NormalizeType).Where(type => type.Length > 0);
            types = types.Concat(prefixes).Distinct().ToArray();
        }
        return new(types.Length == 1 ? types[0] : "", numbers.Length == 1 ? numbers[0] : parsed.EpisodeNumber,
            parsed.HasSeason ? parsed.Season : null, parsed.Ambiguous || types.Length > 1 || numbers.Length > 1)
        {
            Title = elements.FirstOrDefault(element => element.Category == AnitomySharp.Element.ElementCategory.ElementAnimeTitle)?.Value
                ?? parsed.Title,
            Year = parsed.Year,
            PlaybackNumbered = numbered.HasSeason && numbered.EpisodeNumber != null
        };
    }

    public static SubtitleContent Content(string name)
    {
        string[] parts = name.Replace('\\', '/').Split('/');
        var content = ReadContent(parts[^1]);
        if (content.HasSeason && parts.SkipLast(1).Select(ReadContent).Any(parent =>
            parent.HasSeason && !parent.Ambiguous && parent.Season != content.Season))
            return content with { Ambiguous = true };
        if (content.HasSeason || content.Type.Length > 0 || content.Ambiguous) return content;
        // A dedicated type directory can identify an otherwise unlabelled file.
        // Mixed wrappers such as TV+OVA identify neither kind of child.
        foreach (string folder in parts.SkipLast(1).Reverse())
        {
            var parent = ReadContent(folder);
            if (!parent.Ambiguous && parent.Type.Length > 0) return content with { Type = parent.Type };
        }
        return content;
    }

    public SubtitleQuery WithPlayback(TraktMedia media)
    {
        // A library can number an OVA among all specials while its title retains
        // the content's own number. Keep those two coordinate systems separate.
        var contents = AnitomySharp.AnitomySharp.Parse(media.DisplayTitle, new(extension: false))
            .Where(element => element.Category == AnitomySharp.Element.ElementCategory.ElementEpisodeTitle)
            .Select(element => ReadContent(element.Value))
            .Where(content => !content.Ambiguous && content.Extra && content.Episode != null).Distinct().ToArray();
        return this with
        {
            ContentType = contents.Length == 1 ? contents[0].Type : "",
            ContentEpisode = contents.Length == 1 ? contents[0].Episode : null
        };
    }

    public bool MatchesContent(string name, int? season = null, double? episode = null)
    {
        var content = Content(name);
        bool playbackNumbered = content.HasSeason && (!content.Extra || content.PlaybackNumbered);
        int? contentSeason = content.Extra && !playbackNumbered ? 0 : content.Season;
        if (content.Ambiguous || Season < 0 || season < 0
            || Season != null && season != null && Season != season
            || contentSeason != null && season != null && contentSeason != season
            || content.Episode != null && episode != null && content.Episode != episode) return false;
        string type = NormalizeType(ContentType);
        if (ContentType.Length > 0 && type.Length == 0 || Season > 0 && SubtitleContent.IsExtra(type)) return false;
        double? number = content.Episode ?? episode;
        if (playbackNumbered)
        {
            // Explicit SxxExx filenames use the library's playback numbering.
            if (Season != content.Season || Season > 0 && content.Extra
                || type.Length > 0 && content.Type.Length > 0 && type != content.Type) return false;
            return number == Episode;
        }
        if (Season == null) return season == null && !content.Extra && number == null
            && (Year == null || content.Year == null || Year == content.Year);
        if (Season > 0) return !content.Extra && content.Type != "MOVIE" && number == Episode;
        if (content.Type is "TV" or "ONA" or "MOVIE" || type.Length > 0 && type != content.Type) return false;
        if (type.Length == 0 && content.Type.Length == 0 && season != 0) return false;
        return number == (ContentEpisode ?? Episode);
    }
    public bool MatchesTitle(IEnumerable<string> titles)
    {
        var wanted = Names.Append(Title).Where(s => s.Length > 0)
            .SelectMany(s => new[] { s, BangumiMedia.Parse(s).Title }).Select(TraktMedia.Normalize).Distinct().ToArray();
        foreach (string title in titles.Where(s => !string.IsNullOrWhiteSpace(s)).SelectMany(BangumiMedia.TitleVariants)
            .SelectMany(s => new[] { s }.Concat(s.Split('/').Where(part => !string.IsNullOrWhiteSpace(part))
                .SelectMany(part => AnitomySharp.AnitomySharp.Parse(part.Trim(), new(episode: false, title: false, extension: false))
                    .Where(element => element.Category == AnitomySharp.Element.ElementCategory.ElementAnimeTitle)
                    .Select(element => BangumiMedia.Parse(element.Value).Title)))))
        {
            string candidate = TraktMedia.Normalize(title);
            if (wanted.Any(name => BangumiMedia.TitleScore(name, candidate) >= .8
                || name.Length >= 5 && candidate.Contains(name, StringComparison.Ordinal))) return true;
        }
        return false;
    }
    public bool MatchesEpisode(string title)
    {
        if (Season > 0 && IsSpecial(title)) return false;
        return MatchesEpisode(BangumiMedia.Parse(title));
    }
    public bool MatchesFile(string name) => MatchesContent(name);
    internal static bool IsSpecial(string title)
    {
        var content = ReadContent(title);
        if (content.Ambiguous && content.Type.Length == 0) return false;
        return content.Extra || content.Season == 0 || Regex.IsMatch(System.IO.Path.GetFileName(title.Replace('\\', '/')),
            SpecialPattern, RegexOptions.IgnoreCase);
    }
    bool MatchesEpisode(BangumiMedia parsed)
    {
        return (Season == null || !parsed.HasSeason || Season == parsed.Season)
            && (Episode == null || parsed.EpisodeNumber == null || Episode == parsed.EpisodeNumber)
            && (Episode != null || Year == null || parsed.Year == null || Year == parsed.Year);
    }
}
public sealed record SubtitleFile(SubtitleSource Source, string ResultId, int FileId, string Name, string Url, string Size = "")
{
    public string LocalPath { get; init; } = "";
    public int? Season { get; init; }
    public double? Episode { get; init; }
}
public sealed record SubtitleResult(SubtitleSource Source, string Id, string Title, string Language, string Details,
    IReadOnlyList<SubtitleFile> Files)
{
    public string DownloadUrl { get; init; } = "";
    public string DownloadName { get; init; } = "";
    public string Format { get; init; } = "";
    public string FormatLabel => Format.Trim() is "" or "不明" or "其他" ? "字幕" : Format.Trim();
    public bool Local { get; init; }
    public bool Complete { get; init; }
    public bool Collection { get; init; }
    public bool Individual { get; init; }
    public IReadOnlyList<string> TitleNames { get; init; } = [];
    public int? MalId { get; init; }
    public int? Year { get; init; }
    public bool? IsMovie { get; init; }
    public int? Episode { get; init; }
    public IReadOnlyList<string> PackageNames { get; init; } = [];
    public int? Season { get; init; }
    public IReadOnlyList<int?> AvailableSeasons { get; init; } = [];
    public string LanguageCode { get; init; } = "";
    public IReadOnlyList<SubtitleLanguage> Languages => LanguageCode.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(code => SubtitleLanguage.Find(code) ?? new SubtitleLanguage("other", "其他")).DistinctBy(language => language.Code).ToArray() is { Length: > 0 } languages
            ? languages : [new("other", "其他")];
    public IReadOnlyList<int> EpisodesFor(int? season, string language = "")
    {
        var episodes = new HashSet<int>();
        if (Episode is >= 0 && (season == null || Season == season)
            && (language.Length == 0 || Languages.Any(item => item.Code == language))) episodes.Add(Episode.Value);
        foreach (var file in Files.Concat(PackageNames.Except(Files.Select(file => file.Name), StringComparer.Ordinal)
            .Select(name => new SubtitleFile(Source, Id, 0, name, ""))).Where(file => SubtitleDownload.Supported(file.Name)))
        {
            if (language.Length > 0)
            {
                string fileLanguage = SubtitleLibrary.FileLanguage(file.Name);
                if (fileLanguage.Length > 0 ? fileLanguage != language : Languages.Count != 1 || Languages[0].Code != language) continue;
            }
            string name = System.IO.Path.GetFileName(file.Name.Replace('\\', '/'));
            var parsed = BangumiMedia.FromPlayback(name, name);
            if (parsed.HasSeason && file.Name.IndexOfAny(['/', '\\']) >= 0 && SubtitleQuery.Content(file.Name).Ambiguous) continue;
            double? episode = file.Episode ?? (parsed.Ambiguous ? null : parsed.EpisodeNumber);
            if (episode is not (>= 0 and <= int.MaxValue) || episode != Math.Truncate(episode.Value)) continue;
            if (season == null)
            {
                episodes.Add((int)episode.Value);
                continue;
            }
            int? fileSeason = file.Season;
            if (fileSeason == null)
            {
                var query = new SubtitleQuery(Title, Season ?? -1, null, language) { Names = TitleNames };
                fileSeason = SubtitleLibrary.FileQuery(query, this, file.Name).Season;
            }
            if (season == null || season == fileSeason) episodes.Add((int)episode.Value);
        }
        return episodes.Order().ToArray();
    }
    public bool MatchesFilter(SubtitleSource? source, string language, int? season, int? episode = null) =>
        (source == null || Source == source) && (season == null || (Local ? AvailableSeasons.Contains(season) : Collection || Season == season))
        && (language.Length == 0 || Languages.Any(item => item.Code == language))
        && (episode == null || EpisodesFor(season, language).Contains(episode.Value));
}
public sealed record SubtitlePage(IReadOnlyList<SubtitleResult> Results, bool HasMore)
{
    public string PageKey { get; init; } = "";
}
public sealed record SubtitleCredentials(string AssrtToken)
{
    public bool AssrtEnabled { get; init; } = true;
    public bool AmenzbEnabled { get; init; } = true;
    public bool TsukihimeEnabled { get; init; } = true;
    public bool IsEnabled(SubtitleSource source) => source switch
    {
        SubtitleSource.Assrt => AssrtEnabled,
        SubtitleSource.Amenzb => AmenzbEnabled,
        SubtitleSource.Tsukihime => TsukihimeEnabled,
        _ => false
    };
}
public sealed record SubtitleArchiveNameOption(string Encoding, string Name);
public sealed record SubtitleArchiveName(string Key, IReadOnlyList<SubtitleArchiveNameOption> Options);

public class SubtitleServiceException(string message) : Exception(message);

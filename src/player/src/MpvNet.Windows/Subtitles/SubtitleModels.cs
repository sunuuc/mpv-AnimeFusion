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

public sealed record SubtitleQuery(string Title, int? Season, int? Episode, string Language)
{
    public IReadOnlyList<string> Names { get; init; } = [];
    public int? Year { get; init; }
    public int? SearchSeason { get; init; }
    public int? RequestSeason { get; init; }
    public int? RequestEpisode { get; init; }
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
        if (Episode != null && Season > 0 && Special(title) && !Names.Append(Title).Any(Special)) return false;
        return MatchesEpisode(BangumiMedia.Parse(title));
    }
    public bool MatchesFile(string name)
    {
        var parsed = BangumiMedia.FromPlayback(name, name);
        return parsed.Ambiguous || MatchesEpisode(parsed);
    }
    static bool Special(string title) => Regex.IsMatch(title,
        @"\b(?:OVA|OAD|Special)\b|特典|特别篇|特別篇", RegexOptions.IgnoreCase);
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
            double? episode = file.Episode ?? (parsed.Ambiguous ? null : parsed.EpisodeNumber);
            if (episode is not (>= 0 and <= int.MaxValue) || episode != Math.Truncate(episode.Value)) continue;
            int? fileSeason = file.Season;
            if (fileSeason == null)
            {
                var seasons = file.Name.Replace('\\', '/').Split('/').SkipLast(1).Select(BangumiMedia.Parse)
                    .Append(parsed).Where(media => media.HasSeason).Select(media => media.Season).Distinct().ToArray();
                if (seasons.Length > 1) continue;
                fileSeason = seasons.Length == 1 ? seasons[0] : Season;
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

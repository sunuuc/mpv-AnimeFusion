using System.Text.RegularExpressions;
using System.Globalization;
using MpvNet.Windows.Bangumi;
using MpvNet.Windows.Trakt;

namespace MpvNet.Windows.Subtitles;

public enum SubtitleSource { Assrt }

public sealed record SubtitleLanguage(string Code, string Name)
{
    static readonly CultureInfo[] Cultures = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
        .Where(culture => culture.Name.Length > 0).ToArray();
    public static SubtitleLanguage? Find(string code)
    {
        var culture = Cultures.FirstOrDefault(culture => culture.Name.Equals(code, StringComparison.OrdinalIgnoreCase)
            || culture.TwoLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase)
            || culture.ThreeLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase));
        return culture == null ? null : new(culture.TwoLetterISOLanguageName, culture.DisplayName);
    }
}

public sealed record SubtitleQuery(string Title, int? Season, int? Episode, string Language)
{
    public IReadOnlyList<string> Names { get; init; } = [];
    public int? Year { get; init; }
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
    public IReadOnlyList<string> PackageNames { get; init; } = [];
    public int? Season { get; init; }
    public IReadOnlyList<int?> AvailableSeasons { get; init; } = [];
    public string LanguageCode { get; init; } = "";
    public IReadOnlyList<SubtitleLanguage> Languages => LanguageCode.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(code => SubtitleLanguage.Find(code) ?? new SubtitleLanguage("other", "其他")).DistinctBy(language => language.Code).ToArray() is { Length: > 0 } languages
            ? languages : [new("other", "其他")];
    public bool MatchesFilter(SubtitleSource? source, string language, int? season) =>
        (source == null || Source == source) && (season == null || (Local ? AvailableSeasons.Contains(season) : Collection || Season == season))
        && (language.Length == 0 || Languages.Any(item => item.Code == language));
}
public sealed record SubtitlePage(IReadOnlyList<SubtitleResult> Results, bool HasMore);
public sealed record SubtitleCredentials(string AssrtToken);
public sealed record SubtitleArchiveNameOption(string Encoding, string Name);
public sealed record SubtitleArchiveName(string Key, IReadOnlyList<SubtitleArchiveNameOption> Options);

public class SubtitleServiceException(string message) : Exception(message);

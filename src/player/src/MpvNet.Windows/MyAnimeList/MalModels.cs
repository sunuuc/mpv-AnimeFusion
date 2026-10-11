using System.Text.Json.Serialization;
using MpvNet.Windows.Bangumi;

namespace MpvNet.Windows.MyAnimeList;

public sealed record MalPicture(string? Medium, string? Large);
public sealed record MalAlternativeTitles(string[]? Synonyms, string? En, string? Ja);
public sealed record MalListStatus(string Status, int NumEpisodesWatched);
public sealed record MalRelation(MalSubject Node, string RelationType);
public sealed record MalSubject(int Id, string Title, MalPicture? MainPicture, MalAlternativeTitles? AlternativeTitles,
    string? StartDate, string? MediaType, int NumEpisodes, double? Mean, MalListStatus? MyListStatus, MalRelation[]? RelatedAnime)
{
    public IEnumerable<string> Names => new[] { Title, AlternativeTitles?.En ?? "", AlternativeTitles?.Ja ?? "" }
        .Concat(AlternativeTitles?.Synonyms ?? []).Where(s => s.Length > 0);
    public string Display => Title + " · " + Details;
    public string Details => (StartDate ?? "年份未知") + " · " + (MediaType ?? "") + " · " + NumEpisodes + " 集";
    public string Poster => MainPicture?.Large ?? MainPicture?.Medium ?? "";
}
public sealed record MalSearchNode(MalSubject Node);
public sealed record MalSearchResponse(MalSearchNode[] Data);
public sealed record MalEpisode(int Number)
{
    public string Display => "第 " + Number + " 集";
}
public sealed record MalMedia(BangumiMedia Parsed, int? MalId, int? BangumiId)
{
    public string Title => Parsed.Title;
    public string Key => Parsed.Key;
    public int Season => Parsed.Season;
    public int? Episode => Parsed.EpisodeNumber is double n && n >= 1 && n <= int.MaxValue && n == Math.Truncate(n) ? (int)n : null;
    public static MalMedia Parse(string title, string path, IReadOnlyDictionary<string, string> metadata)
    {
        int? Id(string key) => metadata.TryGetValue(key, out var value) && int.TryParse(value, out int id) && id > 0 ? id : null;
        return new(BangumiMedia.FromPlayback(title, path), Id("mal"), Id("bangumi"));
    }
}

// Rhilip/BangumiExtLinker data (CC BY 4.0); attribution in docs/myanimelist-research.md.
public sealed record MalLink(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("name_cn")] string NameCn,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("bgm_id")] string BangumiId,
    [property: JsonPropertyName("mal_id")] string? MalId,
    [property: JsonPropertyName("tmdb_id")] string? TmdbId)
{
    public int? Season
    {
        get
        {
            var match = System.Text.RegularExpressions.Regex.Match(TmdbId ?? "", @"^tv/\d+/season/(\d+)$");
            if (match.Success) return int.Parse(match.Groups[1].Value);
            var named = new[] { BangumiMedia.Parse(Name), BangumiMedia.Parse(NameCn) }.Where(n => n.HasSeason).Select(n => n.Season).Distinct().ToArray();
            return named.Length == 1 ? named[0] : null;
        }
    }
}

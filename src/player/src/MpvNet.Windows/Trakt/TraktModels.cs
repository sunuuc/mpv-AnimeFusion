// API contracts adapted from trakt/trakt-api (MIT, Copyright (c) 2024 Trakt Inc.).
// See THIRD_PARTY_LICENSES/trakt-api-MIT.txt.
namespace MpvNet.Windows.Trakt;

public sealed record TraktIds(int Trakt, string? Slug, string? Imdb, int? Tmdb, int? Tvdb);
public sealed record TraktImages(string[]? Poster);
public sealed record TraktSubject(string Title, int? Year, TraktIds Ids, double? Rating, TraktImages? Images)
{
    public string Kind { get; init; } = "show";
    public string Display => Title + (Year is int year ? " (" + year + ")" : "") + (Kind == "movie" ? " · 电影" : " · 剧集");
    public string Details => (Year?.ToString() ?? "年份未知") + (Kind == "movie" ? " · 电影" : " · 剧集");
    public string Poster => Images?.Poster?.FirstOrDefault() ?? "";
}
public sealed record TraktEpisode(int Season, int Number, string? Title, TraktIds Ids)
{
    public string Display => "第 " + Number + " 集" + (string.IsNullOrEmpty(Title) ? "" : " · " + Title);
}
public sealed record TraktSeason(int Number, int? EpisodeCount);
public sealed record TraktSeasonEpisodes(int Number, TraktEpisode[] Episodes);
public sealed record TraktSearchResult(string Type, TraktSubject? Movie, TraktSubject? Show);

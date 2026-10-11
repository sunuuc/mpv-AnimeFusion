using System.Text;
using System.Text.RegularExpressions;
using MpvNet.Windows.Bangumi;

namespace MpvNet.Windows.Trakt;

public sealed record TraktMedia(BangumiMedia Parsed, IReadOnlyDictionary<string, string> Ids)
{
    public string Title => Parsed.Title;
    public int Season => Parsed.Season;
    public int? Episode => Parsed.EpisodeNumber is double n && n == Math.Floor(n) && n >= 0 && n <= int.MaxValue ? (int)n : null;
    public string Kind => Parsed.EpisodeNumber != null || Parsed.HasSeason ? "show" : "movie";
    public string Key => Kind + "|" + Parsed.Key;

    public static TraktMedia Parse(string title, string path, IReadOnlyDictionary<string, string> metadata)
    {
        var ids = new Dictionary<string, string>();
        foreach (string provider in new[] { "imdb", "tmdb", "tvdb", "trakt" })
        {
            string? value = metadata.FirstOrDefault(p => p.Key.Equals(provider + "_id", StringComparison.OrdinalIgnoreCase)
                || p.Key.Equals(provider, StringComparison.OrdinalIgnoreCase)).Value;
            var tagged = Regex.Match(title + " " + path, @"[\[{]" + provider + @"[-:= ]+(tt\d+|\d+)[\]}]", RegexOptions.IgnoreCase);
            if (string.IsNullOrWhiteSpace(value) && tagged.Success) value = tagged.Groups[1].Value;
            if (value != null && Regex.IsMatch(value, provider == "imdb" ? @"\Att\d+\z" : @"\A\d+\z")) ids[provider] = value;
        }
        var parsed = BangumiMedia.FromPlayback(title, path);
        return new(parsed, ids);
    }

    public static string Normalize(string title) => Regex.Replace(title.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"[\s\p{P}\p{S}]+", "");
    public double Score(string title) => BangumiMedia.TitleVariants(Title)
        .SelectMany(input => BangumiMedia.TitleVariants(title).Select(name =>
            BangumiMedia.TitleScore(Normalize(input), Normalize(BangumiMedia.Parse(name).Title))))
        .DefaultIfEmpty(0).Max();
}

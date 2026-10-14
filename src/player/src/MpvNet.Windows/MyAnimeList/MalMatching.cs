using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MpvNet.Windows.Bangumi;

namespace MpvNet.Windows.MyAnimeList;

public static class MalMatching
{
    static readonly Lazy<MalLink[]> Links = new(() =>
    {
        using var stream = typeof(MalMatching).Assembly.GetManifestResourceStream("MpvNet.Windows.MyAnimeList.anime-map.json")
            ?? throw new InvalidOperationException("缺少跨站匹配数据。");
        return JsonSerializer.Deserialize<MalLink[]>(stream) ?? [];
    });
    static string TitleIdentity(string title) => Regex.Replace(title.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"[\s\p{P}\p{S}]+", "");
    public static double TitleScore(string input, string title)
    {
        var wanted = BangumiMedia.TitleVariants(input).Select(TitleIdentity).Distinct().ToArray();
        var names = BangumiMedia.TitleVariants(title).Select(TitleIdentity).Distinct().ToArray();
        double score = 0;
        foreach (string a in wanted)
            foreach (string b in names)
            {
                score = Math.Max(score, BangumiMedia.TitleScore(a, b));
                if (score == 1) return score;
            }
        return score;
    }
    static double Score(MalMedia media, string title) => TitleScore(media.Title, BangumiMedia.Parse(title).Title);
    static bool DateMatches(MalMedia media, MalSubject subject) => media.Parsed.Year == null
        || subject.StartDate?.StartsWith(media.Parsed.Year + "-", StringComparison.Ordinal) == true;
    static bool SeasonMatches(MalMedia media, MalSubject subject, IEnumerable<MalLink> links, int? catalogueSeason = null)
    {
        if (media.Episode == null) return !media.Parsed.HasSeason;
        var seasons = subject.Names.Select(BangumiMedia.Parse).Where(n => n.HasSeason).Select(n => n.Season)
            .Concat(links.Select(l => l.Season).OfType<int>())
            .Concat(catalogueSeason is int season ? [season] : Array.Empty<int>()).Distinct().ToArray();
        if (seasons.Length > 0) return seasons.Length == 1 && seasons[0] == media.Season;
        // First-season evidence requires a regular TV entry and no prequel relation.
        // Unnamed later seasons and split cours remain for manual confirmation.
        return media.Season == 1 && subject.MediaType == "tv" && subject.RelatedAnime != null
            && !subject.RelatedAnime.Any(r => r.RelationType == "prequel");
    }
    public static bool EpisodeFits(MalMedia media, MalSubject subject) => !media.Parsed.Ambiguous
        && (media.Episode is int ep ? subject.MediaType != "movie" && (subject.NumEpisodes == 0 || ep <= subject.NumEpisodes)
            : !media.Parsed.HasSeason && subject.MediaType == "movie");

    public static IEnumerable<string> SearchNames(string title)
    {
        var media = new MalMedia(BangumiMedia.Parse(title), null, null);
        var aliases = Links.Value.Where(l => Math.Max(Score(media, l.Name), Score(media, l.NameCn)) >= .9)
            .Select(l => l.Name).Distinct().Take(5);
        return BangumiMedia.SearchTitles(title).Concat(aliases).Where(s => s.Length > 0).Distinct();
    }
    public static async Task<MalSubject?> MatchAsync(MalClient client, MalMedia media, CancellationToken token)
    {
        if (media.Parsed.Ambiguous) return null;
        if (media.MalId is int id)
        {
            var linked = Links.Value.Where(l => l.MalId == id.ToString()).ToArray();
            if (media.BangumiId is int bgm && !linked.Any(l => l.BangumiId == bgm.ToString())) return null;
            var known = await client.SubjectAsync(id, false, token).ConfigureAwait(false);
            return EpisodeFits(media, known) && DateMatches(media, known) && SeasonMatches(media, known, linked) ? known : null;
        }
        // A catalogue ID is evidence only after its exact season and episode range are checked.
        var ranked = await Task.Run(() => Links.Value.Where(l => int.TryParse(l.MalId, out int n) && n > 0)
            .Where(l => media.BangumiId == null || l.BangumiId == media.BangumiId.Value.ToString())
            .Where(l => l.Season == null || media.Episode == null || l.Season == media.Season)
            .Where(l => media.Parsed.Year == null || l.Date.StartsWith(media.Parsed.Year + "-", StringComparison.Ordinal))
            .Select(l => (Link: l, Score: media.BangumiId != null ? 1 : Math.Max(Score(media, l.Name), Score(media, l.NameCn))))
            .Where(v => v.Score >= .8).OrderByDescending(v => v.Score).DistinctBy(v => v.Link.MalId).Take(8).ToArray(), token).ConfigureAwait(false);
        var verified = new List<(MalSubject Subject, double Score)>();
        foreach (var item in ranked)
        {
            var subject = await client.SubjectAsync(int.Parse(item.Link.MalId!), false, token).ConfigureAwait(false);
            if (!EpisodeFits(media, subject) || !DateMatches(media, subject)) continue;
            // Cross-site maps can contain split/merged entries; verify the target name and premiere month.
            if (subject.StartDate == null || !subject.StartDate.StartsWith(item.Link.Date, StringComparison.Ordinal)
                || !subject.Names.Any(n => TitleScore(n, item.Link.Name) >= .8)) continue;
            if (!SeasonMatches(media, subject, [item.Link])) continue;
            verified.Add((subject, item.Score));
        }
        if (verified.Count > 0)
        {
            verified.Sort((a,b) => b.Score.CompareTo(a.Score));
            return verified.Count == 1 || verified[0].Score - verified[1].Score >= .08 ? verified[0].Subject : null;
        }
        var catalogue = await client.CatalogueAsync(media.BangumiId, media.Title, token).ConfigureAwait(false);
        var entry = media.Parsed.MatchSubject(catalogue.Where(s => media.Parsed.Year == null
            || s.Date?.StartsWith(media.Parsed.Year + "-", StringComparison.Ordinal) == true).ToArray());
        if (entry != null && !string.IsNullOrWhiteSpace(entry.Name))
        {
            var names = BangumiMedia.SubjectTitles(entry).SelectMany(BangumiMedia.TitleVariants).ToArray();
            var candidates = await client.SearchNameAsync(entry.Name, token).ConfigureAwait(false);
            var linked = new List<(MalSubject Subject, double Score)>();
            foreach (var candidate in candidates)
            {
                if (!EpisodeFits(media, candidate) || !DateMatches(media, candidate)) continue;
                // Verify the live catalogue entry against the target title, premiere month and medium.
                if (entry.Date is not { Length: >= 7 } || candidate.StartDate is not { Length: >= 7 }
                    || entry.Date[..7] != candidate.StartDate[..7]) continue;
                if (entry.Platform?.ToLowerInvariant() is "tv" && candidate.MediaType != "tv") continue;
                double score = names.SelectMany(n => candidate.Names.Select(t => TitleScore(n, t))).DefaultIfEmpty().Max();
                if (score < .8 || !SeasonMatches(media, candidate, [], media.Episode != null ? media.Season : null)) continue;
                linked.Add((candidate, score));
            }
            linked.Sort((a,b) => b.Score.CompareTo(a.Score));
            if (linked.Count > 0)
                return linked.Count == 1 || linked[0].Score - linked[1].Score >= .08 ? linked[0].Subject : null;
        }
        var searched = await client.SearchAsync(media.Title, token).ConfigureAwait(false);
        var detailed = new List<MalSubject>();
        foreach (var candidate in searched.Where(s => EpisodeFits(media, s) && DateMatches(media, s)
            && s.Names.Any(n => Score(media, n) >= .8)).Take(8))
            detailed.Add(await client.SubjectAsync(candidate.Id, false, token).ConfigureAwait(false));
        var matches = detailed.Where(s => EpisodeFits(media, s) && DateMatches(media, s))
            .Where(s => SeasonMatches(media, s, []))
            .Select(s => (Subject: s, Score: s.Names.Select(n => Score(media, n)).DefaultIfEmpty().Max()))
            .Where(v => v.Score >= .8).OrderByDescending(v => v.Score).ToArray();
        return matches.Length == 1 || matches.Length > 1 && matches[0].Score - matches[1].Score >= .08 ? matches[0].Subject : null;
    }
}

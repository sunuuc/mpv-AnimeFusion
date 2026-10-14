// Title normalization, alias candidate ranking, series/season key and episode matching adapted from
// x-Armin/mpv_bangumi_sync src/title_guess.lua, src/stream_data.lua and src/episode_matcher.lua (MIT).
// Copyright (c) 2026 Armin Tso. See THIRD_PARTY_LICENSES/mpv_bangumi_sync-MIT.txt.
using System.Text;
using System.Text.RegularExpressions;
using BangumiNet.Api.V0.Models;

namespace MpvNet.Windows.Bangumi;

public sealed record BangumiMedia(string Title, int Season, double? EpisodeNumber, string Key)
{
    public int? Year { get; init; }
    public bool HasSeason { get; init; }
    public bool Ambiguous { get; init; }

    public static BangumiMedia FromPlayback(string title, string path)
    {
        string filename = Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile
            ? Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last()) : Path.GetFileName(path);
        bool fileTitle = string.IsNullOrWhiteSpace(title) || title == path || title == filename || title.Contains("://");
        string input = fileTitle ? filename : title;
        var media = Parse(input);
        var elements = AnitomySharp.AnitomySharp.Parse(fileTitle ? filename : input, new(extension: fileTitle)).ToArray();
        string Value(AnitomySharp.Element.ElementCategory kind) => elements.FirstOrDefault(e => e.Category == kind)?.Value ?? "";
        var numbers = elements.Where(e => e.Category == AnitomySharp.Element.ElementCategory.ElementEpisodeNumber)
            .Select(e => e.Value).Distinct().ToArray();
        bool ambiguous = numbers.Length > 1;
        double? episode = numbers.Length == 1 && double.TryParse(numbers[0], System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out double ep) ? ep : null;
        int? season = int.TryParse(Value(AnitomySharp.Element.ElementCategory.ElementAnimeSeason), out int sn) ? sn : null;
        string name = fileTitle ? Value(AnitomySharp.Element.ElementCategory.ElementAnimeTitle) : media.Title;
        if (string.IsNullOrWhiteSpace(name)) name = media.Title;
        var named = Parse(name);
        if (media.EpisodeNumber == null) media = media with { EpisodeNumber = episode };
        if (!media.HasSeason && season != null) media = media with { Season = season.Value, HasSeason = true };
        if (named.HasSeason && !media.HasSeason) media = media with { Season = named.Season, HasSeason = true };
        if (!fileTitle && filename.Length > 0)
        {
            var file = FromPlayback(filename, filename);
            ambiguous |= file.Ambiguous || (media.EpisodeNumber != null && file.EpisodeNumber != null && media.EpisodeNumber != file.EpisodeNumber)
                || (media.HasSeason && file.HasSeason && media.Season != file.Season);
            if (media.EpisodeNumber == null) media = media with { EpisodeNumber = file.EpisodeNumber };
            if (!media.HasSeason && file.HasSeason) media = media with { Season = file.Season, HasSeason = true };
        }
        int? year = int.TryParse(Value(AnitomySharp.Element.ElementCategory.ElementAnimeYear), out int yr) ? yr : null;
        string normalized = Regex.Replace(named.Title.ToLowerInvariant(), @"[\s\p{P}\p{S}]+", "");
        return media with { Title = named.Title, Year = media.Year ?? year, Ambiguous = ambiguous, Key = normalized + "|s" + media.Season };
    }

    public static BangumiMedia Parse(string title)
    {
        string text = title.Normalize(NormalizationForm.FormKC).Replace('\u2009', ' ').Trim();
        var yearMatch = Regex.Match(text, @"\(((?:19|20)\d{2})(?:-\d{2})?\)");
        var match = Regex.Match(text, @"^(.*?)\s*[sS]0*(\d+)[ ._-]*[eE]0*(\d+(?:\.\d+)?)");
        int season = 1;
        double? episode = null;
        if (match.Success)
        {
            text = match.Groups[1].Value;
            season = int.Parse(match.Groups[2].Value);
            episode = double.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            match = Regex.Match(text, @"^(.*?)\s*(?:(?<![A-Za-z0-9])[eE](\d+(?:\.\d+)?)\b|第\s*(\d+(?:\.\d+)?)\s*[集话話])");
            if (match.Success)
            {
                text = match.Groups[1].Value;
                string number = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
                episode = double.Parse(number, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        var seasonMatch = Regex.Match(text, @"第\s*([\d一二三四五六七八九十]+)\s*[季部]");
        if (seasonMatch.Success)
        {
            string number = seasonMatch.Groups[1].Value;
            if (!int.TryParse(number, out season)) season = ChineseNumber(number);
        }
        else
        {
            seasonMatch = Regex.Match(text, @"\b(?:Season\s*|S)(\d+)\b|\b(\d+)(?:st|nd|rd|th)\s+Season\b", RegexOptions.IgnoreCase);
            if (seasonMatch.Success)
                season = int.Parse(seasonMatch.Groups[1].Success ? seasonMatch.Groups[1].Value : seasonMatch.Groups[2].Value);
            else
            {
                seasonMatch = Regex.Match(text, @"(?:(?<=[\p{IsCJKUnifiedIdeographs}])\s*|\s+)(VIII|VII|VI|IV|III|II|IX|V|X|I)(?=\s*(?:[~～:：(\-]|$))");
                if (seasonMatch.Success)
                    season = Array.IndexOf(new[] { "", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X" }, seasonMatch.Groups[1].Value);
            }
        }
        if (seasonMatch.Success) text = text.Remove(seasonMatch.Index, seasonMatch.Length);
        text = Regex.Replace(text, @"\s*\((?:19|20)\d{2}(?:-\d{2})?\)\s*$", "").Trim(' ', '-', '_');
        string normalized = Regex.Replace(text.ToLowerInvariant(), @"[\s\p{P}\p{S}]+", "");
        return new(text, season, episode, normalized + "|s" + season)
        {
            Year = yearMatch.Success ? int.Parse(yearMatch.Groups[1].Value) : null,
            HasSeason = seasonMatch.Success || Regex.IsMatch(title.Normalize(NormalizationForm.FormKC), @"[sS]\d+[ ._-]*[eE]\d+")
        };
    }

    public static bool IsMovie(Subject subject) => subject.Platform?.Trim().ToLowerInvariant()
        is "剧场版" or "劇場版" or "电影" or "movie" or "film";

    static string MovieTitle(string title)
    {
        string text = Parse(title).Title;
        text = Regex.Replace(text, @"^(?:剧场版|劇場版|电影版|電影版|Gekijouban\b)\s*", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s*(?:\bThe Movie|\bMovie|剧场版|劇場版)$", "", RegexOptions.IgnoreCase);
        return Regex.Replace(text.ToLowerInvariant(), @"[\s\p{P}\p{S}]+", "");
    }

    public Subject? MatchSubject(IReadOnlyList<Subject> subjects)
    {
        if (Ambiguous) return null;
        bool movie = EpisodeNumber == null;
        if (movie && HasSeason) return null;
        var wanted = TitleVariants(Title).Select(t => movie ? MovieTitle(t) : TitleIdentity(Parse(t).Title)).ToArray();
        var ranked = new List<(Subject Subject, double Score)>();
        foreach (var subject in subjects.DistinctBy(s => s.Id))
        {
            if (IsMovie(subject) != movie) continue;
            if (movie && Year != null && subject.Date?.StartsWith(Year.Value + "-", StringComparison.Ordinal) != true) continue;
            var names = SubjectTitles(subject).SelectMany(TitleVariants).Select(Parse).ToArray();
            var seasons = names.Where(n => n.HasSeason).Select(n => n.Season).Distinct().ToArray();
            if (!movie && (seasons.Length > 1 || (seasons.FirstOrDefault(1) != Season))) continue;
            double score = 0;
            foreach (var name in names)
                foreach (var input in wanted)
                    score = Math.Max(score, TitleScore(input, movie ? MovieTitle(name.Title) : TitleIdentity(name.Title)));
            if (score >= .8) ranked.Add((subject, score));
        }
        ranked.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (ranked.Count == 0) return null;
        if (ranked.Count == 1 || ranked[0].Score - ranked[1].Score >= .08) return ranked[0].Subject;
        if (movie && Year != null)
        {
            var tied = ranked.Where(s => s.Score == ranked[0].Score).ToArray();
            var dated = tied.Where(s => s.Subject.Date?.StartsWith(Year.Value + "-", StringComparison.Ordinal) == true).ToArray();
            if (tied.Length == ranked.Count && dated.Length == 1) return dated[0].Subject;
        }
        return null;
    }

    public static IEnumerable<string> SubjectTitles(Subject subject)
    {
        if (!string.IsNullOrWhiteSpace(subject.NameCn)) yield return subject.NameCn;
        if (!string.IsNullOrWhiteSpace(subject.Name)) yield return subject.Name;
        foreach (var field in subject.Infobox ?? [])
        {
            string key = field.Key ?? "";
            if (!Regex.IsMatch(key, "别名|別名|英文名|日文名|原作名|alias", RegexOptions.IgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(field.Value?.String)) yield return field.Value.String;
            foreach (var alias in field.Value?.SubjectsValueMember1 ?? [])
                if (alias.AdditionalData.TryGetValue("v", out var value) && value is string name && !string.IsNullOrWhiteSpace(name))
                    yield return name;
        }
    }

    public static IEnumerable<string> TitleVariants(string title)
    {
        yield return title;
        foreach (string part in Regex.Split(title.Normalize(NormalizationForm.FormKC), @"[、;；\r\n]+"))
        {
            string value = part.Trim();
            if (value.Length == 0) continue;
            yield return value;
            string movie = Regex.Replace(value, @"^(?:剧场版|劇場版|电影版|電影版|Gekijouban\b)\s*", "", RegexOptions.IgnoreCase);
            if (movie != value && movie.Length > 0) yield return movie;
            string? supplementary = SupplementaryTitle(value);
            if (supplementary != null) yield return supplementary;
            // Bilingual aliases may prepend a Latin title to a CJK translation.
            string translated = Regex.Replace(value, @"^[A-Za-z][A-Za-z0-9 ._'&:+\-]*\s+(?=[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}])", "");
            if (translated != value) yield return translated;
        }
    }

    static string? SupplementaryTitle(string title)
    {
        // Library titles can prefix a separately catalogued short with its franchise
        // and medium. Only labelled supplements qualify; an ordinary series subtitle
        // must retain its franchise identity.
        var match = Regex.Match(title.Normalize(NormalizationForm.FormKC),
            @"^.+?(?:特典|特别篇|特別篇|特别版|特別版|剧场版|劇場版|电影版|電影版|\bSpecial|\bOVA|\bOAD)\s*:\s*(.+)$", RegexOptions.IgnoreCase);
        string value = match.Groups[1].Value.Trim();
        return match.Success && value.EnumerateRunes().Count() >= 5 ? value : null;
    }

    public static IEnumerable<string> SearchTitles(string title)
    {
        yield return title;
        var parsed = Parse(title);
        // Missing episode and season metadata identifies the standalone-title path.
        if (parsed.EpisodeNumber == null && !parsed.HasSeason && SupplementaryTitle(parsed.Title) is string supplementary)
            yield return supplementary;
    }

    static string TitleIdentity(string title) => Regex.Replace(title.ToLowerInvariant(), @"[\s\p{P}\p{S}]+", "");

    public static double TitleScore(string input, string title)
    {
        if (input == title) return input.Length > 0 ? 1 : 0;
        if (string.Join(',', Regex.Matches(input, @"\d+").Select(m => m.Value))
            != string.Join(',', Regex.Matches(title, @"\d+").Select(m => m.Value))) return 0;
        var a = input.EnumerateRunes().ToArray();
        var b = title.EnumerateRunes().ToArray();
        int length = Math.Max(a.Length, b.Length), limit = (int)(length * .2);
        if (Math.Min(a.Length, b.Length) < 5 || length > 120 || Math.Abs(a.Length - b.Length) > limit) return 0;
        int[] previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int[] current = new int[b.Length + 1];
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[b.Length] <= limit ? 1 - (double)previous[b.Length] / length : 0;
    }

    static int ChineseNumber(string number)
    {
        const string digits = "零一二三四五六七八九";
        int ten = number.IndexOf('十');
        if (ten < 0) return number.Length == 1 ? digits.IndexOf(number[0]) : 0;
        int tens = ten == 0 ? 1 : digits.IndexOf(number[0]);
        int ones = ten == number.Length - 1 ? 0 : digits.IndexOf(number[ten + 1]);
        return tens * 10 + ones;
    }

    public static Episode? MatchEpisode(IReadOnlyList<Episode> episodes, double? number, int season, bool movie = false)
    {
        var main = episodes.Where(e => e.Type == 0).OrderBy(e => e.Sort).ToArray();
        if (number == null) return movie && main.Length == 1 ? main[0] : null;
        var exact = main.FirstOrDefault(e => e.Ep == number) ?? main.FirstOrDefault(e => e.Sort == number);
        if (exact != null) return exact;
        // A season-local S3E1 can be Bangumi episode 38. Use the same ordered
        // main-episode list as the watched-through action, excluding specials.
        if (season > 1 && number >= 1 && number == Math.Floor(number.Value) && number <= main.Length)
            return main[(int)number.Value - 1];
        return episodes.FirstOrDefault(e => e.Ep == number)
            ?? episodes.FirstOrDefault(e => e.Sort == number);
    }
}

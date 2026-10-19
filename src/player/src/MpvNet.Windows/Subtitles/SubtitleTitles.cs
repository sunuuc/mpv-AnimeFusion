using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BangumiNet.Api.V0.Models;
using MpvNet.Windows.Bangumi;
using MpvNet.Windows.MyAnimeList;
using MpvNet.Windows.Trakt;

namespace MpvNet.Windows.Subtitles;

public sealed class SubtitleTitles
{
    readonly TraktMedia _media;
    readonly CancellationToken _lifetime;
    readonly MalClient? _client;
    readonly object _lock = new();
    readonly Dictionary<string, Task<SubtitleQuery>> _queries = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Subject> _subjects = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, Task<MalSubject?>> _translations = new();
    readonly HashSet<string> _currentNames = new(StringComparer.OrdinalIgnoreCase);
    int? _currentMalId;

    public SubtitleTitles(TraktMedia media, CancellationToken lifetime, MalClient? client = null)
    {
        _media = media; _lifetime = lifetime; _client = client ?? MalPlayback.Current?.Client;
        foreach (string name in KnownNames()) _currentNames.Add(Name(name));
    }

    static string Name(string title) => string.Concat(title.Where(character => !char.IsControl(character))).Trim();
    static string Key(string title) => Name(title).Normalize(NormalizationForm.FormKC);

    public bool IsCurrent(string title)
    {
        lock (_lock)
        {
            foreach (string name in KnownNames()) _currentNames.Add(Name(name));
            string keyword = Key(title);
            string work = Regex.Replace(keyword, @"\s+(?:S[0-9]+(?:E[0-9]+)?|E[0-9]+)$", "",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            // Season/episode selectors are request conditions, not a different work.
            // Keep the full keyword when sending the subtitle search.
            return !_media.Parsed.Ambiguous && _currentNames.Select(Key).Any(name =>
                StringComparer.OrdinalIgnoreCase.Equals(name, keyword) || StringComparer.OrdinalIgnoreCase.Equals(name, work));
        }
    }

    public async Task<IReadOnlyList<string>> NamesAsync(CancellationToken token)
        => (await ResolveAsync(_media.Title, token).ConfigureAwait(false)).Names;

    public IReadOnlyList<string> CachedNames
    {
        get { lock (_lock) return _currentNames.ToArray(); }
    }

    public SubtitleWork CurrentWork()
    {
        lock (_lock)
        {
            foreach (string name in KnownNames()) _currentNames.Add(Name(name));
            int? malId = _currentMalId;
            var mal = MalPlayback.Current;
            if (mal?.Store.FindBinding(_media.Parsed.Key) is { } binding && binding.Season == _media.Season)
                malId = binding.Id;
            if (mal?.Media.Key == _media.Parsed.Key && mal.Subject is { } subject && mal.Season == _media.Season)
                malId = subject.Id;
            return new(_media, _currentNames.ToArray(), malId);
        }
    }

    public Task<SubtitleQuery> QueryAsync(string title, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _lifetime.ThrowIfCancellationRequested();
        string keyword = Key(title);
        bool current = IsCurrent(keyword);
        lock (_lock)
        {
            // Subtitle searches use only information already in memory. Catalogue reads
            // belong to the explicit alias picker, never to the subtitle request itself.
            IEnumerable<string> names = current ? _currentNames : [keyword];
            if (_queries.TryGetValue(keyword, out var cached) && cached.IsCompletedSuccessfully)
                names = names.Concat(cached.Result.Names);
            return Task.FromResult(Query(keyword, current, names));
        }
    }

    async Task<SubtitleQuery> ResolveAsync(string title, CancellationToken token)
    {
        string keyword = Key(title);
        bool current = IsCurrent(keyword);
        string key = current ? Key(_media.Title) : keyword;
        Task<SubtitleQuery> pending;
        lock (_lock)
        {
            if (!_queries.TryGetValue(key, out pending!) || pending.IsFaulted || pending.IsCanceled)
                _queries[key] = pending = LoadAsync(key, current);
        }
        MpvNet.Diagnostics.Write("subtitle.alias", "start", new { current, cached = pending.IsCompletedSuccessfully });
        try
        {
            var query = await pending.WaitAsync(token).ConfigureAwait(false);
            // Keep the successful catalogue read independently of optional translations.
            // A failed translation can then be retried without requesting the catalogue again.
            var names = (current ? query.Names.Concat(KnownNames()) : query.Names).ToList();
            Subject? subject;
            lock (_lock) subject = _subjects.GetValueOrDefault(key);
            if (current && BangumiPlayback.Current is { Subject: { } matched } playback && playback.Media.Key == _media.Parsed.Key)
                subject = matched;
            if (subject != null)
                await CompleteNamesAsync(subject, names, token, current).ConfigureAwait(false);
            query = Query(query.Title, current, names);
            Remember(query, current);
            MpvNet.Diagnostics.Write("subtitle.alias", "complete", new { count = query.Names.Count });
            return query with { Title = keyword };
        }
        catch (Exception error)
        {
            MpvNet.Diagnostics.Write("subtitle.alias", token.IsCancellationRequested ? "cancelled" : "failed", error: error);
            // Closing the popup cancels only its wait. A failed catalogue read can be retried
            // on the next explicit action, while an in-flight read remains shared.
            if (pending.IsCanceled || pending.IsFaulted)
                lock (_lock) if (_queries.GetValueOrDefault(key) == pending) _queries.Remove(key);
            throw;
        }
    }

    async Task<SubtitleQuery> LoadAsync(string title, bool current)
    {
        var names = new List<string>(current ? KnownNames() : [title]);
        var bangumi = BangumiPlayback.Current;
        Subject? subject = current && bangumi?.Media.Key == _media.Parsed.Key ? bangumi.Subject : null;
        if (subject == null && _client != null && title.Length > 0)
        {
            var binding = current ? bangumi?.Store.FindBinding(_media.Parsed.Key) : null;
            var subjects = await _client.CatalogueAsync(binding?.SubjectId, title, _lifetime).ConfigureAwait(false);
            subject = binding != null ? subjects.FirstOrDefault(item => item.Id == binding.SubjectId)
                : current ? _media.Parsed.MatchSubject(subjects) : MatchTitle(title, subjects);
        }
        if (subject != null)
        {
            names.AddRange(SubjectNames(subject));
            lock (_lock) _subjects[title] = subject;
        }
        _lifetime.ThrowIfCancellationRequested();
        var query = Query(title, current, names);
        Remember(query, current);
        return query;
    }

    async Task CompleteNamesAsync(Subject subject, List<string> names, CancellationToken token, bool current)
    {
        if (_client == null || subject.Id is not > 0 || names.Any(name => Regex.IsMatch(name, @"[A-Za-z]")
            && !Regex.IsMatch(name, @"[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}]"))) return;
        Task<MalSubject?> pending;
        lock (_lock)
            if (!_translations.TryGetValue(subject.Id.Value, out pending!) || pending.IsFaulted || pending.IsCanceled)
                _translations[subject.Id.Value] = pending = MalMatching.MatchCatalogueAsync(_client, subject, _lifetime);
        try
        {
            if (await pending.WaitAsync(token).ConfigureAwait(false) is { } translated)
            {
                names.AddRange(translated.Names);
                if (current) lock (_lock) _currentMalId = translated.Id;
            }
        }
        catch
        {
            if (pending.IsCanceled || pending.IsFaulted)
                lock (_lock) if (_translations.GetValueOrDefault(subject.Id.Value) == pending) _translations.Remove(subject.Id.Value);
            throw;
        }
    }

    void Remember(SubtitleQuery query, bool current)
    {
        lock (_lock)
        {
            foreach (string name in query.Names)
            {
                if (current) _currentNames.Add(Name(name));
                if (!_queries.TryGetValue(Key(name), out var pending) || current && pending.IsCompletedSuccessfully)
                    _queries[Key(name)] = Task.FromResult(query);
            }
        }
    }

    IEnumerable<string> KnownNames()
    {
        yield return _media.Title;
        if (_media.Parsed.Ambiguous) yield break;
        if (BangumiPlayback.Current?.Store.FindBinding(_media.Parsed.Key) is { } bangumiBinding)
            yield return bangumiBinding.Title;
        if (MalPlayback.Current?.Store.FindBinding(_media.Parsed.Key) is { } malBinding && malBinding.Season == _media.Season)
            yield return malBinding.Title;
        if (TraktPlayback.Current?.Store.FindBinding(_media.Key) is { } traktBinding
            && traktBinding.Kind == _media.Kind && (_media.Kind == "movie" || traktBinding.Season == _media.Season))
            yield return traktBinding.Title;
        if (BangumiPlayback.Current is { Subject: { } bangumi } bgm && bgm.Media.Key == _media.Parsed.Key)
            foreach (string name in SubjectNames(bangumi)) yield return name;
        if (MalPlayback.Current is { Subject: { } mal } playback && playback.Media.Key == _media.Parsed.Key)
            foreach (string name in mal.Names) yield return name;
        if (TraktPlayback.Current is { Subject: { } trakt } sync && sync.Media.Key == _media.Key)
            yield return trakt.Title;
    }

    static IEnumerable<string> SubjectNames(Subject subject)
        => BangumiMedia.SubjectTitles(subject);

    SubtitleQuery Query(string title, bool current, IEnumerable<string> names) => new(title, null, current ? _media.Episode : null, "")
    {
        Names = names.Select(Name)
            .Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        SearchSeason = current && _media.Kind == "show" ? _media.Season : null,
        Year = current && _media.Kind == "movie" ? _media.Parsed.Year : null
    };

    static Subject? MatchTitle(string title, IReadOnlyList<Subject> subjects)
    {
        var parsed = BangumiMedia.Parse(title);
        var ranked = subjects.DistinctBy(subject => subject.Id)
            .Where(subject => parsed.Year == null || subject.Date?.StartsWith(parsed.Year + "-", StringComparison.Ordinal) == true)
            .Where(subject => !parsed.HasSeason || BangumiMedia.SubjectTitles(subject).Select(BangumiMedia.Parse)
                .Where(name => name.HasSeason).Select(name => name.Season).Distinct().SequenceEqual([parsed.Season]))
            .Select(subject => (Subject: subject, Score: BangumiMedia.SubjectTitles(subject)
                .Select(name => MalMatching.TitleScore(title, name)).DefaultIfEmpty().Max()))
            .Where(item => item.Score >= .8).OrderByDescending(item => item.Score).ToArray();
        return ranked.Length == 1 || ranked.Length > 1 && ranked[0].Score - ranked[1].Score >= .08 ? ranked[0].Subject : null;
    }
}

public sealed record SubtitleWork(TraktMedia Media, IReadOnlyList<string> Names, int? MalId)
{
    public (SubtitleQuery Query, bool Current) ForDownload(SubtitleQuery search, SubtitleResult result)
    {
        bool current = Compatible(result) && Matches(result);
        if (!current) return (search with { Episode = null }, false);
        int? season = Media.Kind == "movie" ? null : result.Individual ? Media.Season
            : (result.Collection ? null : result.Season) ?? search.Season ?? Media.Season;
        return (search with
        {
            Title = Media.Title,
            Names = Names.Concat(result.TitleNames).Append(result.Title).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Season = season,
            Episode = Media.Kind == "show" && season == Media.Season ? Media.Episode : null,
            Year = Media.Kind == "movie" ? Media.Parsed.Year : null
        }, true);
    }

    bool Compatible(SubtitleResult result)
    {
        if (Media.Parsed.Ambiguous || result.IsMovie is { } movie && movie != (Media.Kind == "movie")) return false;
        if (MalId is > 0 && result.MalId is > 0 && MalId != result.MalId) return false;
        if (Media.Kind == "movie")
        {
            if (result.Season is > 0 || result.Episode != null) return false;
            var wanted = MatchingNames();
            var matched = DisplayTitles(result).Where(title => wanted.Contains(TraktMedia.Normalize(title.Title))).ToArray();
            if (matched.Any(title => title.HasSeason || title.EpisodeNumber != null)) return false;
            if (Media.Parsed.Year is { } year)
            {
                if (result.Year is { } resultYear && year != resultYear) return false;
                if (matched.Any(title => title.Year is { } titleYear && titleYear != year)) return false;
            }
        }
        return true;
    }

    bool Matches(SubtitleResult result)
    {
        if (MalId is > 0 && MalId == result.MalId) return true;
        // A search term may be shortened or belong to another work. Only the selected
        // release's actual titles are compared to the current work's verified names.
        var wanted = MatchingNames();
        var files = result.PackageNames.Concat(result.Files.Select(file => file.Name))
            .Select(name => BangumiMedia.FromPlayback(name, name));
        return DisplayTitles(result).Concat(files).Any(parsed => !parsed.Ambiguous
            && (Media.Kind != "movie" || !parsed.HasSeason && parsed.EpisodeNumber == null
                && (Media.Parsed.Year == null || parsed.Year == null || Media.Parsed.Year == parsed.Year))
            && wanted.Contains(TraktMedia.Normalize(parsed.Title)));
    }

    HashSet<string> MatchingNames() => Names.Append(Media.Title).SelectMany(BangumiMedia.TitleVariants)
        .Select(name => TraktMedia.Normalize(BangumiMedia.Parse(name).Title)).Where(name => name.Length > 0).ToHashSet();

    static IEnumerable<BangumiMedia> DisplayTitles(SubtitleResult result)
    {
        foreach (string name in result.TitleNames.Append(result.Title).SelectMany(BangumiMedia.TitleVariants)
            .SelectMany(name => new[] { name }.Concat(name.Split('/'))).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct())
        {
            // Display labels may join aliases with slashes; they are not file paths.
            var parsed = BangumiMedia.Parse(name);
            yield return parsed;
            var elements = AnitomySharp.AnitomySharp.Parse(name, new(episode: false, title: false, extension: false)).ToArray();
            int? year = int.TryParse(elements.FirstOrDefault(element => element.Category == AnitomySharp.Element.ElementCategory.ElementAnimeYear)?.Value,
                out int number) ? number : null;
            foreach (var element in elements.Where(element => element.Category == AnitomySharp.Element.ElementCategory.ElementAnimeTitle))
                yield return BangumiMedia.Parse(element.Value) with
                { Year = parsed.Year ?? year, HasSeason = parsed.HasSeason, EpisodeNumber = parsed.EpisodeNumber };
        }
    }
}

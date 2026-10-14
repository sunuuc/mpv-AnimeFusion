using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MpvNet.Windows.Bangumi;
using MpvNet.Windows.Trakt;

namespace MpvNet.Windows.Subtitles;

public sealed record LocalSubtitle(int FileId, string Name, string Path, double? Episode, string Variant);
public sealed record SubtitlePackage(SubtitleSource Source, string Id, string Title, string Language, string LanguageCode,
    IReadOnlyList<LocalSubtitle> Files, bool Complete)
{
    public bool Collection { get; init; }
    public string Details { get; init; } = "";
    public string Format { get; init; } = "";
}
public sealed record SubtitleShelf(string Title, int? Season, IReadOnlyList<SubtitlePackage> Packages,
    string PreferredPackage = "", string PreferredVariant = "");
public sealed record SubtitleTrack(string Path, string Title, string Language, bool Preferred);

public sealed class SubtitleLibrary
{
    static readonly object Gate = new();
    public string Root { get; }
    public SubtitleLibrary(string root)
    { Root = System.IO.Path.GetFullPath(root); Directory.CreateDirectory(Root); }
    public string Error { get; private set; } = "";
    public static string PackageKey(SubtitleSource source, string id) => source + "-" + id;

    static string Name(string value)
    {
        string name = Regex.Replace(value, "[<>:\"/\\\\|?*\\x00-\\x1f]", "_").Trim(' ', '.');
        if (name.Length == 0) name = "未命名";
        if (Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)) name = "_" + name;
        return name;
    }
    public string SeasonDirectory(string title, int? season) => System.IO.Path.Combine(Root, Name(title.Normalize(NormalizationForm.FormKC)),
        season is > 0 ? $"第 {season} 季" : season == 0 ? "特别篇" : season < 0 ? "未分类" : "电影");
    public string PackageDirectory(SubtitleQuery query, SubtitleResult result) => System.IO.Path.Combine(
        SeasonDirectory(query.Title, query.Season), Name(PackageKey(result.Source, result.Id)));

    public string ArchivePath(SubtitleQuery query, SubtitleResult result) => System.IO.Path.Combine(Root,
        Name(query.Title.Normalize(NormalizationForm.FormKC)), "字幕包", Name(PackageKey(result.Source, result.Id)), Name(System.IO.Path.GetFileName(result.DownloadName)));

    public string FilePath(SubtitleQuery query, SubtitleResult result, string filename)
    {
        var parts = filename.Replace('\\', '/').Split('/').Where(part => part.Length > 0 && part != ".").ToArray();
        if (parts.Length == 0 || parts.Any(part => part == "..")) throw new SubtitleServiceException("字幕文件名无效。");
        string directory = PackageDirectory(FileQuery(query, result, filename), result);
        string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(new[] { directory }.Concat(parts.Select(Name)).ToArray()));
        if (!path.StartsWith(directory + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new SubtitleServiceException("字幕文件名无效。");
        return path;
    }

    SubtitleShelf Read(string directory, string title, int? season)
    {
        string path = System.IO.Path.Combine(directory, "subtitles.json");
        if (!File.Exists(path)) return new(title, season, []);
        try { return JsonSerializer.Deserialize<SubtitleShelf>(File.ReadAllBytes(path)) ?? throw new JsonException(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { Error = "本地字幕记录无法读取。"; return new(title, season, []); }
    }
    void Write(string directory, SubtitleShelf shelf)
    {
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "subtitles.json");
        File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(shelf, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
    bool Exists(LocalSubtitle file) => System.IO.Path.GetFullPath(file.Path).StartsWith(Root + System.IO.Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase) && File.Exists(file.Path) && new FileInfo(file.Path).Length > 0;

    IEnumerable<(string Directory, SubtitleShelf Shelf)> Shelves(string title)
    {
        string directory = System.IO.Path.Combine(Root, Name(title.Normalize(NormalizationForm.FormKC)));
        return Directory.Exists(directory) ? Directory.GetDirectories(directory).Select(path => (path, Read(path, title, null))) : [];
    }

    public SubtitlePackage? Package(SubtitleQuery query, SubtitleResult result)
    {
        lock (Gate)
        {
            var packages = Shelves(query.Title).SelectMany(item => item.Shelf.Packages)
                .Where(package => package.Source == result.Source && package.Id == result.Id).ToArray();
            return packages.Length == 0 ? null : packages[0] with
                { Files = packages.SelectMany(package => package.Files).ToArray(), Complete = packages.All(package => package.Complete) };
        }
    }
    public string? Cached(SubtitleQuery query, SubtitleResult result, SubtitleFile file)
    {
        var package = Package(query, result);
        return package?.Files.FirstOrDefault(local => (file.FileId > 0 ? local.FileId == file.FileId : local.Name == file.Name) && Exists(local))?.Path;
    }
    public void Save(SubtitleQuery query, SubtitleResult result, SubtitleFile file, string path, bool complete, bool selected = false)
    {
        query = FileQuery(query, result, file.Name);
        var parsed = BangumiMedia.FromPlayback(file.Name, file.Name);
        double? episode = query.Season == null ? null : parsed.Ambiguous ? selected ? query.Episode : null : parsed.EpisodeNumber ?? (selected ? query.Episode : null);
        if (query.Season < 0) episode = null;
        var local = new LocalSubtitle(file.FileId, file.Name, System.IO.Path.GetFullPath(path), episode, Variant(file.Name, result.LanguageCode));
        lock (Gate)
        {
            string directory = SeasonDirectory(query.Title, query.Season);
            var shelf = Read(directory, query.Title, query.Season);
            var old = shelf.Packages.FirstOrDefault(package => package.Source == result.Source && package.Id == result.Id);
            var files = (old?.Files ?? []).Where(item => file.FileId > 0 ? item.FileId != file.FileId : item.Name != file.Name).Append(local).ToArray();
            var package = new SubtitlePackage(result.Source, result.Id, result.Title, result.Language, result.LanguageCode, files, complete)
                { Collection = result.Collection, Details = result.Details, Format = result.Format };
            Write(directory, shelf with { Packages = shelf.Packages.Where(item => item.Source != result.Source || item.Id != result.Id).Append(package).ToArray() });
        }
    }
    public void Complete(SubtitleQuery query, SubtitleResult result)
    {
        lock (Gate)
        {
            var shelves = Shelves(query.Title);
            foreach (var (directory, shelf) in shelves)
                if (shelf.Packages.Any(item => item.Source == result.Source && item.Id == result.Id && !item.Complete))
                    Write(directory, shelf with { Packages = shelf.Packages.Select(item => item.Source == result.Source && item.Id == result.Id ? item with { Complete = true } : item).ToArray() });
        }
    }
    public void Remember(string title, int? season, string path)
    {
        lock (Gate)
        {
            string directory = SeasonDirectory(title, season);
            var shelf = Read(directory, title, season);
            foreach (var package in shelf.Packages)
            {
                var selected = package.Files.FirstOrDefault(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && Exists(file));
                if (selected == null) continue;
                Write(directory, shelf with { PreferredPackage = PackageKey(package.Source, package.Id), PreferredVariant = selected.Variant });
                return;
            }
        }
    }
    static string Variant(string filename, string language)
    {
        var match = Regex.Match(filename, @"(?:^|[.\[\] _-])(chs|cht|sc|tc|zh-cn|zh-tw|简|繁)(?=[.\[\] _-]|$)", RegexOptions.IgnoreCase);
        return (match.Success ? match.Groups[1].Value.ToLowerInvariant() : language) + System.IO.Path.GetExtension(filename).ToLowerInvariant();
    }
    public SubtitleFile? CurrentFile(SubtitleQuery query, SubtitleResult result, IReadOnlyList<SubtitleFile> files)
    {
        string preferred;
        lock (Gate) preferred = Read(SeasonDirectory(query.Title, query.Season), query.Title, query.Season).PreferredVariant;
        string directory = PackageDirectory(query, result) + System.IO.Path.DirectorySeparatorChar;
        result = result with { PackageNames = files.Select(file => file.Name).ToArray() };
        files = files.Where(file => file.LocalPath.Length > 0 ? file.LocalPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase)
            : !result.Collection || FileQuery(query, result, file.Name).Season == query.Season).ToArray();
        return files.Where(file =>
            {
                if (!query.MatchesFile(file.Name)) return false;
                if (query.Season == null) return true;
                if (query.Episode == null) return false;
                var parsed = BangumiMedia.FromPlayback(file.Name, file.Name);
                return !parsed.Ambiguous && parsed.EpisodeNumber == query.Episode
                    || files.Count == 1 && parsed.EpisodeNumber == null;
            })
            .OrderByDescending(file => preferred.Length > 0 && Variant(file.Name, result.LanguageCode) == preferred)
            .ThenByDescending(file => Regex.IsMatch(Variant(file.Name, result.LanguageCode), @"^(sc|chs|zh-cn|简)\.", RegexOptions.IgnoreCase))
            .FirstOrDefault();
    }
    static string FileTitle(string filename)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(filename);
        name = Regex.Replace(name, @"(?<=\])\.[^.]+$", "");
        return TraktMedia.Normalize(AnitomySharp.AnitomySharp.Parse(name, new(extension: false)).FirstOrDefault(element =>
            element.Category == AnitomySharp.Element.ElementCategory.ElementAnimeTitle)?.Value ?? "");
    }
    public static SubtitleQuery FileQuery(SubtitleQuery query, SubtitleResult result, string filename)
    {
        if (!result.Collection) return query;
        string[] Parts(string name) => name.Replace('\\', '/').Split('/');
        var names = result.PackageNames.Append(filename).Distinct().ToArray();
        var paths = names.Select(Parts).ToArray();
        var wanted = query.Names.Append(query.Title).SelectMany(BangumiMedia.TitleVariants)
            .Select(name => TraktMedia.Normalize(BangumiMedia.Parse(name).Title)).Where(name => name.Length > 0).ToHashSet();
        var available = paths.SelectMany(parts => parts.Take(parts.Length - 1).Select(TraktMedia.Normalize))
            .Concat(names.Select(FileTitle)).Where(name => name.Length > 0).ToHashSet();
        bool Special(string value) => Regex.IsMatch(value, @"(?:^|[\s\[\]_.-])(?:OVA|OAD|SP|Specials?)(?:\d*\b|[\s\[\]_.-])|特典|特别篇|特別篇", RegexOptions.IgnoreCase);
        int? Numbered(string value)
        {
            var match = Regex.Match(TraktMedia.Normalize(value), @"^(.+?)([1-9]\d?)$");
            return match.Success && (wanted.Contains(match.Groups[1].Value) || available.Contains(match.Groups[1].Value))
                ? int.Parse(match.Groups[2].Value) : null;
        }
        bool First(string value) => value.Length > 0 && (wanted.Contains(value) || available.Any(name => name.StartsWith(value, StringComparison.Ordinal)
            && name.Length > value.Length && int.TryParse(name[value.Length..], out int number) && number is > 1 and <= 99));
        int? season = null;
        string leaf = System.IO.Path.GetFileName(filename.Replace('\\', '/'));
        var parsed = BangumiMedia.FromPlayback(leaf, leaf);
        if (Special(leaf)) season = 0;
        else if (parsed.HasSeason) season = parsed.Season;
        else
        {
            foreach (string folder in Parts(filename).SkipLast(1).Reverse())
            {
                if (paths.All(parts => parts.SkipLast(1).Contains(folder, StringComparer.OrdinalIgnoreCase))) continue;
                var media = BangumiMedia.Parse(folder);
                if (Special(folder)) season = 0;
                else if (media.HasSeason) season = media.Season;
                else season = Numbered(folder) ?? (First(TraktMedia.Normalize(folder)) ? 1 : null);
                if (season != null) break;
            }
            season ??= Numbered(FileTitle(filename)) ?? (First(FileTitle(filename)) ? 1 : null);
        }
        return query with { Season = season ?? -1, Episode = season == query.Season ? query.Episode : null };
    }
    public IReadOnlyList<SubtitleResult> Results(string title)
    {
        lock (Gate) return Shelves(title).SelectMany(item => item.Shelf.Packages.Where(package => package.Complete)
            .Select(package => (Shelf: item.Shelf, Package: package)))
            .GroupBy(item => (item.Package.Source, item.Package.Id)).Select(group =>
            {
                var package = group.First().Package;
                var seasons = group.Select(item => item.Shelf.Season).Distinct().ToArray();
                return new SubtitleResult(package.Source, package.Id,
                package.Title, package.Language, package.Details, group.SelectMany(item => item.Package.Files.Where(Exists))
                    .Select(file => new SubtitleFile(package.Source, package.Id, file.FileId, file.Name, "") { LocalPath = file.Path }).ToArray())
                { Local = true, Format = package.Format, Season = seasons.Length == 1 ? seasons[0] : null, AvailableSeasons = seasons,
                    LanguageCode = package.LanguageCode, Collection = package.Collection,
                    PackageNames = group.SelectMany(item => item.Package.Files).Select(file => file.Name).ToArray() };
            }).Where(result => result.Files.Count > 0).ToArray();
    }
    public IReadOnlyList<SubtitleTrack> Tracks(TraktMedia media)
    {
        if (media.Parsed.Ambiguous) return [];
        int? season = media.Kind == "show" ? media.Season : null;
        lock (Gate)
        {
            var shelf = Read(SeasonDirectory(media.Title, season), media.Title, season);
            return shelf.Packages.SelectMany(package => package.Files.Where(file => Exists(file) && file.Episode == media.Parsed.EpisodeNumber
                    && !(System.IO.Path.GetExtension(file.Name).Equals(".sub", StringComparison.OrdinalIgnoreCase)
                        && package.Files.Any(pair => System.IO.Path.ChangeExtension(file.Name, ".idx").Equals(pair.Name, StringComparison.OrdinalIgnoreCase))))
                .Select(file => new SubtitleTrack(file.Path, System.IO.Path.GetFileName(file.Name.Replace('\\', '/')), package.LanguageCode,
                    shelf.PreferredPackage == PackageKey(package.Source, package.Id) && shelf.PreferredVariant == file.Variant)))
                .OrderByDescending(track => track.Preferred).ToArray();
        }
    }
}

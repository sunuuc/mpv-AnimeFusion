using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using MpvNet.Windows.Bangumi;
using MpvNet.Windows.Trakt;

namespace MpvNet.Windows.Subtitles;

public sealed record LocalSubtitle(int FileId, string Name, string Path, double? Episode, string Variant)
{
    public string OriginalPath { get; init; } = "";
    public string OriginalHash { get; init; } = "";
}
public sealed record SubtitlePackage(SubtitleSource Source, string Id, string Title, string Language, string LanguageCode,
    IReadOnlyList<LocalSubtitle> Files, bool Complete)
{
    public bool Collection { get; init; }
    public string Details { get; init; } = "";
    public string Format { get; init; } = "";
    public bool Imported { get; init; }
    public bool Individual { get; init; }
}
public sealed record SubtitleShelf(string Title, int? Season, IReadOnlyList<SubtitlePackage> Packages,
    string PreferredPackage = "", string PreferredVariant = "");
public sealed record SubtitleTrack(string Path, string Title, string Language, bool Preferred, bool Pinned = false);
public sealed record SubtitleSelection(string Package = "", string Variant = "");

public sealed class SubtitleLibrary
{
    static readonly object Gate = new();
    public string Root { get; }
    public SubtitleLibrary(string root)
    { Root = System.IO.Path.GetFullPath(root); Directory.CreateDirectory(Root); }
    public string Error { get; private set; } = "";
    public static string PackageKey(SubtitleSource source, string id) => source + "-" + id;
    string WorkDirectory(string title) => System.IO.Path.Combine(Root, Name(title.Normalize(NormalizationForm.FormKC)));

    static string Name(string value)
    {
        string name = Regex.Replace(value, "[<>:\"/\\\\|?*\\x00-\\x1f]", "_").Trim(' ', '.');
        if (name.Length == 0) name = "未命名";
        if (Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)) name = "_" + name;
        return name;
    }
    public string SeasonDirectory(string title, int? season) => System.IO.Path.Combine(WorkDirectory(title),
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
    SubtitleSelection Selection(string title)
    {
        string path = System.IO.Path.Combine(WorkDirectory(title), "selection.json");
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<SubtitleSelection>(File.ReadAllBytes(path)) ?? throw new JsonException(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { Error = "强制使用字幕的记录无法读取。"; return new(); }
    }
    void WriteSelection(string title, SubtitleSelection selection)
    {
        string directory = WorkDirectory(title);
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "selection.json");
        File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(selection, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
    bool Exists(LocalSubtitle file) => System.IO.Path.GetFullPath(file.Path).StartsWith(Root + System.IO.Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase) && File.Exists(file.Path) && new FileInfo(file.Path).Length > 0;

    IEnumerable<(string Directory, SubtitleShelf Shelf)> Shelves(string title)
    {
        string directory = WorkDirectory(title);
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
    public void Save(SubtitleQuery query, SubtitleResult result, SubtitleFile file, string path, bool complete, bool selected = false, bool imported = false,
        string originalPath = "", string originalHash = "")
    {
        query = FileQuery(query, result, file.Name);
        var parsed = BangumiMedia.FromPlayback(file.Name, file.Name);
        double? episode = query.Season == null ? null : result.Individual ? result.Episode
            : parsed.Ambiguous ? selected ? query.Episode : null : parsed.EpisodeNumber ?? (selected ? query.Episode : null);
        if (query.Season < 0) episode = null;
        var local = new LocalSubtitle(file.FileId, file.Name, System.IO.Path.GetFullPath(path), episode, Variant(file.Name, result.LanguageCode, imported))
            { OriginalPath = originalPath, OriginalHash = originalHash };
        lock (Gate)
        {
            string directory = SeasonDirectory(query.Title, query.Season);
            var shelf = Read(directory, query.Title, query.Season);
            var old = shelf.Packages.FirstOrDefault(package => package.Source == result.Source && package.Id == result.Id);
            var files = (old?.Files ?? []).Where(item => file.FileId > 0 ? item.FileId != file.FileId : item.Name != file.Name).Append(local).ToArray();
            var package = new SubtitlePackage(result.Source, result.Id, result.Title, result.Language, result.LanguageCode, files, complete)
                { Collection = result.Collection, Details = result.Details, Format = result.Format, Imported = imported || old?.Imported == true,
                    Individual = result.Individual };
            Write(directory, shelf with { Packages = shelf.Packages.Where(item => item.Source != result.Source || item.Id != result.Id).Append(package).ToArray() });
        }
    }
    public bool IsManaged(string title, string path)
    {
        lock (Gate) return Shelves(title).Any(item => item.Shelf.Packages.Any(package => package.Files.Any(file =>
            file.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && Exists(file))));
    }
    static string ImportFamily(string path)
    {
        string filename = System.IO.Path.GetFileName(path);
        var parsed = BangumiMedia.FromPlayback(filename, filename);
        string release = string.Join('|', AnitomySharp.AnitomySharp.Parse(filename)
            .Where(element => element.Category is AnitomySharp.Element.ElementCategory.ElementReleaseGroup
                or AnitomySharp.Element.ElementCategory.ElementReleaseVersion or AnitomySharp.Element.ElementCategory.ElementSource
                or AnitomySharp.Element.ElementCategory.ElementVideoResolution or AnitomySharp.Element.ElementCategory.ElementVideoTerm
                or AnitomySharp.Element.ElementCategory.ElementAudioTerm)
            .Select(element => element.Category + "=" + element.Value.ToLowerInvariant()).OrderBy(value => value));
        return TraktMedia.Normalize(parsed.Title) + "|" + release;
    }
    static string ImportId(string path)
    {
        if (System.IO.Path.GetExtension(path).Equals(".sub", StringComparison.OrdinalIgnoreCase)) path = System.IO.Path.ChangeExtension(path, ".idx");
        return "local-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            System.IO.Path.GetDirectoryName(path)!.ToUpperInvariant() + "|" + ImportFamily(path))))[..24];
    }
    static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    public string Import(TraktMedia media, string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (media.Title.Length == 0 || media.Parsed.Ambiguous) throw new SubtitleServiceException("无法确定当前作品。");
        if (!SubtitleDownload.Supported(path) || !File.Exists(path) || new FileInfo(path).Length == 0)
            throw new SubtitleServiceException("字幕文件无法读取。");
        if (System.IO.Path.GetExtension(path).Equals(".sub", StringComparison.OrdinalIgnoreCase)
            && File.Exists(System.IO.Path.ChangeExtension(path, ".idx"))) path = System.IO.Path.ChangeExtension(path, ".idx");
        lock (Gate)
        {
            if (Tracks(media).Any(track => track.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return path;
            var selectedMedia = BangumiMedia.FromPlayback(System.IO.Path.GetFileName(path), path);
            if (media.Kind == "show" && (media.Parsed.EpisodeNumber == null || selectedMedia.Ambiguous
                || selectedMedia.HasSeason && selectedMedia.Season != media.Season
                || selectedMedia.EpisodeNumber != null && selectedMedia.EpisodeNumber != media.Parsed.EpisodeNumber))
                throw new SubtitleServiceException("这份字幕与当前剧集不匹配。");
            bool stored = path.StartsWith(Root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            var existing = Shelves(media.Title).SelectMany(item => item.Shelf.Packages)
                .FirstOrDefault(package => package.Files.Any(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase)));
            string sourceDirectory = System.IO.Path.GetDirectoryName(path)!;
            string family = ImportFamily(path);
            string id = existing?.Id ?? ImportId(path);
            var result = new SubtitleResult(existing?.Source ?? SubtitleSource.Assrt, id, existing?.Title ?? System.IO.Path.GetFileName(sourceDirectory),
                existing?.Language ?? "", existing?.Details ?? "", []) { Local = true, LanguageCode = existing?.LanguageCode ?? "" };
            var candidates = new List<string> { path };
            foreach (string candidate in stored ? [] : Directory.EnumerateFiles(sourceDirectory))
            {
                if (candidate.Equals(path, StringComparison.OrdinalIgnoreCase) || !SubtitleDownload.Supported(candidate)
                    || ImportFamily(candidate) != family || new FileInfo(candidate).Length == 0) continue;
                var parsed = BangumiMedia.FromPlayback(System.IO.Path.GetFileName(candidate), candidate);
                if (parsed.Ambiguous || (media.Kind == "show" ? parsed.EpisodeNumber == null || selectedMedia.EpisodeNumber == null
                    : parsed.EpisodeNumber != null || parsed.HasSeason)) continue;
                candidates.Add(candidate);
            }
            // IDX and SUB form one subtitle even when the name has no episode token.
            string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (extension is ".idx" or ".sub")
            {
                string pair = System.IO.Path.ChangeExtension(path, extension == ".idx" ? ".sub" : ".idx");
                if (File.Exists(pair) && new FileInfo(pair).Length > 0 && !candidates.Contains(pair, StringComparer.OrdinalIgnoreCase)) candidates.Add(pair);
                if (extension == ".idx" && !candidates.Contains(pair, StringComparer.OrdinalIgnoreCase))
                    throw new SubtitleServiceException("这份 IDX 字幕缺少对应的 SUB 文件。");
            }
            if (existing == null)
            {
                var languages = candidates.Select(FileLanguage).Where(language => language.Length > 0).Distinct().ToArray();
                result = result with { LanguageCode = string.Join(',', languages),
                    Language = string.Join(" / ", languages.Select(code => SubtitleLanguage.Find(code)?.Name ?? code)) };
            }
            string selected = "";
            foreach (string candidate in candidates)
            {
                string filename = System.IO.Path.GetFileName(candidate);
                var parsed = BangumiMedia.FromPlayback(filename, filename);
                int? season = media.Kind == "show" ? parsed.HasSeason ? parsed.Season : media.Season : null;
                var query = new SubtitleQuery(media.Title, season, media.Episode, "");
                string target = FilePath(query, result, filename);
                if (!target.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                    File.Copy(candidate, target + ".tmp", true);
                    File.Move(target + ".tmp", target, true);
                }
                Save(query, result with { Format = System.IO.Path.GetExtension(filename).TrimStart('.').ToUpperInvariant() },
                    new(result.Source, id, 0, filename, ""), target, true,
                    selected: candidate.Equals(path, StringComparison.OrdinalIgnoreCase)
                        || System.IO.Path.ChangeExtension(candidate, extension).Equals(path, StringComparison.OrdinalIgnoreCase), imported: existing?.Imported ?? true,
                    originalPath: candidate, originalHash: FileHash(target));
                if (candidate.Equals(path, StringComparison.OrdinalIgnoreCase)) selected = target;
            }
            return selected;
        }
    }
    public void SetPinned(string title, string path, bool pinned)
    {
        lock (Gate)
        {
            foreach (var (_, shelf) in Shelves(title))
                foreach (var package in shelf.Packages)
                {
                    var file = package.Files.FirstOrDefault(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && Exists(file));
                    if (file == null) continue;
                    var selection = Selection(title);
                    string key = PackageKey(package.Source, package.Id);
                    string variant = Variant(file.Name, package.LanguageCode, true);
                    if (pinned) WriteSelection(title, new(key, variant));
                    else if (selection.Package == key && selection.Variant == variant) WriteSelection(title, new());
                    return;
                }
        }
    }
    bool CanDelete(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (!full.StartsWith(Root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        return RegularPath(full);
    }
    static bool RegularPath(string path)
    {
        for (string? current = path; current != null; current = System.IO.Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
    public IReadOnlyList<string> Remove(string title, string path, bool deleteLocal)
    {
        lock (Gate)
        {
            var shelves = Shelves(title).ToArray();
            var package = shelves.SelectMany(item => item.Shelf.Packages)
                .FirstOrDefault(package => package.Files.Any(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase)));
            if (package == null) return [];
            string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
            string pair = extension is ".idx" or ".sub" ? System.IO.Path.ChangeExtension(path, extension == ".idx" ? ".sub" : ".idx") : "";
            var removed = package.Files.Where(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase)
                || pair.Length > 0 && file.Path.Equals(pair, StringComparison.OrdinalIgnoreCase)).Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<string>? deletedFiles = null;
            if (deleteLocal)
            {
                var indexed = shelves.SelectMany(item => item.Shelf.Packages.SelectMany(package => package.Files.Select(file => (Package: package, File: file)))).ToArray();
                var pending = new Queue<(SubtitlePackage Package, LocalSubtitle File)>(indexed.Where(item => removed.Contains(item.File.Path)));
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var physical = new HashSet<string>(removed, StringComparer.OrdinalIgnoreCase);
                var packages = new HashSet<string>();
                while (pending.TryDequeue(out var item))
                {
                    var file = item.File;
                    if (!visited.Add(file.Path)) continue;
                    if (!CanDelete(file.Path) || !SubtitleDownload.Supported(file.Path))
                        throw new SubtitleServiceException("字幕文件路径无效，未删除本地文件。");
                    packages.Add(PackageKey(item.Package.Source, item.Package.Id));
                    if (file.OriginalPath.Length == 0)
                    {
                        if (item.Package.Imported) throw new SubtitleServiceException("无法确认导入字幕的原始文件，未删除本地文件。");
                        continue;
                    }
                    string original = System.IO.Path.GetFullPath(file.OriginalPath);
                    bool stored = original.StartsWith(Root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                    if (!SubtitleDownload.Supported(original) || !RegularPath(original)
                        || !System.IO.Path.GetFileName(original).Equals(System.IO.Path.GetFileName(file.Name.Replace('\\', '/')), StringComparison.OrdinalIgnoreCase)
                        || !Regex.IsMatch(file.OriginalHash, "^[A-Fa-f0-9]{64}$")
                        || (stored ? !original.StartsWith(WorkDirectory(title) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            : !item.Package.Imported || item.Package.Id != ImportId(original)))
                        throw new SubtitleServiceException("原始字幕路径无法核实，未删除本地文件。");
                    if (File.Exists(original) && !FileHash(original).Equals(file.OriginalHash, StringComparison.OrdinalIgnoreCase))
                        throw new SubtitleServiceException("原始字幕已更改，未删除：" + System.IO.Path.GetFileName(original));
                    physical.Add(original);
                    foreach (var source in indexed.Where(source => source.File.Path.Equals(original, StringComparison.OrdinalIgnoreCase)))
                    {
                        removed.Add(source.File.Path);
                        pending.Enqueue(source);
                    }
                }
                foreach (string key in packages)
                {
                    string directory = System.IO.Path.Combine(WorkDirectory(title), "字幕包", Name(key));
                    if (!Directory.Exists(directory)) continue;
                    if (!CanDelete(directory)) throw new SubtitleServiceException("字幕包路径无效，未删除本地文件。");
                    foreach (string archive in Directory.EnumerateFiles(directory).Where(SubtitleDownload.PackageSupported))
                    {
                        if (!CanDelete(archive)) throw new SubtitleServiceException("字幕包路径无效，未删除本地文件。");
                        physical.Add(archive);
                    }
                }
                bool shared = Directory.GetDirectories(Root).Any(work => Directory.GetDirectories(work)
                    .Select(directory => Read(directory, "", null)).Any(shelf => shelf.Packages.Any(item => item.Files.Any(file =>
                        (!work.Equals(WorkDirectory(title), StringComparison.OrdinalIgnoreCase) || !removed.Contains(file.Path))
                        && (physical.Contains(file.Path) || physical.Contains(file.OriginalPath))))));
                if (shared) throw new SubtitleServiceException("字幕文件仍被其他字幕或作品使用，未删除本地文件。");
                foreach (string file in physical)
                    if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReadOnly) != 0)
                        throw new SubtitleServiceException("文件为只读，未删除：" + System.IO.Path.GetFileName(file));
                foreach (string file in physical) File.Delete(file);
                deletedFiles = physical.Where(SubtitleDownload.Supported).ToArray();
            }
            foreach (var (directory, shelf) in shelves)
            {
                if (!shelf.Packages.Any(item => item.Files.Any(file => removed.Contains(file.Path)))) continue;
                var packages = shelf.Packages.Select(item => item with { Files = item.Files.Where(file => !removed.Contains(file.Path)).ToArray() })
                    .Where(item => item.Files.Count > 0).ToArray();
                bool preferred = packages.Any(item => PackageKey(item.Source, item.Id) == shelf.PreferredPackage
                    && item.Files.Any(file => file.Variant == shelf.PreferredVariant));
                Write(directory, shelf with { Packages = packages, PreferredPackage = preferred ? shelf.PreferredPackage : "", PreferredVariant = preferred ? shelf.PreferredVariant : "" });
            }
            var selection = Selection(title);
            if (selection.Package.Length > 0 && !Shelves(title).Any(item => item.Shelf.Packages.Any(item => PackageKey(item.Source, item.Id) == selection.Package
                && item.Files.Any(file => Variant(file.Name, item.LanguageCode, true) == selection.Variant && Exists(file))))) WriteSelection(title, new());
            return deletedFiles ?? removed.ToArray();
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
    static string LanguageMarker(string filename)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(filename);
        string Marker(string value)
        {
            value = value.Trim().ToLowerInvariant();
            if (value is "chs" or "cht" or "sc" or "tc" or "zh-cn" or "zh-tw" or "zh-hans" or "zh-hant" or "简" or "繁") return value;
            return SubtitleLanguage.Find(value.Split('-')[0]) is { } language
                ? value.Contains('-') ? value : language.Code : "";
        }
        string suffix = name.Split('.').Skip(1).Select(Marker).LastOrDefault(value => value.Length > 0) ?? "";
        if (suffix.Length > 0) return suffix;
        string bracket = Regex.Matches(name, @"[\[(]([^\]\)]+)[\])]").Select(match => Marker(match.Groups[1].Value))
            .LastOrDefault(value => value.Length > 0) ?? "";
        if (bracket.Length > 0) return bracket;
        foreach (var element in AnitomySharp.AnitomySharp.Parse(filename).Where(element => element.Category == AnitomySharp.Element.ElementCategory.ElementLanguage))
        {
            string marker = Marker(element.Value);
            if (marker.Length > 0) return marker;
            var culture = System.Globalization.CultureInfo.GetCultures(System.Globalization.CultureTypes.NeutralCultures)
                .FirstOrDefault(culture => culture.EnglishName.Equals(element.Value, StringComparison.OrdinalIgnoreCase));
            if (culture != null) return culture.TwoLetterISOLanguageName;
        }
        return "";
    }
    internal static string FileLanguage(string filename)
    {
        string marker = LanguageMarker(filename);
        return marker switch
        {
            "简" or "sc" => "zh-Hans",
            "繁" or "tc" => "zh-Hant",
            _ => SubtitleLanguage.Find(marker)?.Code ?? SubtitleLanguage.Find(marker.Split('-')[0])?.Code ?? ""
        };
    }
    static string Variant(string filename, string language, bool includeEdition = false)
    {
        string marker = LanguageMarker(filename);
        string variant = (marker.Length > 0 ? marker : language) + System.IO.Path.GetExtension(filename).ToLowerInvariant();
        if (!includeEdition) return variant;
        // Episode titles, seasons and checksums are deliberately absent from the
        // set identity; explicit edition tags remain alongside parsed release data.
        string name = System.IO.Path.GetFileNameWithoutExtension(filename).Normalize(NormalizationForm.FormKC);
        var elements = AnitomySharp.AnitomySharp.Parse(filename).ToArray();
        var edition = elements.Where(element => element.Category is AnitomySharp.Element.ElementCategory.ElementReleaseGroup
            or AnitomySharp.Element.ElementCategory.ElementReleaseVersion or AnitomySharp.Element.ElementCategory.ElementSource
            or AnitomySharp.Element.ElementCategory.ElementVideoResolution or AnitomySharp.Element.ElementCategory.ElementVideoTerm
            or AnitomySharp.Element.ElementCategory.ElementAudioTerm or AnitomySharp.Element.ElementCategory.ElementSubtitles
            or AnitomySharp.Element.ElementCategory.ElementOther or AnitomySharp.Element.ElementCategory.ElementReleaseInformation)
            .Select(element => element.Category + "=" + TraktMedia.Normalize(element.Value));
        var labels = Regex.Matches(name, @"(?:^|\.|\[)(full|signs|forced|sdh|cc|commentary|dialogue|dialog|retail|theatrical|extended|dubtitles|(?:v|ver|version|rev|edition)[._ -]?[a-z0-9]+)(?=$|\.|\])", RegexOptions.IgnoreCase)
            .Select(match => "edition=" + TraktMedia.Normalize(match.Groups[1].Value));
        return variant + "|" + string.Join('|', edition.Concat(labels).Distinct().OrderBy(value => value));
    }
    public SubtitleFile? CurrentFile(SubtitleQuery query, SubtitleResult result, IReadOnlyList<SubtitleFile> files)
    {
        if (result.Individual && (result.Season != query.Season || result.Episode != query.Episode || !IndividualMatches(query, result))) return null;
        string preferred;
        lock (Gate) preferred = Read(SeasonDirectory(query.Title, query.Season), query.Title, query.Season).PreferredVariant;
        bool imported = Package(query, result)?.Imported == true;
        string directory = PackageDirectory(query, result) + System.IO.Path.DirectorySeparatorChar;
        result = result with { PackageNames = files.Select(file => file.Name).ToArray() };
        files = files.Where(file => file.LocalPath.Length > 0 ? file.LocalPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase)
            : !result.Collection || FileQuery(query, result, file.Name).Season == query.Season).ToArray();
        return files.Where(file =>
            {
                if (!result.Individual && !query.MatchesFile(file.Name)) return false;
                string language = FileLanguage(file.Name);
                if (query.Language.Length > 0)
                {
                    var languages = language.Length > 0 ? new[] { SubtitleLanguage.Find(language)! } : result.Languages;
                    if (languages.Count != 1 || !languages[0].Matches(query.Language)) return false;
                }
                if (query.Season == null || result.Individual) return true;
                if (query.Episode == null) return false;
                var parsed = BangumiMedia.FromPlayback(file.Name, file.Name);
                return !parsed.Ambiguous && (parsed.EpisodeNumber == query.Episode
                    || files.Count == 1 && parsed.EpisodeNumber == null);
            })
            .OrderByDescending(file => query.Language.Length > 0 && FileLanguage(file.Name) == query.Language)
            .ThenByDescending(file => preferred.Length > 0 && Variant(file.Name, result.LanguageCode, imported) == preferred)
            .ThenByDescending(file => Regex.IsMatch(Variant(file.Name, result.LanguageCode), @"^(sc|chs|zh-cn|zh-hans|简)\.", RegexOptions.IgnoreCase))
            .FirstOrDefault();
    }
    static string FileTitle(string filename)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(filename);
        name = Regex.Replace(name, @"(?<=\])\.[^.]+$", "");
        return TraktMedia.Normalize(AnitomySharp.AnitomySharp.Parse(name, new(extension: false)).FirstOrDefault(element =>
            element.Category == AnitomySharp.Element.ElementCategory.ElementAnimeTitle)?.Value ?? "");
    }
    internal static bool IndividualMatches(SubtitleQuery query, SubtitleResult result)
    {
        if (result.Season < 0 || result.Episode < 0 || (result.Season == null) != (result.Episode == null)) return false;
        if (result.Season != null) return query.MatchesTitle([result.Title]);
        string name = result.DownloadName.EndsWith(".xz", StringComparison.OrdinalIgnoreCase) ? result.DownloadName[..^3] : result.DownloadName;
        string title = result.Title.Split(" / ", StringSplitOptions.None)[0];
        if (query.Year != null && new[] { title, name }.Where(value => value.Length > 0)
            .Select(value => BangumiMedia.FromPlayback(value, value).Year).Any(year => year != null && year != query.Year)) return false;
        return MovieFileMatches(query, title)
            || name.Length > 0 && MovieFileMatches(query, name);
    }
    static bool MovieFileMatches(SubtitleQuery query, string filename)
    {
        // A movie collection can include other films or remakes. Match a file's
        // actual title/alias rather than the fuzzy ranking used by search.
        string name = System.IO.Path.GetFileName(filename.Replace('\\', '/'));
        string marker = LanguageMarker(name);
        if (marker.Length > 0)
            name = Regex.Replace(System.IO.Path.GetFileNameWithoutExtension(name),
                @"(?:^|[._\s\[(-])" + Regex.Escape(marker) + @"[\])]?$", "", RegexOptions.IgnoreCase)
                + System.IO.Path.GetExtension(name);
        var parsed = BangumiMedia.FromPlayback(name, name);
        if (parsed.Ambiguous || parsed.HasSeason || parsed.EpisodeNumber != null) return false;
        if (query.Year != null && parsed.Year != null && query.Year != parsed.Year) return false;
        string title = FileTitle(name);
        return query.Names.Append(query.Title).SelectMany(BangumiMedia.TitleVariants)
            .Select(value => TraktMedia.Normalize(BangumiMedia.Parse(value).Title))
            .Any(value => value.Length > 0 && (title == value || query.Year is int year && title == value + year));
    }
    public static SubtitleQuery FileQuery(SubtitleQuery query, SubtitleResult result, string filename)
    {
        if (!result.Collection) return query;
        string[] Parts(string name) => name.Replace('\\', '/').Split('/');
        if (query.Season == null)
            return query with { Season = MovieFileMatches(query, filename) ? null : -1, Episode = null };
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
                var files = group.SelectMany(item => item.Package.Files.Where(Exists)
                    .Select(file => new SubtitleFile(package.Source, package.Id, file.FileId, file.Name, "")
                        { LocalPath = file.Path, Season = item.Shelf.Season, Episode = file.Episode })).ToArray();
                var languages = files.Select(file => FileLanguage(file.Name)).ToArray();
                var codes = languages.Where(code => code.Length > 0)
                    .Concat(languages.Any(code => code.Length == 0) ? package.LanguageCode.Split(',', StringSplitOptions.RemoveEmptyEntries) : [])
                    .Distinct(StringComparer.Ordinal).ToArray();
                return new SubtitleResult(package.Source, package.Id,
                package.Title, package.Language, package.Details, files)
                { Local = true, Format = package.Format, Season = seasons.Length == 1 ? seasons[0] : null, AvailableSeasons = seasons,
                    TitleNames = group.Select(item => item.Shelf.Title).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    LanguageCode = string.Join(',', codes), Collection = package.Collection, Individual = package.Individual,
                    Episode = package.Individual && package.Files.FirstOrDefault()?.Episode is double episode
                        && episode is >= 0 and <= int.MaxValue && episode == Math.Truncate(episode) ? (int)episode : null,
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
            var pinned = Selection(media.Title);
            return shelf.Packages.SelectMany(package => package.Files.Where(file => Exists(file) && file.Episode == media.Parsed.EpisodeNumber
                    && !(System.IO.Path.GetExtension(file.Name).Equals(".sub", StringComparison.OrdinalIgnoreCase)
                        && package.Files.Any(pair => System.IO.Path.ChangeExtension(file.Name, ".idx").Equals(pair.Name, StringComparison.OrdinalIgnoreCase))))
                .Select(file => new SubtitleTrack(file.Path, System.IO.Path.GetFileName(file.Name.Replace('\\', '/')),
                    FileLanguage(file.Name) is { Length: > 0 } language ? language : package.LanguageCode,
                    shelf.PreferredPackage == PackageKey(package.Source, package.Id) && shelf.PreferredVariant == file.Variant,
                    pinned.Package == PackageKey(package.Source, package.Id) && pinned.Variant == Variant(file.Name, package.LanguageCode, true))))
                .OrderByDescending(track => track.Pinned).ThenByDescending(track => track.Preferred).ToArray();
        }
    }
}

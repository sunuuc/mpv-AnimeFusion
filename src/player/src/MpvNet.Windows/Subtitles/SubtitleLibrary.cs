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
        if (file.Source != result.Source || file.ResultId != result.Id) return null;
        lock (Gate)
        {
            var files = Shelves(query.Title).SelectMany(item => item.Shelf.Packages
                .Where(package => package.Source == result.Source && package.Id == result.Id)
                .SelectMany(package => package.Files.Select(local => (File: local, item.Shelf.Season)))).ToArray();
            var exact = files.Where(local => file.FileId > 0 && local.File.FileId == file.FileId).ToArray();
            if (exact.Length > 0) return exact.Length == 1 && Exists(exact[0].File) ? exact[0].File.Path : null;
            string? name = PackageFileName(files.Select(local => local.File.Name).ToArray(), result, file);
            var matching = files.Where(local => local.File.Name == name).ToArray();
            if (matching.Length != 1 || !Exists(matching[0].File)
                || file.FileId > 0 && matching[0].File.FileId > 0 && file.FileId != matching[0].File.FileId) return null;
            if (StringComparer.OrdinalIgnoreCase.Equals(file.Name.Replace('\\', '/'), name!.Replace('\\', '/')))
                return matching[0].File.Path;
            int? season = file.Season ?? FileQuery(query, result, file.Name).Season;
            return season >= 0 && season == matching[0].Season || season == null && matching[0].Season == null
                ? matching[0].File.Path : null;
        }
    }
    internal static string? PackageFileName(IReadOnlyList<string> names, SubtitleResult result, SubtitleFile file)
    {
        static string Relative(string name) => name.Replace('\\', '/');
        static string Leaf(string name) => System.IO.Path.GetFileName(Relative(name));
        bool SameName(string left, string right) => StringComparer.OrdinalIgnoreCase.Equals(left, right);
        if (result.Files.Where(item => SameName(Relative(item.Name), Relative(file.Name)))
            .Select(item => item.FileId).Distinct().Count() > 1) return null;
        var exact = names.Where(name => SameName(Relative(name), Relative(file.Name))).ToArray();
        if (exact.Length > 0) return exact.Length == 1 ? exact[0] : null;
        // ASSRT file URLs can omit an archive's directory wrapper. Reuse only
        // an unambiguous member of this package, retaining its original path.
        string leaf = Leaf(file.Name);
        var matching = names.Where(name => SameName(Leaf(name), leaf)).ToArray();
        if (matching.Length != 1 || !SameName(Relative(file.Name), leaf) && !SameName(Relative(matching[0]), leaf)) return null;
        if (result.Files.Where(item => SameName(Leaf(item.Name), leaf))
                .Select(item => (Name: Relative(item.Name).ToUpperInvariant(), item.FileId)).Distinct().Count() > 1
            || result.PackageNames.Where(name => SameName(Leaf(name), leaf))
                .Select(name => Relative(name).ToUpperInvariant()).Distinct().Count() > 1) return null;
        return matching[0];
    }
    public void Save(SubtitleQuery query, SubtitleResult result, SubtitleFile file, string path, bool complete, bool selected = false, bool imported = false,
        string originalPath = "", string originalHash = "")
    {
        query = FileQuery(query, result, file.Name);
        var parsed = BangumiMedia.FromPlayback(file.Name, file.Name);
        double? episode = query.Season == null ? null : result.Individual ? result.Episode
            : parsed.Ambiguous ? selected ? query.Episode : null : parsed.EpisodeNumber ?? (selected ? query.Episode : null);
        if (query.Season < 0) episode = null;
        var local = new LocalSubtitle(file.FileId, file.Name, System.IO.Path.GetFullPath(path), episode, Variant(file.Name, result.LanguageCode, true))
            { OriginalPath = originalPath, OriginalHash = originalHash };
        lock (Gate)
        {
            string directory = SeasonDirectory(query.Title, query.Season);
            var shelf = Read(directory, query.Title, query.Season);
            var old = shelf.Packages.FirstOrDefault(package => package.Source == result.Source && package.Id == result.Id);
            var previous = old?.Files.FirstOrDefault(item => file.LocalPath.Length > 0
                && item.Path.Equals(file.LocalPath, StringComparison.OrdinalIgnoreCase));
            if (previous != null) local = local with
                { OriginalPath = originalPath.Length > 0 ? originalPath : previous.OriginalPath,
                    OriginalHash = originalHash.Length > 0 ? originalHash : previous.OriginalHash };
            var files = (old?.Files ?? []).Where(item => item.Name != file.Name && (file.FileId <= 0 || item.FileId != file.FileId)
                && (file.LocalPath.Length == 0 || !item.Path.Equals(file.LocalPath, StringComparison.OrdinalIgnoreCase))).Append(local).ToArray();
            var package = new SubtitlePackage(result.Source, result.Id, result.Title, result.Language, result.LanguageCode, files, complete || old?.Complete == true)
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
    public (string Title, SubtitleTrack Track)? FindTrack(string path)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || !CanDelete(path)) return null;
        string full = System.IO.Path.GetFullPath(path);
        string[] parts = System.IO.Path.GetRelativePath(Root, full).Split(System.IO.Path.DirectorySeparatorChar);
        if (parts.Length < 4) return null;
        lock (Gate)
        {
            string directory = System.IO.Path.Combine(Root, parts[0], parts[1]);
            var shelf = Read(directory, "", null);
            if (shelf.Title.Length == 0 || !SeasonDirectory(shelf.Title, shelf.Season).Equals(directory, StringComparison.OrdinalIgnoreCase))
                return null;
            foreach (var package in shelf.Packages)
            {
                var file = package.Files.FirstOrDefault(file => file.Path.Equals(full, StringComparison.OrdinalIgnoreCase) && Exists(file));
                if (file != null) return (shelf.Title, Track(shelf, package, file, Selection(shelf.Title)));
            }
            return null;
        }
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
            var currentQuery = new SubtitleQuery(media.Title, media.Kind == "show" ? media.Season : null, media.Episode, "").WithPlayback(media);
            if (media.Kind == "show" && (media.Parsed.EpisodeNumber == null || selectedMedia.Ambiguous
                || selectedMedia.HasSeason && selectedMedia.Season != media.Season
                || selectedMedia.EpisodeNumber != null && !currentQuery.MatchesContent(System.IO.Path.GetFileName(path), media.Season)))
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
                var query = new SubtitleQuery(media.Title, season, media.Episode, "").WithPlayback(media);
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
            string workDirectory = WorkDirectory(title);
            if (!Directory.Exists(workDirectory)) return [];
            if (!CanDelete(workDirectory)) throw new SubtitleServiceException("字幕记录路径无效，未删除字幕。");
            var shelves = new List<(string Directory, SubtitleShelf Shelf)>();
            foreach (string directory in Directory.GetDirectories(workDirectory))
            {
                if (!CanDelete(directory)) throw new SubtitleServiceException("字幕记录路径无效，未删除字幕。");
                string? index = Directory.EnumerateFiles(directory, "subtitles.json").SingleOrDefault();
                if (index == null) continue;
                if (!CanDelete(index)) throw new SubtitleServiceException("字幕记录路径无效，未删除字幕。");
                // Deleting a set requires every season's records; a failed read
                // must not turn an unreadable season into an empty shelf.
                var shelf = JsonSerializer.Deserialize<SubtitleShelf>(File.ReadAllBytes(index)) ?? throw new JsonException();
                if (shelf.Packages == null || shelf.Packages.Any(package => package == null || package.Files == null))
                    throw new JsonException();
                shelves.Add((directory, shelf));
            }
            string? selectionPath = Directory.EnumerateFiles(workDirectory, "selection.json").SingleOrDefault();
            var selection = new SubtitleSelection();
            if (selectionPath != null)
            {
                if (!CanDelete(selectionPath)) throw new SubtitleServiceException("字幕记录路径无效，未删除字幕。");
                selection = JsonSerializer.Deserialize<SubtitleSelection>(File.ReadAllBytes(selectionPath)) ?? throw new JsonException();
            }
            var package = shelves.SelectMany(item => item.Shelf.Packages)
                .FirstOrDefault(package => package.Files.Any(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase)));
            if (package == null) return [];
            bool SamePackage(SubtitlePackage item) => item.Source == package.Source && item.Id == package.Id;
            string key = PackageKey(package.Source, package.Id);
            var affected = shelves.Where(item => item.Shelf.Packages.Any(SamePackage)).ToArray();
            var indexed = affected.SelectMany(item => item.Shelf.Packages.Where(SamePackage)
                .SelectMany(package => package.Files.Select(file => (item.Directory, Package: package, File: file)))).ToArray();
            var removed = indexed.Select(item => item.File.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool CanWriteIndex(string index) => CanDelete(index)
                && (!File.Exists(index) || (File.GetAttributes(index) & FileAttributes.ReadOnly) == 0);
            // Validate every index destination before changing files or records.
            foreach (string directory in affected.Select(item => item.Directory))
                if (!CanDelete(directory) || !CanWriteIndex(System.IO.Path.Combine(directory, "subtitles.json"))
                    || !CanWriteIndex(System.IO.Path.Combine(directory, "subtitles.json.tmp")))
                    throw new SubtitleServiceException("字幕记录路径无效，未删除字幕。");
            if (selection.Package == key && (!CanWriteIndex(System.IO.Path.Combine(workDirectory, "selection.json"))
                || !CanWriteIndex(System.IO.Path.Combine(workDirectory, "selection.json.tmp"))))
                throw new SubtitleServiceException("字幕记录路径无效，未删除字幕。");
            IReadOnlyList<string>? deletedFiles = null;
            if (deleteLocal)
            {
                var physical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in indexed)
                {
                    var file = item.File;
                    string managed = System.IO.Path.GetFullPath(file.Path);
                    string packageDirectory = System.IO.Path.Combine(item.Directory, Name(key)) + System.IO.Path.DirectorySeparatorChar;
                    if (!System.IO.Path.IsPathFullyQualified(file.Path) || !CanDelete(managed) || !SubtitleDownload.Supported(managed)
                        || !managed.StartsWith(packageDirectory, StringComparison.OrdinalIgnoreCase))
                        throw new SubtitleServiceException("字幕文件路径无效，未删除本地文件。");
                    physical.Add(managed);
                    if (file.OriginalPath.Length == 0)
                    {
                        if (item.Package.Imported) throw new SubtitleServiceException("无法确认导入字幕的原始文件，未删除本地文件。");
                        continue;
                    }
                    string original = System.IO.Path.GetFullPath(file.OriginalPath);
                    bool stored = original.StartsWith(Root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                    if (!System.IO.Path.IsPathFullyQualified(file.OriginalPath) || !SubtitleDownload.Supported(original) || !RegularPath(original)
                        || !System.IO.Path.GetFileName(original).Equals(System.IO.Path.GetFileName(file.Name.Replace('\\', '/')), StringComparison.OrdinalIgnoreCase)
                        || !Regex.IsMatch(file.OriginalHash, "^[A-Fa-f0-9]{64}$")
                        || (stored ? !original.StartsWith(workDirectory + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            : !item.Package.Imported || item.Package.Id != ImportId(original)))
                        throw new SubtitleServiceException("原始字幕路径无法核实，未删除本地文件。");
                    if (File.Exists(original) && !FileHash(original).Equals(file.OriginalHash, StringComparison.OrdinalIgnoreCase))
                        throw new SubtitleServiceException("原始字幕已更改，未删除：" + System.IO.Path.GetFileName(original));
                    physical.Add(original);
                }
                foreach (var item in affected)
                {
                    string directory = System.IO.Path.Combine(item.Directory, Name(key));
                    if (!Directory.Exists(directory)) continue;
                    var pending = new Stack<string>(); pending.Push(directory);
                    while (pending.Count > 0)
                    {
                        string current = pending.Pop();
                        if (!CanDelete(current)) throw new SubtitleServiceException("字幕文件路径无效，未删除本地文件。");
                        foreach (string file in Directory.EnumerateFiles(current).Where(SubtitleDownload.Supported))
                        {
                            if (!CanDelete(file)) throw new SubtitleServiceException("字幕文件路径无效，未删除本地文件。");
                            physical.Add(System.IO.Path.GetFullPath(file));
                        }
                        foreach (string child in Directory.EnumerateDirectories(current)) pending.Push(child);
                    }
                }
                string archiveDirectory = System.IO.Path.Combine(workDirectory, "字幕包", Name(key));
                if (Directory.Exists(archiveDirectory))
                {
                    if (!CanDelete(archiveDirectory)) throw new SubtitleServiceException("字幕包路径无效，未删除本地文件。");
                    foreach (string archive in Directory.EnumerateFiles(archiveDirectory).Where(SubtitleDownload.PackageSupported))
                    {
                        if (!CanDelete(archive)) throw new SubtitleServiceException("字幕包路径无效，未删除本地文件。");
                        physical.Add(archive);
                    }
                }
                bool RefersToPhysical(string value) => value.Length > 0 && physical.Contains(System.IO.Path.GetFullPath(value));
                bool shared = false;
                foreach (string work in Directory.GetDirectories(Root))
                {
                    if (!RegularPath(work)) throw new SubtitleServiceException("字幕记录路径无效，未删除本地文件。");
                    foreach (string directory in Directory.GetDirectories(work))
                    {
                        string index = System.IO.Path.Combine(directory, "subtitles.json");
                        if (!File.Exists(index)) continue;
                        if (!CanDelete(index)) throw new SubtitleServiceException("字幕记录路径无效，未删除本地文件。");
                        // A failed index read cannot establish that its files are
                        // unreferenced. Validate every reference before deletion.
                        var shelf = JsonSerializer.Deserialize<SubtitleShelf>(File.ReadAllBytes(index)) ?? throw new JsonException();
                        shared |= shelf.Packages.Any(item => (!work.Equals(workDirectory, StringComparison.OrdinalIgnoreCase) || !SamePackage(item))
                            && item.Files.Any(file => RefersToPhysical(file.Path) || RefersToPhysical(file.OriginalPath)));
                    }
                }
                if (shared) throw new SubtitleServiceException("字幕文件仍被其他字幕或作品使用，未删除本地文件。");
                foreach (string file in physical)
                    if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReadOnly) != 0)
                        throw new SubtitleServiceException("文件为只读，未删除：" + System.IO.Path.GetFileName(file));
                foreach (string file in physical) File.Delete(file);
                deletedFiles = physical.Where(SubtitleDownload.Supported).ToArray();
            }
            foreach (var (directory, shelf) in affected)
            {
                var packages = shelf.Packages.Where(item => !SamePackage(item)).ToArray();
                bool preferred = packages.Any(item => PackageKey(item.Source, item.Id) == shelf.PreferredPackage
                    && item.Files.Any(file => file.Variant == shelf.PreferredVariant));
                Write(directory, shelf with { Packages = packages, PreferredPackage = preferred ? shelf.PreferredPackage : "", PreferredVariant = preferred ? shelf.PreferredVariant : "" });
            }
            if (selection.Package == key) WriteSelection(title, new());
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
        if (result.Individual && !IndividualMatches(query, result)) return null;
        string preferred;
        lock (Gate) preferred = Read(SeasonDirectory(query.Title, query.Season), query.Title, query.Season).PreferredVariant;
        string directory = PackageDirectory(query, result) + System.IO.Path.DirectorySeparatorChar;
        result = result with { PackageNames = files.Select(file => file.Name).ToArray() };
        files = files.Where(file => (file.Season ?? FileQuery(query, result, file.Name).Season) == query.Season
            && (file.LocalPath.Length == 0 || file.LocalPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase))).ToArray();
        var candidates = files.Where(file =>
            {
                string language = FileLanguage(file.Name);
                if (query.Language.Length > 0)
                {
                    var languages = language.Length > 0 ? new[] { SubtitleLanguage.Find(language)! } : result.Languages;
                    if (languages.Count != 1 || !languages[0].Matches(query.Language)) return false;
                }
                if (query.Season == null) return true;
                if (query.Episode == null) return false;
                var content = SubtitleQuery.Content(file.Name);
                return query.MatchesContent(file.Name, query.Season, file.Episode)
                    || !result.Individual && files.Count == 1 && file.Episode == null && !content.Ambiguous && content.Episode == null
                        && content.Type.Length == 0 && query.ContentType.Length == 0;
            }).ToArray();
        if (query.Season == 0 && query.ContentType.Length == 0
            && candidates.Select(file => SubtitleQuery.Content(file.Name).Type).Distinct().Count() > 1) return null;
        return candidates
            .OrderByDescending(file => query.Language.Length > 0 && FileLanguage(file.Name) == query.Language)
            .ThenByDescending(file => preferred.Length > 0 && Variant(file.Name, result.LanguageCode, true) == preferred)
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
        string[] Parts(string name) => name.Replace('\\', '/').Split('/');
        if (result.Individual && query.Season < 0) return query;
        if (query.Season == null)
            return !result.Collection ? query : query with { Season = MovieFileMatches(query, filename) ? null : -1, Episode = null };
        var names = result.PackageNames.Append(filename).Distinct().ToArray();
        var paths = names.Select(Parts).ToArray();
        var wanted = query.Names.Append(query.Title).Concat(result.TitleNames).Concat(result.Title.Split('/'))
            .SelectMany(BangumiMedia.TitleVariants)
            .Select(name => TraktMedia.Normalize(BangumiMedia.Parse(name).Title)).Where(name => name.Length > 0).ToHashSet();
        foreach (string title in result.Title.Split('/').Select(FileTitle).Where(title => title.Length > 0)) wanted.Add(title);
        var available = paths.SelectMany(parts => parts.Take(parts.Length - 1).Select(TraktMedia.Normalize))
            .Concat(names.Select(FileTitle)).Where(name => name.Length > 0).ToHashSet();
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
        bool SpecialFolder(string folder) => SubtitleQuery.IsSpecial(folder)
            && !Regex.IsMatch(folder, @"(?:^|[^a-z0-9])TV(?=[^a-z0-9]|$)|正片|本篇|本編", RegexOptions.IgnoreCase);
        var parsed = BangumiMedia.FromPlayback(leaf, leaf);
        if (parsed.HasSeason && parsed.Season > 0 && Parts(filename).SkipLast(1).Any(folder =>
            Regex.IsMatch(folder.Trim(' ', '[', ']', '(', ')'), @"^(?:OVA|OAD|SPs?|Specials?|特典|特别篇|特別篇|特别版|特別版)(?:\s*\d+)?$", RegexOptions.IgnoreCase)
            || BangumiMedia.Parse(folder) is { HasSeason: true, Season: 0 }))
            return query with { Season = -1, Episode = null };
        if (SubtitleQuery.IsSpecial(leaf)) season = 0;
        else if (parsed.HasSeason) season = parsed.Season;
        else
        {
            foreach (string folder in Parts(filename).SkipLast(1).Reverse())
            {
                var media = BangumiMedia.Parse(folder);
                if (SpecialFolder(folder)) season = 0;
                else if (media.HasSeason) season = media.Season;
                else if (result.Collection && !paths.All(parts => parts.SkipLast(1).Contains(folder, StringComparer.OrdinalIgnoreCase)))
                    season = Numbered(folder) ?? (First(TraktMedia.Normalize(folder)) ? 1 : null);
                if (season != null) break;
            }
            if (result.Collection) season ??= Numbered(FileTitle(filename)) ?? (First(FileTitle(filename)) ? 1 : null);
        }
        if (season == null && !result.Collection)
        {
            var titles = names.Select(FileTitle).Where(title => title.Length > 0).Distinct().ToArray();
            string fileTitle = FileTitle(filename);
            if (titles.Length > 1 && titles.Any(wanted.Contains) && !wanted.Contains(fileTitle))
                return query with { Season = -1, Episode = null };
            bool television = Parts(filename).SkipLast(1).Append(result.Title).Any(value => Regex.IsMatch(value,
                @"(?:^|[^a-z0-9])TV(?=[^a-z0-9]|$)|正片|本篇|本編", RegexOptions.IgnoreCase));
            if (query.Season == 0 && result.Season != 0 && !SubtitleQuery.IsSpecial(result.Title) && !television)
                return query with { Season = -1, Episode = null };
            season = result.Season >= 0 ? result.Season : television ? 1 : query.Season > 0 ? query.Season : null;
            if (season == 0 && !SubtitleQuery.IsSpecial(result.Title) && !result.Individual) season = null;
        }
        return query with { Season = season ?? -1, Episode = season == query.Season ? query.Episode : null };
    }
    public IReadOnlyList<SubtitleResult> Results(string title)
    {
        lock (Gate) return Shelves(title).SelectMany(item => item.Shelf.Packages
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
                { Local = true, Complete = group.All(item => item.Package.Complete && item.Package.Files.All(Exists)),
                    Format = package.Format, Season = seasons.Length == 1 ? seasons[0] : null, AvailableSeasons = seasons,
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
        var query = new SubtitleQuery(media.Title, season, media.Episode, "").WithPlayback(media);
        lock (Gate)
        {
            var shelf = Read(SeasonDirectory(media.Title, season), media.Title, season);
            var pinned = Selection(media.Title);
            var files = shelf.Packages.SelectMany(package => package.Files.Where(file => Exists(file)
                    && (season == null ? file.Episode == null : query.MatchesContent(file.Name, shelf.Season, file.Episode))
                    && !(System.IO.Path.GetExtension(file.Name).Equals(".sub", StringComparison.OrdinalIgnoreCase)
                        && package.Files.Any(pair => System.IO.Path.ChangeExtension(file.Name, ".idx").Equals(pair.Name, StringComparison.OrdinalIgnoreCase))))
                .Select(file => (Package: package, File: file))).ToArray();
            if (season == 0 && query.ContentType.Length == 0
                && files.Select(item => SubtitleQuery.Content(item.File.Name).Type).Distinct().Count() > 1) return [];
            return files.Select(item => Track(shelf, item.Package, item.File, pinned))
                .OrderByDescending(track => track.Pinned).ThenByDescending(track => track.Preferred).ToArray();
        }
    }
    static SubtitleTrack Track(SubtitleShelf shelf, SubtitlePackage package, LocalSubtitle file, SubtitleSelection pinned) =>
        new(file.Path, System.IO.Path.GetFileName(file.Name.Replace('\\', '/')),
            FileLanguage(file.Name) is { Length: > 0 } language ? language : package.LanguageCode,
            shelf.PreferredPackage == PackageKey(package.Source, package.Id) && shelf.PreferredVariant == file.Variant,
            pinned.Package == PackageKey(package.Source, package.Id) && pinned.Variant == Variant(file.Name, package.LanguageCode, true));
}

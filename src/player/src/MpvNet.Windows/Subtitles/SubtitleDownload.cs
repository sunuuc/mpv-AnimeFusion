using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Compressors.Xz;
using System.Text.RegularExpressions;

namespace MpvNet.Windows.Subtitles;

public sealed class SubtitleDownload(HttpClient http, SubtitleLibrary library)
{
    public Func<IReadOnlyList<SubtitleArchiveName>, CancellationToken,
        Task<IReadOnlyDictionary<string, string>?>>? ChooseArchiveNamesAsync { get; init; }
    public static bool Supported(string name) => MpvNet.FileTypes.Subtitle.Contains(Path.GetExtension(name).TrimStart('.').ToLowerInvariant());
    public static bool PackageSupported(string name) => Path.GetExtension(name).ToLowerInvariant() is ".zip" or ".rar" or ".7z";

    public async Task<string?> DownloadAsync(SubtitleQuery query, SubtitleResult result, SubtitleFile? selected,
        IReadOnlyList<SubtitleFile> files, IProgress<string>? progress, CancellationToken token)
    {
        if (selected != null && (selected.Source != result.Source || selected.ResultId != result.Id))
            throw new SubtitleServiceException("字幕文件无效。");
        if (!result.Local)
        {
            if (result.Individual)
                return await DownloadIndividualAsync(query, result, selected != null, progress, token).ConfigureAwait(false);
            if (selected != null)
                return await DownloadFileAsync(query, result, selected, files, progress, token).ConfigureAwait(false);
            if (result.DownloadName.Length == 0 || result.DownloadUrl.Length == 0)
                throw new SubtitleServiceException("没有可下载的字幕包。");
            if (query.Season == null && Supported(result.DownloadName))
            {
                var movieFile = new SubtitleFile(result.Source, result.Id, 0, result.DownloadName, "");
                if (library.Cached(query, result, movieFile) is { } cached)
                {
                    MpvNet.Diagnostics.Write("subtitle.download", "reused", new { source = result.Source.ToString(), result.Id });
                    return cached;
                }
                string moviePath = library.FilePath(query, result, movieFile.Name);
                await WriteAsync(moviePath, await FetchAsync(result.DownloadUrl, result.Source, 32 * 1024 * 1024, token, progress).ConfigureAwait(false), token).ConfigureAwait(false);
                library.Save(query, result, movieFile, moviePath, true, true);
                return moviePath;
            }
            if (!PackageSupported(result.DownloadName))
                throw new SubtitleServiceException("请选择整包字幕。");
            string archivePath = library.ArchivePath(query, result);
            if (!File.Exists(archivePath) || new FileInfo(archivePath).Length == 0)
            {
                progress?.Report("正在下载字幕包…");
                byte[] bytes = await FetchAsync(result.DownloadUrl, result.Source, 64 * 1024 * 1024, token, progress).ConfigureAwait(false);
                using var stream = new MemoryStream(bytes);
                if (!ArchiveFactory.IsArchive(stream, out var type) || type is not (SharpCompress.Common.ArchiveType.Zip or SharpCompress.Common.ArchiveType.Rar or SharpCompress.Common.ArchiveType.SevenZip))
                    throw new SubtitleServiceException("字幕包无法解压。");
                await WriteAsync(archivePath, bytes, token).ConfigureAwait(false);
            }
            var package = library.Package(query, result);
            if (package?.Complete != true || package.Files.Any(file => !File.Exists(file.Path)))
                if (await Task.Run(() => ExtractAsync(query, result, archivePath, progress, token), token).ConfigureAwait(false) != true)
                    return null;
            package = library.Package(query, result);
            if (package != null) result = result with { Collection = package.Collection, PackageNames = package.Files.Select(file => file.Name).ToArray() };
            files = package?.Files.Select(file => new SubtitleFile(result.Source, result.Id, file.FileId, file.Name, "") { LocalPath = file.Path }).ToArray() ?? [];
            selected = library.CurrentFile(query, result, files);
        }

        if (selected == null) return null;
        SubtitleFile? pair = null;
        string extension = Path.GetExtension(selected.Name).ToLowerInvariant();
        if (extension is ".idx" or ".sub")
        {
            pair = files.FirstOrDefault(file => Path.ChangeExtension(file.Name, extension).Equals(selected.Name, StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(file.Name).Equals(extension == ".idx" ? ".sub" : ".idx", StringComparison.OrdinalIgnoreCase));
            if (extension == ".idx" && pair == null) throw new SubtitleServiceException("这份 IDX 字幕缺少对应的 SUB 文件。");
        }
        string path = SavedPath(query, result, selected);
        if (pair != null)
        {
            string pairedPath = SavedPath(query, result, pair);
            if (extension == ".sub") path = pairedPath;
        }
        return path;
    }

    async Task<string?> DownloadFileAsync(SubtitleQuery query, SubtitleResult result, SubtitleFile selected,
        IReadOnlyList<SubtitleFile> files, IProgress<string>? progress, CancellationToken token)
    {
        MpvNet.Diagnostics.Write("subtitle.download", "file-start", new
            { source = result.Source.ToString(), result.Id, selected.FileId, selected.Name });
        if (selected.Source != result.Source || selected.ResultId != result.Id || !Supported(selected.Name))
            throw new SubtitleServiceException("不支持这份字幕格式。");
        string extension = Path.GetExtension(selected.Name).ToLowerInvariant();
        SubtitleFile? pair = extension is ".idx" or ".sub" ? files.FirstOrDefault(file => file.Source == result.Source
            && file.ResultId == result.Id && Path.ChangeExtension(file.Name, extension).Equals(selected.Name, StringComparison.OrdinalIgnoreCase)
            && Path.GetExtension(file.Name).Equals(extension == ".idx" ? ".sub" : ".idx", StringComparison.OrdinalIgnoreCase)) : null;
        if (extension == ".idx" && pair == null)
            throw new SubtitleServiceException("这份 IDX 字幕缺少对应的 SUB 文件。");
        // Fetch both parts before saving either. Write the SUB data before its IDX
        // index, so a failed pair never leaves a new, unusable IDX in the library.
        SubtitleFile[] requested = pair == null ? [selected] : extension == ".idx" ? [pair, selected] : [selected, pair];
        if (requested.Any(file => library.Cached(query, result, file) == null)
            && result.DownloadName.Length > 0 && PackageSupported(result.DownloadName)
            && library.ArchivePath(query, result) is { } archivePath && File.Exists(archivePath))
        {
            bool? extracted = await Task.Run(() => ExtractAsync(query, result, archivePath, progress, token, requested), token).ConfigureAwait(false);
            if (extracted == false) return null;
            MpvNet.Diagnostics.Write("subtitle.download", extracted == true ? "file-archive-reused" : "file-archive-miss",
                new { source = result.Source.ToString(), result.Id, selected.FileId });
        }
        var pending = requested.Select(file => (File: file, Cached: library.Cached(query, result, file),
            Target: library.FilePath(query, result, file.Name))).ToArray();
        MpvNet.Diagnostics.Write("subtitle.download", "file-cache", new
            { source = result.Source.ToString(), result.Id, files = pending.Select(item => new { item.File.FileId, cached = item.Cached != null }).ToArray() });
        bool alignPair = pair != null && !Path.ChangeExtension(pending[1].Cached ?? pending[1].Target, ".sub")
            .Equals(pending[0].Cached ?? pending[0].Target, StringComparison.OrdinalIgnoreCase);
        var downloaded = new List<(SubtitleFile File, string Path, byte[] Bytes)>();
        int fetched = 0, copied = 0;
        foreach (var item in pending)
        {
            token.ThrowIfCancellationRequested();
            if (item.Cached != null)
            {
                if (alignPair && !item.Cached.Equals(item.Target, StringComparison.OrdinalIgnoreCase))
                {
                    using var stream = File.OpenRead(item.Cached);
                    byte[] cached = await SubtitleHttp.ReadBytesAsync(stream, 32 * 1024 * 1024, token).ConfigureAwait(false);
                    downloaded.Add((item.File with { LocalPath = item.Cached }, item.Target, cached));
                    copied++;
                }
                continue;
            }
            byte[] bytes = await FetchAsync(item.File.Url, result.Source, 32 * 1024 * 1024, token, progress).ConfigureAwait(false);
            downloaded.Add((item.File, item.Target, bytes));
            fetched++;
        }
        foreach (var item in downloaded)
            await WriteAsync(item.Path, item.Bytes, token).ConfigureAwait(false);
        foreach (var item in downloaded)
            library.Save(query, result, item.File, item.Path, false);
        string selectedPath = SavedPath(query, result, selected);
        var saved = library.Results(query.Title).FirstOrDefault(item => item.Source == result.Source && item.Id == result.Id);
        var savedFile = saved?.Files.FirstOrDefault(file => file.LocalPath.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
        bool matches = saved != null && savedFile != null && library.CurrentFile(query, saved, [savedFile]) != null;
        MpvNet.Diagnostics.Write("subtitle.download", fetched == 0 ? "reused" : "file-indexed",
            new { source = result.Source.ToString(), result.Id, selected.FileId, count = fetched, copied, matches });
        string path = extension == ".sub" && pair != null ? SavedPath(query, result, pair) : selectedPath;
        return path;
    }

    async Task<string?> DownloadIndividualAsync(SubtitleQuery query, SubtitleResult result, bool explicitlySelected,
        IProgress<string>? progress, CancellationToken token)
    {
        string name = result.DownloadName;
        string filename = name.EndsWith(".xz", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
        var sourceQuery = query with { Season = result.Season, Episode = result.Episode };
        int? fileSeason = SubtitleLibrary.FileQuery(sourceQuery, result, filename).Season;
        var title = Bangumi.BangumiMedia.Parse(result.Title);
        int? titleSeason = SubtitleQuery.IsSpecial(result.Title) ? 0 : title.HasSeason ? title.Season : null;
        if (result.Season >= 0 && (fileSeason != result.Season
            || titleSeason != null && titleSeason != result.Season))
        {
            MpvNet.Diagnostics.Write("subtitle.download", "season-mismatch", new { source = result.Source.ToString(), result.Id,
                result.Season, fileSeason, titleSeason });
            throw new SubtitleServiceException("字幕季集信息不一致。");
        }
        // An extracted track belongs to its release, even when the user searches
        // another episode. Automatic loading still requires the current episode;
        // an explicitly selected file is returned without changing its identity.
        // Unresolved coordinates remain in the unclassified shelf without loading.
        bool sameWork = SubtitleLibrary.IndividualMatches(query, result);
        var targetQuery = query with { Season = sameWork ? result.Season : -1, Episode = sameWork ? result.Episode : null };
        bool matches = sameWork && (query.Season == null ? fileSeason == null
            : query.MatchesContent(filename, fileSeason, result.Episode));
        var package = library.Package(targetQuery, result);
        if (package?.Complete == true && package.Files.Count == 1)
        {
            var saved = package.Files[0];
            var savedFile = new SubtitleFile(result.Source, result.Id, saved.FileId, saved.Name, "");
            if (library.Cached(targetQuery, result, savedFile) is { } cached)
            {
                MpvNet.Diagnostics.Write("subtitle.download", "reused", new { source = result.Source.ToString(), result.Id, matches });
                return explicitlySelected || matches ? cached : null;
            }
        }
        byte[] bytes = await FetchAsync(result.DownloadUrl, result.Source, 32 * 1024 * 1024, token, progress).ConfigureAwait(false);
        if (name.EndsWith(".xz", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report("正在解压字幕…");
            try
            {
                using var packed = new MemoryStream(bytes);
                using var decoded = new XZStream(packed);
                bytes = await SubtitleHttp.ReadBytesAsync(decoded, 32 * 1024 * 1024, token).ConfigureAwait(false);
                name = name[..^3];
            }
            catch (Exception error) when (error is SharpCompress.Common.SharpCompressException or InvalidDataException or EndOfStreamException)
            { throw new SubtitleServiceException("字幕无法解压。"); }
        }
        // Some services label extracted ASS tracks as .txt. Identify the actual
        // text format without altering the original subtitle bytes or encoding.
        string text = Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 65536))).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        string format = text.StartsWith("[Script Info]", StringComparison.OrdinalIgnoreCase) ? "ass"
            : text.StartsWith("WEBVTT", StringComparison.Ordinal) ? "vtt"
            : Regex.IsMatch(text, @"(?m)^\d{1,2}:\d{2}:\d{2}[,.]\d{3}\s+-->\s+\d{1,2}:\d{2}:\d{2}[,.]\d{3}") ? "srt" : "";
        if (format.Length == 0) throw new SubtitleServiceException("没有可用的文本字幕。");
        if (format == "ass")
        {
            // A release's catalogue coordinates cannot override an explicit episode
            // in its extracted track. Check only Script Info, never dialogue text.
            string info = Regex.Match(text, @"\A\[Script Info\][^\S\r\n]*\r?\n(?<info>.*?)(?=^\[|\z)",
                RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups["info"].Value;
            string contentTitle = Regex.Match(info, @"^Title:[^\S\r\n]*(?<title>[^\r\n]+)",
                RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups["title"].Value.Trim();
            var content = Bangumi.BangumiMedia.FromPlayback(contentTitle, contentTitle);
            int? contentSeason = SubtitleQuery.IsSpecial(contentTitle) ? 0 : content.HasSeason ? content.Season : null;
            if (content.Ambiguous || result.Episode != null && content.EpisodeNumber != null && result.Episode != content.EpisodeNumber
                || result.Season >= 0 && contentSeason != null && result.Season != contentSeason)
            {
                MpvNet.Diagnostics.Write("subtitle.download", "episode-mismatch", new { source = result.Source.ToString(), result.Id,
                    result.Season, result.Episode, contentSeason,
                    contentEpisode = content.EpisodeNumber, content.Ambiguous });
                throw new SubtitleServiceException("字幕季集信息不一致。");
            }
        }
        if (Path.GetExtension(name).Equals(".txt", StringComparison.OrdinalIgnoreCase)) name = Path.ChangeExtension(name, format);
        if (!Supported(name)) throw new SubtitleServiceException("不支持这份字幕格式。");
        var file = new SubtitleFile(result.Source, result.Id, 0, name, "");
        string path = library.FilePath(targetQuery, result, file.Name);
        await WriteAsync(path, bytes, token).ConfigureAwait(false);
        library.Save(targetQuery, result with { Format = format.ToUpperInvariant() }, file, path, true, true);
        MpvNet.Diagnostics.Write("subtitle.download", "indexed", new { source = result.Source.ToString(), result.Id, format,
            bytes = bytes.Length, sameWork, matches, targetQuery.Season, targetQuery.Episode });
        return explicitlySelected || matches ? path : null;
    }

    async Task<bool?> ExtractAsync(SubtitleQuery query, SubtitleResult result, string archivePath, IProgress<string>? progress,
        CancellationToken token, IReadOnlyList<SubtitleFile>? requested = null)
    {
        MpvNet.Diagnostics.Write("subtitle.download", "extract-start", new { source = result.Source.ToString(), result.Id });
        progress?.Report("正在解压字幕包…");
        try
        {
            var names = new ArchiveFileNames();
            using var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions
            {
                ArchiveEncoding = new ArchiveEncoding { CustomDecoder = names.Decode }
            });
            var entries = archive.Entries.ToArray();
            if (entries.Length > 4000 || entries.Sum(entry => entry.Size) > 256 * 1024 * 1024L)
                throw new SubtitleServiceException("字幕包过大。");
            var choices = names.Resolve(entries, result.PackageNames, token);
            if (choices.Count > 0)
            {
                if (ChooseArchiveNamesAsync == null)
                    throw new SubtitleServiceException("无法确定字幕文件名编码，字幕包已保留。");
                progress?.Report("请确认字幕文件名…");
                var selectedNames = await ChooseArchiveNamesAsync(choices, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (selectedNames == null) return false;
                names.Select(choices, selectedNames);
                progress?.Report("正在解压字幕包…");
            }
            var requestedResult = result;
            result = result with { PackageNames = entries.Where(entry => !entry.IsDirectory && names.Name(entry) is { } name && Supported(name)).Select(entry => names.Name(entry)!).ToArray() };
            var collection = result with { Collection = true };
            if (result.PackageNames.Select(name => SubtitleLibrary.FileQuery(query, collection, name).Season).Where(season => season >= 0).Distinct().Count() > 1)
                result = collection;
            Dictionary<string, SubtitleFile>? selectedFiles = null;
            if (requested != null)
            {
                selectedFiles = new(StringComparer.OrdinalIgnoreCase);
                foreach (var file in requested)
                {
                    string? name = SubtitleLibrary.PackageFileName(result.PackageNames, requestedResult, file);
                    int? expectedSeason = file.Season ?? SubtitleLibrary.FileQuery(query, requestedResult, file.Name).Season;
                    if (name == null || expectedSeason < 0
                        || SubtitleLibrary.FileQuery(query, result, name).Season != expectedSeason
                        || !selectedFiles.TryAdd(name.Replace('\\', '/'), file with { Name = name })) return null;
                }
                if (selectedFiles.Count == 2)
                {
                    string[] paired = selectedFiles.Keys.ToArray();
                    if (!Path.ChangeExtension(paired[0], Path.GetExtension(paired[1])).Equals(paired[1], StringComparison.OrdinalIgnoreCase))
                        return null;
                }
            }
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries.Where(entry => !entry.IsDirectory))
            {
                if (entry.LinkTarget != null || entry.IsEncrypted) throw new SubtitleServiceException("不支持这份字幕包。");
                if (names.Name(entry) is { } name && Supported(name))
                {
                    string target = library.FilePath(query, result, name);
                    if (!targets.Add(target))
                        throw new SubtitleServiceException("字幕包中存在重名字幕文件。");
                }
            }
            int count = 0;
            int total = selectedFiles?.Count ?? entries.Count(entry => !entry.IsDirectory && names.Name(entry) is { } name && Supported(name)), lastPercent = -1;
            var selectedWrites = requested == null ? null : new List<(SubtitleFile File, string Path, byte[] Bytes)>();
            async Task SaveEntryAsync(SharpCompress.Common.IEntry entry, Func<Stream> open)
            {
                token.ThrowIfCancellationRequested();
                string? name = names.Name(entry);
                if (entry.IsDirectory || name == null || !Supported(name)) return;
                if (selectedFiles != null && !selectedFiles.ContainsKey(name.Replace('\\', '/'))) return;
                var file = selectedFiles?.GetValueOrDefault(name.Replace('\\', '/'))
                    ?? result.Files.FirstOrDefault(file => file.Name == name) ?? new SubtitleFile(result.Source, result.Id, 0, name, "");
                string target = library.FilePath(query, result, file.Name);
                if (library.Cached(query, result, file) == null)
                {
                    using var stream = open();
                    byte[] bytes = await SubtitleHttp.ReadBytesAsync(stream, 32 * 1024 * 1024, token).ConfigureAwait(false);
                    if (bytes.Length == 0) return;
                    if (selectedWrites != null) selectedWrites.Add((file, target, bytes));
                    else
                    {
                        await WriteAsync(target, bytes, token).ConfigureAwait(false);
                        library.Save(query, result, file, target, false);
                    }
                }
                count++;
                int percent = count * 100 / Math.Max(1, total) / 10 * 10;
                if (percent != lastPercent) { lastPercent = percent; progress?.Report($"解压字幕 {percent}%"); }
            }
            if (entries.Any(entry => entry.IsSolid) || archive.Type == SharpCompress.Common.ArchiveType.SevenZip)
            {
                using var reader = archive.ExtractAllEntries();
                while (reader.MoveToNextEntry()) await SaveEntryAsync(reader.Entry, reader.OpenEntryStream).ConfigureAwait(false);
            }
            else foreach (var entry in entries) await SaveEntryAsync(entry, entry.OpenEntryStream).ConfigureAwait(false);
            if (count == 0 || selectedFiles != null && count != selectedFiles.Count)
                throw new SubtitleServiceException("字幕包里没有可用字幕。");
            if (selectedWrites != null)
            {
                var ordered = selectedWrites.OrderBy(item => Path.GetExtension(item.File.Name).Equals(".idx", StringComparison.OrdinalIgnoreCase)).ToArray();
                foreach (var item in ordered) await WriteAsync(item.Path, item.Bytes, token).ConfigureAwait(false);
                foreach (var item in ordered) library.Save(query, result, item.File, item.Path, false);
            }
            if (requested == null) library.Complete(query, result);
            MpvNet.Diagnostics.Write("subtitle.download", "extract-complete", new { source = result.Source.ToString(), result.Id, count });
            return true;
        }
        catch (DecoderFallbackException) { throw new SubtitleServiceException("字幕包中的 Unicode 文件名无效。"); }
        catch (Exception error) when (error is SharpCompress.Common.SharpCompressException or InvalidDataException)
        { throw new SubtitleServiceException("字幕包无法解压。"); }
    }

    sealed class ArchiveFileNames
    {
        static readonly Lazy<Encoding[]> Encodings = new(() =>
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var pages = Encoding.GetEncodings().Select(info => info.CodePage).ToHashSet();
            if (!Native.WinApi.EnumSystemCodePages(value =>
                { if (int.TryParse(value, out int page)) pages.Add(page); return true; }, Native.WinApi.CP_SUPPORTED))
                throw new SubtitleServiceException("无法读取系统字符编码。");
            var supported = new List<Encoding>();
            foreach (int page in pages)
            {
                try { supported.Add(Encoding.GetEncoding(page, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)); }
                catch (Exception error) when (error is ArgumentException or NotSupportedException) { }
            }
            return supported.OrderBy(encoding => encoding.WebName, StringComparer.Ordinal).ThenBy(encoding => encoding.CodePage).ToArray();
        });
        readonly string _prefix = "\uE000" + Guid.NewGuid().ToString("N");
        readonly Dictionary<string, byte[]> _unknown = [];
        readonly Dictionary<string, string> _selected = [];

        public string Decode(byte[] bytes, int offset, int length, EncodingType type)
        {
            if (type == EncodingType.UTF8) return Encoding.UTF8.GetString(bytes, offset, length);
            byte[] raw = bytes.AsSpan(offset, length).ToArray();
            if (raw.All(value => value >= 32 && value < 127)) return Encoding.UTF8.GetString(raw);
            // These temporary keys stay inside the archive reader. No file is
            // written until every subtitle name has been resolved.
            string key = _prefix + Convert.ToHexString(raw) + (raw.LastOrDefault() == (byte)'/' ? "/" : "");
            _unknown.TryAdd(key, raw);
            return _selected.GetValueOrDefault(key, key);
        }

        public string? Name(IEntry entry) => entry.Key == null ? null : _selected.GetValueOrDefault(entry.Key, entry.Key);

        public IReadOnlyList<SubtitleArchiveName> Resolve(IEnumerable<IEntry> entries, IReadOnlyList<string> suppliedNames, CancellationToken token)
        {
            var choices = new List<SubtitleArchiveName>();
            foreach (var entry in entries.Where(entry => !entry.IsDirectory && entry.Key != null).DistinctBy(entry => entry.Key))
            {
                token.ThrowIfCancellationRequested();
                string key = entry.Key!;
                if (Supported(key) && key.Contains('\uFFFD'))
                    throw new SubtitleServiceException("字幕包中的 Unicode 文件名无效。");
                if (!_unknown.TryGetValue(key, out var bytes)) continue;
                var candidates = new List<SubtitleArchiveNameOption>();
                foreach (var encoding in Encodings.Value)
                {
                    try
                    {
                        string name = encoding.GetString(bytes);
                        if (Supported(name) && !name.Any(char.IsControl) && !name.Contains('\uFFFD')
                            && encoding.GetBytes(name).AsSpan().SequenceEqual(bytes))
                            candidates.Add(new($"{encoding.WebName} ({encoding.CodePage})", name));
                    }
                    catch (Exception error) when (error is ArgumentException or NotSupportedException) { }
                }
                var options = candidates.GroupBy(candidate => candidate.Name, StringComparer.Ordinal)
                    .Select(group => new SubtitleArchiveNameOption(string.Join(" / ", group.Select(item => item.Encoding)), group.Key)).ToArray();
                if (options.Length == 0)
                {
                    // Non-subtitle entries are not extracted by this downloader.
                    if (Supported(Encoding.Latin1.GetString(bytes)))
                        throw new SubtitleServiceException("无法读取字幕文件名，字幕包已保留。");
                    continue;
                }
                var exact = options.Where(option => suppliedNames.Any(name => name.Replace('\\', '/') == option.Name.Replace('\\', '/'))).ToArray();
                if (exact.Length == 0)
                    exact = options.Where(option => suppliedNames.Any(name => Path.GetFileName(name.Replace('\\', '/'))
                        == Path.GetFileName(option.Name.Replace('\\', '/')))).ToArray();
                if (exact.Length == 1) _selected[key] = exact[0].Name;
                else if (options.Length == 1) _selected[key] = options[0].Name;
                else choices.Add(new(key, options));
            }
            return choices;
        }

        public void Select(IReadOnlyList<SubtitleArchiveName> choices, IReadOnlyDictionary<string, string> selected)
        {
            foreach (var choice in choices)
            {
                if (!selected.TryGetValue(choice.Key, out string? name) || !choice.Options.Any(option => option.Name == name))
                    throw new SubtitleServiceException("请确认字幕文件名。");
                _selected[choice.Key] = name;
            }
        }
    }

    string SavedPath(SubtitleQuery query, SubtitleResult result, SubtitleFile file) =>
        library.Cached(query, result, file) ?? throw new SubtitleServiceException("没有已保存的字幕。");

    async Task<byte[]> FetchAsync(string url, SubtitleSource source, int limit, CancellationToken token, IProgress<string>? progress)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            throw new SubtitleServiceException("字幕下载链接无效。");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("*/*");
        return await SubtitleHttp.ReadAsync(http, request, source.ToString(), async (response, readToken) =>
        {
            if (!response.IsSuccessStatusCode) throw new SubtitleServiceException("字幕下载失败，请稍后重试。");
            string type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type.Contains("html", StringComparison.OrdinalIgnoreCase) || type.Contains("json", StringComparison.OrdinalIgnoreCase))
                throw new SubtitleServiceException("下载内容无效，请换一份字幕。");
            using var stream = await response.Content.ReadAsStreamAsync(readToken).ConfigureAwait(false);
            long total = response.Content.Headers.ContentLength ?? 0;
            int lastPercent = -1;
            long lastUpdate = System.Diagnostics.Stopwatch.GetTimestamp();
            byte[] bytes = await SubtitleHttp.ReadBytesAsync(stream, limit, readToken, received =>
            {
                if (total <= 0)
                {
                    if (System.Diagnostics.Stopwatch.GetElapsedTime(lastUpdate).TotalSeconds < 1) return;
                    lastUpdate = System.Diagnostics.Stopwatch.GetTimestamp();
                    progress?.Report($"下载字幕 {received / 1024} KB");
                    return;
                }
                int percent = (int)Math.Min(100, received * 100L / total) / 10 * 10;
                if (percent != lastPercent) { lastPercent = percent; progress?.Report($"下载字幕 {percent}%"); }
            }).ConfigureAwait(false);
            if (total > 0 && bytes.LongLength != total)
                throw new HttpRequestException(HttpRequestError.ResponseEnded, "Subtitle response was incomplete.");
            if (lastPercent != 100) progress?.Report("下载字幕 100%");
            if (bytes.Length == 0) throw new SubtitleServiceException("下载的字幕文件为空。");
            return bytes;
        }, token, rateLimited: false).ConfigureAwait(false);
    }

    static async Task WriteAsync(string target, byte[] bytes, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            await File.WriteAllBytesAsync(target + ".tmp", bytes, token).ConfigureAwait(false);
            File.Move(target + ".tmp", target, true);
            MpvNet.Diagnostics.Write("subtitle.download", "file-written", new { bytes = bytes.Length, format = Path.GetExtension(target) });
        }
        catch { File.Delete(target + ".tmp"); throw; }
    }
}

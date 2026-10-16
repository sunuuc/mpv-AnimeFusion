using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

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
        if (!result.Local)
        {
            if (query.Season == null && Supported(result.DownloadName))
            {
                var movieFile = new SubtitleFile(result.Source, result.Id, 0, result.DownloadName, "");
                if (library.Cached(query, result, movieFile) is { } cached) return cached;
                string moviePath = library.FilePath(query, result, movieFile.Name);
                await WriteAsync(moviePath, await FetchAsync(result.DownloadUrl, 32 * 1024 * 1024, token, progress).ConfigureAwait(false), token).ConfigureAwait(false);
                library.Save(query, result, movieFile, moviePath, true, true);
                return moviePath;
            }
            if (!PackageSupported(result.DownloadName))
                throw new SubtitleServiceException("请选择整包字幕。");
            string archivePath = library.ArchivePath(query, result);
            if (!File.Exists(archivePath) || new FileInfo(archivePath).Length == 0)
            {
                progress?.Report("正在下载字幕包…");
                byte[] bytes = await FetchAsync(result.DownloadUrl, 64 * 1024 * 1024, token, progress).ConfigureAwait(false);
                using var stream = new MemoryStream(bytes);
                if (!ArchiveFactory.IsArchive(stream, out var type) || type is not (SharpCompress.Common.ArchiveType.Zip or SharpCompress.Common.ArchiveType.Rar or SharpCompress.Common.ArchiveType.SevenZip))
                    throw new SubtitleServiceException("字幕包无法解压。");
                await WriteAsync(archivePath, bytes, token).ConfigureAwait(false);
            }
            var package = library.Package(query, result);
            if (package?.Complete != true || package.Files.Any(file => !File.Exists(file.Path)))
                if (!await Task.Run(() => ExtractAsync(query, result, archivePath, progress, token), token).ConfigureAwait(false))
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
        library.Complete(query, result);
        return path;
    }

    async Task<bool> ExtractAsync(SubtitleQuery query, SubtitleResult result, string archivePath, IProgress<string>? progress, CancellationToken token)
    {
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
            result = result with { PackageNames = entries.Where(entry => !entry.IsDirectory && names.Name(entry) is { } name && Supported(name)).Select(entry => names.Name(entry)!).ToArray() };
            var collection = result with { Collection = true };
            if (result.PackageNames.Select(name => SubtitleLibrary.FileQuery(query, collection, name).Season).Where(season => season >= 0).Distinct().Count() > 1)
                result = collection;
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
            int total = entries.Count(entry => !entry.IsDirectory && names.Name(entry) is { } name && Supported(name)), lastPercent = -1;
            async Task SaveEntryAsync(SharpCompress.Common.IEntry entry, Func<Stream> open)
            {
                token.ThrowIfCancellationRequested();
                string? name = names.Name(entry);
                if (entry.IsDirectory || name == null || !Supported(name)) return;
                var file = new SubtitleFile(result.Source, result.Id, 0, name, "");
                string target = library.FilePath(query, result, file.Name);
                if (library.Cached(query, result, file) == null)
                {
                    using var stream = open();
                    byte[] bytes = await SubtitleHttp.ReadBytesAsync(stream, 32 * 1024 * 1024, token).ConfigureAwait(false);
                    if (bytes.Length == 0) return;
                    await WriteAsync(target, bytes, token).ConfigureAwait(false);
                    library.Save(query, result, file, target, false);
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
            if (count == 0) throw new SubtitleServiceException("字幕包里没有可用字幕。");
            library.Complete(query, result);
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

    async Task<byte[]> FetchAsync(string url, int limit, CancellationToken token, IProgress<string>? progress)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            throw new SubtitleServiceException("字幕下载链接无效。");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", "mpv-AnimeFusion v" + MpvNet.AppInfo.Version.ToString(3));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new SubtitleServiceException("字幕下载失败，请稍后重试。");
        string type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (type.Contains("html", StringComparison.OrdinalIgnoreCase) || type.Contains("json", StringComparison.OrdinalIgnoreCase))
            throw new SubtitleServiceException("下载内容无效，请换一份字幕。");
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        long total = response.Content.Headers.ContentLength ?? 0;
        int lastPercent = -1;
        long lastUpdate = System.Diagnostics.Stopwatch.GetTimestamp();
        byte[] bytes = await SubtitleHttp.ReadBytesAsync(stream, limit, token, received =>
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
        if (lastPercent != 100) progress?.Report("下载字幕 100%");
        if (bytes.Length == 0) throw new SubtitleServiceException("下载的字幕文件为空。");
        return bytes;
    }

    static async Task WriteAsync(string target, byte[] bytes, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            await File.WriteAllBytesAsync(target + ".tmp", bytes, token).ConfigureAwait(false);
            File.Move(target + ".tmp", target, true);
        }
        catch { File.Delete(target + ".tmp"); throw; }
    }
}

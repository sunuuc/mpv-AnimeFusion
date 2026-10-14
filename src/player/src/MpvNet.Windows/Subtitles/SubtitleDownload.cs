using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;

namespace MpvNet.Windows.Subtitles;

public sealed class SubtitleDownload(HttpClient http, SubtitleLibrary library)
{
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
                await Task.Run(() => ExtractAsync(query, result, archivePath, progress, token), token).ConfigureAwait(false);
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

    async Task ExtractAsync(SubtitleQuery query, SubtitleResult result, string archivePath, IProgress<string>? progress, CancellationToken token)
    {
        progress?.Report("正在解压字幕包…");
        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            var entries = archive.Entries.ToArray();
            result = result with { PackageNames = entries.Where(entry => !entry.IsDirectory && entry.Key != null && Supported(entry.Key)).Select(entry => entry.Key!).ToArray() };
            var collection = result with { Collection = true };
            if (result.PackageNames.Select(name => SubtitleLibrary.FileQuery(query, collection, name).Season).Where(season => season >= 0).Distinct().Count() > 1)
                result = collection;
            if (entries.Length > 4000 || entries.Sum(entry => entry.Size) > 256 * 1024 * 1024L)
                throw new SubtitleServiceException("字幕包过大。");
            foreach (var entry in entries.Where(entry => !entry.IsDirectory))
            {
                if (entry.LinkTarget != null || entry.IsEncrypted) throw new SubtitleServiceException("不支持这份字幕包。");
                if (entry.Key != null) library.FilePath(query, result, entry.Key);
            }
            int count = 0;
            int total = entries.Count(entry => !entry.IsDirectory && entry.Key != null && Supported(entry.Key)), lastPercent = -1;
            async Task SaveEntryAsync(SharpCompress.Common.IEntry entry, Func<Stream> open)
            {
                token.ThrowIfCancellationRequested();
                if (entry.IsDirectory || entry.Key == null || !Supported(entry.Key)) return;
                var file = new SubtitleFile(result.Source, result.Id, 0, entry.Key, "");
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
        }
        catch (Exception error) when (error is SharpCompress.Common.SharpCompressException or InvalidDataException)
        { throw new SubtitleServiceException("字幕包无法解压。"); }
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

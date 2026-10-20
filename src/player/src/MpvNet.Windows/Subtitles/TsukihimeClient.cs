using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MpvNet.Windows.Bangumi;
using static MpvNet.Windows.Subtitles.SubtitleHttp;

namespace MpvNet.Windows.Subtitles;

public sealed class TsukihimeClient(HttpClient http) : ISubtitleClient
{
    const int PageSize = 50;
    readonly ConcurrentDictionary<long, JsonElement> _details = new();
    public SubtitleSource Source => SubtitleSource.Tsukihime;

    async Task<JsonDocument> RequestAsync(string path, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.tsukihime.org/v1/" + path);
        var document = await JsonAsync(http, request, "TsukiHime", token).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || Object(document.RootElement, "error").ValueKind == JsonValueKind.True)
        {
            document.Dispose();
            throw new SubtitleServiceException("TsukiHime：返回结果异常，请重试。");
        }
        return document;
    }

    public async Task<SubtitlePage> SearchAsync(SubtitleQuery query, int page, CancellationToken token,
        IProgress<IReadOnlyList<SubtitleResult>>? progress = null)
    {
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        if (page == 1) _details.Clear();
        string name = query.Title.Normalize(NormalizationForm.FormKC).Trim();
        if (name.Length < 2) throw new SubtitleServiceException("TsukiHime 搜索词至少需要两个字符。");
        int offset = checked((page - 1) * PageSize);
        using var document = await RequestAsync("search/torrents?q=" + Uri.EscapeDataString(name)
            + "&limit=" + PageSize + "&offset=" + offset, token).ConfigureAwait(false);
        var root = document.RootElement;
        if (Object(root, "results").ValueKind != JsonValueKind.Array
            || Number(root, "start") != offset)
            throw new SubtitleServiceException("TsukiHime：返回结果异常，请重试。");
        var items = Array(root, "results").ToArray();
        if (items.Length > 0 && items.All(item => Id(item) <= 0))
            throw new SubtitleServiceException("TsukiHime：返回结果异常，请重试。");
        bool more = items.Length > 0 && offset + items.Length < Number(root, "total");
        var results = items.SelectMany(item => Results(item, query.Language)).ToArray();
        token.ThrowIfCancellationRequested();
        return new(results, more) { PageKey = string.Join(',', items.Select(Id)) };
    }

    static IEnumerable<SubtitleResult> Results(JsonElement item, string language)
    {
        long id = Id(item);
        string title = Text(item, "name");
        if (id <= 0 || title.Length == 0 || Number(item, "filecount") != 1
            || Text(item, "state") != "completed") yield break;
        var release = BangumiMedia.FromPlayback(title, title);
        var anime = Object(item, "anime");
        int? season = Season(title, release) ?? AnimeNames(anime)
            .Select(name => Season(name, BangumiMedia.Parse(name))).FirstOrDefault(value => value != null);
        double? position = release.EpisodeNumber;
        int? episode = position is >= 0 and <= int.MaxValue && position == Math.Truncate(position.Value)
            ? (int)position.Value : EpisodeNumber(item);
        if (season == null && episode != null) season = 1;
        if (release.Ambiguous) { season = -1; episode = null; }
        string date = Date(item);
        foreach (string code in Array(item, "sublangs").Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()!.Trim()).Where(value => Regex.IsMatch(value, @"^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$"))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(LanguageOrder))
        {
            string category = LanguageCode(code);
            if (language.Length > 0 && category != language && SubtitleLanguage.Find(category)?.Matches(language) != true) continue;
            string label = LanguageName(code);
            yield return new(SubtitleSource.Tsukihime, id.ToString(CultureInfo.InvariantCulture) + ":" + code,
                title, label, string.Join(" · ", new[] { label, date }.Where(value => value.Length > 0)), [])
            { Individual = true, Season = season, Episode = episode, LanguageCode = category, TitleNames = AnimeNames(anime) };
        }
    }

    static string[] AnimeNames(JsonElement anime) => new[] { Text(anime, "title"), Text(anime, "english_title") }
        .Select(name => name.Trim()).Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    static int? Season(string title, BangumiMedia media) => SubtitleQuery.IsSpecial(title) ? 0 : media.HasSeason ? media.Season : null;

    static long Id(JsonElement value) => Object(value, "id") is var id
        && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out long number) ? number : 0;

    static int? EpisodeNumber(JsonElement item) => Object(item, "episode_no") is var episode
        && episode.ValueKind == JsonValueKind.Number && episode.TryGetInt32(out int number) && number >= 0 ? number : null;

    static string Date(JsonElement item)
    {
        var date = Object(item, "source_date");
        return date.ValueKind == JsonValueKind.Number && date.TryGetInt64(out long seconds)
            && seconds is >= 0 and <= 253402300799
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
    }

    static int LanguageOrder(string code) => code.ToLowerInvariant() switch
    {
        "zh-hans" or "zh-cn" or "zh-sg" => 0,
        "zh-hant" or "zh-tw" or "zh-hk" => 1,
        "zh" or "zho" or "chi" => 2,
        "ja" or "jpn" => 3,
        "en" or "eng" => 4,
        _ => 5
    };

    static string LanguageCode(string code)
    {
        return SubtitleLanguage.Find(code)?.Code ?? "other";
    }

    static string LanguageName(string code)
    {
        if (LanguageOrder(code) == 0) return "简体中文";
        if (LanguageOrder(code) == 1) return "繁体中文";
        if (LanguageOrder(code) == 2) return "中文";
        try { return CultureInfo.GetCultureInfo(code).DisplayName; }
        catch (CultureNotFoundException) { return SubtitleLanguage.Find(code)?.Name ?? "其他"; }
    }

    public async Task<SubtitleResult> DetailAsync(SubtitleResult result, CancellationToken token)
    {
        int separator = result.Id.IndexOf(':');
        if (result.Source != Source || separator <= 0
            || !long.TryParse(result.Id.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0)
            throw new SubtitleServiceException("TsukiHime：字幕编号无效。");
        string language = result.Id[(separator + 1)..];
        if (!_details.TryGetValue(id, out var detail))
        {
            using var document = await RequestAsync("torrents/" + id.ToString(CultureInfo.InvariantCulture), token).ConfigureAwait(false);
            detail = document.RootElement.Clone();
            token.ThrowIfCancellationRequested();
            if (Id(detail) != id) throw new SubtitleServiceException("TsukiHime：返回结果异常，请重试。");
            _details[id] = detail;
        }
        var files = Array(detail, "files").ToArray();
        if (files.Length != 1) throw new SubtitleServiceException("TsukiHime：这份资源没有可单独下载的字幕。");
        var file = files[0];
        string filename = Path.GetFileName(Text(file, "filename").Replace('\\', '/'));
        if (filename.Length == 0) throw new SubtitleServiceException("TsukiHime：字幕文件名缺失。");
        string releaseTitle = Text(detail, "name");
        var release = BangumiMedia.FromPlayback(releaseTitle, releaseTitle);
        var media = BangumiMedia.FromPlayback(filename, filename);
        var anime = Object(detail, "anime");
        int?[] seasons = new[] { Season(releaseTitle, release), Season(filename, media),
                Season(result.Title, BangumiMedia.FromPlayback(result.Title, result.Title)) }
            .Where(value => value != null).Distinct().ToArray();
        if (seasons.Length == 0)
            seasons = AnimeNames(anime).Select(name => Season(name, BangumiMedia.Parse(name)))
                .Append(result.Season).Where(value => value != null).Distinct().ToArray();
        int?[] episodes = new[] { release.EpisodeNumber, media.EpisodeNumber }
            .Where(value => value is >= 0 and <= int.MaxValue && value == Math.Truncate(value.Value))
            .Select(value => (int?)value).Append(EpisodeNumber(detail)).Append(result.Episode)
            .Where(value => value != null).Distinct().ToArray();
        if (release.Ambiguous || media.Ambiguous || seasons.Length > 1 || episodes.Length > 1)
            throw new SubtitleServiceException("TsukiHime：字幕季集信息不一致，请选择其他版本。");
        bool animeTosho = Object(detail, "animetosho").ValueKind == JsonValueKind.True;
        var tracks = Array(file, "attachments").Where(attachment => Number(attachment, "type") == 1 && Id(attachment) > 0)
            .Select(attachment => (Attachment: attachment, Info: Object(attachment, "info")))
            .Where(track => Text(track.Info, "lang").Equals(language, StringComparison.OrdinalIgnoreCase)
                // Mirrored AnimeTosho attachments do not carry TsukiHime's local cache flag.
                && (animeTosho || Number(track.Info, "cached") == 1) && Number(track.Info, "forced") != 1
                && !Regex.IsMatch(Text(track.Info, "name"), @"\b(?:signs?|songs?|forced)\b", RegexOptions.IgnoreCase)
                && Extension(track.Info).Length > 0)
            .OrderByDescending(track => Number(track.Info, "default"))
            .ThenBy(track => Regex.IsMatch(Text(track.Info, "name"), @"\b(?:CC|SDH)\b", RegexOptions.IgnoreCase)).ToArray();
        if (tracks.Length == 0) throw new SubtitleServiceException("TsukiHime：这份资源没有该语言的完整文字字幕。");
        var selected = tracks[0];
        long attachmentId = Id(selected.Attachment);
        string extension = Extension(selected.Info);
        string name = Path.GetFileNameWithoutExtension(filename) + "." + language + extension;
        string prefix = animeTosho ? "/tosho/attach/" : "/attach/";
        string url = "https://storage.tsukihime.org" + prefix + attachmentId.ToString("x8", CultureInfo.InvariantCulture)
            + "/" + attachmentId.ToString(CultureInfo.InvariantCulture) + ".xz";
        string[] titles = AnimeNames(anime);
        int mal = Number(anime, "mal"), year = Number(anime, "release_year");
        bool? movie = Object(anime, "is_movie") is { ValueKind: JsonValueKind.Number } kind
            && kind.TryGetInt32(out int value) && value is 0 or 1 ? value == 1 : null;
        return result with { DownloadName = name + ".xz", DownloadUrl = url, Format = extension[1..].ToUpperInvariant(),
            PackageNames = [name], Individual = true, Season = seasons.FirstOrDefault(), Episode = episodes.FirstOrDefault(),
            TitleNames = titles.Length > 0 ? titles : result.TitleNames, MalId = mal > 0 ? mal : null,
            Year = year is >= 1000 and <= 9999 ? year : null, IsMovie = movie };
    }

    static string Extension(JsonElement info) => Text(info, "codec").ToLowerInvariant() switch
    {
        "ass" => ".ass",
        "ssa" => ".ssa",
        "srt" or "subrip" => ".srt",
        _ => ""
    };
}

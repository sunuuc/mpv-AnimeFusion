using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Text;
using MpvNet.Windows.Bangumi;
using static MpvNet.Windows.Subtitles.SubtitleHttp;

namespace MpvNet.Windows.Subtitles;

public sealed class AssrtClient(HttpClient http, SubtitleStore store)
{
    sealed class InvalidResponseException() : SubtitleServiceException("ASSRT：返回结果异常，请重试。");
    readonly ConcurrentDictionary<SubtitleQuery, string> _searchNames = new();
    async Task<JsonDocument> RequestAsync(string path, CancellationToken token, string? access = null, HttpContent? content = null)
    {
        access ??= store.Credentials.AssrtToken;
        access = access.Trim();
        if (access.Length == 0) throw new SubtitleServiceException("ASSRT：请填写 API 密钥。");
        if (access.Length != 32 || !access.All(char.IsAsciiLetterOrDigit))
            throw new SubtitleServiceException("ASSRT：API 密钥格式不正确。");
        using var request = new HttpRequestMessage(content == null ? HttpMethod.Get : HttpMethod.Post, "https://api.assrt.net/v1/" + path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        if (content != null)
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        var document = await JsonAsync(http, request, "ASSRT", token).ConfigureAwait(false);
        if (Object(document.RootElement, "status").ValueKind != JsonValueKind.Number)
        { document.Dispose(); throw new SubtitleServiceException("ASSRT 返回了无效的响应。"); }
        int status = Number(document.RootElement, "status");
        if (status != 0)
        {
            document.Dispose();
            throw new SubtitleServiceException(status switch
            {
                20001 => "ASSRT：API 密钥无效。",
                30900 => "ASSRT：请求太频繁，请稍后重试。",
                _ => "ASSRT：服务暂时不可用，请稍后重试。"
            });
        }
        return document;
    }

    public async Task ValidateTokenAsync(string access, CancellationToken token)
    { using var response = await RequestAsync("user/quota", token, access).ConfigureAwait(false); }

    public async Task<SubtitlePage> SearchAsync(SubtitleQuery query, int page, CancellationToken token)
    {
        var names = new[] { query.Title }.Concat(query.Names).Select(s => Regex.Replace(s.Normalize(NormalizationForm.FormKC), @"[\s\p{P}\p{S}]+", " ").Trim())
            .Where(s => s.Length >= 3).Distinct().OrderByDescending(s => Regex.IsMatch(s, @"\p{IsCJKUnifiedIdeographs}"))
            .Take(3).ToArray();
        if (names.Length == 0) throw new SubtitleServiceException("ASSRT 搜索词至少需要三个字符。");
        if (page > 1 && _searchNames.TryGetValue(query, out string? chosen)) names = [chosen];
        InvalidResponseException? invalid = null;
        foreach (string name in names)
        {
            try
            {
                var result = await SearchNameAsync(query, name, page, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (result.Results.Count > 0 || result.HasMore || page > 1)
                { _searchNames[query] = name; return result; }
            }
            catch (InvalidResponseException error) { invalid = error; }
        }
        if (invalid != null) throw invalid;
        return new([], false);
    }

    async Task<SubtitlePage> SearchNameAsync(SubtitleQuery query, string name, int page, CancellationToken token)
    {
        using var document = await RequestAsync("sub/search", token, content: new FormUrlEncodedContent(new Dictionary<string, string>
            { ["q"] = name, ["cnt"] = "15", ["pos"] = ((page - 1) * 15).ToString(), ["filelist"] = "1" })).ConfigureAwait(false);
        var items = Array(Object(document.RootElement, "sub"), "subs").ToArray();
        if (items.Length > 0 && !items.Any(s => Number(s, "id") > 0))
            throw new InvalidResponseException();
        var results = items.Where(s => LanguageMatches(s, query.Language) && Number(s, "id") > 0)
            .Select(s => new SubtitleResult(SubtitleSource.Assrt, Number(s, "id").ToString(),
                Title(s),
                Text(Object(s, "lang"), "desc"),
                Details(s), [])
                {
                    Format = Text(s, "subtype"),
                    LanguageCode = string.Join(",", Languages(s)),
                    Collection = Text(s, "videoname").Contains("合集", StringComparison.Ordinal)
                        || Text(s, "filename").Contains("合集", StringComparison.Ordinal),
                    PackageNames = Array(s, "filelist").Select(file => Text(file, "f")).Where(name => name.Length > 0).ToArray(),
                    Season = Season(Text(s, "native_name") is { Length: > 0 } nativeTitle ? nativeTitle : Text(s, "videoname"), query.Episode != null)
                }).ToArray();
        return new(results, items.Length == 15);
    }

    static int? Season(string title, bool episode)
    {
        if (Regex.IsMatch(title, @"\b(?:OVA|OAD|Special)\b|特典|特别篇|特別篇", RegexOptions.IgnoreCase)) return 0;
        var media = BangumiMedia.Parse(title);
        return media.HasSeason || episode ? media.Season : null;
    }

    static bool LanguageMatches(JsonElement value, string language)
        => language.Length == 0 || Languages(value).Contains(language);

    static IReadOnlyList<string> Languages(JsonElement value)
    {
        var lang = Object(value, "lang");
        var list = Object(lang, "langlist");
        var codes = new List<string>();
        if (list.ValueKind == JsonValueKind.Object)
            foreach (var flag in list.EnumerateObject().Where(flag => flag.Value.ValueKind == JsonValueKind.True))
            {
                string code = flag.Name.StartsWith("lang", StringComparison.Ordinal) ? flag.Name[4..] : flag.Name;
                if (code is "chs" or "cht") codes.Add("zh");
                else if (SubtitleLanguage.Find(code) is { } language) codes.Add(language.Code);
                else if (code != "dou") codes.Add("other");
            }
        string desc = Text(lang, "desc");
        if (desc.Contains('中') || desc.Contains('简') || desc.Contains('繁')) codes.Add("zh");
        if (desc.Contains('英')) codes.Add("en");
        if (desc.Contains('日')) codes.Add("ja");
        return codes.Count == 0 ? ["other"] : codes.Distinct().ToArray();
    }

    static string Title(JsonElement value) => string.Join("/", new[] { Text(value, "native_name"), Text(value, "videoname") }
        .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct());

    static string Details(JsonElement value)
    {
        var rows = new List<string>();
        void Add(string text) { if (!string.IsNullOrWhiteSpace(text)) rows.Add(text.Trim()); }
        Add(Text(Object(value, "lang"), "desc"));
        Add(Text(value, "upload_time").Split(' ')[0]);
        var size = Object(value, "size");
        if (size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out long bytes))
            Add(bytes >= 1024 * 1024 ? FormattableString.Invariant($"{bytes / (1024d * 1024):0.#} MB")
                : FormattableString.Invariant($"{bytes / 1024d:0.#} KB"));
        return string.Join(" · ", rows);
    }

    public async Task<SubtitleResult> DetailAsync(SubtitleResult result, CancellationToken token)
    {
        if (!int.TryParse(result.Id, out int id) || id <= 0) throw new SubtitleServiceException("ASSRT 字幕编号无效。");
        using var document = await RequestAsync("sub/detail", token, content: new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = id.ToString() })).ConfigureAwait(false);
        var detail = Array(Object(document.RootElement, "sub"), "subs").FirstOrDefault();
        string name = Text(detail, "filename"), url = Text(detail, "url");
        if (name.Length == 0 || url.Length == 0) throw new SubtitleServiceException("没有可下载的字幕包。");
        return result with { Title = Title(detail), Format = Text(detail, "subtype"), DownloadName = name, DownloadUrl = url, Details = Details(detail),
            PackageNames = Array(detail, "filelist").Select(file => Text(file, "f")).Where(filename => filename.Length > 0).ToArray(),
            Collection = Text(detail, "videoname").Contains("合集", StringComparison.Ordinal) || name.Contains("合集", StringComparison.Ordinal) };
    }
}

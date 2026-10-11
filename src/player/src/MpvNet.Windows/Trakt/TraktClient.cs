// Request and response contracts adapted from trakt/trakt-api (MIT).
// Copyright (c) 2024 Trakt Inc. See THIRD_PARTY_LICENSES/trakt-api-MIT.txt.
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace MpvNet.Windows.Trakt;

public sealed class TraktClient
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    readonly HttpClient _http;
    readonly TraktStore _store;
    readonly SemaphoreSlim _gate = new(1, 1);
    DateTimeOffset _writeAfter;
    public TraktOAuth OAuth { get; }
    public TraktAccount? Account { get; private set; }
    public string AccountError { get; private set; } = "";
    public event Action? AccountChanged;

    public TraktClient(TraktStore store, HttpClient http)
    {
        _store = store; _http = http; OAuth = new(http);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("mpv-AnimeFusion/1.3.1 (+https://github.com/sunuuc/mpv-AnimeFusion)");
        try { Account = store.ReadAccount(); }
        catch (Exception e) when (e is IOException or System.Security.Cryptography.CryptographicException or JsonException)
        { AccountError = "Trakt 登录信息无法读取，请重新登录。"; }
    }

    public async Task ConnectAsync(TraktAccount account, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var response = await SendAsync(HttpMethod.Get, "users/settings", null, account.AccessToken, token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token).ConfigureAwait(false);
            var user = document.RootElement.GetProperty("user");
            Account = account with { Username = user.GetProperty("username").GetString() ?? "",
                AvatarUrl = user.GetProperty("images").GetProperty("avatar").GetProperty("full").GetString() ?? "" };
            _store.SaveAccount(Account); AccountError = "";
        }
        finally { _gate.Release(); }
        AccountChanged?.Invoke();
    }

    public async Task DisconnectAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { _store.RemoveAccount(); Account = null; }
        finally { _gate.Release(); }
        AccountChanged?.Invoke();
    }

    async Task<T> RequestAsync<T>(HttpMethod method, string path, object? body, bool authenticated, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string? access = null;
            if (authenticated)
            {
                var account = Account ?? throw new InvalidOperationException("请先登录 Trakt。");
                if (account.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
                {
                    try
                    {
                        var refreshed = await OAuth.RefreshAsync(account, token).ConfigureAwait(false);
                        // Trakt refresh tokens are single use. Persist the rotated pair before any other request.
                        Account = refreshed; _store.SaveAccount(refreshed); AccountChanged?.Invoke(); account = refreshed;
                    }
                    catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
                    {
                        Account = null; _store.RemoveAccount(); AccountChanged?.Invoke();
                        throw new InvalidOperationException("Trakt 登录已过期，请重新登录。", e);
                    }
                }
                access = account.AccessToken;
            }
            using var response = await SendAsync(method, path, body, access, token).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<T>(Json, token).ConfigureAwait(false) ?? throw new JsonException("Trakt 未返回有效数据。");
        }
        catch (HttpRequestException error) when (authenticated && error.StatusCode == HttpStatusCode.Unauthorized)
        {
            Account = null; _store.RemoveAccount(); AccountChanged?.Invoke();
            throw new InvalidOperationException("Trakt 登录已失效，请重新授权。", error);
        }
        finally { _gate.Release(); }
    }

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string? access, CancellationToken token)
    {
        for (int retry = 0; ; retry++)
        {
            token.ThrowIfCancellationRequested();
            if (method != HttpMethod.Get && _writeAfter > DateTimeOffset.UtcNow)
                await Task.Delay(_writeAfter - DateTimeOffset.UtcNow, token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, "https://api.trakt.tv/" + path);
            request.Headers.Add("trakt-api-key", TraktOAuth.ClientId);
            request.Headers.Add("trakt-api-version", "2");
            request.Headers.Accept.Add(new("application/json"));
            if (access != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            if (body != null) request.Content = JsonContent.Create(body, options: Json);
            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, token).ConfigureAwait(false); }
            catch (Exception e) when (method == HttpMethod.Get && retry < 3 && !token.IsCancellationRequested && e is HttpRequestException or TaskCanceledException)
            { await Task.Delay(TimeSpan.FromMilliseconds(400 * (1 << retry)), token).ConfigureAwait(false); continue; }
            if (method != HttpMethod.Get) _writeAfter = DateTimeOffset.UtcNow.AddSeconds(1);
            if (response.IsSuccessStatusCode) return response;
            // Only replay reads and explicit rate-limit rejections; history writes aren't replayed after transport failures.
            bool transient = response.StatusCode == HttpStatusCode.TooManyRequests || method == HttpMethod.Get && (response.StatusCode == HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500);
            var delay = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : TimeSpan.FromMilliseconds(400 * (1 << Math.Min(retry, 3))));
            if (transient && retry < 3 && delay <= TimeSpan.FromSeconds(30))
            {
                response.Dispose(); await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1), token).ConfigureAwait(false); continue;
            }
            try { response.EnsureSuccessStatusCode(); }
            finally { response.Dispose(); }
        }
    }

    public async Task<IReadOnlyList<TraktSubject>> SearchAsync(string title, string kind, CancellationToken token)
    {
        string query = Uri.EscapeDataString(Regex.Replace(title, @"([+\-!(){}\[\]^""~*?:/\\&|])", @"\$1"));
        string suffix = "?query=" + query + "&extended=full&limit=20";
        var exact = await RequestAsync<TraktSearchResult[]>(HttpMethod.Get, "search/" + kind + "/exact" + suffix, null, false, token).ConfigureAwait(false);
        var broad = await RequestAsync<TraktSearchResult[]>(HttpMethod.Get, "search/" + kind + suffix, null, false, token).ConfigureAwait(false);
        return Subjects(exact.Concat(broad), kind);
    }
    static TraktSubject[] Subjects(IEnumerable<TraktSearchResult> items, string kind) => items.Where(i => i.Type == kind)
        .Select(i => kind == "movie" ? i.Movie : i.Show).OfType<TraktSubject>().Where(s => s.Ids.Trakt > 0)
        .Select(s => s with { Kind = kind }).DistinctBy(s => s.Ids.Trakt).ToArray();

    public async Task<TraktSubject?> MatchAsync(TraktMedia media, CancellationToken token)
    {
        if (media.Parsed.Ambiguous) return null;
        if (media.Ids.Count > 0)
        {
            // All supplied identifiers must identify the same object; conflicting IDs require manual confirmation.
            TraktSubject? selected = null;
            foreach (var id in media.Ids)
            {
                var results = await RequestAsync<TraktSearchResult[]>(HttpMethod.Get, "search/" + id.Key + "/" + id.Value + "?type=" + media.Kind + "&extended=full", null, false, token).ConfigureAwait(false);
                var subjects = Subjects(results, media.Kind);
                if (subjects.Length != 1 || selected != null && selected.Ids.Trakt != subjects[0].Ids.Trakt) return null;
                selected = subjects[0];
            }
            return selected;
        }
        var candidates = await SearchAsync(media.Title, media.Kind, token).ConfigureAwait(false);
        var ranked = new List<(TraktSubject Subject, double Score)>();
        foreach (var subject in candidates.Take(10))
        {
            if (media.Kind == "movie" && media.Parsed.Year != null && subject.Year != media.Parsed.Year) continue;
            double score = media.Score(subject.Title);
            string prefix = (media.Kind == "movie" ? "movies/" : "shows/") + subject.Ids.Trakt;
            // Trakt's search can match translated or alternate titles. Confirm those names before selecting.
            if (score < 1)
            {
                var aliases = await RequestAsync<JsonElement[]>(HttpMethod.Get, prefix + "/aliases", null, false, token).ConfigureAwait(false);
                var translations = await RequestAsync<JsonElement[]>(HttpMethod.Get, prefix + "/translations", null, false, token).ConfigureAwait(false);
                foreach (var name in aliases.Concat(translations))
                    if (name.TryGetProperty("title", out var title)) score = Math.Max(score, media.Score(title.GetString() ?? ""));
            }
            if (score >= .8) ranked.Add((subject, score));
        }
        ranked.Sort((a, b) => b.Score.CompareTo(a.Score));
        return ranked.Count > 0 && (ranked.Count == 1 || ranked[0].Score - ranked[1].Score >= .08) ? ranked[0].Subject : null;
    }

    public Task<TraktSubject> SubjectAsync(int id, string kind, CancellationToken token) => RequestAsync<TraktSubject>(HttpMethod.Get,
        (kind == "movie" ? "movies/" : "shows/") + id + "?extended=full", null, false, token);
    public Task<TraktSeason[]> SeasonsAsync(int id, CancellationToken token) => RequestAsync<TraktSeason[]>(HttpMethod.Get, "shows/" + id + "/seasons?extended=full", null, false, token);
    public Task<TraktEpisode[]> EpisodesAsync(int id, int season, CancellationToken token) => RequestAsync<TraktEpisode[]>(HttpMethod.Get, "shows/" + id + "/seasons/" + season + "?extended=full", null, false, token);

    public async Task<HashSet<int>> WatchedAsync(TraktSubject subject, IReadOnlyList<TraktEpisode> episodes, int season, CancellationToken token)
    {
        var watched = new HashSet<int>();
        if (subject.Kind == "movie")
        {
            var history = await RequestAsync<JsonElement[]>(HttpMethod.Get, "sync/history/movies/" + subject.Ids.Trakt + "?limit=1", null, true, token).ConfigureAwait(false);
            if (history.Length > 0) watched.Add(subject.Ids.Trakt);
        }
        else
        {
            var progress = await RequestAsync<JsonElement>(HttpMethod.Get, "shows/" + subject.Ids.Trakt + "/progress/watched?hidden=false&specials=true", null, true, token).ConfigureAwait(false);
            foreach (var item in progress.GetProperty("seasons").EnumerateArray().Where(s => s.GetProperty("number").GetInt32() == season))
                foreach (var ep in item.GetProperty("episodes").EnumerateArray().Where(e => e.GetProperty("completed").GetBoolean()))
                {
                    var episode = episodes.FirstOrDefault(e => e.Number == ep.GetProperty("number").GetInt32());
                    if (episode != null) watched.Add(episode.Ids.Trakt);
                }
        }
        return watched;
    }

    public async Task SetWatchedAsync(string kind, IReadOnlyList<int> ids, bool watched, CancellationToken token)
    {
        string key = kind == "movie" ? "movies" : "episodes";
        var body = new Dictionary<string, object> { [key] = ids.Distinct().Select(id => new { ids = new { trakt = id }, watched_at = DateTime.UtcNow }).ToArray() };
        var result = await RequestAsync<JsonElement>(HttpMethod.Post, "sync/history" + (watched ? "" : "/remove"), body, true, token).ConfigureAwait(false);
        if (result.TryGetProperty("not_found", out var missing) && missing.TryGetProperty(key, out var entries) && entries.GetArrayLength() > 0)
            throw new InvalidOperationException("Trakt 未找到所选剧集，请重新匹配。");
    }

    public Task<string> AvatarFileAsync(CancellationToken token) => ImageFileAsync(Account?.AvatarUrl ?? "", true, token);
    public Task<string> CoverFileAsync(string url, CancellationToken token) => ImageFileAsync(url, false, token);
    async Task<string> ImageFileAsync(string url, bool avatar, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (!url.Contains("://")) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return "";
        string path = _store.ImagePath(url, avatar);
        int width = avatar ? 64 : 240, height = avatar ? 64 : 360;
        if (File.Exists(path) && new FileInfo(path).Length == width * height * 4) return path;
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        const int limit = 4 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new IOException("Trakt 图片过大。");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var data = new MemoryStream(); byte[] buffer = new byte[8192]; int size;
        while ((size = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        { if (data.Length + size > limit) throw new IOException("Trakt 图片过大。"); await data.WriteAsync(buffer.AsMemory(0, size), token).ConfigureAwait(false); }
        data.Position = 0;
        using var codec = SKCodec.Create(data) ?? throw new IOException("Trakt 图片格式无法读取。");
        if ((long)codec.Info.Width * codec.Info.Height > 16_000_000) throw new IOException("Trakt 图片尺寸过大。");
        using var source = SKBitmap.Decode(codec) ?? throw new IOException("Trakt 图片解码失败。");
        using var image = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(image))
        {
            canvas.Clear(SKColors.Transparent);
            if (avatar)
            {
                using var circle = new SKPath(); circle.AddCircle(width / 2f, height / 2f, width / 2f);
                canvas.ClipPath(circle, antialias: true);
            }
            float scale = Math.Max((float)width / source.Width, (float)height / source.Height);
            float w = source.Width * scale, h = source.Height * scale;
            using var sourceImage = SKImage.FromBitmap(source);
            canvas.DrawImage(sourceImage, new SKRect((width-w)/2, (height-h)/2, (width+w)/2, (height+h)/2),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        }
        byte[] pixels = image.Bytes;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, pixels, token).ConfigureAwait(false); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }
}

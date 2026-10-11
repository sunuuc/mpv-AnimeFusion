using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace MpvNet.Windows.MyAnimeList;

public sealed class MalClient
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    readonly HttpClient _http;
    readonly MalStore _store;
    readonly SemaphoreSlim _gate = new(1, 1);
    DateTimeOffset _writeAfter;
    public MalOAuth OAuth { get; }
    public MalAccount? Account { get; private set; }
    public string AccountError { get; private set; } = "";
    public event Action? AccountChanged;

    public MalClient(MalStore store, HttpClient http)
    {
        _store = store; _http = http; OAuth = new(http);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("mpv-AnimeFusion/1.3.1 (+https://github.com/sunuuc/mpv-AnimeFusion)");
        try { Account = store.ReadAccount(); }
        catch (Exception e) when (e is IOException or System.Security.Cryptography.CryptographicException or JsonException)
        { AccountError = "MyAnimeList 登录信息无法读取，请重新登录。"; }
    }

    public async Task ConnectAsync(MalAccount account, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var response = await SendAsync(HttpMethod.Get, "users/@me?fields=picture", null, account.AccessToken, token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token).ConfigureAwait(false);
            var user = document.RootElement;
            Account = account with { Username = user.GetProperty("name").GetString() ?? "",
                AvatarUrl = user.TryGetProperty("picture", out var picture) ? picture.GetString() ?? "" : "" };
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

    async Task<T> RequestAsync<T>(HttpMethod method, string path, Dictionary<string, string>? body, bool authenticated, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string? access = null;
            if (authenticated)
            {
                var account = Account ?? throw new InvalidOperationException("请先登录 MyAnimeList。");
                if (account.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
                {
                    try
                    {
                        var refreshed = await OAuth.RefreshAsync(account, token).ConfigureAwait(false);
                        // Persist the rotated token pair before the next request.
                        Account = refreshed; _store.SaveAccount(refreshed); AccountChanged?.Invoke(); account = refreshed;
                    }
                    catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
                    {
                        Account = null; _store.RemoveAccount(); AccountChanged?.Invoke();
                        throw new InvalidOperationException("MyAnimeList 登录已过期，请重新登录。", e);
                    }
                }
                access = account.AccessToken;
            }
            using var response = await SendAsync(method, path, body, access, token).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<T>(Json, token).ConfigureAwait(false) ?? throw new JsonException("MyAnimeList 未返回有效数据。");
        }
        catch (HttpRequestException error) when (authenticated && error.StatusCode == HttpStatusCode.Unauthorized)
        {
            Account = null; _store.RemoveAccount(); AccountChanged?.Invoke();
            throw new InvalidOperationException("MyAnimeList 登录已失效，请重新授权。", error);
        }
        finally { _gate.Release(); }
    }

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Dictionary<string, string>? body, string? access, CancellationToken token)
    {
        for (int retry = 0; ; retry++)
        {
            token.ThrowIfCancellationRequested();
            if (method != HttpMethod.Get && _writeAfter > DateTimeOffset.UtcNow)
                await Task.Delay(_writeAfter - DateTimeOffset.UtcNow, token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, "https://api.myanimelist.net/v2/" + path);
            request.Headers.Add("X-MAL-CLIENT-ID", MalOAuth.ClientId);
            request.Headers.Accept.Add(new("application/json"));
            if (access != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            if (body != null) request.Content = new FormUrlEncodedContent(body);
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

    const string Fields = "alternative_titles,start_date,media_type,num_episodes,mean,related_anime";
    public async Task<IReadOnlyList<MalSubject>> SearchAsync(string title, CancellationToken token)
    {
        var results = new List<MalSubject>();
        var names = await Task.Run(() => MalMatching.SearchNames(title).ToArray(), token).ConfigureAwait(false);
        foreach (string name in names)
        {
            var response = await RequestAsync<MalSearchResponse>(HttpMethod.Get,
                "anime?q=" + Uri.EscapeDataString(name) + "&limit=30&fields=" + Fields, null, false, token).ConfigureAwait(false);
            results.AddRange(response.Data.Select(n => n.Node));
        }
        return results.DistinctBy(s => s.Id).ToArray();
    }
    public Task<MalSubject> SubjectAsync(int id, bool authenticated, CancellationToken token) => RequestAsync<MalSubject>(HttpMethod.Get,
        "anime/" + id + "?fields=" + Fields + (authenticated ? ",my_list_status" : ""), null, authenticated, token);
    public async Task<MalListStatus> SetProgressAsync(MalSubject subject, int count, bool automatic, CancellationToken token)
    {
        if (count < 0 || subject.NumEpisodes > 0 && count > subject.NumEpisodes) throw new InvalidOperationException("观看集数超出该作品范围。");
        // Re-read at the write threshold to preserve edits made on MAL during playback.
        var current = (await SubjectAsync(subject.Id, true, token).ConfigureAwait(false)).MyListStatus;
        if (automatic && current != null && current.NumEpisodesWatched >= count) return current;
        var values = new Dictionary<string, string> { ["num_watched_episodes"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (subject.NumEpisodes > 0 && count == subject.NumEpisodes) values["status"] = "completed";
        else if (current == null || current.Status == "plan_to_watch" || !automatic && current.Status == "completed") values["status"] = "watching";
        // PATCH only progress and the required status transition; retain score, tags, dates and rewatch settings.
        return await RequestAsync<MalListStatus>(HttpMethod.Patch, "anime/" + subject.Id + "/my_list_status", values, true, token).ConfigureAwait(false);
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
        if (response.Content.Headers.ContentLength > limit) throw new IOException("MyAnimeList 图片过大。");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var data = new MemoryStream(); byte[] buffer = new byte[8192]; int size;
        while ((size = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        { if (data.Length + size > limit) throw new IOException("MyAnimeList 图片过大。"); await data.WriteAsync(buffer.AsMemory(0, size), token).ConfigureAwait(false); }
        data.Position = 0;
        using var codec = SKCodec.Create(data) ?? throw new IOException("MyAnimeList 图片格式无法读取。");
        if ((long)codec.Info.Width * codec.Info.Height > 16_000_000) throw new IOException("MyAnimeList 图片尺寸过大。");
        using var source = SKBitmap.Decode(codec) ?? throw new IOException("MyAnimeList 图片解码失败。");
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

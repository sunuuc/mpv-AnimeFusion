using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace MpvNet.Windows.Subtitles;

internal static class SubtitleHttp
{
    static readonly RequestRate AssrtRate = new(TimeSpan.FromSeconds(13));
    static readonly RequestRate AmenzbRate = new(TimeSpan.FromSeconds(1));
    static readonly RequestRate TsukihimeRate = new(TimeSpan.FromSeconds(1));
    static long _requestId;
    public static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
    public static int Number(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int number) ? number : 0;
    public static JsonElement Object(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var item) ? item : default;
    public static IEnumerable<JsonElement> Array(JsonElement value, string key) => Object(value, key) is var item
        && item.ValueKind == JsonValueKind.Array ? item.EnumerateArray() : [];

    public static async Task<JsonDocument> JsonAsync(HttpClient http, HttpRequestMessage request, string source, CancellationToken token)
    {
        return await ReadAsync(http, request, source, async (response, readToken) =>
        {
            if (!response.IsSuccessStatusCode)
            {
                string reason = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "API 密钥无效。",
                    HttpStatusCode.Forbidden => "请求被拒绝，请检查密钥或账号。",
                    HttpStatusCode.TooManyRequests => "请求太频繁，请稍后重试。",
                    (HttpStatusCode)406 => "无法下载，请检查剩余配额或换一份字幕。",
                    _ => "服务暂时不可用，请稍后重试。"
                };
                throw new SubtitleServiceException(source + "：" + reason);
            }
            using var stream = await response.Content.ReadAsStreamAsync(readToken).ConfigureAwait(false);
            return JsonDocument.Parse(await ReadBytesAsync(stream, 2 * 1024 * 1024, readToken).ConfigureAwait(false));
        }, token).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(HttpClient http, HttpRequestMessage request, string source,
        Func<HttpResponseMessage, CancellationToken, Task<T>> read, CancellationToken token, bool rateLimited = true)
    {
        long id = Interlocked.Increment(ref _requestId);
        byte[]? body = request.Content == null ? null : await request.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        MpvNet.Diagnostics.Write("subtitle.http", "start", new { id, source, method = request.Method.Method,
            host = request.RequestUri?.Host, rateLimited, timeoutSeconds = http.Timeout.TotalSeconds });
        for (int attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            using var current = new HttpRequestMessage(request.Method, request.RequestUri)
                { Version = request.Version, VersionPolicy = request.VersionPolicy };
            foreach (var header in request.Headers) current.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (body != null)
            {
                current.Content = new ByteArrayContent(body);
                foreach (var header in request.Content!.Headers)
                    current.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            long started = Stopwatch.GetTimestamp();
            try
            {
                MpvNet.Diagnostics.Write("subtitle.http", "attempt", new { id, attempt });
                using var response = await SendAsync(http, current, source, token, rateLimited, id, attempt).ConfigureAwait(false);
                MpvNet.Diagnostics.Write("subtitle.http", "headers", new { id, attempt, status = (int)response.StatusCode,
                    type = response.Content.Headers.ContentType?.MediaType, length = response.Content.Headers.ContentLength,
                    elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
                if (TransientStatus(response.StatusCode))
                    throw new HttpRequestException("Subtitle service request failed.", null, response.StatusCode);
                // ResponseHeadersRead only applies HttpClient.Timeout to the headers.
                // Bound the body separately, without counting rate-limit waits.
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                if (http.Timeout != Timeout.InfiniteTimeSpan) readTimeout.CancelAfter(http.Timeout);
                T result = await read(response, readTimeout.Token).ConfigureAwait(false);
                MpvNet.Diagnostics.Write("subtitle.http", "complete", new { id, attempt,
                    elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
                return result;
            }
            catch (Exception error) when (attempt < 3 && !token.IsCancellationRequested && Transient(error))
            {
                MpvNet.Diagnostics.Write("subtitle.http", "retry", new { id, attempt, delaySeconds = attempt,
                    elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds }, error);
                try { await Task.Delay(TimeSpan.FromSeconds(attempt), token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                { MpvNet.Diagnostics.Write("subtitle.http", "cancelled", new { id, attempt, phase = "retry-delay" }); throw; }
            }
            catch (Exception error)
            {
                MpvNet.Diagnostics.Write("subtitle.http", token.IsCancellationRequested ? "cancelled" : "failed",
                    new { id, attempt, elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds }, error);
                if (error is IOException)
                    throw new HttpRequestException(HttpRequestError.ResponseEnded, "Subtitle response could not be read.", error);
                throw;
            }
        }
    }

    static bool TransientStatus(HttpStatusCode? status) => status is HttpStatusCode.RequestTimeout
        or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    static bool Transient(Exception error) => error.GetBaseException() is not System.Security.Authentication.AuthenticationException
        && error switch
    {
        OperationCanceledException => true,
        HttpRequestException http => TransientStatus(http.StatusCode) || http.StatusCode == null
            && http.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
                or HttpRequestError.ResponseEnded or HttpRequestError.HttpProtocolError,
        IOException => true,
        _ => false
    };

    static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, string source,
        CancellationToken token, bool rateLimited, long id, int attempt)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", "mpv-AnimeFusion v" + MpvNet.AppInfo.Version.ToString(3));
        if (request.Headers.Accept.Count == 0) request.Headers.Accept.Add(new("application/json"));
        var current = request;
        try
        {
            for (int redirects = 0; ; redirects++)
            {
                var rate = source switch { "ameNZB" => AmenzbRate, "TsukiHime" => TsukihimeRate, _ => AssrtRate };
                long waiting = Stopwatch.GetTimestamp();
                if (rateLimited) await rate.WaitAsync(token).ConfigureAwait(false);
                MpvNet.Diagnostics.Write("subtitle.http", "send", new { id, attempt, redirects,
                    waitMs = Stopwatch.GetElapsedTime(waiting).TotalMilliseconds });
                long sent = Stopwatch.GetTimestamp();
                var response = await http.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                MpvNet.Diagnostics.Write("subtitle.http", "received", new { id, attempt, redirects,
                    status = (int)response.StatusCode, elapsedMs = Stopwatch.GetElapsedTime(sent).TotalMilliseconds });
                if (request.Method != HttpMethod.Get || response.StatusCode is not (HttpStatusCode.MovedPermanently
                    or HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;

                var location = response.Headers.Location;
                var origin = request.RequestUri!;
                response.Dispose();
                if (redirects >= 3 || location == null || !Uri.TryCreate(current.RequestUri, location, out var next)
                    || next.Scheme != Uri.UriSchemeHttps || next.Host != origin.Host || next.Port != origin.Port
                    || next.UserInfo.Length > 0 || next.Fragment.Length > 0 || next.AbsolutePath != origin.AbsolutePath)
                    throw new SubtitleServiceException("服务地址异常，请稍后重试。");

                if (current != request) current.Dispose();
                current = new(HttpMethod.Get, next);
                foreach (var header in request.Headers) current.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        finally { if (current != request) current.Dispose(); }
    }

    sealed class RequestRate(TimeSpan interval)
    {
        readonly SemaphoreSlim _gate = new(1, 1);
        long _lastRequest;
        public async Task WaitAsync(CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_lastRequest != 0)
                {
                    var remaining = interval - Stopwatch.GetElapsedTime(_lastRequest);
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                _lastRequest = Stopwatch.GetTimestamp();
            }
            finally { _gate.Release(); }
        }
    }

    public static async Task<byte[]> ReadBytesAsync(Stream stream, int limit, CancellationToken token, Action<int>? bytesRead = null)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > limit) throw new SubtitleServiceException("字幕服务返回的文件过大。");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            bytesRead?.Invoke((int)output.Length);
        }
        return output.ToArray();
    }
}

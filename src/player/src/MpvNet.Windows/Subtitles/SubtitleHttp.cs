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
        using var response = await SendAsync(http, request, source, token).ConfigureAwait(false);
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
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return JsonDocument.Parse(await ReadBytesAsync(stream, 2 * 1024 * 1024, token).ConfigureAwait(false));
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, string source, CancellationToken token)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", "mpv-AnimeFusion v" + MpvNet.AppInfo.Version.ToString(3));
        request.Headers.Accept.Add(new("application/json"));
        var current = request;
        try
        {
            for (int redirects = 0; ; redirects++)
            {
                await AssrtRate.WaitAsync(token).ConfigureAwait(false);
                var response = await http.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
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

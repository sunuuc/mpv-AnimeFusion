using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static MpvNet.Windows.Subtitles.SubtitleHttp;

namespace MpvNet.Windows.Subtitles;

internal static class EmbySubtitleMetadata
{
    internal sealed record Track(string Title, string Language);
    internal sealed record Source(Uri Request, string Item, string MediaSource, int Index, string UserAgent);
    static readonly Regex SubtitlePath = new(@"^(?<base>.*)/Videos/(?<item>[\w-]+)/(?<source>[\w-]+)/Subtitles/(?<index>\d+)/(?:\d+/)?Stream\.[\w]+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static Dictionary<string, string> Query(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);
            if (pair.Length == 2)
                result[Uri.UnescapeDataString(pair[0].Replace('+', ' '))] = Uri.UnescapeDataString(pair[1].Replace('+', ' '));
        }
        return result;
    }
    static bool HttpUri(string path, out Uri uri) => Uri.TryCreate(path, UriKind.Absolute, out uri!)
        && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0;

    public static Source? Identify(string subtitle, string video, string userAgent)
    {
        if (!HttpUri(subtitle, out var uri)) return null;
        var match = SubtitlePath.Match(uri.AbsolutePath);
        if (!match.Success || !int.TryParse(match.Groups["index"].Value, out int index)) return null;
        var query = Query(uri);
        // Only carry authentication from the current media when it belongs to
        // the same origin. Never forward a video token to another subtitle host.
        if (HttpUri(video, out var media) && media.Scheme == uri.Scheme && media.Authority == uri.Authority)
            foreach (var pair in Query(media))
                if (pair.Key.Equals("api_key", StringComparison.OrdinalIgnoreCase)
                    || pair.Key.Equals("UserId", StringComparison.OrdinalIgnoreCase)
                    || pair.Key.Equals("Emby-Authorization", StringComparison.OrdinalIgnoreCase)
                    || pair.Key.StartsWith("X-Emby-", StringComparison.OrdinalIgnoreCase)) query[pair.Key] = pair.Value;
        var authentication = query.Where(pair => pair.Key.Equals("api_key", StringComparison.OrdinalIgnoreCase)
            || pair.Key.Equals("UserId", StringComparison.OrdinalIgnoreCase)
            || pair.Key.Equals("Emby-Authorization", StringComparison.OrdinalIgnoreCase)
            || pair.Key.StartsWith("X-Emby-", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (!authentication.ContainsKey("api_key") && !authentication.ContainsKey("X-Emby-Token")) return null;
        // The caller's identity is already part of the authorized playback URL.
        // Preserve it for its media metadata rather than inventing another client.
        if (authentication.TryGetValue("X-Emby-Client", out string? client) && client.Length > 0)
        {
            authentication.TryGetValue("X-Emby-Client-Version", out string? version);
            userAgent = client + (string.IsNullOrEmpty(version) ? "" : "/" + version);
        }
        string prefix = match.Groups["base"].Value.TrimEnd('/');
        if (!prefix.EndsWith("/emby", StringComparison.OrdinalIgnoreCase)) prefix += "/emby";
        string item = match.Groups["item"].Value;
        // Item listings can omit alternate versions. PlaybackInfo returns the
        // media sources that own the supplied subtitle URLs without opening a stream.
        string path = prefix + "/Items/" + Uri.EscapeDataString(item) + "/PlaybackInfo";
        var request = new UriBuilder(uri) { Path = path, Fragment = "", Query = string.Join("&", authentication
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))) };
        return new(request.Uri, item, match.Groups["source"].Value, index, userAgent);
    }

    public static HttpClient CreateClient(string proxy, bool verifyTls)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };
        if (Uri.TryCreate(proxy, UriKind.Absolute, out var proxyUri)) handler.Proxy = new WebProxy(proxyUri);
        if (!verifyTls) handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        return new(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }
    public static async Task<Dictionary<string, Dictionary<int, Track>>> ReadAsync(HttpClient http, Source source, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source.Request);
        request.Headers.Accept.Add(new("application/json"));
        if (source.UserAgent.Length > 0) request.Headers.TryAddWithoutValidation("User-Agent", source.UserAgent);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await ReadBytesAsync(stream, 2 * 1024 * 1024, token).ConfigureAwait(false));
        var result = new Dictionary<string, Dictionary<int, Track>>(StringComparer.OrdinalIgnoreCase);
        foreach (var media in Array(document.RootElement, "MediaSources"))
        {
            string id = Text(media, "Id");
            if (id.Length == 0) continue;
            var tracks = new Dictionary<int, Track>();
            foreach (var track in Array(media, "MediaStreams"))
            {
                if (!Text(track, "Type").Equals("Subtitle", StringComparison.OrdinalIgnoreCase)
                    || Object(track, "Index").ValueKind != JsonValueKind.Number
                    || !Object(track, "Index").TryGetInt32(out int index) || index < 0) continue;
                string title = Text(track, "Title"), display = Text(track, "DisplayTitle");
                if (title.Length == 0) title = display;
                else if (display.Length > 0 && !display.Contains(title, StringComparison.OrdinalIgnoreCase)) title += " " + display;
                tracks[index] = new(title, Text(track, "Language"));
            }
            result[id] = tracks;
        }
        return result;
    }
}

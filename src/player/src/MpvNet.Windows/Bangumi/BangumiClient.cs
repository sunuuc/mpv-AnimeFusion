using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BangumiNet.Api;
using BangumiNet.Api.V0;
using BangumiNet.Api.V0.Models;
using BangumiNet.Api.V0.V0.Search.Subjects;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Http.HttpClientLibrary;

namespace MpvNet.Windows.Bangumi;

// Uses the MIT-licensed BangumiNet.Api client rather than a separate API implementation.
public sealed class BangumiClient
{
    readonly HttpClient _http;
    readonly BangumiStore _store;
    readonly SemaphoreSlim _gate = new(1, 1);
    public BangumiAccount? Account { get; private set; }
    public BangumiOAuth OAuth { get; }
    public event Action? AccountChanged;
    public string AccountError { get; } = "";

    public BangumiClient(BangumiStore store, HttpClient http)
    {
        _store = store;
        _http = http;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "sunuuc/mpv-AnimeFusion/" + typeof(BangumiClient).Assembly.GetName().Version + " (Windows; https://github.com/sunuuc/mpv-AnimeFusion)");
        OAuth = new(http);
        try { Account = store.ReadAccount(); }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException
            or IOException or UnauthorizedAccessException)
        { AccountError = "账号信息无法读取，请重新连接。"; }
    }

    ApiClient CreateClient(string? token) => new(new HttpClientRequestAdapter(new BangumiAuthenticationProvider(
        new BangumiAuthenticationProvider.AuthenticationContext { Bearer = token }), httpClient: _http));

    public async Task ConnectAsync(BangumiAccount account, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var user = await CreateClient(account.AccessToken).V0.Me.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(user?.Username)) throw new InvalidOperationException("Bangumi 未返回账号信息。");
            var connected = account with { Username = user.Username, AvatarUrl = user.Avatar?.Large ?? "" };
            _store.SaveAccount(connected);
            Account = connected;
            AccountChanged?.Invoke();
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _store.RemoveAccount(); Account = null; AccountChanged?.Invoke(); }
        finally { _gate.Release(); }
    }

    public async Task<string> AvatarFileAsync(CancellationToken cancellationToken)
    {
        string url = Account?.AvatarUrl ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return "";
        string path = _store.AvatarPath(url);
        if (File.Exists(path) && new FileInfo(path).Length == 64 * 64 * 4) return path;
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var source = System.Drawing.Image.FromStream(stream);
        byte[] pixels = CircularAvatar(source);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path + ".tmp", pixels, cancellationToken).ConfigureAwait(false);
        File.Move(path + ".tmp", path, true);
        return path;
    }

    public static byte[] CircularAvatar(System.Drawing.Image source)
    {
        using var bitmap = new System.Drawing.Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        using (var clip = new System.Drawing.Drawing2D.GraphicsPath())
        {
            graphics.Clear(System.Drawing.Color.Transparent);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            clip.AddEllipse(1, 1, 62, 62);
            graphics.SetClip(clip);
            int edge = Math.Min(source.Width, source.Height);
            graphics.DrawImage(source, new System.Drawing.Rectangle(0, 0, 64, 64),
                new System.Drawing.Rectangle((source.Width - edge) / 2, (source.Height - edge) / 2, edge, edge), System.Drawing.GraphicsUnit.Pixel);
        }
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, 64, 64),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            byte[] pixels = new byte[64 * 64 * 4];
            for (int row = 0; row < 64; row++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * 256, 256);
            return pixels;
        }
        finally { bitmap.UnlockBits(data); }
    }

    async Task<ApiClient> AuthenticatedClientAsync(CancellationToken cancellationToken)
    {
        var account = Account ?? throw new InvalidOperationException("请先连接 Bangumi 账号。");
        if (account.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
        {
            if (account.RefreshToken.Length == 0) throw new InvalidOperationException("Bangumi 登录已过期，请重新登录。");
            var refreshed = await OAuth.RefreshAsync(account, cancellationToken).ConfigureAwait(false);
            account = refreshed with { Username = account.Username, AvatarUrl = account.AvatarUrl };
            _store.SaveAccount(account);
            Account = account;
        }
        return CreateClient(account.AccessToken);
    }

    public async Task<IReadOnlyList<Subject>> SearchAsync(string keyword, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var api = await AuthenticatedClientAsync(cancellationToken).ConfigureAwait(false);
            var result = await api.V0.Search.Subjects.PostAsync(new SubjectsPostRequestBody
            {
                Keyword = keyword, Filter = new() { Type = [2] }
            }, c => c.QueryParameters.Limit = 20, cancellationToken).ConfigureAwait(false);
            return result?.Data?.Where(s => s.Id > 0).ToArray() ?? [];
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<Episode>> EpisodesAsync(int subjectId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var api = await AuthenticatedClientAsync(cancellationToken).ConfigureAwait(false);
            var episodes = new List<Episode>();
            int offset = 0;
            while (true)
            {
                var page = await api.V0.Episodes.GetAsync(c =>
                {
                    c.QueryParameters.SubjectId = subjectId;
                    c.QueryParameters.Offset = offset;
                    c.QueryParameters.Limit = 100;
                }, cancellationToken).ConfigureAwait(false);
                if (page?.Data is not { Count: > 0 } data) break;
                episodes.AddRange(data.Where(e => e.Id > 0));
                offset += data.Count;
                if (offset >= page.Total || data.Count < 100) break;
            }
            return episodes;
        }
        finally { _gate.Release(); }
    }

    public async Task<Subject> SubjectAsync(int subjectId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var api = await AuthenticatedClientAsync(cancellationToken).ConfigureAwait(false);
            return await api.V0.Subjects[subjectId].GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Bangumi 未返回条目信息。");
        }
        finally { _gate.Release(); }
    }

    static async Task<UserSubjectCollection?> CollectionAsync(ApiClient api, string username, int subjectId, CancellationToken token)
    {
        try { return await api.V0.Users[username].Collections[subjectId].GetAsync(cancellationToken: token).ConfigureAwait(false); }
        catch (ApiException e) when (e.ResponseStatusCode == 404) { return null; }
    }

    public async Task<int> CollectionTypeAsync(int subjectId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var api = await AuthenticatedClientAsync(cancellationToken).ConfigureAwait(false);
            return (await CollectionAsync(api, Account!.Username, subjectId, cancellationToken).ConfigureAwait(false))?.Type ?? 0;
        }
        finally { _gate.Release(); }
    }

    public async Task<int> CollectAsync(int subjectId, int type, bool onlyIfMissing, CancellationToken cancellationToken)
    {
        if (type is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(type));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var api = await AuthenticatedClientAsync(cancellationToken).ConfigureAwait(false);
            var collection = await CollectionAsync(api, Account!.Username, subjectId, cancellationToken).ConfigureAwait(false);
            if (onlyIfMissing && collection != null) return collection.Type ?? 0;
            if (collection == null)
                await api.V0.Users.Minus.Collections[subjectId].PostAsync(new() { Type = type }, cancellationToken: cancellationToken).ConfigureAwait(false);
            else if (collection.Type != type)
                await api.V0.Users.Minus.Collections[subjectId].PatchAsync(new() { Type = type }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return type;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<UserEpisodeCollection>> EpisodeStatesAsync(int subjectId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var api = await AuthenticatedClientAsync(cancellationToken).ConfigureAwait(false);
            var result = new List<UserEpisodeCollection>();
            while (true)
            {
                var page = await api.V0.Users.Minus.Collections[subjectId].Episodes.GetAsync(c =>
                { c.QueryParameters.Limit = 100; c.QueryParameters.Offset = result.Count; }, cancellationToken).ConfigureAwait(false);
                if (page?.Data is not { Count: > 0 } data) break;
                result.AddRange(data);
                if (result.Count >= page.Total || data.Count < 100) break;
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    // Collection and episode actions follow czy0729/Bangumi's collection/progress actions (MIT).
    // Changing an episode never silently changes the subject's collection status.
    public async Task SetEpisodeStateAsync(int subjectId, IReadOnlyList<int> episodeIds, int type, CancellationToken cancellationToken)
    {
        if (type is < 0 or > 3 || episodeIds.Count == 0) throw new ArgumentOutOfRangeException(nameof(type));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var api = await AuthenticatedClientAsync(cancellationToken).ConfigureAwait(false);
            if (await CollectionAsync(api, Account!.Username, subjectId, cancellationToken).ConfigureAwait(false) == null)
                throw new InvalidOperationException("请先收藏，再修改剧集进度。");
            if (episodeIds.Count == 1)
                await api.V0.Users.Minus.Collections.Minus.Episodes[episodeIds[0]].PutAsync(new() { Type = type }, cancellationToken: cancellationToken).ConfigureAwait(false);
            else
                await api.V0.Users.Minus.Collections[subjectId].Episodes.PatchAsync(new() { Type = type, EpisodeId = episodeIds.Select(id => (int?)id).ToList() }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<string> CoverFileAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return "";
        string path = _store.CoverPath(url);
        if (File.Exists(path) && new FileInfo(path).Length == 240 * 360 * 4) return path;
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var source = System.Drawing.Image.FromStream(stream);
        using var bitmap = new System.Drawing.Bitmap(240, 360, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            float scale = Math.Max(240f / source.Width, 360f / source.Height);
            float width = source.Width * scale, height = source.Height * scale;
            graphics.DrawImage(source, (240 - width) / 2, (360 - height) / 2, width, height);
        }
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, 240, 360),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        byte[] pixels = new byte[240 * 360 * 4];
        try
        {
            for (int row = 0; row < 360; row++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * 960, 960);
        }
        finally { bitmap.UnlockBits(data); }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path + ".tmp", pixels, cancellationToken).ConfigureAwait(false);
        File.Move(path + ".tmp", path, true);
        return path;
    }

    public async Task<bool> MarkWatchedAsync(int subjectId, int episodeId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var api = await AuthenticatedClientAsync(cancellationToken).ConfigureAwait(false);
            if (await CollectionAsync(api, Account!.Username, subjectId, cancellationToken).ConfigureAwait(false) == null)
                throw new InvalidOperationException("请先收藏，再修改剧集进度。");
            var episode = await api.V0.Users.Minus.Collections.Minus.Episodes[episodeId].GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (episode?.Type == 2) return false;
            await api.V0.Users.Minus.Collections.Minus.Episodes[episodeId].PutAsync(new() { Type = 2 }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }
}

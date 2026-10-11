// Browser/loopback handoff adapted from the existing Google OAuth Windows sample integration.
// Copyright 2016 Google Inc. Apache-2.0; see BangumiOAuth.cs and docs/myanimelist-research.md.
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MpvNet.Windows.MyAnimeList;

public sealed class MalOAuth(HttpClient http)
{
    public const string ClientId = "3d457be5f0bd9d5cf51c5b9c35888d60";
    public const string ServiceOrigin = "https://mal.sunuuc.de5.net";
    public const string RedirectUri = "http://127.0.0.1:33167/";
    static string Proof(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public async Task<MalAccount> AuthorizeAsync(Action<string> openBrowser, CancellationToken cancellationToken)
    {
        string state = Proof(RandomNumberGenerator.GetBytes(32)), verifier = Proof(RandomNumberGenerator.GetBytes(32));
        string challenge = Proof(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var listener = new HttpListener();
        listener.Prefixes.Add(RedirectUri);
        listener.Start();
        openBrowser(ServiceOrigin + "/authorize?state=" + Uri.EscapeDataString(state) + "&challenge=" + Uri.EscapeDataString(challenge));
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
            var query = context.Request.QueryString;
            if (context.Request.HttpMethod != "GET" || context.Request.Url?.AbsolutePath != "/" || query["state"] != state)
            { await ReplyAsync(context.Response, 400, "无效的授权回调。").ConfigureAwait(false); continue; }
            if (!string.IsNullOrEmpty(query["error"]))
            {
                await ReplyAsync(context.Response, 400, "授权未完成，请返回播放器重新授权。").ConfigureAwait(false);
                throw new InvalidOperationException("MyAnimeList 授权失败，请重新授权。");
            }
            string code = query["code"] ?? "";
            if (code.Length != 43)
            {
                await ReplyAsync(context.Response, 400, "授权服务未返回有效授权码。").ConfigureAwait(false);
                throw new InvalidOperationException("MyAnimeList 授权服务未返回有效授权码。");
            }
            try
            {
                using var response = await http.PostAsJsonAsync(ServiceOrigin + "/token", new { code, verifier }, timeout.Token).ConfigureAwait(false);
                var account = await ReadTokensAsync(response, timeout.Token).ConfigureAwait(false);
                await ReplyAsync(context.Response, 200, "授权完成，请返回 mpv-AnimeFusion。").ConfigureAwait(false);
                return account;
            }
            catch { await ReplyAsync(context.Response, 502, "授权未完成，请返回播放器重试。").ConfigureAwait(false); throw; }
        }
    }
    public async Task<MalAccount> RefreshAsync(MalAccount account, CancellationToken token)
    {
        using var response = await http.PostAsJsonAsync(ServiceOrigin + "/refresh", new { account.RefreshToken }, token).ConfigureAwait(false);
        var refreshed = await ReadTokensAsync(response, token).ConfigureAwait(false);
        return refreshed with { Username = account.Username, AvatarUrl = account.AvatarUrl };
    }
    static async Task<MalAccount> ReadTokensAsync(HttpResponseMessage response, CancellationToken token)
    {
        response.EnsureSuccessStatusCode();
        var account = await response.Content.ReadFromJsonAsync<MalAccount>(cancellationToken: token).ConfigureAwait(false);
        if (account == null || string.IsNullOrWhiteSpace(account.AccessToken) || string.IsNullOrWhiteSpace(account.RefreshToken)
            || account.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidOperationException("MyAnimeList 授权服务未返回有效令牌。");
        return account;
    }
    static async Task ReplyAsync(HttpListenerResponse response, int status, string message)
    {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
        byte[] body = Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><title>mpv-AnimeFusion</title><p>" + message + "</p>");
        response.ContentLength64 = body.Length;
        try { await response.OutputStream.WriteAsync(body).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or HttpListenerException or ObjectDisposedException) { }
        finally { response.Close(); }
    }
}

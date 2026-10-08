// Copyright 2016 Google Inc.
// Licensed under the Apache License, Version 2.0.
// Adapted from googlesamples/oauth-apps-for-windows, OAuthDesktopApp/MainWindow.xaml.cs.
// Changes: mpv-AnimeFusion authorization service, loopback handoff, state validation,
// SHA-256 handoff proof, cancellation and token refresh. No application secret is shipped.
using System.Net;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MpvNet.Windows.Bangumi;

public sealed class BangumiOAuth(HttpClient http)
{
    public const string ServiceOrigin = "https://bangumi.sunuuc.de5.net";
    public const string RedirectUri = "http://127.0.0.1:33165/";
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    static string Proof(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static Uri AuthorizationUri(string state, string challenge) => new(ServiceOrigin + "/authorize?state="
        + Uri.EscapeDataString(state) + "&challenge=" + Uri.EscapeDataString(challenge));

    public async Task<BangumiAccount> AuthorizeAsync(Action<string> openBrowser, CancellationToken cancellationToken)
    {
        string state = Proof(RandomNumberGenerator.GetBytes(32));
        string verifier = Proof(RandomNumberGenerator.GetBytes(32));
        string challenge = Proof(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var listener = new HttpListener();
        listener.Prefixes.Add(RedirectUri);
        listener.Start();
        openBrowser(AuthorizationUri(state, challenge).AbsoluteUri);
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
            var query = context.Request.QueryString;
            bool valid = context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/" && query["state"] == state;
            if (!valid)
            {
                await ReplyAsync(context.Response, 400, "无效的授权回调。", timeout.Token).ConfigureAwait(false);
                continue;
            }
            if (!string.IsNullOrEmpty(query["error"]))
            {
                await ReplyAsync(context.Response, 400, "授权未完成，请返回播放器重新授权。", timeout.Token).ConfigureAwait(false);
                throw new InvalidOperationException("Bangumi 授权失败，请重新授权。");
            }
            string code = query["code"] ?? "";
            if (code.Length != 43)
            {
                await ReplyAsync(context.Response, 400, "授权未完成，请返回播放器重试。", timeout.Token).ConfigureAwait(false);
                throw new InvalidOperationException("授权服务未返回有效授权码。");
            }
            try
            {
                using var response = await http.PostAsJsonAsync(ServiceOrigin + "/token", new { code, verifier }, timeout.Token).ConfigureAwait(false);
                var account = await ReadTokensAsync(response, timeout.Token).ConfigureAwait(false);
                await ReplyAsync(context.Response, 200, "授权完成，请返回 mpv-AnimeFusion。", timeout.Token).ConfigureAwait(false);
                return account;
            }
            catch
            {
                await ReplyAsync(context.Response, 502, "授权未完成，请返回播放器重试。", CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
    }

    public async Task<BangumiAccount> RefreshAsync(BangumiAccount account, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(ServiceOrigin + "/refresh", new { account.RefreshToken }, cancellationToken).ConfigureAwait(false);
        return await ReadTokensAsync(response, cancellationToken).ConfigureAwait(false);
    }

    static async Task<BangumiAccount> ReadTokensAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(response.StatusCode == HttpStatusCode.Unauthorized
                ? "Bangumi 登录已过期，请重新登录。" : "Bangumi 授权失败，请稍后重试。");
        var account = await response.Content.ReadFromJsonAsync<BangumiAccount>(JsonOptions, cancellationToken).ConfigureAwait(false);
        if (account == null || string.IsNullOrWhiteSpace(account.AccessToken) || string.IsNullOrWhiteSpace(account.RefreshToken)
            || account.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("授权服务未返回有效令牌。");
        return account;
    }

    static async Task ReplyAsync(HttpListenerResponse response, int status, string message, CancellationToken token)
    {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
        byte[] body = Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><title>mpv-AnimeFusion</title><p>" + message + "</p>");
        response.ContentLength64 = body.Length;
        try { await response.OutputStream.WriteAsync(body, token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or HttpListenerException or ObjectDisposedException)
        {
            // Closing the browser response must not discard a completed authorization.
        }
        finally { response.Close(); }
    }
}

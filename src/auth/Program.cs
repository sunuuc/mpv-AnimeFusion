using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.WebUtilities;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 8192);
string origin = builder.Configuration["PublicOrigin"] ?? "https://bangumi.sunuuc.de5.net";
var publicUri = new Uri(origin);
if (publicUri.Scheme != "https" || publicUri.AbsolutePath != "/") throw new InvalidOperationException("PublicOrigin must be an HTTPS origin.");
origin = publicUri.GetLeftPart(UriPartial.Authority);
string ReadSecret(string name) => File.ReadAllText(builder.Configuration[name + "File"] ?? "/run/secrets/" + name).Trim();
string clientId = ReadSecret("app-id"), clientSecret = ReadSecret("app-secret");
bool configured = clientId.Length > 0 && clientSecret.Length > 0;
builder.Services.AddDataProtection().SetApplicationName("AnimeVE.Auth").PersistKeysToFileSystem(new DirectoryInfo("/data/keys"));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Add(IPAddress.Parse("172.30.77.3"));
    options.ForwardLimit = 1;
});
builder.Services.AddSingleton<Handoffs>();
builder.Services.AddHttpClient("Bangumi", client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("mpv-AnimeFusion/1.2.7 (+https://github.com/sunuuc/mpv-AnimeFusion)");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
});
builder.Services.AddAuthentication().AddCookie("handoff").AddOAuth("Bangumi", options =>
{
    options.SignInScheme = "handoff";
    options.ClientId = configured ? clientId : "unconfigured";
    options.ClientSecret = configured ? clientSecret : "unconfigured";
    options.AuthorizationEndpoint = "https://bgm.tv/oauth/authorize";
    options.TokenEndpoint = "https://bgm.tv/oauth/access_token";
    options.CallbackPath = "/oauth/callback";
    options.SaveTokens = true;
    options.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(3);
    options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
    options.CorrelationCookie.HttpOnly = true;
    options.Events.OnTicketReceived = context =>
    {
        var properties = context.Properties ?? throw new InvalidOperationException("Missing authorization state.");
        string state = properties.Items["device_state"]!;
        string challenge = properties.Items["device_challenge"]!;
        string access = properties.GetTokenValue("access_token") ?? "";
        string refresh = properties.GetTokenValue("refresh_token") ?? "";
        string expires = properties.GetTokenValue("expires_at") ?? "";
        if (access.Length == 0 || refresh.Length == 0 || !DateTimeOffset.TryParse(expires, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAt)
            || expiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Invalid Bangumi token response.");
        string code = context.HttpContext.RequestServices.GetRequiredService<Handoffs>().Create(challenge, new(access, refresh, expiresAt, ""));
        context.Response.Redirect(QueryHelpers.AddQueryString("http://127.0.0.1:33165/", new Dictionary<string, string?> { ["state"] = state, ["code"] = code }));
        context.HandleResponse();
        return Task.CompletedTask;
    };
    options.Events.OnRemoteFailure = async context =>
    {
        context.HandleResponse();
        if (context.Properties?.Items.TryGetValue("device_state", out string? state) == true && Handoffs.ValidProof(state))
        {
            context.Response.Redirect(QueryHelpers.AddQueryString("http://127.0.0.1:33165/",
                new Dictionary<string, string?> { ["state"] = state, ["error"] = "authorization_failed" }));
            return;
        }
        context.Response.StatusCode = 400;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("Bangumi 授权未完成，请返回播放器重试。", context.HttpContext.RequestAborted);
    };
});

var app = builder.Build();
app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
    if (context.Request.Host.Value != publicUri.Authority || !context.Request.IsHttps)
    { context.Response.StatusCode = 400; return; }
    try { await next(); }
    catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 502;
            await context.Response.WriteAsJsonAsync(new { error = "Bangumi 授权服务暂时不可用。" });
        }
    }
});
app.UseRateLimiter();
app.UseAuthentication();
app.MapGet("/", () => Results.Text("mpv-AnimeFusion · Bangumi 授权", "text/plain; charset=utf-8"));
app.MapGet("/healthz", () => Results.Json(new { status = "ok", configured }));
app.MapGet("/readyz", () => configured ? Results.Ok(new { status = "ready" }) : Results.Json(new { error = "应用凭据尚未配置。" }, statusCode: 503));
app.MapGet("/authorize", (string? state, string? challenge) =>
{
    if (!configured) return Results.Json(new { error = "应用凭据尚未配置。" }, statusCode: 503);
    if (!Handoffs.ValidProof(state) || !Handoffs.ValidProof(challenge)) return Results.BadRequest();
    var properties = new AuthenticationProperties { RedirectUri = "/", ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(3) };
    properties.Items["device_state"] = state;
    properties.Items["device_challenge"] = challenge;
    return Results.Challenge(properties, ["Bangumi"]);
}).RequireRateLimiting("auth");
app.MapPost("/token", (HandoffRequest request, Handoffs handoffs) =>
{
    var tokens = handoffs.Take(request.Code, request.Verifier);
    return tokens == null ? Results.Json(new { error = "授权码无效或已过期。" }, statusCode: 400) : Results.Json(tokens);
}).RequireRateLimiting("auth");
app.MapPost("/refresh", async (RefreshRequest request, IHttpClientFactory clients, CancellationToken cancellationToken) =>
{
    if (!configured) return Results.Json(new { error = "应用凭据尚未配置。" }, statusCode: 503);
    if (string.IsNullOrWhiteSpace(request.RefreshToken) || request.RefreshToken.Length > 4096) return Results.BadRequest();
    using var body = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["client_secret"] = clientSecret,
        ["refresh_token"] = request.RefreshToken, ["redirect_uri"] = origin + "/oauth/callback"
    });
    using var response = await clients.CreateClient("Bangumi").PostAsync("https://bgm.tv/oauth/access_token", body, cancellationToken);
    if (!response.IsSuccessStatusCode) return Results.Json(new { error = "Bangumi 登录已过期，请重新登录。" }, statusCode: 401);
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    var root = json.RootElement;
    if (!root.TryGetProperty("access_token", out var access) || string.IsNullOrWhiteSpace(access.GetString())
        || !root.TryGetProperty("refresh_token", out var refresh) || string.IsNullOrWhiteSpace(refresh.GetString())
        || !root.TryGetProperty("expires_in", out var expiry) || !expiry.TryGetInt32(out int seconds) || seconds <= 0)
        return Results.Json(new { error = "Bangumi 未返回有效令牌。" }, statusCode: 502);
    return Results.Json(new Tokens(access.GetString()!, refresh.GetString()!, DateTimeOffset.UtcNow.AddSeconds(seconds), ""));
}).RequireRateLimiting("auth");
app.Run();

sealed record Tokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string Username);
sealed record HandoffRequest(string Code, string Verifier);
sealed record RefreshRequest(string RefreshToken);
sealed class Handoffs
{
    sealed record Entry(string Challenge, Tokens Tokens, DateTimeOffset ExpiresAt);
    readonly Dictionary<string, Entry> _entries = new();
    readonly object _lock = new();
    public static bool ValidProof(string? value) => value?.Length == 43 && Regex.IsMatch(value, "\\A[A-Za-z0-9_-]{43}\\z", RegexOptions.CultureInvariant);
    public string Create(string challenge, Tokens tokens)
    {
        lock (_lock)
        {
            foreach (string expired in _entries.Where(e => e.Value.ExpiresAt <= DateTimeOffset.UtcNow).Select(e => e.Key).ToArray()) _entries.Remove(expired);
            if (_entries.Count >= 1000) throw new InvalidOperationException("Too many pending authorizations.");
            string code = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            _entries.Add(code, new(challenge, tokens, DateTimeOffset.UtcNow.AddSeconds(60)));
            return code;
        }
    }
    public Tokens? Take(string? code, string? verifier)
    {
        if (!ValidProof(code) || !ValidProof(verifier)) return null;
        string challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier!)));
        lock (_lock)
        {
            if (!_entries.TryGetValue(code!, out var entry) || entry.ExpiresAt <= DateTimeOffset.UtcNow
                || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(challenge), Encoding.ASCII.GetBytes(entry.Challenge))) return null;
            _entries.Remove(code!);
            return entry.Tokens;
        }
    }
}

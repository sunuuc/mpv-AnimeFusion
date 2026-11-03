using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

static class AuthChecks
{
    static int checks;
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }

    static async Task Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "AnimeFusion-Auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app-id"), "test-client");
        File.WriteAllText(Path.Combine(root, "app-secret"), "test-secret");
        string setting = "ASPNETCORE_TEST_CONTENTROOT_ANIMEVE_AUTH";
        string? previous = Environment.GetEnvironmentVariable(setting);
        Environment.SetEnvironmentVariable(setting, Path.GetFullPath("src/auth"));
        try
        {
            foreach (var (provider, prefix, port) in new[] { ("Bangumi", "/bangumi", 33165), ("Trakt", "/trakt", 33166), ("MyAnimeList", "/mal", 33167) })
            {
                var upstream = new Upstream();
                await using var factory = new AuthFactory(provider, root, upstream);
                using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
                { BaseAddress = new("https://api.sunuuc.de5.net"), AllowAutoRedirect = false, HandleCookies = false });
                using var ready = await client.GetAsync(prefix + "/readyz");
                Check(ready.IsSuccessStatusCode && (await ready.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("provider").GetString() == provider,
                    provider + " readiness identifies the routed provider");
                foreach (string path in new[] { "/readyz", "/wrong/readyz", prefix + "-other/readyz" })
                    Check((await client.GetAsync(path)).StatusCode == HttpStatusCode.BadRequest, provider + " rejects missing or incorrect path base");
                Check((await client.GetAsync("http://api.sunuuc.de5.net" + prefix + "/readyz")).StatusCode == HttpStatusCode.BadRequest, "HTTP is rejected");
                Check((await client.GetAsync("https://other.invalid" + prefix + "/readyz")).StatusCode == HttpStatusCode.BadRequest, "Incorrect host is rejected");

                string state = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
                string verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
                string challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
                Check((await client.GetAsync(prefix + "/authorize?state=invalid&challenge=invalid")).StatusCode == HttpStatusCode.BadRequest, "Invalid handoff proof is rejected");
                using var authorization = await client.GetAsync(prefix + "/authorize?state=" + state + "&challenge=" + challenge);
                Check(authorization.StatusCode == HttpStatusCode.Redirect, provider + " starts authorization");
                var query = QueryHelpers.ParseQuery(authorization.Headers.Location!.Query);
                Check(query["redirect_uri"] == "https://api.sunuuc.de5.net" + prefix + "/oauth/callback", provider + " callback retains its public prefix");
                var cookies = authorization.Headers.GetValues("Set-Cookie").ToArray();
                Check(cookies.All(cookie => cookie.Contains("path=" + prefix + "/", StringComparison.OrdinalIgnoreCase)), provider + " cookies are isolated by path");
                if (provider != "Bangumi") Check(query["code_challenge_method"] == (provider == "Trakt" ? "S256" : "plain"), "Provider PKCE is preserved");

                string code;
                if (provider != "Trakt")
                {
                    using var callbackRequest = new HttpRequestMessage(HttpMethod.Get, prefix + "/oauth/callback?code=test-code&state=" + Uri.EscapeDataString(query["state"].ToString()));
                    callbackRequest.Headers.Add("Cookie", string.Join("; ", cookies.Select(cookie => cookie.Split(';')[0])));
                    using var callback = await client.SendAsync(callbackRequest);
                    Check(callback.StatusCode == HttpStatusCode.Redirect && callback.Headers.Location!.GetLeftPart(UriPartial.Authority) == "http://127.0.0.1:" + port,
                        provider + " callback returns to its fixed desktop listener");
                    var handoff = QueryHelpers.ParseQuery(callback.Headers.Location!.Query);
                    Check(handoff["state"] == state && handoff.ContainsKey("code") && !handoff.ContainsKey("access_token"), "Callback carries only a protected handoff");
                    code = handoff["code"].ToString();
                    Check(QueryHelpers.ParseQuery(upstream.Body)["redirect_uri"] == "https://api.sunuuc.de5.net" + prefix + "/oauth/callback", "Code exchange uses the same prefixed callback");
                }
                else
                    code = factory.Services.GetRequiredService<Handoffs>().Create(challenge, new("test-access", "test-refresh", DateTimeOffset.UtcNow.AddHours(1), ""));

                using var wrongProof = await client.PostAsJsonAsync(prefix + "/token", new { code, verifier = new string('X', 43) });
                Check(wrongProof.StatusCode == HttpStatusCode.BadRequest, "Wrong proof cannot consume a handoff");
                using var tokens = await client.PostAsJsonAsync(prefix + "/token", new { code, verifier });
                Check(tokens.IsSuccessStatusCode && (await tokens.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString() == "test-access", "Valid proof receives tokens");
                Check((await client.PostAsJsonAsync(prefix + "/token", new { code, verifier })).StatusCode == HttpStatusCode.BadRequest, "Handoff cannot be replayed");
                using var refreshed = await client.PostAsJsonAsync(prefix + "/refresh", new { refreshToken = "test-refresh" });
                Check(refreshed.IsSuccessStatusCode, provider + " refresh succeeds through its own prefix");
                if (provider == "Trakt")
                    Check(JsonDocument.Parse(upstream.Body).RootElement.GetProperty("redirect_uri").GetString() == "https://api.sunuuc.de5.net/trakt/oauth/callback", "Trakt refresh uses the new callback");
                if (provider == "Bangumi")
                    Check(QueryHelpers.ParseQuery(upstream.Body)["redirect_uri"] == "https://api.sunuuc.de5.net/bangumi/oauth/callback", "Bangumi refresh uses the new callback");
            }
            Console.WriteLine($"PASS authorization migration: {checks} checks; isolated upstream HTTP, no accounts or browser.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(setting, previous);
            string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup path.");
            Directory.Delete(root, true);
        }
    }

    sealed class AuthFactory(string provider, string root, Upstream upstream) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Provider", provider);
            builder.UseSetting("PublicOrigin", "https://api.sunuuc.de5.net");
            builder.UseSetting("ClientId", "test-client");
            builder.UseSetting("app-idFile", Path.Combine(root, "app-id"));
            builder.UseSetting("app-secretFile", Path.Combine(root, "app-secret"));
            builder.ConfigureServices(services =>
            {
                services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, provider)));
                services.AddHttpClient(provider).ConfigurePrimaryHttpMessageHandler(() => upstream);
                services.PostConfigure<OAuthOptions>("Bangumi", options => options.Backchannel = new HttpClient(upstream));
                services.PostConfigure<MyAnimeListOptions>("MyAnimeList", options => options.Backchannel = new HttpClient(upstream));
                services.PostConfigure<OpenIdConnectOptions>("Trakt", options => options.Configuration = new OpenIdConnectConfiguration
                { Issuer = "https://auth.trakt.tv", AuthorizationEndpoint = "https://auth.trakt.tv/oauth/authorize", TokenEndpoint = "https://auth.trakt.tv/oauth/token" });
            });
        }
    }

    sealed class Upstream : HttpMessageHandler
    {
        public string Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = "test-access", refresh_token = "test-refresh", expires_in = 3600, token_type = "Bearer" }) };
        }
    }
}

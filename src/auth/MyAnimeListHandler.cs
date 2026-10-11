// Provider extension of ASP.NET Core's OAuth handler (dotnet/aspnetcore, MIT).
// MAL public clients use plain PKCE and have no client secret.
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

sealed class MyAnimeListOptions : OAuthOptions
{
    public override void Validate()
    {
        // Preserve the remote options validation without requiring a confidential-client secret.
        new RemoteAuthenticationOptions { CallbackPath = CallbackPath }.Validate();
        ArgumentException.ThrowIfNullOrEmpty(ClientId);
        ArgumentException.ThrowIfNullOrEmpty(AuthorizationEndpoint);
        ArgumentException.ThrowIfNullOrEmpty(TokenEndpoint);
    }
}

sealed class MyAnimeListHandler(IOptionsMonitor<MyAnimeListOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : OAuthHandler<MyAnimeListOptions>(options, logger, encoder)
{
    protected override string BuildChallengeUrl(AuthenticationProperties properties, string redirectUri)
    {
        string verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        properties.Items.Add("code_verifier", verifier);
        // base owns protected state and callback correlation; MAL's only supported method is plain.
        return QueryHelpers.AddQueryString(base.BuildChallengeUrl(properties, redirectUri),
            new Dictionary<string, string?> { ["code_challenge"] = verifier, ["code_challenge_method"] = "plain" });
    }

    protected override async Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthCodeExchangeContext context)
    {
        string verifier = context.Properties.Items["code_verifier"] ?? throw new InvalidOperationException("Missing PKCE verifier.");
        context.Properties.Items.Remove("code_verifier");
        using var request = new HttpRequestMessage(HttpMethod.Post, Options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = Options.ClientId, ["code"] = context.Code, ["code_verifier"] = verifier,
                ["redirect_uri"] = context.RedirectUri, ["grant_type"] = "authorization_code"
            })
        };
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await Backchannel.SendAsync(request, Context.RequestAborted);
        if (!response.IsSuccessStatusCode)
            return OAuthTokenResponse.Failed(new HttpRequestException("MyAnimeList token exchange failed.", null, response.StatusCode));
        return OAuthTokenResponse.Success(System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(Context.RequestAborted)));
    }
}

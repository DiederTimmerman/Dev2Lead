using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dev2Lead.Core;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Dev2Lead.Api.Services;

public sealed class LinkedInConfigurationException : Exception;
public sealed class LinkedInIdentityException : Exception;

public interface ILinkedInIdentityClient
{
    string AuthorizationUrl(string state, string nonce);
    Task<LinkedInProfile> ImportAsync(string code, string nonce, CancellationToken ct);
}

public sealed class LinkedInIdentityClient(IConfiguration configuration) : ILinkedInIdentityClient, IDisposable
{
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
    private readonly ConfigurationManager<OpenIdConnectConfiguration> discovery = new(
        "https://www.linkedin.com/oauth/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever());

    private string Required(string key) => configuration[key] is { Length: > 0 } value ? value : throw new LinkedInConfigurationException();
    private string RedirectUri()
    {
        var value = Required("LinkedIn:RedirectUri");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0
            || uri.Fragment.Length > 0 || uri.AbsolutePath != "/api/linkedin/callback"
            || !(uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback)))
            throw new LinkedInConfigurationException();
        return uri.AbsoluteUri;
    }

    public string AuthorizationUrl(string state, string nonce)
    {
        _ = Required("LinkedIn:ClientSecret");
        var parameters = new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = Required("LinkedIn:ClientId"),
            ["redirect_uri"] = RedirectUri(), ["scope"] = "openid profile email", ["state"] = state, ["nonce"] = nonce
        };
        return "https://www.linkedin.com/oauth/v2/authorization?" + string.Join("&",
            parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    }

    public async Task<LinkedInProfile> ImportAsync(string code, string nonce, CancellationToken ct)
    {
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://www.linkedin.com/oauth/v2/accessToken")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = RedirectUri(),
                ["client_id"] = Required("LinkedIn:ClientId"), ["client_secret"] = Required("LinkedIn:ClientSecret")
            })
        };
        using var tokens = await http.SendAsync(tokenRequest, ct);
        if (!tokens.IsSuccessStatusCode) throw new LinkedInIdentityException();
        using var tokenJson = JsonDocument.Parse(await tokens.Content.ReadAsStringAsync(ct));
        var accessToken = tokenJson.RootElement.GetProperty("access_token").GetString();
        var idToken = tokenJson.RootElement.GetProperty("id_token").GetString();
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(idToken)) throw new LinkedInIdentityException();
        var metadata = await discovery.GetConfigurationAsync(ct);
        var validation = await new JsonWebTokenHandler { MapInboundClaims = false }.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = metadata.Issuer, ValidAudience = Required("LinkedIn:ClientId"),
            IssuerSigningKeys = metadata.SigningKeys, ValidAlgorithms = ["RS256"],
            ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
            RequireSignedTokens = true, RequireExpirationTime = true, ClockSkew = TimeSpan.FromMinutes(1)
        });
        if (!validation.IsValid || validation.ClaimsIdentity.FindFirst("nonce")?.Value != nonce)
            throw new LinkedInIdentityException();
        var subject = validation.ClaimsIdentity.FindFirst("sub")?.Value;
        using var userRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.linkedin.com/v2/userinfo");
        userRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(userRequest, ct);
        if (!response.IsSuccessStatusCode) throw new LinkedInIdentityException();
        using var userJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var user = userJson.RootElement;
        string Read(string field, int max, bool required = false)
        {
            var value = user.TryGetProperty(field, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";
            if (value.Length > max || (required && string.IsNullOrWhiteSpace(value))) throw new LinkedInIdentityException();
            return value;
        }
        var importedSubject = Read("sub", 255, true);
        if (importedSubject != subject) throw new LinkedInIdentityException();
        var picture = Read("picture", 2048);
        if (picture.Length > 0 && (!Uri.TryCreate(picture, UriKind.Absolute, out var pictureUri)
            || pictureUri.Scheme != "https" || pictureUri.UserInfo.Length > 0))
            throw new LinkedInIdentityException();
        var verified = user.TryGetProperty("email_verified", out var emailVerified) && emailVerified.ValueKind == JsonValueKind.True;
        return new(importedSubject, Read("name", 250, true), Read("email", 320), verified,
            picture.Length == 0 ? null : picture, DateTimeOffset.UtcNow);
    }
    public void Dispose() => http.Dispose();
}

public interface ILinkedInService
{
    Task<LinkedInConnectResponse> ConnectAsync(string userId, CancellationToken ct);
    Task CompleteAsync(string state, string? code, bool denied, CancellationToken ct);
}

public sealed class LinkedInService(IAccountRepository repository, IAccountService accounts, ILinkedInIdentityClient identity,
    ILogger<LinkedInService> logger) : ILinkedInService
{
    public async Task<LinkedInConnectResponse> ConnectAsync(string userId, CancellationToken ct)
    {
        await accounts.RequireConsentAsync(userId, ConsentPurpose.LinkedInImport, ct);
        var current = await repository.ReadAsync(userId, ct) ?? throw new ConsentRequiredException();
        var random = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var state = userId + "." + random;
        var url = identity.AuthorizationUrl(state, nonce);
        await repository.WriteAsync(current.Document with
        {
            Authorization = new(Hash(state), nonce, DateTimeOffset.UtcNow.AddMinutes(10)),
            LinkedInStatus = "Awaiting authorization"
        }, current.Version, ct);
        return new(url);
    }

    public async Task CompleteAsync(string state, string? code, bool denied, CancellationToken ct)
    {
        var parts = state.Split('.');
        if (parts.Length != 2 || parts.Any(part => part.Length != 64 || part.Any(c => !Uri.IsHexDigit(c))))
            throw new LinkedInIdentityException();
        var current = await repository.ReadAsync(parts[0], ct) ?? throw new LinkedInIdentityException();
        var pending = current.Document.Authorization;
        if (pending is null || pending.ExpiresOn < DateTimeOffset.UtcNow
            || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(pending.StateHash), Convert.FromHexString(Hash(state))))
            throw new LinkedInIdentityException();
        // Consume before exchanging the code; concurrent/replayed callbacks cannot reuse authorization.
        var consumed = await repository.WriteAsync(current.Document with
        { Authorization = null, LinkedInStatus = "Importing" }, current.Version, ct);
        try
        {
            await accounts.RequireConsentAsync(parts[0], ConsentPurpose.LinkedInImport, ct);
            if (denied || string.IsNullOrWhiteSpace(code) || code.Length > 4096) throw new LinkedInIdentityException();
            var profile = await identity.ImportAsync(code, pending.Nonce, ct);
            await repository.WriteAsync(consumed.Document with { LinkedIn = profile, LinkedInStatus = "Imported" }, consumed.Version, ct);
        }
        catch (Exception ex) when (ex is LinkedInIdentityException or ConsentRequiredException or HttpRequestException
            or JsonException or KeyNotFoundException or OperationCanceledException)
        {
            logger.LogWarning("LinkedIn import failed with {ExceptionType}; no provider credentials are retained", ex.GetType().Name);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await repository.WriteAsync(consumed.Document with { LinkedInStatus = denied ? "Cancelled" : "Failed" }, consumed.Version, cleanup.Token);
            throw;
        }
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

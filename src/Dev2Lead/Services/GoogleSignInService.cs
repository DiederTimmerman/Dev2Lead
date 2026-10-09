using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth;
using Microsoft.Extensions.Configuration;

namespace Dev2Lead.Services;

public sealed record GoogleAccount(string Subject, string Name, string Email);
public sealed record GoogleClientSettings(string ClientId, string ClientSecret);

public sealed class GoogleSignInService(HttpClient http, IConfiguration configuration)
{
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private string? idToken;
    private DateTimeOffset expiresOn;
    public GoogleAccount? Account { get; private set; }
    public bool IsSignedIn => Account is not null;
    private const string RefreshKey = "google-refresh-token";

    public Task<GoogleClientSettings> LoadSettingsAsync() => Task.FromResult(new GoogleClientSettings(
        configuration["Google:ClientId"] ?? "", configuration["Google:ClientSecret"] ?? ""));

    public async Task<GoogleAccount?> RestoreAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(await SecureStorage.Default.GetAsync(RefreshKey))) return null;
        await GetTokenAsync(ct);
        return Account;
    }

    public async Task<GoogleAccount> SignInAsync(CancellationToken ct = default)
    {
        var settings = await LoadSettingsAsync();
        if (string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.ClientSecret))
            throw new InvalidOperationException("The Google Desktop OAuth client must be configured in development User Secrets or deployment configuration.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var state = RandomString();
        var nonce = RandomString();
        var verifier = RandomString();
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirect = $"http://127.0.0.1:{port}/";
        try
        {
            var parameters = new Dictionary<string, string>
            {
                ["client_id"] = settings.ClientId, ["redirect_uri"] = redirect,
                ["response_type"] = "code", ["scope"] = "openid email profile", ["state"] = state,
                ["nonce"] = nonce, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
                ["access_type"] = "offline", ["prompt"] = "select_account consent"
            };
            var url = "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join("&",
                parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
            if (!await Launcher.Default.OpenAsync(new Uri(url)))
                throw new IOException("Windows could not open the Google sign-in browser.");
            var code = await ReceiveCodeAsync(listener, redirect, state, timeout.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = settings.ClientId, ["client_secret"] = settings.ClientSecret,
                    ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code"
                })
            };
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("Google could not complete sign-in. Check your Desktop OAuth client and consent-screen test users.", null, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var token = json.RootElement.GetProperty("id_token").GetString() ?? throw new InvalidDataException("Google returned no ID token.");
            await ValidateTokenAsync(token, settings.ClientId, nonce);
            if (json.RootElement.TryGetProperty("refresh_token", out var refresh) && refresh.GetString() is { Length: > 0 } value)
                await SecureStorage.Default.SetAsync(RefreshKey, value);
            else
                SecureStorage.Default.Remove(RefreshKey);
            return Account!;
        }
        finally { listener.Stop(); }
    }

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        await tokenLock.WaitAsync(ct);
        try
        {
            if (idToken is not null && expiresOn > DateTimeOffset.UtcNow.AddMinutes(2)) return idToken;
            var refresh = await SecureStorage.Default.GetAsync(RefreshKey);
            if (string.IsNullOrWhiteSpace(refresh)) throw new InvalidOperationException("Sign in with Google to continue.");
            var settings = await LoadSettingsAsync();
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = settings.ClientId, ["client_secret"] = settings.ClientSecret,
                    ["refresh_token"] = refresh, ["grant_type"] = "refresh_token"
                })
            };
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("Your Google session could not be refreshed. Sign out, then sign in again.", null, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var token = json.RootElement.GetProperty("id_token").GetString() ?? throw new InvalidDataException("Google returned no ID token.");
            await ValidateTokenAsync(token, settings.ClientId, null);
            return idToken!;
        }
        finally { tokenLock.Release(); }
    }

    private async Task ValidateTokenAsync(string token, string clientId, string? nonce)
    {
        var identity = await GoogleJsonWebSignature.ValidateAsync(token,
            new GoogleJsonWebSignature.ValidationSettings { Audience = [clientId] });
        var payloadPart = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        using var claims = JsonDocument.Parse(Convert.FromBase64String(payloadPart.PadRight((payloadPart.Length + 3) / 4 * 4, '=')));
        if (nonce is not null && (!claims.RootElement.TryGetProperty("nonce", out var tokenNonce) || tokenNonce.GetString() != nonce))
            throw new InvalidDataException("Google sign-in returned a mismatched nonce. Please retry.");
        expiresOn = DateTimeOffset.FromUnixTimeSeconds(claims.RootElement.GetProperty("exp").GetInt64());
        idToken = token;
        Account = new(identity.Subject, identity.Name ?? "Google user", identity.Email ?? "");
    }

    public void SignOut()
    {
        SecureStorage.Default.Remove(RefreshKey);
        idToken = null; Account = null; expiresOn = default;
    }

    private static string RandomString() => Base64Url(RandomNumberGenerator.GetBytes(32));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<string> ReceiveCodeAsync(TcpListener listener, string redirect, string state, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            var buffer = new byte[8192];
            var count = 0;
            try
            {
                while (count < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(count, Math.Min(512, buffer.Length - count)), readTimeout.Token);
                    if (read == 0) break;
                    count += read;
                    if (Encoding.ASCII.GetString(buffer, 0, count).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                System.Diagnostics.Trace.TraceWarning("An incomplete Google callback connection timed out.");
                continue;
            }
            var firstLine = Encoding.ASCII.GetString(buffer, 0, count).Split("\r\n")[0].Split(' ');
            var values = new Dictionary<string, string>();
            var valid = firstLine.Length == 3 && firstLine[0] == "GET"
                && Uri.TryCreate(new Uri(redirect), firstLine[1], out var callback)
                && callback.GetLeftPart(UriPartial.Authority) == new Uri(redirect).GetLeftPart(UriPartial.Authority) && callback.AbsolutePath == "/";
            if (valid)
            {
                var callbackUri = new Uri(new Uri(redirect), firstLine[1]);
                foreach (var pair in callbackUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = pair.Split('=', 2);
                    if (parts.Length != 2 || !values.TryAdd(Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1]))) { valid = false; break; }
                }
                valid = valid && values.GetValueOrDefault("state") == state;
            }
            var body = valid ? "Google sign-in received. You can return to Dev2Lead." : "Invalid sign-in callback.";
            var status = valid ? "200 OK" : "400 Bad Request";
            var output = Encoding.UTF8.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
            await stream.WriteAsync(output, ct);
            if (!valid) { System.Diagnostics.Trace.TraceWarning("Rejected an invalid Google callback state or request."); continue; }
            if (values.ContainsKey("error")) throw new InvalidOperationException("Google sign-in was cancelled or denied.");
            return values.GetValueOrDefault("code") ?? throw new InvalidDataException("Google returned no authorization code.");
        }
    }
}

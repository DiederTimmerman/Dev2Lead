using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dev2Lead.Core;
using Microsoft.Extensions.Configuration;

namespace Dev2Lead.Services;

public sealed class CloudProfileClient(GoogleSignInService google, IConfiguration configuration) : IDisposable
{
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3) };
    public CareerProfileResponse? Profile { get; private set; }
    public AccountSettingsResponse? Settings { get; private set; }
    public string BackendUrl => configuration["Backend:Url"] ?? "http://127.0.0.1:5246/";

    public static Uri ValidateBackendUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || !(uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback))
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
            throw new InvalidOperationException("Use an HTTPS backend root URL. HTTP is permitted only for a local loopback development server.");
        return uri;
    }
    public void Clear() { Profile = null; Settings = null; }

    public async Task<AccountSettingsResponse> LoadSettingsAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/account/settings", null, ct);
        await EnsureSuccessAsync(response, ct);
        Settings = await response.Content.ReadFromJsonAsync<AccountSettingsResponse>(cancellationToken: ct)
            ?? throw new InvalidDataException("The backend returned no account settings.");
        return Settings;
    }
    public async Task<AccountSettingsResponse> SaveSettingsAsync(string transcriptUrl, AccountConsents consents, CancellationToken ct = default)
    {
        var settings = Settings ?? throw new InvalidOperationException("Refresh your account settings before saving.");
        using var response = await SendAsync(HttpMethod.Put, "api/account/settings",
            JsonContent.Create(new UpdateAccountSettingsRequest(transcriptUrl, consents, settings.Version)), ct);
        await EnsureSuccessAsync(response, ct);
        Settings = await response.Content.ReadFromJsonAsync<AccountSettingsResponse>(cancellationToken: ct)
            ?? throw new InvalidDataException("The backend returned no saved account settings.");
        return Settings;
    }
    public async Task<string> StartLinkedInAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/linkedin/connect", null, ct);
        await EnsureSuccessAsync(response, ct);
        var connect = await response.Content.ReadFromJsonAsync<LinkedInConnectResponse>(cancellationToken: ct)
            ?? throw new InvalidDataException("The backend returned no LinkedIn authorization URL.");
        await LoadSettingsAsync(ct);
        return connect.AuthorizationUrl;
    }
    public async Task RemoveLinkedInAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, "api/account/linkedin", null, ct);
        await EnsureSuccessAsync(response, ct);
        Settings = await response.Content.ReadFromJsonAsync<AccountSettingsResponse>(cancellationToken: ct)
            ?? throw new InvalidDataException("The backend returned no updated account settings.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string route, HttpContent? content, CancellationToken ct,
        string? version = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(ValidateBackendUrl(BackendUrl), route)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await google.GetTokenAsync(ct));
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", version);
        return await http.SendAsync(request, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Google sign-in was rejected. Sign in again, and check that the backend uses the same Google client ID.",
            HttpStatusCode.Forbidden => "This Google account cannot access the backend.",
            _ => $"The cloud backend returned HTTP {(int)response.StatusCode}."
        };
        if (response.Content.Headers.ContentType?.MediaType is "application/problem+json" or "application/json")
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (document.RootElement.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } safeMessage)
                message = safeMessage;
        }
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    public async Task<CareerProfileResponse?> LoadAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/profile", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) { Profile = null; return null; }
        await EnsureSuccessAsync(response, ct);
        Profile = await response.Content.ReadFromJsonAsync<CareerProfileResponse>(cancellationToken: ct)
            ?? throw new InvalidDataException("The backend returned an empty profile.");
        return Profile;
    }

    private async Task<CareerProfileResponse> ReadProfileAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        Profile = await response.Content.ReadFromJsonAsync<CareerProfileResponse>(cancellationToken: ct)
            ?? throw new InvalidDataException("The backend returned an empty profile.");
        return Profile;
    }

    public async Task<CareerProfileResponse> UploadAsync(string name, byte[] bytes, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("true"), "consent");
        if (Profile is { } profile) form.Add(new StringContent(profile.Version), "version");
        form.Add(new ByteArrayContent(bytes), "file", name);
        using var response = await SendAsync(HttpMethod.Post, "api/profile/cv", form, ct);
        return await ReadProfileAsync(response, ct);
    }

    public async Task<CareerProfileResponse> AnalyzeAsync(CancellationToken ct = default)
    {
        var version = Profile?.Version ?? throw new InvalidOperationException("Upload or refresh your cloud CV first.");
        using var response = await SendAsync(HttpMethod.Post, "api/profile/analyze", JsonContent.Create(new AnalyzeProfileRequest(version, true)), ct);
        return await ReadProfileAsync(response, ct);
    }
    public async Task<CareerProfileResponse> SaveSkillsAsync(IReadOnlyList<SkillAssessment> skills, CancellationToken ct = default)
    {
        SkillProfile.Validate(skills, requireReviewed: true);
        var version = Profile?.Version ?? throw new InvalidOperationException("Upload a CV to your signed-in cloud profile before saving skills.");
        using var response = await SendAsync(HttpMethod.Put, "api/profile/skills",
            JsonContent.Create(new UpdateSkillsRequest(version, skills.Select(SkillValue.From).ToList())), ct);
        return await ReadProfileAsync(response, ct);
    }
    public async Task DeleteAsync(CancellationToken ct = default)
    {
        var version = Profile?.Version ?? throw new InvalidOperationException("No cloud profile is loaded.");
        using var response = await SendAsync(HttpMethod.Delete, "api/profile", null, ct, version);
        await EnsureSuccessAsync(response, ct);
        Profile = null;
    }
    public async Task<CoachReply> ChatAsync(IReadOnlyList<ConversationMessage> messages, bool generate, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/profile/chat",
            JsonContent.Create(new ChatProfileRequest(messages.TakeLast(24).ToList(), generate, true)), ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<CoachReply>(cancellationToken: ct)
            ?? throw new InvalidDataException("The backend returned an empty coach response.");
    }
    public async Task<CoachReply> GrowthChatAsync(string coachId, IReadOnlyList<ConversationMessage> messages, CancellationToken ct)
    {
        var version = Profile?.Version ?? throw new InvalidOperationException("Upload or refresh your saved CV first.");
        using var response = await SendAsync(HttpMethod.Post, "api/profile/coaches/chat",
            JsonContent.Create(new GrowthChatRequest(coachId, messages.TakeLast(24).ToList(), version)), ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<CoachReply>(cancellationToken: ct)
            ?? throw new InvalidDataException("The backend returned no growth coaching response.");
    }

    public async Task<GapAnalysisResponse> GapAsync(string targetRole, CancellationToken ct)
    {
        GrowthAgent.ValidateTarget(targetRole);
        var version = Profile?.Version ?? throw new InvalidOperationException("Upload or refresh your saved CV first.");
        using var response = await SendAsync(HttpMethod.Post, "api/profile/gap-analysis",
            JsonContent.Create(new GapAnalysisRequest(targetRole, version)), ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<GapAnalysisResponse>(cancellationToken: ct)
            ?? throw new InvalidDataException("The backend returned no gap analysis.");
    }
    public void Dispose() => http.Dispose();
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Dev2Lead.Api.Services;
using Dev2Lead.Api.Endpoints;
using Dev2Lead.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dev2Lead.Api.Tests;

public sealed class ProfileApiTests
{
    private const string Cv = "Developer at Example from 2020-01 to present. Three years of C# and mentoring experience.";

    [Theory]
    [InlineData("GET", "/api/profile")]
    [InlineData("GET", "/api/account")]
    [InlineData("GET", "/api/profile/cv/original")]
    [InlineData("POST", "/api/profile/cv")]
    [InlineData("POST", "/api/profile/analyze")]
    [InlineData("POST", "/api/profile/chat")]
    [InlineData("POST", "/api/profile/coaches/chat")]
    [InlineData("POST", "/api/profile/gap-analysis")]
    [InlineData("PUT", "/api/profile/skills")]
    [InlineData("DELETE", "/api/profile")]
    [InlineData("GET", "/api/account/settings")]
    [InlineData("PUT", "/api/account/settings")]
    [InlineData("POST", "/api/linkedin/connect")]
    [InlineData("DELETE", "/api/account/linkedin")]
    public async Task AllPersonalEndpointsRequireAuthentication(string method, string path)
    {
        await using var factory = new Factory();
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(new(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task ProductionAuthenticationRejectsInvalidGoogleToken()
    {
        await using var factory = new Factory(useTestAuthentication: false);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid");
        using var response = await client.GetAsync("/api/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.Repository.Profiles);
    }

    [Fact]
    public async Task FirstAuthenticatedReadsCreateEmptyAccountAndProfileDocuments()
    {
        await using var factory = new Factory();
        using var client = factory.ForNewUser("alice");

        var settingsResponse = await client.GetAsync("/api/account/settings");
        Assert.Equal(HttpStatusCode.OK, settingsResponse.StatusCode);
        var settings = (await settingsResponse.Content.ReadFromJsonAsync<AccountSettingsResponse>())!;
        Assert.NotNull(settings.Version);
        Assert.Equal(new AccountConsents(), settings.Consents);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/profile")).StatusCode);
        var account = Assert.Single(factory.Accounts.Documents.Values);
        Assert.Equal(new AccountConsents(), account.Document.Consents);
        var profile = Assert.Single(factory.Repository.Profiles.Values);
        Assert.False(profile.Document.HasOriginalCv);
        Assert.Equal("Empty", profile.Document.AnalysisStatus);
        Assert.Equal(account.Document.UserId, profile.Document.UserId);

        var savedSettings = await client.PutAsJsonAsync("/api/account/settings",
            new UpdateAccountSettingsRequest("", new(CvStorage: true), settings.Version));
        Assert.Equal(HttpStatusCode.OK, savedSettings.StatusCode);
        using var form = CvForm();
        using var upload = await client.PostAsync("/api/profile/cv", form);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var storedProfile = Assert.Single(factory.Repository.Profiles.Values);
        Assert.True(storedProfile.Document.HasOriginalCv);
        Assert.Equal("Pending", storedProfile.Document.AnalysisStatus);
    }

    [Fact]
    public async Task UploadedOriginalAndTextArePrivateToGoogleSubjectNotEmail()
    {
        await using var factory = new Factory();
        using var alice = factory.ForUser("alice");
        using var bob = factory.ForUser("bob");
        var profile = await Upload(alice);
        Assert.Equal("Pending", profile.AnalysisStatus);
        Assert.Equal(Cv, profile.CvText);
        Assert.Empty(profile.Skills);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("/api/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("/api/profile/cv/original")).StatusCode);
        Assert.Equal(Cv, await alice.GetStringAsync("/api/profile/cv/original"));
        var bobProfile = await Upload(bob);
        Assert.NotEqual(profile.Version, bobProfile.Version);
        Assert.Equal(2, factory.Repository.Profiles.Count);
        Assert.Equal(2, factory.Blobs.Files.Count);
    }

    [Fact]
    public async Task AnalysisImmediatelyPersistsInitialAiRatingsAndYears()
    {
        await using var factory = new Factory();
        using var client = factory.ForUser("alice");
        var uploaded = await Upload(client);
        var response = await client.PostAsJsonAsync("/api/profile/analyze", new AnalyzeProfileRequest(uploaded.Version, true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ready = await response.Content.ReadFromJsonAsync<CareerProfileResponse>();
        Assert.Equal("Ready", ready!.AnalysisStatus);
        Assert.Single(ready.Experiences);
        Assert.Equal(3, ready.Skills[0].Level);
        Assert.Equal(3m, ready.Skills[0].Years);
        Assert.False(ready.Skills[0].Reviewed);
        Assert.Equal(ready.Version, (await client.GetFromJsonAsync<CareerProfileResponse>("/api/profile"))!.Version);
    }

    [Fact]
    public async Task FailedExtractionRetainsOriginalAndCanBeRetriedWithRefreshedVersion()
    {
        await using var factory = new Factory();
        using var client = factory.ForUser("alice");
        var uploaded = await Upload(client);
        factory.Agent.FailOnce = true;
        Assert.Equal(HttpStatusCode.BadGateway, (await client.PostAsJsonAsync("/api/profile/analyze",
            new AnalyzeProfileRequest(uploaded.Version, true))).StatusCode);
        var failed = (await client.GetFromJsonAsync<CareerProfileResponse>("/api/profile"))!;
        Assert.Equal("Failed", failed.AnalysisStatus);
        Assert.Equal(Cv, await client.GetStringAsync("/api/profile/cv/original"));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/profile/analyze",
            new AnalyzeProfileRequest(failed.Version, true))).StatusCode);
    }

    [Fact]
    public async Task ReviewedEditsAreRequiredAndStaleWritesCannotOverwrite()
    {
        await using var factory = new Factory();
        using var client = factory.ForUser("alice");
        var uploaded = await Upload(client);
        var skill = new SkillValue("C#", "Technical", 3, 3, "Reviewed by me", false);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PutAsJsonAsync("/api/profile/skills",
            new UpdateSkillsRequest(uploaded.Version, [skill]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/profile/skills",
            new UpdateSkillsRequest(uploaded.Version, [skill with { Reviewed = true }]))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/profile/skills",
            new UpdateSkillsRequest(uploaded.Version, []))).StatusCode);
        Assert.Single((await client.GetFromJsonAsync<CareerProfileResponse>("/api/profile"))!.Skills);
    }

    [Fact]
    public async Task DeleteRemovesOriginalAndMetadata()
    {
        await using var factory = new Factory();
        using var client = factory.ForUser("alice");
        var uploaded = await Upload(client);
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(client, uploaded.Version)).StatusCode);
        Assert.Empty(factory.Repository.Profiles);
        Assert.Empty(factory.Blobs.Files);
    }

    [Fact]
    public async Task PartialDeleteRetainsDurableStateForRetry()
    {
        await using var factory = new Factory();
        using var client = factory.ForUser("alice");
        var uploaded = await Upload(client);
        factory.Blobs.FailDeleteOnce = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Delete(client, uploaded.Version)).StatusCode);
        var deleting = (await client.GetFromJsonAsync<CareerProfileResponse>("/api/profile"))!;
        Assert.Equal("Deleting", deleting.AnalysisStatus);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/profile/cv/original")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(client, deleting.Version)).StatusCode);
        Assert.Empty(factory.Repository.Profiles);
        Assert.Empty(factory.Blobs.Files);
    }

    [Fact]
    public async Task FailedMetadataSaveCleansNewlyUploadedBlob()
    {
        await using var factory = new Factory();
        using var client = factory.ForUser("alice");
        factory.Repository.FailWriteOnce = true;
        using var form = CvForm();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/profile/cv", form)).StatusCode);
        Assert.Empty(factory.Repository.Profiles);
        Assert.Empty(factory.Blobs.Files);
    }

    [Fact]
    public async Task ReplacementCleanupFailureKeepsOldBlobReferenceUntilDeletion()
    {
        await using var factory = new Factory();
        using var client = factory.ForUser("alice");
        var first = await Upload(client);
        factory.Blobs.FailDeleteOnce = true;
        using var form = CvForm(first.Version);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync("/api/profile/cv", form)).StatusCode);
        Assert.Equal(2, factory.Blobs.Files.Count);
        Assert.Single(Assert.Single(factory.Repository.Profiles.Values).Document.ObsoleteBlobNames);
        var current = (await client.GetFromJsonAsync<CareerProfileResponse>("/api/profile"))!;
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(client, current.Version)).StatusCode);
        Assert.Empty(factory.Blobs.Files);
    }

    [Fact]
    public async Task MissingContainerCannotMasqueradeAsMissingUserProfile()
    {
        await using var factory = new Factory();
        using var client = factory.ForUser("alice");
        factory.Repository.Unavailable = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/profile")).StatusCode);
    }

    [Fact]
    public async Task ExplicitConsentIsRequiredForUploadAndAnalysis()
    {
        await using var factory = new Factory();
        using var client = factory.ForUser("alice");
        using var form = CvForm(consent: false);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync("/api/profile/cv", form)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync("/api/profile/analyze",
            new AnalyzeProfileRequest("none", false))).StatusCode);
        Assert.Empty(factory.Blobs.Files);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Conflict)]
    [InlineData(true, HttpStatusCode.NoContent)]
    public async Task OnlyActiveAnalysisBlocksDeletion(bool stale, HttpStatusCode expected)
    {
        await using var factory = new Factory();
        using var client = factory.ForUser("alice");
        var uploaded = await Upload(client);
        var stored = Assert.Single(factory.Repository.Profiles.Values);
        factory.Repository.Profiles[stored.Document.UserId] = stored with
        {
            Document = stored.Document with
            {
                AnalysisStatus = "Analyzing",
                AnalysisStartedOn = DateTimeOffset.UtcNow.AddMinutes(stale ? -10 : 0)
            }
        };
        Assert.Equal(expected, (await Delete(client, uploaded.Version)).StatusCode);
    }

    [Fact]
    public async Task RequestConsentCannotOverrideSavedStorageRevocation()
    {
        await using var factory = new Factory();
        using var client = factory.ForUser("alice");
        var settings = (await client.GetFromJsonAsync<AccountSettingsResponse>("/api/account/settings"))!;
        var revoked = await client.PutAsJsonAsync("/api/account/settings", new UpdateAccountSettingsRequest("", new(), settings.Version));
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        using var form = CvForm(consent: true);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/profile/cv", form)).StatusCode);
        Assert.Empty(factory.Blobs.Files);
    }

    [Fact]
    public async Task AiAnalysisChecksSavedConsentAndCvDeletionPreservesPreferences()
    {
        await using var factory = new Factory();
        using var client = factory.ForUser("alice");
        var uploaded = await Upload(client);
        var account = (await client.GetFromJsonAsync<AccountSettingsResponse>("/api/account/settings"))!;
        var saved = await client.PutAsJsonAsync("/api/account/settings", new UpdateAccountSettingsRequest(
            "https://learn.microsoft.com/en-us/users/diedertimmerman/transcript/dl9e1cq3814l0xe",
            new(CvStorage: true), account.Version));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/profile/analyze",
            new AnalyzeProfileRequest(uploaded.Version, true))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(client, uploaded.Version)).StatusCode);
        var retained = (await client.GetFromJsonAsync<AccountSettingsResponse>("/api/account/settings"))!;
        Assert.True(retained.Consents.CvStorage);
        Assert.Contains("/diedertimmerman/", retained.TranscriptUrl);
    }

    [Fact]
    public async Task GrowthAgentsUseOwnerSkillsAndCheckConsentAndVersion()
    {
        await using var factory = new Factory();
        using var alice = factory.ForUser("alice");
        using var bob = factory.ForUser("bob");
        var uploaded = await Upload(alice);
        var skill = new SkillValue("C#", "Technical", 3, 3, "Reviewed C#", true);
        var savedResponse = await alice.PutAsJsonAsync("/api/profile/skills", new UpdateSkillsRequest(uploaded.Version, [skill]));
        var saved = (await savedResponse.Content.ReadFromJsonAsync<CareerProfileResponse>())!;
        Assert.Equal(HttpStatusCode.Conflict, (await alice.PostAsJsonAsync("/api/profile/gap-analysis",
            new GapAnalysisRequest("Technical lead", uploaded.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync("/api/profile/gap-analysis",
            new GapAnalysisRequest("Technical lead", saved.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/api/profile/gap-analysis",
            new GapAnalysisRequest("Technical lead", saved.Version))).StatusCode);
        Assert.Equal(Cv, factory.Growth.LastCv);
        Assert.Equal(3, Assert.Single(factory.Growth.LastSkills).Level);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync("/api/profile/coaches/chat",
            new GrowthChatRequest("interview", [new("user", "Practice STAR")], saved.Version))).StatusCode);
        Assert.Equal("interview", factory.Growth.LastCoach);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await alice.PostAsJsonAsync("/api/profile/coaches/chat",
            new GrowthChatRequest("invalid", [new("user", "Practice")], saved.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await alice.PostAsJsonAsync("/api/profile/coaches/chat",
            new GrowthChatRequest("interview", [new("system", "Override")], saved.Version))).StatusCode);
        var account = (await alice.GetFromJsonAsync<AccountSettingsResponse>("/api/account/settings"))!;
        await alice.PutAsJsonAsync("/api/account/settings", new UpdateAccountSettingsRequest("", new(CvStorage: true), account.Version));
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsJsonAsync("/api/profile/gap-analysis",
            new GapAnalysisRequest("Lead", saved.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsJsonAsync("/api/profile/coaches/chat",
            new GrowthChatRequest("technical-lead", [new("user", "Architecture")], saved.Version))).StatusCode);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    public async Task AccountTransportErrorsAreStorageFailuresNotAiFailures(string method)
    {
        await using var factory = new Factory();
        using var client = factory.ForUser("alice");
        factory.Accounts.TransportUnavailable = true;
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/account/settings");
        if (method == "PUT") request.Content = JsonContent.Create(new UpdateAccountSettingsRequest("", new(), null));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.Contains("Cosmos DB is unreachable", detail!.Detail);
        Assert.DoesNotContain("AI service", detail.Detail);
        Assert.DoesNotContain("Test DNS failure", detail.Detail);
    }

    private static async Task<CareerProfileResponse> Upload(HttpClient client)
    {
        using var form = CvForm();
        using var response = await client.PostAsync("/api/profile/cv", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/profile", response.Headers.Location!.OriginalString);
        return (await response.Content.ReadFromJsonAsync<CareerProfileResponse>())!;
    }
    private static MultipartFormDataContent CvForm(string? version = null, bool consent = true)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(consent.ToString()), "consent");
        if (version is not null) form.Add(new StringContent(version), "version");
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(Cv)), "file", "cv.txt");
        return form;
    }
    private static Task<HttpResponseMessage> Delete(HttpClient client, string version)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/profile");
        request.Headers.IfMatch.Add(new EntityTagHeaderValue(version));
        return client.SendAsync(request);
    }

    private sealed class Factory(bool useTestAuthentication = true) : WebApplicationFactory<Program>
    {
        public Repository Repository { get; } = new();
        public Blobs Blobs { get; } = new();
        public Agent Agent { get; } = new();
        public GrowthStub Growth { get; } = new();
        public TestAccountRepository Accounts { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Google:ClientId"] = "test.apps.googleusercontent.com",
                ["AzureOpenAI:Endpoint"] = "https://example.services.ai.azure.com/",
                ["AzureOpenAI:Deployment"] = "gpt-5-nano",
                ["AzureOpenAI:ApiKey"] = "test-key"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IProfileRepository>(); services.AddSingleton<IProfileRepository>(Repository);
                services.RemoveAll<IAccountRepository>(); services.AddSingleton<IAccountRepository>(Accounts);
                services.RemoveAll<ICvBlobStore>(); services.AddSingleton<ICvBlobStore>(Blobs);
                services.RemoveAll<ICvExtractionAgent>(); services.AddSingleton<ICvExtractionAgent>(Agent);
                services.RemoveAll<IGrowthAgent>(); services.AddSingleton<IGrowthAgent>(Growth);
                if (useTestAuthentication) services.AddAuthentication("test")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
            });
        }
        public HttpClient ForUser(string subject)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
            var userId = ProfileEndpoints.AccountKey(new ClaimsPrincipal(new ClaimsIdentity([new("sub", subject)], "test")));
            Accounts.WriteAsync(new AccountDocument { UserId = userId, Consents = new(true, true, true, true) }, null, default).GetAwaiter().GetResult();
            return client;
        }
        public HttpClient ForNewUser(string subject)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
            return client;
        }
    }
    private sealed class GrowthStub : IGrowthAgent
    {
        public string LastCv { get; private set; } = "";
        public IReadOnlyList<SkillAssessment> LastSkills { get; private set; } = [];
        public string LastCoach { get; private set; } = "";
        public Task<CoachReply> ChatAsync(AzureSettings settings, string cv, IReadOnlyList<SkillAssessment> skills,
            string coachId, IReadOnlyList<ConversationMessage> messages, CancellationToken ct)
        {
            LastCv = cv; LastSkills = skills; LastCoach = coachId;
            return Task.FromResult(new CoachReply { Message = "Tell me about a challenge." });
        }
        public Task<GapAnalysisResponse> AnalyzeAsync(AzureSettings settings, string cv, IReadOnlyList<SkillAssessment> skills,
            string targetRole, CancellationToken ct)
        {
            LastCv = cv; LastSkills = skills;
            return Task.FromResult(GrowthAgent.BuildAnalysis(targetRole, new()
            {
                Summary = "Practice", Assumptions = "Advisory",
                Competencies = new[] { "C#", "Reviews", "Architecture" }
                    .Select(n => new CompetenceTarget(n, "Technical", 4, "Role needs it", "Practice it")).ToList()
            }, skills));
        }
    }

    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var subject = Request.Headers["X-Test-Subject"].ToString();
            if (subject.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new("sub", subject), new("email", "same@example.test")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
    private sealed class Repository : IProfileRepository
    {
        public Dictionary<string, StoredProfile> Profiles { get; } = [];
        public bool FailWriteOnce { get; set; }
        public bool Unavailable { get; set; }
        private int version;
        public Task<StoredProfile?> ReadAsync(string userId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Unavailable) throw new CosmosException("Missing test container", HttpStatusCode.NotFound, 0, "", 0);
            return Task.FromResult(Profiles.GetValueOrDefault(userId));
        }
        public async Task<StoredProfile> GetOrCreateAsync(string userId, CancellationToken ct) =>
            await ReadAsync(userId, ct) ?? await WriteAsync(ProfileDocument.Empty(userId), null, ct);
        public Task<StoredProfile> WriteAsync(ProfileDocument document, string? expectedVersion, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (FailWriteOnce) { FailWriteOnce = false; throw new ProfileConflictException(); }
            if (Profiles.GetValueOrDefault(document.UserId)?.Version != expectedVersion) throw new ProfileConflictException();
            var saved = new StoredProfile(document, $"\"{++version}\"");
            Profiles[document.UserId] = saved;
            return Task.FromResult(saved);
        }
        public Task DeleteAsync(string userId, string expectedVersion, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Profiles.GetValueOrDefault(userId)?.Version != expectedVersion) throw new ProfileConflictException();
            Profiles.Remove(userId);
            return Task.CompletedTask;
        }
    }
    private sealed class Blobs : ICvBlobStore
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public bool FailDeleteOnce { get; set; }
        public Task PutAsync(string name, byte[] content, CancellationToken ct) { Files.Add(name, content); return Task.CompletedTask; }
        public Task<Stream> OpenAsync(string name, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(Files[name]));
        public Task DeleteAsync(string name, CancellationToken ct)
        {
            if (FailDeleteOnce) { FailDeleteOnce = false; throw new Azure.RequestFailedException(503, "Simulated Blob failure"); }
            Files.Remove(name); return Task.CompletedTask;
        }
    }
    private sealed class Agent : ICvExtractionAgent
    {
        public bool FailOnce { get; set; }
        public Task<CvAnalysis> ExtractAsync(AzureSettings settings, string cv, CancellationToken ct)
        {
            if (FailOnce) { FailOnce = false; throw new HttpRequestException("Simulated extraction failure"); }
            return Task.FromResult(new CvAnalysis([new("Developer", "Example", "2020-01", null, true, "Development",
                "Developer at Example from 2020-01 to present.")],
                [new() { Name = "C#", Level = 3, Years = 3, Evidence = "Three years of C#", Reviewed = false }]));
        }
    }
}

using System.Net;
using System.Text.Json;
using Dev2Lead.Core;
using Xunit;

namespace Dev2Lead.Core.Tests;

public sealed class LearnTests
{
    private const string Source = "https://learn.microsoft.com/en-us/users/example/transcript/shared-id";
    private const string ProfileId = "11111111-2222-3333-4444-555555555555";

    [Theory]
    [InlineData(Source)]
    [InlineData("https://learn.microsoft.com/users/example/transcript/shared-id/")]
    public void SharingUrlsAreAccepted(string source) => Assert.Equal("learn.microsoft.com", LearnTranscriptService.ValidateSource(source).Host);

    [Theory]
    [InlineData("https://attacker.test/users/example/transcript/shared-id")]
    [InlineData("http://learn.microsoft.com/en-us/users/example/transcript/shared-id")]
    [InlineData("https://learn.microsoft.com/en-us/users/example/")]
    [InlineData("https://learn.microsoft.com/en-us/users/example/transcript/shared-id?redirect=evil")]
    public void InvalidSourcesAreRejected(string source) => Assert.Throws<InvalidOperationException>(() => LearnTranscriptService.ValidateSource(source));

    [Theory]
    [InlineData("https://attacker.test/badge.svg")]
    [InlineData("//attacker.test/badge.svg")]
    [InlineData("javascript:alert(1)")]
    public void ExternalLinksAreNotLaunched(string url) => Assert.Throws<InvalidDataException>(() => LearnTranscriptService.LearnUrl(url));

    [Fact]
    public async Task LoadsRealFieldsAndKeepsCertificationsDistinctFromBadges()
    {
        var service = new LearnTranscriptService(new HttpClient(new Handler(request =>
        {
            Assert.False(request.Headers.Contains("api-key"));
            if (request.RequestUri!.AbsolutePath.Contains("/transcript/share/"))
                return Response(Transcript());
            Assert.Equal($"/api/achievements/user/{ProfileId}", request.RequestUri.AbsolutePath);
            return Response(Awards());
        })));
        var result = await service.LoadAsync(Source);
        Assert.Single(result.ActiveCertifications);
        Assert.Single(result.HistoricalCertifications);
        Assert.Single(result.Modules);
        Assert.Single(result.Awards);
        Assert.Equal("example", result.UserName);
        Assert.Equal("Active", result.ActiveCertifications[0].Status);
        Assert.Null(result.HistoricalCertifications[0].ExpiresOn);
        Assert.Equal("https://learn.microsoft.com/training/modules/sample/", result.Modules[0].Url);
        Assert.Equal("https://learn.microsoft.com/training/achievements/sample.svg", result.Awards[0].ImageUrl);
        Assert.Equal(90, result.TrainingMinutes);
    }

    [Fact]
    public async Task IncompleteModuleCollectionIsAnError()
    {
        var service = new LearnTranscriptService(new HttpClient(new Handler(_ => Response(Transcript(2)))));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync(Source));
    }

    [Fact]
    public async Task AchievementsWithoutPublishedUrlsRemainVisibleWithoutInventedLinks()
    {
        var transcript = Transcript().Replace("\"url\":\"/training/modules/sample/\",", "");
        var awards = Awards().Replace("\"url\":\"/training/modules/sample/\",", "");
        var service = new LearnTranscriptService(new HttpClient(new Handler(request =>
            Response(request.RequestUri!.AbsolutePath.Contains("/transcript/share/") ? transcript : awards))));
        var result = await service.LoadAsync(Source);
        Assert.Null(result.Modules.Single().Url);
        Assert.Null(result.Awards.Single().Url);
    }

    [Fact]
    public async Task PartialBadgeCollectionIsAnError()
    {
        var service = new LearnTranscriptService(new HttpClient(new Handler(request =>
            Response(request.RequestUri!.AbsolutePath.Contains("/transcript/share/") ? Transcript() : Awards(2)))));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync(Source));
    }

    [Fact]
    public async Task PrivateOrRevokedTranscriptFailsExplicitly()
    {
        var service = new LearnTranscriptService(new HttpClient(new Handler(_ => new(HttpStatusCode.Forbidden))));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => service.LoadAsync(Source));
        Assert.Contains("HTTP 403", error.Message);
    }

    private static string Transcript(int moduleCount = 1) => JsonSerializer.Serialize(new
    {
        docsId = ProfileId, userName = "example", totalModulesCompleted = moduleCount,
        totalLearningPathsCompleted = 1, totalTrainingMinutes = 90,
        contactEmail = "not-retained@example.test",
        certificationData = new
        {
            legalName = "Not Retained", totalActiveCertifications = 1, totalHistoricalCertifications = 1,
            activeCertifications = new[] { new { name = "Microsoft Certified: Example", status = "Active", dateEarned = "2026-01-01T00:00:00Z", expiration = "2027-01-01T00:00:00Z" } },
            historicalCertifications = new[] { new { name = "Legacy credential", status = "Retired", dateEarned = "2020-01-01T00:00:00Z", expiration = (string?)null } }
        },
        modulesCompleted = new[] { new { title = "Sample module", url = "/training/modules/sample/", completedOn = "2026-05-01T00:00:00Z", durationInMinutes = 45 } }
    });

    private static string Awards(int total = 1) => JsonSerializer.Serialize(new
    {
        totalCount = total,
        achievements = new[] { new { title = "Sample module", category = "modules", url = "/training/modules/sample/", imageUrl = "/training/achievements/sample.svg", grantedOn = "2026-05-01T00:00:00Z" } }
    });
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}

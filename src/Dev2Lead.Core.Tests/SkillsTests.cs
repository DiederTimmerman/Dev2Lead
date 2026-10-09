using System.Net;
using System.Text.Json;
using Dev2Lead.Core;
using Xunit;

namespace Dev2Lead.Core.Tests;

public sealed class SkillsTests
{
    private const string Cv = "Developer with 3 years of C# experience and mentoring junior engineers.";
    private static readonly AzureSettings Settings = new("https://example.services.ai.azure.com/", "gpt-5-nano", "test-key");

    [Fact]
    public void UnknownIsNotZeroOrNovice()
    {
        var skill = new SkillAssessment { Name = "WCAG", Category = "Business & standards" };
        SkillProfile.Validate([skill]);
        Assert.Null(skill.Level);
        Assert.Null(skill.Years);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(6, 1)]
    [InlineData(3, -1)]
    [InlineData(3, 81)]
    public void InvalidLevelsAndExperienceAreRejected(int level, int years) =>
        Assert.Throws<InvalidDataException>(() => SkillProfile.Validate([new() { Name = "C#", Level = level, Years = years }]));

    [Fact]
    public void SavingRequiresReview()
    {
        var skill = new SkillAssessment { Name = "C#", Level = 3, Years = 3 };
        Assert.Throws<InvalidDataException>(() => SkillProfile.Validate([skill], true));
        skill.Reviewed = true;
        SkillProfile.Validate([skill], true);
        SkillProfile.Validate([], true);
    }

    [Fact]
    public void DuplicateSkillsAreRejected() => Assert.Throws<InvalidDataException>(() =>
        SkillProfile.Validate([new() { Name = "Python" }, new() { Name = " python " }]));

    [Fact]
    public void UnfoundedEstimatesAreRejected() => Assert.Throws<InvalidDataException>(() =>
        SkillsCoach.ValidateSuggestions([new() { Name = "WCAG", Level = 5, Years = 10, Evidence = "Not evidenced in CV" }], Cv));

    [Fact]
    public void ExactEvidenceSupportsTentativeValues() =>
        SkillsCoach.ValidateSuggestions([new() { Name = "C#", Level = 3, Years = 3, Evidence = "3 years of C# experience" }], Cv);

    [Fact]
    public async Task SkillsUseSharedAzureTransportAndAddUnknownStandards()
    {
        var handler = new Handler(async request =>
        {
            Assert.Equal("/openai/v1/chat/completions", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("gpt-5-nano", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
            var messages = body.RootElement.GetProperty("messages");
            Assert.Contains(Cv, messages[1].GetProperty("content").GetString());
            return Reply([new() { Name = "C#", Level = 3, Years = 3, Evidence = "3 years of C# experience", Reviewed = true }]);
        });
        var skills = await new SkillsCoach(new CareerCoach(new HttpClient(handler))).SuggestAsync(Settings, Cv);
        Assert.Equal(3, skills.Count);
        Assert.All(skills, s => Assert.False(s.Reviewed));
        Assert.All(skills.Where(s => s.Name is "WCAG" or "NORA"), s =>
        {
            Assert.Equal("Business & standards", s.Category);
            Assert.Null(s.Level);
            Assert.Null(s.Years);
        });
    }

    [Fact]
    public async Task EmptySuggestionsAreNotSuccessful()
    {
        var coach = new SkillsCoach(new CareerCoach(new HttpClient(new Handler(_ => Task.FromResult(Reply([]))))));
        await Assert.ThrowsAsync<InvalidDataException>(() => coach.SuggestAsync(Settings, Cv));
    }

    [Fact]
    public void LearningFiltersCombineSourceAndSearch()
    {
        Assert.All(LearningCatalog.Filter("Microsoft Learn", ""), r => Assert.Equal("Microsoft Learn", r.Provider));
        Assert.Single(LearningCatalog.Filter("Python.org", "PYTHON"));
        Assert.Equal(3, LearningCatalog.Filter("Udemy", "").Count());
        Assert.Single(LearningCatalog.Filter("Udemy", "wcag"));
        Assert.Empty(LearningCatalog.Filter("Udemy", "not-a-course"));
        Assert.All(LearningCatalog.Filter("Udemy", ""), r => Assert.Equal("Course search", r.Kind));
    }

    [Fact]
    public async Task ReviewedProfilePersistsAcrossStoreInstancesAndCanBeCleared()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dev2lead-skills-test-{Guid.NewGuid():N}.json");
        try
        {
            Assert.Empty(await new SkillProfileStore(path).LoadAsync());
            await new SkillProfileStore(path).SaveAsync([new() { Name = "WCAG", Category = "Business & standards", Level = 3, Years = 2.5m, Reviewed = true }]);
            var restored = Assert.Single(await new SkillProfileStore(path).LoadAsync());
            Assert.Equal(2.5m, restored.Years);
            Assert.Equal(3, restored.Level);
            Assert.True(restored.Reviewed);
            await Assert.ThrowsAsync<InvalidDataException>(() => new SkillProfileStore(path).SaveAsync([new() { Name = "Unreviewed skill" }]));
            Assert.Equal("WCAG", Assert.Single(await new SkillProfileStore(path).LoadAsync()).Name);
            Assert.False(File.Exists(path + ".tmp"));
            await new SkillProfileStore(path).SaveAsync([]);
            Assert.Empty(await new SkillProfileStore(path).LoadAsync());
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    private static HttpResponseMessage Reply(List<SkillAssessment> skills) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(new { skills }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) } } }
        }))
    };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}

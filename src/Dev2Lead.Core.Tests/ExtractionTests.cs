using System.Net;
using System.Text.Json;
using Dev2Lead.Core;
using Xunit;

namespace Dev2Lead.Core.Tests;

public sealed class ExtractionTests
{
    private const string Cv = "Developer at Example from 2020-01 to present. Three years of C# and mentoring experience.";
    private static readonly AzureSettings Settings = new("https://example.services.ai.azure.com/", "gpt-5-nano", "test-key");
    private static WorkExperience Role => new("Developer", "Example", "2020-01", null, true, "Development",
        "Developer at Example from 2020-01 to present.");
    private static List<SkillAssessment> Skills => [new() { Name = "C#", Level = 3, Years = 3, Evidence = "Three years of C#", Reviewed = true }];

    [Fact]
    public async Task OneAgentResponseExtractsBothAndInitializesUnreviewedValues()
    {
        var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var messages = body.RootElement.GetProperty("messages");
            Assert.Contains("experiences", messages[0].GetProperty("content").GetString());
            Assert.Contains(Cv, messages[1].GetProperty("content").GetString());
            return Reply(new CvAnalysis([Role], Skills));
        });
        var analysis = await new CvExtractionAgent(new CareerCoach(new HttpClient(handler))).ExtractAsync(Settings, Cv, default);
        Assert.Single(analysis.Experiences);
        var csharp = Assert.Single(analysis.Skills, s => s.Name == "C#");
        Assert.Equal(3, csharp.Level);
        Assert.Equal(3m, csharp.Years);
        Assert.All(analysis.Skills, skill => Assert.False(skill.Reviewed));
        Assert.All(analysis.Skills.Where(s => s.Name is "WCAG" or "NORA"), skill =>
        { Assert.Null(skill.Level); Assert.Null(skill.Years); });
    }

    [Fact]
    public void UnknownDatesAreAllowed() => CvExtractionAgent.Validate(new([Role with { StartDate = null }], Skills), Cv);

    [Theory]
    [InlineData("2019-13")]
    [InlineData("tomorrow")]
    [InlineData("2020-01-01")]
    [InlineData("2020\n")]
    public void InvalidDatesAreRejected(string date) =>
        Assert.Throws<InvalidDataException>(() => CvExtractionAgent.Validate(new([Role with { StartDate = date }], Skills), Cv));

    [Fact]
    public void CurrentRoleCannotHaveAnEndDate() =>
        Assert.Throws<InvalidDataException>(() => CvExtractionAgent.Validate(new([Role with { EndDate = "2023-01" }], Skills), Cv));

    [Fact]
    public void ReversedDatesAreRejected() =>
        Assert.Throws<InvalidDataException>(() => CvExtractionAgent.Validate(new([Role with { IsCurrent = false, EndDate = "2019-01" }], Skills), Cv));

    [Fact]
    public void MixedDatePrecisionDoesNotInventAConflict() =>
        CvExtractionAgent.Validate(new([Role with { StartDate = "2020-06", IsCurrent = false, EndDate = "2020" }], Skills), Cv);

    [Fact]
    public void InventedExperienceEvidenceIsRejected() =>
        Assert.Throws<InvalidDataException>(() => CvExtractionAgent.Validate(new([Role with { Evidence = "CEO for ten years" }], Skills), Cv));

    [Fact]
    public void InventedSkillEvidenceIsRejected()
    {
        var skills = Skills;
        skills[0].Evidence = "C# for twenty years";
        Assert.Throws<InvalidDataException>(() => CvExtractionAgent.Validate(new([Role], skills), Cv));
    }

    [Fact]
    public void EmptyExperienceIsValid() => CvExtractionAgent.Validate(new([], Skills), Cv);

    private static HttpResponseMessage Reply(CvAnalysis analysis) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(analysis, new JsonSerializerOptions(JsonSerializerDefaults.Web)) } } }
        }))
    };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
